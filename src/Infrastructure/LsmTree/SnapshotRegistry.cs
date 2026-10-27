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
namespace Boutquin.Storage.Infrastructure.LsmTree;

/// <summary>
/// In-memory registry of live <see cref="ISnapshot{TKey, TValue}"/> tokens for an
/// <see cref="LsmStorageEngine{TKey, TValue}"/>. Tracks the segment pins and seqno
/// watermark each snapshot holds so compaction can consult the registry before
/// physically deleting superseded segments and before stripping reachable tombstones
/// (see docs/snapshot-isolation.md for pinning + tombstone GC semantics).
/// </summary>
/// <remarks>
/// <para><b>Lifetime.</b> Entries are added inside the engine's write lock by
/// <c>AcquireSnapshotAsync</c> and removed by the snapshot's
/// <c>DisposeAsync</c> (also under the engine's lock). The registry is
/// process-local and non-durable — a crash drops every live snapshot
/// (snapshots are in-memory only, non-durable).</para>
///
/// <para><b>Concurrency.</b> The registry delegates to a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> so read accessors
/// (<see cref="GetMinSequenceNumber"/>, <see cref="CollectAllPinned"/>) can be invoked
/// outside the engine's lock without contending with new acquisitions.</para>
/// </remarks>
internal sealed class SnapshotRegistry
{
    private readonly ConcurrentDictionary<Guid, SnapshotState> _entries = new();

    /// <summary>
    /// Adds a snapshot to the registry. Called under the engine's write lock.
    /// </summary>
    public void Register(Guid id, SnapshotState state) => _entries[id] = state;

    /// <summary>
    /// Removes a snapshot from the registry. Idempotent — calling with an unknown id is a no-op.
    /// </summary>
    /// <returns><see langword="true"/> when an entry was removed; <see langword="false"/> when none was present.</returns>
    public bool Unregister(Guid id) => _entries.TryRemove(id, out _);

    /// <summary>
    /// The count of live snapshots.
    /// </summary>
    public int Count => _entries.Count;

    /// <summary>
    /// Minimum snapshot-acquisition sequence number across every live snapshot, or
    /// <see cref="ulong.MaxValue"/> when no snapshots are live. The tombstone GC rule
    /// strips tombstones with seqno <c>T</c> only when <c>min &gt; T</c>.
    /// </summary>
    public ulong GetMinSequenceNumber()
    {
        var min = ulong.MaxValue;
        foreach (var state in _entries.Values)
        {
            if (state.SequenceNumber < min)
            {
                min = state.SequenceNumber;
            }
        }
        return min;
    }

    /// <summary>
    /// Union of every segment filename pinned by any live snapshot.
    /// </summary>
    public HashSet<string> CollectAllPinned()
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var state in _entries.Values)
        {
            foreach (var name in state.PinnedSegmentFileNames)
            {
                result.Add(name);
            }
        }
        return result;
    }
}

/// <summary>
/// Per-snapshot tracking record maintained by <see cref="SnapshotRegistry"/>.
/// </summary>
/// <param name="SequenceNumber">The engine seqno at acquisition time.</param>
/// <param name="PinnedSegmentFileNames">
/// Names of the segment files the engine held at acquisition time. These files may not
/// be physically deleted while the snapshot is live, even after compaction has logically
/// superseded them.
/// </param>
internal sealed record SnapshotState(
    ulong SequenceNumber,
    IReadOnlySet<string> PinnedSegmentFileNames);
