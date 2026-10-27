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

using System.Reflection;
using Boutquin.Storage.Domain.ValueObjects.TimeSeries;
using Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar;
using Boutquin.Storage.Infrastructure.Tests.TimeSeries.Columnar;
using Boutquin.Storage.Infrastructure.TimeSeries;
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar;

namespace Boutquin.Storage.Infrastructure.Tests.TimeSeries;

public sealed class LsmTimeSeriesColumnarTombstoneSeqnoTests : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), $"coltomb-probe-{Guid.NewGuid():N}");

    public LsmTimeSeriesColumnarTombstoneSeqnoTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            try { Directory.Delete(_folder, recursive: true); } catch { /* best-effort */ }
        }
    }

    private (LsmTimeSeriesStore<SerializableWrapper<string>, MarketDataBar> Store,
             LsmStorageEngine<TimeSeriesKey<SerializableWrapper<string>>, MarketDataBar> Inner)
        BuildColumnarStore(int memTableCapacity = 8)
    {
        var innerColumnar = new CompressedColumnarSegmentFormat<MarketDataBar>();
        var segmentFormat = new ColumnarSegmentFormat<SerializableWrapper<string>, MarketDataBar>(innerColumnar);
        var engine = LsmEngineFactory.Build<TimeSeriesKey<SerializableWrapper<string>>, MarketDataBar>(
            memTableCapacity: memTableCapacity,
            segmentFolder: _folder,
            segmentPrefix: "probe",
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

    private static LsmTimeSeriesStorageEngine<SerializableWrapper<string>, MarketDataBar> Decorator(
        LsmTimeSeriesStore<SerializableWrapper<string>, MarketDataBar> store)
        => (LsmTimeSeriesStorageEngine<SerializableWrapper<string>, MarketDataBar>)
            typeof(LsmTimeSeriesStore<SerializableWrapper<string>, MarketDataBar>)
                .GetField("_engine", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(store)!;

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

    private static async Task<List<MarketDataBar>> ToListAsync(IAsyncEnumerable<MarketDataBar> source)
    {
        var list = new List<MarketDataBar>();
        await foreach (var r in source) { list.Add(r); }
        return list;
    }

    [Fact]
    public async Task InSession_DeleteThenFlush_DoesNotResurrect()
    {
        var (store, inner) = BuildColumnarStore();
        var ds = new SerializableWrapper<string>("AAPL");
        var d = new DateOnly(2024, 1, 1);

        await store.AppendAsync(ds, [Bar(d, close: 100m)]);
        await inner.FlushAsync(); // data -> columnar segment 0

        await Decorator(store).RemoveAsync(new TimeSeriesKey<SerializableWrapper<string>>(ds, d));
        await inner.FlushAsync(); // tombstone flushed

        var slice = await ToListAsync(store.GetRangeAsync(ds, d, d, ColumnProjection.All));
        slice.Should().BeEmpty("in-session: the delete must not resurrect after a flush");
    }

    // Projected (subset-column) read path — GetRangeProjectedAsync. A re-insert flushed to a newer
    // columnar segment than an older tombstone must survive: the decorator stamps a columnar value
    // with its segment's max seqno and suppresses only when the tombstone is strictly newer
    // (finding C-3). (The ColumnProjection.All path short-circuits to the generic engine merge and is
    // tracked separately — see the continuation note.)
    [Fact]
    public async Task InSession_DeleteFlushReinsertFlush_KeepsLiveValue()
    {
        var (store, inner) = BuildColumnarStore();
        var ds = new SerializableWrapper<string>("AAPL");
        var d = new DateOnly(2024, 1, 1);

        await store.AppendAsync(ds, [Bar(d, close: 100m)]);
        await inner.FlushAsync(); // seg0: data
        await Decorator(store).RemoveAsync(new TimeSeriesKey<SerializableWrapper<string>>(ds, d));
        await inner.FlushAsync(); // tombstone flushed
        await store.AppendAsync(ds, [Bar(d, close: 999m)]); // re-insert
        await inner.FlushAsync(); // newer segment: re-inserted data

        var slice = await ToListAsync(store.GetRangeAsync(ds, d, d, new ColumnProjection(["Close"])));
        slice.Should().ContainSingle("the re-inserted value must survive a tombstone in an older segment")
            .Which.Close.Should().Be(999m);
    }

    // ColumnProjection.All read path — GetRangeProjectedAsync short-circuits All
    // (FieldNames.Count == 0) to the generic engine's k-way merge (LsmStorageEngine.MergedStreamAsync).
    // There, columnar segment entries carry no per-entry seqno (read as 0), so the seqno-aware
    // tombstone filter would drop a re-insert flushed to a newer segment than an older tombstone
    // unless the merge stamps each seqno-less segment entry with that segment's max seqno
    // (continuation of finding C-3 on the All path).
    [Fact]
    public async Task InSession_DeleteFlushReinsertFlush_KeepsLiveValue_AllProjection()
    {
        var (store, inner) = BuildColumnarStore();
        var ds = new SerializableWrapper<string>("AAPL");
        var d = new DateOnly(2024, 1, 1);

        await store.AppendAsync(ds, [Bar(d, close: 100m)]);
        await inner.FlushAsync(); // seg0: data
        await Decorator(store).RemoveAsync(new TimeSeriesKey<SerializableWrapper<string>>(ds, d));
        await inner.FlushAsync(); // tombstone flushed
        await store.AppendAsync(ds, [Bar(d, close: 999m)]); // re-insert
        await inner.FlushAsync(); // newer segment: re-inserted data

        var slice = await ToListAsync(store.GetRangeAsync(ds, d, d, ColumnProjection.All));
        slice.Should().ContainSingle("the re-inserted value must survive a tombstone in an older segment "
                + "even on the ColumnProjection.All generic-merge path")
            .Which.Close.Should().Be(999m);
    }
}
