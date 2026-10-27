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
/// feature-lsm-snapshot-isolation — behavioural tests for the consumer-facing snapshot
/// API on <see cref="LsmStorageEngine{TKey,TValue}"/>. Asserts the MVCC visibility rule
/// (DDIA 2e Ch. 7 pp. 237–242), the segment-pinning contract under compaction, and the
/// seqno-reachable tombstone GC rule.
/// </summary>
public sealed class SnapshotIsolationTests : IDisposable
{
    private readonly string _folder;

    public SnapshotIsolationTests()
    {
        _folder = Path.Combine(Path.GetTempPath(), $"bssnap_{Guid.NewGuid():N}");
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
        int memTableCapacity = 16)
        => LsmEngineFactory.Build<SerializableWrapper<int>, SerializableWrapper<int>>(
            memTableCapacity: memTableCapacity,
            segmentFolder: _folder,
            segmentPrefix: "snap");

    private static SerializableWrapper<int> K(int v) => new(v);
    private static SerializableWrapper<int> V(int v) => new(v);

    // ---- Acquisition ----

    [Fact]
    public async Task AcquireSnapshot_CapturesCurrentSequenceNumber()
    {
        using var engine = NewEngine();
        await engine.SetAsync(K(1), V(1));
        await engine.SetAsync(K(2), V(2));
        var expectedSeqno = engine.LastAssignedSequenceNumber;

        await using var snap = await engine.AcquireSnapshotAsync();

        snap.SequenceNumber.Should().Be(expectedSeqno);
    }

    [Fact]
    public async Task AcquireSnapshot_EmptyEngine_SequenceNumberIsZero()
    {
        using var engine = NewEngine();
        await using var snap = await engine.AcquireSnapshotAsync();

        snap.SequenceNumber.Should().Be(0UL);
    }

    // ---- Point-lookup visibility ----

    [Fact]
    public async Task Snapshot_PreWrite_ReadsPreValue_AfterOverwrite()
    {
        using var engine = NewEngine();
        await engine.SetAsync(K(1), V(100));
        await using var snap = await engine.AcquireSnapshotAsync();

        await engine.SetAsync(K(1), V(200));

        var (value, found) = await snap.TryGetValueAsync(K(1));
        found.Should().BeTrue();
        value.Value.Should().Be(100);

        // live engine sees the new write.
        var (liveValue, liveFound) = await engine.TryGetValueAsync(K(1));
        liveFound.Should().BeTrue();
        liveValue.Value.Should().Be(200);
    }

    [Fact]
    public async Task Snapshot_PostWrite_ReadsNewValue()
    {
        using var engine = NewEngine();
        await engine.SetAsync(K(1), V(100));
        await engine.SetAsync(K(1), V(200));
        await using var snap = await engine.AcquireSnapshotAsync();

        var (value, found) = await snap.TryGetValueAsync(K(1));
        found.Should().BeTrue();
        value.Value.Should().Be(200);
    }

    [Fact]
    public async Task Snapshot_KeyWrittenAfterAcquisition_IsInvisible()
    {
        using var engine = NewEngine();
        await engine.SetAsync(K(1), V(1));
        await using var snap = await engine.AcquireSnapshotAsync();

        await engine.SetAsync(K(42), V(420));

        var (_, found) = await snap.TryGetValueAsync(K(42));
        found.Should().BeFalse();
    }

    // ---- Tombstone visibility ----

    [Fact]
    public async Task Snapshot_PreDelete_ReadsPreDeletedValue()
    {
        using var engine = NewEngine();
        await engine.SetAsync(K(1), V(1));
        await using var snap = await engine.AcquireSnapshotAsync();

        await engine.RemoveAsync(K(1));

        var (value, found) = await snap.TryGetValueAsync(K(1));
        found.Should().BeTrue();
        value.Value.Should().Be(1);

        // live engine observes the delete.
        var (_, liveFound) = await engine.TryGetValueAsync(K(1));
        liveFound.Should().BeFalse();
    }

    [Fact]
    public async Task Snapshot_PostDelete_SeesTombstone()
    {
        using var engine = NewEngine();
        await engine.SetAsync(K(1), V(1));
        await engine.RemoveAsync(K(1));
        await using var snap = await engine.AcquireSnapshotAsync();

        var (_, found) = await snap.TryGetValueAsync(K(1));
        found.Should().BeFalse();
    }

    [Fact]
    public async Task Snapshot_PreDelete_ReadsPreDeletedValue_AcrossSegments()
    {
        // Force the write into a segment so tombstone visibility crosses the memtable/segment boundary.
        using var engine = NewEngine(memTableCapacity: 4);
        await engine.SetAsync(K(1), V(1));
        await engine.FlushAsync();
        await using var snap = await engine.AcquireSnapshotAsync();

        await engine.RemoveAsync(K(1));
        await engine.FlushAsync();

        var (value, found) = await snap.TryGetValueAsync(K(1));
        found.Should().BeTrue();
        value.Value.Should().Be(1);
    }

    // ---- Range visibility ----

    [Fact]
    public async Task Snapshot_GetRange_ReflectsSnapshotView()
    {
        using var engine = NewEngine();
        await engine.SetAsync(K(1), V(10));
        await engine.SetAsync(K(2), V(20));
        await engine.SetAsync(K(3), V(30));
        await using var snap = await engine.AcquireSnapshotAsync();

        // Mutations after acquisition.
        await engine.SetAsync(K(2), V(200));
        await engine.SetAsync(K(4), V(40));
        await engine.RemoveAsync(K(1));

        var items = new List<(int Key, int Value)>();
        await foreach (var kv in snap.GetRangeAsync(K(0), K(10)))
        {
            items.Add((kv.Key.Value, kv.Value.Value));
        }

        items.Should().Equal(
            (1, 10),   // k=1: delete invisible, pre-delete value visible
            (2, 20),   // k=2: overwrite invisible, original value visible
            (3, 30));  // k=4: written after acquisition, invisible
    }

    // ---- Segment pinning + compaction ----

    [Fact]
    public async Task LiveSnapshot_Prevents_PhysicalSegmentDeletion_OnCompaction()
    {
        using var engine = NewEngine(memTableCapacity: 2);
        await engine.SetAsync(K(1), V(1));
        await engine.SetAsync(K(2), V(2));
        await engine.FlushAsync();
        await engine.SetAsync(K(3), V(3));
        await engine.SetAsync(K(4), V(4));
        await engine.FlushAsync();
        engine.SegmentCount.Should().Be(2);

        // Capture the files that exist under the segment folder before the snapshot is acquired.
        var preSnapFiles = Directory.GetFiles(_folder, "snap_*.sst").Select(Path.GetFileName).ToHashSet();

        await using var snap = await engine.AcquireSnapshotAsync();

        await engine.CompactAsync();

        // Compaction has produced a new segment and logically superseded the picked pair, but the
        // pinned files must remain on disk so the snapshot's reads stay valid.
        var postCompactionFiles = Directory.GetFiles(_folder, "snap_*.sst").Select(Path.GetFileName).ToHashSet();
        preSnapFiles.IsSubsetOf(postCompactionFiles).Should().BeTrue(
            "live snapshot pins the pre-compaction segment set");

        // Snapshot still reads all pre-snapshot keys.
        (await snap.TryGetValueAsync(K(1))).Found.Should().BeTrue();
        (await snap.TryGetValueAsync(K(4))).Found.Should().BeTrue();
    }

    [Fact]
    public async Task SnapshotDispose_Releases_PinnedSegmentsForDeletion()
    {
        using var engine = NewEngine(memTableCapacity: 2);
        await engine.SetAsync(K(1), V(1));
        await engine.SetAsync(K(2), V(2));
        await engine.FlushAsync();
        await engine.SetAsync(K(3), V(3));
        await engine.SetAsync(K(4), V(4));
        await engine.FlushAsync();

        var snap = await engine.AcquireSnapshotAsync();
        await engine.CompactAsync();

        // The snapshot pinned the pre-compaction segment set; those files are still on disk.
        // After snapshot dispose + another compaction cycle, the engine should be free to
        // reclaim them.
        await snap.DisposeAsync();

        // Trigger a no-op compaction cycle that sweeps retired-but-unpinned files.
        await engine.SetAsync(K(5), V(5));
        await engine.SetAsync(K(6), V(6));
        await engine.FlushAsync();
        await engine.CompactAsync();

        var remainingFiles = Directory.GetFiles(_folder, "snap_*.sst").Select(Path.GetFileName).ToList();
        // After two compactions + dispose, only the latest compacted output should remain
        // on disk — no unreachable pinned files.
        remainingFiles.Count.Should().BeLessThanOrEqualTo(engine.SegmentCount);
    }

    [Fact]
    public async Task LiveSnapshot_SurvivesConcurrentClear()
    {
        // ClearAsync must honour the same snapshot-pin contract that compaction honours: a
        // segment reader pinned by a live snapshot must not be disposed until the snapshot
        // releases it. Before the fix, ClearAsync disposes every segment reader unconditionally,
        // so a read through the snapshot after Clear throws ObjectDisposedException.
        using var engine = NewEngine(memTableCapacity: 2);
        await engine.SetAsync(K(1), V(1));
        await engine.SetAsync(K(2), V(2));
        await engine.FlushAsync();
        await engine.SetAsync(K(3), V(3));
        await engine.SetAsync(K(4), V(4));
        await engine.FlushAsync();
        engine.SegmentCount.Should().Be(2);

        await using var snap = await engine.AcquireSnapshotAsync();

        await engine.ClearAsync();

        // The snapshot was acquired before the clear; its pinned readers must remain usable.
        var (v1, found1) = await snap.TryGetValueAsync(K(1));
        found1.Should().BeTrue("the snapshot pinned the pre-clear segment set");
        v1.Value.Should().Be(1);
        (await snap.TryGetValueAsync(K(4))).Found.Should().BeTrue();

        // The live engine is empty after the clear.
        (await engine.TryGetValueAsync(K(1))).Found.Should().BeFalse();
    }

    // ---- Tombstone GC interaction ----

    [Fact]
    public async Task TombstoneGC_Deferred_WhileSnapshotCanObserveDelete()
    {
        using var engine = NewEngine(memTableCapacity: 4);
        await engine.SetAsync(K(1), V(1));
        await engine.FlushAsync();          // segment 0: {1 -> 1}

        await using var snap = await engine.AcquireSnapshotAsync();

        await engine.SetAsync(K(2), V(2));  // keep memtable non-empty so the next flush
                                            // materialises a real segment + tombstone pair.
        await engine.RemoveAsync(K(1));
        await engine.FlushAsync();          // segment 1: data {(2,2)} + tombstone on k=1

        // Full compaction picks index 0, so the bottommost-segment predicate is satisfied.
        // But the live snapshot still needs to see the pre-delete value for k=1, so the
        // tombstone-strip rule must retain the pre-delete version until the snapshot
        // is disposed.
        await engine.CompactAsync();

        var (value, found) = await snap.TryGetValueAsync(K(1));
        found.Should().BeTrue("snapshot was acquired before the delete and must still see the value");
        value.Value.Should().Be(1);
    }

    // ---- Dispose semantics ----

    [Fact]
    public async Task Snapshot_UseAfterDispose_Throws()
    {
        using var engine = NewEngine();
        await engine.SetAsync(K(1), V(1));
        var snap = await engine.AcquireSnapshotAsync();
        await snap.DisposeAsync();

        var readAct = async () => await snap.TryGetValueAsync(K(1));
        await readAct.Should().ThrowAsync<ObjectDisposedException>();

        var rangeAct = async () =>
        {
            await foreach (var _ in snap.GetRangeAsync(K(0), K(10))) { }
        };
        await rangeAct.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task Snapshot_DoubleDispose_Idempotent()
    {
        using var engine = NewEngine();
        var snap = await engine.AcquireSnapshotAsync();
        await snap.DisposeAsync();
        var act = async () => await snap.DisposeAsync();
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task TryGetValueAsync_AfterWriteAndDelete_SnapshotAtDeleteSeqno_ReturnsNotFound()
    {
        // The tombstone (seqno 2) wins over the data entry (seqno 1) when both are within
        // the snapshot's visible range and the tombstone has the higher seqno.
        using var engine = NewEngine(memTableCapacity: 4);

        await engine.SetAsync(K(1), V(10));           // seqno 1
        await engine.FlushAsync();                     // segment S1: K(1)->V(10), seqno 1

        await engine.RemoveAsync(K(1));               // tombstone seqno 2
        await engine.FlushAsync();                     // segment S2: tombstone, seqno 2

        // Snapshot captured after both flushes: sees seqno 1 data AND seqno 2 tombstone.
        var snap = await engine.AcquireSnapshotAsync();
        try
        {
            var (_, found) = await snap.TryGetValueAsync(K(1)).ConfigureAwait(true);

            // Tombstone seqno 2 >= data seqno 1, both within snapshot range → tombstone wins.
            found.Should().BeFalse("tombstone at seqno 2 wins over data at seqno 1");
        }
        finally
        {
            await snap.DisposeAsync().ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task TryGetValueAsync_AfterWriteAndDelete_SnapshotBeforeDelete_ReturnsValue()
    {
        // A snapshot taken between the write and the delete sees the data entry, not the
        // tombstone (tombstone seqno exceeds snapshot's visible range).
        using var engine = NewEngine(memTableCapacity: 4);

        await engine.SetAsync(K(1), V(10));           // seqno 1
        await engine.FlushAsync();                     // segment S1: K(1)->V(10), seqno 1

        // Snapshot taken here — only seqno 1 is visible.
        var snap = await engine.AcquireSnapshotAsync();
        try
        {
            await engine.RemoveAsync(K(1));           // tombstone seqno 2 (after snapshot)
            await engine.FlushAsync();                 // segment S2: tombstone, seqno 2

            var (value, found) = await snap.TryGetValueAsync(K(1)).ConfigureAwait(true);

            found.Should().BeTrue("snapshot was taken before the delete");
            value.Should().Be(V(10));
        }
        finally
        {
            await snap.DisposeAsync().ConfigureAwait(true);
        }
    }
}
