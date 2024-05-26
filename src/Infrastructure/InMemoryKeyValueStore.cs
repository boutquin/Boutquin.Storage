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
/// In-memory implementation of the IBulkKeyValueStore interface using a SortedDictionary.
/// </summary>
/// <typeparam name="TKey">The type of the keys in the key-value store.</typeparam>
/// <typeparam name="TValue">The type of the values in the key-value store.</typeparam>
public class InMemoryKeyValueStore<TKey, TValue> : IBulkKeyValueStore<TKey, TValue> where TKey : IComparable<TKey>
{
    // SortedDictionary to store key-value pairs in sorted order by key.
    private readonly SortedDictionary<TKey, TValue> _store = new();

    /// <summary>
    /// Sets or updates the value for the specified key.
    /// </summary>
    /// <param name="key">The key to set or update.</param>
    /// <param name="value">The value to set for the key.</param>
    /// <returns>A completed task.</returns>
    public Task SetAsync(TKey key, TValue value)
    {
        _store[key] = value; // Set or update the value for the specified key.
        return Task.CompletedTask;
    }

    /// <summary>
    /// Tries to retrieve the value for the specified key.
    /// </summary>
    /// <param name="key">The key to retrieve the value for.</param>
    /// <returns>A task that returns a tuple containing the value and a boolean indicating whether the key was found.</returns>
    public Task<(TValue Value, bool Found)> TryGetValueAsync(TKey key)
    {
        var found = _store.TryGetValue(key, out var value); // Try to get the value for the specified key.
        return Task.FromResult((value, found));
    }

    /// <summary>
    /// Checks if the specified key exists in the store.
    /// </summary>
    /// <param name="key">The key to check for existence.</param>
    /// <returns>A task that returns a boolean indicating whether the key exists.</returns>
    public Task<bool> ContainsKeyAsync(TKey key)
    {
        var contains = _store.ContainsKey(key); // Check if the key exists in the store.
        return Task.FromResult(contains);
    }

    /// <summary>
    /// Removes the specified key from the store.
    /// </summary>
    /// <param name="key">The key to remove.</param>
    /// <returns>A task that returns an exception as this operation is not supported.</returns>
    /// <exception cref="NotSupportedException">Thrown because the remove operation is not supported in an append-only storage engine.</exception>
    public Task RemoveAsync(TKey key)
    {
        throw new NotSupportedException("Remove operation is not supported in an append-only storage engine.");
    }

    /// <summary>
    /// Clears all entries in the store.
    /// </summary>
    /// <returns>A completed task.</returns>
    public Task Clear()
    {
        _store.Clear(); // Clear all entries in the store.
        return Task.CompletedTask;
    }

    /// <summary>
    /// Retrieves all key-value pairs in the store.
    /// </summary>
    /// <returns>A task that returns an IEnumerable of tuples containing the key-value pairs.</returns>
    public Task<IEnumerable<(TKey Key, TValue Value)>> GetAllItemsAsync()
    {
        // Directly iterate over the SortedDictionary to avoid additional memory allocation and copying.
        var items = new List<(TKey Key, TValue Value)>();
        foreach (var kvp in _store)
        {
            items.Add((kvp.Key, kvp.Value));
        }
        return Task.FromResult<IEnumerable<(TKey Key, TValue Value)>>(items);
    }
}