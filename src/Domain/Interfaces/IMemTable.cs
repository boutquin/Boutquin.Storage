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
//  See the License for the specific language governing permissions and
//  limitations under the License.
//
namespace Boutquin.Storage.Domain.Interfaces;

/// <summary>
/// Interface for a MemTable, an in-memory data structure used in an LSM-tree.
/// </summary>
/// <typeparam name="TKey">The type of the keys in the MemTable.</typeparam>
/// <typeparam name="TValue">The type of the values in the MemTable.</typeparam>
/// <remarks>
/// <b>Theory:</b>
/// A MemTable is an in-memory data structure used to store write operations (insertions, updates, and deletions) before they are flushed to disk 
/// in the form of SSTables (Sorted String Tables). The MemTable plays a crucial role in the Log-Structured Merge-Tree (LSM-tree) architecture by 
/// providing a buffer for incoming writes and ensuring that write operations are fast and efficient.
/// 
/// <b>Application in LSM-trees:</b>
/// In an LSM-tree, write operations are first directed to the MemTable, which allows for fast in-memory writes. Once the MemTable reaches a 
/// certain size threshold, it is flushed to disk as an immutable SSTable. This process helps to minimize the number of disk I/O operations 
/// required for each write, significantly improving write throughput.
/// 
/// The MemTable must maintain an ordered structure to facilitate the creation of sorted SSTables during the flush process. Additionally, 
/// the MemTable should provide efficient read operations to allow for quick retrieval of recently written data.
/// 
/// <b>Choices of Implementation:</b>
/// Various data structures can be used to implement a MemTable, each with its own trade-offs:
/// - **Red-Black Tree**: A self-balancing binary search tree that provides O(log n) time complexity for insertions, deletions, and lookups. 
///   It maintains an ordered structure, making it suitable for efficient creation of SSTables. However, it can be more complex to implement 
///   compared to simpler structures.
/// - **Skip List**: A probabilistic data structure that provides O(log n) time complexity for insertions, deletions, and lookups. Skip lists 
///   are simpler to implement than red-black trees and also maintain an ordered structure. However, their performance can be less consistent 
///   due to their probabilistic nature.
/// - **AVL Tree**: Another self-balancing binary search tree with O(log n) time complexity for operations. AVL trees provide more rigid balancing 
///   compared to red-black trees, which can lead to better search performance but higher overhead for insertions and deletions.
/// - **Hash Table**: Provides O(1) average time complexity for insertions, deletions, and lookups. However, hash tables do not maintain an ordered 
///   structure, making them less suitable for MemTables in LSM-trees where sorted order is important for SSTable creation.
/// 
/// The choice of data structure depends on the specific requirements of the application, such as the need for ordered data, the expected write 
/// throughput, and the complexity of implementation.
/// </remarks>
public interface IMemTable<TKey, TValue>
{
    /// <summary>
    /// Adds a key-value pair to the MemTable.
    /// </summary>
    /// <param name="key">The key to add.</param>
    /// <param name="value">The value associated with the key.</param>
    void Add(TKey key, TValue value);

    /// <summary>
    /// Attempts to get the value associated with the specified key from the MemTable.
    /// </summary>
    /// <param name="key">The key to find.</param>
    /// <param name="value">When this method returns, contains the value associated with the specified key, if the key is found; 
    /// otherwise, the default value for the type of the value parameter.</param>
    /// <returns><c>true</c> if the key was found; otherwise, <c>false</c>.</returns>
    bool TryGetValue(TKey key, out TValue value);

    /// <summary>
    /// Clears the MemTable, removing all elements.
    /// </summary>
    void Clear();

    /// <summary>
    /// Gets all the items in the MemTable in sorted order.
    /// </summary>
    /// <returns>An enumeration of key-value pairs in the MemTable, sorted by key.</returns>
    IEnumerable<KeyValuePair<TKey, TValue>> GetAllItems();
}