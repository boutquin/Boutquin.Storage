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
/// End-to-end pins for the Raft invariants the unit-level suites leave unexercised: the
/// file-backed persistence actually preventing a double vote across a crash-restart (the
/// split-brain property the file types exist for), the follower commit-index clamp when the
/// leader's commit outruns the follower's log, and multi-step nextIndex back-off convergence
/// on a multi-entry divergent suffix.
/// </summary>
public sealed class RaftDurabilityAndConvergenceTests
{
    /// <summary>
    /// Raft's Election Safety across a crash: a vote granted in term 5 must survive a process
    /// restart (durable currentTerm + votedFor), so the restarted node REFUSES a second
    /// candidate in the same term. Without durable state the node would forget its vote and
    /// two leaders could be elected in one term (split brain).
    /// </summary>
    [Fact]
    public void FileBackedNode_AfterCrashRestart_CannotGrantSecondVoteInSameTerm()
    {
        var dir = Directory.CreateTempSubdirectory("raft-crash-restart-");
        try
        {
            var statePath = Path.Combine(dir.FullName, "state.bin");

            var node = new RaftNode<string>("voter", persistentState: new FileRaftPersistentState(statePath));
            node.Inbox.Enqueue(new RequestVoteRequest(Term: 5, CandidateId: "c1", LastLogIndex: 0, LastLogTerm: 0));
            node.ProcessMessages();
            node.Outbox.TryDequeue(out var first).Should().BeTrue();
            first.Message.Should().BeOfType<RequestVoteResponse>()
                .Which.VoteGranted.Should().BeTrue("the empty-log voter grants the first candidate of term 5");

            // Crash: drop the instance entirely; reconstruct node + persistent state from disk.
            var restarted = new RaftNode<string>("voter", persistentState: new FileRaftPersistentState(statePath));
            restarted.CurrentTerm.Should().Be(5, "the term must survive the crash");

            restarted.Inbox.Enqueue(new RequestVoteRequest(Term: 5, CandidateId: "c2", LastLogIndex: 0, LastLogTerm: 0));
            restarted.ProcessMessages();
            restarted.Outbox.TryDequeue(out var second).Should().BeTrue();
            second.Message.Should().BeOfType<RequestVoteResponse>()
                .Which.VoteGranted.Should().BeFalse(
                    "the durable votedFor from before the crash forbids a second grant in term 5");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    /// <summary>
    /// A vote is a node identity, so its durable form must decode to exactly the id that was
    /// voted for. An ill-formed UTF-16 id (unpaired surrogate) has no exact UTF-8 encoding; a
    /// replacing encoder would persist U+FFFD instead, and the restarted voter would then
    /// believe it voted for a DIFFERENT node and grant that node a second vote in the term.
    /// The write must be refused and leave both the in-memory and the durable state untouched.
    /// </summary>
    [Fact]
    public void FileBackedState_IllFormedVoteId_IsRefusedWithoutChangingDurableOrInMemoryState()
    {
        var dir = Directory.CreateTempSubdirectory("raft-ill-formed-vote-");
        try
        {
            var statePath = Path.Combine(dir.FullName, "state.bin");
            var state = new FileRaftPersistentState(statePath);
            state.SetTermAndVote(4, "c1");

            var persist = () => state.SetTermAndVote(5, "n\uD800");

            persist.Should().Throw<ArgumentException>("an unpaired surrogate has no exact UTF-8 form");
            state.CurrentTerm.Should().Be(4, "a refused write must not advance the in-memory term");
            state.VotedFor.Should().Be("c1", "a refused write must not change the in-memory vote");
            var reopened = new FileRaftPersistentState(statePath);
            reopened.CurrentTerm.Should().Be(4);
            reopened.VotedFor.Should().Be("c1", "the durable vote must still be the last well-formed one");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    /// <summary>
    /// The decode side of the same contract: a checksum-valid record whose vote bytes are not
    /// well-formed UTF-8 cannot name any node, so loading it must fail as corruption rather
    /// than silently substituting U+FFFD and reporting a vote for some other identity.
    /// </summary>
    [Fact]
    public void FileBackedState_ChecksumValidRecordWithIllFormedUtf8Vote_FailsAsCorruption()
    {
        var dir = Directory.CreateTempSubdirectory("raft-ill-formed-utf8-");
        try
        {
            var statePath = Path.Combine(dir.FullName, "state.bin");
            byte[] vote = [(byte)'n', 0xED, 0xA0, 0x80]; // CESU-style encoded lone surrogate: invalid UTF-8
            var record = new byte[sizeof(long) + sizeof(int) + vote.Length + sizeof(uint)];
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(record, 5);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(sizeof(long)), vote.Length);
            vote.CopyTo(record.AsSpan(sizeof(long) + sizeof(int)));
            var payload = record.Length - sizeof(uint);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(payload),
                Boutquin.Storage.Infrastructure.Algorithms.Crc32C.HashToUInt32(record.AsSpan(0, payload)));
            File.WriteAllBytes(statePath, record);

            var open = () => new FileRaftPersistentState(statePath);

            open.Should().Throw<InvalidDataException>("vote bytes that are not well-formed UTF-8 identify no node");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    /// <summary>
    /// The follower advances its commit index to min(leaderCommit, index of last new entry)
    /// (Raft paper §5.3, AppendEntries receiver step 5). A leaderCommit far beyond the
    /// follower's short log must clamp — otherwise the follower would expose entries it does
    /// not have as "committed".
    /// </summary>
    [Fact]
    public void Follower_LeaderCommitBeyondLogEnd_ClampsCommitIndexToLastEntry()
    {
        var follower = new RaftNode<string>("f");

        follower.Inbox.Enqueue(new AppendEntriesRequest<string>(
            Term: 1, LeaderId: "L", PrevLogIndex: -1, PrevLogTerm: 0,
            Entries: [(1L, "a"), (1L, "b")], LeaderCommitIndex: 100));
        follower.ProcessMessages();

        follower.GetCommittedLog().Count.Should().Be(2,
            "the commit index clamps to the follower's last entry, never beyond its log");
    }

    /// <summary>
    /// A follower whose log diverges from the leader's by SEVERAL entries requires several
    /// nextIndex decrements across retries before the logs converge (Raft paper §5.3). The
    /// single-conflict case is covered elsewhere; this drives the multi-step back-off and
    /// asserts exact entry-by-entry convergence.
    /// </summary>
    [Fact]
    public async Task Leader_MultiDecrementRetry_ConvergesDivergentFollowerSuffix()
    {
        var cluster = new RaftCluster<string>(["L", "F"]);

        var leaderLog = new InMemoryRaftLog<string>();
        leaderLog.Append((1L, "a"));
        leaderLog.Append((2L, "x1"));
        leaderLog.Append((2L, "x2"));
        leaderLog.Append((2L, "x3"));
        var leader = new RaftNode<string>("L", leaderLog) { CurrentTerm = 2 };

        var followerLog = new InMemoryRaftLog<string>();
        followerLog.Append((1L, "a"));      // shared prefix
        followerLog.Append((1L, "s1"));     // divergent 3-entry suffix
        followerLog.Append((1L, "s2"));
        followerLog.Append((1L, "s3"));
        var follower = new RaftNode<string>("F", followerLog) { CurrentTerm = 2 };

        cluster.AddNode(leader);
        cluster.AddNode(follower);

        await cluster.TriggerElectionAsync("L").ConfigureAwait(true);
        for (var i = 0; i < 20; i++)
        {
            await cluster.TickAsync().ConfigureAwait(true);
        }

        followerLog.Count.Should().Be(leaderLog.Count, "the follower's divergent suffix is replaced by the leader's");
        for (var i = 0; i < leaderLog.Count; i++)
        {
            followerLog.GetEntry(i).Should().Be(leaderLog.GetEntry(i),
                $"entry {i} must match the leader's after multi-decrement convergence");
        }
    }
}
