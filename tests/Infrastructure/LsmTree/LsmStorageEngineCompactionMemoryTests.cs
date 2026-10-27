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
namespace Boutquin.Storage.Infrastructure.Tests.LsmTree;

/// <summary>
/// Phase B1/B5 — behavioural tests for the streaming k-way merge path through
/// <see cref="LsmStorageEngine{TKey,TValue}.CompactAsync"/> and
/// <see cref="LsmStorageEngine{TKey,TValue}.GetRangeAsync"/>. Asserts the invariants DDIA 2e
/// p. 120 makes about compaction correctness (sorted output, last-writer-wins, tombstone strip).
/// </summary>
public sealed class LsmStorageEngineCompactionMemoryTests : IDisposable
{
    private readonly string _folder;

    public LsmStorageEngineCompactionMemoryTests()
    {
        _folder = Path.Combine(
            Path.GetTempPath(),
            $"bsstreamcompact_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            try { Directory.Delete(_folder, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task Compaction_ManySegments_ProducesSortedLastWriterWinsSingleSegment()
    {
        // Arrange: capacity=2 means each SetAsync pair flushes one segment. Flush 10 segments;
        // some keys reappear across segments (newer value must win).
        using var engine = LsmEngineFactory.Build<SerializableWrapper<int>, SerializableWrapper<int>>(memTableCapacity: 2,
            segmentFolder: _folder,
            segmentPrefix: "seg");

        var expected = new Dictionary<int, int>();
        var rng = new Random(17);
        for (var i = 0; i < 20; i++)
        {
            var key = rng.Next(0, 30);
            var value = i * 1000;
            expected[key] = value;
            await engine.SetAsync(new SerializableWrapper<int>(key), new SerializableWrapper<int>(value));
        }
        await engine.FlushAsync();

        // Act — compact all segments into one.
        await engine.CompactAsync();

        // Assert — single segment, sorted, newest value wins per key.
        engine.SegmentCount.Should().Be(1);
        var items = (await engine.GetAllItemsAsync()).ToList();
        items.Select(kv => kv.Key.Value).Should().BeInAscendingOrder();
        foreach (var (k, v) in expected)
        {
            items.Should().Contain(kv => kv.Key.Value == k && kv.Value.Value == v);
        }
        items.Should().HaveCount(expected.Count);
    }

    [Fact]
    public async Task Compaction_WithTombstones_StripsDeletedKeys()
    {
        using var engine = LsmEngineFactory.Build<SerializableWrapper<int>, SerializableWrapper<int>>(memTableCapacity: 2,
            segmentFolder: _folder,
            segmentPrefix: "seg");

        // Populate three segments.
        for (var i = 0; i < 6; i++)
        {
            await engine.SetAsync(new SerializableWrapper<int>(i), new SerializableWrapper<int>(i * 10));
        }
        await engine.FlushAsync();

        // Tombstone three keys across the memtable.
        await engine.RemoveAsync(new SerializableWrapper<int>(1));
        await engine.RemoveAsync(new SerializableWrapper<int>(3));
        await engine.RemoveAsync(new SerializableWrapper<int>(5));
        await engine.FlushAsync();

        await engine.CompactAsync();

        var items = (await engine.GetAllItemsAsync()).ToList();
        items.Select(kv => kv.Key.Value).Should().BeEquivalentTo([0, 2, 4]);
    }

    [Fact]
    public async Task GetRangeAsync_LargeDataset_YieldsOnlyInRangeKeys()
    {
        using var engine = LsmEngineFactory.Build<SerializableWrapper<int>, SerializableWrapper<int>>(memTableCapacity: 8,
            segmentFolder: _folder,
            segmentPrefix: "seg");

        // 200 keys, stored across many segments.
        for (var i = 0; i < 200; i++)
        {
            await engine.SetAsync(new SerializableWrapper<int>(i), new SerializableWrapper<int>(i));
        }
        await engine.FlushAsync();

        var range = new List<(SerializableWrapper<int> Key, SerializableWrapper<int> Value)>();
        await foreach (var item in engine.GetRangeAsync(
            new SerializableWrapper<int>(50),
            new SerializableWrapper<int>(60)))
        {
            range.Add(item);
        }

        range.Select(kv => kv.Key.Value).Should().Equal(
            [50, 51, 52, 53, 54, 55, 56, 57, 58, 59, 60],
            because: "GetRangeAsync must return only keys in [50,60] inclusive, sorted ascending.");
    }

    [Fact]
    public async Task Compaction_StreamingPipeline_PreservesNewerValueAcrossSegments()
    {
        using var engine = LsmEngineFactory.Build<SerializableWrapper<int>, SerializableWrapper<int>>(memTableCapacity: 2,
            segmentFolder: _folder,
            segmentPrefix: "seg");

        // Write key 7 in segment 0, overwrite in segment 1, overwrite again in segment 2.
        await engine.SetAsync(new SerializableWrapper<int>(7), new SerializableWrapper<int>(100));
        await engine.SetAsync(new SerializableWrapper<int>(99), new SerializableWrapper<int>(99));
        await engine.FlushAsync();

        await engine.SetAsync(new SerializableWrapper<int>(7), new SerializableWrapper<int>(200));
        await engine.SetAsync(new SerializableWrapper<int>(8), new SerializableWrapper<int>(8));
        await engine.FlushAsync();

        await engine.SetAsync(new SerializableWrapper<int>(7), new SerializableWrapper<int>(300));
        await engine.SetAsync(new SerializableWrapper<int>(9), new SerializableWrapper<int>(9));
        await engine.FlushAsync();

        engine.SegmentCount.Should().Be(3);
        await engine.CompactAsync();

        engine.SegmentCount.Should().Be(1);
        var (val, found) = await engine.TryGetValueAsync(new SerializableWrapper<int>(7));
        found.Should().BeTrue();
        val.Value.Should().Be(300, "last-writer-wins must pick the newest segment's value.");
    }
}
