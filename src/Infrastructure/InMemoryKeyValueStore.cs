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
/// In-memory implementation of the <see cref="IBulkKeyValueStore{TKey, TValue}"/> interface using a <see cref="SortedDictionary{TKey, TValue}"/>.
/// </summary>
/// <typeparam name="TKey">The type of the keys in the key-value store.</typeparam>
/// <typeparam name="TValue">The type of the values in the key-value store.</typeparam>
public class InMemoryKeyValueStore<TKey, TValue> : IBulkKeyValueStore<TKey, TValue> where TKey : IComparable<TKey>
{
    // SortedDictionary to store key-value pairs in sorted order by key.
    private readonly SortedDictionary<TKey, TValue> _store = new();

    /// <inheritdoc/>
    /// <example>
    /// <code>
    /// var store = new InMemoryKeyValueStore&lt;int, string&gt;();
    /// await store.SetAsync(1, "value1");
    /// var (value, found) = await store.TryGetValueAsync(1);
    /// Console.WriteLine($"Key: 1, Value: {value}, Found: {found}");
    /// </code>
    /// </example>
    public Task SetAsync(TKey key, TValue value, CancellationToken cancellationToken = default)
    {
        Guard.AgainstNullOrDefault(() => key);
        Guard.AgainstNullOrDefault(() => value);
        cancellationToken.ThrowIfCancellationRequested();

        _store[key] = value; // Set or update the value for the specified key.
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    /// <example>
    /// <code>
    /// var store = new InMemoryKeyValueStore&lt;int, string&gt;();
    /// await store.SetAsync(1, "value1");
    /// var (value, found) = await store.TryGetValueAsync(1);
    /// Console.WriteLine($"Key: 1, Value: {value}, Found: {found}");
    /// </code>
    /// </example>
    public Task<(TValue Value, bool Found)> TryGetValueAsync(TKey key, CancellationToken cancellationToken = default)
    {
        Guard.AgainstNullOrDefault(() => key);
        cancellationToken.ThrowIfCancellationRequested();

        var found = _store.TryGetValue(key, out var value); // Try to get the value for the specified key.
        return Task.FromResult((value, found));
    }

    /// <inheritdoc/>
    /// <example>
    /// <code>
    /// var store = new InMemoryKeyValueStore&lt;int, string&gt;();
    /// await store.SetAsync(1, "value1");
    /// var contains = await store.ContainsKeyAsync(1);
    /// Console.WriteLine($"Key 1 exists: {contains}");
    /// </code>
    /// </example>
    public Task<bool> ContainsKeyAsync(TKey key, CancellationToken cancellationToken = default)
    {
        Guard.AgainstNullOrDefault(() => key);
        cancellationToken.ThrowIfCancellationRequested();

        var contains = _store.ContainsKey(key); // Check if the key exists in the store.
        return Task.FromResult(contains);
    }

    /// <inheritdoc/>
    /// <example>
    /// <code>
    /// var store = new InMemoryKeyValueStore&lt;int, string&gt;();
    /// await store.SetAsync(1, "value1");
    /// await store.RemoveAsync(1); // This will throw NotSupportedException.
    /// </code>
    /// </example>
    public Task RemoveAsync(TKey key, CancellationToken cancellationToken = default)
    {
        Guard.AgainstNullOrDefault(() => key);
        cancellationToken.ThrowIfCancellationRequested();

        throw new NotSupportedException("Remove operation is not supported in an append-only storage engine.");
    }

    /// <inheritdoc/>
    /// <example>
    /// <code>
    /// var store = new InMemoryKeyValueStore&lt;int, string&gt;();
    /// await store.SetAsync(1, "value1");
    /// await store.SetAsync(2, "value2");
    /// await store.ClearAsync();
    /// var items = await store.GetAllItemsAsync();
    /// Console.WriteLine($"Items count after clear: {items.Count()}");
    /// </code>
    /// </example>
    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _store.Clear(); // Clear all entries in the store.
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    /// <example>
    /// <code>
    /// var store = new InMemoryKeyValueStore&lt;int, string&gt;();
    /// await store.SetAsync(1, "value1");
    /// await store.SetAsync(2, "value2");
    /// var items = await store.GetAllItemsAsync();
    /// foreach (var (key, value) in items)
    /// {
    ///     Console.WriteLine($"Key: {key}, Value: {value}");
    /// }
    /// </code>
    /// </example>
    public Task<IEnumerable<(TKey Key, TValue Value)>> GetAllItemsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var items = new List<(TKey Key, TValue Value)>();
        foreach (var kvp in _store)
        {
            items.Add((kvp.Key, kvp.Value));
        }
        return Task.FromResult<IEnumerable<(TKey Key, TValue Value)>>(items);
    }

    /// <inheritdoc/>
    /// <example>
    /// <code>
    /// var store = new InMemoryKeyValueStore&lt;int, string&gt;();
    /// var items = new List&lt;KeyValuePair&lt;int, string&gt;&gt;
    /// {
    ///     new KeyValuePair&lt;int, string&gt;(1, "value1"),
    ///     new KeyValuePair&lt;int, string&gt;(2, "value2")
    /// };
    /// await store.SetBulkAsync(items);
    /// var allItems = await store.GetAllItemsAsync();
    /// foreach (var (key, value) in allItems)
    /// {
    ///     Console.WriteLine($"Key: {key}, Value: {value}");
    /// }
    /// </code>
    /// </example>
    public Task SetBulkAsync(IEnumerable<KeyValuePair<TKey, TValue>> items, CancellationToken cancellationToken = default)
    {
        Guard.AgainstEmptyOrNullEnumerable(() => items);
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var item in items)
        {
            Guard.AgainstNullOrDefault(() => item.Key);
            Guard.AgainstNullOrDefault(() => item.Value);
            _store[item.Key] = item.Value;
        }
        return Task.CompletedTask;
    }
}