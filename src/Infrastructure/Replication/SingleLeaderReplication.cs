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
namespace Boutquin.Storage.Infrastructure.Replication;

/// <summary>
/// A single-leader replication system where all writes go to the leader and are replicated to followers
/// via a replication log.
/// </summary>
/// <remarks>
/// <para>
/// <b>How it works:</b> The leader maintains an in-memory key-value store and a replication log.
/// Each write is applied to the leader's store and appended to the log with a monotonically increasing
/// sequence number. Followers maintain their own stores and high-water marks (last applied sequence number).
/// Syncing a follower fetches log entries after its high-water mark and applies them in order.
/// </para>
///
/// <para>
/// <b>Thread safety:</b> All public methods are synchronized via <see cref="SemaphoreSlim"/>.
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

    /// <inheritdoc />
    public async Task WriteAsync(TKey key, TValue value, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _leaderStore[key] = value;
            var seqNum = Interlocked.Increment(ref _nextSequenceNumber);
            await _replicationLog.AppendAsync(key, value, seqNum, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<(TValue Value, bool Found)> ReadAsync(TKey key, string? preferredReplica = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
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
    public void AddFollower(string followerId)
    {
        _gate.Wait();
        try
        {
            _followerStores[followerId] = [];
            _followerHighWaterMarks[followerId] = 0;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void RemoveFollower(string followerId)
    {
        _gate.Wait();
        try
        {
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
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
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
        _gate.Wait();
        try
        {
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
}
