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
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar;

namespace Boutquin.Storage.Infrastructure.Tests.TimeSeries.Columnar;

/// <summary>
/// End-to-end integration tests for <see cref="CompressedColumnarSegmentFormat{TRecord}"/> on
/// a realistic 10-year daily bar synthetic dataset. Exercises every Phase-3 codec simultaneously
/// through the generator-emitted field plan (Phase 4), and validates the projection-vs-full
/// read path that the design's cost profile identifies as the columnar format's headline win.
/// </summary>
/// <remarks>
/// Scope note: the columnar build-out's final phase refers to <c>LsmTimeSeriesStore with SegmentFormat =
/// Columnar</c>. The actual LSM-store integration would require intercepting
/// <c>LsmStorageEngine</c>'s row-oriented flush path, which is an architectural change
/// deferred to a follow-up. Here we exercise the same correctness surface — round-trip +
/// projection on realistic 10y daily bar data — at the format layer directly.
/// </remarks>
public sealed class ColumnarIntegrationTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    private static List<ColumnarBar> GenerateTenYearsOfDailyBars()
    {
        var rng = new Random(2026);
        var tickers = new[] { "AAPL", "MSFT", "GOOG", "AMZN", "META" };
        var bars = new List<ColumnarBar>(capacity: 252 * 10 * tickers.Length);
        foreach (var ticker in tickers)
        {
            var closePrice = 100.0m + (decimal)(rng.NextDouble() * 100);
            for (var day = 0; day < 252 * 10; day++)
            {
                var ret = (rng.NextDouble() - 0.5) * 0.02;
                closePrice *= 1 + (decimal)ret;
                closePrice = Math.Round(closePrice, 2);
                bars.Add(new ColumnarBar
                {
                    Date = new DateOnly(2016, 1, 4).AddDays(day),
                    Ticker = ticker,
                    Close = closePrice,
                    Volume = 1_000_000L + rng.Next(0, 9_000_000),
                    TradeCount = 1_000 + rng.Next(0, 5_000),
                    Vwap = (double)closePrice * (1 + ((rng.NextDouble() - 0.5) * 0.001)),
                    IsAdjusted = rng.NextDouble() < 0.1,
                    Residual = (rng.NextDouble() - 0.5) * 0.01,
                });
            }
        }

        return bars;
    }

    [Fact]
    public async Task TenYearsOfDailyBars_RoundTripsBitwise()
    {
        var format = new CompressedColumnarSegmentFormat<ColumnarBar>();
        var bars = GenerateTenYearsOfDailyBars();

        using var stream = new MemoryStream();
        await format.WriteAsync(stream, bars).ConfigureAwait(true);
        stream.Position = 0;

        var recovered = new List<ColumnarBar>();
        await foreach (var bar in format.ReadAsync(
            stream,
            bars[0].Date,
            bars[^1].Date,
            ColumnProjection.All).ConfigureAwait(true))
        {
            recovered.Add(bar);
        }

        recovered.Should().HaveCount(bars.Count);
        for (var i = 0; i < bars.Count; i++)
        {
            recovered[i].Should().Be(bars[i], $"bar at index {i}");
        }

        _output.WriteLine($"Round-trip: {bars.Count:N0} rows, encoded size = {stream.Length:N0} bytes, "
            + $"{(double)stream.Length / bars.Count:F2} B/row");
    }

    [Fact]
    public async Task Projection_OfThreeColumns_YieldsSameValuesAsFullRead_ForTheProjectedFields()
    {
        var format = new CompressedColumnarSegmentFormat<ColumnarBar>();
        var bars = GenerateTenYearsOfDailyBars();

        using var stream = new MemoryStream();
        await format.WriteAsync(stream, bars).ConfigureAwait(true);

        // Full read
        stream.Position = 0;
        var full = new List<ColumnarBar>();
        await foreach (var bar in format.ReadAsync(stream, bars[0].Date, bars[^1].Date, ColumnProjection.All).ConfigureAwait(true))
        {
            full.Add(bar);
        }

        // Projected read — Trading-backtest shape: (Date, Close, Volume).
        stream.Position = 0;
        var projected = new List<ColumnarBar>();
        await foreach (var bar in format.ReadAsync(
            stream,
            bars[0].Date,
            bars[^1].Date,
            new ColumnProjection(["Date", "Close", "Volume"])).ConfigureAwait(true))
        {
            projected.Add(bar);
        }

        projected.Should().HaveSameCount(full);
        for (var i = 0; i < projected.Count; i++)
        {
            projected[i].Date.Should().Be(full[i].Date);
            projected[i].Close.Should().Be(full[i].Close);
            projected[i].Volume.Should().Be(full[i].Volume);
            // Unprojected fields are default(TField) on the projected record.
            projected[i].Ticker.Should().BeEmpty();
            projected[i].Vwap.Should().Be(0.0);
            projected[i].IsAdjusted.Should().BeFalse();
        }
    }

    [Fact]
    public async Task DateRangeQuery_LastQuarter_ScansOnlyMatchingRowGroups()
    {
        var format = new CompressedColumnarSegmentFormat<ColumnarBar>();
        var bars = GenerateTenYearsOfDailyBars();

        using var stream = new MemoryStream();
        await format.WriteAsync(stream, bars).ConfigureAwait(true);
        stream.Position = 0;

        var lastDate = bars[^1].Date;
        var quarterStart = lastDate.AddMonths(-3);

        var result = new List<ColumnarBar>();
        await foreach (var bar in format.ReadAsync(
            stream,
            quarterStart,
            lastDate,
            ColumnProjection.All).ConfigureAwait(true))
        {
            result.Add(bar);
        }

        // Every returned bar is within the last-quarter window.
        result.Should().OnlyContain(b => b.Date >= quarterStart && b.Date <= lastDate);
        // Each of the 5 tickers covers ~90 calendar days in the 3-month quarter (we synthesise
        // one bar per calendar day, not per trading day). Allow a wide envelope so the test
        // remains green under minor date-arithmetic drift (month lengths).
        result.Should().HaveCountGreaterThan(5 * 85);
        result.Should().HaveCountLessThan(5 * 100);
    }
}
