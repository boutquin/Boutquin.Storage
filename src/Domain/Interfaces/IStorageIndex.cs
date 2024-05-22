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
/// Provides an interface for a storage index with asynchronous operations, 
/// potentially involving I/O operations. This interface extends the basic 
/// key-value store functionality, specifically for implementing indexes 
/// that map keys to values for efficient lookups and updates.
/// </summary>
/// <typeparam name="TKey">The type of the keys in the index.</typeparam>
/// <typeparam name="TValue">The type of the values in the index.</typeparam>
/// <remarks>
/// <para><b>Usage and Applications:</b></para>
/// <para>This interface is designed for creating indexes that enhance 
/// the performance and efficiency of data retrieval operations. Indexes 
/// can be used to support secondary lookups, optimize query performance, 
/// and manage large datasets efficiently.</para>
///
/// <para><b>Typical Implementations:</b></para>
/// <para>- **B-Tree Indexes:** Balanced tree structures that maintain sorted 
/// data and allow searches, sequential access, insertions, and deletions in logarithmic time.</para>
/// <para>- **Hash Indexes:** Provide fast access to data by mapping keys to values 
/// through a hash function, suitable for equality comparisons.</para>
/// <para>- **Full-Text Indexes:** Facilitate efficient searching of text data 
/// by indexing words or phrases, commonly used in search engines.</para>
///
/// <para><b>Methods:</b></para>
/// <para>- <see cref="IKeyValueStore{TKey, TValue.SetAsync"/>: Sets or updates the value for a specified key.</para>
/// <para>- <see cref="IKeyValueStore{TKey, TValue.TryGetValueAsync"/>: Attempts to retrieve the value associated with a specified key.</para>
/// <para>- <see cref="IKeyValueStore{TKey, TValue}.ContainsKeyAsync"/>: Checks whether the index contains the specified key.</para>
/// <para>- <see cref="IKeyValueStore{TKey, TValue.RemoveAsync"/>: Removes the value associated with the specified key.</para>
/// </remarks>
public interface IStorageIndex<in TKey, TValue> : IKeyValueStore<TKey, TValue> where TKey : IComparable<TKey>
{
    // Additional methods specific to the index can be added here.
}