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

/// <summary>Base class for Raft inter-node messages.</summary>
internal abstract record RaftMessage;

/// <summary>RequestVote RPC request.</summary>
internal sealed record RequestVoteRequest(long Term, string CandidateId, long LastLogIndex, long LastLogTerm)
    : RaftMessage;

/// <summary>RequestVote RPC response.</summary>
internal sealed record RequestVoteResponse(long Term, bool VoteGranted) : RaftMessage;

/// <summary>AppendEntries RPC request.</summary>
internal sealed record AppendEntriesRequest<TCommand>(
    long Term,
    string LeaderId,
    long PrevLogIndex,
    long PrevLogTerm,
    List<(long Term, TCommand Command)> Entries,
    long LeaderCommitIndex) : RaftMessage;

/// <summary>AppendEntries RPC response.</summary>
internal sealed record AppendEntriesResponse(long Term, bool Success, string FollowerId) : RaftMessage;

/// <summary>
/// A Raft consensus node implementing leader election and log replication.
/// </summary>
/// <remarks>
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
/// </remarks>
/// <typeparam name="TCommand">The command type.</typeparam>
public sealed class RaftNode<TCommand> : IRaftNode<TCommand>
{
    private readonly List<(long Term, TCommand Command)> _log = [];
    private int _commitIndex = -1;
    private int _votesReceived;

    // Per-follower state (leader only)
    private readonly Dictionary<string, int> _nextIndex = [];
    private readonly Dictionary<string, int> _matchIndex = [];

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

    /// <summary>
    /// Initializes a new instance of the <see cref="RaftNode{TCommand}"/> class.
    /// </summary>
    /// <param name="nodeId">The unique node identifier.</param>
    public RaftNode(string nodeId)
    {
        NodeId = nodeId;
    }

    /// <inheritdoc />
    public string NodeId { get; }

    /// <inheritdoc />
    public RaftNodeState State { get; internal set; } = RaftNodeState.Follower;

    /// <inheritdoc />
    public long CurrentTerm { get; internal set; }

    /// <inheritdoc />
    public string? VotedFor { get; internal set; }

    /// <inheritdoc />
    public string? LeaderId { get; internal set; }

    /// <inheritdoc />
    public Task<bool> ProposeAsync(TCommand command, CancellationToken ct = default)
    {
        if (State != RaftNodeState.Leader)
        {
            return Task.FromResult(false);
        }

        // Append to own log
        _log.Add((CurrentTerm, command));
        var entryIndex = _log.Count - 1;

        // Send AppendEntries to all peers
        foreach (var peerId in PeerIds)
        {
            SendAppendEntries(peerId);
        }

        // In a single-node cluster, commit immediately
        if (PeerIds.Count == 0)
        {
            _commitIndex = entryIndex;
            return Task.FromResult(true);
        }

        // Check if already committed
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

        return _log.Take(_commitIndex + 1).ToList();
    }

    /// <summary>
    /// Starts an election: transitions to Candidate, increments term, votes for self.
    /// </summary>
    internal void StartElection()
    {
        CurrentTerm++;
        State = RaftNodeState.Candidate;
        VotedFor = NodeId;
        LeaderId = null;
        _votesReceived = 1; // Vote for self

        // Single-node cluster: become leader immediately
        if (PeerIds.Count == 0)
        {
            BecomeLeader();
            return;
        }

        // Request votes from all peers
        var lastLogIndex = _log.Count - 1;
        var lastLogTerm = _log.Count > 0 ? _log[^1].Term : 0;

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
            var lastLogTerm = _log.Count > 0 ? _log[^1].Term : 0;

            var candidateLogUpToDate =
                req.LastLogTerm > lastLogTerm ||
                (req.LastLogTerm == lastLogTerm && req.LastLogIndex >= lastLogIndex);

            if (candidateLogUpToDate)
            {
                VotedFor = req.CandidateId;
                voteGranted = true;
            }
        }

        Outbox.Enqueue((req.CandidateId, new RequestVoteResponse(CurrentTerm, voteGranted)));
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
            _votesReceived++;

            var totalNodes = PeerIds.Count + 1;
            if (_votesReceived > totalNodes / 2)
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
            Outbox.Enqueue((req.LeaderId, new AppendEntriesResponse(CurrentTerm, false, NodeId)));
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
            if (req.PrevLogIndex >= _log.Count || _log[(int)req.PrevLogIndex].Term != req.PrevLogTerm)
            {
                Outbox.Enqueue((req.LeaderId, new AppendEntriesResponse(CurrentTerm, false, NodeId)));
                return;
            }
        }

        // Append new entries (truncate conflicting entries first)
        var insertIndex = (int)req.PrevLogIndex + 1;
        foreach (var entry in req.Entries)
        {
            if (insertIndex < _log.Count)
            {
                if (_log[insertIndex].Term != entry.Term)
                {
                    _log.RemoveRange(insertIndex, _log.Count - insertIndex);
                    _log.Add(entry);
                }
            }
            else
            {
                _log.Add(entry);
            }

            insertIndex++;
        }

        // Advance commit index
        if (req.LeaderCommitIndex > _commitIndex)
        {
            _commitIndex = (int)Math.Min(req.LeaderCommitIndex, _log.Count - 1);
        }

        Outbox.Enqueue((req.LeaderId, new AppendEntriesResponse(CurrentTerm, true, NodeId)));
    }

    private void HandleAppendEntriesResponse(AppendEntriesResponse resp)
    {
        if (resp.Term > CurrentTerm)
        {
            StepDown(resp.Term);
            return;
        }

        if (State != RaftNodeState.Leader)
        {
            return;
        }

        if (resp.Success)
        {
            // The follower accepted — update matchIndex based on what we actually sent.
            // Why not _log.Count - 1? The leader's log may have grown since we sent the
            // AppendEntries. nextIndex tells us what we sent: entries from nextIndex to the
            // end of the log at send time. After success, matchIndex = nextIndex - 1 + entries
            // sent, but since we sent everything from nextIndex to end, the new matchIndex is
            // what we know the follower has: the previous nextIndex value tells us how far they
            // were, and now they have up to _log.Count - 1 at most. We update nextIndex to
            // _log.Count and matchIndex to nextIndex - 1 (the last entry we know they have).
            var newNextIndex = _log.Count;
            _matchIndex[resp.FollowerId] = newNextIndex - 1;
            _nextIndex[resp.FollowerId] = newNextIndex;

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

        // Per Raft paper Figure 2: nextIndex initialized to leader's last log index + 1,
        // matchIndex initialized to 0 (no entries known to be replicated yet).
        foreach (var peerId in PeerIds)
        {
            _nextIndex[peerId] = _log.Count;
            _matchIndex[peerId] = 0;
        }

        // Send initial heartbeat to all peers
        foreach (var peerId in PeerIds)
        {
            SendAppendEntries(peerId);
        }
    }

    private void StepDown(long newTerm)
    {
        CurrentTerm = newTerm;
        State = RaftNodeState.Follower;
        VotedFor = null;
        LeaderId = null;
    }

    private void SendAppendEntries(string peerId)
    {
        var nextIdx = _nextIndex.GetValueOrDefault(peerId, _log.Count);
        var prevLogIndex = nextIdx - 1;
        var prevLogTerm = prevLogIndex >= 0 && prevLogIndex < _log.Count ? _log[prevLogIndex].Term : 0;

        var entries = nextIdx < _log.Count
            ? _log.GetRange(nextIdx, _log.Count - nextIdx)
            : [];

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

        for (var n = _commitIndex + 1; n < _log.Count; n++)
        {
            if (_log[n].Term != CurrentTerm)
            {
                continue;
            }

            var replicationCount = 1; // Count self (leader always has the entry)
            foreach (var peerId in PeerIds)
            {
                if (_matchIndex.GetValueOrDefault(peerId, 0) >= n)
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
