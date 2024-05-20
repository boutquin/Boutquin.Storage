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
namespace Boutquin.Storage.Infrastructure;

/// <summary>
/// Implementation of a standard Bloom filter.
/// </summary>
/// <typeparam name="TKey">The type of the keys in the Bloom filter.</typeparam>
/// <remarks>
/// <b>Theory of Standard Bloom Filter:</b>
/// A standard Bloom filter is a straightforward implementation that uses a bit array and multiple hash functions to provide probabilistic set 
/// membership testing. When an element is added, each hash function processes the element and sets corresponding bits in the bit array. To check 
/// if an element is in the set, the Bloom filter processes the element with the same hash functions and checks the bits. If all the bits are set, 
/// the element might be in the set; if any bit is not set, the element is definitely not in the set.
/// 
/// <b>Pros and Cons in the Context of an LSM-tree:</b>
/// 
/// Pros:
/// - **Space-Efficient**: Uses a fixed-size bit array, making it very memory efficient.
/// - **Fast Operations**: Both adding elements and checking membership are fast operations, typically O(k) where k is the number of hash functions.
/// - **Simplicity**: Easy to implement and integrate with existing systems.
/// 
/// Cons:
/// - **False Positives**: There is a probability of false positives, meaning the filter might indicate that an element is in the set when it is not. 
///   This requires careful tuning of the bit array size and number of hash functions to minimize false positives.
/// - **No Deletions**: Once an element is added, it cannot be removed. This makes the standard Bloom filter unsuitable for dynamic datasets where 
///   elements need to be deleted.
/// - **Fixed Size**: The size of the bit array is fixed at creation, which means it cannot dynamically adjust to the size of the dataset.
/// 
/// In the context of an LSM-tree, the standard Bloom filter is particularly effective for optimizing read operations. By quickly determining if a key 
/// is not present in an SSTable, it reduces the number of disk reads, significantly improving read performance. However, the filter must be carefully 
/// tuned to balance false positive rates and memory usage.
/// </remarks>
public class StandardBloomFilter<TKey> : IBloomFilter<TKey> 
{
    private readonly BitArray _bitArray;
    private readonly int _numHashFunctions;
    private readonly HashAlgorithm _hashAlgorithm = SHA256.Create();

    /// <summary>
    /// Initializes a new instance of the <see cref="StandardBloomFilter{TKey}"/> class.
    /// </summary>
    /// <param name="size">The size of the Bloom filter bit array.</param>
    /// <param name="numHashFunctions">The number of hash functions to use.</param>
    /// <exception cref="ArgumentException">Thrown when size or numHashFunctions is less than or equal to 0.</exception>
    public StandardBloomFilter(int size, int numHashFunctions)
    {
        if (size <= 0) throw new ArgumentException("Size must be greater than 0.", nameof(size));
        if (numHashFunctions <= 0) throw new ArgumentException("Number of hash functions must be greater than 0.", nameof(numHashFunctions));

        _bitArray = new BitArray(size);
        _numHashFunctions = numHashFunctions;
    }

    /// <summary>
    /// Adds a key to the Bloom filter.
    /// </summary>
    /// <param name="key">The key to add to the filter.</param>
    /// <exception cref="ArgumentNullException">Thrown when key is null.</exception>
    public void Add(TKey key)
    {
        if (key == null) throw new ArgumentNullException(nameof(key), "Key cannot be null.");

        // Set bits for the given key in the bit array.
        foreach (var position in GetHashPositions(key))
        {
            _bitArray[position] = true;
        }
    }

    /// <summary>
    /// Checks if the Bloom filter might contain the specified key.
    /// </summary>
    /// <param name="key">The key to check for in the filter.</param>
    /// <returns>True if the key is possibly in the set, false if the key is definitely not in the set.</returns>
    /// <exception cref="ArgumentNullException">Thrown when key is null.</exception>
    public bool MightContain(TKey key)
    {
        if (key == null) throw new ArgumentNullException(nameof(key), "Key cannot be null.");

        // Check if all bits for the given key are set in the bit array.
        return GetHashPositions(key).All(position => _bitArray[position]);
    }

    /// <summary>
    /// Clears the Bloom filter, removing all elements.
    /// </summary>
    public void Clear()
    {
        // Reset all bits in the bit array to false.
        _bitArray.SetAll(false);
    }

    /// <summary>
    /// Computes the positions in the bit array for the given key using multiple hash functions.
    /// </summary>
    /// <param name="key">The key to hash.</param>
    /// <returns>An enumerable of positions in the bit array.</returns>
    private IEnumerable<int> GetHashPositions(TKey key)
    {
        // Compute the hash bytes for the given key.
        var hashBytes = _hashAlgorithm.ComputeHash(Encoding.UTF8.GetBytes(key.ToString()));

        // Generate positions using the hash bytes and the number of hash functions.
        for (var i = 0; i < _numHashFunctions; i++)
        {
            yield return GetPositionFromHash(hashBytes, i);
        }
    }

    /// <summary>
    /// Computes a position in the bit array from the hash bytes.
    /// </summary>
    /// <param name="hashBytes">The hash bytes generated from the key.</param>
    /// <param name="index">The index of the hash function to use.</param>
    /// <returns>A position in the bit array.</returns>
    private int GetPositionFromHash(byte[] hashBytes, int index)
    {
        // Compute the starting index in the hash bytes array.
        var startIndex = (index * 4) % (hashBytes.Length - 4);

        // Convert 4 bytes from the hash to an integer.
        var hash = BitConverter.ToInt32(hashBytes, startIndex);

        // Ensure the position is within the bounds of the bit array.
        return Math.Abs(hash % _bitArray.Length);
    }
}