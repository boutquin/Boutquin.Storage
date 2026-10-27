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

namespace Boutquin.Storage.Domain.Interfaces.TimeSeries;

/// <summary>
/// A time-series-shaped facade over a sorted, range-scannable storage engine.
/// Writes are idempotent upserts keyed by <c>(dataset, date)</c>; the latest write is visible to subsequent reads.
/// Reads return records sliced to the requested window, in date-ascending order.
/// </summary>
/// <typeparam name="TDatasetKey">
/// The per-entity partition key. For market-data bars this is effectively
/// <c>provider:granularity:identifier</c> modelled as a record struct.
/// Must be both sortable (<see cref="IComparable{T}"/>) and serialisable (<see cref="ISerializable{T}"/>).
/// Serialization must fully deserialize to a comparer-equal dataset and reproduce identical bytes.
/// Lossy encodings are rejected with <see cref="ArgumentException"/> before a write is acknowledged;
/// comparer-equal datasets with distinct, stable encodings remain separate datasets.
/// </typeparam>
/// <typeparam name="TRecord">
/// The dated record type. Implements <see cref="IHasDate"/> to expose its sort-defining date.
/// </typeparam>
/// <remarks>
/// <para><b>Why a facade, not a new engine:</b> every concern — composite-key ordering,
/// last-writer-wins dedup, range scans, compaction — maps onto primitives already shipped
/// by <see cref="ILsmStorageEngine{TKey,TValue}"/>. The facade exposes a time-series-shaped
/// API so consumers (market-data, analytics, backtests) stop reinventing window-keyed caches
/// on top of the generic engine.</para>
/// <para><b>Why dataset-first composite key:</b> DDIA Ch. 7 on range partitioning — a
/// timestamp-first key produces write hotspots; prefixing with the dataset distributes writes
/// across the key space while preserving date-order within each dataset.</para>
/// </remarks>
public interface ITimeSeriesStore<TDatasetKey, TRecord>
    where TDatasetKey : ISerializable<TDatasetKey>, IComparable<TDatasetKey>, new()
    where TRecord : ISerializable<TRecord>, IHasDate, new()
{
    /// <summary>
    /// Idempotently upserts the supplied records under <paramref name="dataset"/>.
    /// Records are keyed by <c>(dataset, record.Date)</c>; re-appending an existing
    /// <c>(dataset, date)</c> replaces the value visible to subsequent reads when this task completes.
    /// </summary>
    /// <param name="dataset">The partition key for this batch.</param>
    /// <param name="records">Records to write; may be empty (no-op) and may arrive unsorted.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    Task AppendAsync(
        TDatasetKey dataset,
        IEnumerable<TRecord> records,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns every stored record in <paramref name="dataset"/> whose
    /// <see cref="IHasDate.Date"/> is in the inclusive range
    /// <c>[<paramref name="from"/>, <paramref name="to"/>]</c>, in date-ascending order, with
    /// only the fields named by <paramref name="projection"/> materialised.
    /// Pass <see langword="null"/> (or omit the argument) to materialise all fields — identical to
    /// passing <see cref="ColumnProjection.All"/>.
    /// An empty sequence indicates either no coverage or no records in the window; callers
    /// distinguish via <see cref="GetCoverageAsync"/>.
    /// If <paramref name="from"/> &gt; <paramref name="to"/>, returns an empty sequence (no exception).
    /// </summary>
    /// <remarks>
    /// <para><b>When projection matters.</b> If the underlying engine uses a columnar segment
    /// format, unprojected column byte ranges are not read from disk and their surviving bytes
    /// are not decoded — this is the read-amplification win the columnar path exists to deliver.
    /// If the engine uses a row segment format, the projection is accepted but ignored (row
    /// formats materialise the full record regardless).</para>
    /// <para><b>Memtable-segment uniformity.</b> Per DDIA Ch. 4 p. 141, unprojected fields are
    /// uniformly <c>default(TField)</c> across memtable and segment rows — but only for records
    /// that implement <see cref="IProjectable{TRecord}"/>. Records without that interface flow
    /// through unprojected from the memtable; segment-resident records are projected by the
    /// format itself.</para>
    /// <para><b>Strict mode.</b> <see cref="ColumnProjection.AllowMissing"/> governs the
    /// behaviour when a projected field name is absent from the segment's footer. Default
    /// (<see langword="false"/>) throws <c>ColumnNotFoundException</c>; opt-in
    /// (<see langword="true"/>) leaves the missing field at <c>default(TField)</c>.</para>
    /// </remarks>
    IAsyncEnumerable<TRecord> GetRangeAsync(
        TDatasetKey dataset,
        DateOnly from,
        DateOnly to,
        ColumnProjection? projection = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the minimum/maximum record dates currently present for <paramref name="dataset"/>,
    /// or <c>null</c> if no records exist.
    /// </summary>
    /// <remarks>
    /// The returned range reflects the actual payload min/max, not any requested window.
    /// Answered from the coverage manifest if available; falls back to a full scan otherwise.
    /// </remarks>
    Task<DateRange?> GetCoverageAsync(
        TDatasetKey dataset,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the sub-ranges of <paramref name="requested"/> that are NOT covered by the current
    /// payload for <paramref name="dataset"/>. At most two ranges are returned (a prefix gap and
    /// a suffix gap). An empty list indicates full coverage.
    /// </summary>
    /// <remarks>
    /// Internal gaps (holidays, missing bars inside the covered span) are NOT reported — callers
    /// treat "covered" as "seek to that span; if a date is missing, the source did not produce a
    /// record for it".
    /// </remarks>
    Task<IReadOnlyList<DateRange>> GetGapsAsync(
        TDatasetKey dataset,
        DateRange requested,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the newest <paramref name="count"/> records in <paramref name="dataset"/> whose
    /// <see cref="IHasDate.Date"/> is on or before <paramref name="onOrBefore"/>
    /// (unbounded when <see langword="null"/>), in date-DESCENDING order (newest first).
    /// Returns fewer than <paramref name="count"/> records when the dataset holds fewer.
    /// </summary>
    /// <remarks>
    /// <para><b>The recency-biased read primitive.</b> Market-data serving queries — latest
    /// close, trailing-N windows (SMA-200, 52-week high), as-of lookups ("newest record ≤ d")
    /// — want the tail of a series without knowing its start date (holidays, halts, and
    /// listing gaps make "N trading days" uncomputable as a calendar offset). An ascending
    /// range read forces an over-estimated window; this method walks back from the newest
    /// covered date in exponentially widening, disjoint date windows, so only the tail's row
    /// groups are read (date zone maps prune the rest).</para>
    /// <para><b>Semantics inherited from <see cref="GetRangeAsync"/>:</b> last-writer-wins,
    /// tombstone suppression, and column projection behave identically — pass
    /// <paramref name="projection"/> as <see langword="null"/> for all fields.</para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is less than 1.</exception>
    IAsyncEnumerable<TRecord> GetTailAsync(
        TDatasetKey dataset,
        int count,
        DateOnly? onOrBefore = null,
        ColumnProjection? projection = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes every record in <paramref name="dataset"/> whose
    /// <see cref="IHasDate.Date"/> is in the inclusive range
    /// <c>[<paramref name="from"/>, <paramref name="to"/>]</c>.
    /// The deletion is visible to subsequent reads when this task completes.
    /// Persistent engines use tombstones; compaction later reclaims the deleted records' space.
    /// If <paramref name="from"/> &gt; <paramref name="to"/>, the call is a no-op (no exception).
    /// </summary>
    Task DeleteRangeAsync(
        TDatasetKey dataset,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default);
}
