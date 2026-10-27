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
using Boutquin.Storage.Domain.ValueObjects.TimeSeries;
using Boutquin.Storage.Infrastructure.TimeSeries;

namespace Boutquin.Storage.Infrastructure.Tests.TimeSeries;

/// <summary>
/// <c>GetTailAsync</c> — the recency-biased read primitive: newest <c>count</c> records,
/// date-descending, optionally bounded by <c>onOrBefore</c> (as-of semantics). Covers the
/// LSM store (windowed walk-back across memtable + flushed segments, LWW, tombstones) and
/// the in-memory sibling for contract parity.
/// </summary>
public sealed class TimeSeriesTailReadTests : IDisposable
{
    private readonly string _testDir;
    private readonly List<IDisposable> _disposables = [];

    public TimeSeriesTailReadTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"TsTailTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        foreach (var d in _disposables)
        {
            try { d.Dispose(); } catch { /* best-effort */ }
        }

        if (Directory.Exists(_testDir))
        {
            try { Directory.Delete(_testDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    private static SerializableWrapper<string> D(string s) => new(s);
    private static TimeSeriesTestRecord R(DateOnly d, int v) => new(d, v);

    private LsmTimeSeriesStore<SerializableWrapper<string>, TimeSeriesTestRecord> NewLsmStore(
        int memTableCapacity = 4)
    {
        var manifestPath = Path.Combine(_testDir, $"coverage_{Guid.NewGuid():N}.json");
        var segmentDir = Path.Combine(_testDir, $"seg_{Guid.NewGuid():N}");
        Directory.CreateDirectory(segmentDir);

        var engine = LsmEngineFactory.Build<
            TimeSeriesKey<SerializableWrapper<string>>,
            TimeSeriesTestRecord>(
            memTableCapacity: memTableCapacity,
            segmentFolder: segmentDir,
            segmentPrefix: "seg");

        var tsEngine = new LsmTimeSeriesStorageEngine<SerializableWrapper<string>, TimeSeriesTestRecord>(engine);
        var coverage = new FileDatasetCoverageIndex<SerializableWrapper<string>>(manifestPath);
        _disposables.Add(engine);
        return new LsmTimeSeriesStore<SerializableWrapper<string>, TimeSeriesTestRecord>(tsEngine, coverage);
    }

    private static async Task<List<TimeSeriesTestRecord>> ToListAsync(IAsyncEnumerable<TimeSeriesTestRecord> source)
    {
        var list = new List<TimeSeriesTestRecord>();
        await foreach (var r in source.ConfigureAwait(true))
        {
            list.Add(r);
        }

        return list;
    }

    [Fact]
    public async Task GetTailAsync_AcrossSegmentsAndMemtable_ReturnsNewestNDescending()
    {
        var store = NewLsmStore(memTableCapacity: 4);
        var ds = D("AAPL");
        var d0 = new DateOnly(2024, 1, 1);
        // 10 records; capacity 4 forces flushes, leaving a mix of segments + memtable.
        await store.AppendAsync(ds, Enumerable.Range(0, 10).Select(i => R(d0.AddDays(i), i)));

        var tail = await ToListAsync(store.GetTailAsync(ds, 3));

        tail.Select(r => r.Date).Should().Equal(
            d0.AddDays(9), d0.AddDays(8), d0.AddDays(7));
        tail.Select(r => r.Value).Should().Equal(9, 8, 7);
    }

    [Fact]
    public async Task GetTailAsync_OnOrBefore_GivesAsOfSemantics()
    {
        var store = NewLsmStore();
        var ds = D("AAPL");
        var d0 = new DateOnly(2024, 1, 1);
        await store.AppendAsync(ds, Enumerable.Range(0, 10).Select(i => R(d0.AddDays(i), i)));

        // "newest record on or before day 5" — the classic as-of lookup is tail(1, bound).
        var asOf = await ToListAsync(store.GetTailAsync(ds, 1, onOrBefore: d0.AddDays(5)));
        asOf.Should().ContainSingle().Which.Value.Should().Be(5);

        // Bound between records (day 5 deleted later scenarios aside): bound at day 4.5 is not
        // expressible with DateOnly; bound at a date with no record (use day 20 → newest is 9).
        var beyond = await ToListAsync(store.GetTailAsync(ds, 2, onOrBefore: d0.AddDays(20)));
        beyond.Select(r => r.Value).Should().Equal(9, 8);
    }

    [Fact]
    public async Task GetTailAsync_CountExceedsAvailable_ReturnsAllDescending()
    {
        var store = NewLsmStore();
        var ds = D("X");
        var d0 = new DateOnly(2024, 3, 1);
        await store.AppendAsync(ds, Enumerable.Range(0, 4).Select(i => R(d0.AddDays(i), i)));

        var tail = await ToListAsync(store.GetTailAsync(ds, 100));

        tail.Select(r => r.Value).Should().Equal(3, 2, 1, 0);
    }

    [Fact]
    public async Task GetTailAsync_EmptyDataset_ReturnsEmpty()
    {
        var store = NewLsmStore();

        var tail = await ToListAsync(store.GetTailAsync(D("NONE"), 5));

        tail.Should().BeEmpty();
    }

    [Fact]
    public async Task GetTailAsync_SparseHistoryWithLargeGap_WalksBackAcrossTheGap()
    {
        var store = NewLsmStore();
        var ds = D("SPARSE");
        var d0 = new DateOnly(2020, 1, 1);
        // 3 ancient records, a 500-day gap, then 2 recent ones. tail(4) must cross the gap.
        await store.AppendAsync(ds,
        [
            R(d0, 0), R(d0.AddDays(1), 1), R(d0.AddDays(2), 2),
            R(d0.AddDays(502), 502), R(d0.AddDays(503), 503),
        ]);

        var tail = await ToListAsync(store.GetTailAsync(ds, 4));

        tail.Select(r => r.Value).Should().Equal(503, 502, 2, 1);
    }

    [Fact]
    public async Task GetTailAsync_LastWriterWins_ReturnsOverwrittenValueOnce()
    {
        var store = NewLsmStore(memTableCapacity: 2);
        var ds = D("LWW");
        var d0 = new DateOnly(2024, 5, 1);
        await store.AppendAsync(ds, [R(d0, 1), R(d0.AddDays(1), 2)]); // flushes
        await store.AppendAsync(ds, [R(d0.AddDays(1), 22)]);          // overwrite newest

        var tail = await ToListAsync(store.GetTailAsync(ds, 2));

        tail.Select(r => r.Value).Should().Equal(22, 1);
    }

    [Fact]
    public async Task GetTailAsync_DeletedNewestRecord_IsSkipped()
    {
        var store = NewLsmStore(memTableCapacity: 2);
        var ds = D("DEL");
        var d0 = new DateOnly(2024, 6, 1);
        await store.AppendAsync(ds, [R(d0, 1), R(d0.AddDays(1), 2), R(d0.AddDays(2), 3)]);
        await store.DeleteRangeAsync(ds, d0.AddDays(2), d0.AddDays(2));

        var tail = await ToListAsync(store.GetTailAsync(ds, 2));

        tail.Select(r => r.Value).Should().Equal(2, 1);
    }

    [Fact]
    public async Task GetTailAsync_CountBelowOne_Throws()
    {
        var store = NewLsmStore();

        var act = async () => await ToListAsync(store.GetTailAsync(D("X"), 0));

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>().ConfigureAwait(true);
    }

    [Fact]
    public async Task GetTailAsync_InMemorySibling_MatchesContract()
    {
        var store = new InMemoryTimeSeriesStore<SerializableWrapper<string>, TimeSeriesTestRecord>();
        var ds = D("MEM");
        var d0 = new DateOnly(2024, 1, 1);
        await store.AppendAsync(ds, Enumerable.Range(0, 6).Select(i => R(d0.AddDays(i), i)));

        (await ToListAsync(store.GetTailAsync(ds, 2)))
            .Select(r => r.Value).Should().Equal(5, 4);
        (await ToListAsync(store.GetTailAsync(ds, 3, onOrBefore: d0.AddDays(3))))
            .Select(r => r.Value).Should().Equal(3, 2, 1);
        (await ToListAsync(store.GetTailAsync(ds, 99)))
            .Select(r => r.Value).Should().Equal(5, 4, 3, 2, 1, 0);

        var act = async () => await ToListAsync(store.GetTailAsync(ds, -1));
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>().ConfigureAwait(true);
    }
}
