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
using Boutquin.Storage.Domain.Interfaces.TimeSeries;
using Boutquin.Storage.Domain.ValueObjects.TimeSeries;
using Boutquin.Storage.Infrastructure.Tests.TimeSeries.Columnar;
using Boutquin.Storage.Infrastructure.TimeSeries;

using CiKey = Boutquin.Storage.Infrastructure.Tests.DatasetKeyIdentityAndSegmentNameContractTests.CiKey;

namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// Pins one total order and one identity for <see cref="TimeSeriesKey{TDatasetKey}"/> datasets whose
/// comparer is coarser than their serialized bytes (here: a case-insensitive comparer over a key that
/// serializes the caller's spelling). Each byte-distinct dataset must occupy one contiguous key range,
/// so a dataset-bounded range read or range delete names exactly that dataset, and the columnar
/// segment must keep each dataset's rows under that dataset's own bytes.
/// </summary>
public sealed class DatasetKeyOrderingAndGroupingContractTests : IDisposable
{
    private static readonly DateOnly s_d0 = new(2024, 1, 1);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ds-key-order-" + Guid.NewGuid().ToString("N"));

    public DatasetKeyOrderingAndGroupingContractTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // best-effort cleanup of a temp directory
        }
    }

    private static MarketDataBar Bar(DateOnly date, decimal close) => new() { Date = date, Close = close };

    private static async Task<List<T>> ToListAsync<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source)
        {
            list.Add(item);
        }

        return list;
    }

    private (ITimeSeriesStore<CiKey, MarketDataBar> Store, LsmStorageEngine<TimeSeriesKey<CiKey>, MarketDataBar> Engine) NewRowStore()
    {
        var segmentDir = Path.Combine(_dir, "row-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(segmentDir);
        var engine = LsmEngineFactory.Build<TimeSeriesKey<CiKey>, MarketDataBar>(
            memTableCapacity: 10_000, segmentFolder: segmentDir, segmentPrefix: "seg");
        var tsEngine = new LsmTimeSeriesStorageEngine<CiKey, MarketDataBar>(engine);
        var store = new LsmTimeSeriesStore<CiKey, MarketDataBar>(tsEngine, new InMemoryDatasetCoverageIndex<CiKey>());
        return (store, engine);
    }

    // ---- The key order ----

    [Fact]
    public void TimeSeriesKey_ComparerEqualDatasetsWithDistinctBytes_EachOccupyOneContiguousKeyRange()
    {
        var keys = new List<TimeSeriesKey<CiKey>>();
        for (var day = 0; day < 4; day++)
        {
            keys.Add(new TimeSeriesKey<CiKey>(new CiKey("Alpha"), s_d0.AddDays(day)));
            keys.Add(new TimeSeriesKey<CiKey>(new CiKey("ALPHA"), s_d0.AddDays(day)));
        }

        keys.Sort();
        var spellings = keys.Select(k => k.Dataset.Name).ToList();
        var runs = 1 + spellings.Zip(spellings.Skip(1)).Count(p => p.First != p.Second);

        runs.Should().Be(2,
            "each byte-distinct dataset must be one contiguous partition of the key space; sorted spellings were {0}",
            string.Join(",", spellings));

        var lower = new TimeSeriesKey<CiKey>(new CiKey("Alpha"), s_d0);
        var upper = new TimeSeriesKey<CiKey>(new CiKey("Alpha"), s_d0.AddDays(3));
        keys.Where(k => k.CompareTo(lower) >= 0 && k.CompareTo(upper) <= 0)
            .Select(k => k.Dataset.Name)
            .Should().OnlyContain(n => n == "Alpha", "the bounds (Alpha, from)..(Alpha, to) must bracket Alpha's keys only");
    }

    // ---- Public store, row segment format ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RowStore_RangeReadAndDeleteRangeOfOneSpelling_LeaveTheOtherSpellingIntact(bool flush)
    {
        var (store, engine) = NewRowStore();
        await using var _ = engine;
        await store.AppendAsync(new CiKey("Alpha"), [.. Enumerable.Range(0, 5).Select(i => Bar(s_d0.AddDays(i), 10 + i))]);
        await store.AppendAsync(new CiKey("ALPHA"), [.. Enumerable.Range(0, 5).Select(i => Bar(s_d0.AddDays(i), 20 + i))]);
        if (flush)
        {
            await engine.FlushAsync();
        }

        var alpha = await ToListAsync(store.GetRangeAsync(new CiKey("Alpha"), s_d0, s_d0.AddDays(2)));
        alpha.Select(r => r.Close).Should().Equal([10m, 11m, 12m], "a range read names only the requested dataset's rows");

        await store.DeleteRangeAsync(new CiKey("Alpha"), s_d0, s_d0.AddDays(2));

        var other = await ToListAsync(store.GetRangeAsync(new CiKey("ALPHA"), DateOnly.MinValue, DateOnly.MaxValue));
        other.Select(r => r.Close).Should().Equal([20m, 21m, 22m, 23m, 24m],
            "DeleteRange of one dataset must not remove a comparer-equal, byte-distinct dataset's acknowledged rows");
    }

    // ---- Public store, columnar segment format ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ColumnarStore_RangeReadAndDeleteRangeOfOneSpelling_LeaveTheOtherSpellingIntact(bool flush)
    {
        await using var stack = await ColumnarTimeSeriesEngineBuilder.BuildAsync<CiKey, MarketDataBar>(
            Path.Combine(_dir, "col"), memTableCapacity: 10_000);
        await stack.Store.AppendAsync(new CiKey("A"), [.. Enumerable.Range(0, 3).Select(i => Bar(s_d0.AddDays(i), 1 + i))]);
        await stack.Store.AppendAsync(new CiKey("a"), [.. Enumerable.Range(0, 3).Select(i => Bar(s_d0.AddDays(i), 10 * (1 + i)))]);
        if (flush)
        {
            await stack.InnerEngine.FlushAsync();
        }

        var lower = await ToListAsync(stack.Store.GetRangeAsync(new CiKey("a"), s_d0, s_d0.AddDays(2)));
        lower.Select(r => r.Close).Should().Equal([10m, 20m, 30m]);

        await stack.Store.DeleteRangeAsync(new CiKey("a"), s_d0, s_d0.AddDays(2));

        var upper = await ToListAsync(stack.Store.GetRangeAsync(new CiKey("A"), DateOnly.MinValue, DateOnly.MaxValue));
        upper.Select(r => r.Close).Should().Equal([1m, 2m, 3m]);
    }

    // ---- Columnar segment block grouping ----

    [Fact]
    public async Task ColumnarFlush_KeepsEachSpellingUnderItsOwnBytesAndPointLookupFindsItsOwnValue()
    {
        var folder = Path.Combine(_dir, "col-group");
        await using (var stack = await ColumnarTimeSeriesEngineBuilder.BuildAsync<CiKey, MarketDataBar>(folder, memTableCapacity: 10_000))
        {
            var engine = stack.InnerEngine;
            await engine.SetAsync(new TimeSeriesKey<CiKey>(new CiKey("Alpha"), s_d0), Bar(s_d0, 1));
            await engine.SetAsync(new TimeSeriesKey<CiKey>(new CiKey("ALPHA"), s_d0), Bar(s_d0, 2));
            await engine.SetAsync(new TimeSeriesKey<CiKey>(new CiKey("ALPHA"), s_d0.AddDays(1)), Bar(s_d0.AddDays(1), 3));
            await engine.FlushAsync();

            var all = (await engine.GetAllItemsAsync()).Select(kv => $"{kv.Key.Dataset.Name}@{kv.Key.Date:yyyy-MM-dd}={kv.Value.Close}");
            all.Should().BeEquivalentTo(["Alpha@2024-01-01=1", "ALPHA@2024-01-01=2", "ALPHA@2024-01-02=3"],
                "a flush must persist every key under its own dataset bytes");
            (await engine.TryGetValueAsync(new TimeSeriesKey<CiKey>(new CiKey("Alpha"), s_d0))).Value.Close.Should().Be(1m);
            (await engine.TryGetValueAsync(new TimeSeriesKey<CiKey>(new CiKey("ALPHA"), s_d0))).Value.Close.Should().Be(2m);
        }

        await using var reopened = await ColumnarTimeSeriesEngineBuilder.BuildAsync<CiKey, MarketDataBar>(folder, memTableCapacity: 10_000);
        (await reopened.InnerEngine.TryGetValueAsync(new TimeSeriesKey<CiKey>(new CiKey("Alpha"), s_d0))).Value.Close.Should().Be(1m);
        (await reopened.InnerEngine.TryGetValueAsync(new TimeSeriesKey<CiKey>(new CiKey("ALPHA"), s_d0.AddDays(1)))).Value.Close.Should().Be(3m);
    }

    [Fact]
    public async Task ColumnarFlush_DeleteOfAFlushedSpellingTakesEffectAndTheSegmentStillCompacts()
    {
        await using var stack = await ColumnarTimeSeriesEngineBuilder.BuildAsync<CiKey, MarketDataBar>(
            Path.Combine(_dir, "col-delete"), memTableCapacity: 10_000);
        var engine = stack.InnerEngine;
        await engine.SetAsync(new TimeSeriesKey<CiKey>(new CiKey("Alpha"), s_d0), Bar(s_d0, 1));
        await engine.SetAsync(new TimeSeriesKey<CiKey>(new CiKey("ALPHA"), s_d0), Bar(s_d0, 2));
        await engine.SetAsync(new TimeSeriesKey<CiKey>(new CiKey("ALPHA"), s_d0.AddDays(1)), Bar(s_d0.AddDays(1), 3));
        await engine.FlushAsync();

        await engine.RemoveAsync(new TimeSeriesKey<CiKey>(new CiKey("ALPHA"), s_d0.AddDays(1)));
        await engine.FlushAsync();
        await engine.CompactAsync();

        var all = (await engine.GetAllItemsAsync()).Select(kv => $"{kv.Key.Dataset.Name}@{kv.Key.Date:yyyy-MM-dd}={kv.Value.Close}");
        all.Should().BeEquivalentTo(["Alpha@2024-01-01=1", "ALPHA@2024-01-01=2"],
            "the delete names a key the segment stored and compaction rewrites the segment");
    }

    // ---- Sibling parity over randomized comparer-equal, byte-distinct datasets ----

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(20261024)]
    public async Task AllStores_RandomizedComparerEqualSpellings_AgreeWithAByteIdentityModel(int seed)
    {
        var random = new Random(seed);
        string[] spellings = ["ab", "Ab", "aB", "AB", "cd", "Cd"];
        var model = new Dictionary<string, SortedDictionary<DateOnly, decimal>>(StringComparer.Ordinal);
        foreach (var s in spellings)
        {
            model[s] = [];
        }

        var inMemory = new InMemoryTimeSeriesStore<CiKey, MarketDataBar>();
        var (row, rowEngine) = NewRowStore();
        await using var _ = rowEngine;
        await using var columnar = await ColumnarTimeSeriesEngineBuilder.BuildAsync<CiKey, MarketDataBar>(
            Path.Combine(_dir, "col-random"), memTableCapacity: 10_000);
        ITimeSeriesStore<CiKey, MarketDataBar>[] stores = [inMemory, row, columnar.Store];

        for (var step = 0; step < 40; step++)
        {
            var spelling = spellings[random.Next(spellings.Length)];
            if (random.Next(5) == 0)
            {
                var from = s_d0.AddDays(random.Next(10));
                var to = from.AddDays(random.Next(5));
                foreach (var store in stores)
                {
                    await store.DeleteRangeAsync(new CiKey(spelling), from, to);
                }

                foreach (var d in model[spelling].Keys.Where(d => d >= from && d <= to).ToList())
                {
                    model[spelling].Remove(d);
                }
            }
            else
            {
                var date = s_d0.AddDays(random.Next(10));
                var close = random.Next(1, 1_000_000);
                foreach (var store in stores)
                {
                    await store.AppendAsync(new CiKey(spelling), [Bar(date, close)]);
                }

                model[spelling][date] = close;
            }

            if (random.Next(8) == 0)
            {
                await rowEngine.FlushAsync();
                await columnar.InnerEngine.FlushAsync();
            }
        }

        await rowEngine.FlushAsync();
        await columnar.InnerEngine.FlushAsync();

        foreach (var spelling in spellings)
        {
            var expected = model[spelling].Select(kv => $"{kv.Key:yyyy-MM-dd}={kv.Value}").ToList();
            for (var s = 0; s < stores.Length; s++)
            {
                var actual = (await ToListAsync(stores[s].GetRangeAsync(new CiKey(spelling), DateOnly.MinValue, DateOnly.MaxValue)))
                    .Select(r => $"{r.Date:yyyy-MM-dd}={r.Close}").ToList();
                actual.Should().Equal(expected, "store #{0} must return exactly dataset '{1}' (seed {2})", s, spelling, seed);
            }
        }
    }
}
