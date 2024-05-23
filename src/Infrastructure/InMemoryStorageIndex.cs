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
namespace Boutquin.Storage.Infrastructure;

/// <summary>
/// Provides an in-memory implementation of the <see cref="IStorageIndex{TKey, TValue}"/> interface
/// using a sorted dictionary for efficient lookups, insertions, and deletions.
/// </summary>
/// <typeparam name="TKey">The type of the keys in the index.</typeparam>
/// <typeparam name="TValue">The type of the values in the index.</typeparam>
public class InMemoryStorageIndex<TKey, TValue> : IStorageIndex<TKey, TValue> where TKey : IComparable<TKey>
{
    private readonly SortedDictionary<TKey, TValue> _index = [];

    /// <inheritdoc/>
    public Task SetAsync(TKey key, TValue value)
    {
        // Validate the key to ensure it is not null or default.
        Guard.AgainstNullOrDefault(() => key);

        _index[key] = value;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<(TValue Value, bool Found)> TryGetValueAsync(TKey key)
    {
        // Validate the key to ensure it is not null or default.
        Guard.AgainstNullOrDefault(() => key);

        return Task.FromResult(_index.TryGetValue(key, out var value) ? (value, true) : (default(TValue), false));
    }

    /// <inheritdoc/>
    public Task<bool> ContainsKeyAsync(TKey key)
    {
        // Validate the key to ensure it is not null or default.
        Guard.AgainstNullOrDefault(() => key);

        return Task.FromResult(_index.ContainsKey(key));
    }

    /// <inheritdoc/>
    public Task RemoveAsync(TKey key)
    {
        // Validate the key to ensure it is not null or default.
        Guard.AgainstNullOrDefault(() => key);

        _index.Remove(key);
        return Task.CompletedTask;
    }
}