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
#nullable disable

namespace Boutquin.Storage.Infrastructure.Tests.LsmTree;

/// <summary>
/// Code Arena 2026-06-29-0740-fd0ea1, finding 0fe6c49297de.
/// Correctness tests for the SSTable block-seek optimisation in <c>GetRangeAsync</c>.
/// The output must be byte-identical to the pre-optimisation path (same keys, same order,
/// same newest-wins dedup, same tombstone semantics, same [startKey, endKey) bound).
/// The RED test references <c>ISstSegmentSeekable.StreamBlockEntriesFromAsync</c> which
/// does not exist yet — it is a compile-time RED.
/// </summary>
public sealed class GetRangeSeekToStartKeyTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), $"RangeSeek_{Guid.NewGuid():N}");

    public GetRangeSeekToStartKeyTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            try { Directory.Delete(_dir, recursive: true); }
            catch (IOException) { /* best-effort cleanup */ }
        }
    }

    private static SerializableWrapper<int> K(int i) => new(i);
    private static SerializableWrapper<string> V(string s) => new(s);

    // ---------------------------------------------------------------------------
    // RED: compile-time reference to the new seek entry point that does not exist yet.
    // Once ISstSegmentSeekable<TKey,TValue>.StreamBlockEntriesFromAsync is added to
    // SstSegmentFormat.SstSegmentReader this test becomes GREEN.
    // ---------------------------------------------------------------------------

    [Fact]
    public void SstSegmentReader_ImplementsISstSegmentSeekable()
    {
        // Arrange: build an SST segment via the format
        var sst = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _dir, "seek_check.sst");
        var entries = Enumerable.Range(0, 5)
            .Select(i => SegmentWriteEntry<SerializableWrapper<int>, SerializableWrapper<string>>
                .Inline(K(i), V($"v{i}"), (ulong)(i + 1)))
            .ToList();
        sst.Write(entries);

        var format = new SstSegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>>();
        var reader = format.Open(_dir, "seek_check.sst");

        // Assert: the reader implements the new seek interface (compile-time RED)
        reader.Should().BeAssignableTo<ISstSegmentSeekable<SerializableWrapper<int>, SerializableWrapper<string>>>(
            "SstSegmentReader must implement ISstSegmentSeekable to enable block-seek in GetRangeAsync");
    }

    // ---------------------------------------------------------------------------
    // Correctness tests: these run after the fix and validate observable behaviour.
    // They assert only on GetRangeAsync output — not on internal block indices.
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Basic range over a single flushed SSTable: result is exactly [startKey, endKey] inclusive.
    /// </summary>
    [Fact]
    public async Task GetRangeAsync_SingleSegment_ReturnsExactRange()
    {
        using var engine = LsmEngineFactory.Build<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 1000, segmentFolder: _dir, segmentPrefix: "seg");

        for (var i = 0; i < 50; i++)
        {
            await engine.SetAsync(K(i), V($"v{i}"));
        }
        await engine.FlushAsync();

        var result = new List<(SerializableWrapper<int> Key, SerializableWrapper<string> Value)>();
        await foreach (var kv in engine.GetRangeAsync(K(20), K(30)))
        {
            result.Add(kv);
        }

        result.Should().HaveCount(11, "keys 20..30 inclusive = 11 entries");
        result.Select(x => x.Key.Value).Should().BeInAscendingOrder();
        result.First().Key.Value.Should().Be(20);
        result.Last().Key.Value.Should().Be(30);
    }

    /// <summary>
    /// Range query spanning memtable + flushed SSTable — newest-wins dedup and tombstone
    /// semantics must be preserved after the block-seek optimisation.
    /// </summary>
    [Fact]
    public async Task GetRangeAsync_MemtablePlusSegment_NewestWinsAndTombstone()
    {
        using var engine = LsmEngineFactory.Build<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 1000, segmentFolder: _dir, segmentPrefix: "seg");

        // Segment 1: keys 0..49, all values "old"
        for (var i = 0; i < 50; i++)
        {
            await engine.SetAsync(K(i), V("old"));
        }
        await engine.FlushAsync();

        // Overwrite key 25 in the active memtable (newest-wins)
        await engine.SetAsync(K(25), V("new"));

        // Tombstone key 30 in the active memtable
        await engine.RemoveAsync(K(30));

        var result = new List<(SerializableWrapper<int> Key, SerializableWrapper<string> Value)>();
        await foreach (var kv in engine.GetRangeAsync(K(20), K(35)))
        {
            result.Add(kv);
        }

        // Keys 20..35 minus tombstoned 30 = 15 entries
        result.Should().HaveCount(15, "keys 20..35 minus tombstone at 30 = 15");
        result.Select(x => x.Key.Value).Should().BeInAscendingOrder();
        result.Should().NotContain(x => x.Key.Value == 30, "key 30 is tombstoned");

        var hit25 = result.Single(x => x.Key.Value == 25);
        hit25.Value.Value.Should().Be("new", "memtable overwrite beats segment value");
    }

    /// <summary>
    /// High startKey forces the block-seek to skip most of the SSTable.
    /// Verifiable because: no prior-range keys must appear in the result.
    /// Uses enough data to guarantee a genuine multi-block SSTable (block size = 16 KiB;
    /// 2000 entries of ~50 bytes each ≈ 100 KiB ≫ 16 KiB → several data blocks).
    /// </summary>
    [Fact]
    public async Task GetRangeAsync_HighStartKey_SkipsEarlyContent()
    {
        // Use a small block size to force many blocks from moderate key count.
        var format = new SstSegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>>(
            blockSizeBytes: 512);
        using var engine = LsmEngineFactory.Build<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 5000, segmentFolder: _dir, segmentPrefix: "seg",
            segmentFormat: format);

        // Write 2000 keys so data spills across many 512-byte blocks.
        for (var i = 0; i < 2000; i++)
        {
            await engine.SetAsync(K(i), V($"value_{i:D6}"));
        }
        await engine.FlushAsync();

        // Query the final 10 keys (1990..1999).
        var result = new List<(SerializableWrapper<int> Key, SerializableWrapper<string> Value)>();
        await foreach (var kv in engine.GetRangeAsync(K(1990), K(1999)))
        {
            result.Add(kv);
        }

        result.Should().HaveCount(10);
        result.Select(x => x.Key.Value).Should().BeInAscendingOrder();
        result.First().Key.Value.Should().Be(1990);
        result.Last().Key.Value.Should().Be(1999);
        foreach (var (k, v) in result)
        {
            v.Value.Should().Be($"value_{k.Value:D6}");
        }
    }

    /// <summary>
    /// Multiple segments, startKey lands in the middle segment — result must contain
    /// exactly the keys from all segments that fall in [startKey, endKey].
    /// </summary>
    [Fact]
    public async Task GetRangeAsync_MultipleSegments_CorrectMerge()
    {
        using var engine = LsmEngineFactory.Build<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 1000, segmentFolder: _dir, segmentPrefix: "seg");

        // Segment A: 0..99
        for (var i = 0; i < 100; i++)
        {
            await engine.SetAsync(K(i), V($"A{i}"));
        }
        await engine.FlushAsync();

        // Segment B: 200..299
        for (var i = 200; i < 300; i++)
        {
            await engine.SetAsync(K(i), V($"B{i}"));
        }
        await engine.FlushAsync();

        // Segment C: 400..499
        for (var i = 400; i < 500; i++)
        {
            await engine.SetAsync(K(i), V($"C{i}"));
        }
        await engine.FlushAsync();

        engine.SegmentCount.Should().BeGreaterThanOrEqualTo(3);

        // Query range [50, 450] — spans parts of A, all of B, part of C
        var result = new List<(SerializableWrapper<int> Key, SerializableWrapper<string> Value)>();
        await foreach (var kv in engine.GetRangeAsync(K(50), K(450)))
        {
            result.Add(kv);
        }

        // Expected: A[50..99] (50) + B[200..299] (100) + C[400..450] (51) = 201
        result.Should().HaveCount(201);
        result.Select(x => x.Key.Value).Should().BeInAscendingOrder();
        result.First().Key.Value.Should().Be(50);
        result.Last().Key.Value.Should().Be(450);
    }

    /// <summary>
    /// Overwrites + tombstones + memtable entries in a high-key range — the block-seek
    /// must not disturb the newest-wins dedup or tombstone suppression.
    /// </summary>
    [Fact]
    public async Task GetRangeAsync_OverwritesAndTombstones_HighKeyRange_CorrectSemantics()
    {
        var format = new SstSegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>>(
            blockSizeBytes: 512);
        using var engine = LsmEngineFactory.Build<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 5000, segmentFolder: _dir, segmentPrefix: "seg",
            segmentFormat: format);

        // First segment: keys 0..999, value "v1"
        for (var i = 0; i < 1000; i++)
        {
            await engine.SetAsync(K(i), V("v1"));
        }
        await engine.FlushAsync();

        // Second segment: overwrite keys 800..899, value "v2"
        for (var i = 800; i < 900; i++)
        {
            await engine.SetAsync(K(i), V("v2"));
        }
        await engine.FlushAsync();

        // Memtable: tombstone key 850, overwrite key 900 with "mem"
        await engine.RemoveAsync(K(850));
        await engine.SetAsync(K(900), V("mem"));

        // Query [820, 910]
        var result = new List<(SerializableWrapper<int> Key, SerializableWrapper<string> Value)>();
        await foreach (var kv in engine.GetRangeAsync(K(820), K(910)))
        {
            result.Add(kv);
        }

        // 820..910 = 91 keys; minus tombstone at 850 = 90
        result.Should().HaveCount(90, "91 keys in [820,910] minus tombstone at 850 = 90");
        result.Select(x => x.Key.Value).Should().BeInAscendingOrder();
        result.Should().NotContain(x => x.Key.Value == 850, "key 850 is tombstoned");

        // Keys 820..849 and 851..899 should have "v2" (from second segment)
        foreach (var (k, v) in result.Where(x => x.Key.Value >= 820 && x.Key.Value <= 899 && x.Key.Value != 850))
        {
            v.Value.Should().Be("v2", $"key {k.Value} was overwritten with v2 in the second segment");
        }

        // Key 900 from memtable wins
        var hit900 = result.Single(x => x.Key.Value == 900);
        hit900.Value.Value.Should().Be("mem", "key 900 was written to memtable last");
    }
}
