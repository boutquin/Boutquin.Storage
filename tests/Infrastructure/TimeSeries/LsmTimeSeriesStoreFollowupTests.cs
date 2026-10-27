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
/// Phase-10 follow-up — covers the three uncovered MarketData-reachable arms in
/// <see cref="LsmTimeSeriesStore{TDatasetKey, TRecord}"/>: out-of-date-order ingestion
/// (<c>r.Date &lt; min</c> branch in <c>AppendAsync</c>, L89/L91), the empty-range
/// fast-path in <c>GetGapsAsync</c> (L164/L166), and the no-coverage fallback in
/// <c>GetGapsAsync</c> (L170/L172). Each branch is reachable from a routine
/// MarketData consumer pattern; the original Phase-10 push left them uncovered.
/// </summary>
public sealed class LsmTimeSeriesStoreFollowupTests : IDisposable
{
    private readonly string _testDir;
    private readonly List<IDisposable> _disposables = [];

    public LsmTimeSeriesStoreFollowupTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"LsmTsStoreFollowup_{Guid.NewGuid():N}");
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

    private static SerializableWrapper<string> Ds(string s) => new(s);
    private static DateOnly D(int y, int m, int d) => new(y, m, d);
    private static TimeSeriesTestRecord R(int y, int m, int d, int v) => new(D(y, m, d), v);

    private LsmTimeSeriesStore<SerializableWrapper<string>, TimeSeriesTestRecord> NewStore()
        => NewStoreWithCoverage().Store;

    private (LsmTimeSeriesStore<SerializableWrapper<string>, TimeSeriesTestRecord> Store,
        InMemoryDatasetCoverageIndex<SerializableWrapper<string>> Coverage) NewStoreWithCoverage()
    {
        var segmentDir = Path.Combine(_testDir, $"seg_{Guid.NewGuid():N}");
        Directory.CreateDirectory(segmentDir);

        var engine = LsmEngineFactory.Build<
            TimeSeriesKey<SerializableWrapper<string>>,
            TimeSeriesTestRecord>(
            memTableCapacity: 64,
            segmentFolder: segmentDir,
            segmentPrefix: "seg");

        var tsEngine = new LsmTimeSeriesStorageEngine<SerializableWrapper<string>, TimeSeriesTestRecord>(engine);
        var coverage = new InMemoryDatasetCoverageIndex<SerializableWrapper<string>>();
        var store = new LsmTimeSeriesStore<SerializableWrapper<string>, TimeSeriesTestRecord>(tsEngine, coverage);

        _disposables.Add(engine);
        return (store, coverage);
    }

    // ── 1. AppendAsync — records arrive out of date order ──────────────────

    /// <summary>
    /// MarketData ingestion can receive bars out of date order — late-arriving intraday
    /// bars from one source after a later-dated bar from another, multi-source aggregation,
    /// reordered backfills. <c>AppendAsync</c>'s min/max scan must still register the full
    /// span on the coverage index, exercising the <c>r.Date &lt; min</c> arm at L89/L91 that
    /// only fires when at least one record's date is before the running minimum.
    /// </summary>
    [Fact]
    public async Task AppendAsync_RecordsOutOfDateOrder_RegistersFullSpanInCoverage()
    {
        var store = NewStore();

        // Records in 2-1-3 order so the second record (date 2026-01-01) is BEFORE the first
        // (2026-01-15). The min-tracking branch at L89 (`r.Date < min`) fires on this record.
        var records = new[]
        {
            R(2026, 1, 15, 100),
            R(2026, 1, 1, 50),   // earlier than first → updates min via L91
            R(2026, 1, 30, 200),
        };

        await store.AppendAsync(Ds("AAPL"), records).ConfigureAwait(true);

        var coverage = await store.GetCoverageAsync(Ds("AAPL")).ConfigureAwait(true);

        coverage.Should().NotBeNull();
        coverage!.Value.From.Should().Be(D(2026, 1, 1),
            "min-tracking must surface the earliest date even when records arrive out of order");
        coverage.Value.To.Should().Be(D(2026, 1, 30),
            "max-tracking must surface the latest date regardless of arrival order");
    }

    // ── 2. GetGapsAsync — empty requested range ─────────────────────────────

    /// <summary>
    /// A MarketData caller asking "what gaps exist for [D, D-1]" (To &lt; From — an empty
    /// requested range) must receive an empty array, not the full requested range and not a
    /// gap derived from coverage subtraction. The fast-path at L164/L166 short-circuits on
    /// <c>requested.IsEmpty</c> before consulting the coverage index. Reachable when callers
    /// build the requested range from arithmetic that can produce an inverted interval
    /// (e.g., <c>(end, end.AddDays(-1))</c> for "no horizon").
    /// </summary>
    [Fact]
    public async Task GetGapsAsync_EmptyRange_ReturnsEmptyArray()
    {
        var store = NewStore();
        await store.AppendAsync(Ds("AAPL"),
        [
            R(2026, 1, 1, 1),
            R(2026, 1, 31, 31),
        ]).ConfigureAwait(true);

        // Inverted range — From > To → IsEmpty == true.
        var emptyRequested = new DateRange(D(2026, 6, 30), D(2026, 6, 1));
        emptyRequested.IsEmpty.Should().BeTrue("test setup: requested must be IsEmpty");

        var gaps = await store.GetGapsAsync(Ds("AAPL"), emptyRequested).ConfigureAwait(true);

        gaps.Should().BeEmpty(
            "the IsEmpty fast-path at L164–L166 returns Array.Empty<DateRange>() without " +
            "consulting the coverage index — distinct from 'no coverage' which returns " +
            "the full requested range");
    }

    // ── 3. GetGapsAsync — no coverage for the dataset ──────────────────────

    /// <summary>
    /// A MarketData caller asking "what date gaps must I fetch for dataset X" against a
    /// store that has never seen X must receive the full requested range as a single gap —
    /// the no-coverage fallback at L170/L172. Reachable on the very first call for a new
    /// dataset, before any <c>AppendAsync</c> has registered coverage. Distinct from the
    /// IsEmpty path (test #2) — here the requested range IS valid; there's just no coverage
    /// to subtract from it.
    /// </summary>
    [Fact]
    public async Task GetGapsAsync_NoCoverage_ReturnsRequestedAsSingleGap()
    {
        var store = NewStore();
        // Note: we deliberately do NOT call AppendAsync — coverage index is empty.

        var requested = new DateRange(D(2026, 1, 1), D(2026, 12, 31));
        requested.IsEmpty.Should().BeFalse("test setup: requested must be non-empty");

        var gaps = await store.GetGapsAsync(Ds("NEVER-SEEN"), requested).ConfigureAwait(true);

        gaps.Should().HaveCount(1,
            "no-coverage path at L170–L172 returns the requested range as a single gap " +
            "so callers can fetch it whole");
        gaps[0].Should().Be(requested,
            "the returned gap is the requested range verbatim — the From/To bounds carry " +
            "into the caller's fetcher unchanged");
    }

    // ── 4. GetTailAsync — stale advisory coverage lower bound ───────────────

    /// <summary>
    /// The coverage index is advisory: a failure between the engine write and the index
    /// registration leaves durable records older than the index's lower bound. The tail read
    /// must not treat that bound as the end of the data — it must still return the older
    /// durable records, exactly as a full <c>GetRangeAsync</c> scan does.
    /// </summary>
    [Fact]
    public async Task GetTailAsync_StaleCoverageLowerBound_StillReturnsOlderDurableRecords()
    {
        var (store, coverage) = NewStoreWithCoverage();
        var ds = Ds("STALE");
        await store.AppendAsync(ds, [R(2020, 1, 1, 1), R(2024, 1, 10, 2)]).ConfigureAwait(true);

        // Simulate the crash window: the index only ever learned about the newer record.
        await coverage.SetAsync(ds, new DateRange(D(2024, 1, 10), D(2024, 1, 10))).ConfigureAwait(true);

        var tail = new List<TimeSeriesTestRecord>();
        await foreach (var r in store.GetTailAsync(ds, 2).ConfigureAwait(true))
        {
            tail.Add(r);
        }

        tail.Select(r => r.Date).Should().Equal(
            [D(2024, 1, 10), D(2020, 1, 1)],
            "both records are durable in the engine; a stale advisory lower bound must not hide the older one");
    }

    /// <summary>
    /// Same stale-floor window, reached through <c>onOrBefore</c>: a bound earlier than the
    /// advisory coverage lower bound must still return the durable records at or before it
    /// rather than short-circuiting to an empty tail.
    /// </summary>
    [Fact]
    public async Task GetTailAsync_OnOrBeforeEarlierThanStaleCoverageFloor_ReturnsOlderDurableRecords()
    {
        var (store, coverage) = NewStoreWithCoverage();
        var ds = Ds("STALE-BOUND");
        await store.AppendAsync(ds, [R(2020, 1, 1, 1), R(2024, 1, 10, 2)]).ConfigureAwait(true);
        await coverage.SetAsync(ds, new DateRange(D(2024, 1, 10), D(2024, 1, 10))).ConfigureAwait(true);

        var tail = new List<TimeSeriesTestRecord>();
        await foreach (var r in store.GetTailAsync(ds, 5, onOrBefore: D(2021, 1, 1)).ConfigureAwait(true))
        {
            tail.Add(r);
        }

        tail.Select(r => r.Date).Should().Equal(
            [D(2020, 1, 1)],
            "the 2020 record is durable and on or before the bound, whatever the advisory index claims");
    }
}
