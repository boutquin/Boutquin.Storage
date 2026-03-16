// Copyright (c) 2024-2026 Pierre G. Boutquin. All rights reserved.
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
/// <remarks>
/// <para>
/// <b>Why a Bloom filter decorator?</b> This is the Decorator pattern applied to storage engines.
/// The Bloom filter sits in front of the inner store and short-circuits reads for keys that definitely
/// don't exist, avoiding expensive disk I/O. In an LSM-tree, a read may need to check multiple
/// SSTables; the Bloom filter eliminates checks against SSTables that don't contain the key. This
/// can reduce disk reads by 90%+ for negative lookups.
/// </para>
/// <para>
/// <b>Why not rebuild the Bloom filter on Remove?</b> Bloom filters do not support element removal —
/// you cannot "unset" bits because multiple elements may share the same bit positions. Removing an
/// element's bits could create false negatives for other elements. A full rebuild (Clear + re-Add
/// all keys) is the only safe approach, but it requires reading all keys from the inner store, which
/// is expensive. The trade-off is that removed keys may produce false positives until the next
/// compaction.
/// </para>
/// <para>
/// <b>Thread safety:</b> All public operations are serialized via a <see cref="SemaphoreSlim"/>
/// to ensure thread safety. This is a simple approach suitable for single-node deployments.
/// The semaphore is disposed when the instance is disposed via <see cref="IDisposable"/>.
/// </para>
/// </remarks>
public sealed class BulkKeyValueStoreWithBloomFilter<TKey, TValue> :
    ICompactableBulkStorageEngine<TKey, TValue>,
    IDisposable
    where TKey : ISerializable<TKey>, IComparable<TKey>, new()
    where TValue : ISerializable<TValue>, new()
{
    private readonly ICompactableBulkStorageEngine<TKey, TValue> _innerStore;
    private readonly IBloomFilter<TKey> _bloomFilter;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _disposed;

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
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _innerStore.SetAsync(key, value, cancellationToken).ConfigureAwait(false);
            _bloomFilter.Add(key);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<(TValue Value, bool Found)> TryGetValueAsync(TKey key, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Why check Bloom filter first? If the Bloom filter says the key is NOT present, it's
            // guaranteed to be absent (no false negatives). We can skip the expensive inner store
            // lookup entirely. If it says the key IS present, it might be a false positive, so we
            // must verify against the inner store.
            if (!_bloomFilter.Contains(key))
            {
                return (default!, false);
            }

            return await _innerStore.TryGetValueAsync(key, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<bool> ContainsKeyAsync(TKey key, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_bloomFilter.Contains(key))
            {
                return false;
            }

            return await _innerStore.ContainsKeyAsync(key, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task RemoveAsync(TKey key, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _innerStore.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _innerStore.ClearAsync(cancellationToken).ConfigureAwait(false);
            _bloomFilter.Clear();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<(TKey Key, TValue Value)>> GetAllItemsAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _innerStore.GetAllItemsAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task SetBulkAsync(IEnumerable<KeyValuePair<TKey, TValue>> items, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _innerStore.SetBulkAsync(items, cancellationToken).ConfigureAwait(false);
            foreach (var item in items)
            {
                _bloomFilter.Add(item.Key);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task CompactAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _innerStore.CompactAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Releases the semaphore used for thread safety.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _lock.Dispose();
        _disposed = true;
    }
}
