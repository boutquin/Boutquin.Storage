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
using Boutquin.Storage.Infrastructure.TimeSeries;

namespace Boutquin.Storage.Infrastructure.Tests.TimeSeries;

public sealed class InMemoryDatasetCoverageIndexTests
{
    private static SerializableWrapper<string> D(string s) => new(s);
    private static DateOnly DD(int y, int m, int d) => new(y, m, d);

    [Fact]
    public async Task Get_UnknownDataset_ReturnsNull()
    {
        var idx = new InMemoryDatasetCoverageIndex<SerializableWrapper<string>>();

        var coverage = await idx.GetAsync(D("AAPL"));

        coverage.Should().BeNull();
    }

    [Fact]
    public async Task Register_FirstTime_SetsRange()
    {
        var idx = new InMemoryDatasetCoverageIndex<SerializableWrapper<string>>();

        await idx.RegisterAsync(D("AAPL"), DD(2020, 1, 1), DD(2020, 12, 31));
        var coverage = await idx.GetAsync(D("AAPL"));

        coverage.Should().Be(new DateRange(DD(2020, 1, 1), DD(2020, 12, 31)));
    }

    [Fact]
    public async Task Register_WidensExistingRange_TakesMinMin_MaxMax()
    {
        var idx = new InMemoryDatasetCoverageIndex<SerializableWrapper<string>>();

        await idx.RegisterAsync(D("AAPL"), DD(2020, 6, 1), DD(2020, 12, 31));
        await idx.RegisterAsync(D("AAPL"), DD(2019, 1, 1), DD(2020, 8, 1));
        var coverage = await idx.GetAsync(D("AAPL"));

        coverage.Should().Be(new DateRange(DD(2019, 1, 1), DD(2020, 12, 31)));
    }

    [Fact]
    public async Task Register_DifferentDatasets_AreIsolated()
    {
        var idx = new InMemoryDatasetCoverageIndex<SerializableWrapper<string>>();

        await idx.RegisterAsync(D("AAPL"), DD(2020, 1, 1), DD(2020, 6, 1));
        await idx.RegisterAsync(D("MSFT"), DD(2000, 1, 1), DD(2010, 1, 1));

        (await idx.GetAsync(D("AAPL"))).Should().Be(new DateRange(DD(2020, 1, 1), DD(2020, 6, 1)));
        (await idx.GetAsync(D("MSFT"))).Should().Be(new DateRange(DD(2000, 1, 1), DD(2010, 1, 1)));
    }

    [Fact]
    public async Task Set_ReplacesOutright()
    {
        var idx = new InMemoryDatasetCoverageIndex<SerializableWrapper<string>>();
        await idx.RegisterAsync(D("AAPL"), DD(2020, 1, 1), DD(2020, 12, 31));

        await idx.SetAsync(D("AAPL"), new DateRange(DD(2021, 1, 1), DD(2021, 6, 1)));

        (await idx.GetAsync(D("AAPL"))).Should().Be(new DateRange(DD(2021, 1, 1), DD(2021, 6, 1)));
    }

    [Fact]
    public async Task Set_Null_ClearsEntry()
    {
        var idx = new InMemoryDatasetCoverageIndex<SerializableWrapper<string>>();
        await idx.RegisterAsync(D("AAPL"), DD(2020, 1, 1), DD(2020, 12, 31));

        await idx.SetAsync(D("AAPL"), null);

        (await idx.GetAsync(D("AAPL"))).Should().BeNull();
    }

    [Fact]
    public async Task Rebuild_FromScan_ReplacesState()
    {
        var idx = new InMemoryDatasetCoverageIndex<SerializableWrapper<string>>();
        await idx.RegisterAsync(D("stale"), DD(1999, 1, 1), DD(1999, 12, 31));

        await idx.RebuildAsync(Scan(
            (D("AAPL"), DD(2020, 6, 1)),
            (D("AAPL"), DD(2020, 1, 1)),
            (D("AAPL"), DD(2020, 12, 31)),
            (D("MSFT"), DD(2000, 1, 1))));

        (await idx.GetAsync(D("AAPL"))).Should().Be(new DateRange(DD(2020, 1, 1), DD(2020, 12, 31)));
        (await idx.GetAsync(D("MSFT"))).Should().Be(new DateRange(DD(2000, 1, 1), DD(2000, 1, 1)));
        (await idx.GetAsync(D("stale"))).Should().BeNull();
    }

    // The file-backed sibling refuses an inverted interval; the in-memory index must refuse it too,
    // otherwise the store serves the inverted range as an empty coverage and reports a populated
    // window as a gap.
    [Fact]
    public async Task Register_InvertedInterval_ThrowsAndLeavesCoverageUnchanged()
    {
        var idx = new InMemoryDatasetCoverageIndex<SerializableWrapper<string>>();
        await idx.RegisterAsync(D("AAPL"), DD(2024, 1, 1), DD(2024, 1, 5));

        var act = () => idx.RegisterAsync(D("AAPL"), DD(2024, 12, 31), DD(2014, 1, 1));

        await act.Should().ThrowAsync<ArgumentException>();
        (await idx.GetAsync(D("AAPL"))).Should().Be(new DateRange(DD(2024, 1, 1), DD(2024, 1, 5)));
    }

    [Fact]
    public async Task Set_InvertedRange_ThrowsAndLeavesCoverageUnchanged()
    {
        var idx = new InMemoryDatasetCoverageIndex<SerializableWrapper<string>>();
        await idx.RegisterAsync(D("AAPL"), DD(2024, 1, 1), DD(2024, 1, 5));

        var act = () => idx.SetAsync(D("AAPL"), new DateRange(DD(2030, 1, 1), DD(1980, 1, 1)));

        await act.Should().ThrowAsync<ArgumentException>();
        (await idx.GetAsync(D("AAPL"))).Should().Be(new DateRange(DD(2024, 1, 1), DD(2024, 1, 5)));
    }

    private static async IAsyncEnumerable<(SerializableWrapper<string> Dataset, DateOnly Date)> Scan(
        params (SerializableWrapper<string>, DateOnly)[] items)
    {
        foreach (var (ds, date) in items)
        {
            yield return (ds, date);
            await Task.Yield();
        }
    }
}
