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
namespace Boutquin.Storage.Distributed.Replication;

/// <summary>
/// A single-leader replication system where all writes go to the leader and are replicated to followers
/// via a replication log.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope:</b> In-process reference implementation in <c>Boutquin.Storage.Distributed</c>,
/// which is not published to NuGet (see <c>src/Distributed/README.md</c>). Not durable
/// across process restart, not intended for production deployment. Leader/follower and quorum replication
/// is implemented for educational fidelity to DDIA 2e Ch. 6, but production-grade extensions
/// — sync-vs-async replication modes, sloppy-quorum with hinted handoff, snapshot-stream catchup APIs, and version-vector / Lamport conflict resolution — are not included. For production use, prefer <a href="https://getakka.net">Akka.NET cluster</a>, <a href="https://orleans.dev">Orleans</a> silos, or a dedicated replicated-log service.
/// </para>
///
/// <para>
/// <b>How it works:</b> The leader maintains an in-memory key-value store and a replication log.
/// Each write is applied to the leader's store and appended to the log with a monotonically increasing
/// sequence number. Followers maintain their own stores and high-water marks (last applied sequence number).
/// Syncing a follower fetches log entries after its high-water mark and applies them in order.
/// </para>
///
/// <para>
/// <b>Read-after-write consistency holds only against a fully-caught-up replica.</b> The
/// quorum-overlap argument that a Dynamo-style W + R &gt; N system relies on has no analogue here:
/// a single-leader topology has no read quorum. A read directed at the leader always observes the
/// latest committed write, but a read directed at a FOLLOWER reflects only entries up to that
/// follower's high-water mark — every write since the follower last synced is invisible to it
/// (asynchronous-replication lag, DDIA 2e Ch. 6 — &quot;Problems with Replication Lag&quot;). So the
/// read-your-writes guarantee holds only when the read targets the leader, or a follower whose
/// high-water mark has reached the write's sequence number after a sync. Reading an unsynced
/// follower can return a stale value or report the key absent; that staleness is an inherent
/// property of asynchronous single-leader replication, not a defect. Synchronous replication and
/// follower read-your-writes routing are documented future enhancements, not implemented here.
/// </para>
///
/// <para>
/// <b>Thread safety:</b> All public methods are synchronized via <see cref="SemaphoreSlim"/>, and
/// <see cref="Dispose"/> may run concurrently with them (see its remarks).
/// </para>
/// </remarks>
/// <typeparam name="TKey">The key type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
public sealed class SingleLeaderReplication<TKey, TValue> : ISingleLeaderReplication<TKey, TValue>
    where TKey : IComparable<TKey>
{
    private readonly Dictionary<TKey, TValue> _leaderStore = [];
    private readonly ReplicationLog<TKey, TValue> _replicationLog = new();
    private readonly Dictionary<string, Dictionary<TKey, TValue>> _followerStores = [];
    private readonly Dictionary<string, long> _followerHighWaterMarks = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _nextSequenceNumber;
    private readonly List<ChangeRecord<TKey, TValue>> _cdcHistory = [];
    private readonly Dictionary<TKey, ChangeRecord<TKey, TValue>> _cdcLatest = [];
    private int _disposed;

    /// <inheritdoc />
    public async Task WriteAsync(TKey key, TValue value, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            var seqNum = checked(_nextSequenceNumber + 1);
            var operation = _leaderStore.ContainsKey(key) ? ChangeOperation.Update : ChangeOperation.Insert;
            await _replicationLog.AppendAsync(key, value, seqNum, ct).ConfigureAwait(false);
            _leaderStore[key] = value;
            _nextSequenceNumber = seqNum;
            var change = new ChangeRecord<TKey, TValue>(seqNum - 1, operation, key, value, DateTimeOffset.UtcNow);
            _cdcHistory.Add(change);
            _cdcLatest[key] = change;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async ValueTask<CdcSnapshot<TKey, TValue>> CaptureCdcSnapshotAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            return new(_cdcLatest.Values.OrderBy(r => r.Position).Select(r => r with { Operation = ChangeOperation.Snapshot }).ToArray(), _nextSequenceNumber);
        }
        finally { _gate.Release(); }
    }

    internal async ValueTask<ChangeRecord<TKey, TValue>[]> CaptureCdcChangesAsync(long position, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { ObjectDisposedException.ThrowIf(_disposed != 0, this); return _cdcHistory.Where(r => r.Position >= position).ToArray(); }
        finally { _gate.Release(); }
    }

    /// <inheritdoc />
    public async Task<(TValue Value, bool Found)> ReadAsync(TKey key, string? preferredReplica = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (preferredReplica is null)
            {
                // Read from leader
                return _leaderStore.TryGetValue(key, out var leaderValue)
                    ? (leaderValue, true)
                    : (default!, false);
            }

            if (!_followerStores.TryGetValue(preferredReplica, out var followerStore))
            {
                throw new ArgumentException($"Unknown replica: {preferredReplica}", nameof(preferredReplica));
            }

            return followerStore.TryGetValue(key, out var followerValue)
                ? (followerValue, true)
                : (default!, false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Implements DDIA 2e Ch. 6 "Setting Up New Followers": the follower is seeded with a
    /// consistent snapshot of the leader's store, associated with the leader's current
    /// replication-log position (the in-process equivalent of PostgreSQL's log sequence
    /// number). Its first sync then requests only the changes since that position — so a
    /// follower added after the log has truncated still catches up, instead of permanently
    /// failing with <see cref="FollowerTooFarBehindException"/> and no snapshot to
    /// rebootstrap from. The snapshot is consistent because it is taken under the same gate
    /// that serializes writes.
    /// </remarks>
    public void AddFollower(string followerId)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        _gate.Wait();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            _followerStores[followerId] = new Dictionary<TKey, TValue>(_leaderStore);
            _followerHighWaterMarks[followerId] = _replicationLog.GetLatestSequenceNumber();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void RemoveFollower(string followerId)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        _gate.Wait();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            _followerStores.Remove(followerId);
            _followerHighWaterMarks.Remove(followerId);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<long> SyncFollowerAsync(string followerId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (!_followerStores.TryGetValue(followerId, out var store))
            {
                throw new ArgumentException($"Unknown follower: {followerId}", nameof(followerId));
            }

            var highWaterMark = _followerHighWaterMarks[followerId];
            var entries = await _replicationLog.GetEntriesAfterAsync(highWaterMark, ct).ConfigureAwait(false);

            foreach (var (key, value, seqNum) in entries)
            {
                store[key] = value;
                _followerHighWaterMarks[followerId] = seqNum;
            }

            // Opportunistically reclaim replication-log memory: if every follower has advanced
            // past some sequence number, entries at or below that number are safe to truncate.
            // The watermark is the MIN follower hwm; any lower bound is safe because every
            // follower has already applied through at least that sequence. A brand-new follower
            // at hwm=0 keeps the full log alive.
            if (_followerHighWaterMarks.Count > 0)
            {
                var minHwm = long.MaxValue;
                foreach (var h in _followerHighWaterMarks.Values)
                {
                    if (h < minHwm)
                    {
                        minHwm = h;
                    }
                }

                // Only truncate strictly below minHwm+1 (i.e., below entries every follower has
                // already seen). Entry with seq == minHwm was applied; truncating it is fine.
                if (minHwm > 0)
                {
                    await _replicationLog.TruncateBelowAsync(minHwm + 1, ct).ConfigureAwait(false);
                }
            }

            return _followerHighWaterMarks[followerId];
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, long> GetReplicationLag()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        _gate.Wait();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            var leaderSeqNum = _replicationLog.GetLatestSequenceNumber();
            var lag = new Dictionary<string, long>();

            foreach (var (followerId, highWaterMark) in _followerHighWaterMarks)
            {
                lag[followerId] = leaderSeqNum - highWaterMark;
            }

            return lag;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Marks the instance disposed. Idempotent — concurrent disposers are gated by
    /// <see cref="Interlocked.Exchange(ref int, int)"/>. After dispose, every public method throws
    /// <see cref="ObjectDisposedException"/>, including a call already queued on the gate: it wakes
    /// when the current holder releases, observes the disposal and throws. An operation that held the
    /// gate when Dispose ran completes normally.
    /// </summary>
    /// <remarks>
    /// The gate <see cref="SemaphoreSlim"/> is deliberately never disposed. It allocates no kernel
    /// handle unless <see cref="SemaphoreSlim.AvailableWaitHandle"/> is read, which this type never
    /// does, and disposing it under a holder would make the holder's release throw after its mutation
    /// ran and strand every queued caller forever.
    /// </remarks>
    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
}
