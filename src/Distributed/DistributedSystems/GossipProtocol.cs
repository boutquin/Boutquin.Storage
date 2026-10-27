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
namespace Boutquin.Storage.Distributed.DistributedSystems;

/// <summary>
/// A gossip protocol implementation for disseminating state across nodes in a distributed system.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope:</b> In-process reference implementation in <c>Boutquin.Storage.Distributed</c>,
/// which is not published to NuGet (see <c>src/Distributed/README.md</c>). Not durable
/// across process restart, not intended for production deployment. Cluster-membership gossip
/// is implemented for educational fidelity to DDIA 2e Ch. 6 and Ch. 10, but production-grade extensions
/// — a phi-accrual failure detector, anti-entropy modes, and suspicion-timer tuning — are not included. For production use, prefer Akka.NET cluster gossip or the gossip layer of Hashicorp memberlist.
/// </para>
///
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
    public IReadOnlyDictionary<TNodeId, (TState State, long Version)> PrepareSyncMessage()
    {
        // Return a snapshot of the entire current cluster state.
        // Each node propagates its full view; the recipient merges only entries with strictly
        // higher version numbers (see ProcessSyncMessage). Sending the complete view is correct
        // for this reference implementation — production deployments may apply digest-based
        // filtering (Demers et al. 1987 §3) for bandwidth efficiency.
        return new Dictionary<TNodeId, (TState State, long Version)>(_clusterState);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <b>Thread safety of version comparison:</b> implemented as an explicit TryGetValue +
    /// TryUpdate CAS loop rather than <see cref="ConcurrentDictionary{TKey, TValue}.AddOrUpdate(TKey, Func{TKey, TValue}, Func{TKey, TValue, TValue})"/>.
    /// <c>AddOrUpdate</c>'s update factory can observe a stale <c>existing</c> value, pass the
    /// <c>incoming.Version &gt; existing.Version</c> comparison, and then have its CAS
    /// succeed after a concurrent higher-version update lands — installing an older version
    /// over the newer one. The explicit TryUpdate call binds the comparison-value and the CAS
    /// to the same <c>existing</c> snapshot: either no one raced and we install, or we
    /// lose the CAS and re-read. The per-key Version sequence is therefore monotone under any
    /// interleaving (Demers et al. 1987 §3 on anti-entropy propagation; DDIA 2e Ch. 10).
    /// </remarks>
    public void ProcessSyncMessage(IReadOnlyDictionary<TNodeId, (TState State, long Version)> remoteState)
    {
        ArgumentNullException.ThrowIfNull(remoteState);

        foreach (var kvp in remoteState)
        {
            var incoming = kvp.Value;
            while (true)
            {
                if (!_clusterState.TryGetValue(kvp.Key, out var existing))
                {
                    if (_clusterState.TryAdd(kvp.Key, incoming))
                    {
                        break;
                    }
                    continue; // Another thread added an entry between TryGetValue and TryAdd — re-read.
                }

                if (incoming.Version <= existing.Version)
                {
                    break; // Stale or equal — leave the stored entry untouched.
                }

                if (_clusterState.TryUpdate(kvp.Key, incoming, existing))
                {
                    break;
                }
                // CAS lost — the stored value changed between TryGetValue and TryUpdate. Re-read
                // and re-evaluate; the loop always terminates because the stored Version is
                // monotonically non-decreasing.
            }
        }
    }
}
