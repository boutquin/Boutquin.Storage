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

namespace Boutquin.Storage.BenchMark;

/// <summary>
/// Measures the columnar IO-skip win the k-way-merge range scan unlocks on the public
/// <c>ITimeSeriesStore.GetRangeAsync</c> surface. Three scenarios share a single pre-populated
/// 100,000-row columnar segment keyed to a 12-field
/// <see cref="MarketDataShapedBar"/> record:
/// <list type="bullet">
///   <item><description><c>FullRead</c>: <see cref="ColumnProjection.All"/>; the baseline.</description></item>
///   <item><description><c>CloseOnly</c>: projection <c>["Close"]</c>; one decimal column.</description></item>
///   <item><description><c>CloseAndVolume</c>: projection <c>["Close", "Volume"]</c>.</description></item>
/// </list>
/// <para><b>What this catches.</b> A regression that re-introduces full-row materialisation on
/// the projected path (the Phase-1 post-hoc <c>ProjectTo</c> pass) would show the projected
/// wall time approaching the baseline's. The companion
/// <c>ColumnarProjectionBenchmarkValidationTests</c> is the cheap CI guard that runs a
/// single-iteration variant of each scenario and enforces the regression bound. The full BDN
/// run is the characterisation harness — invoked on-demand via
/// <c>dotnet run --project benchmarks/StorageEngine -c Release -- --filter '*ColumnarProjection*'</c>.</para>
/// </summary>
[MemoryDiagnoser]
public class ColumnarProjectionBenchmark
{
    private string _folder = null!;
    private ColumnarTimeSeriesStack<SerializableWrapper<string>, MarketDataShapedBar>? _stack;
    private SerializableWrapper<string> _ticker = null!;
    private DateOnly _startDate;
    private DateOnly _endDate;

    private const int RowCount = 100_000;

    [GlobalSetup]
    public async Task Setup()
    {
        _folder = Path.Combine(Path.GetTempPath(), $"ColProjBench_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_folder);

        _stack = await ColumnarTimeSeriesEngineBuilder.BuildAsync<SerializableWrapper<string>, MarketDataShapedBar>(
            segmentFolder: _folder,
            segmentPrefix: "projbench",
            memTableCapacity: 10_000).ConfigureAwait(false);

        _ticker = new SerializableWrapper<string>("BENCH");
        _startDate = new DateOnly(2000, 1, 1);
        _endDate = _startDate.AddDays(RowCount);

        var records = new List<MarketDataShapedBar>(RowCount);
        var rng = new Random(42);
        for (var i = 0; i < RowCount; i++)
        {
            var price = 100m + (decimal)rng.NextDouble() * 50m;
            records.Add(new MarketDataShapedBar
            {
                Date = _startDate.AddDays(i),
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
                ExchangeCode = i % 8,
            });
        }

        await _stack.Store.AppendAsync(_ticker, records).ConfigureAwait(false);
        await _stack.InnerEngine.FlushAsync().ConfigureAwait(false);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _stack?.Dispose();
        if (Directory.Exists(_folder))
        {
            try { Directory.Delete(_folder, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Benchmark(Baseline = true)]
    public async Task<int> FullRead_ColumnProjectionAll()
    {
        var count = 0;
        await foreach (var _ in _stack!.Store.GetRangeAsync(_ticker, _startDate, _endDate, ColumnProjection.All).ConfigureAwait(false))
        {
            count++;
        }
        return count;
    }

    [Benchmark]
    public async Task<int> Projected_CloseOnly()
    {
        var projection = new ColumnProjection(["Close"]);
        var count = 0;
        await foreach (var _ in _stack!.Store.GetRangeAsync(_ticker, _startDate, _endDate, projection).ConfigureAwait(false))
        {
            count++;
        }
        return count;
    }

    [Benchmark]
    public async Task<int> Projected_CloseAndVolume()
    {
        var projection = new ColumnProjection(["Close", "Volume"]);
        var count = 0;
        await foreach (var _ in _stack!.Store.GetRangeAsync(_ticker, _startDate, _endDate, projection).ConfigureAwait(false))
        {
            count++;
        }
        return count;
    }

}
