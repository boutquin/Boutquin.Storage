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
using Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar;
using Boutquin.Storage.Infrastructure.TimeSeries;

namespace Boutquin.Storage.Infrastructure.Tests.TimeSeries.Columnar;

// Realistic MarketData-shaped integration test. Exercises the full LSM time-series
// stack (LsmTimeSeriesStore → LsmTimeSeriesStorageEngine → LsmStorageEngine →
// ColumnarSegmentFormat → CompressedColumnarSegmentFormat) with a 12-field record whose mix of
// decimal / long / int / short / DateOnly fields mirrors the Boutquin.MarketData `Bar` shape the
// parent spec's read-amplification narrative is built on.
//
// The assertions target DDIA Ch. 4 p. 141 uniformity: projection shape must be identical whether
// a record lives in the memtable, in a flushed columnar segment, or straddles the two. The test
// does not directly measure bytes-read-from-disk — that is the benchmark suite's responsibility — but
// it locks down the shape-uniformity guarantee that makes the IO-skip a safe refactor target.
public sealed class ColumnarProjectionMarketDataIntegrationTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        $"MarketDataBar_{Guid.NewGuid():N}");

    public ColumnarProjectionMarketDataIntegrationTests()
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

    private Task<ColumnarTimeSeriesStack<SerializableWrapper<string>, MarketDataBar>> BuildStore(
        int memTableCapacity = 512)
        => ColumnarTimeSeriesEngineBuilder.BuildAsync<SerializableWrapper<string>, MarketDataBar>(
            segmentFolder: _folder, segmentPrefix: "mdb", memTableCapacity: memTableCapacity);

    private static IReadOnlyList<MarketDataBar> SyntheticCorpus(int count, DateOnly startDate)
    {
        var random = new Random(42);
        var list = new List<MarketDataBar>(count);
        for (var i = 0; i < count; i++)
        {
            var price = 100m + (decimal)(random.NextDouble() * 50);
            list.Add(new MarketDataBar
            {
                Date = startDate.AddDays(i),
                Open = price,
                High = price + 1m,
                Low = price - 1m,
                Close = price + 0.5m,
                Volume = 1_000_000L + i,
                Vwap = price + 0.25m,
                TradeCount = 500 + i,
                Dividend = 0m,
                SplitRatio = 1m,
                AdjustedClose = price + 0.5m,
                SourceFlags = i % 8,
            });
        }
        return list;
    }

    private static async Task<List<MarketDataBar>> ToListAsync(IAsyncEnumerable<MarketDataBar> source)
    {
        var list = new List<MarketDataBar>();
        await foreach (var r in source)
        {
            list.Add(r);
        }
        return list;
    }

    // Assertion (a): every returned row has Close and Volume populated; every other field is
    // default. This is the DDIA p. 141 uniformity claim, on the flushed-segment path where the
    // columnar segment format can physically skip unprojected bytes.
    [Fact]
    public async Task FlushedSegment_CloseAndVolume_Projection_PopulatesOnlyNamedFields()
    {
        await using var stack = await BuildStore(memTableCapacity: 256);
        var ds = new SerializableWrapper<string>("AAPL");
        var d0 = new DateOnly(2018, 1, 1);
        var corpus = SyntheticCorpus(count: 1_000, d0);

        await stack.Store.AppendAsync(ds, corpus);
        await stack.InnerEngine.FlushAsync();

        stack.InnerEngine.SegmentCount.Should().BeGreaterThan(0,
            "the corpus exceeds the memtable capacity — at least one columnar segment must exist");

        var projection = new ColumnProjection(["Close", "Volume"]);
        var slice = await ToListAsync(stack.Store.GetRangeAsync(ds, d0, d0.AddDays(1_000), projection));

        slice.Should().HaveCount(1_000);

        for (var i = 0; i < slice.Count; i++)
        {
            var bar = slice[i];
            var expected = corpus[i];
            bar.Close.Should().Be(expected.Close, $"row {i}: Close is projected and must survive");
            bar.Volume.Should().Be(expected.Volume, $"row {i}: Volume is projected and must survive");

            // Every other field must be default(TField) — the uniformity assertion.
            bar.Open.Should().Be(0m);
            bar.High.Should().Be(0m);
            bar.Low.Should().Be(0m);
            bar.Vwap.Should().Be(0m);
            bar.TradeCount.Should().Be(0);
            bar.Dividend.Should().Be(0m);
            bar.SplitRatio.Should().Be(0m);
            bar.AdjustedClose.Should().Be(0m);
            bar.SourceFlags.Should().Be(0);
            // Date is preserved by the segment reader so the result stream can be ordered.
        }
    }

    // Assertion (b): a record appended but not flushed exposes the same projection shape as
    // a flushed row. This locks down the DDIA p. 141 "engine hides memtable-vs-segment
    // distinction" guarantee on the memtable path specifically.
    [Fact]
    public async Task MemtableResidentRecord_CloseAndVolume_Projection_PopulatesOnlyNamedFields()
    {
        await using var stack = await BuildStore(memTableCapacity: 1_000);
        var ds = new SerializableWrapper<string>("MSFT");
        var d0 = new DateOnly(2019, 3, 15);
        var corpus = SyntheticCorpus(count: 5, d0);

        // No flush — every record stays in the memtable path.
        await stack.Store.AppendAsync(ds, corpus);

        var projection = new ColumnProjection(["Close", "Volume"]);
        var slice = await ToListAsync(stack.Store.GetRangeAsync(ds, d0, d0.AddDays(5), projection));

        slice.Should().HaveCount(5);

        for (var i = 0; i < slice.Count; i++)
        {
            var bar = slice[i];
            var expected = corpus[i];
            bar.Close.Should().Be(expected.Close, $"row {i}: Close is projected and must survive memtable path");
            bar.Volume.Should().Be(expected.Volume, $"row {i}: Volume is projected and must survive memtable path");

            bar.Open.Should().Be(0m, "memtable-resident Open must be default when unprojected");
            bar.High.Should().Be(0m);
            bar.Low.Should().Be(0m);
            bar.Vwap.Should().Be(0m);
            bar.TradeCount.Should().Be(0);
            bar.Dividend.Should().Be(0m);
            bar.SplitRatio.Should().Be(0m);
            bar.AdjustedClose.Should().Be(0m);
            bar.SourceFlags.Should().Be(0);
        }
    }

    // Assertion (c): a range that straddles memtable + segment returns a continuous stream
    // whose projection shape is identical across the boundary. This is the strongest form of the
    // uniformity claim: the caller cannot tell which row came from where.
    [Fact]
    public async Task CrossBoundary_MemtablePlusSegment_ProjectionShape_IsUniform()
    {
        await using var stack = await BuildStore(memTableCapacity: 8);
        var ds = new SerializableWrapper<string>("NVDA");
        var d0 = new DateOnly(2020, 6, 1);

        // First batch — big enough to spill to segment(s).
        var firstBatch = SyntheticCorpus(count: 20, d0);
        await stack.Store.AppendAsync(ds, firstBatch);
        await stack.InnerEngine.FlushAsync();

        stack.InnerEngine.SegmentCount.Should().BeGreaterThan(0,
            "first batch must have produced at least one segment to exercise cross-boundary");

        // Second batch — stays in memtable (count < capacity) so the scan spans both layers.
        var secondBatch = SyntheticCorpus(count: 5, d0.AddDays(20));
        await stack.Store.AppendAsync(ds, secondBatch);

        var projection = new ColumnProjection(["Close"]);
        var slice = await ToListAsync(stack.Store.GetRangeAsync(ds, d0, d0.AddDays(30), projection));

        slice.Should().HaveCount(25, "cross-boundary range must return every record exactly once");

        // Shape uniformity: only Close populated across the entire stream, regardless of which
        // layer each row came from.
        for (var i = 0; i < slice.Count; i++)
        {
            var bar = slice[i];
            bar.Close.Should().NotBe(0m, $"row {i}: Close is projected — must be populated from source");
            bar.Volume.Should().Be(0L, $"row {i}: Volume is not projected — memtable and segment rows both default it");
            bar.Open.Should().Be(0m);
            bar.TradeCount.Should().Be(0);
        }
    }

    // ColumnProjection.All on the full 12-field corpus returns every field populated. Acts
    // as the baseline: if this test fails, the fixture is broken, not the projection machinery.
    [Fact]
    public async Task FlushedSegment_ColumnProjectionAll_ReturnsAllFields()
    {
        await using var stack = await BuildStore(memTableCapacity: 64);
        var ds = new SerializableWrapper<string>("GOOG");
        var d0 = new DateOnly(2021, 9, 1);
        var corpus = SyntheticCorpus(count: 200, d0);

        await stack.Store.AppendAsync(ds, corpus);
        await stack.InnerEngine.FlushAsync();

        var slice = await ToListAsync(stack.Store.GetRangeAsync(ds, d0, d0.AddDays(200), ColumnProjection.All));

        slice.Should().HaveCount(200);
        for (var i = 0; i < slice.Count; i++)
        {
            var bar = slice[i];
            var expected = corpus[i];
            bar.Should().BeEquivalentTo(expected,
                "ColumnProjection.All must round-trip every field of the 12-field record");
        }
    }
}
