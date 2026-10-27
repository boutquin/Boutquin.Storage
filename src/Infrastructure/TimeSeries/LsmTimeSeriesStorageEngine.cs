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
using Boutquin.Storage.Infrastructure.LsmTree;
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar;

// TimeSeriesStoreOptions lives in Domain.ValueObjects.TimeSeries; ITimeSeriesStorageEngine
// lives in Domain.Interfaces.TimeSeries. Both usings above cover Phase 1 surface.

namespace Boutquin.Storage.Infrastructure.TimeSeries;

/// <summary>
/// Time-series-specialised LSM engine that wraps a generic
/// <see cref="ILsmStorageEngine{TKey,TValue}"/> and carries the
/// <see cref="TimeSeriesStoreOptions{TRecord}"/> that select the on-disk segment format.
/// </summary>
/// <typeparam name="TDatasetKey">The per-entity partition key type.</typeparam>
/// <typeparam name="TRecord">The dated record type.</typeparam>
/// <remarks>
/// <para><b>Why a decorator:</b> this engine adds the segment-format options surface without
/// owning the underlying LSM machinery. Consumers that already hold a tuned
/// <see cref="ILsmStorageEngine{TKey,TValue}"/> (with WAL, bloom filters, custom compaction)
/// wrap it here. The generic engine stays untouched; no <see cref="IHasDate"/> constraint
/// leaks down to its <c>TValue</c> parameter.</para>
/// <para><b>Forwarding:</b> every storage operation forwards to the underlying engine
/// unchanged. The options carried here describe the <i>format</i> the inner engine was
/// constructed with — this wrapper does not re-route flush calls itself.</para>
/// <para><b>Caller responsibility for segment format:</b> when
/// <see cref="TimeSeriesStoreOptions{TRecord}.SegmentFormat"/> is
/// <see cref="TimeSeriesSegmentFormat.Row"/> (the default), the inner engine must be
/// constructed with a row-oriented <c>ISegmentFormat</c> — either the default
/// <c>SstSegmentFormat&lt;TKey, TValue&gt;</c> (auto-built by the <c>ICompressionCodec</c>
/// convenience constructor) or an explicit one passed via the
/// <c>ISegmentFormat&lt;TKey, TValue&gt;</c> constructor. When the
/// option is <see cref="TimeSeriesSegmentFormat.Columnar"/>, the inner engine must be
/// constructed with a <c>ColumnarSegmentFormat&lt;TDatasetKey, TRecord&gt;</c> wrapping the
/// supplied <see cref="ColumnarTimeSeriesStoreOptions{TRecord}.ColumnarFormat"/>. When the inner
/// engine is an <c>LsmStorageEngine</c>, the constructor verifies this and rejects Columnar options
/// over a row-formatted inner engine, or over a <c>ColumnarSegmentFormat</c> wrapping a different
/// format instance. An inner engine of any other type cannot be inspected, and a Row option over a
/// columnar inner engine is not detected. Example columnar construction:
/// <code>
/// var innerColumnarFormat = new CompressedColumnarSegmentFormat&lt;Bar&gt;();
/// var segmentFormat = new ColumnarSegmentFormat&lt;Ticker, Bar&gt;(innerColumnarFormat);
/// var inner = new LsmStorageEngine&lt;TimeSeriesKey&lt;Ticker&gt;, Bar&gt;(
///     memTableCapacity: 10_000,
///     segmentFolder: path,
///     segmentPrefix: "ts",
///     segmentFormat: segmentFormat);
/// var options = new ColumnarTimeSeriesStoreOptions&lt;Bar&gt;(innerColumnarFormat);
/// var engine = new LsmTimeSeriesStorageEngine&lt;Ticker, Bar&gt;(inner, options);
/// </code>
/// </para>
/// <para><b>Lifetime:</b> the underlying engine is not owned by this wrapper. Callers that
/// need disposal dispose the underlying engine directly.</para>
/// </remarks>
public sealed class LsmTimeSeriesStorageEngine<TDatasetKey, TRecord>
    : ITimeSeriesStorageEngine<TDatasetKey, TRecord>,
      ILsmEntryAdmissionCheck<TimeSeriesKey<TDatasetKey>, TRecord>
    where TDatasetKey : ISerializable<TDatasetKey>, IComparable<TDatasetKey>, new()
    where TRecord : ISerializable<TRecord>, IHasDate, new()
{
    private readonly ILsmStorageEngine<TimeSeriesKey<TDatasetKey>, TRecord> _inner;

    // SetAsync forwards to the inner engine unchanged, so the inner engine's admission check is
    // exactly the one a write through this decorator meets. Forwarding it lets a caller that must
    // commit an entry elsewhere first (the CDC adapter's journal) refuse it beforehand.
    void ILsmEntryAdmissionCheck<TimeSeriesKey<TDatasetKey>, TRecord>.ValidateEntryAdmission(
        TimeSeriesKey<TDatasetKey> key, TRecord value)
        => (_inner as ILsmEntryAdmissionCheck<TimeSeriesKey<TDatasetKey>, TRecord>)?.ValidateEntryAdmission(key, value);

    // The projected-subset range path augments the caller's projection with the literal
    // column name "Date" (the columnar segment's internal date filter needs the decoded
    // dates). That requires a PUBLIC DateOnly property named Date — IHasDate alone can be
    // satisfied by an explicit interface implementation backed by a differently-named column.
    // Resolved once per closed generic by the CLR's type-init machinery.
    private static readonly bool s_hasPublicDateColumn =
        typeof(TRecord).GetProperty("Date", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            ?.PropertyType == typeof(DateOnly);

    // Asserts that every projected-range snapshot segment has a corresponding SegmentMaxSeqnos
    // entry. A shortfall means the inner LSM engine's snapshot machinery produced a malformed
    // snapshot; silently falling back to seqno 0 would make the affected segment lose every
    // tombstone race and surface stale data. Extracted so the invariant (normally unreachable
    // through the wired path because the inner engine maintains it) is directly testable without
    // forging an internal snapshot. Called per-segment from GetRangeProjectedAsync.
    internal static void ValidateSegmentMaxSeqnoInvariant(int segmentCount, int maxSeqnoCount, int segmentIndex)
    {
        if (segmentIndex >= maxSeqnoCount)
        {
            throw new InvalidOperationException(
                $"SegmentMaxSeqnos invariant violated: snapshot has {segmentCount} segment(s) " +
                $"but only {maxSeqnoCount} max-seqno entry/entries. " +
                $"Segment at index {segmentIndex} has no corresponding max seqno.");
        }
    }

    /// <summary>
    /// Wraps <paramref name="inner"/> with the supplied <paramref name="options"/>. When
    /// <paramref name="options"/> is omitted, defaults to
    /// <see cref="TimeSeriesSegmentFormat.Row"/> — the format new engines ship with.
    /// </summary>
    /// <param name="inner">The underlying generic LSM engine to forward storage operations to.</param>
    /// <param name="options">Segment-format configuration. If <see langword="null"/>, defaults are applied.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="inner"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="options"/> selects <see cref="TimeSeriesSegmentFormat.Columnar"/>
    /// but does not carry a columnar format (i.e. is not a
    /// <see cref="ColumnarTimeSeriesStoreOptions{TRecord}"/> with a non-null
    /// <see cref="ColumnarTimeSeriesStoreOptions{TRecord}.ColumnarFormat"/>), or when
    /// <paramref name="inner"/> is an <c>LsmStorageEngine</c> whose segment format is not a
    /// <c>ColumnarSegmentFormat&lt;TDatasetKey, TRecord&gt;</c> wrapping that same format instance.
    /// </exception>
    public LsmTimeSeriesStorageEngine(
        ILsmStorageEngine<TimeSeriesKey<TDatasetKey>, TRecord> inner,
        TimeSeriesStoreOptions<TRecord>? options = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        Options = options ?? new TimeSeriesStoreOptions<TRecord>();

        if (Options.SegmentFormat == TimeSeriesSegmentFormat.Columnar
            && !Options.IsColumnarFormatSupplied)
        {
            throw new ArgumentException(
                "TimeSeriesStoreOptions.SegmentFormat = Columnar requires a ColumnarTimeSeriesStoreOptions "
                + "carrying a non-null ColumnarFormat. Supply an IColumnarSegmentFormat<TRecord> (typically "
                + "source-generator-emitted CompressedColumnarSegmentFormat<TRecord>) via "
                + "ColumnarTimeSeriesStoreOptions<TRecord> when constructing the engine. The inner "
                + "LsmStorageEngine must be constructed with a ColumnarSegmentFormat<TDatasetKey, TRecord> "
                + "wrapping the same IColumnarSegmentFormat; see type remarks for the wiring pattern.",
                nameof(options));
        }

        // The columnar options name the format the engine writes. When the inner engine is an
        // LsmStorageEngine its segment format is inspectable, so a Columnar label over row segments,
        // or over an envelope wrapping a different format, is refused instead of describing a
        // format the engine never uses.
        if (Options.SegmentFormat == TimeSeriesSegmentFormat.Columnar
            && inner is LsmStorageEngine<TimeSeriesKey<TDatasetKey>, TRecord> lsm
            && !DescribesSegmentFormat(lsm.SegmentFormat, Options))
        {
            throw new ArgumentException(
                "ColumnarTimeSeriesStoreOptions.ColumnarFormat must be the IColumnarSegmentFormat<TRecord> that the "
                + "inner LsmStorageEngine's ColumnarSegmentFormat<TDatasetKey, TRecord> wraps, but the inner engine "
                + $"writes segments with {lsm.SegmentFormat.GetType().Name}. Construct the inner engine with "
                + "new ColumnarSegmentFormat<TDatasetKey, TRecord>(format) and pass the same format to the options.",
                nameof(options));
        }
    }

    private static bool DescribesSegmentFormat(
        ISegmentFormat<TimeSeriesKey<TDatasetKey>, TRecord> segmentFormat, TimeSeriesStoreOptions<TRecord> options)
        => segmentFormat is IColumnarSegmentEnvelope envelope && envelope.IsDescribedBy(options);

    /// <inheritdoc />
    /// <remarks>
    /// Reports the inner engine's isolation capability unchanged. Snapshot isolation is
    /// enforced by engine-level segment pinning plus an at-acquisition memtable copy — a
    /// format-agnostic mechanism — so this is <see langword="true"/> for both the row and the
    /// columnar segment format. It is NOT gated on
    /// <see cref="TimeSeriesSegmentFormat.Columnar"/>: a pinned columnar segment is immutable
    /// pre-acquisition state, so the absence of a per-entry seqno slot in the columnar bytes
    /// does not weaken isolation (post-acquisition writes land in the live memtable or new
    /// segments that the snapshot never captured). The narrower "does this format carry a
    /// per-entry seqno slot?" question lives on
    /// <see cref="ISegmentFormat{TKey, TValue}.SupportsSnapshots"/>, not here.
    /// </remarks>
    public bool SupportsSnapshots => _inner.SupportsSnapshots;

    /// <inheritdoc />
    public TimeSeriesStoreOptions<TRecord> Options { get; }

    /// <inheritdoc />
    public int SegmentCount => _inner.SegmentCount;

    /// <inheritdoc />
    public Task SetAsync(
        TimeSeriesKey<TDatasetKey> key,
        TRecord value,
        CancellationToken cancellationToken = default)
        => _inner.SetAsync(key, value, cancellationToken);

    /// <inheritdoc />
    public Task<(TRecord Value, bool Found)> TryGetValueAsync(
        TimeSeriesKey<TDatasetKey> key,
        CancellationToken cancellationToken = default)
        => _inner.TryGetValueAsync(key, cancellationToken);

    /// <inheritdoc />
    public Task<bool> ContainsKeyAsync(
        TimeSeriesKey<TDatasetKey> key,
        CancellationToken cancellationToken = default)
        => _inner.ContainsKeyAsync(key, cancellationToken);

    /// <inheritdoc />
    public Task RemoveAsync(
        TimeSeriesKey<TDatasetKey> key,
        CancellationToken cancellationToken = default)
        => _inner.RemoveAsync(key, cancellationToken);

    /// <inheritdoc />
    public Task ClearAsync(CancellationToken cancellationToken = default)
        => _inner.ClearAsync(cancellationToken);

    /// <inheritdoc />
    public Task<IEnumerable<(TimeSeriesKey<TDatasetKey> Key, TRecord Value)>> GetAllItemsAsync(
        CancellationToken cancellationToken = default)
        => _inner.GetAllItemsAsync(cancellationToken);

    /// <inheritdoc />
    public Task SetBulkAsync(
        IEnumerable<KeyValuePair<TimeSeriesKey<TDatasetKey>, TRecord>> items,
        CancellationToken cancellationToken = default)
        => _inner.SetBulkAsync(items, cancellationToken);

    /// <inheritdoc />
    public Task FlushAsync(CancellationToken cancellationToken = default)
        => _inner.FlushAsync(cancellationToken);

    /// <inheritdoc />
    public Task CompactAsync(CancellationToken cancellationToken = default)
        => _inner.CompactAsync(cancellationToken);

    /// <inheritdoc />
    // RS0026: two GetRangeAsync overloads both have optional CancellationToken. The 4-parameter
    // overload has an additional REQUIRED ColumnProjection parameter — no call site resolves
    // ambiguously. Suppressed under the pre-GA greenfield premise.
#pragma warning disable RS0026
    public IAsyncEnumerable<(TimeSeriesKey<TDatasetKey> Key, TRecord Value)> GetRangeAsync(
        TimeSeriesKey<TDatasetKey> startKey,
        TimeSeriesKey<TDatasetKey> endKey,
        CancellationToken cancellationToken = default)
        => _inner.GetRangeAsync(startKey, endKey, cancellationToken);
#pragma warning restore RS0026

    /// <inheritdoc />
    /// <remarks>
    /// Forwards to the inner engine unconditionally for both formats. Isolation is enforced by
    /// segment pinning + an at-acquisition memtable copy, not by per-entry seqno filtering on
    /// the columnar bytes: a pinned columnar segment holds only pre-acquisition data (it was
    /// flushed before acquisition and is never appended to), so post-acquisition writes — which
    /// land in the live memtable or a new segment outside the snapshot's captured segment set,
    /// including those a concurrent compaction merges into a fresh segment — stay invisible to
    /// the live snapshot. (The former columnar guard that rejected this call was over-conservative
    /// relative to what the engine already does; see the columnar snapshot-isolation tests.)
    /// </remarks>
    public Task<ISnapshot<TimeSeriesKey<TDatasetKey>, TRecord>> AcquireSnapshotAsync(
        CancellationToken cancellationToken = default)
        => _inner.AcquireSnapshotAsync(cancellationToken);

    /// <inheritdoc />
    // RS0026 flags the new projection-aware overload because the inherited 3-parameter
    // GetRangeAsync also carries an optional CancellationToken. The analyzer's concern —
    // a caller's binding silently migrating between overloads on recompile — does not
    // apply here: the new overload has one additional REQUIRED parameter (ColumnProjection),
    // so no call site can resolve to both. The overload is an intentional addition to the
    // unshipped 2.0 API surface.
#pragma warning disable RS0026
    public IAsyncEnumerable<(TimeSeriesKey<TDatasetKey> Key, TRecord Value)> GetRangeAsync(
        TimeSeriesKey<TDatasetKey> startKey,
        TimeSeriesKey<TDatasetKey> endKey,
        ColumnProjection projection,
        CancellationToken cancellationToken = default)
    {
        // Row-format short-circuit: projection is a no-op — row readers materialise every field
        // regardless. Delegate to the generic engine's range scan; no ProjectTo pass is needed
        // because nothing downstream consumes the projection hint on the row path.
        if (Options.SegmentFormat != TimeSeriesSegmentFormat.Columnar)
        {
            return _inner.GetRangeAsync(startKey, endKey, cancellationToken);
        }

        return GetRangeProjectedAsync(startKey, endKey, projection, cancellationToken);
    }
#pragma warning restore RS0026

    // Columnar-format projected range. Uses a k-way merge over per-segment projected streams +
    // the memtable slice so unprojected column byte ranges are never read from disk — the
    // read-amplification reduction that DDIA 2e Ch. 4 pp. 141–143 promises for column stores
    // now reaches the public ITimeSeriesStore.GetRangeAsync surface.
    //
    // Fallback path: an inner engine that is not an LsmStorageEngine<,> (some future
    // ILsmStorageEngine implementer) routes through the generic GetRangeAsync and applies
    // ProjectTo after materialisation. Correctness holds; the IO-skip win does not apply.
    private async IAsyncEnumerable<(TimeSeriesKey<TDatasetKey> Key, TRecord Value)>
        GetRangeProjectedAsync(
            TimeSeriesKey<TDatasetKey> startKey,
            TimeSeriesKey<TDatasetKey> endKey,
            ColumnProjection projection,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // ColumnProjection.All — the memtable-vs-segment shape is already uniform (every
        // field populated on both sides), and the per-column IO-skip would gain nothing.
        // Route through the generic merge for minimum cost.
        if (projection.FieldNames.Count == 0)
        {
            await foreach (var item in _inner.GetRangeAsync(startKey, endKey, cancellationToken)
                .ConfigureAwait(false))
            {
                yield return item;
            }
            yield break;
        }

        if (_inner is not LsmStorageEngine<TimeSeriesKey<TDatasetKey>, TRecord> concrete)
        {
            // Foreign ILsmStorageEngine — we can't reach its memtable. Fall back to the
            // generic merge followed by per-row ProjectTo, preserving correctness at the cost
            // of the IO-skip.
            await foreach (var item in GetRangeProjectedFallbackAsync(startKey, endKey, projection, cancellationToken)
                .ConfigureAwait(false))
            {
                yield return item;
            }
            yield break;
        }

        // Acquire an atomic snapshot of (memtable range, tombstones, segment readers) under the
        // inner engine's lock so the merge sees a flush-consistent view.

        // The snapshot pins its segment files until released, so a compaction that runs while
        // this merge streams them parks the readers instead of deleting the files. The using
        // releases the pin on every exit — the Date-column guard below, a read fault,
        // cancellation — and the explicit release before the yield loop keeps a caller that
        // stops early or holds the enumerator from pinning segments the merge no longer reads.
        var snapshot = await concrete.AcquireProjectedRangeSnapshotAsync(
            startKey, endKey, cancellationToken).ConfigureAwait(false);
        await using var snapshotPin = snapshot.ConfigureAwait(false);

        // The columnar segment reader filters records by Date inside its inner
        // IColumnarSegmentFormat.ReadAsync, so the Date column MUST be decoded even when the
        // caller excluded it from the projection. Augment with Date internally and clip it
        // back via the caller's projection after the segment read. The IO-skip still applies
        // to every other unprojected column (11 out of 12 on the MarketDataBar fixture) —
        // the one-column widening is the cheapest correctness fix.
        //
        // The augmentation couples this path to a PUBLIC property literally named "Date" —
        // IHasDate alone doesn't guarantee one (an explicit interface implementation can back
        // Date with a differently-named public column). Without this guard the mismatch
        // surfaced as ColumnNotFoundException("Date") from deep inside the segment read,
        // blaming a column the caller never projected. Fail fast at the engine boundary with
        // the actual contract instead.
        if (!s_hasPublicDateColumn)
        {
            throw new InvalidOperationException(
                $"Projected-subset range reads on '{typeof(TRecord).Name}' require a public 'Date' "
                + "property of type DateOnly, because the columnar segment's internal date filter is "
                + "driven by the decoded \"Date\" column. The record satisfies IHasDate through an "
                + "explicit interface implementation (or names its date column differently). Rename "
                + "the date-carrying property to 'Date', or read with ColumnProjection.All.");
        }

        var extendedFieldNames = new List<string>(projection.FieldNames);
        if (!extendedFieldNames.Contains("Date"))
        {
            extendedFieldNames.Add("Date");
        }
        var segmentProjection = new ColumnProjection(extendedFieldNames, projection.AllowMissing);

        // Accumulate the merge in a SortedDictionary keyed by TimeSeriesKey<TDatasetKey>. The
        // iteration order — segments oldest→newest, then memtable — is the same newest-wins
        // policy the generic engine's k-way merge enforces through the KWayMerge source-index
        // tiebreaker. Using the dictionary keeps the implementation straightforward; the
        // ≤-low-tens-of-segments LSM shape makes the extra allocation negligible.
        var merged = new SortedDictionary<TimeSeriesKey<TDatasetKey>, TRecord>();

        // Effective seqno of the winning data entry per key, used by the tombstone filter below to
        // keep a re-insert that out-versions an older tombstone (finding C-3). Row segments carry a
        // per-entry SequenceNumber; columnar segments carry none, so a value sourced from columnar
        // segment i is stamped with that segment's max seqno (snapshot.SegmentMaxSeqnos[i]) — sound
        // because a segment is one ordered memtable flush. The memtable (processed last) overwrites
        // with the precise per-entry seqno. Keyed by CompareTo, exactly like `merged`: entry keys
        // and tombstone keys are distinct instances (deserialized from different segments), and
        // the dataset-key contract promises no value equality, so a default-equality map would
        // miss the winner and the filter below would delete a re-insert.
        var winningSeqnos = new SortedDictionary<TimeSeriesKey<TDatasetKey>, ulong>();

        for (var segmentIndex = 0; segmentIndex < snapshot.Segments.Count; segmentIndex++)
        {
            var segment = snapshot.Segments[segmentIndex];
            // SegmentMaxSeqnos is populated for every segment in the snapshot (invariant
            // maintained by LsmStorageEngine's snapshot machinery). Falling back to 0 would
            // cause this segment to lose every tombstone race, silently surfacing stale data.
            // Assert the invariant rather than masking a snapshot-assembly bug.
            ValidateSegmentMaxSeqnoInvariant(
                snapshot.Segments.Count, snapshot.SegmentMaxSeqnos.Count, segmentIndex);

            var segmentMaxSeqno = snapshot.SegmentMaxSeqnos[segmentIndex];
            if (segment is IProjectingSegmentReader<TimeSeriesKey<TDatasetKey>, TRecord, ColumnProjection> projecting)
            {
                // Columnar segment — projected stream skips unprojected bytes on disk. We pass
                // the Date-augmented projection so the segment's inner date filter is driven by
                // decoded Date bytes, then apply the caller's original projection to clip Date
                // back to default — satisfying the caller's contract without sacrificing the
                // per-column IO-skip on the 10+ other fields.
                await foreach (var entry in projecting
                    .StreamProjectedRangeAsync(startKey, endKey, segmentProjection, cancellationToken: cancellationToken)
                    .ConfigureAwait(false))
                {
                    var record = entry.Value;
                    if (record is IProjectable<TRecord> projectable)
                    {
                        record = projectable.ProjectTo(projection);
                    }
                    merged[entry.Key] = record;
                    winningSeqnos[entry.Key] = segmentMaxSeqno;
                }
            }
            else
            {
                // Row-format segment — no per-column skip available. Stream every entry,
                // filter by range, resolve pointer entries through the value log, and apply
                // ProjectTo to match the columnar-side projection shape. This preserves DDIA
                // p. 141 uniformity in mixed-format engines without forcing the columnar path
                // through full materialisation.
                await foreach (var entry in segment
                    .StreamBlockEntriesAsync(ulong.MaxValue, cancellationToken: cancellationToken)
                    .ConfigureAwait(false))
                {
                    if (entry.Key.CompareTo(startKey) < 0 || entry.Key.CompareTo(endKey) > 0)
                    {
                        continue;
                    }

                    var resolved = concrete.ResolveSegmentWriteEntryValue(entry);
                    if (resolved is IProjectable<TRecord> projectable)
                    {
                        resolved = projectable.ProjectTo(projection);
                    }

                    merged[entry.Key] = resolved;
                    winningSeqnos[entry.Key] = entry.SequenceNumber;
                }
            }
        }

        // Memtable — newest source, overwrites any segment value at the same key. Mirrors
        // the KWayMerge highest-index-wins rule the generic engine's merge enforces. The
        // winning entry's precise per-key seqno overwrites any segment-derived seqno.
        foreach (var entry in snapshot.MemtableEntries)
        {
            var record = entry.Value;
            if (record is IProjectable<TRecord> projectable)
            {
                record = projectable.ProjectTo(projection);
            }
            merged[entry.Key] = record;
            winningSeqnos[entry.Key] = entry.Seqno;
        }

        // Tombstone filter — seqno-aware, mirroring LsmStorageEngine.MergedStreamAsync. A tombstone
        // suppresses a key only when it is strictly newer than the winning data entry. This now
        // covers a re-insert flushed to a newer (columnar) segment than the tombstone — its winning
        // seqno is that segment's max seqno (finding C-3) — not just an in-memtable re-insert.
        foreach (var (key, tombstoneSeqno) in snapshot.Tombstones)
        {
            if (winningSeqnos.TryGetValue(key, out var dataSeqno) && dataSeqno >= tombstoneSeqno)
            {
                continue; // the winning data entry is newer than the delete — keep it
            }
            merged.Remove(key);
        }

        // Every row is materialised in `merged`; the segments are no longer read.
        await snapshot.DisposeAsync().ConfigureAwait(false);

        foreach (var kv in merged)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return (kv.Key, kv.Value);
        }
    }

    // Preserves the Phase-1 behaviour for non-LsmStorageEngine inner engines. The public
    // correctness guarantee holds (projection shape is uniform across memtable and segments)
    // but the IO-skip win is not reached — segments are read in full before ProjectTo clips
    // the shape. Real consumers wire LsmStorageEngine; this path exists so a future
    // ILsmStorageEngine implementation that cannot expose its memtable atomically still
    // returns correctly-shaped data.
    private async IAsyncEnumerable<(TimeSeriesKey<TDatasetKey> Key, TRecord Value)>
        GetRangeProjectedFallbackAsync(
            TimeSeriesKey<TDatasetKey> startKey,
            TimeSeriesKey<TDatasetKey> endKey,
            ColumnProjection projection,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var (Key, Value) in _inner.GetRangeAsync(startKey, endKey, cancellationToken)
            .ConfigureAwait(false))
        {
            var record = Value;
            if (record is IProjectable<TRecord> projectable)
            {
                record = projectable.ProjectTo(projection);
            }
            yield return (Key, record);
        }
    }
}
