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
/// Defines a node in a Raft consensus cluster.
///
/// <para><b>Reference:</b> Kleppmann, <i>Designing Data-Intensive Applications</i> (O'Reilly, 2017), Ch. 9 —
/// Raft consensus algorithm (Ongaro &amp; Ousterhout, 2014). Guarantees that all nodes agree on the same
/// sequence of commands as long as a majority is alive.</para>
/// </summary>
/// <typeparam name="TCommand">The command type that the state machine processes.</typeparam>
public interface IRaftNode<TCommand>
{
    /// <summary>Gets the unique node identifier.</summary>
    string NodeId { get; }

    /// <summary>Gets the current state (Follower, Candidate, or Leader).</summary>
    RaftNodeState State { get; }

    /// <summary>Gets the current term number.</summary>
    long CurrentTerm { get; }

    /// <summary>Gets who this node voted for in the current term, or null if it hasn't voted.</summary>
    string? VotedFor { get; }

    /// <summary>Gets the known leader ID, or null if unknown.</summary>
    string? LeaderId { get; }

    /// <summary>
    /// Proposes a command to be appended to the replicated log.
    /// Only the leader accepts proposals; followers return false immediately.
    /// </summary>
    /// <param name="command">The command to propose.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// True if the command was committed immediately (only possible in single-node clusters).
    /// In multi-node clusters, the method appends the entry and sends AppendEntries RPCs but
    /// returns before replication completes. Use <see cref="IRaftCluster{TCommand}.TickAsync"/>
    /// to process responses and advance the commit index, then check
    /// <see cref="GetCommittedLog"/> for committed entries.
    /// </returns>
    Task<bool> ProposeAsync(TCommand command, CancellationToken ct = default);

    /// <summary>
    /// Returns the committed log entries.
    /// </summary>
    IReadOnlyList<(long Term, TCommand Command)> GetCommittedLog();
}
