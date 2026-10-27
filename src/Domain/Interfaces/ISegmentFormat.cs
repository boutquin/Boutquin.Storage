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
/// Strategy that materialises an LSM memtable flush into an immutable on-disk segment and
/// re-opens existing segments at engine startup. The LSM engine accepts an
/// <see cref="ISegmentFormat{TKey, TValue}"/> at construction and delegates all format-level
/// decisions to it; the engine itself owns only memtable, write-ahead-log, lock,
/// tombstones, and compaction orchestration.
/// </summary>
/// <typeparam name="TKey">The type of the keys in segments this format writes.</typeparam>
/// <typeparam name="TValue">The type of the values in segments this format writes.</typeparam>
/// <remarks>
/// <para><b>Layering:</b> <see cref="ISegmentFormat{TKey, TValue}"/> is the LSM-level
/// abstraction — one <see cref="WriteAsync"/> per memtable flush or compaction result.
/// Format-specific sub-structure (row-packed entries, per-column pages, per-dataset
/// blocks with a dataset index) is entirely the implementation's concern; the engine sees
/// only the <see cref="ISegmentReader{TKey, TValue}"/> returned from
/// <see cref="WriteAsync"/> or <see cref="Open"/>.</para>
/// <para><b>Known implementations:</b>
/// <list type="bullet">
/// <item><description><c>SstSegmentFormat&lt;TKey, TValue&gt;</c> — v1 block-structured SSTable;
/// the sole segment format the LSM engine uses by default in v2.0.0.
/// Every flushed entry carries a sequence number on disk (C6 slot), enabling recovery to
/// reseed the engine's monotonic counter from <c>SortedStringTable.MaxSequenceNumber</c>.</description></item>
/// <item><description><c>ColumnarSegmentFormat&lt;TDatasetKey, TRecord&gt;</c> — column-oriented
/// segment layout; available only when the LSM's <c>TKey</c> is
/// <c>TimeSeriesKey&lt;TDatasetKey&gt;</c> and <c>TValue</c> satisfies
/// <c>IHasDate, new()</c> (so the columnar footer can build its date-range
/// skip index). Accepts but ignores the per-entry seqno — time-series records have
/// <c>IHasDate</c> ordering as their authoritative semantic.</description></item>
/// </list>
/// </para>
/// <para><b>Atomic write contract:</b> <see cref="WriteAsync"/> writes the segment to disk in
/// full before the returned task completes — no partial segments are observable. The LSM
/// engine relies on this for crash safety during compaction, where the old segments are
/// deleted only after the new compacted segment's <see cref="WriteAsync"/> has returned.</para>
/// <para><b>File-extension ownership:</b> each format owns a distinct <see cref="FileExtension"/>
/// so that recovery scanning (<see cref="Open"/> across an existing segment folder) can
/// select only segments this format can read. The LSM engine names segments as
/// <c>{prefix}_{counter:D6}{FileExtension}</c>.</para>
/// </remarks>
public interface ISegmentFormat<TKey, TValue>
    where TKey : IStorageKey<TKey>, new()
    where TValue : ISerializable<TValue>, new()
{
    /// <summary>
    /// The file extension (including the leading dot) used for segments this format writes.
    /// </summary>
    /// <remarks>
    /// The SST format uses <c>".sst"</c>; the columnar format uses <c>".bsct"</c> (Boutquin
    /// Storage Columnar Time-series). The LSM engine uses this value both to name new segments
    /// and to glob existing segments during startup recovery — a segment folder may therefore
    /// contain segments of only one format for any given engine instance.
    /// </remarks>
    string FileExtension { get; }

    /// <summary>
    /// Indicates whether segments produced by this format carry a per-entry sequence-number
    /// slot a read can filter on. Row-style formats persist a seqno per entry (SST:
    /// <c>true</c>); column-oriented formats do not (Columnar: <c>false</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the narrow <i>seqno-filter-support</i> predicate, NOT the engine-level
    /// snapshot-isolation capability. <see cref="ILsmStorageEngine{TKey, TValue}.SupportsSnapshots"/>
    /// does NOT delegate to this flag: the engine enforces snapshot isolation by segment pinning
    /// plus an at-acquisition memtable copy (a format-agnostic mechanism), so it reports
    /// <c>true</c> for both formats. A pinned segment is immutable pre-acquisition state, and
    /// post-acquisition writes land in the live memtable or new segments the snapshot never
    /// captured — so the absence of a per-entry seqno slot does not weaken isolation.
    /// </para>
    /// <para>
    /// The flag is retained as the honest "this format carries a per-entry seqno slot" answer
    /// for a hypothetical future reader that needs seqno-filter availability specifically; it
    /// has no remaining engine-level isolation consumer.
    /// </para>
    /// </remarks>
    bool SupportsSnapshots { get; }

    /// <summary>
    /// Indicates whether this format persists <see cref="ValueKind.ValueLogPointer"/> entries — the
    /// value-log pointer the LSM engine stores in place of a value larger than its routing threshold.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The engine routes a value to the value log only over a format that answers
    /// <see langword="true"/> (SST: <see langword="true"/>). Over any other format value-log
    /// dispatch is off whatever value-log options the engine was given: every value stays inline,
    /// and a value too large to keep inline (more than 128 MiB, the largest routing threshold any
    /// configuration accepts) is refused before the write is acknowledged. A format that cannot
    /// store a pointer therefore never receives one, so the flush never refuses a write the engine
    /// already acknowledged.
    /// </para>
    /// <para>
    /// The default is <see langword="false"/>, so a format declares the capability rather than its
    /// absence: a decorator or a new format that does not answer stays on the inline path, which
    /// every format can store. A decorator that should keep the inner format's value-log routing
    /// forwards this member.
    /// </para>
    /// </remarks>
    bool SupportsValueLogPointers => false;

    /// <summary>
    /// Writes <paramref name="sortedEntries"/> as a new segment in <paramref name="segmentFolder"/>
    /// under <paramref name="segmentFileName"/>. Returns an <see cref="ISegmentReader{TKey, TValue}"/>
    /// over the freshly-written segment.
    /// </summary>
    /// <param name="segmentFolder">The directory to write the segment into. Must exist.</param>
    /// <param name="segmentFileName">
    /// The segment's file name (including extension). Convention: <c>{prefix}_{counter:D6}{FileExtension}</c>.
    /// </param>
    /// <param name="sortedEntries">
    /// The entries to persist, in ascending key order. Each entry carries the LSM-assigned
    /// monotonic sequence number, a discriminator selecting inline vs. value-log-pointer
    /// representation, and the value-slot payload for that branch
    /// (see <see cref="SegmentWriteEntry{TKey, TValue}"/>). The engine guarantees the key
    /// order; formats that require a specific ordering rely on this contract rather than
    /// re-sorting. Formats with no per-entry seqno slot (e.g., the columnar format) accept
    /// and ignore the seqno; all SST-family formats persist it. The engine hands a
    /// <see cref="ValueKind.ValueLogPointer"/> entry only to a format whose
    /// <see cref="SupportsValueLogPointers"/> is <see langword="true"/>; any other format may
    /// reject one with <see cref="NotSupportedException"/>, as the columnar format does.
    /// </param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>
    /// A reader positioned over the newly-written segment. The reader owns the on-disk file
    /// from this point onward; the engine releases it via
    /// <see cref="ISegmentReader{TKey, TValue}.ClearAsync"/> during compaction or clear.
    /// </returns>
    /// <remarks>
    /// Implementations materialise <paramref name="sortedEntries"/> eagerly — LSM flushes are
    /// bounded by the memtable capacity, which is small enough to hold in memory, and
    /// compaction already materialises the merged map in memory before calling this.
    /// </remarks>
    Task<ISegmentReader<TKey, TValue>> WriteAsync(
        string segmentFolder,
        string segmentFileName,
        IReadOnlyList<SegmentWriteEntry<TKey, TValue>> sortedEntries,
        IReadOnlyDictionary<TKey, ulong>? tombstones = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens an existing segment file for reading. Called by the LSM engine during startup
    /// recovery for each segment file the engine discovers in its segment folder.
    /// </summary>
    /// <param name="segmentFolder">The directory containing the segment.</param>
    /// <param name="segmentFileName">The segment's file name (including extension).</param>
    /// <returns>A reader over the segment.</returns>
    /// <remarks>
    /// This is synchronous and cheap — typical implementations record only the folder/name
    /// pair and defer I/O until the first read. Format validation (magic bytes, footer CRC,
    /// version check) happens lazily on first access so that recovery scans are fast even
    /// across many segments.
    /// </remarks>
    ISegmentReader<TKey, TValue> Open(string segmentFolder, string segmentFileName);
}
