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
using Boutquin.Storage.Distributed.Consensus;

namespace Boutquin.Storage.Distributed.Simulation;

/// <summary>Routes the real RaftNode message queues through a deterministic faulty network.</summary>
/// <typeparam name="TCommand">Immutable Raft command type.</typeparam>
/// <remarks>Borrowed runtime and caller-owned nodes/persistence. Membership is fixed before the first election.
/// Elections are explicit scheduled actions; no wall-clock timers or automatic liveness guarantee are added.
/// Call Propose on the simulation driver, then Pump to transmit its outbox. Admission is not commitment:
/// only GetCommittedLog is evidence that a proposal was acknowledged by a majority.</remarks>
public sealed class RaftSimulation<TCommand>
{
    private readonly SimulatedNodeRuntime _runtime;
    private readonly Dictionary<string, RaftNode<TCommand>> _nodes = new(StringComparer.Ordinal);
    private readonly StaticRaftMembership _membership;

    /// <summary>Creates an inert adapter over a borrowed runtime.</summary>
    /// <param name="runtime">Non-null single-driver runtime.</param>
    /// <exception cref="ArgumentNullException">Runtime is null.</exception>
    public RaftSimulation(SimulatedNodeRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _runtime = runtime;
        _membership = new StaticRaftMembership();
    }

    /// <summary>Creates a simulation restoring a fixed, caller-recorded original membership.</summary>
    /// <param name="runtime">Borrowed single-driver runtime.</param>
    /// <param name="restoredMemberIds">All original identities; register every one before execution.</param>
    /// <remarks>The caller must retain the original membership independently of the term/vote/log seams.
    /// A different supplied set cannot be detected from those seams and is unsupported reconfiguration.</remarks>
    /// <exception cref="ArgumentNullException">Runtime or membership is null.</exception>
    /// <exception cref="ArgumentException">Membership is empty or contains blank or duplicate identities.</exception>
    public RaftSimulation(SimulatedNodeRuntime runtime, IEnumerable<string> restoredMemberIds)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(restoredMemberIds);
        _runtime = runtime;
        _membership = new StaticRaftMembership(restoredMemberIds);
    }

    /// <summary>Registers an actual Raft node before any election and synchronizes peer membership.</summary>
    /// <param name="node">Non-null fresh follower; the caller owns its persistence collaborators.</param>
    /// <exception cref="ArgumentNullException">Node is null.</exception>
    /// <exception cref="ArgumentException">Node identity is already registered.</exception>
    /// <exception cref="InvalidOperationException">An election has begun or node is not a follower.</exception>
    public void AddNode(RaftNode<TCommand> node)
    {
        ArgumentNullException.ThrowIfNull(node);
        _membership.ValidateAddition(node, _nodes);
        _runtime.AddNode(node.NodeId, message =>
        {
            node.Inbox.Enqueue((RaftMessage)message);
            node.ProcessMessages();
            Pump();
        });
        _nodes.Add(node.NodeId, node);
        foreach (var member in _nodes.Values)
        {
            member.PeerIds.Clear();
            member.PeerIds.AddRange(_nodes.Keys.Where(id => id != member.NodeId));
        }
    }

    /// <summary>Schedules an explicit election timeout on a node, subject to process pauses.</summary>
    /// <param name="nodeId">Registered Raft node.</param>
    /// <param name="delay">Nonnegative virtual delay.</param>
    /// <exception cref="KeyNotFoundException">Node is unknown.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Delay is negative.</exception>
    public void Elect(string nodeId, long delay = 0)
    {
        var node = _nodes[nodeId];
        ArgumentOutOfRangeException.ThrowIfNegative(delay);
        _membership.Start(_nodes.Count);
        _runtime.Schedule(nodeId, delay, () =>
        {
            node.StartElection();
            Pump();
        });
    }

    /// <summary>Drains current Raft outboxes into the network, preserving source iteration order.</summary>
    /// <remarks>Call after direct proposal admission; receiving protocol messages also pumps automatically.
    /// Does not retransmit lost messages or manufacture commit acknowledgments.</remarks>
    public void Pump()
    {
        _membership.Start(_nodes.Count);
        foreach (var node in _nodes.Values)
        {
            while (node.Outbox.TryDequeue(out var outgoing))
            {
                _runtime.Network.Send(node.NodeId, outgoing.TargetNodeId, outgoing.Message);
            }
        }
    }
}
