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
using System.Text.Json;
using Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar;
using Boutquin.Storage.Infrastructure.Analytics;
using Boutquin.Storage.Infrastructure.Tests.TimeSeries.Columnar;
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar;

namespace Boutquin.Storage.Infrastructure.Tests.OssComparison;

public sealed class ColumnarSqliteQueryTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(17)]
    [InlineData(512)]
    public async Task PredicatesProjectionAndExactAggregates_AgreeWithSqliteAcrossRowGroupBoundaries(int groupSize)
    {
        using var asset = typeof(ColumnarSqliteQueryTests).Assembly.GetManifestResourceStream("StorageOracle.columnar-sqlite-oracle.json")!;
        using var fixture = JsonDocument.Parse(asset);
        var start = new DateOnly(2020, 1, 1);
        var rows = fixture.RootElement.GetProperty("rows").EnumerateArray().Select(row => new ColumnarBar
        {
            Date = start.AddDays(row.GetProperty("day").GetInt32()),
            Close = row.GetProperty("cents").GetInt32() / 100m,
            Volume = row.GetProperty("volume").GetInt64(),
            TradeCount = row.GetProperty("trades").GetInt32(),
            Ticker = row.GetProperty("ticker").GetString()!,
            IsAdjusted = row.GetProperty("adjusted").GetBoolean(),
            Vwap = row.GetProperty("cents").GetInt32() / 8.0,
            Residual = row.GetProperty("day").GetInt32() / 16.0
        }).ToArray();
        var format = new CompressedColumnarSegmentFormat<ColumnarBar>(rowGroupMaxRows: groupSize);
        using var encoded = new MemoryStream();
        await format.WriteAsync(encoded, rows).ConfigureAwait(true);
        var bytes = encoded.ToArray();
        foreach (var query in fixture.RootElement.GetProperty("cases").EnumerateArray())
        {
            var field = query.GetProperty("field").GetString()!;
            var column = field switch { "cents" => "Close", "volume" => "Volume", "trades" => "TradeCount", "ticker" => "Ticker", _ => "IsAdjusted" };
            var bound = query.GetProperty("bound");
            object value = field switch
            {
                "cents" => bound.GetInt32() / 100m,
                "volume" => bound.GetInt64(),
                "trades" => bound.GetInt32(),
                "ticker" => bound.GetString()!,
                _ => bound.GetInt32() == 1
            };
            var op = query.GetProperty("operator").GetString() switch
            {
                "=" => ComparisonOperator.Equal,
                "!=" => ComparisonOperator.NotEqual,
                "<" => ComparisonOperator.LessThan,
                "<=" => ComparisonOperator.LessThanOrEqual,
                ">" => ComparisonOperator.GreaterThan,
                _ => ComparisonOperator.GreaterThanOrEqual
            };
            var from = start.AddDays(query.GetProperty("lower").GetInt32());
            var to = start.AddDays(query.GetProperty("upper").GetInt32());
            using var input = new MemoryStream(bytes, false);
            var actual = new List<ColumnarBar>();
            await foreach (var row in format.StreamWhereAsync(input, from, to,
                new ColumnProjection(["Close", "Volume"]), [new ColumnPredicate(column, op, value)]).ConfigureAwait(true))
            {
                actual.Add(row);
            }
            actual.Select(row => row.Date.DayNumber - start.DayNumber).Should().Equal(
                query.GetProperty("days").EnumerateArray().Select(day => day.GetInt32()), query.GetProperty("sql").GetString());
            actual.Sum(row => row.Close).Should().Be(query.GetProperty("cents_sum").GetInt64() / 100m);
            actual.Sum(row => row.Volume).Should().Be(query.GetProperty("volume_sum").GetInt64());
            actual.Should().OnlyContain(row => row.Vwap == 0 && row.Residual == 0,
                "only requested, date and predicate columns are materialised");
        }

        // Independent exact decimal fold, min/max and count over decoded batches. ReadBatches
        // admits boundary rows, so the explicit date filter is part of the production composition.
        var lower = start.AddDays(3);
        var upper = start.AddDays(171);
        var expected = rows.Where(row => row.Date >= lower && row.Date <= upper && row.Close > 0).ToArray();
        decimal sum = 0;
        var count = 0;
        decimal? min = null;
        decimal? max = null;
        using var batches = new MemoryStream(bytes, false);
        await foreach (var batch in format.ReadBatchesAsync(batches, lower, upper, ColumnProjection.All).ConfigureAwait(true))
        {
            var window = new FilterOperator<ColumnarBar, DateOnly>("Date", date => date >= lower && date <= upper).Execute(batch);
            var positive = new FilterOperator<ColumnarBar, decimal>("Close", close => close > 0).Execute(window);
            var projected = new ProjectionOperator<ColumnarBar>(["Close"]).Execute(positive);
            sum += new AggregateOperator<ColumnarBar, decimal, decimal>("Close", 0m, (total, close) => total + close).Execute(projected);
            count += projected.RowCount;
            min = new AggregateOperator<ColumnarBar, decimal, decimal?>("Close", min, (best, close) => best is null ? close : Math.Min(best.Value, close)).Execute(projected);
            max = new AggregateOperator<ColumnarBar, decimal, decimal?>("Close", max, (best, close) => best is null ? close : Math.Max(best.Value, close)).Execute(projected);
        }
        sum.Should().Be(expected.Sum(row => row.Close));
        count.Should().Be(expected.Length);
        min.Should().Be(expected.Min(row => row.Close));
        max.Should().Be(expected.Max(row => row.Close));
    }
}
