// Copyright (c) 2024-2026 Pierre G. Boutquin. All rights reserved.
//
//  Licensed under the Apache License, Version 2.0 (the "License").
//  You may not use this file except in compliance with the License.
//  You may obtain a copy of the License at
//
//      http://www.apache.org/licenses/LICENSE-2.0
//
//  Unless required by applicable law or agreed to in writing, software
//  distributed under the License is distributed on an "AS IS" BASIS,
//  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//
//  See the License for the specific language governing permissions and
//  limitations under the License.
//
namespace Boutquin.Storage.Infrastructure.DataStructures;

/// <summary>
/// Represents a Bloom filter, a probabilistic data structure used to test whether an element is a member of a set.
/// This implementation uses Murmur3 and xxHash32 as hash functions to ensure efficient and fast hashing.
/// 
/// <para>This Bloom filter implementation is highly space-efficient and allows for fast insertions and membership checks.
/// It can report false positives but will never report false negatives.</para>
/// 
/// <para>Implementation Choices:</para>
/// <para>This implementation makes specific choices to balance performance and memory usage:
/// <list type="bullet">
/// <item>
/// <description><b>Bit Array Size (m):</b> The number of bits in the Bloom filter is chosen based on the expected number of elements and the desired false positive probability.</description>
/// </item>
/// <item>
/// <description><b>Number of Hash Functions (k):</b> The number of different hash functions is derived from the bit array size and the expected number of elements.</description>
/// </item>
/// <item>
/// <description><b>Hash Functions:</b> Uses Murmur3 and xxHash32 to provide a good balance of speed and low collision rates.</description>
/// </item>
/// </list>
/// </para>
/// 
/// <para>
/// <b>Why double hashing instead of k independent hash functions?</b> Kirsch and Mitzenmacher (2006)
/// proved that double hashing (h1 + i*h2) with only 2 hash functions achieves the same false positive
/// rate as k fully independent hash functions. This halves the computational cost — we compute 2 hashes
/// instead of k, then derive additional positions arithmetically.
/// </para>
///
/// <para>
/// <b>Why Murmur3 + xxHash32 specifically?</b> The two hash functions must be independent (uncorrelated
/// outputs for the same input). Murmur3 and xxHash32 use fundamentally different mixing strategies
/// (Murmur3: shift-multiply-xor rounds; xxHash: accumulator-based with different primes), so their
/// outputs are effectively independent. Using two instances of the same algorithm with different seeds
/// would risk correlated outputs.
/// </para>
///
/// <para>
/// <b>Byte identity is the filter's identity.</b> The filter hashes the bytes the serializer produces,
/// not the item, so it answers "was an item with these bytes added". The no-false-negative guarantee
/// therefore holds only when the serializer agrees with the equality relation the caller queries by:
/// equal items must produce equal bytes (and, to keep the false-positive rate, distinct items distinct
/// bytes). A storage engine finds keys through <see cref="IComparable{T}.CompareTo"/>, so a key type
/// whose <c>CompareTo</c> identifies values that <c>Serialize</c> writes differently — <c>-0.0</c> and
/// <c>+0.0</c>, NaNs with different payloads, a case-insensitive name — would make a decorated store
/// report a stored key absent. The filter cannot canonicalise an arbitrary type, so that obligation
/// rests on the key type: canonicalise inside <c>Serialize</c> (as <c>SerializableWrapper&lt;T&gt;</c> and
/// the <c>[Key]</c> ordered-wire encoding do) or supply an explicit serializer that does.
/// </para>
///
/// <para>
/// <b>Thread safety:</b> This class is not thread-safe. Concurrent calls to <see cref="Add"/> and
/// <see cref="Contains"/> may produce incorrect results due to unsynchronized access to the underlying
/// <see cref="BitArray"/>. External synchronization is required for concurrent use.
/// </para>
///
/// <para>Typical Uses:</para>
/// <para>In the context of a storage engine, a Bloom filter is often used to reduce the number of disk lookups.
/// Before performing a disk read operation to fetch a value, the Bloom filter can be checked to see if the key likely exists in the dataset.
/// If the Bloom filter indicates that the key does not exist, the costly disk read can be skipped, thereby improving performance.</para>
/// 
/// <para>Example:</para>
/// <code>
/// var expectedElements = 1000;
/// var falsePositiveProbability = 0.01; // 1% false positive rate
///
/// // Required: explicit byte serializer. For ISerializable&lt;T&gt; keys, use the factory:
/// var keyFilter = BloomFilter.ForSerializable&lt;Key&gt;(expectedElements, falsePositiveProbability);
///
/// // For ad-hoc keys, pass an explicit serializer:
/// var stringFilter = new BloomFilter&lt;string&gt;(expectedElements, falsePositiveProbability, Encoding.UTF8.GetBytes);
///
/// stringFilter.Add("exampleKey");
/// bool exists = stringFilter.Contains("exampleKey"); // True
/// stringFilter.Clear();
/// </code>
/// </summary>
/// <typeparam name="T">The type of elements to be stored in the Bloom filter.</typeparam>
public sealed class BloomFilter<T> : IBloomFilter<T>
{
    private readonly BitArray _bitArray;
    private readonly int _hashFunctionCount;
    private readonly int _bitArraySize;

    // Test-only observability: the derived double-hashing parameter, exposed so tests can pin
    // that BloomFilter and CountingBloomFilter derive IDENTICAL parameters from identical
    // inputs (the counting filter documents "the same double-hashing approach").
    internal int HashFunctionCount => _hashFunctionCount;
    private readonly Func<T, byte[]>? _itemToBytes;
    private readonly BloomFilterStreamSerializer<T>? _streamSerializer;
    private readonly IHashAlgorithm _hashAlgorithm1;
    private readonly IHashAlgorithm _hashAlgorithm2;

    // Per-instance scratch buffer reused across Add/Contains calls when the streaming-serializer
    // path is in use. The class is documented as not thread-safe, so a single instance buffer
    // suffices — every call sets length to 0, writes the item bytes, and reads back via
    // GetBuffer().AsSpan(0, length) without allocating a new byte[] per call. The legacy
    // Func<T, byte[]> path bypasses this field.
    private MemoryStream? _scratch;
    private int _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="BloomFilter{T}" /> class with the default
    /// Murmur3 + xxHash32 algorithms and a required item-to-bytes serializer.
    /// </summary>
    /// <param name="expectedElements">The expected number of elements to store in the Bloom filter.</param>
    /// <param name="falsePositiveProbability">The desired false positive probability.</param>
    /// <param name="itemToBytes">
    /// Required serializer that maps an item to a stable byte representation. The serializer is
    /// load-bearing — equal items must produce equal bytes (otherwise a stored item is reported
    /// absent) and distinct items must produce distinct bytes (otherwise the filter degenerates
    /// to always-true). For <see cref="ISerializable{T}"/> keys, prefer
    /// <see cref="BloomFilter.ForSerializable{T}(int, double)"/>, which uses the
    /// allocation-free streaming-serializer path.
    /// </param>
    public BloomFilter(int expectedElements, double falsePositiveProbability, Func<T, byte[]> itemToBytes)
        : this(expectedElements, falsePositiveProbability, new Murmur3(), new XxHash32(), itemToBytes)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="BloomFilter{T}" /> class with the default
    /// Murmur3 + xxHash32 algorithms and a streaming serializer that writes item bytes into a
    /// reusable, per-instance scratch buffer instead of allocating a fresh <c>byte[]</c> per
    /// Add/Contains call.
    /// </summary>
    /// <param name="expectedElements">The expected number of elements to store in the Bloom filter.</param>
    /// <param name="falsePositiveProbability">The desired false positive probability.</param>
    /// <param name="streamSerializer">
    /// Required serializer that writes the item's stable byte representation into the supplied
    /// <see cref="Stream"/>. The bloom filter rewinds the stream on every call (so the
    /// serializer always sees an empty target) and reads back via <c>GetBuffer()</c> without
    /// allocating. Equal items must produce equal byte sequences (otherwise a stored item is reported
    /// absent) and distinct items distinct byte sequences.
    /// </param>
    public BloomFilter(int expectedElements, double falsePositiveProbability, BloomFilterStreamSerializer<T> streamSerializer)
        : this(expectedElements, falsePositiveProbability, new Murmur3(), new XxHash32(), streamSerializer)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="BloomFilter{T}" /> class with custom hash functions.
    /// </summary>
    /// <param name="expectedElements">The expected number of elements to store in the Bloom filter.</param>
    /// <param name="falsePositiveProbability">The desired false positive probability.</param>
    /// <param name="hashAlgorithm1">The first hash algorithm.</param>
    /// <param name="hashAlgorithm2">The second hash algorithm.</param>
    /// <param name="itemToBytes">A function to convert items to byte arrays.</param>
    public BloomFilter(int expectedElements, double falsePositiveProbability, IHashAlgorithm hashAlgorithm1, IHashAlgorithm hashAlgorithm2, Func<T, byte[]> itemToBytes)
        : this(expectedElements, falsePositiveProbability, hashAlgorithm1, hashAlgorithm2)
    {
        _itemToBytes = itemToBytes ?? throw new ArgumentNullException(nameof(itemToBytes));
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="BloomFilter{T}" /> class with custom hash
    /// functions and the streaming-serializer path.
    /// </summary>
    public BloomFilter(int expectedElements, double falsePositiveProbability, IHashAlgorithm hashAlgorithm1, IHashAlgorithm hashAlgorithm2, BloomFilterStreamSerializer<T> streamSerializer)
        : this(expectedElements, falsePositiveProbability, hashAlgorithm1, hashAlgorithm2)
    {
        _streamSerializer = streamSerializer ?? throw new ArgumentNullException(nameof(streamSerializer));
    }

    private BloomFilter(int expectedElements, double falsePositiveProbability, IHashAlgorithm hashAlgorithm1, IHashAlgorithm hashAlgorithm2)
    {
        if (expectedElements <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedElements), "Expected elements must be positive.");
        }
        if (!(falsePositiveProbability > 0 && falsePositiveProbability < 1))
        {
            throw new ArgumentOutOfRangeException(nameof(falsePositiveProbability), "Must be in (0, 1).");
        }
        _bitArraySize = BloomFilterSizing.OptimalArraySize(expectedElements, falsePositiveProbability);
        _hashFunctionCount = BloomFilterSizing.OptimalHashFunctionCount(expectedElements, _bitArraySize);
        _bitArray = new BitArray(_bitArraySize);
        _hashAlgorithm1 = hashAlgorithm1 ?? throw new ArgumentNullException(nameof(hashAlgorithm1));
        _hashAlgorithm2 = hashAlgorithm2 ?? throw new ArgumentNullException(nameof(hashAlgorithm2));
    }

    /// <summary>
    /// Adds an item to the Bloom filter. Each item is hashed multiple times; each hash points to a bit in the bit array which is then set to true.
    /// </summary>
    /// <param name="item">The item to add to the Bloom filter.</param>
    public void Add(T item)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentNullException.ThrowIfNull(item);

        if (_streamSerializer is not null)
        {
            var ms = _scratch ??= new MemoryStream(64);
            ms.SetLength(0);
            _streamSerializer(item, ms);
            AddCore(ms.GetBuffer().AsSpan(0, (int)ms.Length));
        }
        else
        {
            AddCore(_itemToBytes!(item));
        }
    }

    /// <summary>
    /// Checks if an item is possibly in the Bloom filter. If any of the bits corresponding to the item's hashes are false, the item is definitely not in the filter.
    /// </summary>
    /// <param name="item">The item to check for presence in the Bloom filter.</param>
    /// <returns>True if the item might be in the set, false if the item is definitely not in the set.</returns>
    public bool Contains(T item)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentNullException.ThrowIfNull(item);

        if (_streamSerializer is not null)
        {
            var ms = _scratch ??= new MemoryStream(64);
            ms.SetLength(0);
            _streamSerializer(item, ms);
            return ContainsCore(ms.GetBuffer().AsSpan(0, (int)ms.Length));
        }

        return ContainsCore(_itemToBytes!(item));
    }

    /// <summary>
    /// Clears the Bloom filter, resetting all bits in the bit array to false.
    /// </summary>
    public void Clear()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        _bitArray.SetAll(false);
    }

    /// <summary>
    /// Releases the per-instance scratch <see cref="MemoryStream"/> used by the
    /// streaming-serializer constructor path. Idempotent — concurrent disposers cannot
    /// double-free (<see cref="Interlocked.Exchange(ref int, int)"/> gates the cleanup).
    /// After dispose, every public method throws <see cref="ObjectDisposedException"/>.
    /// </summary>
    /// <remarks>
    /// The load-bearing benefit is releasing the scratch buffer — once a single oversize key
    /// has grown the buffer to its peak, it stays at that size for the instance's lifetime;
    /// long-lived processes that produce occasional large keys must dispose to release the
    /// memory. (The <see cref="BitArray"/> itself is a readonly field and is reclaimed with
    /// the instance, not by Dispose.)
    /// </remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _scratch?.Dispose();
        _scratch = null;
    }

    private void AddCore(ReadOnlySpan<byte> itemBytes)
    {
        // Inlined hash loop — replaces a yield-return enumerator (one allocation per call) with
        // a tight loop over hashFunctionCount. ComputeHash already accepts ReadOnlySpan<byte>
        // (IHashAlgorithm), so the entire path runs without per-call heap allocations.
        var hash1 = _hashAlgorithm1.ComputeHash(itemBytes);
        var hash2 = _hashAlgorithm2.ComputeHash(itemBytes);
        for (var i = 0; i < _hashFunctionCount; i++)
        {
            // Kirsch–Mitzenmacher double hashing: h1 + i·h2 in all-unsigned arithmetic
            // (ComputeHash returns uint), wrapping at 2^32 by construction — `unchecked`
            // documents that the wrap is intentional. The (uint) modulo keeps the position
            // computation unsigned end-to-end.
            var pos = (int)(unchecked((uint)(hash1 + (uint)i * hash2)) % (uint)_bitArraySize);
            _bitArray[pos] = true;
        }
    }

    private bool ContainsCore(ReadOnlySpan<byte> itemBytes)
    {
        var hash1 = _hashAlgorithm1.ComputeHash(itemBytes);
        var hash2 = _hashAlgorithm2.ComputeHash(itemBytes);
        for (var i = 0; i < _hashFunctionCount; i++)
        {
            // Kirsch–Mitzenmacher double hashing: h1 + i·h2 in all-unsigned arithmetic
            // (ComputeHash returns uint), wrapping at 2^32 by construction — `unchecked`
            // documents that the wrap is intentional. The (uint) modulo keeps the position
            // computation unsigned end-to-end.
            var pos = (int)(unchecked((uint)(hash1 + (uint)i * hash2)) % (uint)_bitArraySize);
            if (!_bitArray[pos])
            {
                return false;
            }
        }
        return true;
    }

}

/// <summary>
/// Writes the stable byte representation of <paramref name="item"/> into <paramref name="destination"/>.
/// Used by <see cref="BloomFilter{T}"/>'s allocation-free constructor to avoid the per-call
/// <c>byte[]</c> allocation the legacy <see cref="Func{T, TResult}"/>-based path required.
/// </summary>
/// <typeparam name="T">The item type the bloom filter holds.</typeparam>
/// <param name="item">The item to serialise (never <see langword="null"/> at the call site).</param>
/// <param name="destination">
/// A reusable scratch <see cref="Stream"/> owned by the bloom filter. The implementation may
/// assume the stream's <see cref="Stream.Position"/> and <see cref="Stream.Length"/> are 0
/// on entry and may write any number of bytes; the bloom filter reads the written span back
/// via <c>GetBuffer()</c> without allocating.
/// </param>
public delegate void BloomFilterStreamSerializer<in T>(T item, Stream destination);

/// <summary>
/// Static convenience entry points for <see cref="BloomFilter{T}"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this static class exists.</b> The generic <see cref="BloomFilter{T}"/> cannot conditionally
/// constrain its type parameter on <see cref="ISerializable{T}"/> at construction time — a class-level
/// constraint would over-constrain non-engine consumers (test seams, ad-hoc <see cref="string"/> keys).
/// This static helper offers an opt-in factory whose method-level <c>where T : ISerializable&lt;T&gt;</c>
/// constraint binds the engine-friendly path automatically. Non-ISerializable consumers continue to
/// use the explicit-serializer constructor on <see cref="BloomFilter{T}"/>.
/// </para>
/// </remarks>
public static class BloomFilter
{
    /// <summary>
    /// Creates a <see cref="BloomFilter{T}"/> for an <see cref="ISerializable{T}"/> key type, binding the
    /// item-to-bytes serializer automatically via <see cref="ISerializable{T}.Serialize(Stream)"/>.
    /// </summary>
    /// <typeparam name="T">A type implementing <see cref="ISerializable{T}"/>.</typeparam>
    /// <param name="expectedElements">The expected number of elements.</param>
    /// <param name="falsePositiveProbability">The desired false positive probability.</param>
    /// <returns>A configured <see cref="BloomFilter{T}"/>.</returns>
    /// <remarks>
    /// The filter hashes <c>Serialize</c>'s bytes, so <typeparamref name="T"/> must serialize equal
    /// keys identically: equal items must produce equal bytes, where "equal" is the relation the
    /// fronted store matches keys by (<see cref="IComparable{T}.CompareTo"/>). Keys that satisfy this
    /// out of the box are <c>SerializableWrapper&lt;T&gt;</c> and <c>[Key]</c>-generated record structs.
    /// </remarks>
    public static BloomFilter<T> ForSerializable<T>(int expectedElements, double falsePositiveProbability)
        where T : ISerializable<T>
        => new(expectedElements, falsePositiveProbability, SerializeToStream<T>);

    // Streaming serializer — writes directly into the bloom filter's reusable scratch
    // MemoryStream. After the first call (which may grow the underlying buffer), every
    // subsequent Add/Contains is allocation-free along this path. Compare to the previous
    // implementation, which allocated a fresh MemoryStream + byte[] for every call.
    private static void SerializeToStream<T>(T item, Stream destination)
        where T : ISerializable<T>
        => item.Serialize(destination);
}
