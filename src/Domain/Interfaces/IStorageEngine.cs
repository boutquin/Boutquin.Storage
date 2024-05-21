﻿// Copyright (c) 2024 Pierre G. Boutquin. All rights reserved.
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
/// Interface for the storage engine, which coordinates the overall operation.
/// </summary>
/// <typeparam name="TKey">The type of the keys in the storage engine.</typeparam>
/// <typeparam name="TValue">The type of the values in the storage engine.</typeparam>
/// <remarks>
/// <para><b>Theory:</b></para>
/// <para>A storage engine is a fundamental component of a database system, responsible for managing how data is stored, retrieved, and maintained. 
/// It coordinates the overall operation of various underlying data structures and algorithms to provide efficient and reliable data management. 
/// Different storage engine implementations offer unique advantages and trade-offs, making them suitable for various use cases.</para>
/// 
/// <para><b>Implementation Choices:</b></para>
/// 
/// <para><b>LSM-tree (Log-Structured Merge-tree):</b></para>
/// <para>- <b>Pros:</b></para>
/// <para>  - <b>Write Performance:</b> Optimized for write-intensive workloads due to sequential writes, reducing write amplification and I/O operations.</para>
/// <para>  - <b>Efficient Use of Disk:</b> Handles large write loads efficiently by writing data to disk in bulk during compaction processes.</para>
/// <para>  - <b>Compaction:</b> Periodic compaction operations merge and reorganize SSTables, eliminating deleted or overwritten values and improving read performance.</para>
/// <para>  - <b>Durability:</b> Write-ahead logs (WAL) ensure data durability by logging writes before they are applied to the MemTable, allowing recovery after crashes.</para>
/// <para>  - <b>Scalability:</b> Highly scalable and can handle large datasets effectively, managing high-throughput write operations while maintaining good read performance.</para>
/// <para>- <b>Cons:</b></para>
/// <para>  - <b>Read Latency:</b> Initial read operations can be slower due to the need to search through multiple SSTables, though mitigated by Bloom filters.</para>
/// <para>  - <b>Compaction Overhead:</b> Compaction processes can be resource-intensive, requiring significant I/O and CPU resources.</para>
/// <para>  - <b>Complexity:</b> More complex to implement and manage compared to simpler data structures like B-trees.</para>
/// 
/// <para><b>B-tree:</b></para>
/// <para>- <b>Pros:</b></para>
/// <para>  - <b>Balanced Read and Write Performance:</b> Provides balanced read and write performance, suitable for mixed workloads.</para>
/// <para>  - <b>Efficient Range Queries:</b> Maintains data in sorted order, allowing for efficient range queries and ordered traversals.</para>
/// <para>  - <b>Immediate Consistency:</b> Changes are immediately applied to the tree, providing consistent read performance without the need for background compaction processes.</para>
/// <para>  - <b>Simplicity:</b> Relatively simple to implement and understand, with well-established algorithms and data structures.</para>
/// <para>- <b>Cons:</b></para>
/// <para>  - <b>Write Amplification:</b> Can suffer from write amplification due to the need to maintain balance, leading to multiple disk writes for a single insert or update operation.</para>
/// <para>  - <b>Space Overhead:</b> Maintaining internal nodes and ensuring balance can lead to higher space overhead.</para>
/// <para>  - <b>Scalability:</b> While B-trees can handle large datasets, they may not scale as efficiently as LSM-trees for very write-heavy workloads.</para>
/// 
/// <para><b>Hash Table:</b></para>
/// <para>- <b>Pros:</b></para>
/// <para>  - <b>Fast Lookups:</b> Provides O(1) average time complexity for lookups, making them extremely fast for key-value access.</para>
/// <para>  - <b>Simplicity:</b> Simple to implement and understand, with straightforward insertion and lookup operations.</para>
/// <para>- <b>Cons:</b></para>
/// <para>  - <b>No Ordered Operations:</b> Does not maintain data in sorted order, making them unsuitable for range queries and ordered traversals.</para>
/// <para>  - <b>Limited Scalability:</b> Can suffer from poor performance with large datasets due to hash collisions and the need for resizing.</para>
/// 
/// <para><b>Trie:</b></para>
/// <para>- <b>Pros:</b></para>
/// <para>  - <b>Prefix Searches:</b> Efficient for prefix searches and autocompletion, making them suitable for text-based applications.</para>
/// <para>  - <b>Ordered Data:</b> Maintains data in lexicographical order, allowing for ordered operations and range queries.</para>
/// <para>- <b>Cons:</b></para>
/// <para>  - <b>Space Overhead:</b> Can consume more memory compared to other data structures due to the need to store nodes for each character.</para>
/// <para>  - <b>Complexity:</b> More complex to implement and manage compared to simpler data structures like hash tables or B-trees.</para>
/// 
/// <para><b>In-Memory Storage (e.g., Redis):</b></para>
/// <para>- <b>Pros:</b></para>
/// <para>  - <b>High Performance:</b> Provides extremely fast read and write operations due to the absence of disk I/O.</para>
/// <para>  - <b>Advanced Data Structures:</b> Many in-memory systems offer advanced data structures and functionalities, such as sets, lists, and pub/sub messaging.</para>
/// <para>- <b>Cons:</b></para>
/// <para>  - <b>Volatility:</b> Data stored in memory is volatile and can be lost in the event of a crash or power failure, unless persistent storage mechanisms are used.</para>
/// <para>  - <b>Cost:</b> Memory is more expensive compared to disk storage, making it cost-prohibitive for very large datasets.</para>
/// 
/// <para>The choice of storage engine implementation depends on the specific requirements of the application, such as read/write performance, data consistency, scalability, and cost considerations.</para>
/// </remarks>
public interface IStorageEngine<in TKey, TValue> where TKey : IComparable<TKey>
{
    /// <summary>
    /// Writes a key-value pair to the storage engine.
    /// </summary>
    /// <param name="key">The key of the item to write.</param>
    /// <param name="value">The value of the item to write.</param>
    /// <param name="cancellationToken">A cancellation token that can be used by other objects or threads to receive notice of cancellation.</param>
    /// <returns>A task representing the asynchronous write operation.</returns>
    Task WriteAsync(TKey key, TValue value, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds the value associated with the specified key in the storage engine.
    /// </summary>
    /// <param name="key">The key to locate.</param>
    /// <param name="cancellationToken">A cancellation token that can be used by other objects or threads to receive notice of cancellation.</param>
    /// <returns>
    /// A task representing the asynchronous locate operation. The task result contains a tuple with a boolean indicating 
    /// if the key was found and the value associated with the specified key, if found; otherwise, the default value for 
    /// the type of the value parameter.
    /// </returns>
    Task<(bool found, TValue value)> FindAsync(TKey key, CancellationToken cancellationToken = default);
}