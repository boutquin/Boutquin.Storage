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

using Boutquin.Storage.Domain.Interfaces.TimeSeries.Columnar;
using Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar;
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar;

namespace Boutquin.Storage.Infrastructure.Tests.TimeSeries.Columnar;

/// <summary>
/// P3.3 vectorisation-ready read surface — <see cref="IColumnarSegmentFormat{TRecord}.ReadBatchesAsync"/>.
/// </summary>
public sealed class ColumnBatchTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    private static async Task<MemoryStream> WriteSegmentAsync(IReadOnlyList<MarketDataBar> bars)
    {
        var format = new CompressedColumnarSegmentFormat<MarketDataBar>();
        var ms = new MemoryStream();
        await format.WriteAsync(ms, bars);
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public async Task ReadBatchesAsync_YieldsBatches_OnePerRowGroup()
    {
        var bars = Enumerable.Range(1, 20)
            .Select(i => new MarketDataBar
            {
                Date = D(2024, 1, i),
                Close = i * 10m,
                SplitRatio = 1m
            })
            .ToList();

        // Small row group = 5 rows → expect 4 batches.
        var format = new CompressedColumnarSegmentFormat<MarketDataBar>(rowGroupMaxRows: 5);
        var ms = new MemoryStream();
        await format.WriteAsync(ms, bars);
        ms.Position = 0;

        var batches = new List<ColumnBatch<MarketDataBar>>();
        await foreach (var batch in format.ReadBatchesAsync(ms, D(2024, 1, 1), D(2024, 1, 20), ColumnProjection.All))
        {
            batches.Add(batch);
        }

        batches.Should().HaveCount(4);
        batches.Sum(b => b.RowCount).Should().Be(20);
    }

    [Fact]
    public async Task ReadBatchesAsync_ExposesColumnArraysKeyedByName()
    {
        var bars = new[]
        {
            new MarketDataBar { Date = D(2024, 1, 1), Close = 100m, SplitRatio = 1m },
            new MarketDataBar { Date = D(2024, 1, 2), Close = 110m, SplitRatio = 1m },
            new MarketDataBar { Date = D(2024, 1, 3), Close = 120m, SplitRatio = 1m },
        };

        var ms = await WriteSegmentAsync(bars);
        var format = new CompressedColumnarSegmentFormat<MarketDataBar>();

        var batches = new List<ColumnBatch<MarketDataBar>>();
        await foreach (var b in format.ReadBatchesAsync(ms, D(2024, 1, 1), D(2024, 1, 3), ColumnProjection.All))
        {
            batches.Add(b);
        }

        batches.Should().HaveCount(1);
        var batch = batches[0];
        batch.RowCount.Should().Be(3);
        batch.Columns.Should().ContainKey("Date");
        batch.Columns.Should().ContainKey("Close");

        var dates = (DateOnly[])batch.Columns["Date"];
        dates.Should().BeEquivalentTo([D(2024, 1, 1), D(2024, 1, 2), D(2024, 1, 3)]);

        var closes = (decimal[])batch.Columns["Close"];
        closes.Should().BeEquivalentTo([100m, 110m, 120m]);
    }

    [Fact]
    public async Task ReadBatchesAsync_CarriesDateRange()
    {
        var bars = new[]
        {
            new MarketDataBar { Date = D(2024, 3, 1), Close = 50m, SplitRatio = 1m },
            new MarketDataBar { Date = D(2024, 3, 15), Close = 60m, SplitRatio = 1m },
        };

        var ms = await WriteSegmentAsync(bars);
        var format = new CompressedColumnarSegmentFormat<MarketDataBar>();

        ColumnBatch<MarketDataBar>? batch = null;
        await foreach (var b in format.ReadBatchesAsync(ms, D(2024, 1, 1), D(2024, 12, 31), ColumnProjection.All))
        {
            batch = b;
        }

        batch.Should().NotBeNull();
        batch!.MinDate.Should().Be(D(2024, 3, 1));
        batch.MaxDate.Should().Be(D(2024, 3, 15));
    }

    [Fact]
    public async Task ReadBatchesAsync_YieldsNothing_WhenFromGtTo()
    {
        var bars = new[]
        {
            new MarketDataBar { Date = D(2024, 1, 1), Close = 100m, SplitRatio = 1m },
        };

        var ms = await WriteSegmentAsync(bars);
        var format = new CompressedColumnarSegmentFormat<MarketDataBar>();

        var count = 0;
        await foreach (var _ in format.ReadBatchesAsync(ms, D(2024, 12, 31), D(2024, 1, 1), ColumnProjection.All))
        {
            count++;
        }

        count.Should().Be(0);
    }

    [Fact]
    public async Task ReadAsync_RemainsCorrect_AfterRefactoringToBatchWrapper()
    {
        var bars = Enumerable.Range(1, 10)
            .Select(i => new MarketDataBar
            {
                Date = D(2024, 2, i),
                Close = i * 5m,
                SplitRatio = 1m
            })
            .ToList();

        var ms = await WriteSegmentAsync(bars);
        var format = new CompressedColumnarSegmentFormat<MarketDataBar>();

        var records = new List<MarketDataBar>();
        await foreach (var r in format.ReadAsync(ms, D(2024, 2, 1), D(2024, 2, 10), ColumnProjection.All))
        {
            records.Add(r);
        }

        records.Should().HaveCount(10);
        for (var i = 0; i < 10; i++)
        {
            records[i].Date.Should().Be(D(2024, 2, i + 1));
            records[i].Close.Should().Be((i + 1) * 5m);
        }
    }
}
