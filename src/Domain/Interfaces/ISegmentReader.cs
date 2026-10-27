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
using Boutquin.Storage.Domain.ValueObjects.SortedStringTable;

namespace Boutquin.Storage.Domain.Interfaces;

/// <summary>
/// Read-side view of an immutable on-disk segment owned by an
/// <see cref="ILsmStorageEngine{TKey, TValue}"/>. Decouples the LSM engine's
/// segment-tracking list from any particular on-disk file layout.
/// </summary>
/// <typeparam name="TKey">The type of the keys in the segment.</typeparam>
/// <typeparam name="TValue">The type of the values in the segment.</typeparam>
/// <remarks>
/// <para><b>Value-kind propagation.</b> v2.0.0 surfaces the per-entry
/// <see cref="ValueKind"/> discriminator to the engine so that
/// <see cref="ValueKind.ValueLogPointer"/> entries flow through flush and compaction
/// without ever calling <c>ValueLogReader.Read</c> at the segment layer. The engine layer
/// owns the pointer-dereference step; segment readers only surface
/// <see cref="BlockEntry"/> or <see cref="SegmentWriteEntry{TKey, TValue}"/> records.</para>
///
/// <para><b>Immutability contract.</b> segments are written once by
/// <see cref="ISegmentFormat{TKey, TValue}.WriteAsync"/> and thereafter read-only.
/// Implementations must not mutate the underlying bytes except via
/// <see cref="ClearAsync"/>, which deletes the segment file entirely as part of
/// compaction or engine <c>Clear</c>. A reader whose <see cref="ClearAsync"/> has been
/// called is disposed.</para>
/// </remarks>
public interface ISegmentReader<TKey, TValue>
    where TKey : IStorageKey<TKey>, new()
    where TValue : ISerializable<TValue>, new()
{
    /// <summary>
    /// Attempts to read the block entry for <paramref name="key"/> whose per-entry seqno is
    /// <c>≤ <paramref name="maxSequenceNumber"/></c>. Surfaces the raw
    /// <see cref="BlockEntry"/> so the engine can dispatch on
    /// <see cref="BlockEntry.Kind"/> without the segment reader re-entering the value log.
    /// </summary>
    /// <param name="key">The key to look up.</param>
    /// <param name="maxSequenceNumber">Inclusive upper bound on per-entry seqno. Callers
    /// outside the snapshot read path pass <see cref="ulong.MaxValue"/>.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>
    /// A tuple carrying the decoded <see cref="BlockEntry"/> (when <c>Found</c> is
    /// <see langword="true"/>) and a flag indicating whether an entry visible under the
    /// filter was found. On a miss, <c>Entry</c> is the default <see cref="BlockEntry"/>.
    /// </returns>
    /// <remarks>
    /// The engine calls this per segment, newest-first, stopping at the first visible
    /// entry. Columnar segments carry no per-row seqno slot — they synthesize a
    /// <see cref="BlockEntry"/> with <see cref="ValueKind.Inline"/> and
    /// <c>SequenceNumber = 0</c>; the seqno filter collapses cheaply because
    /// <c>0 ≤ maxSequenceNumber</c> always holds.
    /// </remarks>
    Task<(BlockEntry Entry, bool Found)> TryGetBlockEntryAsync(
        TKey key, ulong maxSequenceNumber = ulong.MaxValue, CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams every segment entry whose per-entry seqno is
    /// <c>≤ <paramref name="maxSequenceNumber"/></c> as a
    /// <see cref="SegmentWriteEntry{TKey, TValue}"/> in ascending key order (SST) or stored
    /// order (columnar). Used by the compaction merge so pointer entries flow through
    /// k-way merge without ever calling <c>TValue.Deserialize</c>.
    /// </summary>
    /// <param name="maxSequenceNumber">Inclusive upper bound on per-entry seqno.</param>
    /// <param name="direction">
    /// Direction in which to iterate the segment's stored order. v2.0.0 ships only
    /// <see cref="ScanDirection.Ascending"/>; readers reject any other value with
    /// <see cref="ArgumentOutOfRangeException"/>. The parameter exists at v2.0.0 GA so
    /// descending scans can land later as an additive enum-member extension without
    /// breaking callers — the columnar envelope's reader threads <paramref name="direction"/>
    /// through to the inner <c>IColumnarSegmentFormat&lt;TRecord&gt;.ReadAsync</c> call so
    /// envelope and inner reader iterate in lockstep.
    /// </param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    IAsyncEnumerable<SegmentWriteEntry<TKey, TValue>> StreamBlockEntriesAsync(
        ulong maxSequenceNumber = ulong.MaxValue,
        ScanDirection direction = ScanDirection.Ascending,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the underlying segment bytes (typically by deleting the file).
    /// </summary>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    Task ClearAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Highest sequence number persisted in this segment, or <c>0</c> when the segment
    /// carries no per-entry seqno slot (columnar) or is empty.
    /// </summary>
    ulong MaxSequenceNumber { get; }

    /// <summary>
    /// Surfaces the segment's persisted key range for engine consumers (range queries,
    /// recovery, manifest snapshots) that need MinKey/MaxKey without scanning every block.
    /// Returns <see langword="false"/> when the segment is empty or carries no range slot
    /// (e.g., columnar segments whose key is hierarchical and whose engine-side range
    /// filter does not apply).
    /// </summary>
    /// <param name="min">When the call returns <see langword="true"/>, the lowest key persisted.</param>
    /// <param name="max">When the call returns <see langword="true"/>, the highest key persisted.</param>
    /// <remarks>
    /// DDIA 2e Ch. 4 (p. 79–82) treats the SSTable footer as the source of truth for a
    /// segment's key range — the runtime consults the footer rather than re-deriving from
    /// blocks. The engine previously maintained a parallel <c>_segmentKeyRanges</c> list and
    /// rebuilt every entry by scanning all blocks at recovery time; this method lets the
    /// engine drop the parallel list and read the canonical footer slots directly.
    /// </remarks>
    bool TryGetRange(out TKey min, out TKey max);

    /// <summary>
    /// Surfaces the segment's persisted tombstone map (deleted key → deletion seqno) for
    /// engine-level compaction GC and snapshot-visibility checks. Returns an empty map for
    /// formats that do not carry tombstones (e.g. columnar segments) or for segments
    /// written before tombstone persistence shipped. Never returns <see langword="null"/>.
    /// </summary>
    IReadOnlyDictionary<TKey, ulong> GetTombstoneSeqnos();

    /// <summary>
    /// Convenience wrapper over <see cref="TryGetBlockEntryAsync"/> that returns the
    /// inline-decoded <typeparamref name="TValue"/> for entries whose
    /// <see cref="BlockEntry.Kind"/> is <see cref="ValueKind.Inline"/>. Entries whose
    /// kind is <see cref="ValueKind.ValueLogPointer"/> are reported as a miss — the
    /// engine-level read path dereferences pointers through the value-log reader and
    /// calls <see cref="TryGetBlockEntryAsync"/> directly.
    /// </summary>
    /// <param name="key">The key to look up.</param>
    /// <param name="maxSequenceNumber">Inclusive upper bound on per-entry seqno.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    Task<(TValue Value, ulong SequenceNumber, bool Found)> TryGetValueWithSeqnoAsync(
        TKey key, ulong maxSequenceNumber, CancellationToken cancellationToken = default);
}
