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

using Boutquin.Storage.Domain.ValueObjects.TimeSeries;
using Boutquin.Storage.Infrastructure.Tests.TimeSeries.Columnar;
using Boutquin.Storage.Infrastructure.TimeSeries;
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar;

namespace Boutquin.Storage.Infrastructure.Tests.TimeSeries;

// Change 2 (R2a) — the four MarketData isolation probes (AC-2.2), row-vs-columnar parity
// (AC-2.3), the headline compaction-during-snapshot guard-removal proof (AC-2.4), the
// no-other-consumer grep marker (AC-2.5), and deferred-reclamation observability (AC-2.7),
// run against REAL columnar segments on disk (CompressedColumnarSegmentFormat) — not the
// StubColumnarFormat used by the decorator unit tests. Each columnar assertion is paired with
// a row-format control built on the same fixture so the parity is direct.
public sealed class ColumnarSnapshotIsolationTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"colsnap-{Guid.NewGuid():N}");

    public ColumnarSnapshotIsolationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
        }
    }

    private (LsmTimeSeriesStorageEngine<SerializableWrapper<string>, MarketDataBar> Engine,
             LsmStorageEngine<TimeSeriesKey<SerializableWrapper<string>>, MarketDataBar> Inner)
        BuildColumnar(int memTableCapacity = 8)
    {
        var folder = Path.Combine(_root, $"col_{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        var innerColumnar = new CompressedColumnarSegmentFormat<MarketDataBar>();
        var segmentFormat = new ColumnarSegmentFormat<SerializableWrapper<string>, MarketDataBar>(innerColumnar);
        var inner = LsmEngineFactory.Build<TimeSeriesKey<SerializableWrapper<string>>, MarketDataBar>(
            memTableCapacity: memTableCapacity,
            segmentFolder: folder,
            segmentPrefix: "col",
            segmentFormat: segmentFormat);
        var options = new ColumnarTimeSeriesStoreOptions<MarketDataBar>
        {
            ColumnarFormat = innerColumnar,
        };
        var engine = new LsmTimeSeriesStorageEngine<SerializableWrapper<string>, MarketDataBar>(inner, options);
        return (engine, inner);
    }

    private (LsmTimeSeriesStorageEngine<SerializableWrapper<string>, MarketDataBar> Engine,
             LsmStorageEngine<TimeSeriesKey<SerializableWrapper<string>>, MarketDataBar> Inner)
        BuildRow(int memTableCapacity = 8)
    {
        var folder = Path.Combine(_root, $"row_{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        var inner = LsmEngineFactory.Build<TimeSeriesKey<SerializableWrapper<string>>, MarketDataBar>(
            memTableCapacity: memTableCapacity,
            segmentFolder: folder,
            segmentPrefix: "row");
        var engine = new LsmTimeSeriesStorageEngine<SerializableWrapper<string>, MarketDataBar>(inner);
        return (engine, inner);
    }

    private static MarketDataBar Bar(DateOnly date, decimal close) => new()
    {
        Date = date,
        Open = close - 1m,
        High = close + 1m,
        Low = close - 2m,
        Close = close,
        Volume = 1000,
        Vwap = close,
        TradeCount = 1,
        Dividend = 0m,
        SplitRatio = 1m,
        AdjustedClose = close,
        SourceFlags = 0,
    };

    private static TimeSeriesKey<SerializableWrapper<string>> Key(string ds, DateOnly d)
        => new(new SerializableWrapper<string>(ds), d);

    // ── AC-2.2(a): post-acquisition new-key + flush is invisible to the snapshot ─────────────
    [Theory]
    [InlineData(true)]   // columnar
    [InlineData(false)]  // row control
    public async Task Probe_A_PostAcquisitionNewKeyFlush_InvisibleToSnapshot(bool columnar)
    {
        var (engine, inner) = columnar ? BuildColumnar() : BuildRow();
        var d = new DateOnly(2024, 1, 1);
        await engine.SetAsync(Key("AAPL", d), Bar(d, 100m));
        await inner.FlushAsync();

        await using var snap = await engine.AcquireSnapshotAsync();

        // Post-acquisition new key, flushed to a NEW segment.
        var d2 = new DateOnly(2024, 1, 2);
        await engine.SetAsync(Key("AAPL", d2), Bar(d2, 200m));
        await inner.FlushAsync();

        var (_, foundNew) = await snap.TryGetValueAsync(Key("AAPL", d2));
        foundNew.Should().BeFalse("the post-acquisition key must be invisible to the pinned snapshot");

        var (preVal, foundPre) = await snap.TryGetValueAsync(Key("AAPL", d));
        foundPre.Should().BeTrue();
        preVal.Close.Should().Be(100m);
    }

    // ── AC-2.2(b): post-acquisition memtable write (no flush) is invisible ────────────────────
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Probe_B_PostAcquisitionMemtableWrite_Invisible(bool columnar)
    {
        var (engine, inner) = columnar ? BuildColumnar() : BuildRow();
        var d = new DateOnly(2024, 1, 1);
        await engine.SetAsync(Key("AAPL", d), Bar(d, 100m));
        await inner.FlushAsync();

        await using var snap = await engine.AcquireSnapshotAsync();

        // Post-acquisition memtable write — NO flush.
        var d2 = new DateOnly(2024, 1, 2);
        await engine.SetAsync(Key("AAPL", d2), Bar(d2, 200m));

        var (_, foundNew) = await snap.TryGetValueAsync(Key("AAPL", d2));
        foundNew.Should().BeFalse("post-acquisition memtable writes are invisible to the snapshot");
    }

    // ── AC-2.2(c): post-acquisition same-key update + flush — snapshot still reads pre-value ──
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Probe_C_PostAcquisitionSameKeyUpdateFlush_SnapshotReadsPreValue(bool columnar)
    {
        var (engine, inner) = columnar ? BuildColumnar() : BuildRow();
        var d = new DateOnly(2024, 1, 1);
        await engine.SetAsync(Key("AAPL", d), Bar(d, 100m));
        await inner.FlushAsync();

        await using var snap = await engine.AcquireSnapshotAsync();

        // Post-acquisition UPDATE of the same key, flushed.
        await engine.SetAsync(Key("AAPL", d), Bar(d, 999m));
        await inner.FlushAsync();

        var (val, found) = await snap.TryGetValueAsync(Key("AAPL", d));
        found.Should().BeTrue();
        val.Close.Should().Be(100m, "the snapshot reads the pre-acquisition version, not the post-acquisition update");

        // Live view sees the update.
        var (liveVal, liveFound) = await engine.TryGetValueAsync(Key("AAPL", d));
        liveFound.Should().BeTrue();
        liveVal.Close.Should().Be(999m);
    }

    // ── AC-2.2(d): pre-acquisition delete/reinsert seqno-0 collision resolves to pre-state ────
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Probe_D_PreAcquisitionDeleteReinsert_SnapshotResolvesToPreState(bool columnar)
    {
        var (engine, inner) = columnar ? BuildColumnar() : BuildRow();
        var d = new DateOnly(2024, 1, 1);

        // Pre-acquisition: insert -> flush -> delete -> flush -> reinsert(777) -> flush.
        await engine.SetAsync(Key("AAPL", d), Bar(d, 100m));
        await inner.FlushAsync();
        await engine.RemoveAsync(Key("AAPL", d));
        await inner.FlushAsync();
        await engine.SetAsync(Key("AAPL", d), Bar(d, 777m));
        await inner.FlushAsync();

        await using var snap = await engine.AcquireSnapshotAsync();

        // Post-acquisition: another update, flushed — must NOT bleed into the snapshot.
        await engine.SetAsync(Key("AAPL", d), Bar(d, 1m));
        await inner.FlushAsync();

        var (val, found) = await snap.TryGetValueAsync(Key("AAPL", d));
        found.Should().BeTrue("the pre-acquisition re-insert (777) is the snapshot-visible state");
        val.Close.Should().Be(777m);
    }

    // ── AC-2.3: row-vs-columnar parity for the full probe sequence ────────────────────────────
    [Fact]
    public async Task RowVsColumnar_SameMutationSequence_IdenticalSnapshotVisibleResults()
    {
        var d = new DateOnly(2024, 1, 1);
        var d2 = new DateOnly(2024, 1, 2);

        async Task<(bool PreFound, decimal PreClose, bool NewFound)> RunAsync(
            LsmTimeSeriesStorageEngine<SerializableWrapper<string>, MarketDataBar> engine,
            LsmStorageEngine<TimeSeriesKey<SerializableWrapper<string>>, MarketDataBar> inner)
        {
            await engine.SetAsync(Key("AAPL", d), Bar(d, 100m));
            await inner.FlushAsync();
            var snap = await engine.AcquireSnapshotAsync();
            await using var _ = snap;
            await engine.SetAsync(Key("AAPL", d), Bar(d, 555m));   // update existing
            await engine.SetAsync(Key("AAPL", d2), Bar(d2, 200m)); // new key
            await inner.FlushAsync();
            var (preVal, preFound) = await snap.TryGetValueAsync(Key("AAPL", d));
            var (_, newFound) = await snap.TryGetValueAsync(Key("AAPL", d2));
            return (preFound, preFound ? preVal.Close : 0m, newFound);
        }

        var (colEngine, colInner) = BuildColumnar();
        var (rowEngine, rowInner) = BuildRow();

        var col = await RunAsync(colEngine, colInner);
        var row = await RunAsync(rowEngine, rowInner);

        col.Should().Be(row, "columnar and row snapshots must isolate identically");
        col.PreFound.Should().BeTrue();
        col.PreClose.Should().Be(100m);
        col.NewFound.Should().BeFalse();
    }

    // ── AC-2.4 (HEADLINE): isolation holds across a compaction that merges a pinned segment ───
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Compaction_MergesPinnedSegmentDuringSnapshot_IsolationHolds(bool columnar)
    {
        var (engine, inner) = columnar ? BuildColumnar() : BuildRow();
        var dA = new DateOnly(2024, 1, 1);
        var dB = new DateOnly(2024, 2, 1); // different dataset/date to land in distinct blocks

        // Build ≥2 pre-acquisition segments.
        await engine.SetAsync(Key("AAA", dA), Bar(dA, 100m));
        await inner.FlushAsync(); // segment 0
        await engine.SetAsync(Key("BBB", dB), Bar(dB, 200m));
        await inner.FlushAsync(); // segment 1
        inner.SegmentCount.Should().BeGreaterThanOrEqualTo(2);

        await using (var snap = await engine.AcquireSnapshotAsync())
        {
            // Post-acquisition write that lands in a NEW segment.
            var dC = new DateOnly(2024, 3, 1);
            await engine.SetAsync(Key("CCC", dC), Bar(dC, 300m));
            await inner.FlushAsync();

            // Force a full compaction that picks the pinned pre-acquisition segments.
            await engine.CompactAsync();

            // (i) pinned reader PARKED not deleted: snapshot reads still succeed.
            var (aVal, aFound) = await snap.TryGetValueAsync(Key("AAA", dA));
            aFound.Should().BeTrue("the pinned segment file is parked, not deleted, so the snapshot can still read it");
            aVal.Close.Should().Be(100m);

            // (ii) snapshot returns pre-acquisition state for every key; post-acquisition write invisible.
            var (bVal, bFound) = await snap.TryGetValueAsync(Key("BBB", dB));
            bFound.Should().BeTrue();
            bVal.Close.Should().Be(200m);
            var (_, cFound) = await snap.TryGetValueAsync(Key("CCC", dC));
            cFound.Should().BeFalse("the post-acquisition write merged by compaction stays invisible to the live snapshot");

            // While the snapshot is held, parked files are observable (R2-F / AC-2.7 cross-check).
            inner.RetiredPinnedFileCount.Should().BeGreaterThan(0,
                "the compaction parked at least one pinned pre-acquisition segment");
        } // snapshot disposes here

        // (iii) after dispose, the parked file is swept/reclaimed on the next compaction/dispose.
        inner.RetiredPinnedFileCount.Should().Be(0, "dispose triggers the deferred-reclamation sweep");

        // (iv) a fresh NON-snapshot read after dispose+sweep observes the POST-acquisition value.
        var (liveC, liveCFound) = await engine.TryGetValueAsync(Key("CCC", new DateOnly(2024, 3, 1)));
        liveCFound.Should().BeTrue("the live view sees the post-acquisition write");
        liveC.Close.Should().Be(300m);
        // Pre-acquisition values are intact in the compacted live view too.
        var (liveA, liveAFound) = await engine.TryGetValueAsync(Key("AAA", dA));
        liveAFound.Should().BeTrue();
        liveA.Close.Should().Be(100m);
    }

    // ── AC-2.7: deferred-reclamation observability — parked-file count is operator-visible ────
    [Fact]
    public async Task DeferredReclamation_ParkedPinnedFileCount_NonZeroWhileHeld_ZeroAfterDispose()
    {
        var (engine, inner) = BuildColumnar();
        var dA = new DateOnly(2024, 1, 1);
        var dB = new DateOnly(2024, 2, 1);
        await engine.SetAsync(Key("AAA", dA), Bar(dA, 100m));
        await inner.FlushAsync();
        await engine.SetAsync(Key("BBB", dB), Bar(dB, 200m));
        await inner.FlushAsync();

        inner.RetiredPinnedFileCount.Should().Be(0, "no snapshot held yet");

        var snap = await engine.AcquireSnapshotAsync();
        await engine.CompactAsync(); // parks the pinned pre-acquisition segments

        inner.RetiredPinnedFileCount.Should().BeGreaterThan(0,
            "a held columnar snapshot defers physical delete of compaction-superseded pinned segments");

        await snap.DisposeAsync();

        inner.RetiredPinnedFileCount.Should().Be(0,
            "dispose runs the sweep so parked files are reclaimed and the count returns to zero");
    }

    // ── AC-2.5: no other consumer relies on the old SupportsSnapshots=false / throw ───────────
    // The only readers of the format-level ISegmentFormat.SupportsSnapshots flag are the two
    // format impls' own definitions, the interface declaration, and tests. The engine-level
    // SupportsSnapshots no longer delegates to the format flag (LsmStorageEngine.SupportsSnapshots
    // => true; LsmTimeSeriesStorageEngine.SupportsSnapshots => _inner.SupportsSnapshots). No
    // production consumer branches on the columnar-false value, and the decorator throw is gone.
    // This is asserted behaviorally by the probes above (a columnar engine snapshots correctly)
    // and pinned here as the committed AC-2.5 marker.
    [Fact]
    public async Task NoConsumer_ReliesOnOldColumnarSupportsSnapshotsFalse_OrThrow()
    {
        var (engine, _) = BuildColumnar();
        engine.SupportsSnapshots.Should().BeTrue();
        var snap = await engine.AcquireSnapshotAsync();
        await snap.DisposeAsync(); // no throw on the columnar format
    }

    // ── Same-segment tombstone vs re-insert after compaction ──────────────────────────────────
    // A live snapshot pins the delete, so compaction preserves the tombstone into the output
    // segment next to the newer re-insert. Columnar segments carry no per-row seqno, so the
    // point read must resolve the pair with the segment's own max seqno — the same effective
    // version the scan merge stamps — or the newest acknowledged write turns invisible to
    // point reads while the scan still returns it.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PreservedTombstoneThenReinsert_AfterCompaction_PointReadReturnsReinsert(bool columnar)
    {
        var (engine, inner) = columnar ? BuildColumnar() : BuildRow();
        var d = new DateOnly(2024, 1, 1);
        await engine.SetAsync(Key("AAPL", d), Bar(d, 100m));
        await inner.FlushAsync();
        await engine.RemoveAsync(Key("AAPL", d));
        await inner.FlushAsync();
        await using (var pinning = await engine.AcquireSnapshotAsync())
        {
            await engine.SetAsync(Key("AAPL", d), Bar(d, 999m));
            await inner.FlushAsync();
            await engine.CompactAsync();

            var (live, liveFound) = await engine.TryGetValueAsync(Key("AAPL", d));
            liveFound.Should().BeTrue("the 999 re-insert is the newest acknowledged write");
            live.Close.Should().Be(999m);

            var scan = (await inner.GetAllItemsAsync()).Select(kv => kv.Value.Close).ToList();
            scan.Should().Equal([999m], "point read and scan must agree on the same key");

            // A snapshot acquired after the compaction reads the compacted segment itself.
            await using var fresh = await engine.AcquireSnapshotAsync();
            var (snapVal, snapFound) = await fresh.TryGetValueAsync(Key("AAPL", d));
            snapFound.Should().BeTrue("a post-compaction snapshot sees the re-insert");
            snapVal.Close.Should().Be(999m);
            var snapRange = new List<decimal>();
            await foreach (var kv in fresh.GetRangeAsync(Key("AAPL", d), Key("AAPL", d)))
            {
                snapRange.Add(kv.Value.Close);
            }
            snapRange.Should().Equal([999m], "snapshot range scan agrees with the snapshot point read");
        }
    }

    // The reverse pairing: the preserved tombstone is NEWER than the data it shadows, and an
    // unrelated key flushed after the delete raises the compacted segment's max seqno above the
    // tombstone. A columnar segment cannot record which of a same-key data/tombstone pair is
    // newer, so the shadowed data must not reach the output, or the segment max seqno would
    // resurrect the deleted key on both the point-read and the scan path.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PreservedTombstoneOverOlderData_NewerUnrelatedKey_StaysDeletedAfterCompaction(bool columnar)
    {
        var (engine, inner) = columnar ? BuildColumnar() : BuildRow();
        var d = new DateOnly(2024, 1, 1);
        var other = new DateOnly(2024, 6, 1);
        await engine.SetAsync(Key("AAPL", d), Bar(d, 100m));
        await inner.FlushAsync();
        await engine.RemoveAsync(Key("AAPL", d));
        await inner.FlushAsync();
        await using (var pinning = await engine.AcquireSnapshotAsync())
        {
            await engine.SetAsync(Key("MSFT", other), Bar(other, 555m));
            await inner.FlushAsync();
            await engine.CompactAsync();

            var (_, liveFound) = await engine.TryGetValueAsync(Key("AAPL", d));
            liveFound.Should().BeFalse("the delete is newer than the only AAPL write");

            var scan = (await inner.GetAllItemsAsync()).Select(kv => kv.Value.Close).ToList();
            scan.Should().Equal([555m], "the scan must not resurrect the deleted key");

            await using var fresh = await engine.AcquireSnapshotAsync();
            var (_, snapFound) = await fresh.TryGetValueAsync(Key("AAPL", d));
            snapFound.Should().BeFalse("a post-compaction snapshot sees the delete");
            var snapRange = new List<decimal>();
            await foreach (var kv in fresh.GetRangeAsync(Key("AAPL", d), Key("MSFT", other)))
            {
                snapRange.Add(kv.Value.Close);
            }
            snapRange.Should().Equal([555m], "the snapshot range scan must not resurrect the deleted key");

            // The pinning snapshot still reads its pre-compaction view: the delete is visible.
            var (_, pinnedFound) = await pinning.TryGetValueAsync(Key("AAPL", d));
            pinnedFound.Should().BeFalse();
        }
    }
}
