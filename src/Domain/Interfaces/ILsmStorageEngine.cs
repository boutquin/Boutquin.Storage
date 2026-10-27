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
namespace Boutquin.Storage.Domain.Interfaces;

/// <summary>
/// Interface for a Log-Structured Merge-tree (LSM-tree) storage engine.
/// </summary>
/// <typeparam name="TKey">The type of the keys in the store.</typeparam>
/// <typeparam name="TValue">The type of the values in the store.</typeparam>
/// <remarks>
/// <para><b>Theory:</b></para>
/// <para>An LSM-tree is a data structure designed for high write throughput. It buffers writes in an
/// in-memory MemTable and periodically flushes them to immutable on-disk segments (SSTables). Reads
/// check the MemTable first, then search on-disk segments from newest to oldest. This design trades
/// read amplification (checking multiple segments) for write performance (sequential I/O only).</para>
///
/// <para><b>Architecture:</b></para>
/// <para>This interface extends <see cref="IBulkStorageEngine{TKey, TValue}"/> with LSM-specific
/// operations: explicit flush control, compaction, range queries, and segment count inspection.
/// By extending IBulkStorageEngine (which combines IStorageEngine and IBulkKeyValueStore),
/// an LSM engine is usable anywhere an IStorageEngine is expected. Implementations wire together
/// a MemTable (e.g., RedBlackTree) with on-disk segment storage (e.g., AppendOnlyFileStorageEngine).</para>
///
/// <para><b>Complexity (where n = total keys, k = number of on-disk segments, M = MemTable size):</b></para>
/// <para>- <b>Write (SetAsync):</b> O(log M) — inserts into the in-memory MemTable (balanced tree). Amortized O(1) if MemTable is a skip list.</para>
/// <para>- <b>Read (TryGetValueAsync):</b> O(log M + k * log S) — checks MemTable first, then searches up to k segments (each of size S) from newest to oldest. Bloom filters reduce this to O(log M + log S) in the common case.</para>
/// <para>- <b>FlushAsync:</b> O(M) — writes all MemTable entries sequentially to a new SSTable.</para>
/// <para>- <b>CompactAsync:</b> O(N) where N = total entries across all segments — reads, merges, and rewrites all entries. Reduces k to 1.</para>
/// <para>- <b>Write amplification:</b> O(k) in the worst case — each key may be rewritten during compaction across k levels.</para>
/// <para>- <b>Read amplification:</b> O(k) without Bloom filters — must check each segment. O(1) amortized with Bloom filters. After compaction, k = 1.</para>
/// <para>- <b>Space amplification:</b> O(n) to O(k * n) — depends on compaction strategy and how many obsolete versions are retained.</para>
///
/// <para><b>Reference:</b> Kleppmann, <i>Designing Data-Intensive Applications</i> (O'Reilly, 2nd ed., 2026), Ch. 4 — "Storage and Retrieval",
/// section on LSM-trees and SSTables. LSM-trees optimize write throughput by converting random writes into
/// sequential I/O through the MemTable + SSTable architecture.</para>
/// </remarks>
public interface ILsmStorageEngine<TKey, TValue> : IBulkStorageEngine<TKey, TValue>
    where TKey : IStorageKey<TKey>, new()
    where TValue : ISerializable<TValue>, new()
{
    /// <summary>
    /// Forces a flush of the current MemTable to a new on-disk segment.
    /// </summary>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous flush operation.</returns>
    /// <remarks>
    /// After flushing, the MemTable is cleared and a new on-disk segment is created containing
    /// the flushed data. This is also triggered automatically when the MemTable reaches capacity.
    /// </remarks>
    Task FlushAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the number of on-disk segments currently managed by the engine.
    /// </summary>
    int SegmentCount { get; }

    /// <summary>
    /// Whether this engine instance can serve point-in-time snapshots via
    /// <see cref="AcquireSnapshotAsync(System.Threading.CancellationToken)"/>
    /// (or any tier-specific equivalent). <see langword="false"/> means snapshot acquisition
    /// will throw at runtime; consumers that need snapshot isolation should fail-fast at
    /// construction time via this getter rather than after writes have already piled up
    /// (DDIA 2e Ch. 8, transactions — snapshot isolation, p. 293–299).
    /// </summary>
    /// <remarks>
    /// <b>Why a static capability getter, not a dynamic check.</b> Snapshot capability is a
    /// structural property of the engine instance — it cannot change after construction.
    /// Surfacing it on the interface lets composing engines (e.g., the time-series decorator)
    /// fail at construction when their format choice is incompatible with their inner
    /// engine's snapshot guarantees, rather than letting a writer run for hours before the
    /// failure surfaces at the first <see cref="AcquireSnapshotAsync"/> call.
    /// </remarks>
    bool SupportsSnapshots { get; }

    /// <summary>
    /// Compacts on-disk segments by merging them into a single new segment, removing obsolete entries.
    /// </summary>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous compaction operation.</returns>
    /// <remarks>
    /// <para><b>What it does:</b> Reads all entries from every on-disk segment, performs a sorted merge
    /// with last-writer-wins deduplication (newer segments take precedence), writes the merged result
    /// to a new segment file, and deletes the old segment files.</para>
    ///
    /// <para><b>When to call:</b> Compaction reduces read amplification by consolidating multiple
    /// segments into one. Without compaction, every read must check up to k segments (O(k) I/O).
    /// After compaction, reads check at most one segment. Compaction can be triggered explicitly
    /// or automatically when the segment count reaches a configurable threshold.</para>
    ///
    /// <para><b>No-op conditions:</b> If there are fewer than 2 segments, compaction has nothing
    /// to merge and returns immediately.</para>
    ///
    /// <para><b>Complexity:</b> O(N log k) where N = total entries across all segments and
    /// k = number of segments. In practice, for 2-way merge this simplifies to O(N).
    /// Space: O(N) — all entries must be held in memory during the merge.</para>
    ///
    /// <para><b>Durability:</b> Old segment files are deleted only after the new compacted segment
    /// is fully written. A crash during compaction leaves the old segments intact — no data loss,
    /// but the compacted segment may be partially written (detected as an extra file on recovery).</para>
    ///
    /// <para><b>Reference:</b> Kleppmann, <i>Designing Data-Intensive Applications</i> (O'Reilly, 2nd ed., 2026), Ch. 4 —
    /// "Constructing and maintaining SSTables": compaction merges multiple SSTable segments to
    /// reduce read amplification and reclaim space from obsolete entries.</para>
    /// </remarks>
    Task CompactAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns all key-value pairs within the inclusive key range [startKey, endKey],
    /// sorted by key.
    /// </summary>
    /// <param name="startKey">The inclusive lower bound of the range.</param>
    /// <param name="endKey">The inclusive upper bound of the range.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>An async stream of sorted key-value pairs within the range. The engine
    /// buffers the full range under its internal lock before the first element is yielded —
    /// the stream shape exists for signature symmetry with <c>ISnapshot.GetRangeAsync</c>,
    /// not as an incremental-production guarantee. Callers that need bounded memory or
    /// early-first-result on large ranges should range over a snapshot, which genuinely
    /// streams.</returns>
    /// <remarks>
    /// <para><b>How it works:</b> Merges results from the MemTable and all on-disk segments
    /// using the same last-writer-wins deduplication as <see cref="IBulkKeyValueStore{TKey, TValue}.GetAllItemsAsync"/>,
    /// then filters to the requested range.</para>
    ///
    /// <para><b>Complexity:</b> O(N) where N = total entries across all segments and the MemTable,
    /// since the current implementation performs a full merge then filters. A more optimized
    /// implementation could use sparse indexes and seek to reduce I/O.</para>
    ///
    /// <para><b>Reference:</b> Kleppmann, <i>Designing Data-Intensive Applications</i> (O'Reilly, 2nd ed., 2026), Ch. 4 —
    /// range queries are a natural operation on SSTables because data is stored in sorted order.</para>
    /// </remarks>
    IAsyncEnumerable<(TKey Key, TValue Value)> GetRangeAsync(
        TKey startKey, TKey endKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Acquires a point-in-time consistent read snapshot of the engine.
    /// </summary>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>
    /// An <see cref="ISnapshot{TKey, TValue}"/> that serves reads as of the engine's
    /// state at acquisition time. Callers must dispose the snapshot (<c>await using</c>)
    /// to release the segment-pinning hold that blocks physical deletion of superseded
    /// segments during compaction.
    /// </returns>
    /// <remarks>
    /// <para><b>Atomicity.</b> Acquisition runs under the engine's write lock — the
    /// <c>(LastAssignedSequenceNumber, current segment set)</c> pair is captured while
    /// no concurrent <c>SetAsync</c> / <c>RemoveAsync</c> is straddling the memtable or
    /// segment-list mutation. Every write that completed before
    /// <see cref="AcquireSnapshotAsync"/> returns is visible to the snapshot; every
    /// write that starts after the return is invisible.</para>
    ///
    /// <para><b>Pinning + compaction.</b> The snapshot keeps its captured segments
    /// on disk until it is disposed, even if compaction has superseded them. Retention
    /// cost is bounded by the longest-lived snapshot × post-acquisition write rate —
    /// dispose snapshots promptly.</para>
    ///
    /// <para><b>Reference:</b> Kleppmann, <i>Designing Data-Intensive Applications</i>
    /// (2nd ed., 2026), Ch. 4 p. 131 (SSTable immutability as the substrate for
    /// snapshot isolation); Ch. 8 (MVCC visibility rule).</para>
    /// </remarks>
    Task<ISnapshot<TKey, TValue>> AcquireSnapshotAsync(CancellationToken cancellationToken = default);
}
