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
/// Provides an interface for a read-only key-value store with asynchronous operations,
/// potentially involving I/O operations. This interface serves as a foundational
/// abstraction for various storage engine implementations, offering basic read operations
/// for key-value pairs.
/// </summary>
/// <typeparam name="TKey">The type of the keys in the store.</typeparam>
/// <typeparam name="TValue">The type of the values in the store.</typeparam>
/// <remarks>
/// <para><b>Usage and Applications:</b></para>
/// <para>This interface can be implemented for a variety of storage solutions, including:</para>
/// <para>- **Indexes:** Implementing secondary indexes that map from keys to values, supporting efficient lookups.</para>
/// <para>- **Storage Engines:** Building underlying storage engines that manage the physical layout and persistence of data.</para>
/// <para>- **Log-Structured Storage Engines:** Optimized for read-heavy workloads, often involving compaction to merge and clean up data.</para>
/// <para>- **Page-Oriented Storage Engines:** Manage data in fixed-size pages, supporting efficient random access and defragmentation to maintain performance.</para>
///
/// <para><b>Typical Implementations:</b></para>
/// <para>- **In-Memory Storage:** Fast, ephemeral storage for caching or testing.</para>
/// <para>- **Persistent Disk Storage:** Reliable, durable storage using files or databases.</para>
/// <para>- **Distributed Storage Systems:** Scalable storage across multiple nodes, ensuring high availability and fault tolerance.</para>
///
/// <para><b>Methods:</b></para>
/// <para>- <see cref="TryGetValueAsync"/>: Attempts to retrieve the value associated with a specified key.</para>
/// <para>- <see cref="ContainsKeyAsync"/>: Checks whether the store contains the specified key.</para>
/// </remarks>
public interface IReadOnlyKeyValueStore<in TKey, TValue>
    where TKey : IComparable<TKey>
{
    /// <summary>
    /// Attempts to retrieve the value associated with the specified key.
    /// </summary>
    /// <param name="key">The key whose value to retrieve.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. 
    /// The task result contains a tuple with the value associated with the key 
    /// and a boolean indicating whether the key was found.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown if the key is null.</exception>
    /// <exception cref="ArgumentException">Thrown if the key is the default value.</exception>
    /// <exception cref="OperationCanceledException">Thrown if the operation is canceled.</exception>
    Task<(TValue Value, bool Found)> TryGetValueAsync(TKey key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks whether the store contains the specified key.
    /// </summary>
    /// <param name="key">The key to check for existence.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. 
    /// The task result contains a boolean indicating whether the key exists in the store.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown if the key is null.</exception>
    /// <exception cref="ArgumentException">Thrown if the key is the default value.</exception>
    /// <exception cref="OperationCanceledException">Thrown if the operation is canceled.</exception>
    Task<bool> ContainsKeyAsync(TKey key, CancellationToken cancellationToken = default);
}