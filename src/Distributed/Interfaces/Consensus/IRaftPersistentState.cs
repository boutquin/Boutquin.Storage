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
/// Persistence seam for a Raft node's two crash-survivable state words —
/// <see cref="CurrentTerm"/> and <see cref="VotedFor"/>. Both must be flushed to durable
/// storage before any RPC referencing them is sent (Raft paper §5.2; DDIA 2e Ch. 10
/// p. 366–370).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a seam.</b> Without persistent term + vote, a crash and restart loses both,
/// which can elect two leaders in the same term (split brain) — a textbook Raft
/// <i>safety</i> violation, not a perf gap. The in-memory default ships at v2.0.0 GA so
/// the canonical formulation works out of the box for in-process reference scenarios;
/// file-backed implementations land post-GA as a binary-compatible substitute via the same
/// ctor parameter.
/// </para>
/// <para>
/// <b>Two state words, not three.</b> The Raft paper's persistent state is
/// <c>currentTerm</c>, <c>votedFor</c>, and the log itself. This interface covers the first
/// two; the log lives behind <see cref="IRaftLog{TCommand}"/>.
/// </para>
/// <para>
/// <b>Threading contract.</b> Implementations need not be thread-safe — the consuming
/// <see cref="Boutquin.Storage.Distributed.Consensus.RaftNode{TCommand}"/> is single-tick-driven
/// and serialises every state mutation through the cluster's tick loop.
/// </para>
/// <para>
/// <b>Reference:</b> Ongaro &amp; Ousterhout (2014); Kleppmann &amp; Riccomini,
/// <i>Designing Data-Intensive Applications</i>, 2nd ed. (O'Reilly, 2026), Ch. 10
/// p. 366–370.
/// </para>
/// </remarks>
public interface IRaftPersistentState
{
    /// <summary>
    /// The latest term the node has seen — incremented monotonically as the node observes
    /// higher terms in incoming RPCs or starts an election. Persistent on disk in
    /// production deployments; volatile in the in-memory default.
    /// </summary>
    long CurrentTerm { get; set; }

    /// <summary>
    /// The candidate id the node voted for in <see cref="CurrentTerm"/>, or
    /// <see langword="null"/> if the node has not voted yet in this term. Cleared (set to
    /// <see langword="null"/>) when the node steps down to a strictly higher term.
    /// </summary>
    string? VotedFor { get; set; }

    /// <summary>
    /// Persists a term transition and its vote as a single atomic durable update. Every
    /// term change pairs with a vote change (StartElection: vote-for-self; StepDown: vote
    /// cleared), and the Raft paper requires the durable (term, vote) pair to be internally
    /// consistent — persisting them through two independent setter writes opens a crash
    /// window where the new term sits on disk next to the PREVIOUS term's vote, making the
    /// restarted node refuse (or misdirect) its vote for the new term.
    /// </summary>
    /// <param name="term">The new current term.</param>
    /// <param name="votedFor">The vote recorded for that term, or <see langword="null"/> for no vote.</param>
    void SetTermAndVote(long term, string? votedFor);
}
