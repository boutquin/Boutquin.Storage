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
/// Provides an interface for a key-value store with asynchronous operations, 
/// potentially involving I/O operations. This interface serves as a foundational 
/// abstraction for various storage engine implementations, offering basic 
/// CRUD operations (Create, Read, Update, Delete) for key-value pairs.
/// </summary>
/// <typeparam name="TKey">The type of the keys in the store.</typeparam>
/// <typeparam name="TValue">The type of the values in the store.</typeparam>
/// <remarks>
/// <para><b>Usage and Applications:</b></para>
/// <para>This interface can be implemented for a variety of storage solutions, including:</para>
/// <para>- **Indexes:** Implementing secondary indexes that map from keys to values, supporting efficient lookups and updates.</para>
/// <para>- **Storage Engines:** Building underlying storage engines that manage the physical layout and persistence of data.</para>
/// <para>- **Log-Structured Storage Engines:** Optimized for write-heavy workloads, often involving compaction to merge and clean up data.</para>
/// <para>- **Page-Oriented Storage Engines:** Manage data in fixed-size pages, supporting efficient random access and defragmentation to maintain performance.</para>
///
/// <para><b>Typical Implementations:</b></para>
/// <para>- **In-Memory Storage:** Fast, ephemeral storage for caching or testing.</para>
/// <para>- **Persistent Disk Storage:** Reliable, durable storage using files or databases.</para>
/// <para>- **Distributed Storage Systems:** Scalable storage across multiple nodes, ensuring high availability and fault tolerance.</para>
///
/// <para><b>Methods:</b></para>
/// <para>- <see cref="SetAsync"/>: Sets or updates the value for a specified key.</para>
/// <para>- <see cref="TryGetValueAsync"/>: Attempts to retrieve the value associated with a specified key.</para>
/// <para>- <see cref="ContainsKeyAsync"/>: Checks whether the store contains the specified key.</para>
/// <para>- <see cref="RemoveAsync"/>: Removes the value associated with the specified key.</para>
/// </remarks>
public interface IKeyValueStore<in TKey, TValue> where TKey : IComparable<TKey>
{
    /// <summary>
    /// Sets or updates the value for the specified key.
    /// If the key already exists in the store, the value is updated.
    /// If the key does not exist, a new key-value pair is added.
    /// </summary>
    /// <param name="key">The key to set or update.</param>
    /// <param name="value">The value to associate with the key.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <exception cref="ArgumentNullException">Thrown if the key or value is null.</exception>
    Task SetAsync(TKey key, TValue value);

    /// <summary>
    /// Attempts to retrieve the value associated with the specified key.
    /// </summary>
    /// <param name="key">The key whose value to retrieve.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. 
    /// The task result contains a tuple with the value associated with the key 
    /// and a boolean indicating whether the key was found.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown if the key is null.</exception>
    Task<(TValue Value, bool Found)> TryGetValueAsync(TKey key);

    /// <summary>
    /// Checks whether the store contains the specified key.
    /// </summary>
    /// <param name="key">The key to check for existence.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. 
    /// The task result contains a boolean indicating whether the key exists in the store.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown if the key is null.</exception>
    Task<bool> ContainsKeyAsync(TKey key);

    /// <summary>
    /// Removes the value associated with the specified key.
    /// If the key does not exist, the operation is a no-op.
    /// </summary>
    /// <param name="key">The key to remove.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <exception cref="ArgumentNullException">Thrown if the key is null.</exception>
    Task RemoveAsync(TKey key);
}