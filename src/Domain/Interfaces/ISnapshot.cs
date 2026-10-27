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
/// Point-in-time consistent read view of an <see cref="ILsmStorageEngine{TKey, TValue}"/>.
/// </summary>
/// <typeparam name="TKey">The key type of the underlying LSM engine.</typeparam>
/// <typeparam name="TValue">The value type of the underlying LSM engine.</typeparam>
/// <remarks>
/// <para><b>Model.</b> A snapshot is an immutable <c>(sequence_number_at_start,
/// segment_set_at_start)</c> pair captured atomically inside the engine's write lock.
/// Reads through the snapshot see every entry whose per-entry sequence number is
/// <c>≤ <see cref="SequenceNumber"/></c> and are served from the captured segment set
/// plus the snapshot-time MemTable state. Entries written after acquisition — including
/// tombstones — are invisible.</para>
///
/// <para><b>Visibility rule (DDIA 2e Ch. 8).</b> Per-key resolution walks
/// MemTable → segments newest-to-oldest and stops on the first entry with
/// <c>seqno ≤ <see cref="SequenceNumber"/></c>. If that entry is a tombstone, the key
/// is not visible (returns <c>(default, false)</c>); if it is a value-write, its value
/// is returned. A tombstone written <i>after</i> the snapshot is skipped and traversal
/// continues to older versions. This implements MVCC's two-sided visibility
/// (<c>creator ≤ snap AND (deleter IS NULL OR deleter &gt; snap)</c>) over the LSM
/// newest-first scan.</para>
///
/// <para><b>Pinning.</b> While a snapshot is live, the engine's compaction path cannot
/// physically delete any segment the snapshot captured — pinned segments remain on disk
/// even after compaction has logically superseded them, so the snapshot's reads remain
/// valid. Pinned segments become eligible for physical deletion when
/// <see cref="IAsyncDisposable.DisposeAsync"/> releases the last live reference.</para>
///
/// <para><b>Lifecycle.</b> Always consume with <c>await using</c>. A snapshot that is
/// dropped without disposal is never reclaimed by the garbage collector: its segment pins
/// and its sequence-number floor (which holds back tombstone garbage collection) stay
/// registered until the engine is disposed, so every compaction round retains the pinned
/// files for the engine's lifetime. After <see cref="IAsyncDisposable.DisposeAsync"/>, calls to
/// <see cref="TryGetValueAsync"/> and <see cref="GetRangeAsync"/> throw
/// <see cref="ObjectDisposedException"/>.</para>
///
/// <para><b>Not serializable.</b> Plain snapshot isolation does not prevent write skew,
/// phantom reads, or lost update (DDIA 2e Ch. 8). A consumer who reads under
/// a snapshot and then writes <i>outside</i> the snapshot must use compare-and-set or
/// application-level locking to be race-free. For serializable semantics use
/// <c>ISnapshotIsolationStore&lt;TKey,TValue&gt;</c> from the <c>Boutquin.Storage.Distributed</c>
/// in-process reference assembly.</para>
///
/// <para><b>Not durable.</b> Snapshots live only in the engine's address space; a
/// process restart drops every live snapshot. Post-crash recovery starts with an empty
/// live-snapshot set.</para>
/// </remarks>
public interface ISnapshot<TKey, TValue> : IAsyncDisposable
    where TKey : IStorageKey<TKey>, new()
    where TValue : ISerializable<TValue>, new()
{
    /// <summary>
    /// The engine sequence number at which this snapshot was acquired. Reads through this
    /// snapshot serve only entries with <c>per-entry seqno ≤ SequenceNumber</c>.
    /// </summary>
    ulong SequenceNumber { get; }

    /// <summary>
    /// Reads the value for <paramref name="key"/> as of the snapshot's
    /// <see cref="SequenceNumber"/>, applying the MVCC visibility rule.
    /// </summary>
    /// <param name="key">The key to look up.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>
    /// <c>(value, true)</c> when the snapshot sees a value-write for <paramref name="key"/>;
    /// <c>(default, false)</c> when the key is absent or its newest visible version is a
    /// tombstone.
    /// </returns>
    /// <exception cref="ObjectDisposedException">The snapshot has been disposed.</exception>
    Task<(TValue Value, bool Found)> TryGetValueAsync(TKey key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams every <c>(key, value)</c> pair visible under this snapshot whose key lies
    /// in the inclusive range <c>[<paramref name="startKey"/>, <paramref name="endKey"/>]</c>,
    /// in ascending key order.
    /// </summary>
    /// <param name="startKey">The inclusive lower bound of the range.</param>
    /// <param name="endKey">The inclusive upper bound of the range.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>The visible entries in ascending key order.</returns>
    /// <remarks>
    /// Tombstones are applied per-key under the visibility rule; a key whose newest
    /// visible version is a tombstone is absent from the stream.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The snapshot has been disposed.</exception>
    IAsyncEnumerable<KeyValuePair<TKey, TValue>> GetRangeAsync(
        TKey startKey, TKey endKey, CancellationToken cancellationToken = default);
}
