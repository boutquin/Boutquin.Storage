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
/// Extends the <see cref="IKeyValueStore{K, V}"/> interface with additional methods
/// for bulk operations, such as clearing the store and retrieving all items.
/// </summary>
/// <typeparam name="TKey">The type of the keys in the store.</typeparam>
/// <typeparam name="TValue">The type of the values in the store.</typeparam>
/// <remarks>
/// <para><b>Methods:</b></para>
/// <para>- <see cref="Clear"/>: Removes all key-value pairs from the store.</para>
/// <para>- <see cref="GetAllItems"/>: Retrieves all key-value pairs from the store.</para>
/// </remarks>
public interface IBulkKeyValueStore<TKey, TValue> : IKeyValueStore<TKey, TValue> where TKey : IComparable<TKey>
{
    /// <summary>
    /// Removes all key-value pairs from the store.
    /// </summary>
    /// <returns>A task representing the asynchronous clear operation.</returns>
    Task Clear();

    /// <summary>
    /// Retrieves all key-value pairs from the store.
    /// </summary>
    /// <returns>A task representing the asynchronous operation. 
    /// The task result contains an enumerable collection of all key-value pairs in the store.
    /// </returns>
    Task<IEnumerable<KeyValuePair<TKey, TValue>>> GetAllItems();
}