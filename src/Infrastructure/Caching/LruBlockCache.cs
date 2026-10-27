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
using Boutquin.Storage.Domain.ValueObjects.SortedStringTable;

namespace Boutquin.Storage.Infrastructure.Caching;

/// <summary>
/// Sharded least-recently-used implementation of <see cref="IBlockCache"/>. Each shard owns
/// its own lock + dictionary + recency list, so concurrent readers across distinct shards
/// never contend; readers within a shard contend on a per-shard <see langword="lock"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Eviction policy.</b> Each shard is a standard LRU implemented as a
/// <see cref="LinkedList{T}"/> + <see cref="Dictionary{TKey,TValue}"/> pair: <see cref="TryGet"/>
/// promotes the hit entry to the head; <see cref="Put"/> inserts at the head and evicts from
/// the tail until the per-shard byte budget is satisfied. Eviction order across shards is
/// independent — the global budget is split evenly across shards, matching the expected key
/// hash distribution.
/// </para>
/// <para>
/// <b>Sharding.</b> Shard count defaults to <c>16</c> (the LevelDB / RocksDB convention) and
/// must be a positive integer. Each shard receives <c>maxBytes / shardCount</c> bytes of
/// budget; remainders fall to the last shard. The shard for a given key is
/// <c>(key.SegmentId * 0x9E3779B97F4A7C15) ^ key.BlockOffset</c> mod shard count — the
/// SegmentId × Fibonacci-mixing constant decorrelates the dominant offset prefix so adjacent
/// blocks within the same segment land on different shards.
/// </para>
/// <para>
/// <b>Thread-safety.</b> Each shard's lock guards its dictionary, recency list, and current
/// byte usage. <see cref="Clear"/> takes every shard's lock in turn — readers may observe a
/// partially-cleared state but never an inconsistent shard, which matches the contract that
/// <see cref="Clear"/> can race with reads.
/// </para>
/// <para>
/// <b>Reference:</b> the sharded LRU block cache of LevelDB / RocksDB (the SSTable read-path
/// cache this type mirrors); DDIA 2e Ch. 4 covers the SSTable read path itself but does not
/// discuss block caches.
/// </para>
/// </remarks>
public sealed class LruBlockCache : IBlockCache
{
    /// <summary>Default shard count — matches RocksDB's BlockBasedTableOptions default.</summary>
    public const int DefaultShardCount = 16;

    private readonly Shard[] _shards;

    /// <summary>
    /// Creates a sharded LRU block cache with the given total byte budget and shard count.
    /// </summary>
    /// <param name="maxBytes">Total byte budget across all shards. Must be positive.</param>
    /// <param name="shardCount">Number of independent shards. Must be positive.
    /// Defaults to <see cref="DefaultShardCount"/>.</param>
    public LruBlockCache(long maxBytes, int shardCount = DefaultShardCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(shardCount);

        _shards = new Shard[shardCount];
        var perShardBudget = maxBytes / shardCount;
        var remainder = maxBytes - (perShardBudget * shardCount);
        for (var i = 0; i < shardCount; i++)
        {
            // Spill the remainder into the last shard so the cache exactly honours maxBytes.
            var budget = i == shardCount - 1 ? perShardBudget + remainder : perShardBudget;
            _shards[i] = new Shard(budget);
        }
    }

    /// <inheritdoc/>
    public bool TryGet(BlockCacheKey key, out byte[] payload) =>
        ShardFor(key).TryGet(key, out payload);

    /// <inheritdoc/>
    public void Put(BlockCacheKey key, byte[] payload, long costBytes)
    {
        ArgumentNullException.ThrowIfNull(payload);
        // Strictly positive: a zero-cost entry never contributes to _currentBytes, never
        // triggers eviction, and accumulates node + dictionary overhead without bound —
        // exactly the unbounded growth the byte budget exists to prevent.
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(costBytes);
        ShardFor(key).Put(key, payload, costBytes);
    }

    /// <inheritdoc/>
    public void Clear()
    {
        foreach (var shard in _shards)
        {
            shard.Clear();
        }
    }

    private Shard ShardFor(BlockCacheKey key)
    {
        // Fibonacci mixing constant decorrelates the SegmentId-dominant prefix of the cache
        // key so adjacent blocks within the same segment hash to different shards.
        const ulong fibMix = 0x9E3779B97F4A7C15UL;
        var hash = unchecked(((ulong)key.SegmentId * fibMix) ^ (ulong)key.BlockOffset);
        return _shards[(int)(hash % (uint)_shards.Length)];
    }

    private sealed class Shard(long maxBytes)
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<BlockCacheKey, LinkedListNode<Entry>> _index = [];
        private readonly LinkedList<Entry> _recency = new();
        private readonly long _maxBytes = maxBytes;
        private long _currentBytes;

        public bool TryGet(BlockCacheKey key, out byte[] payload)
        {
            lock (_gate)
            {
                if (_index.TryGetValue(key, out var node))
                {
                    _recency.Remove(node);
                    _recency.AddFirst(node);
                    payload = node.Value.Payload;
                    return true;
                }
            }

            payload = [];
            return false;
        }

        public void Put(BlockCacheKey key, byte[] payload, long costBytes)
        {
            lock (_gate)
            {
                if (_index.TryGetValue(key, out var existing))
                {
                    // Replace existing entry; recency moves to head, byte accounting adjusts.
                    _currentBytes -= existing.Value.CostBytes;
                    _recency.Remove(existing);
                    _index.Remove(key);
                }

                // If a single entry exceeds the per-shard budget, drop it on the floor —
                // caching a bigger-than-budget entry would wedge the shard at zero capacity
                // for every subsequent insert. Checked BEFORE the eviction loop: an entry
                // that can never be cached must not flush resident entries on its way out.
                if (costBytes > _maxBytes)
                {
                    return;
                }

                while (_currentBytes + costBytes > _maxBytes && _recency.Last is { } tail)
                {
                    _index.Remove(tail.Value.Key);
                    _recency.RemoveLast();
                    _currentBytes -= tail.Value.CostBytes;
                }

                var node = _recency.AddFirst(new Entry(key, payload, costBytes));
                _index[key] = node;
                _currentBytes += costBytes;
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _index.Clear();
                _recency.Clear();
                _currentBytes = 0;
            }
        }

        private readonly record struct Entry(BlockCacheKey Key, byte[] Payload, long CostBytes);
    }
}
