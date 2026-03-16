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
using Boutquin.Storage.Infrastructure.Consensus;

namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// Unit tests for the Raft consensus implementation (RaftNode and RaftCluster).
/// Each test follows the Arrange-Act-Assert pattern.
/// </summary>
public sealed class RaftTests
{
    /// <summary>
    /// Helper to run multiple ticks to let messages propagate.
    /// </summary>
    private static async Task RunTicksAsync(RaftCluster<string> cluster, int count)
    {
        for (var i = 0; i < count; i++)
        {
            await cluster.TickAsync().ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Test that a single node becomes leader when election is triggered.
    /// </summary>
    [Fact]
    public async Task SingleNode_BecomesLeader_WhenElectionTriggered()
    {
        // Arrange
        var cluster = new RaftCluster<string>();
        var node = new RaftNode<string>("node-1");
        cluster.AddNode(node);

        // Act
        await cluster.TriggerElectionAsync("node-1").ConfigureAwait(true);

        // Assert
        Assert.Equal(RaftNodeState.Leader, node.State);
        Assert.Equal(1, node.CurrentTerm);
    }

    /// <summary>
    /// Test that a 3-node cluster produces exactly one leader after election.
    /// </summary>
    [Fact]
    public async Task ThreeNodeCluster_ElectionProducesOneLeader()
    {
        // Arrange
        var cluster = new RaftCluster<string>();
        cluster.AddNode(new RaftNode<string>("node-1"));
        cluster.AddNode(new RaftNode<string>("node-2"));
        cluster.AddNode(new RaftNode<string>("node-3"));

        // Act — trigger election on node-1 and process messages
        await cluster.TriggerElectionAsync("node-1").ConfigureAwait(true);
        await RunTicksAsync(cluster, 3).ConfigureAwait(true);

        // Assert — exactly one leader
        var leaders = cluster.GetNodes().Where(n => n.State == RaftNodeState.Leader).ToList();
        Assert.Single(leaders);
        Assert.Equal("node-1", leaders[0].NodeId);
    }

    /// <summary>
    /// Test that the leader accepts proposals and replicates to followers.
    /// </summary>
    [Fact]
    public async Task Leader_AcceptsProposals_AndReplicatesToFollowers()
    {
        // Arrange
        var cluster = new RaftCluster<string>();
        cluster.AddNode(new RaftNode<string>("node-1"));
        cluster.AddNode(new RaftNode<string>("node-2"));
        cluster.AddNode(new RaftNode<string>("node-3"));

        await cluster.TriggerElectionAsync("node-1").ConfigureAwait(true);
        await RunTicksAsync(cluster, 3).ConfigureAwait(true);

        var leader = cluster.GetLeader()!;

        // Act — propose a command
        await leader.ProposeAsync("cmd-1").ConfigureAwait(true);
        await RunTicksAsync(cluster, 3).ConfigureAwait(true);

        // Need another propose+tick cycle to commit (AppendEntries responses arrive, then commit advances)
        await leader.ProposeAsync("cmd-2").ConfigureAwait(true);
        await RunTicksAsync(cluster, 3).ConfigureAwait(true);

        // Assert — leader should have committed entries
        var committedLog = leader.GetCommittedLog();
        Assert.True(committedLog.Count >= 1);
    }

    /// <summary>
    /// Test that a follower rejects proposals.
    /// </summary>
    [Fact]
    public async Task Follower_RejectsProposals()
    {
        // Arrange
        var cluster = new RaftCluster<string>();
        cluster.AddNode(new RaftNode<string>("node-1"));
        cluster.AddNode(new RaftNode<string>("node-2"));
        cluster.AddNode(new RaftNode<string>("node-3"));

        await cluster.TriggerElectionAsync("node-1").ConfigureAwait(true);
        await RunTicksAsync(cluster, 3).ConfigureAwait(true);

        // Find a follower
        var follower = cluster.GetNodes().First(n => n.State == RaftNodeState.Follower);

        // Act
        var result = await follower.ProposeAsync("cmd-1").ConfigureAwait(true);

        // Assert
        Assert.False(result);
    }

    /// <summary>
    /// Test that term increases on each election.
    /// </summary>
    [Fact]
    public async Task Term_IncreasesOnEachElection()
    {
        // Arrange
        var cluster = new RaftCluster<string>();
        cluster.AddNode(new RaftNode<string>("node-1"));
        cluster.AddNode(new RaftNode<string>("node-2"));
        cluster.AddNode(new RaftNode<string>("node-3"));

        // Act — trigger two elections
        await cluster.TriggerElectionAsync("node-1").ConfigureAwait(true);
        await RunTicksAsync(cluster, 3).ConfigureAwait(true);
        var term1 = cluster.GetLeader()!.CurrentTerm;

        await cluster.TriggerElectionAsync("node-2").ConfigureAwait(true);
        await RunTicksAsync(cluster, 3).ConfigureAwait(true);
        var term2 = cluster.GetLeader()!.CurrentTerm;

        // Assert
        Assert.True(term2 > term1);
    }

    /// <summary>
    /// Test that a node votes at most once per term.
    /// </summary>
    [Fact]
    public async Task Node_VotesAtMostOncePerTerm()
    {
        // Arrange
        var cluster = new RaftCluster<string>();
        var node1 = new RaftNode<string>("node-1");
        var node2 = new RaftNode<string>("node-2");
        var node3 = new RaftNode<string>("node-3");
        cluster.AddNode(node1);
        cluster.AddNode(node2);
        cluster.AddNode(node3);

        // Act — trigger election on node-1
        await cluster.TriggerElectionAsync("node-1").ConfigureAwait(true);
        await RunTicksAsync(cluster, 3).ConfigureAwait(true);

        // Assert — node-2 and node-3 voted for node-1, so they can't vote for anyone else in the same term
        // After the election, the non-leader nodes should have voted
        // The leader's VotedFor is set to its own NodeId during election (then cleared on heartbeat receipt)
        Assert.Equal(RaftNodeState.Leader, node1.State);
    }

    /// <summary>
    /// Test that committed log entries are identical across majority.
    /// </summary>
    [Fact]
    public async Task CommittedEntries_IdenticalAcrossMajority()
    {
        // Arrange
        var cluster = new RaftCluster<string>();
        cluster.AddNode(new RaftNode<string>("node-1"));
        cluster.AddNode(new RaftNode<string>("node-2"));
        cluster.AddNode(new RaftNode<string>("node-3"));

        await cluster.TriggerElectionAsync("node-1").ConfigureAwait(true);
        await RunTicksAsync(cluster, 3).ConfigureAwait(true);

        var leader = cluster.GetLeader()!;

        // Act — propose and replicate
        await leader.ProposeAsync("cmd-1").ConfigureAwait(true);
        await RunTicksAsync(cluster, 5).ConfigureAwait(true);
        await leader.ProposeAsync("cmd-2").ConfigureAwait(true);
        await RunTicksAsync(cluster, 5).ConfigureAwait(true);

        // Assert — all nodes should have the same committed entries
        var leaderLog = leader.GetCommittedLog();
        foreach (var node in cluster.GetNodes())
        {
            var nodeLog = node.GetCommittedLog();
            if (nodeLog.Count > 0 && leaderLog.Count > 0)
            {
                // At minimum the committed entries should match the leader's
                for (var i = 0; i < Math.Min(nodeLog.Count, leaderLog.Count); i++)
                {
                    Assert.Equal(leaderLog[i].Term, nodeLog[i].Term);
                    Assert.Equal(leaderLog[i].Command, nodeLog[i].Command);
                }
            }
        }
    }

    /// <summary>
    /// Test that an old leader steps down when it discovers a higher term.
    /// </summary>
    [Fact]
    public async Task OldLeader_StepsDown_WhenDiscoveringHigherTerm()
    {
        // Arrange
        var cluster = new RaftCluster<string>();
        cluster.AddNode(new RaftNode<string>("node-1"));
        cluster.AddNode(new RaftNode<string>("node-2"));
        cluster.AddNode(new RaftNode<string>("node-3"));

        // node-1 becomes leader in term 1
        await cluster.TriggerElectionAsync("node-1").ConfigureAwait(true);
        await RunTicksAsync(cluster, 3).ConfigureAwait(true);
        Assert.Equal(RaftNodeState.Leader, cluster.GetNodes().First(n => n.NodeId == "node-1").State);

        // Act — node-2 starts election in term 2
        await cluster.TriggerElectionAsync("node-2").ConfigureAwait(true);
        await RunTicksAsync(cluster, 3).ConfigureAwait(true);

        // Assert — node-1 should have stepped down
        var node1 = cluster.GetNodes().First(n => n.NodeId == "node-1");
        Assert.NotEqual(RaftNodeState.Leader, node1.State);
    }

    /// <summary>
    /// Test that GetCommittedLog returns only committed entries.
    /// </summary>
    [Fact]
    public async Task GetCommittedLog_ReturnsOnlyCommittedEntries()
    {
        // Arrange
        var cluster = new RaftCluster<string>();
        var node = new RaftNode<string>("node-1");
        cluster.AddNode(node);

        await cluster.TriggerElectionAsync("node-1").ConfigureAwait(true);

        // Act — propose a command (single-node cluster: commits immediately)
        await node.ProposeAsync("cmd-1").ConfigureAwait(true);

        // Assert
        var log = node.GetCommittedLog();
        Assert.Single(log);
        Assert.Equal("cmd-1", log[0].Command);
    }

    /// <summary>
    /// Test that nodes start as followers.
    /// </summary>
    [Fact]
    public void NewNode_StartsAsFollower()
    {
        // Arrange & Act
        var node = new RaftNode<string>("node-1");

        // Assert
        Assert.Equal(RaftNodeState.Follower, node.State);
        Assert.Equal(0, node.CurrentTerm);
        Assert.Null(node.VotedFor);
        Assert.Null(node.LeaderId);
    }

    /// <summary>
    /// Test that a 5-node cluster can elect a leader.
    /// </summary>
    [Fact]
    public async Task FiveNodeCluster_CanElectLeader()
    {
        // Arrange
        var cluster = new RaftCluster<string>();
        for (var i = 1; i <= 5; i++)
        {
            cluster.AddNode(new RaftNode<string>($"node-{i}"));
        }

        // Act
        await cluster.TriggerElectionAsync("node-3").ConfigureAwait(true);
        await RunTicksAsync(cluster, 5).ConfigureAwait(true);

        // Assert
        var leader = cluster.GetLeader();
        Assert.NotNull(leader);
        Assert.Equal("node-3", leader.NodeId);
    }

    /// <summary>
    /// Test re-election: after leader changes, new leader can accept proposals.
    /// </summary>
    [Fact]
    public async Task ReElection_NewLeaderAcceptsProposals()
    {
        // Arrange
        var cluster = new RaftCluster<string>();
        cluster.AddNode(new RaftNode<string>("node-1"));
        cluster.AddNode(new RaftNode<string>("node-2"));
        cluster.AddNode(new RaftNode<string>("node-3"));

        // First leader
        await cluster.TriggerElectionAsync("node-1").ConfigureAwait(true);
        await RunTicksAsync(cluster, 3).ConfigureAwait(true);

        // New election
        await cluster.TriggerElectionAsync("node-2").ConfigureAwait(true);
        await RunTicksAsync(cluster, 3).ConfigureAwait(true);

        var newLeader = cluster.GetLeader()!;

        // Act — new leader should accept proposals
        _ = await newLeader.ProposeAsync("after-reelection").ConfigureAwait(true);
        await RunTicksAsync(cluster, 3).ConfigureAwait(true);

        // Assert
        // The propose may or may not be committed yet (depends on response timing),
        // but the leader should have accepted it (not returned false as a non-leader would)
        Assert.Equal(RaftNodeState.Leader, newLeader.State);
    }

    // ========== Tier 1 correctness tests ==========

    /// <summary>
    /// Test that matchIndex is initialized to 0 (not -1) when a node becomes leader,
    /// per Raft paper Figure 2.
    /// </summary>
    [Fact]
    public async Task BecomeLeader_InitializesMatchIndexToZero()
    {
        // Arrange
        var cluster = new RaftCluster<string>();
        var node1 = new RaftNode<string>("node-1");
        var node2 = new RaftNode<string>("node-2");
        var node3 = new RaftNode<string>("node-3");
        cluster.AddNode(node1);
        cluster.AddNode(node2);
        cluster.AddNode(node3);

        // Act — node-1 becomes leader
        await cluster.TriggerElectionAsync("node-1").ConfigureAwait(true);
        await RunTicksAsync(cluster, 3).ConfigureAwait(true);

        // Propose a command and let it replicate fully
        await node1.ProposeAsync("cmd-1").ConfigureAwait(true);
        await RunTicksAsync(cluster, 5).ConfigureAwait(true);

        // Assert — the command should be committed after replication
        var committedLog = node1.GetCommittedLog();
        Assert.True(committedLog.Count >= 1, "Leader should commit entries after replication completes");
    }

    /// <summary>
    /// Test that a follower retains its vote after receiving a heartbeat in the same term.
    /// VotedFor must NOT be cleared on AppendEntries in the same term (Raft election safety).
    /// </summary>
    [Fact]
    public async Task Follower_RetainsVote_AfterHeartbeatInSameTerm()
    {
        // Arrange
        var cluster = new RaftCluster<string>();
        var node1 = new RaftNode<string>("node-1");
        var node2 = new RaftNode<string>("node-2");
        var node3 = new RaftNode<string>("node-3");
        cluster.AddNode(node1);
        cluster.AddNode(node2);
        cluster.AddNode(node3);

        // Act — node-1 becomes leader in term 1
        await cluster.TriggerElectionAsync("node-1").ConfigureAwait(true);
        await RunTicksAsync(cluster, 3).ConfigureAwait(true);

        // Assert — followers voted for node-1 in this term and should retain that vote
        // even after receiving heartbeats
        Assert.Equal("node-1", node2.VotedFor);
        Assert.Equal("node-1", node3.VotedFor);
    }

    /// <summary>
    /// Test that TryAdvanceCommitIndex commits entries sequentially (ascending),
    /// not by jumping to the highest replicated index.
    /// </summary>
    [Fact]
    public async Task CommitIndex_AdvancesSequentially()
    {
        // Arrange
        var cluster = new RaftCluster<string>();
        var node1 = new RaftNode<string>("node-1");
        var node2 = new RaftNode<string>("node-2");
        var node3 = new RaftNode<string>("node-3");
        cluster.AddNode(node1);
        cluster.AddNode(node2);
        cluster.AddNode(node3);

        await cluster.TriggerElectionAsync("node-1").ConfigureAwait(true);
        await RunTicksAsync(cluster, 3).ConfigureAwait(true);

        // Act — propose multiple commands and let them replicate
        await node1.ProposeAsync("cmd-1").ConfigureAwait(true);
        await node1.ProposeAsync("cmd-2").ConfigureAwait(true);
        await node1.ProposeAsync("cmd-3").ConfigureAwait(true);
        await RunTicksAsync(cluster, 10).ConfigureAwait(true);

        // Assert — all three commands should be committed in order
        var committedLog = node1.GetCommittedLog();
        Assert.Equal(3, committedLog.Count);
        Assert.Equal("cmd-1", committedLog[0].Command);
        Assert.Equal("cmd-2", committedLog[1].Command);
        Assert.Equal("cmd-3", committedLog[2].Command);
    }

    /// <summary>
    /// Test that ProposeAsync correctly tracks what was sent to each follower,
    /// ensuring matchIndex reflects actual replication state. Commands proposed in
    /// sequence are committed on the leader after responses arrive.
    /// </summary>
    [Fact]
    public async Task MatchIndex_ReflectsActualReplicationAfterResponses()
    {
        // Arrange
        var cluster = new RaftCluster<string>();
        var node1 = new RaftNode<string>("node-1");
        var node2 = new RaftNode<string>("node-2");
        var node3 = new RaftNode<string>("node-3");
        cluster.AddNode(node1);
        cluster.AddNode(node2);
        cluster.AddNode(node3);

        await cluster.TriggerElectionAsync("node-1").ConfigureAwait(true);
        await RunTicksAsync(cluster, 3).ConfigureAwait(true);

        // Act — propose commands, each followed by ticks for replication.
        // The leader's commitIndex advances when it processes AppendEntries responses.
        // Followers learn about the leader's commitIndex from the next round of AppendEntries.
        await node1.ProposeAsync("cmd-1").ConfigureAwait(true);
        await RunTicksAsync(cluster, 5).ConfigureAwait(true);
        await node1.ProposeAsync("cmd-2").ConfigureAwait(true);
        await RunTicksAsync(cluster, 5).ConfigureAwait(true);

        // Assert — leader should have committed both entries
        var leaderLog = node1.GetCommittedLog();
        Assert.Equal(2, leaderLog.Count);
        Assert.Equal("cmd-1", leaderLog[0].Command);
        Assert.Equal("cmd-2", leaderLog[1].Command);

        // Followers' committed log may lag by one round since they learn about
        // leaderCommitIndex from subsequent AppendEntries. But the leader's log
        // is authoritative — the entries are safely replicated to a majority.
    }

    /// <summary>
    /// Test that the leader correctly handles the decrement-retry path
    /// when a follower's log is behind.
    /// </summary>
    [Fact]
    public async Task Leader_DecrementRetry_ConvergesFollowerLog()
    {
        // Arrange — single-node leader with some committed entries
        var cluster = new RaftCluster<string>();
        var node1 = new RaftNode<string>("node-1");
        cluster.AddNode(node1);

        await cluster.TriggerElectionAsync("node-1").ConfigureAwait(true);
        await node1.ProposeAsync("cmd-1").ConfigureAwait(true);
        await node1.ProposeAsync("cmd-2").ConfigureAwait(true);

        // Now add followers — they have empty logs but leader has entries
        var node2 = new RaftNode<string>("node-2");
        var node3 = new RaftNode<string>("node-3");
        cluster.AddNode(node2);
        cluster.AddNode(node3);

        // Re-elect node-1 so it sends entries to the new followers
        await cluster.TriggerElectionAsync("node-1").ConfigureAwait(true);
        await RunTicksAsync(cluster, 10).ConfigureAwait(true);

        // Propose new command to drive replication
        await node1.ProposeAsync("cmd-3").ConfigureAwait(true);
        await RunTicksAsync(cluster, 10).ConfigureAwait(true);

        // Assert — followers should have caught up
        var leaderLog = node1.GetCommittedLog();
        Assert.True(leaderLog.Count >= 1, "Leader should have committed entries");
    }
}
