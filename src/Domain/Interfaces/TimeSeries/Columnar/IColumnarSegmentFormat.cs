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

// Value objects (ColumnProjection, ColumnBatch) live in Domain.ValueObjects.TimeSeries.Columnar
// per the architecture rule that Domain.Interfaces namespaces hold interfaces only.
namespace Boutquin.Storage.Domain.Interfaces.TimeSeries.Columnar;

/// <summary>
/// On-disk segment format that stores each field of <typeparamref name="TRecord"/> as a
/// separately compressed column. An alternative to row-oriented
/// <c>IEntrySerializer&lt;TKey, TValue&gt;</c> for the on-disk-segment path of a time-series
/// storage engine — selected via
/// <c>TimeSeriesStoreOptions&lt;TRecord&gt;.SegmentFormat = TimeSeriesSegmentFormat.Columnar</c>.
/// </summary>
/// <typeparam name="TRecord">
/// The dated record type. Must implement <see cref="IHasDate"/> so the format can build a
/// date-range skip index in the footer, <see cref="IColumnarRehydratable{TRecord}"/> so the
/// reader can materialise records from column arrays without reflection, and must be
/// default-constructible so the generator-emitted <c>Rehydrate</c> can produce the seed value.
/// </typeparam>
/// <remarks>
/// <para><b>Write contract:</b> <see cref="WriteAsync"/> writes a segment atomically — no
/// partial or incremental append mode. Records are assumed date-sorted at entry; callers that
/// cannot guarantee order must sort before calling. Idempotency and atomic replacement are
/// delegated to the owning time-series store's segment-rotation contract.</para>
/// <para><b>Determinism:</b> implementations must produce byte-identical output when called
/// twice with identical inputs and identical options. No embedded timestamps in the footer,
/// no non-deterministic iteration order over columns or codec state.</para>
/// <para><b>Read contract:</b> <see cref="ReadAsync"/> yields records whose dates fall in
/// <c>[from, to]</c> inclusive, in stored order (row group by row group, row by row, exactly as
/// the writer laid them out), with only the fields named by <c>projection</c> materialised.
/// Stored order is date-ascending only when the writer was handed date-ascending records;
/// implementations do not sort on read, so a caller that needs a date-ordered stream (an
/// early break at the first date past its window, a k-way merge) must sort or supply
/// date-ordered records. Unprojected column spans are not read from
/// disk — this is the read-amplification win the columnar path provides.</para>
/// <para><b>Reader error-handling:</b> corrupt or truncated segments, unknown format
/// versions, unknown encoding tags, and strict-mode missing-column projections raise
/// typed exceptions. See the Phase 5 Deliverables in the spec for the full table of
/// failure modes and exception types.</para>
/// </remarks>
public interface IColumnarSegmentFormat<TRecord>
    where TRecord : IHasDate, IColumnarRehydratable<TRecord>, new()
{
    /// <summary>
    /// Writes <paramref name="records"/> as a columnar segment to <paramref name="output"/>.
    /// </summary>
    /// <param name="output">Stream to write to. Must be writable. Caller owns the stream's lifecycle.</param>
    /// <param name="records">Date-sorted records to serialise.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task that completes when the segment has been written in full.</returns>
    Task WriteAsync(
        Stream output,
        IReadOnlyList<TRecord> records,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads records from <paramref name="input"/> whose dates fall in
    /// <c>[<paramref name="from"/>, <paramref name="to"/>]</c>, in stored order,
    /// materialising only the fields named by <paramref name="projection"/>.
    /// </summary>
    /// <remarks>
    /// Stored order is the order the records were handed to <see cref="WriteAsync"/>: it is
    /// date-ascending only when the writer was given date-ascending records, and the read path
    /// does not sort.
    /// </remarks>
    /// <param name="input">Stream to read from. Must be readable and seekable.</param>
    /// <param name="from">Inclusive start of the date range.</param>
    /// <param name="to">Inclusive end of the date range. If <c>from &gt; to</c>, the reader yields nothing.</param>
    /// <param name="projection">Which fields to materialise. See <see cref="ColumnProjection.All"/> for all fields.</param>
    /// <param name="direction">
    /// Direction in which to iterate the persisted row order. v2.0.0 ships only
    /// <see cref="ScanDirection.Ascending"/> (the persisted write-time order). The parameter
    /// exists at v2.0.0 GA so descending scans can land later as an additive enum-member
    /// extension without breaking callers.
    /// </param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>An async stream of records, in the requested direction, one per matching row.</returns>
    IAsyncEnumerable<TRecord> ReadAsync(
        Stream input,
        DateOnly from,
        DateOnly to,
        ColumnProjection projection,
        ScanDirection direction = ScanDirection.Ascending,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads row-group-aligned column batches from <paramref name="input"/> for the date
    /// range <c>[<paramref name="from"/>, <paramref name="to"/>]</c>. Each batch corresponds
    /// to one on-disk row group and exposes decoded column arrays keyed by property name.
    /// </summary>
    /// <remarks>
    /// Batches may contain edge rows whose dates fall outside <c>[from, to]</c>. Callers that
    /// need strict per-row filtering should check <see cref="ColumnBatch{TRecord}.MinDate"/>
    /// and <see cref="ColumnBatch{TRecord}.MaxDate"/> or post-filter individual rows. This is
    /// the vectorisation foundation — future <c>IVectorizedOperator</c> implementations
    /// plug into this surface rather than the record-at-a-time <see cref="ReadAsync"/> path.
    /// </remarks>
    /// <param name="input">Stream to read from. Must be readable and seekable.</param>
    /// <param name="from">Inclusive start of the date range.</param>
    /// <param name="to">Inclusive end of the date range. If <c>from &gt; to</c>, the method yields nothing.</param>
    /// <param name="projection">Which fields to materialise.</param>
    /// <param name="direction">
    /// Direction in which to iterate row groups. v2.0.0 ships only
    /// <see cref="ScanDirection.Ascending"/>; the parameter exists so descending scans can
    /// land later without breaking callers.
    /// </param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>An async stream of <see cref="ColumnBatch{TRecord}"/> values, one per matching row group.</returns>
    IAsyncEnumerable<ColumnBatch<TRecord>> ReadBatchesAsync(
        Stream input,
        DateOnly from,
        DateOnly to,
        ColumnProjection projection,
        ScanDirection direction = ScanDirection.Ascending,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads records in <c>[<paramref name="from"/>, <paramref name="to"/>]</c> that satisfy the
    /// conjunction (logical AND) of <paramref name="predicates"/>, in stored order (date-ascending
    /// only when the writer was given date-ascending records; the read path does not sort).
    /// Implementations use the segment's zone maps and bitmap indexes to skip whole row groups that
    /// cannot contain a match — without decoding their column bytes — and apply a row-level filter
    /// so the result set is exact regardless of skip granularity (DDIA 2e Ch. 4 — data-skipping
    /// indexes).
    /// </summary>
    /// <remarks>
    /// The predicate columns are materialised so the row-level filter can evaluate them; the
    /// returned records therefore carry the <paramref name="projection"/> columns plus any
    /// predicate columns. An empty <paramref name="predicates"/> list degenerates to
    /// <see cref="ReadAsync"/>.
    /// </remarks>
    /// <param name="input">Stream to read from. Must be readable and seekable.</param>
    /// <param name="from">Inclusive start of the date range.</param>
    /// <param name="to">Inclusive end of the date range. If <c>from &gt; to</c>, yields nothing.</param>
    /// <param name="projection">Which fields to materialise (predicate columns are added automatically).</param>
    /// <param name="predicates">The conjunction of single-column value predicates to apply.</param>
    /// <param name="direction">Iteration direction. v2.0.0 ships only <see cref="ScanDirection.Ascending"/>.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>An async stream of matching records in the requested direction.</returns>
    IAsyncEnumerable<TRecord> StreamWhereAsync(
        Stream input,
        DateOnly from,
        DateOnly to,
        ColumnProjection projection,
        IReadOnlyList<ColumnPredicate> predicates,
        ScanDirection direction = ScanDirection.Ascending,
        CancellationToken cancellationToken = default);
}
