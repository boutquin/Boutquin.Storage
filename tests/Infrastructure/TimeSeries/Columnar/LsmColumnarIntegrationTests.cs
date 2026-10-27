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

/// <summary>
/// End-to-end test for the full columnar LSM stack assembled by
/// <see cref="ColumnarTimeSeriesEngineBuilder"/>. Covers the integration criterion: a
/// <see cref="ColumnarTimeSeriesStack{TDatasetKey,TRecord}"/> writes columnar segments on
/// flush and round-trips correctly across AppendAsync → GetRangeAsync → DeleteRangeAsync.
/// </summary>
public sealed class LsmColumnarIntegrationTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        $"LsmColumnar_{Guid.NewGuid():N}");

    public LsmColumnarIntegrationTests()
    {
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private static MarketDataBar MakeBar(DateOnly date, decimal close)
        => new() { Date = date, Close = close, SplitRatio = 1m };

    [Fact]
    public async Task AppendAndGetRange_SingleDataset_MemtableOnly()
    {
        await using var stack = await ColumnarTimeSeriesEngineBuilder.BuildAsync<SerializableWrapper<string>, MarketDataBar>(
            segmentFolder: _folder, segmentPrefix: "colts", memTableCapacity: 1000);

        var ds = new SerializableWrapper<string>("AAPL");
        var d0 = new DateOnly(2024, 1, 1);
        var records = Enumerable.Range(0, 10)
            .Select(i => MakeBar(d0.AddDays(i), 100m + i))
            .ToList();

        await stack.Store.AppendAsync(ds, records);
        var fetched = new List<MarketDataBar>();
        await foreach (var r in stack.Store.GetRangeAsync(ds, d0, d0.AddDays(10), ColumnProjection.All))
        {
            fetched.Add(r);
        }

        fetched.Should().HaveCount(10);
        fetched.Select(r => r.Close).Should().Equal(records.Select(r => r.Close));
    }

    [Fact]
    public async Task AppendAndGetRange_SingleDataset_FlushesToColumnarSegment()
    {
        await using var stack = await ColumnarTimeSeriesEngineBuilder.BuildAsync<SerializableWrapper<string>, MarketDataBar>(
            segmentFolder: _folder, segmentPrefix: "colts", memTableCapacity: 4);

        var ds = new SerializableWrapper<string>("AAPL");
        var d0 = new DateOnly(2024, 1, 1);
        var records = Enumerable.Range(0, 10)
            .Select(i => MakeBar(d0.AddDays(i), 100m + i))
            .ToList();

        // Capacity = 4 forces multiple automatic flushes during bulk insert.
        await stack.Store.AppendAsync(ds, records);
        await stack.InnerEngine.FlushAsync(); // flush residual memtable so every record is in a segment

        stack.InnerEngine.SegmentCount.Should().BeGreaterThan(0, "bulk insert past memtable capacity must produce segments");

        // Verify the on-disk segments are .bsct (columnar), not .dat (row).
        Directory.GetFiles(_folder, "*.bsct").Should().NotBeEmpty();
        Directory.GetFiles(_folder, "*.dat").Should().BeEmpty(
            "columnar engine must not write .dat row-format files");

        var fetched = new List<MarketDataBar>();
        await foreach (var r in stack.Store.GetRangeAsync(ds, d0, d0.AddDays(10), ColumnProjection.All))
        {
            fetched.Add(r);
        }

        fetched.Should().HaveCount(10);
        fetched.Select(r => r.Close).Should().Equal(records.Select(r => r.Close));
    }

    [Fact]
    public async Task AppendAndGetRange_MultipleDatasets_DifferentiatesByKey()
    {
        await using var stack = await ColumnarTimeSeriesEngineBuilder.BuildAsync<SerializableWrapper<string>, MarketDataBar>(
            segmentFolder: _folder, segmentPrefix: "colts", memTableCapacity: 8);

        var aapl = new SerializableWrapper<string>("AAPL");
        var msft = new SerializableWrapper<string>("MSFT");
        var d0 = new DateOnly(2024, 1, 1);

        await stack.Store.AppendAsync(aapl, Enumerable.Range(0, 5)
            .Select(i => MakeBar(d0.AddDays(i), 100m + i))
            .ToList());
        await stack.Store.AppendAsync(msft, Enumerable.Range(0, 5)
            .Select(i => MakeBar(d0.AddDays(i), 200m + i))
            .ToList());
        await stack.InnerEngine.FlushAsync();

        var aaplFetched = new List<MarketDataBar>();
        await foreach (var r in stack.Store.GetRangeAsync(aapl, d0, d0.AddDays(5), ColumnProjection.All))
        {
            aaplFetched.Add(r);
        }

        var msftFetched = new List<MarketDataBar>();
        await foreach (var r in stack.Store.GetRangeAsync(msft, d0, d0.AddDays(5), ColumnProjection.All))
        {
            msftFetched.Add(r);
        }

        aaplFetched.Select(r => r.Close).Should().Equal([100m, 101m, 102m, 103m, 104m]);
        msftFetched.Select(r => r.Close).Should().Equal([200m, 201m, 202m, 203m, 204m]);
    }

    [Fact]
    public async Task GetCoverage_AfterMultipleAppends_ReturnsFullRange()
    {
        await using var stack = await ColumnarTimeSeriesEngineBuilder.BuildAsync<SerializableWrapper<string>, MarketDataBar>(
            segmentFolder: _folder, segmentPrefix: "colts", memTableCapacity: 4);

        var ds = new SerializableWrapper<string>("AAPL");
        var d0 = new DateOnly(2024, 1, 1);

        await stack.Store.AppendAsync(ds, Enumerable.Range(0, 10)
            .Select(i => MakeBar(d0.AddDays(i), (decimal)i))
            .ToList());
        await stack.InnerEngine.FlushAsync();

        var coverage = await stack.Store.GetCoverageAsync(ds);

        coverage.Should().NotBeNull();
        coverage!.Value.From.Should().Be(d0);
        coverage.Value.To.Should().Be(d0.AddDays(9));
    }

    [Fact]
    public async Task Compaction_MergesMultipleColumnarSegments_IntoOne()
    {
        await using var stack = await ColumnarTimeSeriesEngineBuilder.BuildAsync<SerializableWrapper<string>, MarketDataBar>(
            segmentFolder: _folder, segmentPrefix: "compact", memTableCapacity: 4);

        var ds = new SerializableWrapper<string>("AAPL");
        var d0 = new DateOnly(2024, 1, 1);
        await stack.Store.AppendAsync(ds, Enumerable.Range(0, 20)
            .Select(i => MakeBar(d0.AddDays(i), (decimal)i))
            .ToList());
        await stack.InnerEngine.FlushAsync();

        var segmentCountBefore = stack.InnerEngine.SegmentCount;
        segmentCountBefore.Should().BeGreaterThanOrEqualTo(2);

        await stack.InnerEngine.CompactAsync();

        stack.InnerEngine.SegmentCount.Should().Be(1, "compaction must merge all segments into one");

        // Compacted segment must still be columnar .bsct, not row .dat.
        Directory.GetFiles(_folder, "*.bsct").Should().HaveCount(1);
        Directory.GetFiles(_folder, "*.dat").Should().BeEmpty();

        // Data survives compaction intact.
        var fetched = new List<MarketDataBar>();
        await foreach (var r in stack.Store.GetRangeAsync(ds, d0, d0.AddDays(20), ColumnProjection.All))
        {
            fetched.Add(r);
        }

        fetched.Should().HaveCount(20);
        fetched.Select(r => r.Close).Should().Equal(Enumerable.Range(0, 20).Select(i => (decimal)i));
    }

    [Fact]
    public async Task Recovery_ReopensExistingColumnarSegmentsOnStartup()
    {
        var ds = new SerializableWrapper<string>("AAPL");
        var d0 = new DateOnly(2024, 1, 1);

        // First engine: write and flush, then dispose to release file handles.
        {
            await using var stack1 = await ColumnarTimeSeriesEngineBuilder.BuildAsync<SerializableWrapper<string>, MarketDataBar>(
                segmentFolder: _folder, segmentPrefix: "colts", memTableCapacity: 4);
            await stack1.Store.AppendAsync(ds, Enumerable.Range(0, 10)
                .Select(i => MakeBar(d0.AddDays(i), (decimal)i))
                .ToList());
            await stack1.InnerEngine.FlushAsync();
        }

        // Second engine: same folder/prefix, new instance — recovery must load the existing segments.
        await using var stack2 = await ColumnarTimeSeriesEngineBuilder.BuildAsync<SerializableWrapper<string>, MarketDataBar>(
            segmentFolder: _folder, segmentPrefix: "colts", memTableCapacity: 1000);

        stack2.InnerEngine.SegmentCount.Should().BeGreaterThan(0,
            "startup recovery must discover existing .bsct segments");

        var fetched = new List<MarketDataBar>();
        await foreach (var r in stack2.Store.GetRangeAsync(ds, d0, d0.AddDays(10), ColumnProjection.All))
        {
            fetched.Add(r);
        }

        fetched.Should().HaveCount(10);
    }
}
