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
/// Sorted String Table (SSTable) — immutable on-disk sorted key/value store used as an
/// LSM-tree segment. Every entry carries a monotonic sequence number assigned by the
/// calling LSM engine so that recovery can reseed its counter to
/// <c>max(sst_max, wal_max) + 1</c> (DDIA 2e Ch. 4 p. 119).
/// </summary>
/// <typeparam name="TKey">The type of the keys in the SSTable.</typeparam>
/// <typeparam name="TValue">The type of the values in the SSTable.</typeparam>
/// <remarks>
/// <para><b>Theory.</b> An SSTable is an immutable, sorted, block-structured file. Writes
/// are issued once per memtable flush (or compaction result); subsequent reads are
/// point-lookup (O(log N)) or full scan (sequential). The sorted order enables O(n+m)
/// merges during compaction and sparse-index / bloom-filter acceleration on lookup.</para>
///
/// <para><b>Seqno contract.</b> <c>Write</c> takes triples
/// <c>(Key, Value, SequenceNumber)</c>; each entry's seqno persists to disk as the
/// C6 slot on the block entry and <see cref="MaxSequenceNumber"/> surfaces the highest
/// persisted seqno. Callers without a seqno to offer (demos, standalone use) pass
/// <c>0UL</c>.</para>
///
/// <para><b>Complexity</b> (where S = entries in this SSTable, n + m = entries in two
/// SSTables being merged):
/// <list type="bullet">
///   <item><description><b>Write:</b> O(S) — one sequential pass over pre-sorted input.</description></item>
///   <item><description><b>TryGetValue:</b> O(log B + E) where B = number of blocks, E = entries per block.</description></item>
///   <item><description><b>GetEntryCount / MaxSequenceNumber:</b> O(1) — cached from the footer.</description></item>
/// </list>
/// </para>
///
/// <para><b>No raw file surface.</b> The table is not an <see cref="IStorageFile"/>: its footer,
/// bloom filter and block-cache identity are derived from the bytes it wrote, so a raw
/// whole-file write, append or writable stream would leave it answering from bytes no longer
/// on disk. <see cref="Write(IEnumerable{SegmentWriteEntry{TKey, TValue}})"/> is the only way to
/// replace its contents and <see cref="Delete"/> the only way to remove them.</para>
///
/// <para><b>Reference.</b> Kleppmann, <i>Designing Data-Intensive Applications</i>
/// (2nd ed., 2026), Ch. 4 "Storage and Retrieval" — SSTable / LSM-tree primitives.</para>
/// </remarks>
public interface ISortedStringTable<TKey, TValue> : IFileInformation, IDisposable where TKey : IComparable<TKey>, IEquatable<TKey>
{
    /// <summary>
    /// Whether the table's file exists on disk.
    /// </summary>
    /// <returns><see langword="true"/> when the file exists; otherwise <see langword="false"/>.</returns>
    bool Exists();

    /// <summary>
    /// Deletes the table's file and invalidates every piece of state derived from it. Content
    /// reads, content-derived metadata and scans throw <see cref="ObjectDisposedException"/> until a successful
    /// <c>Write</c> replaces its contents; an interrupted scan must never appear complete.
    /// </summary>
    /// <param name="deletionHandling">Whether a missing file is ignored or raises.</param>
    /// <exception cref="FileNotFoundException">The file does not exist and
    /// <paramref name="deletionHandling"/> is <see cref="FileDeletionHandling.ThrowIfNotExists"/>.</exception>
    void Delete(FileDeletionHandling deletionHandling);

    /// <summary>
    /// Writes a sorted, seqno-carrying stream of discriminated entries to the SSTable file.
    /// </summary>
    /// <param name="items">
    /// The entries to persist, in strictly ascending key order. Each entry's sequence number
    /// is stamped into the on-disk block entry and surfaces back through <see cref="EnumerateInline"/>
    /// and <see cref="MaxSequenceNumber"/>. Entries with <c>Kind == ValueKind.Inline</c> are
    /// serialised via <c>TValue.Serialize</c>; entries with
    /// <c>Kind == ValueKind.ValueLogPointer</c> write the 12-byte pointer body verbatim into
    /// the block-entry value slot verbatim.
    /// </param>
    /// <exception cref="ArgumentException">Keys are not strictly ascending or an entry fails
    /// the <see cref="SegmentWriteEntry{TKey, TValue}"/> invariant.</exception>
    void Write(IEnumerable<SegmentWriteEntry<TKey, TValue>> items);

    /// <summary>
    /// Writes a sorted, seqno-carrying stream of discriminated entries to the SSTable file
    /// AND persists the supplied tombstone map (deleted key → deletion seqno) into the
    /// footer. Used by the LSM engine's flush path to carry the active memtable's
    /// tombstones onto disk so compaction's tombstone-GC step (DDIA Ch. 4 p. 124) can
    /// safely reclaim the markers — it now has the deletion seqno needed to prove that no
    /// older live entry survives elsewhere.
    /// </summary>
    /// <param name="items">As <c>Write(IEnumerable&lt;SegmentWriteEntry&lt;TKey, TValue&gt;&gt;)</c>.</param>
    /// <param name="tombstones">Deleted-key → deletion-seqno map persisted into the footer.
    /// <see langword="null"/> writes a tombstone-free segment.</param>
    void Write(
        IEnumerable<SegmentWriteEntry<TKey, TValue>> items,
        IReadOnlyDictionary<TKey, ulong>? tombstones);

    /// <summary>
    /// Decodes the persisted footer-level tombstone list into a
    /// <typeparamref name="TKey"/> → deletion-seqno map. Empty when the segment carries no
    /// tombstones. The compactor consumes this to reclaim deletion markers safely.
    /// </summary>
    IReadOnlyDictionary<TKey, ulong> GetTombstoneSeqnos();

    /// <summary>
    /// Reads the value associated with the specified key from the SSTable file,
    /// filtering entries whose per-entry sequence number exceeds
    /// <paramref name="maxSequenceNumber"/>.
    /// </summary>
    /// <param name="key">The key to search for.</param>
    /// <param name="value">The value associated with the key, if found and visible.</param>
    /// <param name="maxSequenceNumber">
    /// Inclusive upper bound on per-entry seqno. Callers outside the snapshot read path
    /// pass <see cref="ulong.MaxValue"/> (the default) — the filter collapses cheaply and
    /// yields every persisted entry. Callers serving a snapshot read pass the snapshot's
    /// <c>SequenceNumber</c> so entries written after acquisition are invisible. See
    /// <c>docs/snapshot-isolation.md</c> for the visibility rule.
    /// </param>
    /// <returns>True if the key is found and its seqno is visible; otherwise, false.</returns>
    bool TryGetValue(TKey key, out TValue value, ulong maxSequenceNumber = ulong.MaxValue);

    /// <summary>
    /// Yields every persisted <b>inline</b> entry whose per-entry seqno is
    /// <c>≤ <paramref name="maxSequenceNumber"/></c> as a
    /// <c>(Key, Value, SequenceNumber)</c> triple in ascending key order. Pointer entries
    /// (WiscKey value-log references) are skipped because their raw 12-byte bodies cannot
    /// be decoded as <typeparamref name="TValue"/> at this layer; callers that need the
    /// discriminated form use the engine-level read path.
    /// </summary>
    /// <param name="maxSequenceNumber">
    /// Inclusive upper bound on per-entry seqno. Pass <see cref="ulong.MaxValue"/>
    /// (the default) to enumerate every persisted inline entry. Snapshot reads pass the
    /// snapshot's <c>SequenceNumber</c>.
    /// </param>
    IEnumerable<(TKey Key, TValue Value, ulong SequenceNumber)> EnumerateInline(ulong maxSequenceNumber = ulong.MaxValue);

    /// <summary>
    /// Highest sequence number persisted in the SSTable, or zero when the file is empty.
    /// Recovery reads this to reseed the LSM engine's monotonic counter to
    /// <c>max(sst_max, wal_max) + 1</c>.
    /// </summary>
    ulong MaxSequenceNumber { get; }

    /// <summary>
    /// Gets the number of entries in the SSTable.
    /// </summary>
    /// <returns>The number of entries.</returns>
    int GetEntryCount();

    /// <summary>
    /// Gets the creation timestamp of the SSTable.
    /// </summary>
    /// <returns>The creation timestamp.</returns>
    DateTime GetCreationTime();

    /// <summary>
    /// Gets the modification timestamp of the SSTable.
    /// </summary>
    /// <returns>The modification timestamp.</returns>
    DateTime GetLastModificationTime();
}
