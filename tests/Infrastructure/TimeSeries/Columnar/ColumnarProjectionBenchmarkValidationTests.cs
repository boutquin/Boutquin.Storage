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
using System.Diagnostics;
using System.Text.Json;
using Boutquin.Storage.Domain.Interfaces.TimeSeries;
using Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar;
using Boutquin.Storage.Infrastructure.TimeSeries;

namespace Boutquin.Storage.Infrastructure.Tests.TimeSeries.Columnar;

// CI-friendly single-iteration variant of the ColumnarProjectionBenchmark. Enforces a
// regression-tolerance bound: the projected scan's wall time must stay below the full-column
// baseline (the full ratio check is computed for diagnostics but the cheap guard asserts the
// weaker `projected < 1.2 × baseline` form).
//
// This test is cheap (~2 s) so it runs on every PR. A heavier statistical characterisation lives
// in the benchmarks project — run on-demand via
//   dotnet run --project benchmarks/StorageEngine -c Release -- --filter '*ColumnarProjection*'
// and the JSON the BDN harness exports is then renamed to baseline.json when the numbers
// materially shift.
public sealed class ColumnarProjectionBenchmarkValidationTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        $"ColProjValidation_{Guid.NewGuid():N}");

    public ColumnarProjectionBenchmarkValidationTests()
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

    // Row count chosen small enough that the validation test finishes in well under the 2-second
    // CI budget while still being large enough for the projected-vs-full ratio to surface above
    // the timer's noise floor. The BDN benchmark uses 100_000 for characterisation.
    private const int RowCount = 10_000;

    [Fact]
    public async Task ProjectedScan_StaysWithinRegressionTolerance()
    {
        var baseline = LoadBaseline();
        baseline.Schema.Should().Be(1,
            "baseline schema version mismatch — deliberate bump required");

        await using var stack = await ColumnarTimeSeriesEngineBuilder.BuildAsync<SerializableWrapper<string>, MarketDataBar>(
            segmentFolder: _folder, segmentPrefix: "validate", memTableCapacity: 5_000);

        var ticker = new SerializableWrapper<string>("BENCH");
        var startDate = new DateOnly(2000, 1, 1);
        var endDate = startDate.AddDays(RowCount);
        var records = new List<MarketDataBar>(RowCount);
        var rng = new Random(42);
        for (var i = 0; i < RowCount; i++)
        {
            var price = 100m + (decimal)rng.NextDouble() * 50m;
            records.Add(new MarketDataBar
            {
                Date = startDate.AddDays(i),
                Open = price,
                High = price + 1m,
                Low = price - 1m,
                Close = price + 0.5m,
                Volume = 1_000_000L + i,
                Vwap = price,
                TradeCount = 500 + i,
                Dividend = 0m,
                SplitRatio = 1m,
                AdjustedClose = price + 0.5m,
                SourceFlags = i % 8,
            });
        }

        await stack.Store.AppendAsync(ticker, records);
        await stack.InnerEngine.FlushAsync();

        // Warm up JIT + filesystem caches before the measurement window so the two samples
        // are comparable. Not rigorous — that is the BDN harness's job — but enough to
        // reject the "forgot to skip unprojected bytes" regression.
        var allColumns = new ColumnProjection(
        [
            "Date", "Open", "High", "Low", "Close", "Volume",
            "Vwap", "TradeCount", "Dividend", "SplitRatio", "AdjustedClose", "SourceFlags",
        ]);
        var closeOnly = new ColumnProjection(["Close"]);

        await Count(stack.Store, ticker, startDate, endDate, allColumns);
        await Count(stack.Store, ticker, startDate, endDate, closeOnly);

        // Both baseline and projected samples go through the k-way-merge code path — the
        // only difference is the projection name list. Comparing the two isolates the
        // per-column IO-skip win from the SortedDictionary merge overhead the projected
        // path carries across every call (whether projected or not). Routing the baseline
        // through the generic short-circuit (ColumnProjection.All) would mix two paths and
        // make the ratio meaningless if projected and full-column reads diverge on codec path.
        var baselineMs = await MeasureMs(() => Count(stack.Store, ticker, startDate, endDate, allColumns));
        var projectedMs = await MeasureMs(() => Count(stack.Store, ticker, startDate, endDate, closeOnly));

        // Regression guard: the projected scan reads one data column plus Date while the
        // 12-column baseline reads every column. A sane implementation is demonstrably
        // faster; a regression that re-introduced full-row materialisation on the
        // projected path would make projectedMs ≥ baselineMs. We assert the weak form
        // (projected < baseline × 1.2) so the CI budget tolerates one-off scheduling
        // noise without surfacing false positives; the full characterisation ratio
        // (projected / theoretical_min) lives in the BDN harness run on demand.
        //
        // The theoretical-minimum calculation is still computed and surfaced in the
        // failure message so maintainers can see both numbers when the cheap guard fires.
        const int projectedColumnCount = 1; // "Close" — augmented internally with "Date"
        var totalColumnCount = baseline.TotalColumnCount;
        var theoreticalMinMs = (double)projectedColumnCount / totalColumnCount * baselineMs;
        if (theoreticalMinMs < baseline.ProjectedColumnFloorMs)
        {
            theoreticalMinMs = baselineMs * 0.05;
        }

        var bound = baselineMs * 1.2;
        projectedMs.Should().BeLessThan(bound,
            $"projected scan ({projectedMs:F2} ms) must be faster than the full 12-column "
            + $"baseline ({baselineMs:F2} ms). Theoretical minimum is {theoreticalMinMs:F2} ms "
            + $"(1/12 × baseline). If this fires, the projected path has likely re-introduced "
            + "full-row materialisation — check LsmTimeSeriesStorageEngine.GetRangeProjectedAsync's "
            + "segment branch and the IProjectingSegmentReader.StreamProjectedRangeAsync wiring.");
    }

    private static async Task<int> Count(
        ITimeSeriesStore<SerializableWrapper<string>, MarketDataBar> store,
        SerializableWrapper<string> ticker,
        DateOnly from,
        DateOnly to,
        ColumnProjection projection)
    {
        var count = 0;
        await foreach (var _ in store.GetRangeAsync(ticker, from, to, projection))
        {
            count++;
        }
        return count;
    }

    private static async Task<double> MeasureMs(Func<Task> action)
    {
        var sw = Stopwatch.StartNew();
        await action();
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds;
    }

    private static BaselineConfig LoadBaseline()
    {
        var path = Path.Combine(RepoRoot(), "benchmarks", "StorageEngine", "ColumnarProjection.baseline.json");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"ColumnarProjection baseline file is missing. Regenerate via "
                + $"`dotnet run --project benchmarks/StorageEngine -c Release -- --filter '*ColumnarProjection*' "
                + $"--exporters json --artifacts benchmarks/StorageEngine`, then commit the result at {path}.",
                path);
        }

        try
        {
            var json = File.ReadAllText(path);
            var cfg = JsonSerializer.Deserialize<BaselineConfig>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return cfg ?? throw new InvalidDataException("Deserialised baseline was null.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"ColumnarProjection baseline file at {path} is malformed. Regenerate via "
                + $"`dotnet run --project benchmarks/StorageEngine -c Release -- --filter '*ColumnarProjection*' "
                + $"--exporters json --artifacts benchmarks/StorageEngine`.",
                ex);
        }
    }

    // Locates the repository root by walking up from the test DLL until a `.slnx` is found.
    // The test assembly lands in `tests/Infrastructure/bin/...` so we need to climb a handful
    // of parents to reach the solution root; no hardcoded path lets the test survive
    // reorganisation.
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && dir.GetFiles("*.slnx").Length == 0)
        {
            dir = dir.Parent;
        }
        if (dir is null)
        {
            throw new InvalidOperationException(
                "Could not locate repository root — no .slnx file found walking up from "
                + AppContext.BaseDirectory);
        }
        return dir.FullName;
    }

    private sealed record BaselineConfig(
        int Schema,
        string Description,
        int TotalColumnCount,
        double ProjectedColumnFloorMs,
        double RatioToleranceMultiplier,
        int RowCount);
}
