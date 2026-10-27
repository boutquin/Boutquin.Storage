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
using Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar;
using Boutquin.Storage.Infrastructure.Tests.TimeSeries.Columnar;
using Boutquin.Storage.Infrastructure.TimeSeries;
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar;

namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// Pins the segment-pin contract of the projected-range snapshot that the columnar decorator's
/// column-subset range read is built on. While the snapshot is live, a concurrent compaction
/// must keep its captured input segments readable (DDIA 2e p. 121 — reads continue to use the
/// input segments while the merge runs; only afterwards can the inputs be deleted). Once the
/// snapshot is released, those inputs must be reclaimed rather than leak until engine dispose.
/// </summary>
public sealed class ProjectedRangeSnapshotPinTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"ProjectedPin_{Guid.NewGuid():N}");

    public ProjectedRangeSnapshotPinTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best-effort */ }
        }
    }

    private static SerializableWrapper<int> K(int v) => new(v);
    private static SerializableWrapper<int> V(int v) => new(v);

    private LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<int>> NewEngine()
        => LsmEngineFactory.Build<SerializableWrapper<int>, SerializableWrapper<int>>(
            memTableCapacity: 8, segmentFolder: _dir, segmentPrefix: "seg");

    private static async Task<int> CountRowsAsync(ISegmentReader<SerializableWrapper<int>, SerializableWrapper<int>> segment)
    {
        var rows = 0;
        await foreach (var _ in segment.StreamBlockEntriesAsync(ulong.MaxValue).ConfigureAwait(true))
        {
            rows++;
        }
        return rows;
    }

    [Fact]
    public async Task LiveProjectedSnapshot_CompactionKeepsCapturedSegmentsReadable()
    {
        using var engine = NewEngine();
        for (var i = 0; i < 3; i++)
        {
            await engine.SetAsync(K(i), V(i * 10)).ConfigureAwait(true);
            await engine.FlushAsync().ConfigureAwait(true);
        }
        engine.SegmentCount.Should().Be(3, "three flushes produce three input segments");

        var snapshot = await engine.AcquireProjectedRangeSnapshotAsync(K(0), K(100)).ConfigureAwait(true);
        snapshot.Segments.Should().HaveCount(3);

        // A compaction (the flusher runs one in the background) merges the captured segments
        // while the projected read has yet to stream them.
        await engine.CompactAsync().ConfigureAwait(true);

        var rows = 0;
        Func<Task> readCaptured = async () =>
        {
            foreach (var segment in snapshot.Segments)
            {
                rows += await CountRowsAsync(segment).ConfigureAwait(true);
            }
        };

        await readCaptured.Should().NotThrowAsync(
            "a live projected snapshot pins its input segments: compaction must park them, not delete them").ConfigureAwait(true);
        rows.Should().Be(3, "every captured segment still serves its one row");
        await snapshot.DisposeAsync().ConfigureAwait(true);
    }

    [Fact]
    public async Task ReleasedProjectedSnapshot_CompactedInputsAreReclaimed()
    {
        using var engine = NewEngine();
        for (var i = 0; i < 3; i++)
        {
            await engine.SetAsync(K(i), V(i * 10)).ConfigureAwait(true);
            await engine.FlushAsync().ConfigureAwait(true);
        }
        var inputFiles = Directory.GetFiles(_dir, "seg_*.sst");
        inputFiles.Should().HaveCount(3);

        var snapshot = await engine.AcquireProjectedRangeSnapshotAsync(K(0), K(100)).ConfigureAwait(true);
        await engine.CompactAsync().ConfigureAwait(true);
        engine.RetiredPinnedFileCount.Should().Be(3, "the live projected snapshot parks all three compacted inputs");

        await snapshot.DisposeAsync().ConfigureAwait(true);

        engine.RetiredPinnedFileCount.Should().Be(0,
            "releasing the projected snapshot drops its pin, so the parked inputs are reclaimed instead of leaking until dispose");
        foreach (var file in inputFiles)
        {
            File.Exists(file).Should().BeFalse($"'{Path.GetFileName(file)}' was merged away and is no longer pinned");
        }

        // A second release is a no-op, not a fault.
        await snapshot.DisposeAsync().ConfigureAwait(true);
    }

    private (LsmTimeSeriesStorageEngine<SerializableWrapper<string>, MarketDataBar> Engine,
             LsmStorageEngine<TimeSeriesKey<SerializableWrapper<string>>, MarketDataBar> Inner,
             string Folder)
        BuildColumnar()
    {
        var folder = Path.Combine(_dir, "col");
        Directory.CreateDirectory(folder);
        var innerColumnar = new CompressedColumnarSegmentFormat<MarketDataBar>();
        var inner = LsmEngineFactory.Build<TimeSeriesKey<SerializableWrapper<string>>, MarketDataBar>(
            memTableCapacity: 8,
            segmentFolder: folder,
            segmentPrefix: "col",
            segmentFormat: new ColumnarSegmentFormat<SerializableWrapper<string>, MarketDataBar>(innerColumnar));
        var engine = new LsmTimeSeriesStorageEngine<SerializableWrapper<string>, MarketDataBar>(
            inner, new ColumnarTimeSeriesStoreOptions<MarketDataBar> { ColumnarFormat = innerColumnar });
        return (engine, inner, folder);
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

    [Fact]
    public async Task ProjectedSubsetRangeRead_ReleasesItsPin_CompleteAndAbandoned()
    {
        var (engine, inner, folder) = BuildColumnar();
        using var innerScope = inner;
        var ds = new SerializableWrapper<string>("AAPL");
        var d0 = new DateOnly(2024, 1, 1);
        for (var i = 0; i < 3; i++)
        {
            var d = d0.AddDays(i);
            await engine.SetAsync(new TimeSeriesKey<SerializableWrapper<string>>(ds, d), Bar(d, 100m + i)).ConfigureAwait(true);
            await inner.FlushAsync().ConfigureAwait(true);
        }
        var inputFiles = Directory.GetFiles(folder, "col_*");
        inputFiles.Should().HaveCount(3, "three flushes produce three columnar input segments");

        var start = new TimeSeriesKey<SerializableWrapper<string>>(ds, d0);
        var end = new TimeSeriesKey<SerializableWrapper<string>>(ds, d0.AddDays(10));
        var projection = new ColumnProjection(["Close"]);

        // A column-subset read enumerated to the end.
        var closes = new List<decimal>();
        await foreach (var (_, bar) in engine.GetRangeAsync(start, end, projection).ConfigureAwait(true))
        {
            closes.Add(bar.Close);
        }
        closes.Should().Equal(100m, 101m, 102m);

        // A column-subset read the caller abandons after the first row.
        await foreach (var abandoned in engine.GetRangeAsync(start, end, projection).ConfigureAwait(true))
        {
            break;
        }

        // A column-subset read whose caller has taken the first row and still holds the
        // enumerator: every row is already merged, so the read must not pin the segments.
        var held = engine.GetRangeAsync(start, end, projection).GetAsyncEnumerator();
        (await held.MoveNextAsync().ConfigureAwait(true)).Should().BeTrue();

        // None of the reads may leave its segment pin behind: the compaction that merges the
        // three inputs must be able to delete them at once.
        await engine.CompactAsync().ConfigureAwait(true);
        inner.RetiredPinnedFileCount.Should().Be(0,
            "a finished, abandoned or suspended projected read releases its snapshot pin, so compaction reclaims its inputs");
        foreach (var file in inputFiles)
        {
            File.Exists(file).Should().BeFalse($"'{Path.GetFileName(file)}' was merged away and no read still pins it");
        }

        await held.DisposeAsync().ConfigureAwait(true);
    }

    [Fact]
    public async Task ProjectedSubsetRangeRead_ThatFaults_ReleasesItsPin()
    {
        // ExplicitDateBar has no public "Date" column, so the projected-subset path refuses the
        // read after it has acquired the snapshot.
        var folder = Path.Combine(_dir, "expl");
        Directory.CreateDirectory(folder);
        var innerColumnar = new CompressedColumnarSegmentFormat<ExplicitDateBar>();
        using var inner = LsmEngineFactory.Build<TimeSeriesKey<SerializableWrapper<string>>, ExplicitDateBar>(
            memTableCapacity: 8,
            segmentFolder: folder,
            segmentPrefix: "expl",
            segmentFormat: new ColumnarSegmentFormat<SerializableWrapper<string>, ExplicitDateBar>(innerColumnar));
        var engine = new LsmTimeSeriesStorageEngine<SerializableWrapper<string>, ExplicitDateBar>(
            inner, new ColumnarTimeSeriesStoreOptions<ExplicitDateBar> { ColumnarFormat = innerColumnar });

        var ds = new SerializableWrapper<string>("XDAT");
        var d0 = new DateOnly(2024, 1, 1);
        for (var i = 0; i < 2; i++)
        {
            var d = d0.AddDays(i);
            await engine.SetAsync(
                new TimeSeriesKey<SerializableWrapper<string>>(ds, d),
                new ExplicitDateBar { TradeDate = d, Value = i }).ConfigureAwait(true);
            await inner.FlushAsync().ConfigureAwait(true);
        }
        var inputFiles = Directory.GetFiles(folder, "expl_*");
        inputFiles.Should().HaveCount(2);

        var read = async () =>
        {
            await foreach (var row in engine.GetRangeAsync(
                new TimeSeriesKey<SerializableWrapper<string>>(ds, d0),
                new TimeSeriesKey<SerializableWrapper<string>>(ds, d0.AddDays(10)),
                new ColumnProjection(["Value"])).ConfigureAwait(true))
            {
                // the read faults before the first row
            }
        };
        await read.Should().ThrowAsync<InvalidOperationException>().ConfigureAwait(true);

        await engine.CompactAsync().ConfigureAwait(true);
        inner.RetiredPinnedFileCount.Should().Be(0,
            "a projected read that faults after acquiring its snapshot still releases the pin");
        foreach (var file in inputFiles)
        {
            File.Exists(file).Should().BeFalse($"'{Path.GetFileName(file)}' was merged away and the faulted read released it");
        }
    }
}
