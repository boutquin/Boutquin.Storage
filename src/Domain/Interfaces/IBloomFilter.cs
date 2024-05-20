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
namespace Boutquin.Storage.Domain.Interfaces;

/// <summary>
/// Interface for a Bloom filter, a probabilistic data structure for set membership testing.
/// </summary>
/// <typeparam name="TKey">The type of the keys in the Bloom filter.</typeparam>
/// <remarks>
/// <b>Theory:</b>
/// Bloom filters are probabilistic data structures designed to efficiently test whether an element is part of a set. They are highly memory-efficient and 
/// can quickly determine if an element is definitely not in the set or might be in the set, with some false positives but no false negatives. This makes 
/// Bloom filters particularly useful for scenarios where false negatives are unacceptable but false positives are tolerable.
/// 
/// In the context of an LSM-tree (Log-Structured Merge-Tree) algorithm, Bloom filters play a critical role in optimizing read performance by reducing 
/// unnecessary disk I/O operations. As data is written to an LSM-tree, it is first stored in an in-memory structure called a MemTable. When the MemTable 
/// is full, it is flushed to disk as an immutable SSTable (Sorted String Table). Over time, multiple SSTables accumulate, making read operations potentially 
/// slow as a query might need to scan through many SSTables to find or confirm the absence of a key.
/// 
/// <b>Workings in the Context of an LSM-tree:</b>
/// By integrating Bloom filters with each SSTable, the LSM-tree algorithm can quickly determine if a key does not exist in an SSTable, thereby avoiding the 
/// need to scan the SSTable for non-existent keys. This is achieved by creating a Bloom filter for each SSTable during the flush process from the MemTable 
/// to disk. The Bloom filter stores the keys present in the SSTable using multiple hash functions to set bits in a bit array.
/// 
/// During a read operation, the Bloom filter is consulted first. If the Bloom filter indicates that a key is definitely not present (any of the bits are not set), 
/// the SSTable scan is skipped. If the Bloom filter indicates that the key might be present (all bits are set), the SSTable is then scanned to confirm the key's 
/// presence. This significantly reduces the number of SSTable scans required for non-existent keys, optimizing read performance.
/// 
/// The efficiency of a Bloom filter is determined by its parameters: the size of the bit array and the number of hash functions. These parameters are chosen 
/// based on the expected number of elements and the acceptable false positive rate, balancing memory usage and accuracy.
/// </remarks>
public interface IBloomFilter<TKey>
{
    /// <summary>
    /// Adds a key to the Bloom filter.
    /// </summary>
    /// <param name="key">The key to add to the filter.</param>
    void Add(TKey key);

    /// <summary>
    /// Checks if the Bloom filter possibly contains the specified key.
    /// </summary>
    /// <param name="key">The key to check for in the filter.</param>
    /// <returns>True if the key is possibly in the set, false if the key is definitely not in the set.</returns>
    bool MightContain(TKey key);

    /// <summary>
    /// Clears the Bloom filter, removing all elements.
    /// </summary>
    void Clear();
}
