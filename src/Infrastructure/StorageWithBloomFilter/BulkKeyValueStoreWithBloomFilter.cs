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
namespace Boutquin.Storage.Infrastructure.StorageWithBloomFilter;

/// <summary>
/// Provides an implementation of <see cref="IFileBasedStorageEngine{TKey, TValue}"/> that uses a Bloom filter to speed up searches.
/// </summary>
/// <typeparam name="TKey">The type of the keys in the store.</typeparam>
/// <typeparam name="TValue">The type of the values in the store.</typeparam>
public class BulkKeyValueStoreWithBloomFilter<TKey, TValue> :
    ICompactableBulkStorageEngine<TKey, TValue>
    where TKey : ISerializable<TKey>, IComparable<TKey>, new()
    where TValue : ISerializable<TValue>, new()
{
    private readonly ICompactableBulkStorageEngine<TKey, TValue> _innerStore;
    private readonly IBloomFilter<TKey> _bloomFilter;

    /// <summary>
    /// Initializes a new instance of the <see cref="BulkKeyValueStoreWithBloomFilter{TKey, TValue}"/> class.
    /// </summary>
    /// <param name="innerStore">The underlying key-value store.</param>
    /// <param name="bloomFilter">The Bloom filter to use for speeding up searches.</param>
    public BulkKeyValueStoreWithBloomFilter(ICompactableBulkStorageEngine<TKey, TValue> innerStore, IBloomFilter<TKey> bloomFilter)
    {
        _innerStore = innerStore ?? throw new ArgumentNullException(nameof(innerStore));
        _bloomFilter = bloomFilter ?? throw new ArgumentNullException(nameof(bloomFilter));
    }

    /// <inheritdoc/>
    public IEntrySerializer<TKey, TValue> EntrySerializer => _innerStore.EntrySerializer;

    /// <inheritdoc/>
    public async Task SetAsync(TKey key, TValue value, CancellationToken cancellationToken = default)
    {
        await _innerStore.SetAsync(key, value, cancellationToken);
        _bloomFilter.Add(key);
    }

    /// <inheritdoc/>
    public async Task<(TValue Value, bool Found)> TryGetValueAsync(TKey key, CancellationToken cancellationToken = default)
    {
        if (!_bloomFilter.Contains(key))
        {
            return (default, false);
        }

        return await _innerStore.TryGetValueAsync(key, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<bool> ContainsKeyAsync(TKey key, CancellationToken cancellationToken = default)
    {
        if (!_bloomFilter.Contains(key))
        {
            return false;
        }

        return await _innerStore.ContainsKeyAsync(key, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task RemoveAsync(TKey key, CancellationToken cancellationToken = default)
    {
        await _innerStore.RemoveAsync(key, cancellationToken);
        // Optionally, you can clear the Bloom filter and rebuild it.
        // For this implementation, we assume the Bloom filter is not cleared on remove.
    }

    /// <inheritdoc/>
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _innerStore.ClearAsync(cancellationToken);
        _bloomFilter.Clear();
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<(TKey Key, TValue Value)>> GetAllItemsAsync(CancellationToken cancellationToken = default)
    {
        return await _innerStore.GetAllItemsAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task SetBulkAsync(IEnumerable<KeyValuePair<TKey, TValue>> items, CancellationToken cancellationToken = default)
    {
        await _innerStore.SetBulkAsync(items, cancellationToken);
        foreach (var item in items)
        {
            _bloomFilter.Add(item.Key);
        }
    }

    /// <inheritdoc/>
    public async Task CompactAsync(CancellationToken cancellationToken = default)
    {
        await _innerStore.CompactAsync(cancellationToken);
    }
}