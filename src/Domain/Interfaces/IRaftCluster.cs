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
/// Defines a Raft consensus cluster that coordinates multiple <see cref="IRaftNode{TCommand}"/> instances.
///
/// <para><b>Reference:</b> Kleppmann, <i>Designing Data-Intensive Applications</i> (O'Reilly, 2017), Ch. 9 —
/// Raft provides total order broadcast via leader election + log replication. Key invariants:
/// election safety (at most one leader per term), log matching (if two logs contain an entry with the
/// same index and term, all preceding entries are identical).</para>
/// </summary>
/// <typeparam name="TCommand">The command type.</typeparam>
public interface IRaftCluster<TCommand>
{
    /// <summary>
    /// Adds a node to the cluster.
    /// </summary>
    void AddNode(IRaftNode<TCommand> node);

    /// <summary>
    /// Forces an election timeout on a specific node, causing it to start an election.
    /// </summary>
    Task TriggerElectionAsync(string nodeId, CancellationToken ct = default);

    /// <summary>
    /// Advances the cluster by one logical tick — delivers and processes pending messages.
    /// </summary>
    Task TickAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the current leader node, or null if no leader is known.
    /// </summary>
    IRaftNode<TCommand>? GetLeader();

    /// <summary>
    /// Returns all nodes in the cluster.
    /// </summary>
    IReadOnlyList<IRaftNode<TCommand>> GetNodes();
}
