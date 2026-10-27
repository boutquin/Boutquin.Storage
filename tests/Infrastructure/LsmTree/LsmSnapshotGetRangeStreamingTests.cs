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
/// P4.1 — LsmSnapshot.GetRangeAsync streaming k-way-merge correctness. Exercises
/// multi-segment scenarios, tombstone masking, tombstone-then-rewrite ordering, and
/// range boundary filtering. These tests guard that the streaming rewrite produces
/// identical results to the legacy collect-and-sort approach while also catching
/// the "allTombstones-set" mistake (seqno-ignorant tombstone post-filter) that would
/// incorrectly mask a key that was re-written after its tombstone.
/// </summary>
public sealed class LsmSnapshotGetRangeStreamingTests : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), $"SnapRange_{Guid.NewGuid():N}");

    public LsmSnapshotGetRangeStreamingTests()
    {
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            try { Directory.Delete(_folder, recursive: true); } catch { /* best-effort */ }
        }
    }

    private LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<int>> NewEngine(
        int memTableCapacity = 2)
        => LsmEngineFactory.Build<SerializableWrapper<int>, SerializableWrapper<int>>(
            memTableCapacity: memTableCapacity,
            segmentFolder: _folder,
            segmentPrefix: "snap");

    private static SerializableWrapper<int> K(int v) => new(v);
    private static SerializableWrapper<int> V(int v) => new(v);

    private static async Task<List<(int Key, int Value)>> RangeToListAsync(
        IAsyncEnumerable<KeyValuePair<SerializableWrapper<int>, SerializableWrapper<int>>> source)
    {
        var result = new List<(int, int)>();
        await foreach (var kv in source)
        {
            result.Add((kv.Key.Value, kv.Value.Value));
        }
        return result;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Multi-segment sorted output
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetRange_AcrossMultipleSegments_YieldsAscendingKeys()
    {
        // Arrange: write keys spread across two segments so the range scan must
        // merge multiple sorted sources.
        using var engine = NewEngine(memTableCapacity: 2);

        // Seg 0: keys 1, 2
        await engine.SetAsync(K(1), V(10));
        await engine.SetAsync(K(2), V(20));
        await engine.FlushAsync();

        // Seg 1: keys 3, 4
        await engine.SetAsync(K(3), V(30));
        await engine.SetAsync(K(4), V(40));
        await engine.FlushAsync();

        await using var snap = await engine.AcquireSnapshotAsync();

        var items = await RangeToListAsync(snap.GetRangeAsync(K(0), K(10)));

        items.Should().BeInAscendingOrder(x => x.Key);
        items.Should().HaveCount(4);
        items.Should().Equal((1, 10), (2, 20), (3, 30), (4, 40));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Tombstone masking
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetRange_SegmentTombstone_MasksOlderSegmentValue()
    {
        // Arrange: value in seg 0, then tombstone in seg 1 (before snapshot).
        // GetRange must NOT yield the key.
        using var engine = NewEngine(memTableCapacity: 2);

        await engine.SetAsync(K(1), V(111));
        await engine.SetAsync(K(2), V(222));
        await engine.FlushAsync();

        await engine.RemoveAsync(K(1));
        await engine.SetAsync(K(3), V(333)); // pad to fill memtable
        await engine.FlushAsync();

        await using var snap = await engine.AcquireSnapshotAsync();

        var items = await RangeToListAsync(snap.GetRangeAsync(K(0), K(10)));

        items.Select(x => x.Key).Should().NotContain(1,
            "tombstone in newer segment must mask the value in older segment");
        items.Should().Contain((2, 222));
        items.Should().Contain((3, 333));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Tombstone then re-write — the critical P4.1 correctness guard
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetRange_TombstoneThenRewrite_YieldsRewrittenValue()
    {
        // Arrange:
        //   seg 0: value(key=5, seqno=1)
        //   seg 1: tombstone(key=5)          ← lower-indexed = older than seg 2
        //   seg 2: value(key=5, seqno=N)     ← newest, supersedes tombstone
        //
        // A naive "allTombstones HashSet" post-filter would discard key=5 because it
        // appears in _segmentTombstones[1]. The seqno-aware winner algorithm must yield
        // the re-written value instead.
        using var engine = NewEngine(memTableCapacity: 2);

        // seg 0
        await engine.SetAsync(K(5), V(100));
        await engine.SetAsync(K(6), V(200)); // filler
        await engine.FlushAsync();

        // seg 1: tombstone for key=5
        await engine.RemoveAsync(K(5));
        await engine.SetAsync(K(7), V(300)); // filler to flush
        await engine.FlushAsync();

        // seg 2: re-write key=5 with a new value
        await engine.SetAsync(K(5), V(999));
        await engine.SetAsync(K(8), V(400)); // filler to flush
        await engine.FlushAsync();

        await using var snap = await engine.AcquireSnapshotAsync();

        var items = await RangeToListAsync(snap.GetRangeAsync(K(0), K(20)));

        items.Should().Contain((5, 999),
            "re-write at seqno > tombstone seqno must win — key must be visible with new value");
        items.Select(x => x.Key).Should().BeInAscendingOrder();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Range boundary filter
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetRange_BoundaryFilter_ExcludesKeysOutsideRange()
    {
        using var engine = NewEngine(memTableCapacity: 2);

        await engine.SetAsync(K(1), V(10));
        await engine.SetAsync(K(2), V(20));
        await engine.FlushAsync();
        await engine.SetAsync(K(3), V(30));
        await engine.SetAsync(K(4), V(40));
        await engine.FlushAsync();

        await using var snap = await engine.AcquireSnapshotAsync();

        // Inclusive [2, 3]
        var items = await RangeToListAsync(snap.GetRangeAsync(K(2), K(3)));

        items.Should().HaveCount(2);
        items.Should().Equal((2, 20), (3, 30));
        items.Select(x => x.Key).Should().NotContain(1);
        items.Select(x => x.Key).Should().NotContain(4);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Snapshot seqno filter — post-acquisition writes must be invisible
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetRange_PostAcquisitionFlush_IsInvisible()
    {
        using var engine = NewEngine(memTableCapacity: 2);

        await engine.SetAsync(K(1), V(10));
        await engine.SetAsync(K(2), V(20));
        await engine.FlushAsync();

        await using var snap = await engine.AcquireSnapshotAsync();

        // Write and flush after snapshot — must not appear in range scan
        await engine.SetAsync(K(3), V(30));
        await engine.SetAsync(K(4), V(40));
        await engine.FlushAsync();

        var items = await RangeToListAsync(snap.GetRangeAsync(K(0), K(10)));

        items.Should().HaveCount(2, "post-acquisition flush must not bleed into snapshot range");
        items.Should().Equal((1, 10), (2, 20));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Overwrite in newer segment supersedes older value
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetRange_OverwriteInNewerSegment_YieldsNewerValue()
    {
        using var engine = NewEngine(memTableCapacity: 2);

        // seg 0
        await engine.SetAsync(K(1), V(10));
        await engine.SetAsync(K(2), V(20));
        await engine.FlushAsync();

        // seg 1: overwrite key=1
        await engine.SetAsync(K(1), V(11));
        await engine.SetAsync(K(2), V(21));
        await engine.FlushAsync();

        await using var snap = await engine.AcquireSnapshotAsync();

        var items = await RangeToListAsync(snap.GetRangeAsync(K(0), K(10)));

        items.Should().Equal([(1, 11), (2, 21)],
            "newer-segment overwrite must shadow the older segment value");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Empty range returns nothing
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetRange_EmptyRange_YieldsNothing()
    {
        using var engine = NewEngine(memTableCapacity: 4);

        await engine.SetAsync(K(1), V(10));
        await engine.SetAsync(K(2), V(20));
        await engine.FlushAsync();

        await using var snap = await engine.AcquireSnapshotAsync();

        // Request range that doesn't overlap with stored keys
        var items = await RangeToListAsync(snap.GetRangeAsync(K(100), K(200)));

        items.Should().BeEmpty();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // MemTable-resident data + tombstone interleaving (BuildMemTableSourceAsync)
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetRange_MemTableData_NoSegments_YieldsLiveValues()
    {
        // Snapshot before any flush — all entries live in the memtable.
        using var engine = NewEngine(memTableCapacity: 8);
        await engine.SetAsync(K(1), V(10));
        await engine.SetAsync(K(2), V(20));
        await engine.SetAsync(K(3), V(30));

        await using var snap = await engine.AcquireSnapshotAsync();
        var items = await RangeToListAsync(snap.GetRangeAsync(K(0), K(10)));
        items.Should().Equal((1, 10), (2, 20), (3, 30));
    }

    [Fact]
    public async Task GetRange_MemTableTombstoneOnly_NoData_YieldsNothing()
    {
        // Memtable holds a tombstone but no data with that key — exercises the
        // "tombstones only, no data" branch in BuildMemTableSourceAsync.
        using var engine = NewEngine(memTableCapacity: 8);
        await engine.SetAsync(K(1), V(10));
        await engine.FlushAsync();
        // Tombstone goes into the memtable (post-flush state).
        await engine.RemoveAsync(K(1));

        await using var snap = await engine.AcquireSnapshotAsync();
        var items = await RangeToListAsync(snap.GetRangeAsync(K(0), K(10)));
        items.Select(x => x.Key).Should().NotContain(1,
            "memtable tombstone with no live data must mask the older flushed value");
    }

    [Fact]
    public async Task GetRange_MemTableDataAndTombstoneInterleaved_BothYielded()
    {
        // Memtable holds data for some keys and tombstones for others, all in the
        // requested range — exercises the (hasD && hasT) interleaving branch.
        using var engine = NewEngine(memTableCapacity: 8);
        // Seed segment with old values so the tombstones have something to mask.
        await engine.SetAsync(K(1), V(10));
        await engine.SetAsync(K(2), V(20));
        await engine.SetAsync(K(3), V(30));
        await engine.SetAsync(K(4), V(40));
        await engine.FlushAsync();

        // Memtable now: tombstones for 1, 3 (delete) + new data for 5
        await engine.RemoveAsync(K(1));
        await engine.RemoveAsync(K(3));
        await engine.SetAsync(K(5), V(50));

        await using var snap = await engine.AcquireSnapshotAsync();
        var items = await RangeToListAsync(snap.GetRangeAsync(K(0), K(10)));
        items.Select(x => x.Key).Should().NotContain(1);
        items.Select(x => x.Key).Should().NotContain(3);
        items.Should().Contain((2, 20));
        items.Should().Contain((4, 40));
        items.Should().Contain((5, 50));
    }

    [Fact]
    public async Task GetRange_MemTableTombstoneAfterFlushedSegment_MasksOlderEntry()
    {
        // Specifically tests the "memtable tombstone only" yield in BuildMemTableSourceAsync
        // (line 249) AND the segment-data-then-memtable-tombstone interleaving in the outer
        // k-way merge across the memtable + segment sources.
        using var engine = NewEngine(memTableCapacity: 8);
        await engine.SetAsync(K(2), V(20));
        await engine.SetAsync(K(4), V(40));
        await engine.FlushAsync();

        // Memtable: only tombstones — no data.
        await engine.RemoveAsync(K(2));

        await using var snap = await engine.AcquireSnapshotAsync();
        var items = await RangeToListAsync(snap.GetRangeAsync(K(0), K(10)));
        items.Select(x => x.Key).Should().NotContain(2);
        items.Should().Contain((4, 40));
    }

    [Fact]
    public async Task GetRange_LargeRange_OrdersAcrossMemTableAndSegments()
    {
        // Three sources: two segments + one memtable. Tests the 3-way merge of the
        // outer KWayMerge across the memtable + per-segment streams.
        using var engine = NewEngine(memTableCapacity: 8);
        await engine.SetAsync(K(1), V(10));
        await engine.SetAsync(K(4), V(40));
        await engine.FlushAsync();

        await engine.SetAsync(K(2), V(20));
        await engine.SetAsync(K(5), V(50));
        await engine.FlushAsync();

        await engine.SetAsync(K(3), V(30));
        await engine.SetAsync(K(6), V(60));
        // memtable holds 3, 6

        await using var snap = await engine.AcquireSnapshotAsync();
        var items = await RangeToListAsync(snap.GetRangeAsync(K(0), K(10)));
        items.Should().Equal((1, 10), (2, 20), (3, 30), (4, 40), (5, 50), (6, 60));
    }

    [Fact]
    public async Task GetRange_AfterDispose_ThrowsObjectDisposed()
    {
        using var engine = NewEngine(memTableCapacity: 8);
        await engine.SetAsync(K(1), V(10));
        await engine.FlushAsync();

        var snap = await engine.AcquireSnapshotAsync();
        await snap.DisposeAsync();

        Func<Task> act = async () =>
        {
            await foreach (var _ in snap.GetRangeAsync(K(0), K(10)))
            {
                break;
            }
        };
        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task TryGetValueAsync_AfterDispose_ThrowsObjectDisposed()
    {
        using var engine = NewEngine(memTableCapacity: 8);
        await engine.SetAsync(K(1), V(10));
        await engine.FlushAsync();

        var snap = await engine.AcquireSnapshotAsync();
        await snap.DisposeAsync();

        Func<Task> act = async () => await snap.TryGetValueAsync(K(1));
        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent()
    {
        using var engine = NewEngine(memTableCapacity: 8);
        await engine.SetAsync(K(1), V(10));
        await engine.FlushAsync();

        var snap = await engine.AcquireSnapshotAsync();
        await snap.DisposeAsync();
        Func<Task> act = async () => await snap.DisposeAsync();
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task TryGetValueAsync_TombstoneOnlyInSegment_ReturnsNotFound()
    {
        // Segment has a tombstone but no data for the key — exercises the
        // (hasTombstone, !dataFound) branch in TryGetValueAsync (line 134-137).
        using var engine = NewEngine(memTableCapacity: 8);
        await engine.SetAsync(K(1), V(10));
        await engine.FlushAsync();
        await engine.RemoveAsync(K(1));
        await engine.FlushAsync();

        await using var snap = await engine.AcquireSnapshotAsync();
        var (_, found) = await snap.TryGetValueAsync(K(1));
        found.Should().BeFalse();
    }

    [Fact]
    public async Task TryGetValueAsync_DataOnly_ReturnsValue()
    {
        // Snapshot path with data only, no tombstones — exercises the
        // (!hasTombstone, dataFound) branch (line 139).
        using var engine = NewEngine(memTableCapacity: 8);
        await engine.SetAsync(K(7), V(77));
        await engine.FlushAsync();

        await using var snap = await engine.AcquireSnapshotAsync();
        var (val, found) = await snap.TryGetValueAsync(K(7));
        found.Should().BeTrue();
        val.Value.Should().Be(77);
    }

    // ── B1: null and inverted-range guards ────────────────────────────────────

    [Fact]
    public async Task GetRangeAsync_NullStartKey_ThrowsArgumentException()
    {
        using var engine = NewEngine();
        await engine.SetAsync(K(1), V(1));
        await using var snap = await engine.AcquireSnapshotAsync();

        var act = async () =>
            await snap.GetRangeAsync(null!, K(5)).ToListAsync();
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task GetRangeAsync_NullEndKey_ThrowsArgumentException()
    {
        using var engine = NewEngine();
        await engine.SetAsync(K(1), V(1));
        await using var snap = await engine.AcquireSnapshotAsync();

        var act = async () =>
            await snap.GetRangeAsync(K(1), null!).ToListAsync();
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task GetRangeAsync_InvertedRange_ThrowsArgumentException()
    {
        using var engine = NewEngine();
        await engine.SetAsync(K(1), V(1));
        await using var snap = await engine.AcquireSnapshotAsync();

        var act = async () =>
            await snap.GetRangeAsync(K(5), K(1)).ToListAsync();
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*startKey*");
    }

    // ── B7: end-bound early-stop ──────────────────────────────────────────────

    [Fact]
    public async Task GetRangeAsync_ReturnsOnlyKeysWithinRange()
    {
        using var engine = NewEngine(memTableCapacity: 10);
        for (var i = 1; i <= 9; i++)
        {
            await engine.SetAsync(K(i), V(i * 10));
        }

        await using var snap = await engine.AcquireSnapshotAsync();
        var results = await RangeToListAsync(snap.GetRangeAsync(K(3), K(6)));
        results.Should().Equal([(3, 30), (4, 40), (5, 50), (6, 60)]);
    }
}
