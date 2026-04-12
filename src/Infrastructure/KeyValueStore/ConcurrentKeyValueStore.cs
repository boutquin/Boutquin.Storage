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
namespace Boutquin.Storage.Infrastructure.KeyValueStore;

/// <summary>
/// Thread-safe in-memory key-value store backed by <see cref="ConcurrentDictionary{TKey,TValue}"/>.
///
/// <para>Unlike <see cref="InMemoryKeyValueStore{TKey,TValue}"/>, this implementation is safe for
/// concurrent reads and writes without external synchronization, and requires only
/// <c>IComparable</c> on <typeparamref name="TKey"/> — no <c>ISerializable</c> constraint.
/// This makes it suitable for caching use cases (e.g., <c>ExactOnceMemoryCache</c>) where
/// sorted enumeration is not needed and concurrent access is the norm.</para>
///
/// <para><b>Trade-off vs. InMemoryKeyValueStore:</b> this store does NOT maintain sorted key order.
/// <see cref="InMemoryKeyValueStore{TKey,TValue}"/> uses <c>SortedDictionary</c> for LSM MemTable
/// semantics (keys must be sorted for SSTable flush). Use that store for MemTable; use this store
/// for caches, dedup tables, and general concurrent key-value access.</para>
///
/// <para><b>RemoveAsync semantics:</b> unlike <see cref="InMemoryKeyValueStore{TKey,TValue}"/> which
/// throws <see cref="NotSupportedException"/> (append-only MemTable semantics), this store supports
/// removal — caches must evict entries.</para>
///
/// <para><b>Reference:</b> Kleppmann, <i>Designing Data-Intensive Applications</i> (O'Reilly, 2017),
/// Ch. 4 — "Keeping Everything in Memory": in-memory databases avoid disk encoding overhead;
/// the performance advantage comes from native in-memory data structures, not from avoiding disk reads.</para>
/// </summary>
/// <typeparam name="TKey">The type of the keys in the store.</typeparam>
/// <typeparam name="TValue">The type of the values in the store.</typeparam>
public sealed class ConcurrentKeyValueStore<TKey, TValue> : IKeyValueStore<TKey, TValue>
    where TKey : IComparable<TKey>
{
    private readonly ConcurrentDictionary<TKey, TValue> _store = new();

    /// <inheritdoc />
    public Task SetAsync(TKey key, TValue value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        cancellationToken.ThrowIfCancellationRequested();

        _store[key] = value;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<(TValue Value, bool Found)> TryGetValueAsync(TKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();

        var found = _store.TryGetValue(key, out var value);
        return Task.FromResult((value!, found));
    }

    /// <inheritdoc />
    public Task<bool> ContainsKeyAsync(TKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(_store.ContainsKey(key));
    }

    /// <inheritdoc />
    public Task RemoveAsync(TKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();

        _store.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _store.Clear();
        return Task.CompletedTask;
    }
}
