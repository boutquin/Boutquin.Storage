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
namespace Boutquin.Storage.Distributed.Interfaces.DistributedSystems;

/// <summary>
/// Represents a gossip protocol for disseminating state information across nodes in a distributed system.
///
/// <para>Gossip protocols work by having each node periodically exchange state information with a random
/// subset of other nodes. Over time, all nodes converge to the same view of the cluster state. This is
/// an eventually consistent approach — updates propagate epidemically through the cluster.</para>
///
/// <para><b>Applications:</b></para>
/// <para>- <b>Failure detection:</b> Nodes that stop gossiping are suspected of failure.</para>
/// <para>- <b>Membership:</b> Tracking which nodes are alive and their current roles.</para>
/// <para>- <b>State dissemination:</b> Propagating configuration changes, schema updates, or load metrics.</para>
///
/// <para><b>Versioning:</b> Each node's state is versioned with a monotonically increasing counter.
/// When merging, the higher version wins. This prevents stale updates from overwriting newer state.</para>
///
/// <para><b>Reference:</b> Kleppmann, <i>Designing Data-Intensive Applications</i> (O'Reilly, 2nd ed., 2026), Ch. 6 — "Replication",
/// section on gossip protocols for failure detection and state dissemination. Used in Cassandra and Riak.</para>
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope:</b> In-process reference implementation in <c>Boutquin.Storage.Distributed</c>,
/// which is not published to NuGet (see <c>src/Distributed/README.md</c>). Not durable
/// across process restart, not intended for production deployment. The gossip-protocol contract
/// is implemented for educational fidelity to DDIA 2e Ch. 6, but production-grade extensions
/// — phi-accrual failure detection and anti-entropy modes — are not included. For production use, prefer Akka.NET cluster gossip.
/// </para>
/// </remarks>
/// <typeparam name="TNodeId">The type used to identify nodes.</typeparam>
/// <typeparam name="TState">The type representing a node's state.</typeparam>
public interface IGossipProtocol<TNodeId, TState>
    where TNodeId : notnull
{
    /// <summary>
    /// Updates the local node's state and increments its version.
    /// </summary>
    /// <remarks>
    /// <b>Single-writer precondition.</b> Each node may update only its OWN entry through
    /// this method; every other node's entry must arrive exclusively via
    /// <c>ProcessSyncMessage</c>. Version-based merging resolves conflicts only because a
    /// single writer makes each (version → state) pair unique per node; two replicas that
    /// both author version <i>v</i> for the same node id with different states would keep
    /// their own value on the equal-version tie and never converge.
    /// </remarks>
    /// <param name="nodeId">The identifier of the local node.</param>
    /// <param name="state">The new state for the node.</param>
    void UpdateLocalState(TNodeId nodeId, TState state);

    /// <summary>
    /// Returns the current known state of all nodes in the cluster.
    /// </summary>
    /// <returns>A dictionary mapping node identifiers to their state and version.</returns>
    IReadOnlyDictionary<TNodeId, (TState State, long Version)> GetClusterState();

    /// <summary>
    /// Prepares a sync message containing the current cluster state to send to another node.
    /// </summary>
    /// <returns>
    /// A snapshot of the current cluster state. Recipients merge only entries with a strictly
    /// higher version than what they already hold (see <see cref="ProcessSyncMessage"/>).
    /// Production deployments may apply digest-based filtering (Demers et al. 1987 §3) to reduce
    /// bandwidth; this reference implementation sends the full view.
    /// </returns>
    IReadOnlyDictionary<TNodeId, (TState State, long Version)> PrepareSyncMessage();

    /// <summary>
    /// Processes a sync message received from another node, merging in newer state.
    /// </summary>
    /// <param name="remoteState">The cluster state received from the remote node.</param>
    void ProcessSyncMessage(IReadOnlyDictionary<TNodeId, (TState State, long Version)> remoteState);
}
