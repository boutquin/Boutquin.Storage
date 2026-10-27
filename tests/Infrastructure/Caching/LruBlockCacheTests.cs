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
using Boutquin.Storage.Infrastructure.Caching;

namespace Boutquin.Storage.Infrastructure.Tests.Caching;

/// <summary>
/// Unit tests for <see cref="LruBlockCache"/> — the sharded LRU implementation of
/// <see cref="IBlockCache"/>. Pins the cache contract (TryGet/Put/Clear semantics, eviction
/// order, sharded thread-safety) without touching SortedStringTable.
/// </summary>
public sealed class LruBlockCacheTests
{
    [Fact]
    public void TryGet_OnMissingKey_ReturnsFalse()
    {
        var cache = new LruBlockCache(maxBytes: 1024);
        var hit = cache.TryGet(new BlockCacheKey(1, 0), out var payload);

        hit.Should().BeFalse();
        payload.Should().BeEmpty();
    }

    [Fact]
    public void Put_ThenTryGet_RoundTripsThePayload()
    {
        var cache = new LruBlockCache(maxBytes: 1024);
        var key = new BlockCacheKey(1, 64);
        var payload = new byte[] { 1, 2, 3, 4 };

        cache.Put(key, payload, payload.Length);
        var hit = cache.TryGet(key, out var got);

        hit.Should().BeTrue();
        got.Should().BeSameAs(payload, "the cache stores the byte[] reference, not a copy");
    }

    [Fact]
    public void Put_Replacing_PromotesEntryAndUpdatesPayload()
    {
        var cache = new LruBlockCache(maxBytes: 1024);
        var key = new BlockCacheKey(1, 64);
        var first = new byte[] { 0xAA };
        var second = new byte[] { 0xBB, 0xCC };

        cache.Put(key, first, first.Length);
        cache.Put(key, second, second.Length);

        cache.TryGet(key, out var got).Should().BeTrue();
        got.Should().BeSameAs(second);
    }

    [Fact]
    public void EvictionEvictsLeastRecentlyUsedWhenBudgetExceeded()
    {
        // Single shard so the eviction order is fully deterministic across the test.
        var cache = new LruBlockCache(maxBytes: 32, shardCount: 1);
        var k1 = new BlockCacheKey(1, 1);
        var k2 = new BlockCacheKey(1, 2);
        var k3 = new BlockCacheKey(1, 3);

        cache.Put(k1, new byte[16], 16);
        cache.Put(k2, new byte[16], 16);
        // Touch k1 → k1 becomes MRU, k2 becomes LRU.
        cache.TryGet(k1, out _).Should().BeTrue();

        // Inserting k3 (16 bytes) requires evicting one entry. k2 is the LRU and goes.
        cache.Put(k3, new byte[16], 16);

        cache.TryGet(k1, out _).Should().BeTrue("k1 was the most recently used");
        cache.TryGet(k2, out _).Should().BeFalse("k2 was the LRU and must be evicted");
        cache.TryGet(k3, out _).Should().BeTrue("k3 was the freshly-inserted entry");
    }

    [Fact]
    public void OversizedEntry_IsDropped_NotCached()
    {
        var cache = new LruBlockCache(maxBytes: 32, shardCount: 1);
        var k = new BlockCacheKey(1, 1);
        // Bigger than the per-shard budget — caching it would wedge the shard at zero capacity.
        cache.Put(k, new byte[64], 64);

        cache.TryGet(k, out _).Should().BeFalse();
    }

    [Fact]
    public void OversizedEntry_IsDropped_WithoutEvictingExistingEntries()
    {
        // The oversize check must run BEFORE the eviction loop: an entry that can never be
        // cached must not flush the whole shard on its way to being dropped.
        var cache = new LruBlockCache(maxBytes: 32, shardCount: 1);
        var resident = new BlockCacheKey(1, 1);
        cache.Put(resident, new byte[8], 8);

        cache.Put(new BlockCacheKey(1, 2), new byte[64], 64); // oversized — dropped

        cache.TryGet(resident, out _).Should().BeTrue(
            "dropping an uncacheable oversized entry must not evict resident entries");
    }

    [Fact]
    public void Clear_EmptiesEveryShard()
    {
        var cache = new LruBlockCache(maxBytes: 1024);
        for (var i = 0; i < 16; i++)
        {
            cache.Put(new BlockCacheKey(1, i), new byte[8], 8);
        }

        cache.Clear();

        for (var i = 0; i < 16; i++)
        {
            cache.TryGet(new BlockCacheKey(1, i), out _).Should().BeFalse();
        }
    }

    [Fact]
    public void Ctor_RejectsNonPositiveBudgets()
    {
        var act = () => new LruBlockCache(maxBytes: 0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Ctor_RejectsNonPositiveShardCounts()
    {
        var act = () => new LruBlockCache(maxBytes: 1024, shardCount: 0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    /// <summary>
    /// A zero-cost entry never contributes to the byte budget, never triggers eviction, and
    /// therefore accumulates node + dictionary overhead without bound — an unbounded-memory
    /// hazard the budget exists to prevent. Cost must be strictly positive.
    /// </summary>
    [Fact]
    public void Put_RejectsZeroCost()
    {
        var cache = new LruBlockCache(maxBytes: 1024);
        var act = () => cache.Put(new BlockCacheKey(1, 0), [1, 2, 3], costBytes: 0);
        act.Should().Throw<ArgumentOutOfRangeException>(
            "zero-cost entries are invisible to the byte budget and can accumulate without bound");
    }
}
