// Copyright (c) 2024-2026 Pierre G. Boutquin. All rights reserved.
//
//   Licensed under the Apache License, Version 2.0 (the "License").
//   You may not use this file except in compliance with the License.
//   You may obtain a copy of the License at
//
//       http://www.apache.org/licenses/LICENSE-2.0
//
//   Unless required by applicable law or agreed to in writing, software
//   distributed under the License is distributed on an "AS IS" BASIS,
//   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//
//   See the License for the specific language governing permissions and
//   limitations under the License.
//

using Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar;
using Boutquin.Storage.Infrastructure.Analytics;
using Boutquin.Storage.Infrastructure.Tests.TimeSeries.Columnar;

namespace Boutquin.Storage.Infrastructure.Tests.Analytics;

public sealed class AnalyticsOperatorTests
{
    [Fact]
    public void Filter_ProcessesWholeBatchAndPreservesTypedColumns()
    {
        var batch = Batch(
            [new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 2), new DateOnly(2026, 1, 3)],
            [1.10m, 2.20m, 3.30m],
            ["a", null!, "c"]);

        var filtered = new FilterOperator<ColumnarBar, decimal>("Close", value => value >= 2m)
            .Execute(batch);

        filtered.RowCount.Should().Be(2);
        filtered.Columns["Date"].Should().BeOfType<DateOnly[]>()
            .Which.Should().Equal(new DateOnly(2026, 1, 2), new DateOnly(2026, 1, 3));
        filtered.Columns["Close"].Should().BeOfType<decimal[]>().Which.Should().Equal(2.20m, 3.30m);
        filtered.Columns["Ticker"].Should().BeOfType<string[]>().Which.Should().Equal(null, "c");
    }

    [Fact]
    public void Filter_NoMatches_ReturnsEmptyTypedArraysAndStableDates()
    {
        var batch = Batch([new DateOnly(2026, 1, 1)], [1m], ["a"]);
        var filtered = new FilterOperator<ColumnarBar, decimal>("Close", value => value > 10m)
            .Execute(batch);

        filtered.RowCount.Should().Be(0);
        filtered.MinDate.Should().Be(batch.MinDate);
        filtered.MaxDate.Should().Be(batch.MaxDate);
        filtered.Columns["Close"].Should().BeOfType<decimal[]>().Which.Should().BeEmpty();
    }

    [Fact]
    public void Projection_SelectsColumnsWithoutChangingExactValues()
    {
        var batch = Batch([new DateOnly(2026, 1, 1)], [1.2300m], ["a"]);
        var projected = new ProjectionOperator<ColumnarBar>(["Date", "Close"]).Execute(batch);

        projected.Columns.Keys.Should().Equal("Date", "Close");
        projected.Columns["Close"].Should().BeOfType<decimal[]>().Which.Single().Should().Be(1.2300m);
    }

    [Fact]
    public void Aggregate_UsesTypedReducerAndSeedForEmptyBatch()
    {
        var batch = Batch([new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 2)], [0.1m, 0.2m], ["a", "b"]);
        var aggregate = new AggregateOperator<ColumnarBar, decimal, decimal>("Close", 0m, (sum, value) => sum + value);

        aggregate.Execute(batch).Should().Be(0.3m);
        aggregate.Execute(Batch([], [], [])).Should().Be(0m);
    }

    [Fact]
    public void Aggregate_PassesNullElementsToCallerReducer()
    {
        var batch = Batch(
            [new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 2), new DateOnly(2026, 1, 3)],
            [1m, 2m, 3m],
            ["a", null!, "c"]);
        var nonNullCount = new AggregateOperator<ColumnarBar, string, int>(
            "Ticker", 0, static (count, ticker) => ticker is null ? count : count + 1);

        nonNullCount.Execute(batch).Should().Be(2);
    }

    [Fact]
    public void Operators_RejectMissingWrongTypedAndLengthMismatchedColumns()
    {
        var valid = Batch([new DateOnly(2026, 1, 1)], [1m], ["a"]);
        var missing = () => new FilterOperator<ColumnarBar, decimal>("Missing", _ => true).Execute(valid);
        missing.Should().Throw<ArgumentException>();

        var wrong = () => new FilterOperator<ColumnarBar, long>("Close", _ => true).Execute(valid);
        wrong.Should().Throw<ArgumentException>();

        var malformed = new ColumnBatch<ColumnarBar>(2,
            new Dictionary<string, Array> { ["Date"] = new[] { new DateOnly(2026, 1, 1) } },
            new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 2));
        var mismatched = () => new ProjectionOperator<ColumnarBar>(["Date"]).Execute(malformed);
        mismatched.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Operators_RejectArrayCovariantElementTypes()
    {
        // The CLR lets int[] pass `is uint[]` and `is SignedColor[]`, and string[] pass `is object[]`;
        // the operators document an exact element type, so each of these must be refused.
        var batch = new ColumnBatch<ColumnarBar>(2,
            new Dictionary<string, Array>
            {
                ["Date"] = new[] { new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 2) },
                ["Volume"] = new[] { -1, -2 },
                ["Ticker"] = new[] { "a", "b" },
            },
            new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 2));

        var unsignedSum = () => new AggregateOperator<ColumnarBar, uint, ulong>("Volume", 0UL, static (sum, value) => sum + value).Execute(batch);
        var unsignedFilter = () => new FilterOperator<ColumnarBar, uint>("Volume", static value => value > 1000U).Execute(batch);
        var enumAggregate = () => new AggregateOperator<ColumnarBar, SignedColor, int>("Volume", 0, static (count, _) => count + 1).Execute(batch);
        var objectFilter = () => new FilterOperator<ColumnarBar, object>("Ticker", static _ => true).Execute(batch);

        unsignedSum.Should().Throw<ArgumentException>();
        unsignedFilter.Should().Throw<ArgumentException>();
        enumAggregate.Should().Throw<ArgumentException>();
        objectFilter.Should().Throw<ArgumentException>();
        new AggregateOperator<ColumnarBar, int, long>("Volume", 0L, static (sum, value) => sum + value).Execute(batch).Should().Be(-3L);
    }

    [Fact]
    public void Operators_RejectNonZeroLowerBoundColumns()
    {
        // Array.CreateInstance with a lower bound of 1 yields a rank-1 String[*] whose GetValue(i)
        // reads logical index i; copying it by zero-based row index would shift every row by one.
        var shifted = Array.CreateInstance(typeof(string), [3], [1]);
        shifted.SetValue("r0", 1);
        shifted.SetValue("r1", 2);
        shifted.SetValue("r2", 3);
        var batch = new ColumnBatch<ColumnarBar>(3,
            new Dictionary<string, Array>
            {
                ["Date"] = new[] { new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 2), new DateOnly(2026, 1, 3) },
                ["Close"] = new[] { 10m, 20m, 30m },
                ["Ticker"] = shifted,
            },
            new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 3));

        var filter = () => new FilterOperator<ColumnarBar, decimal>("Close", static value => value >= 20m).Execute(batch);
        var project = () => new ProjectionOperator<ColumnarBar>(["Ticker"]).Execute(batch);

        filter.Should().Throw<ArgumentException>();
        project.Should().Throw<ArgumentException>();
    }

    private enum SignedColor
    {
        Red = -1,
        Green = -2,
    }

    private static ColumnBatch<ColumnarBar> Batch(DateOnly[] dates, decimal[] closes, string[] tickers)
    {
        var min = dates.Length == 0 ? DateOnly.MinValue : dates.Min();
        var max = dates.Length == 0 ? DateOnly.MinValue : dates.Max();
        return new ColumnBatch<ColumnarBar>(dates.Length,
            new Dictionary<string, Array>
            {
                ["Date"] = dates,
                ["Close"] = closes,
                ["Ticker"] = tickers,
            }, min, max);
    }
}
