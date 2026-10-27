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
using Boutquin.Storage.Domain.ValueObjects.LsmTree;
using Boutquin.Storage.Infrastructure.Caching;

namespace Boutquin.Storage.Infrastructure.Tests.Caching;

/// <summary>
/// Verifies the <see cref="IBlockCache"/> ctor seam is observable end-to-end:
/// SortedStringTable point lookups hit the cache on the second read, the
/// LsmStorageEngine surfaces the configured cache via its <c>BlockCache</c>
/// property, and the SST format threads a per-instance segment-id into BlockCacheKey so
/// blocks from different files do not collide.
/// </summary>
public sealed class BlockCacheSeamTests : IDisposable
{
    private readonly string _testDirectory = Path.Combine(
        Path.GetTempPath(), $"BlockCacheSeam_{Guid.NewGuid():N}");

    public BlockCacheSeamTests() => Directory.CreateDirectory(_testDirectory);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDirectory))
            {
                Directory.Delete(_testDirectory, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    [Fact]
    public void SortedStringTable_PointLookup_PopulatesCacheOnFirstRead_HitsCacheOnSecond()
    {
        var cache = new CountingBlockCache(new LruBlockCache(maxBytes: 64 * 1024));
        var sst = new global::Boutquin.Storage.Infrastructure.SortedStringTable.SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _testDirectory,
            "seg_000001.sst",
            blockCache: cache);

        var entries = Enumerable.Range(0, 64)
            .Select(i => SegmentWriteEntry<SerializableWrapper<int>, SerializableWrapper<string>>.Inline(
                new SerializableWrapper<int>(i),
                new SerializableWrapper<string>($"v_{i}"),
                (ulong)(i + 1)))
            .ToList();
        sst.Write(entries);

        // First lookup — cache miss; the SST reads + decompresses the block, then puts it.
        sst.TryGetBlockEntry(new SerializableWrapper<int>(5), out _).Should().BeTrue();
        var hitsAfterFirst = cache.HitCount;
        var putsAfterFirst = cache.PutCount;

        // Second lookup of a key in the same block — must be a cache hit; no extra Put.
        sst.TryGetBlockEntry(new SerializableWrapper<int>(7), out _).Should().BeTrue();

        cache.HitCount.Should().BeGreaterThan(hitsAfterFirst, "the second lookup of a key in the cached block must hit the cache");
        cache.PutCount.Should().Be(putsAfterFirst, "no second put — the block was already cached");
    }

    [Fact]
    public void SortedStringTable_DifferentSegmentIds_DoNotCollideInCache()
    {
        var cache = new LruBlockCache(maxBytes: 64 * 1024);

        var sstA = new global::Boutquin.Storage.Infrastructure.SortedStringTable.SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _testDirectory, "seg_000001.sst", blockCache: cache);
        var sstB = new global::Boutquin.Storage.Infrastructure.SortedStringTable.SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _testDirectory, "seg_000002.sst", blockCache: cache);

        var entriesA = new[]
        {
            SegmentWriteEntry<SerializableWrapper<int>, SerializableWrapper<string>>.Inline(
                new SerializableWrapper<int>(1), new SerializableWrapper<string>("from-A"), 1UL),
        };
        var entriesB = new[]
        {
            SegmentWriteEntry<SerializableWrapper<int>, SerializableWrapper<string>>.Inline(
                new SerializableWrapper<int>(1), new SerializableWrapper<string>("from-B"), 1UL),
        };
        sstA.Write(entriesA);
        sstB.Write(entriesB);

        sstA.TryGetBlockEntry(new SerializableWrapper<int>(1), out var entryA).Should().BeTrue();
        sstB.TryGetBlockEntry(new SerializableWrapper<int>(1), out var entryB).Should().BeTrue();

        var rawA = entryA.Value.ToArray();
        var rawB = entryB.Value.ToArray();
        rawA.Should().NotEqual(rawB,
            "blocks from different segments must not collide — segmentId is part of the cache key");
    }

    [Fact]
    public void SortedStringTable_NullCache_PointLookupsStillSucceed()
    {
        // Null cache = no caching, the seam degrades cleanly. This is the default and the
        // "preserves today's behaviour" guarantee.
        var sst = new global::Boutquin.Storage.Infrastructure.SortedStringTable.SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _testDirectory, "seg_000001.sst", blockCache: null);

        var entries = new[]
        {
            SegmentWriteEntry<SerializableWrapper<int>, SerializableWrapper<string>>.Inline(
                new SerializableWrapper<int>(1), new SerializableWrapper<string>("v"), 1UL),
        };
        sst.Write(entries);

        sst.TryGetBlockEntry(new SerializableWrapper<int>(1), out _).Should().BeTrue();
    }

    [Fact]
    public void LsmStorageEngine_BlockCacheProperty_SurfacesTheConfiguredCache()
    {
        var cache = new LruBlockCache(maxBytes: 64 * 1024);
        var options = new LsmStorageEngineOptions<SerializableWrapper<int>, SerializableWrapper<string>>(
            MemTableCapacity: 16,
            SegmentFolder: _testDirectory,
            SegmentPrefix: "engine",
            SegmentFormat: new SstSegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>>(blockCache: cache),
            BlockCache: cache);

        using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(options);

        engine.BlockCache.Should().BeSameAs(cache,
            "the engine surfaces the configured cache via its public property so tests can verify the seam end-to-end");
    }

    [Fact]
    public void LsmStorageEngine_NullCache_BlockCacheIsNull()
    {
        var options = new LsmStorageEngineOptions<SerializableWrapper<int>, SerializableWrapper<string>>(
            MemTableCapacity: 16,
            SegmentFolder: _testDirectory,
            SegmentPrefix: "engine",
            SegmentFormat: new SstSegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>>());

        using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(options);

        engine.BlockCache.Should().BeNull("default null = no caching");
    }

    /// <summary>
    /// Test wrapper that delegates to an inner <see cref="IBlockCache"/> while counting calls.
    /// Used to assert hit/miss/put cadence without re-implementing eviction behaviour.
    /// </summary>
    private sealed class CountingBlockCache(IBlockCache inner) : IBlockCache
    {
        private readonly IBlockCache _inner = inner;
        public int HitCount;
        public int MissCount;
        public int PutCount;

        public bool TryGet(BlockCacheKey key, out byte[] payload)
        {
            var hit = _inner.TryGet(key, out payload);
            if (hit)
            {
                Interlocked.Increment(ref HitCount);
            }
            else
            {
                Interlocked.Increment(ref MissCount);
            }
            return hit;
        }

        public void Put(BlockCacheKey key, byte[] payload, long costBytes)
        {
            Interlocked.Increment(ref PutCount);
            _inner.Put(key, payload, costBytes);
        }

        public void Clear() => _inner.Clear();
    }
}
