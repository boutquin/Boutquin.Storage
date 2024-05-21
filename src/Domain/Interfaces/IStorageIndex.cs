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
/// Interface for the storage index, which manages the mapping of keys to their respective file offsets and sizes.
/// </summary>
/// <typeparam name="TKey">The type of the keys in the storage index.</typeparam>
public interface IStorageIndex<in TKey> where TKey : IComparable<TKey>
{
    /// <summary>
    /// Asynchronously adds or updates the key, offset, and count to the index.
    /// </summary>
    /// <param name="key">The key to save.</param>
    /// <param name="offset">The file offset associated with the key.</param>
    /// <param name="count">The size of the value in bytes associated with the key.</param>
    /// <param name="cancellationToken">A cancellation token that can be used by other objects or threads to receive notice of cancellation.</param>
    /// <returns>A task representing the asynchronous add or update operation.</returns>
    Task AddOrUpdateAsync(TKey key, long offset, int count, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asynchronously retrieves the offset and count for the specified key from the index.
    /// </summary>
    /// <param name="key">The key to retrieve the offset and count for.</param>
    /// <param name="cancellationToken">A cancellation token that can be used by other objects or threads to receive notice of cancellation.</param>
    /// <returns>
    /// A task representing the asynchronous retrieve operation. The task result contains a tuple with a boolean indicating 
    /// if the key was found, the offset, and the count. If the key is not found, the boolean is false, and the offset and count are default values.
    /// </returns>
    Task<(bool found, long offset, int count)> RetrieveAsync(TKey key, CancellationToken cancellationToken = default);
}
