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
namespace Boutquin.Storage.Infrastructure.DistributedSystems;

/// <summary>
/// A gossip protocol implementation for disseminating state across nodes in a distributed system.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why ConcurrentDictionary?</b> Gossip sync messages may arrive from multiple nodes concurrently.
/// ConcurrentDictionary provides thread-safe reads and writes without explicit locking, which matches
/// the gossip pattern where any thread may process an incoming sync message at any time.
/// </para>
///
/// <para>
/// <b>Why version-based merging?</b> When processing a sync message, we compare version numbers
/// to decide whether to accept the remote state. Higher version wins because it represents a more
/// recent update. This is a simple but effective conflict resolution strategy for state that is
/// owned by a single node (each node only updates its own entry).
/// </para>
/// </remarks>
/// <typeparam name="TNodeId">The type used to identify nodes.</typeparam>
/// <typeparam name="TState">The type representing a node's state.</typeparam>
public sealed class GossipProtocol<TNodeId, TState> : IGossipProtocol<TNodeId, TState>
    where TNodeId : notnull
{
    private readonly ConcurrentDictionary<TNodeId, (TState State, long Version)> _clusterState = new();

    /// <inheritdoc/>
    public void UpdateLocalState(TNodeId nodeId, TState state)
    {
        ArgumentNullException.ThrowIfNull(nodeId);

        _clusterState.AddOrUpdate(
            nodeId,
            _ => (state, 1),
            (_, existing) => (state, existing.Version + 1));
    }

    /// <inheritdoc/>
    public IReadOnlyDictionary<TNodeId, (TState State, long Version)> GetClusterState()
    {
        return new Dictionary<TNodeId, (TState State, long Version)>(_clusterState);
    }

    /// <inheritdoc/>
    public IReadOnlyDictionary<TNodeId, (TState State, long Version)> PrepareSyncMessage(TNodeId fromNode)
    {
        ArgumentNullException.ThrowIfNull(fromNode);

        // Return a snapshot of the current cluster state
        return new Dictionary<TNodeId, (TState State, long Version)>(_clusterState);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <b>Thread safety of version comparison:</b> ConcurrentDictionary.AddOrUpdate retries the
    /// updateValueFactory if the internal CAS (compare-and-swap) fails, so concurrent calls to
    /// UpdateLocalState will cause the factory to re-execute with the latest existing value. This
    /// makes the version comparison eventually consistent without requiring an explicit lock.
    /// </remarks>
    public void ProcessSyncMessage(IReadOnlyDictionary<TNodeId, (TState State, long Version)> remoteState)
    {
        ArgumentNullException.ThrowIfNull(remoteState);

        foreach (var kvp in remoteState)
        {
            _clusterState.AddOrUpdate(
                kvp.Key,
                _ => kvp.Value,
                (_, existing) => kvp.Value.Version > existing.Version ? kvp.Value : existing);
        }
    }
}
