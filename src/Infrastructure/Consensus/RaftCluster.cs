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
namespace Boutquin.Storage.Infrastructure.Consensus;

/// <summary>
/// A Raft cluster that coordinates message routing between <see cref="RaftNode{TCommand}"/> instances.
/// </summary>
/// <remarks>
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

    /// <inheritdoc />
    public void AddNode(IRaftNode<TCommand> node)
    {
        if (node is not RaftNode<TCommand> raftNode)
        {
            throw new ArgumentException("Node must be a RaftNode<TCommand>.", nameof(node));
        }

        _nodes[raftNode.NodeId] = raftNode;

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
        if (_nodes.TryGetValue(nodeId, out var node))
        {
            node.StartElection();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task TickAsync(CancellationToken ct = default)
    {
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
            if (node.State == Domain.Enums.RaftNodeState.Leader)
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
