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
/// Pins the three Raft safety invariants (Ongaro &amp; Ousterhout 2014, §5.3–5.4) that the existing
/// cluster-level tests never exercise because they only run already-consistent clusters: the
/// Leader-Completeness up-to-date vote restriction, the Log-Matching consistency check and
/// conflict truncation, and the State-Machine-Safety / "figure-8" commit rule. Each drives a
/// single <see cref="RaftNode{TCommand}"/> directly through its message inbox/outbox so the
/// divergent-log scenarios these invariants protect against can be constructed precisely. A
/// regression in any of these handlers would otherwise pass every other Raft test in the suite.
/// </summary>
public sealed class RaftSafetyInvariantTests
{
    // ───── Leader Completeness — up-to-date vote restriction (§5.4.1) ──────────

    [Fact]
    public void RequestVote_FromCandidateWithStaleLog_IsDenied_EvenAtHigherTerm()
    {
        // A node grants its vote only if the candidate's log is at least as up-to-date as its own:
        // compare lastLogTerm first, then lastLogIndex. A candidate at a HIGHER election term but
        // whose log ends in an OLDER term must be denied — otherwise it could win an election and
        // overwrite already-committed entries, violating Leader Completeness.
        var log = new InMemoryRaftLog<string>();
        log.Append((2L, "committed")); // the voter's last entry is term 2
        var voter = new RaftNode<string>("voter", log) { CurrentTerm = 2 };

        // Candidate is at term 3 (higher) but its log ends at term 1 (less up-to-date).
        voter.Inbox.Enqueue(new RequestVoteRequest(Term: 3, CandidateId: "c", LastLogIndex: 0, LastLogTerm: 1));
        voter.ProcessMessages();

        voter.Outbox.TryDequeue(out var sent).Should().BeTrue();
        sent.Message.Should().BeOfType<RequestVoteResponse>()
            .Which.VoteGranted.Should().BeFalse("the candidate's log ends in an older term than the voter's");
        voter.VotedFor.Should().BeNull("a denied vote must not record a votedFor");
    }

    [Fact]
    public void RequestVote_FromCandidateWithEquallyUpToDateLog_IsGranted()
    {
        // Positive control for the restriction above: same lastLogTerm and lastLogIndex ≥ ours ⇒ grant.
        var log = new InMemoryRaftLog<string>();
        log.Append((2L, "committed"));
        var voter = new RaftNode<string>("voter", log) { CurrentTerm = 2 };

        voter.Inbox.Enqueue(new RequestVoteRequest(Term: 3, CandidateId: "c", LastLogIndex: 0, LastLogTerm: 2));
        voter.ProcessMessages();

        voter.Outbox.TryDequeue(out var sent).Should().BeTrue();
        sent.Message.Should().BeOfType<RequestVoteResponse>().Which.VoteGranted.Should().BeTrue();
        voter.VotedFor.Should().Be("c");
    }

    // ───── Election Safety — per-voter grant idempotency (Fig. 2 votesGranted set) ─────

    [Fact]
    public void Candidate_CountsDuplicateGrantsFromSameVoterOnce_AndDoesNotWin()
    {
        // Ongaro & Ousterhout 2014 Figure 2 ("Rules for Servers / Candidates") tracks a SET of
        // peers that granted the vote, so vote handling is idempotent under message duplication
        // ("messages may be delayed, reordered, duplicated, or lost"). A candidate must count
        // DISTINCT granting voters, not raw grant messages — otherwise one honest follower's grant
        // delivered twice double-counts toward the majority threshold and elects a false leader from
        // fewer than a real quorum, breaking Election Safety (≤1 leader per term).
        var node = new RaftNode<string>("c");
        node.PeerIds.AddRange(["p1", "p2", "p3", "p4"]); // 5-node cluster, majority = 3

        node.StartElection(); // term advances, self-vote = 1 distinct voter; needs 2 more peers

        // The SAME honest voter's grant, delivered three times (duplicates carry the same VoterId).
        node.Inbox.Enqueue(new RequestVoteResponse(node.CurrentTerm, VoteGranted: true, VoterId: "p1"));
        node.Inbox.Enqueue(new RequestVoteResponse(node.CurrentTerm, VoteGranted: true, VoterId: "p1"));
        node.Inbox.Enqueue(new RequestVoteResponse(node.CurrentTerm, VoteGranted: true, VoterId: "p1"));
        node.ProcessMessages();

        // Only two DISTINCT voters granted (self + p1) — short of the majority of 3 — so the
        // candidate must NOT become leader. Counting the duplicates as three would wrongly elect it.
        node.State.Should().Be(RaftNodeState.Candidate,
            "duplicate grants from one voter count once, leaving the candidate short of majority");
    }

    [Fact]
    public void Candidate_WinsOnDistinctVoterMajority()
    {
        // Positive control / no-regression: a real majority of DISTINCT voters still elects a leader.
        var node = new RaftNode<string>("c");
        node.PeerIds.AddRange(["p1", "p2", "p3", "p4"]); // 5-node cluster, majority = 3

        node.StartElection(); // self-vote = 1; two distinct peer grants reach majority of 3

        node.Inbox.Enqueue(new RequestVoteResponse(node.CurrentTerm, VoteGranted: true, VoterId: "p1"));
        node.Inbox.Enqueue(new RequestVoteResponse(node.CurrentTerm, VoteGranted: true, VoterId: "p2"));
        node.ProcessMessages();

        node.State.Should().Be(RaftNodeState.Leader,
            "self + two distinct granting peers is a majority of the 5-node cluster");
    }

    // ───── Log Matching — consistency check + conflict truncation (§5.3) ───────

    [Fact]
    public void AppendEntries_WithMismatchedPrevLogTerm_IsRejected_AndLogUnchanged()
    {
        // The follower rejects AppendEntries unless its entry at prevLogIndex carries prevLogTerm.
        // A mismatch must be rejected WITHOUT mutating the log (no append, no truncation).
        var log = new InMemoryRaftLog<string>();
        log.Append((1L, "a"));
        log.Append((1L, "b"));
        var follower = new RaftNode<string>("f", log) { CurrentTerm = 1 };

        // Leader claims index 1 held term 2, but the follower has term 1 there.
        follower.Inbox.Enqueue(new AppendEntriesRequest<string>(
            Term: 2, LeaderId: "L", PrevLogIndex: 1, PrevLogTerm: 2,
            Entries: [(2L, "c")], LeaderCommitIndex: -1));
        follower.ProcessMessages();

        follower.Outbox.TryDequeue(out var sent).Should().BeTrue();
        sent.Message.Should().BeOfType<AppendEntriesResponse>().Which.Success.Should().BeFalse();
        log.Count.Should().Be(2, "a rejected AppendEntries must neither append nor truncate");
        log.GetEntry(1).Command.Should().Be("b");
    }

    [Fact]
    public void AppendEntries_WithConflictingEntry_TruncatesDivergentSuffix_ThenAppends()
    {
        // On a term conflict at an existing index, the follower deletes the divergent entry and all
        // that follow it, then appends the leader's entries — while preserving the matching prefix.
        var log = new InMemoryRaftLog<string>();
        log.Append((1L, "a"));     // index 0 — matches the leader's prefix
        log.Append((2L, "stale")); // index 1 — conflicts with the leader
        var follower = new RaftNode<string>("f", log) { CurrentTerm = 2 };

        follower.Inbox.Enqueue(new AppendEntriesRequest<string>(
            Term: 3, LeaderId: "L", PrevLogIndex: 0, PrevLogTerm: 1,
            Entries: [(3L, "new")], LeaderCommitIndex: -1));
        follower.ProcessMessages();

        follower.Outbox.TryDequeue(out var sent).Should().BeTrue();
        sent.Message.Should().BeOfType<AppendEntriesResponse>().Which.Success.Should().BeTrue();
        log.Count.Should().Be(2);
        log.GetEntry(0).Command.Should().Be("a", "the matching prefix is preserved");
        log.GetEntry(1).Should().Be((3L, "new"), "the conflicting term-2 entry is replaced by the leader's term-3 entry");
    }

    // ───── State Machine Safety — the figure-8 commit rule (§5.4.2) ────────────

    [Fact]
    public async Task Leader_DoesNotCommitPriorTermEntry_ByReplicaCountAlone_UntilCurrentTermEntryCommits()
    {
        // The figure-8 problem: a leader must never commit an entry from a PRIOR term by counting
        // replicas alone — a later leader could still overwrite it. The leader may only advance
        // commitIndex to a CURRENT-term entry that has reached a majority; prior-term entries then
        // commit indirectly, carried along underneath it.
        var log = new InMemoryRaftLog<string>();
        log.Append((1L, "prior")); // index 0 — replicated under an earlier (term-1) leader
        var leader = new RaftNode<string>("L", log) { CurrentTerm = 3, State = RaftNodeState.Leader };
        leader.PeerIds.AddRange(["f1", "f2"]); // 3-node cluster, majority = 2

        // Replicate the prior-term entry to BOTH followers — a full majority holds it.
        // MatchIndex 0: each follower has acknowledged through log index 0 (the prior entry).
        leader.Inbox.Enqueue(new AppendEntriesResponse(3, true, "f1", 0));
        leader.Inbox.Enqueue(new AppendEntriesResponse(3, true, "f2", 0));
        leader.ProcessMessages();

        leader.GetCommittedLog().Should().BeEmpty(
            "a prior-term entry replicated to a majority must NOT be committed by replica count alone");

        // The leader appends and replicates a CURRENT-term (3) entry on top of the prior-term one.
        await leader.ProposeAsync("current");
        // MatchIndex 1: each follower now has the current-term entry at log index 1.
        leader.Inbox.Enqueue(new AppendEntriesResponse(3, true, "f1", 1));
        leader.Inbox.Enqueue(new AppendEntriesResponse(3, true, "f2", 1));
        leader.ProcessMessages();

        leader.GetCommittedLog().Should().HaveCount(2,
            "once a current-term entry reaches a majority it commits, carrying the prior-term entry with it");
    }

    [Fact]
    public async Task Leader_DoesNotCommitEntry_OnStaleSuccessThatAcknowledgesAnEarlierIndex()
    {
        // A Success response carries the index the follower actually acknowledged by ITS request.
        // If the leader instead advanced matchIndex to its own log tail (which grew via an
        // interleaved proposal), a stale Success for an earlier, shorter AppendEntries would claim
        // the follower holds an entry it never received — and the leader would commit a leader-only
        // entry, violating State Machine Safety / Leader Completeness.
        var log = new InMemoryRaftLog<string>();
        var leader = new RaftNode<string>("L", log) { CurrentTerm = 1, State = RaftNodeState.Leader };
        leader.PeerIds.AddRange(["f1", "f2"]); // 3-node cluster, majority = 2

        await leader.ProposeAsync("A"); // index 0, term 1 — AppendEntries for index 0 dispatched to f1
        await leader.ProposeAsync("B"); // index 1, term 1 — log tail is now 1, but f1 only has up to 0

        // f1 responds Success for the FIRST AppendEntries: it acknowledges through index 0 only.
        leader.Inbox.Enqueue(new AppendEntriesResponse(1, true, "f1", 0));
        leader.ProcessMessages();

        // Index 0 ("A") legitimately commits — the leader and f1 (matchIndex 0) form a majority and
        // it is a current-term entry. But index 1 ("B") is held only by the leader and MUST NOT
        // commit. The bug advanced matchIndex[f1] to the log tail (1), counted f1 toward "B", and
        // committed the leader-only entry — so it returned 2.
        leader.GetCommittedLog().Should().HaveCount(1,
            "only index 0 reached a majority; the leader-only index 1 must not be committed");
    }

    /// <summary>
    /// The Raft TLA+ spec drops any response whose term is below currentTerm (the vote-response
    /// handler here already does). Accepting a stale-term AppendEntriesResponse is not just
    /// nextIndex churn: a delayed Success ack from an OLD leadership of this node can carry a
    /// MatchIndex that — after the old long log was truncated by an intervening leader and
    /// regrown in the current term — now points AT a current-term entry. Crediting the follower
    /// for it lets TryAdvanceCommitIndex count a replica that never acknowledged the
    /// current-term entry, committing a leader-only entry: a State Machine Safety violation.
    /// </summary>
    [Fact]
    public async Task Leader_DropsStaleTermAppendEntriesResponse_DoesNotCommitOnItsAck()
    {
        // L led in term 2 with a 2-entry log; a term-3 leader truncated it to one entry (Y@3);
        // L is now leader at term 4 and proposes A@4 at index 1.
        var log = new InMemoryRaftLog<string>();
        log.Append((3L, "Y")); // index 0 — prior term
        var leader = new RaftNode<string>("L", log) { CurrentTerm = 4, State = RaftNodeState.Leader };
        leader.PeerIds.AddRange(["f1", "f2"]); // 3-node cluster, majority = 2

        await leader.ProposeAsync("A"); // index 1, term 4

        // A delayed ack from L's term-2 leadership: f1 acknowledged index 1 of the OLD log
        // (a term-2 entry long since truncated away) — not today's A@4.
        leader.Inbox.Enqueue(new AppendEntriesResponse(2, true, "f1", 1));
        leader.ProcessMessages();

        leader.GetCommittedLog().Should().BeEmpty(
            "a stale-term ack must be dropped — f1 never acknowledged the current-term entry, " +
            "so counting it commits a leader-only entry (State Machine Safety violation)");
    }

    // ───── Constructor input validation — fail-fast at the API boundary ──────

    /// <summary>
    /// Pins the fail-fast invariant: constructing a <see cref="RaftNode{TCommand}"/> with a
    /// null, empty, or whitespace <c>nodeId</c> must throw <see cref="ArgumentException"/>
    /// immediately at construction — not silently propagate an invalid identity into protocol
    /// messages, dictionary keys, and vote-tracking structures, where it would produce routing
    /// errors or allow duplicate-vote scenarios at protocol time.
    /// The repo convention in <c>src/Distributed/</c> is
    /// <c>ArgumentException.ThrowIfNullOrWhiteSpace(nodeId)</c> — used by
    /// <c>GCounter</c>, <c>ORSet</c>, <c>VectorClock</c>, and <c>ConsistentHashRing</c>.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_WithInvalidNodeId_ThrowsArgumentException(string? invalidNodeId)
    {
        // Predict: ArgumentException is thrown at the first line of the ctor — before NodeId is
        // assigned — because ArgumentException.ThrowIfNullOrWhiteSpace fires on a null, empty,
        // or whitespace argument. Without the guard, no exception is thrown and NodeId is
        // assigned the invalid value, violating the fail-fast convention.
        var act = () => new RaftNode<string>(invalidNodeId!);

        act.Should().Throw<ArgumentException>(
            "RaftNode must reject a null, empty, or whitespace nodeId at construction — " +
            "matching the ArgumentException.ThrowIfNullOrWhiteSpace convention used by " +
            "GCounter, ORSet, VectorClock, and other src/Distributed/ types");
    }
}
