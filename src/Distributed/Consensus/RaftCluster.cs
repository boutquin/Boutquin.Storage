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
namespace Boutquin.Storage.Distributed.Consensus;

/// <summary>
/// A Raft cluster that coordinates message routing between <see cref="RaftNode{TCommand}"/> instances.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope:</b> In-process reference implementation in <c>Boutquin.Storage.Distributed</c>,
/// which is not published to NuGet (see <c>src/Distributed/README.md</c>). Not durable
/// across process restart, not intended for production deployment. The Raft consensus algorithm (Ongaro &amp; Ousterhout, 2014)
/// is implemented for educational fidelity to DDIA 2e Ch. 10, but production-grade extensions
/// — pre-vote, log compaction / snapshot install, joint-consensus reconfiguration, and leadership-transfer / read-index optimisations — are not included. File-backed term/vote/log seams are available, but membership must be restored explicitly from the caller's unchanged membership manifest. For production use, prefer <a href="https://github.com/dotnet/dotNext">dotNext Raft</a> or an embedded Raft integration.
/// </para>
///
/// <para>
/// <b>How it works:</b> The cluster maintains a list of nodes and routes messages between them.
/// Each <see cref="TickAsync"/> call delivers pending outbox messages to target nodes' inboxes,
/// then processes all inbox messages on each node.
/// </para>
///
/// <para>
/// <b>Why simulated networking?</b> This implementation uses in-memory queues rather than real network
/// sockets, making it suitable for unit testing and educational exploration of the Raft algorithm.
/// </para>
/// </remarks>
/// <typeparam name="TCommand">The command type.</typeparam>
public sealed class RaftCluster<TCommand> : IRaftCluster<TCommand>
{
    private readonly Dictionary<string, RaftNode<TCommand>> _nodes = [];
    private readonly StaticRaftMembership _membership;

    /// <summary>Creates a cluster accepting only fresh followers before execution.</summary>
    public RaftCluster() => _membership = new StaticRaftMembership();

    /// <summary>Creates a cluster for restoring an unchanged, caller-recorded membership.</summary>
    /// <param name="restoredMemberIds">Every original member identity. All must be registered before execution.</param>
    /// <remarks>The persistence seams store terms, votes and entries, not membership. The caller must supply
    /// the original set; this constructor cannot prove its historical accuracy. It does not implement reconfiguration.</remarks>
    /// <exception cref="ArgumentNullException">The membership is null.</exception>
    /// <exception cref="ArgumentException">The membership is empty or contains blank or duplicate identities.</exception>
    public RaftCluster(IEnumerable<string> restoredMemberIds)
    {
        ArgumentNullException.ThrowIfNull(restoredMemberIds);
        _membership = new StaticRaftMembership(restoredMemberIds);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <b>Before execution only.</b> Fresh clusters reject followers with prior term, vote or log history.
    /// Restored clusters accept only their declared original membership, before any message processing.
    /// Joint-consensus reconfiguration
    /// (Ongaro &amp; Ousterhout §6 / DDIA 2e) is out of scope for this reference
    /// implementation; membership changes must be applied before the cluster begins electing.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Thrown after execution begins, when any node is not a follower, or when a node's history or identity
    /// conflicts with the declared fresh/restored membership.
    /// </exception>
    public void AddNode(IRaftNode<TCommand> node)
    {
        if (node is not RaftNode<TCommand> raftNode)
        {
            throw new ArgumentException("Node must be a RaftNode<TCommand>.", nameof(node));
        }

        _membership.ValidateAddition(raftNode, _nodes);
        _nodes.Add(raftNode.NodeId, raftNode);

        // Update peer lists for all nodes
        foreach (var (id, n) in _nodes)
        {
            n.PeerIds.Clear();
            foreach (var otherId in _nodes.Keys)
            {
                if (otherId != id)
                {
                    n.PeerIds.Add(otherId);
                }
            }
        }
    }

    /// <inheritdoc />
    public Task TriggerElectionAsync(string nodeId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_nodes.TryGetValue(nodeId, out var node))
        {
            _membership.Start(_nodes.Count);
            node.StartElection();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task TickAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _membership.Start(_nodes.Count);
        // Deliver all outbox messages to target inboxes
        foreach (var node in _nodes.Values)
        {
            while (node.Outbox.TryDequeue(out var outgoing))
            {
                if (_nodes.TryGetValue(outgoing.TargetNodeId, out var targetNode))
                {
                    targetNode.Inbox.Enqueue(outgoing.Message);
                }
            }
        }

        // Process all inbox messages
        foreach (var node in _nodes.Values)
        {
            node.ProcessMessages();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public IRaftNode<TCommand>? GetLeader()
    {
        foreach (var node in _nodes.Values)
        {
            if (node.State == RaftNodeState.Leader)
            {
                return node;
            }
        }

        return null;
    }

    /// <inheritdoc />
    public IReadOnlyList<IRaftNode<TCommand>> GetNodes()
    {
        return _nodes.Values.Cast<IRaftNode<TCommand>>().ToList();
    }
}
