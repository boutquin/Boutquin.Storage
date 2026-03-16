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
/// <b>Thread safety:</b> This class is not thread-safe. External synchronization is required for concurrent use.
/// </para>
/// </remarks>
/// <typeparam name="T">The type of elements to be stored in the counting Bloom filter.</typeparam>
public sealed class CountingBloomFilter<T> : ICountingBloomFilter<T>
{
    private readonly int[] _counters;
    private readonly int _hashFunctionCount;
    private readonly int _counterArraySize;
    private readonly Func<T, byte[]> _itemToBytes;
    private readonly IHashAlgorithm _hashAlgorithm1;
    private readonly IHashAlgorithm _hashAlgorithm2;

    /// <summary>
    /// Initializes a new instance of the <see cref="CountingBloomFilter{T}"/> class with expected elements and false positive probability.
    /// Uses Murmur3 and XxHash32 as default hash algorithms.
    /// </summary>
    /// <param name="expectedElements">The expected number of elements to store.</param>
    /// <param name="falsePositiveProbability">The desired false positive probability.</param>
    public CountingBloomFilter(int expectedElements, double falsePositiveProbability)
        : this(expectedElements, falsePositiveProbability, new Murmur3(), new XxHash32(), DefaultItemToBytes)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CountingBloomFilter{T}"/> class with custom hash functions.
    /// </summary>
    public CountingBloomFilter(int expectedElements, double falsePositiveProbability, IHashAlgorithm hashAlgorithm1, IHashAlgorithm hashAlgorithm2, Func<T, byte[]> itemToBytes)
    {
        _counterArraySize = CalculateOptimalBitArraySize(expectedElements, falsePositiveProbability);
        _hashFunctionCount = CalculateOptimalHashFunctionCount(expectedElements, _counterArraySize);
        _counters = new int[_counterArraySize];
        _itemToBytes = itemToBytes ?? throw new ArgumentNullException(nameof(itemToBytes));
        _hashAlgorithm1 = hashAlgorithm1 ?? throw new ArgumentNullException(nameof(hashAlgorithm1));
        _hashAlgorithm2 = hashAlgorithm2 ?? throw new ArgumentNullException(nameof(hashAlgorithm2));
    }

    /// <inheritdoc/>
    public void Add(T item)
    {
        if (item == null)
        {
            throw new ArgumentNullException(nameof(item), "Cannot add null item to the counting Bloom filter.");
        }

        var itemBytes = _itemToBytes(item);
        foreach (var position in GetHashPositions(itemBytes))
        {
            _counters[position]++;
        }
    }

    /// <inheritdoc/>
    public bool Contains(T item)
    {
        if (item == null)
        {
            throw new ArgumentNullException(nameof(item), "Cannot check null item in the counting Bloom filter.");
        }

        var itemBytes = _itemToBytes(item);
        foreach (var position in GetHashPositions(itemBytes))
        {
            if (_counters[position] <= 0)
            {
                return false;
            }
        }
        return true;
    }

    /// <inheritdoc/>
    public void Remove(T item)
    {
        if (item == null)
        {
            throw new ArgumentNullException(nameof(item), "Cannot remove null item from the counting Bloom filter.");
        }

        var itemBytes = _itemToBytes(item);
        foreach (var position in GetHashPositions(itemBytes))
        {
            // Why floor at 0? Decrementing below 0 would corrupt the filter — a counter at -1 would
            // cause Contains to return false for any element that hashes to that position, even if
            // the element was legitimately added.
            if (_counters[position] > 0)
            {
                _counters[position]--;
            }
        }
    }

    /// <inheritdoc/>
    public int GetCount(T item)
    {
        if (item == null)
        {
            throw new ArgumentNullException(nameof(item), "Cannot get count for null item.");
        }

        var itemBytes = _itemToBytes(item);
        var minCount = int.MaxValue;
        foreach (var position in GetHashPositions(itemBytes))
        {
            if (_counters[position] < minCount)
            {
                minCount = _counters[position];
            }
        }
        return minCount == int.MaxValue ? 0 : minCount;
    }

    /// <inheritdoc/>
    public void Clear()
    {
        Array.Clear(_counters, 0, _counters.Length);
    }

    private static int CalculateOptimalBitArraySize(int n, double p)
    {
        return (int)Math.Ceiling((-n * Math.Log(p)) / (Math.Log(2) * Math.Log(2)));
    }

    private static int CalculateOptimalHashFunctionCount(int n, int m)
    {
        return (int)Math.Max(1, Math.Round((double)m / n * Math.Log(2)));
    }

    private IEnumerable<int> GetHashPositions(byte[] itemBytes)
    {
        var hash1 = _hashAlgorithm1.ComputeHash(itemBytes);
        var hash2 = _hashAlgorithm2.ComputeHash(itemBytes);

        for (var i = 0; i < _hashFunctionCount; i++)
        {
            var combinedHash = unchecked((uint)(hash1 + (uint)i * hash2)) % (uint)_counterArraySize;
            yield return (int)combinedHash;
        }
    }

    private static byte[] DefaultItemToBytes(T item)
    {
        return Encoding.UTF8.GetBytes(item?.ToString() ?? string.Empty);
    }
}
