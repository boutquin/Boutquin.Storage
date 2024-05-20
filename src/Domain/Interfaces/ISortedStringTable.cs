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
/// Interface for interacting with a Sorted String Table (SSTable) file.
/// </summary>
/// <typeparam name="TKey">The type of the keys in the SSTable.</typeparam>
/// <typeparam name="TValue">The type of the values in the SSTable.</typeparam>
/// <remarks>
/// <b>Theory:</b>
/// A Sorted String Table (SSTable) is an immutable data structure used in the Log-Structured Merge-Tree (LSM-tree) architecture. 
/// SSTables are written to disk as the result of flushing the in-memory MemTable when it becomes full. SSTables contain sorted key-value pairs, 
/// which allows for efficient range queries and merges.
/// 
/// In the LSM-tree, write operations are first handled by the MemTable, an in-memory data structure. When the MemTable reaches its size threshold, 
/// it is flushed to disk, creating a new SSTable. This process is efficient because the MemTable already maintains the key-value pairs in sorted 
/// order, allowing the SSTable to be written sequentially to disk.
/// 
/// SSTables are immutable, meaning once they are written, they do not change. This immutability ensures that read operations can be performed 
/// without locking, thus improving read performance. To manage deleted and updated records, compaction processes merge and reorganize SSTables, 
/// discarding obsolete data.
/// 
/// <b>Choices of Implementation:</b>
/// - **Standard SSTable**: Stores sorted key-value pairs with indexing for efficient lookups. Simple to implement and provides good read performance.
/// - **Indexed SSTable**: Extends the standard SSTable by adding more advanced indexing structures to further speed up read operations. More complex 
///   to implement but can significantly improve performance for large datasets.
/// - **Compressed SSTable**: Uses compression techniques to reduce the storage footprint. Involves additional computational overhead for compression 
///   and decompression but can save significant disk space.
/// 
/// The choice of SSTable implementation depends on the specific requirements of the application, such as the need for read performance, storage 
/// efficiency, and complexity.
/// </remarks>\
public interface ISortedStringTable<TKey, TValue> : IStorageFile where TKey : IComparable<TKey>
{
    /// <summary>
    /// Writes a collection of key-value pairs to the SSTable file.
    /// </summary>
    /// <param name="items">The collection of key-value pairs to write.</param>
    void Write(IEnumerable<KeyValuePair<TKey, TValue>> items);

    /// <summary>
    /// Reads the value associated with the specified key from the SSTable file.
    /// </summary>
    /// <param name="key">The key to search for.</param>
    /// <param name="value">The value associated with the key, if found.</param>
    /// <returns>True if the key is found; otherwise, false.</returns>
    bool TryGetValue(TKey key, out TValue value);

    /// <summary>
    /// Merges another SSTable into the current SSTable.
    /// </summary>
    /// <param name="otherTable">The other SSTable to merge.</param>
    void Merge(ISortedStringTable<TKey, TValue> otherTable);

    /// <summary>
    /// Gets the number of entries in the SSTable.
    /// </summary>
    /// <returns>The number of entries.</returns>
    int GetEntryCount();

    /// <summary>
    /// Gets the creation timestamp of the SSTable.
    /// </summary>
    /// <returns>The creation timestamp.</returns>
    DateTime GetCreationTime();

    /// <summary>
    /// Gets the modification timestamp of the SSTable.
    /// </summary>
    /// <returns>The modification timestamp.</returns>
    DateTime GetLastModificationTime();
}
