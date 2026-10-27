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

using Boutquin.Storage.Domain.Attributes;
using Boutquin.Storage.Domain.Interfaces.TimeSeries;
using Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar;
using Boutquin.Storage.Infrastructure.Analytics;
using Boutquin.Storage.Infrastructure.ObjectStore;
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar;

namespace Boutquin.Storage.Samples;

/// <summary>Demonstrates a strict date filter over column batches stored in an object store.</summary>
internal static class AnalyticsDemo
{
    public static async Task RunAsync()
    {
        var records = new[]
        {
            new AnalyticsBar(new DateOnly(2026, 1, 1), "CA", 10.10m),
            new AnalyticsBar(new DateOnly(2026, 1, 2), "CA", 20.20m),
            new AnalyticsBar(new DateOnly(2026, 1, 3), "US", 30.30m),
        };
        var format = new CompressedColumnarSegmentFormat<AnalyticsBar>(rowGroupMaxRows: 128);
        await using var encoded = new MemoryStream();
        await format.WriteAsync(encoded, records).ConfigureAwait(false);
        encoded.Position = 0;

        var objectStore = new InMemoryObjectStore();
        await objectStore.WriteAsync("analytics/daily-bars.col", encoded).ConfigureAwait(false);
        var reader = new ColumnarObjectStoreReader<AnalyticsBar>(objectStore, format);
        var from = new DateOnly(2026, 1, 2);
        var to = new DateOnly(2026, 1, 2);
        var total = 0m;

        await foreach (var rowGroup in reader.ReadBatchesAsync(
            "analytics/daily-bars.col", from, to, ColumnProjection.All).ConfigureAwait(false))
        {
            // Row-group pruning is coarse at range boundaries. This batch predicate makes the
            // requested inclusive range exact before aggregation.
            var strict = new FilterOperator<AnalyticsBar, DateOnly>("Date", date => date >= from && date <= to)
                .Execute(rowGroup);
            total += new AggregateOperator<AnalyticsBar, decimal, decimal>(
                "Amount", 0m, static (sum, amount) => sum + amount).Execute(strict);
        }

        Console.WriteLine($"Strict inclusive range {from:yyyy-MM-dd}..{to:yyyy-MM-dd}: {total}");
    }
}

/// <summary>Columnar sample row used by <see cref="AnalyticsDemo"/>.</summary>
[ColumnarStorable]
public sealed partial record AnalyticsBar : IHasDate
{
    public DateOnly Date { get; init; }
    public string Country { get; init; } = "";
    public decimal Amount { get; init; }

    public AnalyticsBar() { }

    public AnalyticsBar(DateOnly date, string country, decimal amount)
    {
        Date = date;
        Country = country;
        Amount = amount;
    }
}
