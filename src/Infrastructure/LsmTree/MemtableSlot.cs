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
using Boutquin.Storage.Infrastructure.DataStructures;

namespace Boutquin.Storage.Infrastructure.LsmTree;

/// <summary>
/// Per-memtable lifecycle state. Drives the engine's "active + immutable + flushing +
/// persisted" state machine (DDIA Ch. 4 p. 117–119). Faulted is a terminal off-diagram
/// state reached when the flusher's SST write or commit step throws.
/// </summary>
internal enum MemtableSlotState : byte
{
    /// <summary>
    /// Writable. Exactly one slot is in this state at any instant (engine invariant L1).
    /// </summary>
    Active = 0,

    /// <summary>
    /// Read-only, queued in <c>_immutables</c>, waiting for the background flusher.
    /// </summary>
    Sealed = 1,

    /// <summary>
    /// Read-only, queued in <c>_immutables</c>, currently being written to an SSTable by
    /// the flusher. Still visible to readers.
    /// </summary>
    Flushing = 2,

    /// <summary>
    /// Removed from <c>_immutables</c>; segment is durable on disk. The engine holds no
    /// further references; data structures are eligible for GC.
    /// </summary>
    Persisted = 3,

    /// <summary>
    /// Terminal state reached when the flusher's SST write or commit step throws. Promotes
    /// the engine to its Faulted state; reads continue from in-memory data, writes throw.
    /// </summary>
    Faulted = 4,
}

/// <summary>
/// Bundles the state of a single memtable instance — its in-memory tree, its tombstone
/// map, its lifecycle state, the maximum seqno observed at the moment of seal, and the
/// <see cref="TaskCompletionSource{TResult}"/> that fires when the slot reaches
/// <see cref="MemtableSlotState.Persisted"/>. Replaces the engine's former bare
/// <c>_memTable</c> + <c>_memTableTombstones</c> field pair so a finite collection of
/// concurrent memtables (one Active + a bounded queue of Immutables) can rotate cleanly.
/// </summary>
/// <remarks>
/// <para>
/// <b>DDIA Ch. 4 p. 117–119.</b> The classical LSM design holds an active memtable (writable)
/// while a queue of immutable memtables drains to disk in the background. The slot is the
/// per-memtable record that walks through that lifecycle: <see cref="MemtableSlotState.Active"/>
/// → <see cref="MemtableSlotState.Sealed"/> → <see cref="MemtableSlotState.Flushing"/> →
/// <see cref="MemtableSlotState.Persisted"/>. Reads remain consistent through every transition
/// because a slot stays in the engine's <c>_immutables</c> list until the SSTable that
/// captures its data is durable and the manifest reflects it.
/// </para>
/// <para>
/// <b>Why mutable fields, not a record.</b> The slot mutates over its lifetime
/// (<see cref="State"/> advances; <see cref="MaxSeqnoAtSeal"/> is captured at seal time).
/// Records would conflict with the project's <c>ValueObjects_ShouldBeRecordTypes</c>
/// architecture rule if they were named with that convention; this type is engine-internal
/// state, lives under <c>Boutquin.Storage.Infrastructure.LsmTree</c> as
/// <c>internal sealed class</c>, and is exempt from that rule.
/// </para>
/// </remarks>
/// <typeparam name="TKey">Key type. Same constraints the engine enforces.</typeparam>
/// <typeparam name="TValue">Value type. Same constraints the engine enforces.</typeparam>
internal sealed class MemtableSlot<TKey, TValue>
    where TKey : IStorageKey<TKey>, new()
    where TValue : ISerializable<TValue>, new()
{
    /// <summary>
    /// Monotonic identity assigned at allocation by the engine's slot-generation counter.
    /// Sole purpose: equality identity for the flusher's "is this slot still queued at the
    /// head?" re-check after the SST write completes. Not on-disk; lives only in memory.
    /// </summary>
    public ulong Generation { get; }

    /// <summary>
    /// The in-memory MemTable that holds this slot's writes. Mutated only while this slot
    /// is the engine's active; treated as read-only once <see cref="State"/> leaves
    /// <see cref="MemtableSlotState.Active"/>.
    /// </summary>
    public RedBlackTree<TKey, MemTableValue<TValue>> Tree { get; }

    /// <summary>
    /// Tombstone seqno map for this slot. Keys deleted while this slot was the active
    /// memtable map to the tombstone's seqno. Mutated only while this slot is active.
    /// </summary>
    public Dictionary<TKey, ulong> Tombstones { get; }

    /// <summary>
    /// Per-slot lifecycle state. Read under the engine's lock; transitions are driven by
    /// the engine's <c>SealActiveLocked</c>, the flusher's commit step, and
    /// <c>EnterFaultedState</c>.
    /// </summary>
    public MemtableSlotState State { get; internal set; }

    /// <summary>
    /// The engine's <c>_lastAssignedSequenceNumber</c> at the instant this slot is sealed.
    /// Captured so the flusher can populate <c>_segmentMaxSeqnos</c> with the right value
    /// without re-walking the slot's tree. <c>0UL</c> while the slot is still
    /// <see cref="MemtableSlotState.Active"/>.
    /// </summary>
    public ulong MaxSeqnoAtSeal { get; internal set; }

    /// <summary>
    /// Completes (with <see langword="true"/>) when this slot reaches
    /// <see cref="MemtableSlotState.Persisted"/>. The engine's <c>FlushAsync</c> awaits
    /// this task for every in-flight slot plus any slot it just sealed. Faulted with the
    /// originating exception when the flusher fails; cancelled when the engine is disposed
    /// or <c>ClearAsync</c> discards the slot. The <c>TaskCompletionSource</c> behaviour
    /// is <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/> so a continuation
    /// completing on the flusher thread does not run synchronously inside the engine lock.
    /// </summary>
    public TaskCompletionSource<bool> Persisted { get; }

    /// <summary>
    /// Constructs a freshly-active slot. Callers (the engine) must increment the generation
    /// counter and pass the resulting value here.
    /// </summary>
    public MemtableSlot(
        ulong generation,
        RedBlackTree<TKey, MemTableValue<TValue>> tree,
        Dictionary<TKey, ulong> tombstones)
    {
        Generation = generation;
        Tree = tree;
        Tombstones = tombstones;
        State = MemtableSlotState.Active;
        MaxSeqnoAtSeal = 0UL;
        Persisted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
