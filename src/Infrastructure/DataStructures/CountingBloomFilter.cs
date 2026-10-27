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
/// A counting Bloom filter that supports element removal by using integer counters instead of single bits.
/// Uses the same double-hashing approach as <see cref="BloomFilter{T}"/> (Murmur3 + XxHash32).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why int[] counters instead of BitArray?</b> A standard Bloom filter uses single bits, making removal
/// impossible — clearing a bit that is shared by multiple elements would cause false negatives. By using
/// integer counters, we can safely decrement on removal. The trade-off is ~32x memory overhead per position.
/// </para>
///
/// <para>
/// <b>Byte identity is the filter's identity.</b> As in <see cref="BloomFilter{T}"/>, positions derive
/// from the serializer's bytes, so equal items must produce equal bytes: an item whose queried bytes differ
/// from the bytes it was added under is reported absent (and a <c>Remove</c> decrements different counters
/// than the <c>Add</c> incremented). Canonicalise inside the serializer for any key type whose equality
/// identifies values that serialize differently.
/// </para>
///
/// <para>
/// <b>Thread safety:</b> This class is not thread-safe. External synchronization is required for concurrent use.
/// </para>
/// </remarks>
/// <typeparam name="T">The type of elements to be stored in the counting Bloom filter.</typeparam>
public sealed class CountingBloomFilter<T> : ICountingBloomFilter<T>
{
    private readonly int[] _counters;
    private readonly int _hashFunctionCount;
    private readonly int _counterArraySize;

    // Test-only observability — see BloomFilter<T>.HashFunctionCount.
    internal int HashFunctionCount => _hashFunctionCount;
    private readonly Func<T, byte[]>? _itemToBytes;
    private readonly BloomFilterStreamSerializer<T>? _streamSerializer;
    private readonly IHashAlgorithm _hashAlgorithm1;
    private readonly IHashAlgorithm _hashAlgorithm2;

    // Per-instance scratch reused across Add/Contains/Remove/GetCount calls when the
    // streaming-serializer path is in use. The class is documented as not thread-safe, so a
    // single instance buffer is sufficient.
    private MemoryStream? _scratch;
    private int _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="CountingBloomFilter{T}"/> class with the default
    /// Murmur3 + xxHash32 algorithms and a required item-to-bytes serializer.
    /// </summary>
    /// <param name="expectedElements">The expected number of elements to store.</param>
    /// <param name="falsePositiveProbability">The desired false positive probability.</param>
    /// <param name="itemToBytes">
    /// Required serializer that maps an item to a stable byte representation. Equal items must
    /// produce equal bytes, otherwise a stored item is reported absent; distinct items must produce
    /// distinct bytes, otherwise the filter degenerates. For <see cref="ISerializable{T}"/>
    /// keys, prefer <see cref="CountingBloomFilter.ForSerializable{T}(int, double)"/>, which uses
    /// the allocation-free streaming-serializer path.
    /// </param>
    public CountingBloomFilter(int expectedElements, double falsePositiveProbability, Func<T, byte[]> itemToBytes)
        : this(expectedElements, falsePositiveProbability, new Murmur3(), new XxHash32(), itemToBytes)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CountingBloomFilter{T}"/> class with the default
    /// Murmur3 + xxHash32 algorithms and a streaming serializer that writes item bytes into a
    /// reusable, per-instance scratch buffer instead of allocating a fresh <c>byte[]</c> per call.
    /// </summary>
    public CountingBloomFilter(int expectedElements, double falsePositiveProbability, BloomFilterStreamSerializer<T> streamSerializer)
        : this(expectedElements, falsePositiveProbability, new Murmur3(), new XxHash32(), streamSerializer)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CountingBloomFilter{T}"/> class with custom hash functions.
    /// </summary>
    public CountingBloomFilter(int expectedElements, double falsePositiveProbability, IHashAlgorithm hashAlgorithm1, IHashAlgorithm hashAlgorithm2, Func<T, byte[]> itemToBytes)
        : this(expectedElements, falsePositiveProbability, hashAlgorithm1, hashAlgorithm2)
    {
        _itemToBytes = itemToBytes ?? throw new ArgumentNullException(nameof(itemToBytes));
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CountingBloomFilter{T}"/> class with custom hash
    /// functions and the streaming-serializer path.
    /// </summary>
    public CountingBloomFilter(int expectedElements, double falsePositiveProbability, IHashAlgorithm hashAlgorithm1, IHashAlgorithm hashAlgorithm2, BloomFilterStreamSerializer<T> streamSerializer)
        : this(expectedElements, falsePositiveProbability, hashAlgorithm1, hashAlgorithm2)
    {
        _streamSerializer = streamSerializer ?? throw new ArgumentNullException(nameof(streamSerializer));
    }

    private CountingBloomFilter(int expectedElements, double falsePositiveProbability, IHashAlgorithm hashAlgorithm1, IHashAlgorithm hashAlgorithm2)
    {
        if (expectedElements <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedElements), "Expected elements must be positive.");
        }
        if (!(falsePositiveProbability > 0 && falsePositiveProbability < 1))
        {
            throw new ArgumentOutOfRangeException(nameof(falsePositiveProbability), "Must be in (0, 1).");
        }
        _counterArraySize = BloomFilterSizing.OptimalArraySize(expectedElements, falsePositiveProbability);
        _hashFunctionCount = BloomFilterSizing.OptimalHashFunctionCount(expectedElements, _counterArraySize);
        _counters = new int[_counterArraySize];
        _hashAlgorithm1 = hashAlgorithm1 ?? throw new ArgumentNullException(nameof(hashAlgorithm1));
        _hashAlgorithm2 = hashAlgorithm2 ?? throw new ArgumentNullException(nameof(hashAlgorithm2));
    }

    /// <inheritdoc/>
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

    /// <inheritdoc/>
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

    /// <inheritdoc/>
    public void Remove(T item)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentNullException.ThrowIfNull(item);

        if (_streamSerializer is not null)
        {
            var ms = _scratch ??= new MemoryStream(64);
            ms.SetLength(0);
            _streamSerializer(item, ms);
            RemoveCore(ms.GetBuffer().AsSpan(0, (int)ms.Length));
        }
        else
        {
            RemoveCore(_itemToBytes!(item));
        }
    }

    /// <inheritdoc/>
    public int GetCount(T item)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentNullException.ThrowIfNull(item);

        if (_streamSerializer is not null)
        {
            var ms = _scratch ??= new MemoryStream(64);
            ms.SetLength(0);
            _streamSerializer(item, ms);
            return GetCountCore(ms.GetBuffer().AsSpan(0, (int)ms.Length));
        }

        return GetCountCore(_itemToBytes!(item));
    }

    /// <inheritdoc/>
    public void Clear()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        Array.Clear(_counters, 0, _counters.Length);
    }

    /// <summary>
    /// Releases the per-instance scratch <see cref="MemoryStream"/> used by the
    /// streaming-serializer constructor path. Idempotent — concurrent disposers cannot
    /// double-free (<see cref="Interlocked.Exchange(ref int, int)"/> gates the cleanup).
    /// After dispose, every public method throws <see cref="ObjectDisposedException"/>.
    /// </summary>
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
        var hash1 = _hashAlgorithm1.ComputeHash(itemBytes);
        var hash2 = _hashAlgorithm2.ComputeHash(itemBytes);
        for (var i = 0; i < _hashFunctionCount; i++)
        {
            var pos = (int)(unchecked((uint)(hash1 + (uint)i * hash2)) % (uint)_counterArraySize);
            // Saturate at int.MaxValue — wrapping past int.MaxValue to a negative value would
            // corrupt Contains (negative counters read as "absent") and GetCount (min scan
            // returns negative).
            if (_counters[pos] < int.MaxValue)
            {
                _counters[pos]++;
            }
        }
    }

    private bool ContainsCore(ReadOnlySpan<byte> itemBytes)
    {
        var hash1 = _hashAlgorithm1.ComputeHash(itemBytes);
        var hash2 = _hashAlgorithm2.ComputeHash(itemBytes);
        for (var i = 0; i < _hashFunctionCount; i++)
        {
            var pos = (int)(unchecked((uint)(hash1 + (uint)i * hash2)) % (uint)_counterArraySize);
            if (_counters[pos] <= 0)
            {
                return false;
            }
        }
        return true;
    }

    private void RemoveCore(ReadOnlySpan<byte> itemBytes)
    {
        var hash1 = _hashAlgorithm1.ComputeHash(itemBytes);
        var hash2 = _hashAlgorithm2.ComputeHash(itemBytes);

        // Decrement ONLY when all k counters are positive — i.e., the item currently passes
        // the membership test. Removing a key that was never added (any counter at 0) must
        // be a complete no-op: an unguarded per-counter decrement drains whatever positive
        // counters the ghost key collides with, eventually producing a false negative for a
        // genuine member — the one failure the filter contract forbids. (A ghost that is a
        // bloom false positive can still decrement shared counters; that residual hazard is
        // inherent to counting filters, which is why Remove's contract requires removing
        // only previously-added items, at most as many times as they were added.)
        for (var i = 0; i < _hashFunctionCount; i++)
        {
            var pos = (int)(unchecked((uint)(hash1 + (uint)i * hash2)) % (uint)_counterArraySize);
            if (_counters[pos] <= 0)
            {
                return;
            }
        }

        for (var i = 0; i < _hashFunctionCount; i++)
        {
            var pos = (int)(unchecked((uint)(hash1 + (uint)i * hash2)) % (uint)_counterArraySize);
            _counters[pos]--;
        }
    }

    private int GetCountCore(ReadOnlySpan<byte> itemBytes)
    {
        var hash1 = _hashAlgorithm1.ComputeHash(itemBytes);
        var hash2 = _hashAlgorithm2.ComputeHash(itemBytes);
        var minCount = int.MaxValue;
        for (var i = 0; i < _hashFunctionCount; i++)
        {
            var pos = (int)(unchecked((uint)(hash1 + (uint)i * hash2)) % (uint)_counterArraySize);
            if (_counters[pos] < minCount)
            {
                minCount = _counters[pos];
            }
        }
        return minCount == int.MaxValue ? 0 : minCount;
    }
}

/// <summary>
/// Static convenience entry points for <see cref="CountingBloomFilter{T}"/>.
/// </summary>
/// <remarks>
/// See <see cref="BloomFilter"/> for the rationale: method-level <c>where T : ISerializable&lt;T&gt;</c>
/// constraints provide an opt-in factory for engine-friendly key types without over-constraining the
/// generic type itself.
/// </remarks>
public static class CountingBloomFilter
{
    /// <summary>
    /// Creates a <see cref="CountingBloomFilter{T}"/> for an <see cref="ISerializable{T}"/> key type,
    /// binding the item-to-bytes serializer automatically via <see cref="ISerializable{T}.Serialize(Stream)"/>.
    /// </summary>
    /// <typeparam name="T">A type implementing <see cref="ISerializable{T}"/>.</typeparam>
    /// <param name="expectedElements">The expected number of elements.</param>
    /// <param name="falsePositiveProbability">The desired false positive probability.</param>
    /// <returns>A configured <see cref="CountingBloomFilter{T}"/>.</returns>
    /// <remarks>
    /// The filter positions derive from <c>Serialize</c>'s bytes, so <typeparamref name="T"/> must
    /// serialize equal keys identically: equal items must produce equal bytes, where "equal" is the
    /// relation the fronted store matches keys by (<see cref="IComparable{T}.CompareTo"/>).
    /// </remarks>
    public static CountingBloomFilter<T> ForSerializable<T>(int expectedElements, double falsePositiveProbability)
        where T : ISerializable<T>
        => new(expectedElements, falsePositiveProbability, SerializeToStream<T>);

    private static void SerializeToStream<T>(T item, Stream destination)
        where T : ISerializable<T>
        => item.Serialize(destination);
}
