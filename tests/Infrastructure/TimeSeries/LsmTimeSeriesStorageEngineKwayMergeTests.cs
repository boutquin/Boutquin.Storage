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

namespace Boutquin.Storage.Infrastructure.Tests.TimeSeries;

// Exercises the k-way merge semantics of the columnar projected range scan: newest-wins across
// memtable and segments, tombstone suppression, IProjectingSegmentReader dispatch, and the
// row-segment fallback. Failures here indicate the merge refactor diverged from the generic
// LSM engine's merge semantics.
public sealed class LsmTimeSeriesStorageEngineKwayMergeTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        $"TsKway_{Guid.NewGuid():N}");

    public LsmTimeSeriesStorageEngineKwayMergeTests()
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

    private (LsmTimeSeriesStore<SerializableWrapper<string>, MarketDataBar> Store,
             LsmStorageEngine<TimeSeriesKey<SerializableWrapper<string>>, MarketDataBar> Inner)
        BuildColumnarStore(int memTableCapacity = 32)
    {
        var innerColumnar = new CompressedColumnarSegmentFormat<MarketDataBar>();
        var segmentFormat = new ColumnarSegmentFormat<SerializableWrapper<string>, MarketDataBar>(innerColumnar);

        var engine = LsmEngineFactory.Build<TimeSeriesKey<SerializableWrapper<string>>, MarketDataBar>(
            memTableCapacity: memTableCapacity,
            segmentFolder: _folder,
            segmentPrefix: "kway",
            segmentFormat: segmentFormat);

        var options = new ColumnarTimeSeriesStoreOptions<MarketDataBar>
        {
            ColumnarFormat = innerColumnar,
        };
        var tsEngine = new LsmTimeSeriesStorageEngine<SerializableWrapper<string>, MarketDataBar>(engine, options);
        var store = new LsmTimeSeriesStore<SerializableWrapper<string>, MarketDataBar>(
            tsEngine,
            new InMemoryDatasetCoverageIndex<SerializableWrapper<string>>());
        return (store, engine);
    }

    private static MarketDataBar Bar(DateOnly date, decimal close, long volume)
        => new()
        {
            Date = date,
            Open = close - 1m,
            High = close + 1m,
            Low = close - 2m,
            Close = close,
            Volume = volume,
            Vwap = close,
            TradeCount = (int)volume / 1000,
            Dividend = 0m,
            SplitRatio = 1m,
            AdjustedClose = close,
            SourceFlags = 0,
        };

    private static async Task<List<MarketDataBar>> ToListAsync(IAsyncEnumerable<MarketDataBar> source)
    {
        var list = new List<MarketDataBar>();
        await foreach (var r in source)
        {
            list.Add(r);
        }
        return list;
    }

    // ColumnarMerge_MemtableAndSegments_NewestWins.
    // The generic LSM engine's k-way merge gives the memtable the highest source index so a
    // key appearing in both memtable and a segment yields the memtable value. The columnar
    // decorator must preserve this — otherwise a stale flushed value would shadow a fresh
    // in-memory overwrite and break last-writer-wins semantics.
    [Fact]
    public async Task ColumnarMerge_MemtableAndSegments_NewestWins()
    {
        var (store, inner) = BuildColumnarStore(memTableCapacity: 8);
        var ds = new SerializableWrapper<string>("AAPL");
        var d = new DateOnly(2024, 1, 1);

        // First write: Close = 100. Flush to segment so it's on disk.
        await store.AppendAsync(ds, [Bar(d, close: 100m, volume: 1_000L)]);
        await inner.FlushAsync();
        inner.SegmentCount.Should().BeGreaterThan(0,
            "first write must have produced a segment to exercise the memtable-vs-segment race");

        // Second write: same key, Close = 999. Stays in memtable (capacity 8 >> 1 record).
        await store.AppendAsync(ds, [Bar(d, close: 999m, volume: 2_000L)]);

        var slice = await ToListAsync(store.GetRangeAsync(ds, d, d, ColumnProjection.All));

        slice.Should().ContainSingle("duplicate key across memtable + segment must collapse to one row");
        slice[0].Close.Should().Be(999m,
            "memtable is the newest source — its value must win over the older segment value");
        slice[0].Volume.Should().Be(2_000L,
            "every field from the newest source wins, not just the collation key");
    }

    // ColumnarMerge_TombstoneInMemtable_SuppressesSegmentMatch.
    // A Remove issued after a Set records a memtable-level tombstone that suppresses any older
    // segment value at the same key. Without this, delete-after-flush would silently resurrect
    // the stale segment value on the next range scan.
    [Fact]
    public async Task ColumnarMerge_TombstoneInMemtable_SuppressesSegmentMatch()
    {
        var (store, inner) = BuildColumnarStore(memTableCapacity: 8);
        var ds = new SerializableWrapper<string>("AAPL");
        var d0 = new DateOnly(2024, 1, 1);
        var d1 = new DateOnly(2024, 1, 2);

        // Two records — write both, flush so they're in a segment.
        await store.AppendAsync(ds,
        [
            Bar(d0, close: 100m, volume: 1_000L),
            Bar(d1, close: 200m, volume: 2_000L),
        ]);
        await inner.FlushAsync();
        inner.SegmentCount.Should().BeGreaterThan(0);

        // Issue a Remove on k0 via the engine's tombstone path. Going through the engine
        // directly (rather than a hypothetical store-level delete) keeps the test focused on
        // the merge's tombstone filter — that is what this test verifies.
        var k0 = new TimeSeriesKey<SerializableWrapper<string>>(ds, d0);
        await ((LsmTimeSeriesStorageEngine<SerializableWrapper<string>, MarketDataBar>)
            typeof(LsmTimeSeriesStore<SerializableWrapper<string>, MarketDataBar>)
                .GetField("_engine", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(store)!)
            .RemoveAsync(k0);

        var slice = await ToListAsync(
            store.GetRangeAsync(ds, d0, d1, new ColumnProjection(["Close"])));

        slice.Should().ContainSingle(
            "tombstone for k0 must suppress the segment match; only k1 survives")
            .Which.Close.Should().Be(200m);
    }

    // ColumnarMerge_RowSegmentInMix_FallsBackToFullMaterialisation.
    // Row-format segments don't implement IProjectingSegmentReader. When one appears in a merge
    // alongside columnar segments (an unusual but legal mixed-format case), the decorator must
    // fall back to StreamBlockEntriesAsync + post-hoc ProjectTo for that segment. We exercise
    // the pure row-format store variant here — the decorator's Row short-circuit should route
    // to the generic engine without hitting the columnar k-way merge at all.
    [Fact]
    public async Task RowFormat_Engine_Projection_RoundsTripsThroughGenericEngine()
    {
        // Build a row-format store (default options) using a hand-rolled MarketDataBar to verify
        // projection over a row-format path still returns correctly shaped records. The Row
        // short-circuit at the top of GetRangeAsync(projection) routes to _inner.GetRangeAsync
        // directly — no ProjectTo applied, caller sees the full record (row format has no
        // per-field skip to offer; the uniformity guarantee is trivially held because every
        // field is read anyway).
        var engine = LsmEngineFactory.Build<TimeSeriesKey<SerializableWrapper<string>>, MarketDataBar>(
            memTableCapacity: 16,
            segmentFolder: _folder,
            segmentPrefix: "rowmix");
        var tsEngine = new LsmTimeSeriesStorageEngine<SerializableWrapper<string>, MarketDataBar>(engine);

        var ds = new SerializableWrapper<string>("AAPL");
        var d = new DateOnly(2024, 1, 1);
        var k = new TimeSeriesKey<SerializableWrapper<string>>(ds, d);
        var bar = Bar(d, close: 100m, volume: 1_000L);
        await tsEngine.SetAsync(k, bar);
        await engine.FlushAsync();

        var pairs = new List<(TimeSeriesKey<SerializableWrapper<string>> Key, MarketDataBar Value)>();
        await foreach (var item in tsEngine.GetRangeAsync(k, k, new ColumnProjection(["Close"])))
        {
            pairs.Add(item);
        }

        pairs.Should().ContainSingle(
            "row-format path still materialises the entry; projection is a no-op on row segments")
            .Which.Value.Close.Should().Be(100m);
    }

    // The decorator handles the pure-columnar case end-to-end: memtable records and
    // segment records present the same projected shape when merged. A regression in either path
    // (missing Date-augmentation for the segment reader, or missing ProjectTo on memtable) would
    // fail here with either a count mismatch or a field-shape mismatch.
    [Fact]
    public async Task ColumnarMerge_MemtablePlusSegment_ProjectedShape_Uniform()
    {
        var (store, inner) = BuildColumnarStore(memTableCapacity: 4);
        var ds = new SerializableWrapper<string>("AAPL");
        var d0 = new DateOnly(2024, 1, 1);

        // First batch — 10 records, overflows capacity 4 so at least one segment is written.
        var firstBatch = Enumerable.Range(0, 10)
            .Select(i => Bar(d0.AddDays(i), close: 100m + i, volume: 1_000L + i))
            .ToList();
        await store.AppendAsync(ds, firstBatch);
        await inner.FlushAsync();
        inner.SegmentCount.Should().BeGreaterThan(0);

        // Second batch — 3 records, stays in memtable.
        var secondBatch = Enumerable.Range(0, 3)
            .Select(i => Bar(d0.AddDays(10 + i), close: 200m + i, volume: 2_000L + i))
            .ToList();
        await store.AppendAsync(ds, secondBatch);

        // Projected range covering both batches.
        var slice = await ToListAsync(store.GetRangeAsync(ds, d0, d0.AddDays(13),
            new ColumnProjection(["Close", "Volume"])));

        slice.Should().HaveCount(13);

        // First 10 records come from the segment path; next 3 from the memtable path. Both
        // must populate Close and Volume, leaving every other field at default.
        for (var i = 0; i < 13; i++)
        {
            var bar = slice[i];
            var expected = i < 10 ? firstBatch[i] : secondBatch[i - 10];
            bar.Close.Should().Be(expected.Close, $"row {i}");
            bar.Volume.Should().Be(expected.Volume, $"row {i}");
            bar.Open.Should().Be(0m, $"row {i}: Open not projected — default on both paths");
            bar.TradeCount.Should().Be(0, $"row {i}: TradeCount not projected — default on both paths");
        }
    }

    // GetTailAsync forwards its projection to every window's range read — assert the tail's
    // records actually arrive projected (selected columns populated, others default), not
    // just that the forwarding parameter exists.
    [Fact]
    public async Task GetTailAsync_WithProjection_ReturnsProjectedRecords()
    {
        var (store, inner) = BuildColumnarStore(memTableCapacity: 4);
        var ds = new SerializableWrapper<string>("AAPL");
        var d0 = new DateOnly(2024, 1, 1);

        var bars = Enumerable.Range(0, 6)
            .Select(i => Bar(d0.AddDays(i), close: 100m + i, volume: 1_000L + i))
            .ToList();
        await store.AppendAsync(ds, bars);
        await inner.FlushAsync();

        var tail = await ToListAsync(store.GetTailAsync(ds, 3, projection: new ColumnProjection(["Close"])));

        tail.Should().HaveCount(3);
        for (var i = 0; i < 3; i++)
        {
            var expected = bars[5 - i]; // newest first — descending order proven via Close
            tail[i].Close.Should().Be(expected.Close, $"tail row {i}: projected column populated, date-descending");
            tail[i].Date.Should().Be(default, $"tail row {i}: Date not projected — clipped back per the projection contract");
            tail[i].Volume.Should().Be(0L, $"tail row {i}: Volume not projected — default");
            tail[i].Open.Should().Be(0m, $"tail row {i}: Open not projected — default");
        }
    }
}
