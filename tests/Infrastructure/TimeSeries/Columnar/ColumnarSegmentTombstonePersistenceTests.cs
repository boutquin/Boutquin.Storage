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
using Boutquin.Storage.Domain.ValueObjects.TimeSeries;
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar;

namespace Boutquin.Storage.Infrastructure.Tests.TimeSeries.Columnar;

/// <summary>
/// Pins the columnar-segment tombstone-persistence contract (Option A): the outer envelope
/// footer carries the per-segment tombstone map (deleted <see cref="TimeSeriesKey{TDatasetKey}"/>
/// → deletion seqno) so a no-WAL reopen does not resurrect a deleted row.
/// <list type="bullet">
///   <item><description><c>WriteAsync(entries, tombstones)</c> persists the map; a re-opened
///   reader returns it via <c>GetTombstoneSeqnos</c>.</description></item>
///   <item><description>A no-tombstone write yields an empty map.</description></item>
///   <item><description>End-to-end: a columnar LSM engine with no WAL, after
///   append/flush/delete/flush/dispose, does not resurrect the delete on reopen.</description></item>
/// </list>
/// Reference: DDIA Ch. 4 p. 121, 124 — without the deletion seqno surviving to disk, a delete
/// silently resurrects from an older segment after a restart, and the compactor cannot safely
/// reclaim the marker.
/// </summary>
public sealed class ColumnarSegmentTombstonePersistenceTests : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), $"colttomb-persist-{Guid.NewGuid():N}");

    public ColumnarSegmentTombstonePersistenceTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            try { Directory.Delete(_folder, recursive: true); } catch { /* best-effort */ }
        }
    }

    private static ColumnarSegmentFormat<SerializableWrapper<string>, MarketDataBar> Format()
        => new(new CompressedColumnarSegmentFormat<MarketDataBar>());

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

    private static SegmentWriteEntry<TimeSeriesKey<SerializableWrapper<string>>, MarketDataBar> Entry(
        string ds, DateOnly d, decimal close)
        => SegmentWriteEntry<TimeSeriesKey<SerializableWrapper<string>>, MarketDataBar>.Inline(
            Key(ds, d), Bar(d, close), 0UL);

    [Fact]
    public async Task WriteWithTombstones_RoundTripsViaGetTombstoneSeqnos()
    {
        var d1 = new DateOnly(2024, 1, 1);
        var d2 = new DateOnly(2024, 1, 2);
        var entries = new[] { Entry("AAPL", d1, 100m) };
        var tombstones = new Dictionary<TimeSeriesKey<SerializableWrapper<string>>, ulong>
        {
            { Key("AAPL", d2), 42UL },
            { Key("MSFT", d1), 99UL },
        };

        var reader = await Format().WriteAsync(_folder, "tomb_roundtrip.bsct", entries, tombstones);

        var got = reader.GetTombstoneSeqnos();
        got.Should().HaveCount(2);
        got[Key("AAPL", d2)].Should().Be(42UL);
        got[Key("MSFT", d1)].Should().Be(99UL);
    }

    [Fact]
    public async Task ReOpen_RestoresTombstoneSeqnosFromFooter()
    {
        var d1 = new DateOnly(2024, 1, 1);
        var d2 = new DateOnly(2024, 1, 2);
        var entries = new[] { Entry("AAPL", d1, 100m) };
        var tombstones = new Dictionary<TimeSeriesKey<SerializableWrapper<string>>, ulong>
        {
            { Key("AAPL", d2), 200UL },
        };

        await Format().WriteAsync(_folder, "tomb_reopen.bsct", entries, tombstones);

        // Fresh Open on the same path — must reconstruct the tombstone map from the footer.
        var reopened = Format().Open(_folder, "tomb_reopen.bsct");

        var got = reopened.GetTombstoneSeqnos();
        got.Should().HaveCount(1);
        got[Key("AAPL", d2)].Should().Be(200UL,
            "the deletion seqno must survive to disk and recover bit-exact on re-open so a "
            + "no-WAL restart does not resurrect the delete");
    }

    // Crash-atomicity / durability (#2): a columnar segment must be written to a sibling temp
    // file and atomically renamed over the final path (DurableFile.AtomicReplace), so a write
    // that fails partway cannot corrupt or truncate a pre-existing segment at the final path —
    // and on success the manifest never references a segment whose bytes are still buffered.
    // The non-durable in-place FileMode.Create + FlushAsync write truncated the existing file
    // before the body was written.
    [Fact]
    public async Task FailedWrite_DoesNotCorruptExistingSegmentAtFinalPath()
    {
        var d1 = new DateOnly(2024, 1, 1);
        var path = Path.Combine(_folder, "atomic.bsct");

        // A complete, valid segment is already on disk at the final path.
        await Format().WriteAsync(_folder, "atomic.bsct", new[] { Entry("AAPL", d1, 100m) });
        var originalBytes = await File.ReadAllBytesAsync(path);
        originalBytes.Length.Should().BeGreaterThan(8);

        // Overwrite it with a write that fails partway: a pre-cancelled token throws inside the
        // dataset loop, AFTER the file is opened (the first ThrowIfCancellationRequested).
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var overwrite = async () => await Format().WriteAsync(
            _folder, "atomic.bsct",
            new[] { Entry("AAPL", d1, 200m), Entry("MSFT", d1, 300m) },
            tombstones: null,
            cancellationToken: cts.Token);
        await overwrite.Should().ThrowAsync<OperationCanceledException>();

        // The original segment must be byte-for-byte intact — the durable write touched only a
        // temp file and never reached the atomic rename.
        var afterBytes = await File.ReadAllBytesAsync(path);
        afterBytes.Should().Equal(originalBytes,
            "a failed columnar write must not corrupt the pre-existing segment at the final path; "
            + "the write must use a temp file + atomic replace, not in-place FileMode.Create");
    }

    [Fact]
    public async Task WriteWithoutTombstones_GetTombstoneSeqnosReturnsEmpty()
    {
        var d1 = new DateOnly(2024, 1, 1);
        var entries = new[] { Entry("AAPL", d1, 100m) };

        var reader = await Format().WriteAsync(_folder, "no_tomb.bsct", entries);

        reader.GetTombstoneSeqnos().Should().BeEmpty();
    }

    // End-to-end recovery: a write and its delete flushed into a single columnar segment must
    // survive a no-WAL reopen. The flush hands WriteAsync both the data entry and the tombstone
    // map; the footer persists the tombstone, and RecoverSegmentsFromDisk rebuilds the engine's
    // per-segment tombstone state from GetTombstoneSeqnos so the delete shadows the data on read.
    [Fact]
    public async Task EngineReopen_NoWal_DeleteCoFlushedWithData_DoesNotResurrect()
    {
        var d = new DateOnly(2024, 1, 1);

        // First engine lifetime: write, delete, flush (both land in one segment), dispose. No WAL
        // is wired (LsmEngineFactory.Build defaults writeAheadLog: null), so recovery on reopen
        // depends entirely on the columnar segment footer — exactly the path under test.
        {
            var inner = BuildColumnarEngine();
            await inner.SetAsync(Key("AAPL", d), Bar(d, 100m));
            await inner.RemoveAsync(Key("AAPL", d));
            await inner.FlushAsync();
            inner.Dispose();
        }

        // Reopen a fresh engine on the same folder. The delete must survive via the
        // footer-persisted tombstone, not resurrect from the co-flushed data.
        var reopened = BuildColumnarEngine();
        var (_, found) = await reopened.TryGetValueAsync(Key("AAPL", d));
        found.Should().BeFalse(
            "a delete co-flushed with its data into a columnar segment must survive a no-WAL "
            + "reopen — the tombstone is persisted in the segment footer and recovered at startup");
        reopened.Dispose();
    }

    // Cross-flush recovery: the delete is flushed in a SEPARATE, data-less flush after the data
    // segment already landed. The tombstone-only flush now writes its own tombstone-carrying
    // segment (rather than only attaching to the newest segment's in-memory map), so the delete
    // is persisted to disk and survives a no-WAL reopen.
    [Fact]
    public async Task EngineReopen_NoWal_DeleteInSeparateFlush_DoesNotResurrect()
    {
        var d = new DateOnly(2024, 1, 1);

        {
            var inner = BuildColumnarEngine();
            await inner.SetAsync(Key("AAPL", d), Bar(d, 100m));
            await inner.FlushAsync(); // seg0: data
            await inner.RemoveAsync(Key("AAPL", d));
            await inner.FlushAsync(); // seg1: tombstone-only (data-less)
            inner.Dispose();
        }

        var reopened = BuildColumnarEngine();
        var (_, found) = await reopened.TryGetValueAsync(Key("AAPL", d));
        found.Should().BeFalse(
            "a delete flushed in a separate data-less flush must survive a no-WAL reopen — the "
            + "tombstone-only flush persists its own tombstone-carrying segment");
        reopened.Dispose();
    }

    // Re-insert after a recovered cross-flush delete must be visible. This pins the counter-reseed
    // correctness: recovery folds the recovered tombstone seqnos into the engine's sequence-number
    // watermark, so a post-recovery re-insert is assigned a seqno ABOVE the recovered tombstone and
    // is not wrongly suppressed by it. (Columnar data segments recover with max-seqno 0, and a
    // tombstone-only segment's data-derived max is 0, so the tombstone seqno is the only signal.)
    [Fact]
    public async Task EngineReopen_NoWal_ReinsertAfterRecoveredDelete_IsVisible()
    {
        var d = new DateOnly(2024, 1, 1);

        {
            var inner = BuildColumnarEngine();
            await inner.SetAsync(Key("AAPL", d), Bar(d, 100m));
            await inner.FlushAsync();
            await inner.RemoveAsync(Key("AAPL", d));
            await inner.FlushAsync();
            inner.Dispose();
        }

        var reopened = BuildColumnarEngine();
        await reopened.SetAsync(Key("AAPL", d), Bar(d, 999m)); // re-insert after recovery
        var (value, found) = await reopened.TryGetValueAsync(Key("AAPL", d));
        found.Should().BeTrue(
            "a re-insert after a recovered delete must out-version the recovered tombstone — "
            + "recovery must reseed the seqno counter above every recovered tombstone seqno");
        value.Close.Should().Be(999m);
        reopened.Dispose();
    }

    private LsmStorageEngine<TimeSeriesKey<SerializableWrapper<string>>, MarketDataBar> BuildColumnarEngine()
    {
        var innerColumnar = new CompressedColumnarSegmentFormat<MarketDataBar>();
        var segmentFormat = new ColumnarSegmentFormat<SerializableWrapper<string>, MarketDataBar>(innerColumnar);
        return LsmEngineFactory.Build<TimeSeriesKey<SerializableWrapper<string>>, MarketDataBar>(
            memTableCapacity: 8,
            segmentFolder: _folder,
            segmentPrefix: "probe",
            segmentFormat: segmentFormat);
    }
}
