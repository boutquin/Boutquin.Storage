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

/// <summary>Base class for Raft inter-node messages.</summary>
internal abstract record RaftMessage;

/// <summary>RequestVote RPC request.</summary>
internal sealed record RequestVoteRequest(long Term, string CandidateId, long LastLogIndex, long LastLogTerm)
    : RaftMessage;

/// <summary>
/// RequestVote RPC response. <paramref name="VoterId"/> identifies the responding voter so the
/// candidate can count <em>distinct</em> granting voters (Ongaro &amp; Ousterhout 2014 Fig. 2 —
/// <c>votesGranted</c> is a per-peer set), making grant-counting idempotent under message
/// duplication. Without it, one honest follower's grant delivered twice would double-count toward
/// the majority threshold — the same dedup role <c>FollowerId</c> plays on the AppendEntries path.
/// </summary>
internal sealed record RequestVoteResponse(long Term, bool VoteGranted, string VoterId) : RaftMessage;

/// <summary>AppendEntries RPC request.</summary>
internal sealed record AppendEntriesRequest<TCommand>(
    long Term,
    string LeaderId,
    long PrevLogIndex,
    long PrevLogTerm,
    List<(long Term, TCommand Command)> Entries,
    long LeaderCommitIndex) : RaftMessage;

/// <summary>
/// AppendEntries RPC response. <paramref name="MatchIndex"/> is the index of the highest log entry
/// the follower has confirmed replicated <em>by this request</em> (<c>prevLogIndex + entries.Count</c>);
/// it lets the leader advance matchIndex to what was actually acknowledged rather than to its own —
/// possibly since-grown — log tail. It is <c>-1</c> on a failure response (unused by the leader).
/// </summary>
internal sealed record AppendEntriesResponse(long Term, bool Success, string FollowerId, long MatchIndex) : RaftMessage;

/// <summary>
/// A Raft consensus node implementing leader election and log replication.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope:</b> In-process reference implementation in <c>Boutquin.Storage.Distributed</c>,
/// which is not published to NuGet (see <c>src/Distributed/README.md</c>). The default
/// in-memory collaborators do not survive restart. File-backed log and term/vote collaborators
/// are available, but do not make this a production deployment. The Raft consensus algorithm (Ongaro &amp; Ousterhout, 2014)
/// is implemented for educational fidelity to DDIA 2e Ch. 10, but production-grade extensions
/// — pre-vote, log compaction / snapshot install, joint-consensus reconfiguration, and leadership-transfer / read-index optimisations — are not included. For production use, prefer <a href="https://github.com/dotnet/dotNext">dotNext Raft</a> or an embedded Raft integration.
/// </para>
///
/// <para>
/// <b>How it works:</b> Nodes communicate via an in-memory message queue (simulating a network).
/// The <see cref="RaftCluster{TCommand}"/> routes messages between nodes and triggers election timeouts.
/// </para>
///
/// <para>
/// <b>Key invariants (Ongaro &amp; Ousterhout, 2014, Figure 2):</b>
/// - Election safety: at most one leader per term. Enforced by VotedFor persisting for the entire
///   term — it is only cleared when stepping down to a new (higher) term, never on AppendEntries.
/// - Log matching: if two logs contain an entry with the same index and term, all preceding entries are identical.
/// - Leader completeness: if a log entry is committed in a given term, it will be present in the logs of leaders for all higher terms.
/// </para>
///
/// <para>
/// <b>matchIndex tracking:</b> When a leader sends AppendEntries, it records the entries sent.
/// On a successful response, matchIndex advances to prevLogIndex + entries.Count (what was actually
/// sent), not _log.Count - 1 (which may have grown since the send).
/// </para>
///
/// <para>
/// <b>Commit index advancement:</b> TryAdvanceCommitIndex iterates ascending from commitIndex + 1,
/// committing entries sequentially. This ensures the Log Matching invariant — an entry at index N
/// is only committed if all entries before it are also committed.
/// </para>
///
/// <para>
/// <b>Threading contract — single-tick-driven (DDIA Ch. 9, Consensus, p. 364–376).</b>
/// Mutable state on this type — the injected log + persistent-state primitives,
/// <c>_commitIndex</c>, <c>_nextIndex</c>, <c>_matchIndex</c>, <c>_votesReceived</c>,
/// <c>LeaderId</c>, <c>State</c> — is <i>not</i> guarded by an internal lock, semaphore, or
/// interlocked exchange. <b>All mutating operations must be invoked from a single
/// thread</b>, normally the cluster's tick driver (<c>RaftCluster{TCommand}</c>'s
/// step loop). Mixing user-thread <c>ProposeAsync</c> calls with concurrent
/// <c>ProcessMessages</c> from the tick thread will tear state.
/// </para>
///
/// <para>
/// This is the canonical Raft single-tick-driven formulation: the cluster's step loop is
/// the only writer; user threads enqueue commands via the inbox and observe results via
/// the outbox / committed-log signals. Production-grade Raft hardenings — internal
/// synchronization, log compaction, joint-consensus reconfiguration — are post-GA scope.
/// Adding internal synchronization later is binary-compatible: the public API surface
/// (<c>PublicAPI.Shipped.txt</c>) does not change, so a future hardening pass can wrap
/// every method body in a <c>lock</c> or <c>SemaphoreSlim</c> without breaking external
/// callers. Consumers who need thread-safety today must wrap <see cref="RaftNode{TCommand}"/>
/// in their own gate.
/// </para>
///
/// <para>
/// <b>Persistence seam.</b> The Raft paper requires <c>currentTerm</c>, <c>votedFor</c>,
/// and the log itself to survive crashes (paper §5.2; DDIA 2e Ch. 10 p. 366–370).
/// <see cref="RaftNode{TCommand}"/> reads and writes <c>currentTerm</c> / <c>votedFor</c>
/// through an injected <see cref="IRaftPersistentState"/> and log entries through an
/// injected <see cref="IRaftLog{TCommand}"/>. When either ctor parameter is
/// <see langword="null"/>, the in-memory defaults
/// (<see cref="InMemoryRaftPersistentState"/> / <see cref="InMemoryRaftLog{TCommand}"/>) are
/// used — these are correct only for non-failing reference scenarios. Production
/// deployments must supply persistent implementations to avoid term-and-vote loss across
/// crashes. <see cref="FileRaftLog{TCommand}"/> and <see cref="FileRaftPersistentState"/>
/// provide file-backed implementations through those constructor parameters.
/// </para>
/// </remarks>
/// <typeparam name="TCommand">The command type.</typeparam>
public sealed class RaftNode<TCommand> : IRaftNode<TCommand>
{
    private readonly IRaftLog<TCommand> _log;
    private readonly IRaftPersistentState _persistentState;
    // long, not int: every other Raft index/term on this type is a long (CurrentTerm,
    // LastLogIndex, LeaderCommitIndex, MatchIndex), and _commitIndex is compared and min'd
    // against the long LeaderCommitIndex on the AppendEntries path. Keeping it int forced a
    // narrowing (int)Math.Min(...) cast there that could silently truncate a > int.MaxValue
    // leader commit index; as a long the cast is gone and the index math is uniform.
    private long _commitIndex = -1;

    // The set of distinct voters (including self) that have granted this candidate's current
    // election. A SET — not a counter — so a single voter's grant delivered more than once
    // (honest message duplication) counts toward the majority exactly once. Mirrors Fig. 2's
    // votesGranted; without it the candidate could "win" from fewer than a real quorum.
    private readonly HashSet<string> _votesReceived = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="RaftNode{TCommand}"/> class.
    /// </summary>
    /// <param name="nodeId">The unique node identifier.</param>
    /// <param name="log">Optional log backing. <see langword="null"/> uses the in-memory
    /// default (<see cref="InMemoryRaftLog{TCommand}"/>) — correct only for non-failing
    /// reference scenarios. Production deployments must supply a persistent
    /// implementation.</param>
    /// <param name="persistentState">Optional persistent-state backing.
    /// <see langword="null"/> uses the in-memory default
    /// (<see cref="InMemoryRaftPersistentState"/>) — correct only for non-failing reference
    /// scenarios. Production deployments must supply a persistent implementation to avoid
    /// term-and-vote loss across crashes.</param>
    public RaftNode(
        string nodeId,
        IRaftLog<TCommand>? log = null,
        IRaftPersistentState? persistentState = null)
    {
        // Fail-fast at the API boundary: a null/empty/whitespace nodeId would propagate into
        // protocol messages (VoterId, CandidateId, FollowerId, LeaderId), dictionary keys
        // (_nextIndex, _matchIndex), and the self-vote identity (_votesReceived), producing
        // routing errors and duplicate-vote scenarios at protocol time rather than construction.
        // Matches ArgumentException.ThrowIfNullOrWhiteSpace — the convention used by all
        // sibling types in src/Distributed/ (GCounter, ORSet, VectorClock, ConsistentHashRing).
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);

        // The id is persisted as a vote (votedFor) by every peer that grants one, and the durable
        // store encodes it as strict UTF-8. An id that is not well-formed UTF-16 (an unpaired
        // surrogate) cannot be encoded, so it would fail inside a peer's tick at protocol time.
        if (!IsWellFormedUtf16(nodeId))
        {
            throw new ArgumentException("The node id must be well-formed UTF-16 (no unpaired surrogates).", nameof(nodeId));
        }

        NodeId = nodeId;
        _log = log ?? new InMemoryRaftLog<TCommand>();
        _persistentState = persistentState ?? new InMemoryRaftPersistentState();
    }

    // True when every surrogate in the id belongs to a high/low pair, i.e. the id encodes to UTF-8.
    private static bool IsWellFormedUtf16(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(value[i]))
            {
                return false;
            }
        }

        return true;
    }

    // Per-follower state (leader only).
    // long, not int: MatchIndex is a Raft log index; all other index/term fields on this
    // type are long (CurrentTerm, LastLogIndex, LeaderCommitIndex, MatchIndex on the
    // message). An int dictionary would truncate any index > int.MaxValue at the cast
    // site, crediting the follower for entries it never received — a Leader Completeness
    // / State Machine Safety violation.
    private readonly Dictionary<string, long> _nextIndex = [];
    private readonly Dictionary<string, long> _matchIndex = [];

    /// <summary>
    /// Message inbox — other nodes and the cluster enqueue messages here.
    /// </summary>
    internal ConcurrentQueue<RaftMessage> Inbox { get; } = new();

    /// <summary>
    /// Message outbox — messages to be delivered to other nodes by the cluster.
    /// </summary>
    internal ConcurrentQueue<(string TargetNodeId, RaftMessage Message)> Outbox { get; } = new();

    /// <summary>
    /// Peer node IDs (set by the cluster).
    /// </summary>
    internal List<string> PeerIds { get; } = [];

    // Follower is a volatile role, not evidence that the node has never belonged to a cluster.
    internal bool HasPersistentHistory => CurrentTerm != 0 || VotedFor is not null || _log.Count != 0;

    /// <inheritdoc />
    public string NodeId { get; }

    /// <inheritdoc />
    public RaftNodeState State { get; internal set; } = RaftNodeState.Follower;

    /// <inheritdoc />
    public long CurrentTerm
    {
        get => _persistentState.CurrentTerm;
        internal set => _persistentState.CurrentTerm = value;
    }

    /// <inheritdoc />
    public string? VotedFor
    {
        get => _persistentState.VotedFor;
        internal set => _persistentState.VotedFor = value;
    }

    /// <inheritdoc />
    public string? LeaderId { get; internal set; }

    /// <inheritdoc />
    public Task<bool> ProposeAsync(TCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (State != RaftNodeState.Leader)
        {
            return Task.FromResult(false);
        }

        // Append to own log
        _log.Append((CurrentTerm, command));
        var entryIndex = _log.Count - 1;

        // Send AppendEntries to all peers
        foreach (var peerId in PeerIds)
        {
            SendAppendEntries(peerId);
        }

        // Advance the commit index through the SAME current-term guard the multi-node path
        // uses (Ongaro & Ousterhout 2014 §5.4.2 — a leader only advances its commit index over
        // entries from its OWN term). A single-node cluster is its own majority, so
        // TryAdvanceCommitIndex commits the just-appended current-term entry immediately —
        // there is no divergent "single-node commits unconditionally" branch that could skip the
        // term check. With no peers the majority threshold (totalNodes / 2 == 0) is met by the
        // leader's self-count alone.
        TryAdvanceCommitIndex();

        return Task.FromResult(_commitIndex >= entryIndex);
    }

    /// <inheritdoc />
    public IReadOnlyList<(long Term, TCommand Command)> GetCommittedLog()
    {
        if (_commitIndex < 0)
        {
            return [];
        }

        // _commitIndex is a long for index-math uniformity, but IRaftLog.Count is int-bounded.
        // A committed entry is one already present in _log, so _commitIndex
        // is bounded by _log.Count - 1 (≤ int.MaxValue); the cast cannot overflow.
        return _log.GetRange(0, (int)(_commitIndex + 1));
    }

    /// <summary>
    /// Starts an election: transitions to Candidate, increments term, votes for self.
    /// </summary>
    internal void StartElection()
    {
        // Term + vote-for-self persist as ONE durable update — two setter writes would open
        // a crash window pairing the new term with the previous term's vote on disk.
        _persistentState.SetTermAndVote(CurrentTerm + 1, NodeId);
        State = RaftNodeState.Candidate;
        LeaderId = null;
        _votesReceived.Clear();
        _votesReceived.Add(NodeId); // Vote for self

        // Single-node cluster: become leader immediately
        if (PeerIds.Count == 0)
        {
            BecomeLeader();
            return;
        }

        // Request votes from all peers
        var lastLogIndex = _log.Count - 1;
        var lastLogTerm = _log.Count > 0 ? _log.GetEntry(_log.Count - 1).Term : 0;

        foreach (var peerId in PeerIds)
        {
            Outbox.Enqueue((peerId, new RequestVoteRequest(CurrentTerm, NodeId, lastLogIndex, lastLogTerm)));
        }
    }

    /// <summary>
    /// Processes all messages in the inbox.
    /// </summary>
    internal void ProcessMessages()
    {
        while (Inbox.TryDequeue(out var message))
        {
            switch (message)
            {
                case RequestVoteRequest req:
                    HandleRequestVote(req);
                    break;
                case RequestVoteResponse resp:
                    HandleRequestVoteResponse(resp);
                    break;
                case AppendEntriesRequest<TCommand> req:
                    HandleAppendEntries(req);
                    break;
                case AppendEntriesResponse resp:
                    HandleAppendEntriesResponse(resp);
                    break;
            }
        }
    }

    private void HandleRequestVote(RequestVoteRequest req)
    {
        if (req.Term > CurrentTerm)
        {
            StepDown(req.Term);
        }

        var voteGranted = false;

        if (req.Term >= CurrentTerm &&
            (VotedFor is null || VotedFor == req.CandidateId))
        {
            var lastLogIndex = _log.Count - 1;
            var lastLogTerm = _log.Count > 0 ? _log.GetEntry(_log.Count - 1).Term : 0;

            var candidateLogUpToDate =
                req.LastLogTerm > lastLogTerm ||
                (req.LastLogTerm == lastLogTerm && req.LastLogIndex >= lastLogIndex);

            if (candidateLogUpToDate)
            {
                VotedFor = req.CandidateId;
                voteGranted = true;
            }
        }

        Outbox.Enqueue((req.CandidateId, new RequestVoteResponse(CurrentTerm, voteGranted, NodeId)));
    }

    private void HandleRequestVoteResponse(RequestVoteResponse resp)
    {
        if (resp.Term > CurrentTerm)
        {
            StepDown(resp.Term);
            return;
        }

        if (State != RaftNodeState.Candidate || resp.Term != CurrentTerm)
        {
            return;
        }

        if (resp.VoteGranted)
        {
            // Record by voter identity: a duplicate grant from the same voter is idempotent and
            // does not advance the count toward majority.
            _votesReceived.Add(resp.VoterId);

            var totalNodes = PeerIds.Count + 1;
            if (_votesReceived.Count > totalNodes / 2)
            {
                BecomeLeader();
            }
        }
    }

    private void HandleAppendEntries(AppendEntriesRequest<TCommand> req)
    {
        if (req.Term > CurrentTerm)
        {
            StepDown(req.Term);
        }

        if (req.Term < CurrentTerm)
        {
            Outbox.Enqueue((req.LeaderId, new AppendEntriesResponse(CurrentTerm, false, NodeId, -1)));
            return;
        }

        // Valid leader — reset to follower but do NOT clear VotedFor.
        // Why: Raft requires that a node votes for at most one candidate per term. Clearing
        // VotedFor here would allow double-voting if another candidate's RequestVote arrives
        // after this heartbeat. VotedFor is only cleared when stepping down to a new term.
        State = RaftNodeState.Follower;
        LeaderId = req.LeaderId;

        // Log consistency check
        if (req.PrevLogIndex >= 0)
        {
            if (req.PrevLogIndex >= _log.Count || _log.GetEntry((int)req.PrevLogIndex).Term != req.PrevLogTerm)
            {
                Outbox.Enqueue((req.LeaderId, new AppendEntriesResponse(CurrentTerm, false, NodeId, -1)));
                return;
            }
        }

        // Append new entries (truncate conflicting entries first)
        var insertIndex = (int)req.PrevLogIndex + 1;
        foreach (var entry in req.Entries)
        {
            if (insertIndex < _log.Count)
            {
                if (_log.GetEntry(insertIndex).Term != entry.Term)
                {
                    _log.TruncateFrom(insertIndex);
                    _log.Append(entry);
                }
            }
            else
            {
                _log.Append(entry);
            }

            insertIndex++;
        }

        // Advance commit index. _commitIndex and LeaderCommitIndex are both long, so the min
        // is taken in long space with no narrowing cast — _log.Count - 1 widens to long.
        if (req.LeaderCommitIndex > _commitIndex)
        {
            _commitIndex = Math.Min(req.LeaderCommitIndex, _log.Count - 1);
        }

        Outbox.Enqueue((req.LeaderId, new AppendEntriesResponse(CurrentTerm, true, NodeId, req.PrevLogIndex + req.Entries.Count)));
    }

    private void HandleAppendEntriesResponse(AppendEntriesResponse resp)
    {
        if (resp.Term > CurrentTerm)
        {
            StepDown(resp.Term);
            return;
        }

        // Drop stale-term responses (Raft TLA+ discipline; the vote-response handler already
        // does). A delayed Success ack from this node's OWN earlier leadership can carry a
        // MatchIndex that — after the old log was truncated by an intervening leader and
        // regrown in the current term — points at a CURRENT-term entry; crediting it lets
        // TryAdvanceCommitIndex commit a leader-only entry (State Machine Safety violation).
        if (resp.Term < CurrentTerm)
        {
            return;
        }

        if (State != RaftNodeState.Leader)
        {
            return;
        }

        if (resp.Success)
        {
            // matchIndex advances to what the follower ACTUALLY acknowledged in its request
            // (resp.MatchIndex = that request's prevLogIndex + entries.Count), never to the leader's
            // current log tail — which may have grown via an interleaved ProposeAsync since the
            // AppendEntries was sent. Using _log.Count - 1 here claimed the follower held entries it
            // had never received, letting TryAdvanceCommitIndex count it toward a current-term entry
            // and commit a leader-only entry (a State Machine Safety / Leader Completeness violation).
            // Guard against a stale or duplicate response regressing matchIndex.
            var acked = resp.MatchIndex;
            if (!_matchIndex.TryGetValue(resp.FollowerId, out var current) || acked > current)
            {
                _matchIndex[resp.FollowerId] = acked;
            }
            _nextIndex[resp.FollowerId] = _matchIndex[resp.FollowerId] + 1;

            TryAdvanceCommitIndex();
        }
        else
        {
            // Decrement nextIndex and retry
            if (_nextIndex.TryGetValue(resp.FollowerId, out var idx) && idx > 0)
            {
                _nextIndex[resp.FollowerId] = idx - 1;
                SendAppendEntries(resp.FollowerId);
            }
        }
    }

    private void BecomeLeader()
    {
        State = RaftNodeState.Leader;
        LeaderId = NodeId;

        // Per Raft paper Figure 2: nextIndex initialized to leader's last log index + 1 (in the
        // 1-indexed formulation). matchIndex initialized to a sentinel meaning "no entries known
        // to be replicated yet". This implementation uses 0-indexed logs, so the correct sentinel
        // is -1 — a matchIndex of 0 would incorrectly claim the entry at index 0 is replicated,
        // letting TryAdvanceCommitIndex commit log[0] before any AppendEntriesResponse lands.
        foreach (var peerId in PeerIds)
        {
            _nextIndex[peerId] = _log.Count;
            _matchIndex[peerId] = -1;
        }

        // Send initial heartbeat to all peers
        foreach (var peerId in PeerIds)
        {
            SendAppendEntries(peerId);
        }
    }

    private void StepDown(long newTerm)
    {
        // Atomic pair-write — see StartElection.
        _persistentState.SetTermAndVote(newTerm, null);
        State = RaftNodeState.Follower;
        LeaderId = null;
        // Clear per-follower leader state: stale _nextIndex / _matchIndex entries from a prior
        // leadership term would corrupt quorum math on re-election — the old entries name peers
        // from the previous term and may carry matchIndex values that pre-count entries toward
        // majority before any AppendEntries confirm replication.
        _nextIndex.Clear();
        _matchIndex.Clear();
    }

    // Coordination's virtual driver uses the same replication path for periodic retry and
    // commit-index propagation. A heartbeat is not itself proof of a linearizable read.
    internal void ReplicatePendingEntries()
    {
        if (State != RaftNodeState.Leader)
        {
            return;
        }
        foreach (var peerId in PeerIds)
        {
            SendAppendEntries(peerId);
        }
    }

    private void SendAppendEntries(string peerId)
    {
        var nextIdx = _nextIndex.GetValueOrDefault(peerId, _log.Count);
        var prevLogIndex = nextIdx - 1;
        // nextIdx is long; IRaftLog.GetEntry/GetRange take int (log size is bounded by
        // IRaftLog.Count ≤ int.MaxValue, so the narrowing casts below cannot overflow in practice).
        var prevLogTerm = prevLogIndex >= 0 && prevLogIndex < _log.Count ? _log.GetEntry((int)prevLogIndex).Term : 0;

        var entries = nextIdx < _log.Count
            ? [.. _log.GetRange((int)nextIdx, _log.Count - (int)nextIdx)]
            : new List<(long Term, TCommand Command)>();

        Outbox.Enqueue((peerId, new AppendEntriesRequest<TCommand>(
            CurrentTerm, NodeId, prevLogIndex, prevLogTerm, entries, _commitIndex)));
    }

    /// <summary>
    /// Advances the commit index by scanning uncommitted entries in ascending order.
    /// Only entries from the current term can be committed directly (Raft safety property §5.4.2).
    /// </summary>
    /// <remarks>
    /// Why ascending? Iterating from commitIndex + 1 upward ensures entries are committed
    /// sequentially, preserving the Log Matching invariant. Descending iteration could skip
    /// intermediate entries, committing index N before N-1 is verified.
    /// </remarks>
    private void TryAdvanceCommitIndex()
    {
        var totalNodes = PeerIds.Count + 1;

        // n walks log positions bounded by int-typed IRaftLog.Count. _commitIndex is a long for index-math
        // uniformity but is bounded by _log.Count - 1 here (we only ever commit entries already
        // in the log), so (int)(_commitIndex + 1) cannot overflow.
        for (var n = (int)(_commitIndex + 1); n < _log.Count; n++)
        {
            if (_log.GetEntry(n).Term != CurrentTerm)
            {
                continue;
            }

            var replicationCount = 1; // Count self (leader always has the entry)
            foreach (var peerId in PeerIds)
            {
                // Default -1 matches the BecomeLeader sentinel: an entry in _matchIndex is
                // "no entries replicated yet", so the peer contributes to replicationCount only
                // once an AppendEntriesResponse confirms an entry at or above index n.
                if (_matchIndex.GetValueOrDefault(peerId, -1) >= n)
                {
                    replicationCount++;
                }
            }

            // Strict majority: more than half of all nodes
            if (replicationCount > totalNodes / 2)
            {
                _commitIndex = n;
            }
            else
            {
                // If this entry isn't committed, no higher entries can be either
                break;
            }
        }
    }
}
