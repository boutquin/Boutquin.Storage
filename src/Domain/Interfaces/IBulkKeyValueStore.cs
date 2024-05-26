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
namespace Boutquin.Storage.Domain.Interfaces
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Extends the <see cref="IKeyValueStore{TKey, TValue}"/> interface with additional methods
    /// for bulk operations, such as clearing the store and retrieving all items.
    /// </summary>
    /// <typeparam name="TKey">The type of the keys in the store.</typeparam>
    /// <typeparam name="TValue">The type of the values in the store.</typeparam>
    /// <remarks>
    /// <para><b>Usage and Applications:</b></para>
    /// <para>This interface can be implemented for a variety of storage solutions that require bulk operations, including:</para>
    /// <para>- **Data Migration:** Facilitating the transfer of large volumes of key-value pairs between storage systems.</para>
    /// <para>- **Batch Processing:** Efficiently processing or analyzing all items in the store at once.</para>
    /// <para>- **Maintenance Tasks:** Performing bulk operations such as clearing the store or archiving old data.</para>
    ///
    /// <para><b>Typical Implementations:</b></para>
    /// <para>- **In-Memory Storage:** Efficiently handling bulk operations in a fast, ephemeral storage.</para>
    /// <para>- **Persistent Disk Storage:** Managing large datasets on disk with support for bulk retrieval and deletion.</para>
    /// <para>- **Distributed Storage Systems:** Coordinating bulk operations across multiple nodes in a scalable storage system.</para>
    ///
    /// <para><b>Methods:</b></para>
    /// <para>- <see cref="IKeyValueStore{TKey, TValue}.SetAsync"/>: Sets or updates the value for a specified key.</para>
    /// <para>- <see cref="IKeyValueStore{TKey, TValue}.TryGetValueAsync"/>: Attempts to retrieve the value associated with a specified key.</para>
    /// <para>- <see cref="IKeyValueStore{TKey, TValue}.ContainsKeyAsync"/>: Checks whether the store contains the specified key.</para>
    /// <para>- <see cref="IKeyValueStore{TKey, TValue}.RemoveAsync"/>: Removes the value associated with the specified key.</para>
    /// <para>- <see cref="IKeyValueStore{TKey, TValue}.ClearAsync"/>: Removes all key-value pairs from the store.</para>
    /// <para>- <see cref="GetAllItemsAsync"/>: Retrieves all key-value pairs from the store.</para>
    /// <para>- <see cref="SetBulkAsync"/>: Sets or updates values for multiple keys.</para>
    /// </remarks>
    public interface IBulkKeyValueStore<TKey, TValue> : IKeyValueStore<TKey, TValue> where TKey : IComparable<TKey>
    {
        /// <summary>
        /// Retrieves all key-value pairs from the store.
        /// </summary>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>
        /// A task representing the asynchronous operation. 
        /// The task result contains an enumerable collection of all key-value pairs in the store.
        /// </returns>
        /// <exception cref="OperationCanceledException">Thrown if the operation is canceled.</exception>
        Task<IEnumerable<(TKey Key, TValue Value)>> GetAllItemsAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Sets or updates the values for the specified keys.
        /// If a key already exists in the store, the value is updated.
        /// If a key does not exist, a new key-value pair is added.
        /// </summary>
        /// <param name="items">The key-value pairs to set or update.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        /// <exception cref="ArgumentNullException">Thrown if the items collection or any of its elements are null.</exception>
        /// <exception cref="ArgumentException">Thrown if any key or value in the items collection is the default value.</exception>
        /// <exception cref="OperationCanceledException">Thrown if the operation is canceled.</exception>
        Task SetBulkAsync(IEnumerable<KeyValuePair<TKey, TValue>> items, CancellationToken cancellationToken = default);
    }
}
