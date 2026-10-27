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
/// <b>Key bytes must follow key equality.</b> The read path answers "absent" from the filter alone, and
/// the filter hashes <typeparamref name="TKey"/>'s serialized bytes while the inner store finds a key
/// through <see cref="IComparable{T}.CompareTo"/>. The short-circuit is therefore sound only under one rule:
/// equal items must produce equal bytes. A key type whose <c>CompareTo</c> identifies values that
/// <c>Serialize</c> writes differently would be reported absent here although the inner store holds it. The shipped key types
/// (<c>SerializableWrapper&lt;T&gt;</c>, <c>[Key]</c>-generated record structs) satisfy this; a hand-written
/// key must canonicalise inside <c>Serialize</c>.
/// </para>
/// <para>
/// <b>Thread safety:</b> All public operations are serialized via a <see cref="SemaphoreSlim"/>
/// to ensure thread safety. This is a simple approach suitable for single-node deployments.
/// Disposing the instance while calls are in flight is safe: the call holding the semaphore completes
/// and every queued call throws <see cref="ObjectDisposedException"/>.
/// </para>
/// </remarks>
public sealed class BulkKeyValueStoreWithBloomFilter<TKey, TValue> :
    ICompactableBulkStorageEngine<TKey, TValue>,
    IDisposable
    where TKey : IStorageKey<TKey>, new()
    where TValue : ISerializable<TValue>, new()
{
    private readonly ICompactableBulkStorageEngine<TKey, TValue> _innerStore;
    private readonly IBloomFilter<TKey> _bloomFilter;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private int _disposed;

    // Private: construction alone cannot establish the filter's covering invariant (the inner
    // store may already hold keys the filter has never seen, and seeding requires async I/O).
    // CreateAsync is the only entry point, mirroring the LsmStorageEngine.OpenAsync /
    // AppendOnlyEventLog.CreateAsync async-factory convention.
    private BulkKeyValueStoreWithBloomFilter(ICompactableBulkStorageEngine<TKey, TValue> innerStore, IBloomFilter<TKey> bloomFilter)
    {
        _innerStore = innerStore ?? throw new ArgumentNullException(nameof(innerStore));
        _bloomFilter = bloomFilter ?? throw new ArgumentNullException(nameof(bloomFilter));
    }

    /// <summary>
    /// Creates a Bloom-filter-decorated store over <paramref name="innerStore"/>, seeding the
    /// filter with every key the inner store already contains.
    /// </summary>
    /// <param name="innerStore">The underlying key-value store. May already contain data.</param>
    /// <param name="bloomFilter">The Bloom filter to use for speeding up searches. Seeded from the inner store's existing keys during creation.</param>
    /// <param name="cancellationToken">A token to cancel the seeding enumeration.</param>
    /// <returns>A decorator whose filter covers every key currently in the inner store.</returns>
    /// <remarks>
    /// Seeding is what makes the read path's short-circuit sound: the "filter says absent ⇒ key
    /// is absent" guarantee (DDIA 2e Ch. 4) only holds when the filter covers <i>every</i> key in
    /// the store. A filter created empty over a pre-populated store (process restart, recovery,
    /// engine written before wrapping) would return false negatives for durably stored keys.
    /// </remarks>
    public static async Task<BulkKeyValueStoreWithBloomFilter<TKey, TValue>> CreateAsync(
        ICompactableBulkStorageEngine<TKey, TValue> innerStore,
        IBloomFilter<TKey> bloomFilter,
        CancellationToken cancellationToken = default)
    {
        var store = new BulkKeyValueStoreWithBloomFilter<TKey, TValue>(innerStore, bloomFilter);

        var existingItems = await innerStore.GetAllItemsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var (key, _) in existingItems)
        {
            bloomFilter.Add(key);
        }

        return store;
    }

    /// <inheritdoc/>
    public IEntrySerializer<TKey, TValue> EntrySerializer => _innerStore.EntrySerializer;

    /// <inheritdoc/>
    public async Task SetAsync(TKey key, TValue value, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            // Add before the inner write: a write that lands and then throws (flush failure,
            // cancellation after the append) must still be covered. A surplus bit only costs a
            // false positive; a missing bit hides a stored key.
            _bloomFilter.Add(key);
            await _innerStore.SetAsync(key, value, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<(TValue Value, bool Found)> TryGetValueAsync(TKey key, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
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
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
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
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
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
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
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
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
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
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            // Snapshot unconditionally, exactly once: the snapshot is walked for the bloom adds AND
            // handed to the inner store. A lazy one-shot enumerable would yield nothing on the
            // second pass, and a live IReadOnlyCollection (ConcurrentQueue, ConcurrentDictionary)
            // takes a fresh moment-in-time view on every enumeration. Either way keys the inner
            // store durably holds would never reach the filter — permanent false negatives on the
            // membership check.
            var materialized = items.ToList();

            // Add before the inner write: the inner engines are not atomic (they validate per
            // item inside their write loop), so a batch that fails midway can leave earlier items
            // persisted. Pre-adding keeps the filter a superset of what the inner store holds.
            foreach (var item in materialized)
            {
                _bloomFilter.Add(item.Key);
            }

            await _innerStore.SetBulkAsync(materialized, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task CompactAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            await _innerStore.CompactAsync(cancellationToken).ConfigureAwait(false);

            // Rebuild the bloom filter from the post-compaction key set. RemoveAsync between
            // compactions cannot un-set bloom bits (shared-bit hazard documented on the type),
            // so the filter's false-positive rate degrades monotonically with delete pressure.
            // Compaction is the natural rebuild point: keys gone from the inner store after
            // compaction are now also gone from the filter.
            //
            // Read and materialise the live key set BEFORE clearing: a failed or cancelled read
            // (including a fault raised while enumerating a lazy result) must leave the previous
            // covering filter in place, never an emptied one that hides stored keys.
            var liveItems = await _innerStore.GetAllItemsAsync(cancellationToken).ConfigureAwait(false);
            var liveKeys = liveItems.Select(item => item.Key).ToList();

            _bloomFilter.Clear();
            foreach (var key in liveKeys)
            {
                _bloomFilter.Add(key);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Marks the decorator disposed. The inner store is treated as a borrowed dependency
    /// (constructor injection), so the caller retains responsibility for disposing it. After
    /// dispose, every public method throws <see cref="ObjectDisposedException"/>, including a call
    /// already queued on the gate: it wakes when the current holder releases, observes the disposal
    /// and throws. An operation that held the gate when Dispose ran completes normally.
    /// </summary>
    /// <remarks>
    /// Thread-safe and idempotent: <see cref="System.Threading.Interlocked.Exchange(ref int, int)"/>
    /// atomically claims the dispose slot. The gate <see cref="SemaphoreSlim"/> is deliberately never
    /// disposed. It allocates no kernel handle unless <see cref="SemaphoreSlim.AvailableWaitHandle"/> is
    /// read, which this type never does, and disposing it under a holder would make the holder's release
    /// throw after its write reached the inner store and strand every queued caller forever.
    /// </remarks>
    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
}
