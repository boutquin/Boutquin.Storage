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
using Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar;
using Boutquin.Storage.Infrastructure.TimeSeries;

namespace Boutquin.Storage.Infrastructure.Tests.TimeSeries;

public sealed class LsmTimeSeriesStoreTests : IDisposable
{
    private readonly string _testDir;
    private readonly List<IDisposable> _disposables = [];

    public LsmTimeSeriesStoreTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"LsmTsTests_{Guid.NewGuid():N}");
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
    private static DateOnly DD(int y, int m, int d) => new(y, m, d);
    private static TimeSeriesTestRecord R(int y, int m, int d, int v) => new(DD(y, m, d), v);

    private (LsmTimeSeriesStore<SerializableWrapper<string>, TimeSeriesTestRecord> Store,
             LsmStorageEngine<TimeSeriesKey<SerializableWrapper<string>>, TimeSeriesTestRecord> Engine,
             string ManifestPath)
        NewStore(int memTableCapacity = 4)
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
        var store = new LsmTimeSeriesStore<SerializableWrapper<string>, TimeSeriesTestRecord>(tsEngine, coverage);

        _disposables.Add(engine);
        return (store, engine, manifestPath);
    }

    private static async Task<List<TimeSeriesTestRecord>> ToListAsync(IAsyncEnumerable<TimeSeriesTestRecord> source)
    {
        var list = new List<TimeSeriesTestRecord>();
        await foreach (var r in source)
        {
            list.Add(r);
        }
        return list;
    }

    // AC-1: Range scan correctness over multiple segments + MemTable.
    [Fact]
    public async Task GetRange_AcrossMultipleSegments_ReturnsExactSlice_InAscendingOrder()
    {
        var (store, engine, _) = NewStore(memTableCapacity: 4);

        // 10 records → MemTable capacity 4 means at least 2 flushed segments + MemTable residue.
        // Under the active+immutable rotation (DDIA Ch. 4 p. 117–119) flushes are async; drain
        // explicitly so SegmentCount is deterministic.
        var batch1 = Enumerable.Range(0, 10)
            .Select(i => R(2020, 1, 1, i))
            .Select((r, i) => new TimeSeriesTestRecord(DD(2020, 1, 1).AddDays(i), i))
            .ToArray();
        await store.AppendAsync(D("AAPL"), batch1);
        await engine.FlushAsync();

        engine.SegmentCount.Should().BeGreaterThanOrEqualTo(2);

        var slice = await ToListAsync(
            store.GetRangeAsync(D("AAPL"), DD(2020, 1, 3), DD(2020, 1, 7), ColumnProjection.All));

        slice.Select(r => r.Value).Should().Equal(2, 3, 4, 5, 6);
    }

    // AC-2: Cross-dataset isolation.
    [Fact]
    public async Task CrossDataset_KeepsWindowsIsolated()
    {
        var (store, _, _) = NewStore();

        await store.AppendAsync(D("A"), [R(2020, 1, 1, 1), R(2020, 1, 2, 2)]);
        await store.AppendAsync(D("B"), [R(2020, 1, 1, 99), R(2020, 1, 2, 100)]);

        var sliceA = await ToListAsync(store.GetRangeAsync(D("A"), DD(2020, 1, 1), DD(2020, 12, 31), ColumnProjection.All));
        var sliceB = await ToListAsync(store.GetRangeAsync(D("B"), DD(2020, 1, 1), DD(2020, 12, 31), ColumnProjection.All));

        sliceA.Select(r => r.Value).Should().Equal(1, 2);
        sliceB.Select(r => r.Value).Should().Equal(99, 100);
    }

    // AC-3: Idempotent append — calling twice with identical records produces the same state.
    [Fact]
    public async Task AppendTwice_SameRecords_CountAfterCompactionMatchesDistinctDates()
    {
        var (store, engine, _) = NewStore(memTableCapacity: 8);
        var recs = Enumerable.Range(0, 10)
            .Select(i => new TimeSeriesTestRecord(DD(2020, 1, 1).AddDays(i), i))
            .ToArray();

        await store.AppendAsync(D("AAPL"), recs);
        await store.AppendAsync(D("AAPL"), recs);
        await engine.CompactAsync();

        var slice = await ToListAsync(store.GetRangeAsync(D("AAPL"), DD(2020, 1, 1), DD(2020, 12, 31), ColumnProjection.All));
        slice.Should().HaveCount(10);
    }

    // AC-4: Coverage accuracy after multi-segment writes.
    [Fact]
    public async Task Coverage_AfterMultiSegmentWrites_MatchesOverallMinMax()
    {
        var (store, engine, _) = NewStore(memTableCapacity: 3);

        // Four batches triggering multiple flushes (drain via FlushAsync because the
        // active+immutable rotation makes flushes async).
        await store.AppendAsync(D("AAPL"), [R(2020, 1, 1, 1), R(2020, 2, 1, 2)]);
        await store.AppendAsync(D("AAPL"), [R(2020, 3, 1, 3), R(2020, 4, 1, 4)]);
        await store.AppendAsync(D("AAPL"), [R(2020, 5, 1, 5), R(2020, 6, 1, 6)]);
        await store.AppendAsync(D("AAPL"), [R(2020, 7, 1, 7), R(2020, 8, 1, 8)]);
        await engine.FlushAsync();

        engine.SegmentCount.Should().BeGreaterThan(1);

        (await store.GetCoverageAsync(D("AAPL"))).Should()
            .Be(new DateRange(DD(2020, 1, 1), DD(2020, 8, 1)));
    }

    // AC-5: Gap computation — full cover.
    [Fact]
    public async Task Gaps_FullCover_ReturnsEmpty()
    {
        var (store, _, _) = NewStore();
        await store.AppendAsync(D("AAPL"), [R(2000, 1, 1, 1), R(2026, 4, 18, 2)]);

        var gaps = await store.GetGapsAsync(D("AAPL"),
            new DateRange(DD(2020, 1, 1), DD(2026, 4, 17)));

        gaps.Should().BeEmpty();
    }

    // AC-6: Prefix gap.
    [Fact]
    public async Task Gaps_PrefixGap()
    {
        var (store, _, _) = NewStore();
        await store.AppendAsync(D("AAPL"), [R(2010, 1, 1, 1), R(2026, 4, 18, 2)]);

        var gaps = await store.GetGapsAsync(D("AAPL"),
            new DateRange(DD(2005, 1, 1), DD(2026, 4, 18)));

        gaps.Should().ContainSingle().Which.Should().Be(new DateRange(DD(2005, 1, 1), DD(2009, 12, 31)));
    }

    // AC-7: Suffix gap.
    [Fact]
    public async Task Gaps_SuffixGap()
    {
        var (store, _, _) = NewStore();
        await store.AppendAsync(D("AAPL"), [R(2010, 1, 1, 1), R(2020, 12, 31, 2)]);

        var gaps = await store.GetGapsAsync(D("AAPL"),
            new DateRange(DD(2010, 1, 1), DD(2026, 4, 18)));

        gaps.Should().ContainSingle().Which.Should().Be(new DateRange(DD(2021, 1, 1), DD(2026, 4, 18)));
    }

    // AC-8: Two gaps.
    [Fact]
    public async Task Gaps_BothGaps()
    {
        var (store, _, _) = NewStore();
        await store.AppendAsync(D("AAPL"), [R(2010, 1, 1, 1), R(2020, 12, 31, 2)]);

        var gaps = await store.GetGapsAsync(D("AAPL"),
            new DateRange(DD(2005, 1, 1), DD(2026, 4, 18)));

        gaps.Should().HaveCount(2);
        gaps[0].Should().Be(new DateRange(DD(2005, 1, 1), DD(2009, 12, 31)));
        gaps[1].Should().Be(new DateRange(DD(2021, 1, 1), DD(2026, 4, 18)));
    }

    // AC-9: Coverage manifest rebuild — delete the file, next GetCoverage still returns correct range.
    [Fact]
    public async Task CoverageManifestDeleted_NextGetCoverage_Rebuilds()
    {
        var (store, _, manifestPath) = NewStore();
        await store.AppendAsync(D("AAPL"),
            [R(2020, 1, 1, 1), R(2020, 6, 1, 2), R(2020, 12, 31, 3)]);

        // Simulate a corrupted or lost manifest mid-session by deleting the file.
        File.Delete(manifestPath);

        // A fresh store instance against the same engine + empty manifest should still answer correctly.
        // The existing store keeps an in-memory copy, so we rebuild a new one.
        var newCoverage = new FileDatasetCoverageIndex<SerializableWrapper<string>>(manifestPath);
        var engine = (ITimeSeriesStorageEngine<SerializableWrapper<string>, TimeSeriesTestRecord>)
            ((LsmTimeSeriesStore<SerializableWrapper<string>, TimeSeriesTestRecord>)store)
                .GetType()
                .GetField("_engine", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(store)!;
        var rebuilt = new LsmTimeSeriesStore<SerializableWrapper<string>, TimeSeriesTestRecord>(engine, newCoverage);

        (await rebuilt.GetCoverageAsync(D("AAPL"))).Should()
            .Be(new DateRange(DD(2020, 1, 1), DD(2020, 12, 31)));
    }

    // AC-11: Delete-range tombstones.
    [Fact]
    public async Task DeleteRange_FollowedByGetRange_ReturnsEmpty_EvenAcrossSegments()
    {
        var (store, engine, _) = NewStore(memTableCapacity: 3);
        await store.AppendAsync(D("AAPL"), [R(2020, 1, 1, 1), R(2020, 2, 1, 2)]);
        await store.AppendAsync(D("AAPL"), [R(2020, 3, 1, 3), R(2020, 4, 1, 4)]);
        await store.AppendAsync(D("AAPL"), [R(2020, 5, 1, 5), R(2020, 6, 1, 6)]);

        await store.DeleteRangeAsync(D("AAPL"), DD(2020, 2, 1), DD(2020, 5, 1));
        await engine.CompactAsync();

        var inDeletedRange = await ToListAsync(
            store.GetRangeAsync(D("AAPL"), DD(2020, 2, 1), DD(2020, 5, 1), ColumnProjection.All));
        inDeletedRange.Should().BeEmpty();

        var survivors = await ToListAsync(
            store.GetRangeAsync(D("AAPL"), DD(2020, 1, 1), DD(2020, 12, 31), ColumnProjection.All));
        survivors.Select(r => r.Value).Should().Equal(1, 6);
    }

    // DeleteRange shrinks coverage accurately.
    [Fact]
    public async Task DeleteRange_ShrinksCoverage()
    {
        var (store, _, _) = NewStore();
        await store.AppendAsync(D("AAPL"),
            [R(2020, 1, 1, 1), R(2020, 6, 1, 2), R(2020, 12, 31, 3)]);

        await store.DeleteRangeAsync(D("AAPL"), DD(2020, 1, 1), DD(2020, 1, 1));

        (await store.GetCoverageAsync(D("AAPL"))).Should()
            .Be(new DateRange(DD(2020, 6, 1), DD(2020, 12, 31)));
    }

    // Empty append — no segments, no coverage.
    [Fact]
    public async Task AppendEmpty_IsNoop()
    {
        var (store, engine, _) = NewStore();

        await store.AppendAsync(D("AAPL"), []);

        (await store.GetCoverageAsync(D("AAPL"))).Should().BeNull();
        engine.SegmentCount.Should().Be(0);
    }

    // from > to → GetRange is empty (no exception).
    [Fact]
    public async Task GetRange_FromGreaterThanTo_ReturnsEmpty_NoException()
    {
        var (store, _, _) = NewStore();
        await store.AppendAsync(D("AAPL"), [R(2020, 1, 1, 1)]);

        var slice = await ToListAsync(store.GetRangeAsync(D("AAPL"), DD(2021, 1, 1), DD(2020, 1, 1), ColumnProjection.All));
        slice.Should().BeEmpty();
    }

    // from > to → DeleteRange is a no-op.
    [Fact]
    public async Task DeleteRange_FromGreaterThanTo_IsNoop()
    {
        var (store, _, _) = NewStore();
        await store.AppendAsync(D("AAPL"), [R(2020, 1, 1, 1)]);

        await store.DeleteRangeAsync(D("AAPL"), DD(2021, 1, 1), DD(2020, 1, 1));

        var survivors = await ToListAsync(
            store.GetRangeAsync(D("AAPL"), DD(2020, 1, 1), DD(2020, 12, 31), ColumnProjection.All));
        survivors.Should().ContainSingle().Which.Value.Should().Be(1);
    }

    // ── RebuildCoverageAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task RebuildCoverageAsync_AfterDataExists_RegistersCoverageForAllDatasets()
    {
        var (store, engine, _) = NewStore(memTableCapacity: 4);

        await store.AppendAsync(D("AAPL"),
        [
            R(2024, 1, 1, 1),
            R(2024, 6, 15, 2),
        ]);
        await store.AppendAsync(D("GOOG"),
        [
            R(2024, 3, 1, 3),
        ]);
        await engine.FlushAsync().ConfigureAwait(true);

        await store.RebuildCoverageAsync().ConfigureAwait(true);

        var aaplCoverage = await store.GetCoverageAsync(D("AAPL")).ConfigureAwait(true);
        aaplCoverage.Should().NotBeNull();
        aaplCoverage!.Value.From.Should().Be(DD(2024, 1, 1));
        aaplCoverage.Value.To.Should().Be(DD(2024, 6, 15));

        var googCoverage = await store.GetCoverageAsync(D("GOOG")).ConfigureAwait(true);
        googCoverage.Should().NotBeNull();
        googCoverage!.Value.From.Should().Be(DD(2024, 3, 1));
        googCoverage.Value.To.Should().Be(DD(2024, 3, 1));
    }

    [Fact]
    public async Task RebuildCoverageAsync_OnEmptyStore_LeavesAllCoverageNull()
    {
        var (store, _, _) = NewStore();

        await store.RebuildCoverageAsync().ConfigureAwait(true);

        var coverage = await store.GetCoverageAsync(D("AAPL")).ConfigureAwait(true);
        coverage.Should().BeNull("an empty store has no coverage for any dataset");
    }

    [Fact]
    public async Task RebuildCoverageAsync_WithCancelledToken_ThrowsOperationCanceledException()
    {
        var (store, _, _) = NewStore();
        await store.AppendAsync(D("AAPL"), [R(2024, 1, 1, 1)]).ConfigureAwait(true);

        var act = async () => await store.RebuildCoverageAsync(new CancellationToken(canceled: true))
            .ConfigureAwait(true);

        await act.Should().ThrowAsync<OperationCanceledException>().ConfigureAwait(true);
    }

    // Fix 11: GetRangeAsync with null projection must be equivalent to ColumnProjection.All.
    [Fact]
    public async Task GetRangeAsync_NullProjection_EquivalentToAll()
    {
        var (store, _, _) = NewStore();
        await store.AppendAsync(D("AAPL"),
        [
            R(2024, 1, 1, 10),
            R(2024, 1, 2, 20),
        ]).ConfigureAwait(true);

        var withNull = new List<TimeSeriesTestRecord>();
        await foreach (var r in store.GetRangeAsync(D("AAPL"), DD(2024, 1, 1), DD(2024, 1, 2)).ConfigureAwait(true))
        {
            withNull.Add(r);
        }

        var withAll = new List<TimeSeriesTestRecord>();
        await foreach (var r in store.GetRangeAsync(D("AAPL"), DD(2024, 1, 1), DD(2024, 1, 2), ColumnProjection.All).ConfigureAwait(true))
        {
            withAll.Add(r);
        }

        withNull.Should().BeEquivalentTo(withAll,
            because: "null projection must behave identically to ColumnProjection.All");
    }
}
