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
/// <para><b>Reference:</b> Kleppmann, <i>Designing Data-Intensive Applications</i> (O'Reilly, 2017), Ch. 5 — "Replication",
/// section on gossip protocols for failure detection and state dissemination. Used in Cassandra and Riak.</para>
/// </summary>
/// <typeparam name="TNodeId">The type used to identify nodes.</typeparam>
/// <typeparam name="TState">The type representing a node's state.</typeparam>
public interface IGossipProtocol<TNodeId, TState>
    where TNodeId : notnull
{
    /// <summary>
    /// Updates the local node's state and increments its version.
    /// </summary>
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
    /// <param name="fromNode">The identifier of the node sending the sync.</param>
    /// <returns>The current cluster state snapshot to be sent.</returns>
    IReadOnlyDictionary<TNodeId, (TState State, long Version)> PrepareSyncMessage(TNodeId fromNode);

    /// <summary>
    /// Processes a sync message received from another node, merging in newer state.
    /// </summary>
    /// <param name="remoteState">The cluster state received from the remote node.</param>
    void ProcessSyncMessage(IReadOnlyDictionary<TNodeId, (TState State, long Version)> remoteState);
}
