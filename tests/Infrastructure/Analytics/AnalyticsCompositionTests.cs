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

using System.Text.Json;
using Boutquin.Storage.Domain.ValueObjects.Analytics;
using Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar;
using Boutquin.Storage.Infrastructure.Analytics;
using Boutquin.Storage.Infrastructure.Events;
using Boutquin.Storage.Infrastructure.ObjectStore;
using Boutquin.Storage.Infrastructure.Streaming.Checkpointing;
using Boutquin.Storage.Infrastructure.Tests.TimeSeries.Columnar;
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar;

namespace Boutquin.Storage.Infrastructure.Tests.Analytics;

public sealed class AnalyticsCompositionTests
{
    [Fact]
    public void DataCube_ProducesEveryGroupingSetIncludingGrandTotal()
    {
        Sale[] sales = [new("CA", "Book", 10m), new("CA", "Game", 20m), new("US", "Book", 7m)];
        var builder = new DataCubeBuilder<Sale, decimal>(
            [new CubeDimension<Sale>("Country", sale => sale.Country),
             new CubeDimension<Sale>("Product", sale => sale.Product)],
            sale => sale.Amount, 0m, (sum, amount) => sum + amount);

        var cells = builder.Build(sales);

        cells.Should().HaveCount(8);
        cells.Single(cell => cell.Dimensions.Count == 0).Value.Should().Be(37m);
        cells.Single(cell => cell.Dimensions.Count == 1
                && cell.Dimensions.TryGetValue("Country", out var country) && Equals(country, "CA"))
            .Value.Should().Be(30m);
        cells.Single(cell => cell.Dimensions.Count == 2
                && cell.Dimensions.TryGetValue("Country", out var country) && Equals(country, "US"))
            .Value.Should().Be(7m);
    }

    [Fact]
    public void DataCube_EmptyInput_ReturnsSeededGrandTotal()
    {
        var builder = new DataCubeBuilder<Sale, decimal>(
            [new CubeDimension<Sale>("Country", sale => sale.Country)],
            sale => sale.Amount, 0m, (sum, amount) => sum + amount);

        var cells = builder.Build([]);

        cells.Should().ContainSingle();
        cells[0].Dimensions.Should().BeEmpty();
        cells[0].Value.Should().Be(0m);
        cells[0].RowCount.Should().Be(0);
    }

    [Fact]
    public async Task MaterializedView_InterruptedRefresh_ReplaysToSameStateAsRebuild()
    {
        var log = new InMemoryEventLog<KeyValuePair<string, decimal>>();
        await log.AppendAsync(new("CA", 1.10m));
        await log.AppendAsync(new("US", 2.20m));
        await log.AppendAsync(new("CA", 3.30m));
        var durable = new InMemoryCheckpointStore();
        var failing = new FailOnceCheckpointStore(durable, failAtOffset: 1);
        var interrupted = View(log, failing);

        await interrupted.Invoking(view => view.RefreshAsync().AsTask()).Should().ThrowAsync<IOException>();

        var restarted = View(log, durable);
        var refreshed = await restarted.RefreshAsync();
        var rebuilt = await View(log, new InMemoryCheckpointStore()).RebuildAsync();
        refreshed.Should().BeEquivalentTo(rebuilt);
        refreshed.Position.Should().Be(2);
        refreshed.State.Should().BeEquivalentTo(new Dictionary<string, decimal> { ["CA"] = 4.40m, ["US"] = 2.20m });
    }

    [Fact]
    public async Task ObjectStoreReader_ReadsRealColumnarSegmentAndStrictFilterRemovesEdgeRows()
    {
        var format = new CompressedColumnarSegmentFormat<ColumnarBar>(rowGroupMaxRows: 10);
        var rows = new[]
        {
            new ColumnarBar { Date = new DateOnly(2026, 1, 1), Ticker = "A", Close = 1m },
            new ColumnarBar { Date = new DateOnly(2026, 1, 2), Ticker = "A", Close = 2m },
            new ColumnarBar { Date = new DateOnly(2026, 1, 3), Ticker = "A", Close = 3m },
        };
        await using var encoded = new MemoryStream();
        await format.WriteAsync(encoded, rows);
        encoded.Position = 0;
        var store = new InMemoryObjectStore();
        await store.WriteAsync("analytics/bars.col", encoded);
        var reader = new ColumnarObjectStoreReader<ColumnarBar>(store, format);
        var batches = new List<ColumnBatch<ColumnarBar>>();

        await foreach (var batch in reader.ReadBatchesAsync("analytics/bars.col",
            new DateOnly(2026, 1, 2), new DateOnly(2026, 1, 2), ColumnProjection.All))
        {
            batches.Add(new FilterOperator<ColumnarBar, DateOnly>("Date", date => date == new DateOnly(2026, 1, 2))
                .Execute(batch));
        }

        batches.Should().ContainSingle();
        batches[0].RowCount.Should().Be(1);
        batches[0].Columns["Close"].Should().BeOfType<decimal[]>().Which.Should().Equal(2m);
    }

    [Fact]
    public async Task ObjectStoreReader_MissingObject_ThrowsFileNotFound()
    {
        var reader = new ColumnarObjectStoreReader<ColumnarBar>(new InMemoryObjectStore(),
            new CompressedColumnarSegmentFormat<ColumnarBar>());
        var enumerate = async () =>
        {
            await foreach (var _ in reader.ReadBatchesAsync("missing", DateOnly.MinValue, DateOnly.MaxValue,
                ColumnProjection.All)) { }
        };
        await enumerate.Should().ThrowAsync<FileNotFoundException>();
    }

    private static MaterializedAggregateView<string, decimal> View(
        InMemoryEventLog<KeyValuePair<string, decimal>> log,
        Domain.Interfaces.Streaming.ICheckpointStore checkpoints) =>
        new("sales", log, checkpoints, (left, right) => left + right,
            state => JsonSerializer.SerializeToUtf8Bytes(state),
            bytes => JsonSerializer.Deserialize<Dictionary<string, decimal>>(bytes)
                ?? throw new InvalidDataException("Missing aggregate state."));

    private sealed record Sale(string Country, string Product, decimal Amount);

    private sealed class FailOnceCheckpointStore(
        Domain.Interfaces.Streaming.ICheckpointStore inner, long failAtOffset)
        : Domain.Interfaces.Streaming.ICheckpointStore
    {
        private int _failed;
        public ValueTask<(long Offset, byte[] State)?> LoadAsync(string processorId, CancellationToken ct = default) =>
            inner.LoadAsync(processorId, ct);

        public ValueTask SaveAsync(string processorId, long offset, byte[] state, CancellationToken ct = default)
        {
            if (offset == failAtOffset && Interlocked.Exchange(ref _failed, 1) == 0)
            {
                throw new IOException("Injected checkpoint interruption.");
            }
            return inner.SaveAsync(processorId, offset, state, ct);
        }
    }
}
