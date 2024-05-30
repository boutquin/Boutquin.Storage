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
/// Provides an interface for a bulk storage engine with asynchronous operations, 
/// potentially involving I/O operations. This interface extends the basic 
/// bulk key-value store functionality with additional features suited for 
/// robust storage solutions.
/// </summary>
/// <typeparam name="TKey">The type of the keys in the storage engine.</typeparam>
/// <typeparam name="TValue">The type of the values in the storage engine.</typeparam>
/// <remarks>
/// <para><b>Usage and Applications:</b></para>
/// <para>This interface is designed for more comprehensive storage solutions, 
/// expanding on the basic bulk key-value store capabilities. Implementations can vary 
/// widely and include features such as indexing, transactions, and advanced 
/// optimization techniques.</para>
///
/// <para><b>Typical Implementations:</b></para>
/// <para>- **Log-Structured Storage Engines:** Optimized for write-heavy workloads, often involving 
/// compaction to merge and clean up data.</para>
/// <para>- **Page-Oriented Storage Engines:** Manage data in fixed-size pages, supporting 
/// efficient random access and defragmentation to maintain performance.</para>
/// <para>- **Transactional Storage Engines:** Provide support for ACID transactions, 
/// ensuring data integrity and consistency.</para>
///
/// <para><b>Methods:</b></para>
/// <para>- <see cref="IBulkKeyValueStore{TKey, TValue}.SetAsync"/>: Sets or updates the value for a specified key.</para>
/// <para>- <see cref="IBulkKeyValueStore{TKey, TValue}.TryGetValueAsync"/>: Attempts to retrieve the value associated with a specified key.</para>
/// <para>- <see cref="IBulkKeyValueStore{TKey, TValue}.ContainsKeyAsync"/>: Checks whether the store contains the specified key.</para>
/// <para>- <see cref="IBulkKeyValueStore{TKey, TValue}.RemoveAsync"/>: Removes the value associated with the specified key.</para>
/// <para>- <see cref="IBulkKeyValueStore{TKey, TValue}.ClearAsync"/>: Removes all key-value pairs from the store.</para>
/// <para>- <see cref="IBulkKeyValueStore{TKey, TValue}.GetAllItemsAsync"/>: Retrieves all key-value pairs from the store.</para>
/// <para>- <see cref="IBulkKeyValueStore{TKey, TValue}.SetBulkAsync"/>: Sets or updates values for multiple keys.</para>
/// </remarks>
public interface IBulkStorageEngine<TKey, TValue> : 
    IStorageEngine<TKey, TValue>, IBulkKeyValueStore<TKey, TValue>
        where TKey : IComparable<TKey>, ISerializable<TKey>, new()
        where TValue : ISerializable<TValue>, new();