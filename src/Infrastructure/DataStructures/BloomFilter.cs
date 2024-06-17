// Copyright (c) 2024 Pierre G. Boutquin. All rights reserved.
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
/// <para>Typical Uses:</para>
/// <para>In the context of a storage engine, a Bloom filter is often used to reduce the number of disk lookups.
/// Before performing a disk read operation to fetch a value, the Bloom filter can be checked to see if the key likely exists in the dataset.
/// If the Bloom filter indicates that the key does not exist, the costly disk read can be skipped, thereby improving performance.</para>
/// 
/// <para>Example:</para>
/// <code>
/// var expectedElements = 1000;
/// var falsePositiveProbability = 0.01; // 1% false positive rate
/// var bloomFilter = new BloomFilter&lt;string&gt;(expectedElements, falsePositiveProbability);
/// 
/// bloomFilter.Add("exampleKey");
/// bool exists = bloomFilter.Contains("exampleKey"); // True
/// bloomFilter.Clear();
/// </code>
/// </summary>
/// <typeparam name="T">The type of elements to be stored in the Bloom filter.</typeparam>
public class BloomFilter<T> : IBloomFilter<T>
{
    private readonly BitArray _bitArray;
    private readonly int _hashFunctionCount;
    private readonly int _bitArraySize;
    private readonly Func<T, byte[]> _itemToBytes;
    private readonly IHashAlgorithm _hashAlgorithm1;
    private readonly IHashAlgorithm _hashAlgorithm2;

    /// <summary>
    /// Initializes a new instance of the <see cref="BloomFilter{T}" /> class with expected elements and false positive probability.
    /// This constructor uses Murmur3 and xxHash32 as default hash algorithms.
    /// </summary>
    /// <param name="expectedElements">The expected number of elements to store in the Bloom filter.</param>
    /// <param name="falsePositiveProbability">The desired false positive probability.</param>
    public BloomFilter(int expectedElements, double falsePositiveProbability)
        : this(expectedElements, falsePositiveProbability, new Murmur3(), new XxHash32(), DefaultItemToBytes)
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
    {
        _bitArraySize = CalculateOptimalBitArraySize(expectedElements, falsePositiveProbability);
        _hashFunctionCount = CalculateOptimalHashFunctionCount(expectedElements, _bitArraySize);
        _bitArray = new BitArray(_bitArraySize);
        _itemToBytes = itemToBytes ?? throw new ArgumentNullException(nameof(itemToBytes));
        _hashAlgorithm1 = hashAlgorithm1 ?? throw new ArgumentNullException(nameof(hashAlgorithm1));
        _hashAlgorithm2 = hashAlgorithm2 ?? throw new ArgumentNullException(nameof(hashAlgorithm2));
    }

    /// <summary>
    /// Adds an item to the Bloom filter. Each item is hashed multiple times; each hash points to a bit in the bit array which is then set to true.
    /// </summary>
    /// <param name="item">The item to add to the Bloom filter.</param>
    public void Add(T item)
    {
        // Check if the item is null and throw ArgumentNullException
        if (item == null)
        {
            throw new ArgumentNullException(nameof(item), "Cannot add null item to the Bloom filter.");
        }

        var itemBytes = _itemToBytes(item);
        foreach (var position in GetHashPositions(itemBytes))
        {
            _bitArray[position] = true;
        }
    }

    /// <summary>
    /// Checks if an item is possibly in the Bloom filter. If any of the bits corresponding to the item's hashes are false, the item is definitely not in the filter.
    /// </summary>
    /// <param name="item">The item to check for presence in the Bloom filter.</param>
    /// <returns>True if the item might be in the set, false if the item is definitely not in the set.</returns>
    public bool Contains(T item)
    {
        var itemBytes = _itemToBytes(item);
        foreach (var position in GetHashPositions(itemBytes))
        {
            if (!_bitArray[position])
            {
                return false; // Definitely not present
            }
        }
        return true; // Possibly present
    }

    /// <summary>
    /// Clears the Bloom filter, resetting all bits in the bit array to false.
    /// </summary>
    public void Clear()
    {
        _bitArray.SetAll(false);
    }

    /// <summary>
    /// Calculates the optimal size of the bit array based on the expected number of elements and the desired false positive probability.
    /// </summary>
    /// <param name="n">The expected number of elements.</param>
    /// <param name="p">The desired false positive probability.</param>
    /// <returns>The optimal size of the bit array.</returns>
    private static int CalculateOptimalBitArraySize(int n, double p)
    {
        return (int)Math.Ceiling((-n * Math.Log(p)) / (Math.Log(2) * Math.Log(2)));
    }

    /// <summary>
    /// Calculates the optimal number of hash functions based on the size of the bit array and the expected number of elements.
    /// </summary>
    /// <param name="n">The expected number of elements.</param>
    /// <param name="m">The size of the bit array.</param>
    /// <returns>The optimal number of hash functions.</returns>
    private static int CalculateOptimalHashFunctionCount(int n, int m)
    {
        return (int)Math.Max(1, Math.Round((double)m / n * Math.Log(2)));
    }

    /// <summary>
    /// Generates the hash positions for the given item using double hashing. This method is crucial for distributing elements uniformly across the bit array.
    /// </summary>
    /// <param name="itemBytes">The byte array representation of the item.</param>
    /// <returns>A list of positions in the bit array.</returns>
    /// <remarks>
    /// This method combines two hash functions, Murmur3 and xxHash32, to generate multiple hash values for the item.
    /// Double hashing helps to reduce the likelihood of hash collisions and improves the distribution of bits in the array.
    /// </remarks>
    private IEnumerable<int> GetHashPositions(byte[] itemBytes)
    {
        var hash1 = (int)_hashAlgorithm1.ComputeHash(itemBytes);
        var hash2 = (int)_hashAlgorithm2.ComputeHash(itemBytes);

        for (var i = 0; i < _hashFunctionCount; i++)
        {
            var combinedHash = Math.Abs(hash1 + i * hash2) % _bitArraySize;
            yield return combinedHash;
        }
    }

    /// <summary>
    /// Default method to convert an item to a byte array. This method is used if no custom conversion function is provided.
    /// </summary>
    /// <param name="item">The item to convert.</param>
    /// <returns>The byte array representation of the item.</returns>
    private static byte[] DefaultItemToBytes(T item)
    {
        return Encoding.UTF8.GetBytes(item?.ToString() ?? string.Empty);
    }
}
