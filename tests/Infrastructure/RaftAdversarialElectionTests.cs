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
namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// Adversarial election-safety tests for <see cref="RaftNode{TCommand}"/> closing the named Raft
/// test-coverage gaps: a split vote resolving via a higher-term re-election, a leader stepping
/// down on observing a higher term in a response, and the empty-log election boundary. Each
/// targets an Ongaro &amp; Ousterhout 2014 Figure 2 safety rule that the happy-path election tests
/// never exercise.
/// </summary>
public sealed class RaftAdversarialElectionTests
{
    private static async Task RunTicksAsync(RaftCluster<string> cluster, int count)
    {
        for (var i = 0; i < count; i++)
        {
            await cluster.TickAsync().ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Split vote → higher-term re-election. In an even (2-node) cluster both nodes can become
    /// candidates in the same term, each voting for itself; neither reaches the 2-of-2 majority,
    /// so the term is split with no leader. A fresh election by one node bumps the term; the peer,
    /// seeing the higher term in the RequestVote, steps down and grants — a leader emerges at the
    /// new, higher term. Pins that a split term does NOT produce a leader and that the resolving
    /// election's term strictly exceeds the split term.
    /// </summary>
    [Fact]
    public async Task SplitVote_ResolvesViaHigherTermReElection()
    {
        var cluster = new RaftCluster<string>();
        var node1 = new RaftNode<string>("node-1");
        var node2 = new RaftNode<string>("node-2");
        cluster.AddNode(node1);
        cluster.AddNode(node2);

        // Both nodes start an election in the same term — each votes for itself.
        await cluster.TriggerElectionAsync("node-1").ConfigureAwait(true);
        await cluster.TriggerElectionAsync("node-2").ConfigureAwait(true);
        await RunTicksAsync(cluster, 3).ConfigureAwait(true);

        // Split vote: neither candidate could win its own term, so no leader exists and both
        // remain candidates at term 1.
        var splitTerm = node1.CurrentTerm;
        cluster.GetLeader().Should().BeNull("a split vote in an even cluster yields no leader");
        node1.State.Should().Be(RaftNodeState.Candidate);
        node2.State.Should().Be(RaftNodeState.Candidate);

        // A fresh election bumps the term; the peer sees the higher term and grants.
        await cluster.TriggerElectionAsync("node-1").ConfigureAwait(true);
        await RunTicksAsync(cluster, 4).ConfigureAwait(true);

        var leader = cluster.GetLeader();
        leader.Should().NotBeNull("the higher-term re-election resolves the split");
        leader!.NodeId.Should().Be("node-1");
        leader.CurrentTerm.Should().BeGreaterThan(splitTerm,
            "the resolving election ran in a strictly higher term than the split");
    }

    /// <summary>
    /// Leader step-down on a higher response term. A leader that receives ANY response carrying a
    /// term greater than its own must immediately revert to follower and adopt the higher term
    /// (Figure 2 — "If RPC request or response contains term T &gt; currentTerm: set currentTerm =
    /// T, convert to follower"). Drives a single-node leader, injects an AppendEntriesResponse from
    /// a phantom peer carrying a higher term, and asserts the leader steps down.
    /// </summary>
    [Fact]
    public async Task Leader_StepsDown_OnHigherTermInResponse()
    {
        var cluster = new RaftCluster<string>();
        var leaderNode = new RaftNode<string>("node-1");
        var peerNode = new RaftNode<string>("node-2");
        cluster.AddNode(leaderNode);
        cluster.AddNode(peerNode);

        // Elect node-1 leader.
        await cluster.TriggerElectionAsync("node-1").ConfigureAwait(true);
        await RunTicksAsync(cluster, 4).ConfigureAwait(true);
        cluster.GetLeader().Should().NotBeNull();
        cluster.GetLeader()!.NodeId.Should().Be("node-1");
        var leaderTerm = leaderNode.CurrentTerm;

        // A peer at a strictly higher term responds — model a stale leader whose term was
        // superseded. The leader must step down and adopt the higher term.
        var higherTerm = leaderTerm + 5;
        leaderNode.Inbox.Enqueue(new AppendEntriesResponse(higherTerm, false, "node-2", -1));
        await RunTicksAsync(cluster, 1).ConfigureAwait(true);

        leaderNode.State.Should().Be(RaftNodeState.Follower,
            "a leader observing a higher term in a response must revert to follower");
        leaderNode.CurrentTerm.Should().Be(higherTerm,
            "the leader adopts the higher term it observed");
        leaderNode.LeaderId.Should().BeNull("after stepping down the node knows no leader yet");
    }

    /// <summary>
    /// Empty-log election boundary. A node whose log is empty (no entries) must still be able to
    /// run an election: <c>lastLogIndex</c> is the -1 boundary and <c>lastLogTerm</c> is 0, which
    /// the log-up-to-date comparison (Figure 2 §5.4.1) must handle without an index-out-of-range
    /// fault. A single empty-log node elects itself; a peer with an equally-empty log grants.
    /// </summary>
    [Fact]
    public async Task EmptyLog_ElectionBoundary_ElectsLeaderWithoutFault()
    {
        var cluster = new RaftCluster<string>();
        var node1 = new RaftNode<string>("node-1");
        var node2 = new RaftNode<string>("node-2");
        var node3 = new RaftNode<string>("node-3");
        cluster.AddNode(node1);
        cluster.AddNode(node2);
        cluster.AddNode(node3);

        // No ProposeAsync has run — every node's log is empty. The election must still succeed
        // across the lastLogIndex = -1 / lastLogTerm = 0 boundary.
        await cluster.TriggerElectionAsync("node-1").ConfigureAwait(true);
        await RunTicksAsync(cluster, 4).ConfigureAwait(true);

        var leader = cluster.GetLeader();
        leader.Should().NotBeNull("an empty-log node must be electable");
        leader!.NodeId.Should().Be("node-1");
        leader.GetCommittedLog().Should().BeEmpty("no entries were ever proposed");
    }
}
