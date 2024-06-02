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
namespace Boutquin.Storage.Infrastructure.Indexing;

/// <summary>
/// Provides an in-memory implementation of the <see cref="IStorageIndex{TKey, TValue}"/> interface
/// using a sorted dictionary for efficient lookups, insertions, and deletions.
/// </summary>
/// <typeparam name="TKey">The type of the keys in the index.</typeparam>
/// <typeparam name="TValue">The type of the values in the index.</typeparam>
/// <remarks>
/// This class is not sealed to allow for extensibility and specialization. It serves as a base class 
/// for other storage index implementations, such as <see cref="InMemoryFileIndex{TKey}"/>, which may 
/// add additional features or optimizations. For instance, the derived class 
/// <see cref="InMemoryFileIndex{TKey}"/> enhances the base functionality by specifically managing 
/// file locations associated with keys. Sealing this class would prevent such extensions and limit 
/// the flexibility needed to create specialized storage indexes that build upon the core in-memory 
/// indexing functionality. Thus, to enable the creation of more advanced and feature-rich storage 
/// index implementations, this class remains unsealed.
/// </remarks>
public class InMemoryStorageIndex<TKey, TValue> : IStorageIndex<TKey, TValue> where TKey : IComparable<TKey>
{
    private readonly SortedDictionary<TKey, TValue> _index = new();

    /// <inheritdoc/>
    /// <example>
    /// <code>
    /// var index = new InMemoryStorageIndex&lt;int, string&gt;();
    /// await index.SetAsync(1, "value1");
    /// var (value, found) = await index.TryGetValueAsync(1);
    /// Console.WriteLine($"Key: 1, Value: {value}, Found: {found}");
    /// </code>
    /// </example>
    public Task SetAsync(TKey key, TValue value, CancellationToken cancellationToken = default)
    {
        Guard.AgainstNullOrDefault(() => key);
        Guard.AgainstNullOrDefault(() => value);
        cancellationToken.ThrowIfCancellationRequested();

        _index[key] = value;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    /// <example>
    /// <code>
    /// var index = new InMemoryStorageIndex&lt;int, string&gt;();
    /// await index.SetAsync(1, "value1");
    /// var (value, found) = await index.TryGetValueAsync(1);
    /// Console.WriteLine($"Key: 1, Value: {value}, Found: {found}");
    /// </code>
    /// </example>
    public Task<(TValue Value, bool Found)> TryGetValueAsync(TKey key, CancellationToken cancellationToken = default)
    {
        Guard.AgainstNullOrDefault(() => key);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(_index.TryGetValue(key, out var value) ? (value, true) : (default(TValue), false));
    }

    /// <inheritdoc/>
    /// <example>
    /// <code>
    /// var index = new InMemoryStorageIndex&lt;int, string&gt;();
    /// await index.SetAsync(1, "value1");
    /// var contains = await index.ContainsKeyAsync(1);
    /// Console.WriteLine($"Key 1 exists: {contains}");
    /// </code>
    /// </example>
    public Task<bool> ContainsKeyAsync(TKey key, CancellationToken cancellationToken = default)
    {
        Guard.AgainstNullOrDefault(() => key);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(_index.ContainsKey(key));
    }

    /// <inheritdoc/>
    /// <example>
    /// <code>
    /// var index = new InMemoryStorageIndex&lt;int, string&gt;();
    /// await index.SetAsync(1, "value1");
    /// await index.RemoveAsync(1);
    /// var (value, found) = await index.TryGetValueAsync(1);
    /// Console.WriteLine($"Key: 1, Value: {value}, Found: {found}"); // Found should be false
    /// </code>
    /// </example>
    public Task RemoveAsync(TKey key, CancellationToken cancellationToken = default)
    {
        Guard.AgainstNullOrDefault(() => key);
        cancellationToken.ThrowIfCancellationRequested();

        _index.Remove(key);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    /// <example>
    /// <code>
    /// var index = new InMemoryStorageIndex&lt;int, string&gt;();
    /// await index.SetAsync(1, "value1");
    /// await index.SetAsync(2, "value2");
    /// await index.ClearAsync();
    /// var items = await index.GetAllItemsAsync();
    /// Console.WriteLine($"Items count after clear: {items.Count()}");
    /// </code>
    /// </example>
    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _index.Clear();
        return Task.CompletedTask;
    }
}