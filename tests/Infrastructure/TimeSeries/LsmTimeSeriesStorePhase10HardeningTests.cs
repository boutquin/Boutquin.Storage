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
using Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar;
using Boutquin.Storage.Infrastructure.TimeSeries;

namespace Boutquin.Storage.Infrastructure.Tests.TimeSeries;

/// <summary>
/// Phase-10 follow-up hardening for <see cref="LsmTimeSeriesStore{TDatasetKey, TRecord}"/>.
/// Closes the remaining MarketData-reachable partial-branch coverage (IEnumerable-not-IList
/// materialisation, async-iterator cancellation propagation) and pins the AppendAsync min/max
/// contract for edge cases (single record, all-same-date) so the equivalent refactor in
/// production code can be verified without behavioural drift.
/// </summary>
public sealed class LsmTimeSeriesStorePhase10HardeningTests : IDisposable
{
    private readonly string _testDir;
    private readonly List<IDisposable> _disposables = [];

    public LsmTimeSeriesStorePhase10HardeningTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"LsmTsStoreP10Harden_{Guid.NewGuid():N}");
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
        return store;
    }

    // ── 1. AppendAsync — IEnumerable that's NOT an IList (LINQ projection) ──

    /// <summary>
    /// MarketData ingestion routinely projects raw source rows into <c>TRecord</c> via
    /// <c>LINQ.Select(...)</c>, producing a deferred-execution <see cref="IEnumerable{T}"/>
    /// that does NOT implement <see cref="IList{T}"/>. The <c>records as IList ?? .ToList()</c>
    /// pattern at L78 of <c>AppendAsync</c> takes the materialisation arm in this case.
    /// Coverage tooling reports L78 as a partial branch (1/2) — only the IList arm was
    /// previously exercised by tests passing arrays. This pins the materialisation arm.
    /// </summary>
    [Fact]
    public async Task AppendAsync_NonIListEnumerable_RegistersFullSpanInCoverage()
    {
        var store = NewStore();

        // Force a non-IList enumerable via LINQ Select. The Enumerable.Select<T, U> result
        // type does not implement IList<T>, so AppendAsync hits the .ToList() arm at L78.
        var rawRows = new[] { (2026, 1, 5), (2026, 1, 1), (2026, 1, 31) };
        IEnumerable<TimeSeriesTestRecord> projected = rawRows
            .Select(t => new TimeSeriesTestRecord(D(t.Item1, t.Item2, t.Item3), t.Item2 * 100 + t.Item3));

        // Sanity-check the test setup: the projection MUST be non-IList for this test to
        // exercise the materialisation arm. Arrays/Lists would skip past it.
        (projected is IList<TimeSeriesTestRecord>).Should().BeFalse(
            "test setup: the projected enumerable must NOT be an IList<T> for L78's materialisation arm to fire");

        await store.AppendAsync(Ds("AAPL"), projected).ConfigureAwait(true);

        var coverage = await store.GetCoverageAsync(Ds("AAPL")).ConfigureAwait(true);
        coverage.Should().NotBeNull();
        coverage!.Value.From.Should().Be(D(2026, 1, 1),
            "min/max scan across the materialised list must surface the earliest date");
        coverage.Value.To.Should().Be(D(2026, 1, 31),
            "min/max scan across the materialised list must surface the latest date");
    }

    // ── 2. GetRangeAsync — pre-cancelled token propagates ──────────────────

    /// <summary>
    /// A long-running <c>GetRangeAsync</c> over a multi-segment dataset must respect
    /// cancellation. The async iterator at L128/L130 checks
    /// <c>cancellationToken.ThrowIfCancellationRequested()</c> per yielded record;
    /// passing a pre-cancelled token surfaces <see cref="OperationCanceledException"/>
    /// before any record is yielded. MarketData's "user aborts a backtest" scenario
    /// hits this path.
    /// </summary>
    [Fact]
    public async Task GetRangeAsync_PreCancelledToken_PropagatesOperationCanceledException()
    {
        var store = NewStore();
        await store.AppendAsync(Ds("AAPL"),
        [
            R(2026, 1, 1, 1),
            R(2026, 1, 2, 2),
            R(2026, 1, 3, 3),
        ]).ConfigureAwait(true);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> act = async () =>
        {
            await foreach (var _ in store.GetRangeAsync(
                Ds("AAPL"), D(2026, 1, 1), D(2026, 1, 31), ColumnProjection.All, cts.Token).ConfigureAwait(true))
            {
                // Pre-cancelled token — the first yield must surface OperationCanceledException
                // without producing any records.
            }
        };

        await act.Should().ThrowAsync<OperationCanceledException>().ConfigureAwait(true);
    }

    // ── 3. RebuildCoverageAsync — pre-cancelled token propagates ──────────

    /// <summary>
    /// <c>RebuildCoverageAsync</c> scans every dataset via the inner async iterator
    /// <c>ScanAll</c> at L218–227. Per-record
    /// <c>cancellationToken.ThrowIfCancellationRequested()</c> at L224 must propagate
    /// to the caller. MarketData's catalogue-rebuild operation can scan large datasets;
    /// cancellation is a real operator-driven path.
    /// </summary>
    [Fact]
    public async Task RebuildCoverageAsync_PreCancelledToken_PropagatesOperationCanceledException()
    {
        var store = NewStore();
        await store.AppendAsync(Ds("AAPL"),
        [
            R(2026, 1, 1, 1),
            R(2026, 1, 2, 2),
        ]).ConfigureAwait(true);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> act = async () => await store.RebuildCoverageAsync(cts.Token).ConfigureAwait(true);

        await act.Should().ThrowAsync<OperationCanceledException>().ConfigureAwait(true);
    }

    // ── 4 + 5. Hardening #2 regression: AppendAsync min/max contract ──────

    /// <summary>
    /// <b>Hardening #2 regression contract.</b> A single-record <c>AppendAsync</c>
    /// must register both min and max as that record's date. The current production
    /// code initialises <c>min</c> and <c>max</c> from <c>list[0].Date</c> and then
    /// iterates the same list — the first iteration's <c>r.Date &lt; min</c> arm is
    /// always false (`list[0].Date &lt; list[0].Date`). A future refactor that
    /// skips the first iteration must preserve this single-record behaviour; this
    /// test pins the contract.
    /// </summary>
    [Fact]
    public async Task AppendAsync_SingleRecord_RegistersThatDateAsBothMinAndMax()
    {
        var store = NewStore();
        var date = D(2026, 6, 15);

        await store.AppendAsync(Ds("AAPL"), [new TimeSeriesTestRecord(date, 42)]).ConfigureAwait(true);

        var coverage = await store.GetCoverageAsync(Ds("AAPL")).ConfigureAwait(true);
        coverage.Should().NotBeNull();
        coverage!.Value.From.Should().Be(date,
            "single-record append seeds min from list[0].Date — refactor must preserve this");
        coverage.Value.To.Should().Be(date,
            "single-record append seeds max from list[0].Date — refactor must preserve this");
    }

    /// <summary>
    /// <b>Hardening #2 regression contract.</b> When all appended records share the
    /// same date, neither <c>r.Date &lt; min</c> nor <c>r.Date &gt; max</c> ever fires.
    /// Coverage and behaviour must still be correct — both bounds equal that date.
    /// Pins the contract for a refactor that loops from index 1.
    /// </summary>
    [Fact]
    public async Task AppendAsync_AllSameDate_RegistersThatDateAsBothMinAndMax()
    {
        var store = NewStore();
        var date = D(2026, 6, 15);

        await store.AppendAsync(Ds("AAPL"),
        [
            new TimeSeriesTestRecord(date, 1),
            new TimeSeriesTestRecord(date, 2),
            new TimeSeriesTestRecord(date, 3),
        ]).ConfigureAwait(true);

        var coverage = await store.GetCoverageAsync(Ds("AAPL")).ConfigureAwait(true);
        coverage.Should().NotBeNull();
        coverage!.Value.From.Should().Be(date,
            "all-same-date append: min equals that date — refactor must not introduce off-by-one");
        coverage.Value.To.Should().Be(date,
            "all-same-date append: max equals that date — refactor must not introduce off-by-one");
    }
}
