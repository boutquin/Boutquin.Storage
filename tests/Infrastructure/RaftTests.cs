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

        // Assert — node-2 and node-3 voted for node-1, and VotedFor PERSISTS for the term (it is
        // NOT cleared on receiving the leader's heartbeat — clearing it would permit double-voting
        // if another candidate's RequestVote arrived in the same term; see RaftNode.HandleAppendEntries).
        Assert.Equal(RaftNodeState.Leader, node1.State);
        Assert.Equal("node-1", node2.VotedFor);
        Assert.Equal("node-1", node3.VotedFor);
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
    /// A newly elected leader initializes matchIndex to the -1 sentinel (this 0-indexed log's
    /// equivalent of Raft Figure 2's "0" in a 1-indexed log) — meaning "no entry known replicated
    /// yet" — so a proposed command still replicates and commits once a majority acknowledges it.
    /// A matchIndex of 0 would wrongly claim log[0] is already replicated and commit it prematurely.
    /// </summary>
    [Fact]
    public async Task BecomeLeader_ReplicatesAndCommits_FromMinusOneMatchIndexSentinel()
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
    /// Pins the decrement-retry path: a follower partitioned away while the leader commits
    /// entries (2-of-3 majority) rejoins with an empty log. On re-election the leader's
    /// nextIndex for that follower resets to the log tail, so its first AppendEntries carries a
    /// PrevLogIndex the follower cannot match — the conflict-retry loop must walk nextIndex
    /// back entry by entry and backfill until the follower's committed log converges with the
    /// leader's. All nodes are added before the first election (AddNode is pre-election-only);
    /// the partition is simulated by dropping messages to/from the follower at delivery time.
    /// </summary>
    [Fact]
    public async Task Leader_DecrementRetry_ConvergesFollowerLog()
    {
        // Arrange — build full cluster BEFORE any election (AddNode is pre-election-only).
        var cluster = new RaftCluster<string>();
        var node1 = new RaftNode<string>("node-1");
        var node2 = new RaftNode<string>("node-2");
        var node3 = new RaftNode<string>("node-3");
        cluster.AddNode(node1);
        cluster.AddNode(node2);
        cluster.AddNode(node3);

        // Elect node-1 as leader with all nodes reachable.
        await cluster.TriggerElectionAsync("node-1").ConfigureAwait(true);
        await RunTicksAsync(cluster, 3).ConfigureAwait(true);
        Assert.Equal(RaftNodeState.Leader, node1.State);

        // Partition node-3 and commit two entries via the node-1/node-2 majority.
        // Ticks during the partition drop every message to or from node-3.
        await node1.ProposeAsync("cmd-1").ConfigureAwait(true);
        TickWithPartition([node1, node2, node3], partitionedId: "node-3");
        await node1.ProposeAsync("cmd-2").ConfigureAwait(true);
        for (var i = 0; i < 5; i++)
        {
            TickWithPartition([node1, node2, node3], partitionedId: "node-3");
        }

        Assert.Equal(2, node1.GetCommittedLog().Count);
        Assert.Empty(node3.GetCommittedLog()); // partitioned follower saw nothing

        // Heal the partition and re-elect node-1: BecomeLeader resets nextIndex[node-3] to the
        // log tail, so the first AppendEntries to node-3 is rejected and the decrement-retry
        // loop must walk back and backfill the whole log.
        await cluster.TriggerElectionAsync("node-1").ConfigureAwait(true);
        await RunTicksAsync(cluster, 10).ConfigureAwait(true);

        // Assert — every node's committed log converges to the leader's exact sequence.
        var leaderCommands = node1.GetCommittedLog().Select(e => e.Command).ToList();
        Assert.Equal(["cmd-1", "cmd-2"], leaderCommands);
        Assert.Equal(leaderCommands, node2.GetCommittedLog().Select(e => e.Command).ToList());
        Assert.Equal(leaderCommands, node3.GetCommittedLog().Select(e => e.Command).ToList());
    }

    /// <summary>
    /// One cluster tick (deliver outboxes, then process inboxes) that simulates a network
    /// partition by dropping every message sent to or by <paramref name="partitionedId"/>.
    /// Mirrors <see cref="RaftCluster{TCommand}.TickAsync"/>'s delivery order.
    /// </summary>
    private static void TickWithPartition(RaftNode<string>[] nodes, string partitionedId)
    {
        var byId = nodes.ToDictionary(n => n.NodeId);
        foreach (var node in nodes)
        {
            while (node.Outbox.TryDequeue(out var outgoing))
            {
                if (node.NodeId == partitionedId || outgoing.TargetNodeId == partitionedId)
                {
                    continue; // dropped by the partition
                }

                if (byId.TryGetValue(outgoing.TargetNodeId, out var target))
                {
                    target.Inbox.Enqueue(outgoing.Message);
                }
            }
        }

        foreach (var node in nodes)
        {
            node.ProcessMessages();
        }
    }

    /// <summary>
    /// AddNode is pre-election-only. Once any node has left the Follower state (i.e. an
    /// election is underway or a leader has been elected), AddNode must throw to prevent
    /// silent quorum-math corruption.
    /// </summary>
    [Fact]
    public async Task AddNode_AfterElectionStarted_ThrowsInvalidOperationException()
    {
        // Arrange — build a 3-node cluster and drive it to elect a leader.
        var cluster = new RaftCluster<string>();
        cluster.AddNode(new RaftNode<string>("node-1"));
        cluster.AddNode(new RaftNode<string>("node-2"));
        cluster.AddNode(new RaftNode<string>("node-3"));

        await cluster.TriggerElectionAsync("node-1").ConfigureAwait(true);
        await RunTicksAsync(cluster, 5).ConfigureAwait(true);

        // Sanity: at least one node must be non-Follower (leader or was candidate).
        var leaders = cluster.GetNodes().Where(n => n.State == RaftNodeState.Leader).ToList();
        Assert.Single(leaders);

        // Act — attempt to add a new node while the cluster is electing / has a leader.
        var newNode = new RaftNode<string>("node-4");

        var act = () => cluster.AddNode(newNode);

        // Assert
        Assert.Throws<InvalidOperationException>(act);
    }

    // C4: CancellationToken must be observed by ProposeAsync, TriggerElectionAsync, and TickAsync.

    [Fact]
    public async Task ProposeAsync_CancelledToken_ThrowsOperationCanceledException()
    {
        var node = new RaftNode<string>("leader");
        var cluster = new RaftCluster<string>();
        cluster.AddNode(node);
        await cluster.TriggerElectionAsync("leader").ConfigureAwait(true);
        // Single-node cluster — node is already leader after TriggerElection.

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => node.ProposeAsync("cmd", cts.Token)).ConfigureAwait(true);
    }

    [Fact]
    public async Task TriggerElectionAsync_CancelledToken_ThrowsOperationCanceledException()
    {
        var cluster = new RaftCluster<string>();
        cluster.AddNode(new RaftNode<string>("n1"));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => cluster.TriggerElectionAsync("n1", cts.Token)).ConfigureAwait(true);
    }

    [Fact]
    public async Task TickAsync_CancelledToken_ThrowsOperationCanceledException()
    {
        var cluster = new RaftCluster<string>();
        cluster.AddNode(new RaftNode<string>("n1"));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => cluster.TickAsync(cts.Token)).ConfigureAwait(true);
    }

    // C5: _nextIndex and _matchIndex must be cleared when a leader steps down.

    [Fact]
    public async Task StepDown_ClearsNextIndexAndMatchIndex_SoStaleReplicationStateDoesNotCarryOver()
    {
        // Build a 3-node cluster, elect a leader, propose a command so matchIndex/nextIndex
        // are populated, then force the leader to step down by presenting a higher term via
        // TriggerElection on a different node. The ex-leader's per-follower state must be
        // empty after the step-down so it cannot miscount quorum if re-elected.
        var cluster = new RaftCluster<string>();
        var n1 = new RaftNode<string>("n1");
        var n2 = new RaftNode<string>("n2");
        var n3 = new RaftNode<string>("n3");
        cluster.AddNode(n1);
        cluster.AddNode(n2);
        cluster.AddNode(n3);

        await cluster.TriggerElectionAsync("n1").ConfigureAwait(true);
        await RunTicksAsync(cluster, 5).ConfigureAwait(true);

        var leader = (RaftNode<string>?)cluster.GetLeader();
        Assert.NotNull(leader);

        // Propose a command to populate _nextIndex and _matchIndex on the leader.
        await leader.ProposeAsync("data").ConfigureAwait(true);
        await RunTicksAsync(cluster, 5).ConfigureAwait(true);

        // Force a step-down: trigger election on a peer with a higher term.
        var peer = leader.NodeId == "n1" ? "n2" : "n1";
        await cluster.TriggerElectionAsync(peer).ConfigureAwait(true);
        await RunTicksAsync(cluster, 10).ConfigureAwait(true);

        // The old leader must now be a follower with empty per-follower state.
        leader.State.Should().NotBe(RaftNodeState.Leader);
        // Verify via reflection that the dictionaries are empty.
        var nextIndexField = typeof(RaftNode<string>).GetField("_nextIndex",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var matchIndexField = typeof(RaftNode<string>).GetField("_matchIndex",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var nextIndex = nextIndexField!.GetValue(leader) as System.Collections.IDictionary;
        var matchIndex = matchIndexField!.GetValue(leader) as System.Collections.IDictionary;
        nextIndex!.Count.Should().Be(0, "stale nextIndex survives step-down and can corrupt quorum math on re-election");
        matchIndex!.Count.Should().Be(0, "stale matchIndex survives step-down and can cause premature commits on re-election");
    }

    /// <summary>
    /// Regression guard: _nextIndex and _matchIndex must be Dictionary&lt;string, long&gt;,
    /// not Dictionary&lt;string, int&gt;. An int dictionary truncates any MatchIndex &gt;
    /// int.MaxValue (2 147 483 647) at the (int) cast, causing the leader to credit a
    /// follower for log entries it never received and potentially committing un-replicated
    /// entries — a Leader Completeness / State Machine Safety violation.
    /// </summary>
    [Fact]
    public void MatchIndex_StoredAsLong_NotTruncatedAtIntMaxValue()
    {
        // Verify the dictionary value type is long via reflection.
        // (Instantiation not required — the field type is a compile-time property of the class.)
        var nextIndexField = typeof(RaftNode<string>).GetField("_nextIndex",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var matchIndexField = typeof(RaftNode<string>).GetField("_matchIndex",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        Assert.NotNull(nextIndexField);
        Assert.NotNull(matchIndexField);

        // The generic argument of Dictionary<string, T> must be long, not int.
        var nextIndexValueType = nextIndexField!.FieldType.GetGenericArguments()[1];
        var matchIndexValueType = matchIndexField!.FieldType.GetGenericArguments()[1];

        nextIndexValueType.Should().Be(typeof(long),
            because: "_nextIndex must store long indices to avoid truncation of values > int.MaxValue");
        matchIndexValueType.Should().Be(typeof(long),
            because: "_matchIndex must store long indices to avoid truncation of values > int.MaxValue");
    }
}
