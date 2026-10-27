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
using Boutquin.Storage.Domain.Helpers;
using Boutquin.Storage.Domain.Interfaces.TimeSeries;
using Boutquin.Storage.Domain.ValueObjects.TimeSeries;
using Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar;

namespace Boutquin.Storage.Infrastructure.TimeSeries;

/// <summary>
/// LSM-backed implementation of <see cref="ITimeSeriesStore{TDatasetKey,TRecord}"/>. Wraps
/// an <see cref="ITimeSeriesStorageEngine{TDatasetKey,TRecord}"/> and maintains a
/// <see cref="IDatasetCoverageIndex{TDatasetKey}"/> for O(1) coverage queries.
/// </summary>
/// <remarks>
/// <para><b>Why composition over a specialised engine:</b> the row-vs-columnar segment format
/// is an engine-level property (segment flush is owned by the engine). The store composes over
/// <see cref="ITimeSeriesStorageEngine{TDatasetKey,TRecord}"/> — a time-series-narrowed LSM
/// contract — so the store itself remains storage-engine-agnostic and inherits the format
/// decision from the engine it receives. Callers that already hold a generic
/// <see cref="ILsmStorageEngine{TKey,TValue}"/> wrap it via
/// <c>LsmTimeSeriesStorageEngine&lt;TDatasetKey, TRecord&gt;</c>.</para>
/// <para><b>Lifetime:</b> the engine and coverage index are not owned by this store — the
/// caller creates and disposes them.</para>
/// <para><b>Coverage semantics:</b> <see cref="GetCoverageAsync"/> / <see cref="GetGapsAsync"/>
/// consult the injected <see cref="IDatasetCoverageIndex{TDatasetKey}"/>. The index is
/// advisory — if it is empty or stale, <see cref="GetRangeAsync"/> still returns correct
/// results (it scans the engine directly). Only coverage/gap answers degrade to "no coverage"
/// until a subsequent <see cref="AppendAsync"/> or an explicit rebuild against the engine's
/// full scan.</para>
/// </remarks>
/// <typeparam name="TDatasetKey">Dataset partition key type.</typeparam>
/// <typeparam name="TRecord">Dated record type.</typeparam>
public sealed class LsmTimeSeriesStore<TDatasetKey, TRecord>
    : ITimeSeriesStore<TDatasetKey, TRecord>
    where TDatasetKey : ISerializable<TDatasetKey>, IComparable<TDatasetKey>, new()
    where TRecord : ISerializable<TRecord>, IHasDate, new()
{
    private readonly ITimeSeriesStorageEngine<TDatasetKey, TRecord> _engine;
    private readonly IDatasetCoverageIndex<TDatasetKey> _coverage;

    /// <summary>
    /// Initializes a new instance composing a time-series storage engine with a coverage index.
    /// Neither is owned by this store — the caller is responsible for disposal.
    /// </summary>
    public LsmTimeSeriesStore(
        ITimeSeriesStorageEngine<TDatasetKey, TRecord> engine,
        IDatasetCoverageIndex<TDatasetKey> coverage)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(coverage);
        _engine = engine;
        _coverage = coverage;
    }

    /// <inheritdoc />
    public async Task AppendAsync(
        TDatasetKey dataset,
        IEnumerable<TRecord> records,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        cancellationToken.ThrowIfCancellationRequested();
        _ = DatasetKeyEncoding.SerializeValidated(dataset);

        var list = records as IList<TRecord> ?? [.. records];
        if (list.Count == 0)
        {
            return;
        }

        var batch = new List<KeyValuePair<TimeSeriesKey<TDatasetKey>, TRecord>>(list.Count);

        // Seed min/max from list[0] and add it to the batch, then iterate from index 1 —
        // the first-iteration `r.Date < min` / `r.Date > max` checks against itself are
        // always false and add a comparison per call without changing behaviour. The
        // edge cases (single record, all-same-date, ascending order) are pinned by
        // LsmTimeSeriesStorePhase10HardeningTests.
        var first = list[0];
        var min = first.Date;
        var max = first.Date;
        batch.Add(new KeyValuePair<TimeSeriesKey<TDatasetKey>, TRecord>(
            new TimeSeriesKey<TDatasetKey>(dataset, first.Date),
            first));

        for (var i = 1; i < list.Count; i++)
        {
            var r = list[i];
            if (r.Date < min)
            {
                min = r.Date;
            }
            else if (r.Date > max)
            {
                max = r.Date;
            }

            batch.Add(new KeyValuePair<TimeSeriesKey<TDatasetKey>, TRecord>(
                new TimeSeriesKey<TDatasetKey>(dataset, r.Date),
                r));
        }

        await _engine.SetBulkAsync(batch, cancellationToken).ConfigureAwait(false);
        await _coverage.RegisterAsync(dataset, min, max, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<TRecord> GetRangeAsync(
        TDatasetKey dataset,
        DateOnly from,
        DateOnly to,
        ColumnProjection? projection = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (from > to)
        {
            yield break;
        }

        _ = DatasetKeyEncoding.SerializeValidated(dataset);
        var start = new TimeSeriesKey<TDatasetKey>(dataset, from);
        var end = new TimeSeriesKey<TDatasetKey>(dataset, to);
        // Null means "all columns" — resolve before forwarding to the engine-decorator so it
        // never receives null (the engine contract takes non-nullable ColumnProjection).
        var effectiveProjection = projection ?? ColumnProjection.All;
        // Forward the projection to the engine-decorator, which performs row-format
        // short-circuit vs columnar dispatch. The store itself does no format-aware
        // work — preserving its storage-engine-agnostic contract.
        await foreach (var (_, value) in _engine.GetRangeAsync(start, end, effectiveProjection, cancellationToken)
            .ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return value;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para><b>Windowed walk-back.</b> For date-keyed data, recency pruning by date window is
    /// equivalent to recency pruning by segment age, so the tail read needs no descending scan
    /// machinery: it issues ascending <see cref="GetRangeAsync"/> reads over disjoint,
    /// exponentially widening date windows walking back from the newest covered date, and date
    /// zone maps prune every non-tail block and row group. Dense series resolve in one window
    /// of roughly <c>2 × count</c> days; sparse series cross gaps in O(log span) windows.</para>
    /// <para><b>Coverage is only a seed.</b> The first window's upper bound stays at
    /// <paramref name="onOrBefore"/> (or open-ended), so records newer than the advisory
    /// coverage index claims — possible after a crash between the data write and the index
    /// write — are still returned. Likewise the coverage lower bound is never treated as the end
    /// of the data: the window that reaches it is widened down to <see cref="DateOnly.MinValue"/>,
    /// so records older than the index claims are returned too.</para>
    /// </remarks>
    public IAsyncEnumerable<TRecord> GetTailAsync(
        TDatasetKey dataset,
        int count,
        DateOnly? onOrBefore = null,
        ColumnProjection? projection = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        return TailCoreAsync(dataset, count, onOrBefore, projection ?? ColumnProjection.All, cancellationToken);
    }

    private async IAsyncEnumerable<TRecord> TailCoreAsync(
        TDatasetKey dataset,
        int count,
        DateOnly? onOrBefore,
        ColumnProjection projection,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var coverage = await GetCoverageAsync(dataset, cancellationToken).ConfigureAwait(false);
        if (coverage is null)
        {
            yield break;
        }

        var minDay = coverage.Value.From.DayNumber;
        var end = onOrBefore ?? DateOnly.MaxValue;

        // Anchor the first window where the data actually stops; the window's upper bound
        // stays at `end` so post-coverage stragglers are included (see remarks).
        var anchorDay = Math.Min(coverage.Value.To.DayNumber, end.DayNumber);

        var results = new List<TRecord>(count);
        var windowEnd = end;
        var width = Math.Max(count * 2, 16);
        var firstWindow = true;

        while (results.Count < count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var baseDay = firstWindow ? anchorDay : windowEnd.DayNumber;
            var startDay = baseDay - width + 1;

            // The coverage lower bound is advisory too: a failure between the engine write and
            // the index registration leaves durable records older than it. So the window that
            // reaches it is opened all the way down instead of stopping there — one last
            // ascending read whose zone maps prune everything that is not actually stored.
            var reachesCoverageFloor = startDay <= minDay;
            if (reachesCoverageFloor)
            {
                startDay = DateOnly.MinValue.DayNumber;
            }

            var windowStart = DateOnly.FromDayNumber(startDay);

            // Read the window ascending (LWW + tombstones + projection inherited), keep its
            // newest records. Dense windows hold ~2x count records; wide windows only occur
            // when earlier windows were sparse.
            var window = new List<TRecord>();
            await foreach (var r in GetRangeAsync(dataset, windowStart, windowEnd, projection, cancellationToken)
                .ConfigureAwait(false))
            {
                window.Add(r);
            }

            for (var i = window.Count - 1; i >= 0 && results.Count < count; i--)
            {
                results.Add(window[i]);
            }

            if (reachesCoverageFloor)
            {
                break; // read everything at or before windowEnd
            }

            windowEnd = windowStart.AddDays(-1); // next (older) disjoint window
            width *= 2;
            firstWindow = false;
        }

        foreach (var r in results)
        {
            yield return r;
        }
    }

    /// <inheritdoc />
    public async Task<DateRange?> GetCoverageAsync(
        TDatasetKey dataset,
        CancellationToken cancellationToken = default)
    {
        _ = DatasetKeyEncoding.SerializeValidated(dataset);
        var cached = await _coverage.GetAsync(dataset, cancellationToken).ConfigureAwait(false);
        if (cached is not null)
        {
            return cached;
        }

        // Fallback: manifest is empty or was cleared. Scan the engine for this dataset so
        // callers never see a false null when data exists on disk.
        var rebuilt = await ComputeCoverageFromEngineAsync(dataset, cancellationToken)
            .ConfigureAwait(false);
        if (rebuilt is not null)
        {
            await _coverage.SetAsync(dataset, rebuilt, cancellationToken).ConfigureAwait(false);
        }

        return rebuilt;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DateRange>> GetGapsAsync(
        TDatasetKey dataset,
        DateRange requested,
        CancellationToken cancellationToken = default)
    {
        if (requested.IsEmpty)
        {
            return [];
        }

        var coverage = await GetCoverageAsync(dataset, cancellationToken).ConfigureAwait(false);
        if (coverage is null)
        {
            return [requested];
        }

        return requested.Minus(coverage.Value);
    }

    /// <inheritdoc />
    public async Task DeleteRangeAsync(
        TDatasetKey dataset,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default)
    {
        if (from > to)
        {
            return;
        }

        _ = DatasetKeyEncoding.SerializeValidated(dataset);
        var start = new TimeSeriesKey<TDatasetKey>(dataset, from);
        var end = new TimeSeriesKey<TDatasetKey>(dataset, to);
        // Materialise the keys to delete before mutating — can't remove during async enumeration.
        var keysToRemove = new List<TimeSeriesKey<TDatasetKey>>();
        await foreach (var (key, _) in _engine.GetRangeAsync(start, end, cancellationToken)
            .ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            keysToRemove.Add(key);
        }

        foreach (var key in keysToRemove)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _engine.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
        }

        // Recompute coverage for this dataset from the surviving data. This is O(N) over the
        // dataset's keys, which is acceptable because DeleteRangeAsync is a rare operation in
        // time-series workloads.
        var rebuilt = await ComputeCoverageFromEngineAsync(dataset, cancellationToken)
            .ConfigureAwait(false);
        await _coverage.SetAsync(dataset, rebuilt, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Rebuilds the coverage index for this store by scanning the underlying engine once and
    /// extracting min/max date per dataset.
    /// </summary>
    public async Task RebuildCoverageAsync(CancellationToken cancellationToken = default)
    {
        await _coverage.RebuildAsync(ScanAll(cancellationToken), cancellationToken)
            .ConfigureAwait(false);
    }

    private async IAsyncEnumerable<(TDatasetKey Dataset, DateOnly Date)> ScanAll(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var all = await _engine.GetAllItemsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var (key, _) in all)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return (key.Dataset, key.Date);
        }
    }

    private async Task<DateRange?> ComputeCoverageFromEngineAsync(
        TDatasetKey dataset,
        CancellationToken cancellationToken)
    {
        var start = new TimeSeriesKey<TDatasetKey>(dataset, DateOnly.MinValue);
        var end = new TimeSeriesKey<TDatasetKey>(dataset, DateOnly.MaxValue);

        var first = true;
        DateOnly min = default, max = default;
        await foreach (var (key, _) in _engine.GetRangeAsync(start, end, cancellationToken)
            .ConfigureAwait(false))
        {
            if (first)
            {
                min = key.Date;
                max = key.Date;
                first = false;
            }
            else
            {
                if (key.Date < min)
                {
                    min = key.Date;
                }

                if (key.Date > max)
                {
                    max = key.Date;
                }
            }
        }

        return first ? (DateRange?)null : new DateRange(min, max);
    }
}
