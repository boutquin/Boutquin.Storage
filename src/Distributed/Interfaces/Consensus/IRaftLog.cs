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
namespace Boutquin.Storage.Distributed.Interfaces.Consensus;

/// <summary>
/// Persistence seam for a Raft node's replicated log. Each entry pairs the term in which it
/// was assigned with an immutable command; the engine exposes append, truncate, indexed read,
/// and slice operations that match every <c>_log</c> mutation site in
/// <see cref="Boutquin.Storage.Distributed.Consensus.RaftNode{TCommand}"/>.
/// </summary>
/// <typeparam name="TCommand">The immutable command type the state machine processes.</typeparam>
/// <remarks>
/// <para>
/// <b>Why a seam.</b> Raft's safety properties depend on the log being persisted across
/// crashes (Raft paper §5.2; DDIA 2e Ch. 10 p. 366–370). The in-memory default ships at
/// v2.0.0 GA so the canonical formulation works out of the box for in-process reference
/// scenarios; file-backed implementations can be injected through the same constructor parameter.
/// </para>
/// <para>
/// <b>Threading contract.</b> Implementations need not be thread-safe — the consuming
/// <see cref="Boutquin.Storage.Distributed.Consensus.RaftNode{TCommand}"/> is single-tick-driven
/// and serialises every log mutation through the cluster's tick loop. File-backed
/// implementations may serialise their own I/O internally; in-memory implementations need
/// no synchronisation.
/// </para>
/// <para>
/// <b>Command ownership.</b> Callers must not mutate a command after appending it or mutate a command
/// returned by a read. The in-memory implementation retains command references, including when
/// constructing a range result. File-backed implementations may serialize and decode private copies;
/// that stronger ownership guarantee is implementation-specific, not a requirement of this seam.
/// The in-process message transport also carries immutable command references.
/// </para>
/// <para>
/// <b>Indexing.</b> Log indices are 0-based <see cref="int"/>s, matching
/// <see cref="Boutquin.Storage.Distributed.Consensus.RaftNode{TCommand}"/>'s on-the-wire
/// formulation. The Raft paper uses 1-based indexing; this implementation uses 0-based with
/// <c>-1</c> as the "no entries replicated yet" sentinel inside the node.
/// </para>
/// <para>
/// <b>Reference:</b> Ongaro &amp; Ousterhout (2014); Kleppmann &amp; Riccomini,
/// <i>Designing Data-Intensive Applications</i>, 2nd ed. (O'Reilly, 2026), Ch. 10
/// p. 366–370.
/// </para>
/// </remarks>
public interface IRaftLog<TCommand>
{
    /// <summary>The total number of entries currently in the log.</summary>
    int Count { get; }

    /// <summary>
    /// Reads the entry at <paramref name="index"/>. Throws
    /// <see cref="ArgumentOutOfRangeException"/> when <paramref name="index"/> is outside
    /// <c>[0, <see cref="Count"/>)</c>.
    /// </summary>
    (long Term, TCommand Command) GetEntry(int index);

    /// <summary>Appends a single <paramref name="entry"/> to the tail of the log.</summary>
    void Append((long Term, TCommand Command) entry);

    /// <summary>
    /// Truncates the log so that every entry at index <paramref name="fromIndex"/> and
    /// beyond is removed. <paramref name="fromIndex"/> must satisfy
    /// <c>0 ≤ fromIndex ≤ <see cref="Count"/></c>; <c>fromIndex == Count</c> is a no-op.
    /// Used by AppendEntries when a follower's log has diverged from the leader at
    /// <paramref name="fromIndex"/> onward.
    /// </summary>
    void TruncateFrom(int fromIndex);

    /// <summary>
    /// Returns a slice of the log starting at <paramref name="startIndex"/> and containing
    /// <paramref name="count"/> entries. <paramref name="startIndex"/> + <paramref name="count"/>
    /// must not exceed <see cref="Count"/>. Used by the leader to construct AppendEntries
    /// requests for a follower whose <c>nextIndex</c> trails the leader's tail.
    /// </summary>
    IReadOnlyList<(long Term, TCommand Command)> GetRange(int startIndex, int count);
}
