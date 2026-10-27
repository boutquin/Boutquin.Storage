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
using Boutquin.Storage.Distributed.Interfaces.Consensus;

namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// Pins the persistence-seam ctors that let production deployments substitute persistent
/// implementations of the Raft log + persistent-state primitives without breaking the
/// public API. Asserts:
/// <list type="bullet">
///   <item><description>The default-null ctor preserves today's in-memory behaviour.
///   </description></item>
///   <item><description>The interface contract for both seams (append, indexed read,
///   range, truncate, count; current-term + voted-for round-trip).</description></item>
///   <item><description>A test-mock implementation that mirrors the in-memory default
///   produces identical state-machine traces when injected into a single-node
///   <see cref="RaftNode{TCommand}"/> — the substitutability invariant the post-GA
///   file-backed defaults will rely on.</description></item>
/// </list>
/// </summary>
public sealed class RaftPersistenceSeamTests
{
    [Fact]
    public void RaftNode_NullSeams_UsesInMemoryDefaults()
    {
        var node = new RaftNode<string>("n1");

        node.NodeId.Should().Be("n1");
        node.CurrentTerm.Should().Be(0);
        node.VotedFor.Should().BeNull();
    }

    [Fact]
    public void InMemoryRaftLog_AppendIndex_RoundTrips()
    {
        var log = new InMemoryRaftLog<string>();
        log.Count.Should().Be(0);

        log.Append((1, "a"));
        log.Append((1, "b"));
        log.Append((2, "c"));

        log.Count.Should().Be(3);
        log.GetEntry(0).Should().Be((1L, "a"));
        log.GetEntry(2).Should().Be((2L, "c"));
        log.GetRange(1, 2).Should().Equal((1L, "b"), (2L, "c"));
    }

    [Fact]
    public void InMemoryRaftLog_TruncateFrom_RemovesTail()
    {
        var log = new InMemoryRaftLog<int>();
        log.Append((1, 10));
        log.Append((1, 20));
        log.Append((2, 30));

        log.TruncateFrom(1);

        log.Count.Should().Be(1);
        log.GetEntry(0).Should().Be((1L, 10));
    }

    [Fact]
    public void InMemoryRaftLog_TruncateFrom_AtCount_IsNoOp()
    {
        var log = new InMemoryRaftLog<int>();
        log.Append((1, 10));

        log.TruncateFrom(1);

        log.Count.Should().Be(1);
    }

    [Fact]
    public void InMemoryRaftLog_OutOfRange_Throws()
    {
        var log = new InMemoryRaftLog<int>();
        log.Append((1, 10));

        var act1 = () => log.GetEntry(5);
        var act2 = () => log.TruncateFrom(-1);
        var act3 = () => log.GetRange(0, 5);

        act1.Should().Throw<ArgumentOutOfRangeException>();
        act2.Should().Throw<ArgumentOutOfRangeException>();
        act3.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void InMemoryRaftPersistentState_RoundTripsTermAndVote()
    {
        var state = new InMemoryRaftPersistentState();
        state.CurrentTerm.Should().Be(0);
        state.VotedFor.Should().BeNull();

        state.CurrentTerm = 7;
        state.VotedFor = "candidate-A";

        state.CurrentTerm.Should().Be(7);
        state.VotedFor.Should().Be("candidate-A");

        state.VotedFor = null;
        state.VotedFor.Should().BeNull();
    }

    [Fact]
    public async Task RaftNode_InjectedSeams_DriveStateMachineIdenticallyToDefaults()
    {
        // Build two single-node Raft instances — one with the default null seams, one with
        // explicit instances of the same defaults. Driving identical command sequences must
        // produce identical committed-log traces. This is the substitutability invariant
        // the post-GA file-backed defaults will rely on.
        var defaultNode = new RaftNode<string>("default");
        var injectedNode = new RaftNode<string>(
            "injected",
            log: new InMemoryRaftLog<string>(),
            persistentState: new InMemoryRaftPersistentState());

        // Drive both into leader state by running an election (single-node clusters become
        // leader immediately on StartElection — see RaftNode.StartElection).
        StartElection(defaultNode);
        StartElection(injectedNode);

        defaultNode.State.Should().Be(RaftNodeState.Leader);
        injectedNode.State.Should().Be(RaftNodeState.Leader);

        // Propose the same commands; single-node clusters commit immediately.
        await defaultNode.ProposeAsync("op-1");
        await defaultNode.ProposeAsync("op-2");
        await injectedNode.ProposeAsync("op-1");
        await injectedNode.ProposeAsync("op-2");

        var defaultLog = defaultNode.GetCommittedLog();
        var injectedLog = injectedNode.GetCommittedLog();

        injectedLog.Should().Equal(defaultLog,
            "the injected in-memory seam must produce a state-machine trace " +
            "byte-identical to the implicit default — the substitutability invariant the " +
            "post-GA file-backed defaults will rely on");
    }

    [Fact]
    public async Task RaftNode_PersistentStateSeam_ReceivesTermAndVoteUpdates()
    {
        // The injected persistent-state instance must observe every CurrentTerm /
        // VotedFor transition the node's state machine produces. This is the invariant
        // that lets a file-backed implementation flush each transition before the
        // corresponding RPC fires (Raft paper §5.2).
        var state = new InMemoryRaftPersistentState();
        var node = new RaftNode<string>("n1", persistentState: state);

        // Trigger an election → CurrentTerm increments + VotedFor is set to self.
        StartElection(node);

        state.CurrentTerm.Should().Be(1, "election advanced the term to 1");
        state.VotedFor.Should().Be("n1", "the node voted for itself in this term");

        await node.ProposeAsync("op-1");
        state.CurrentTerm.Should().Be(1, "no further term advance from a leader propose");
    }

    [Fact]
    public async Task RaftNode_LogSeam_ReceivesAppendAndCommittedReads()
    {
        // The injected log instance must observe every append the node's state machine
        // produces, and the committed-log read path must consume from the seam (not a
        // shadow copy).
        var log = new InMemoryRaftLog<string>();
        var node = new RaftNode<string>("n1", log: log);

        StartElection(node);
        await node.ProposeAsync("op-1");
        await node.ProposeAsync("op-2");

        log.Count.Should().Be(2, "every propose appends one entry to the injected log");
        log.GetEntry(0).Command.Should().Be("op-1");
        log.GetEntry(1).Command.Should().Be("op-2");

        node.GetCommittedLog().Should().HaveCount(2,
            "the committed-log read path must consume from the injected seam");
    }

    // RaftNode.StartElection is internal — tests in this assembly reach it via
    // InternalsVisibleTo. The two calls go to the same internal API the cluster's tick
    // driver invokes.
    private static void StartElection<TCommand>(RaftNode<TCommand> node)
    {
        var method = typeof(RaftNode<TCommand>).GetMethod(
            "StartElection",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        method!.Invoke(node, null);
    }

    /// <summary>
    /// A term transition pairs a new term with a new vote (StartElection: vote-for-self;
    /// StepDown: vote cleared). The pair must reach the persistence seam ATOMICALLY — a
    /// crash between "persist new term" and "persist new vote" would otherwise leave
    /// currentTerm=N+1 paired with the term-N vote on disk, and the restarted node would
    /// refuse (or misdirect) its term-N+1 vote. This pins that no intermediate persisted
    /// state ever combines the new term with the stale vote.
    /// </summary>
    [Fact]
    public void StartElection_PersistsTermAndVoteAtomically_NoStaleVoteWindow()
    {
        var recorder = new RecordingPersistentState { CurrentTermDirect = 5, VotedForDirect = "stale-candidate" };
        var node = new RaftNode<string>("n1", persistentState: recorder);
        node.PeerIds.Add("n2"); // multi-node so StartElection stays a Candidate

        recorder.PersistedStates.Clear();
        StartElection(node);

        recorder.PersistedStates.Should().NotContain(s => s.Term == 6 && s.Vote == "stale-candidate",
            "no persisted snapshot may pair the new term with the previous term's vote");
        recorder.PersistedStates.Should().Contain(s => s.Term == 6 && s.Vote == "n1");
    }

    /// <summary>
    /// Records every state the persistence seam is asked to make durable, in order — the
    /// crash-window observability a file cannot give us.
    /// </summary>
    private sealed class RecordingPersistentState : IRaftPersistentState
    {
        public List<(long Term, string? Vote)> PersistedStates { get; } = [];

        public long CurrentTermDirect { get; set; }
        public string? VotedForDirect { get; set; }

        public long CurrentTerm
        {
            get => CurrentTermDirect;
            set
            {
                CurrentTermDirect = value;
                PersistedStates.Add((CurrentTermDirect, VotedForDirect));
            }
        }

        public string? VotedFor
        {
            get => VotedForDirect;
            set
            {
                VotedForDirect = value;
                PersistedStates.Add((CurrentTermDirect, VotedForDirect));
            }
        }

        public void SetTermAndVote(long term, string? votedFor)
        {
            CurrentTermDirect = term;
            VotedForDirect = votedFor;
            PersistedStates.Add((CurrentTermDirect, VotedForDirect));
        }
    }
}
