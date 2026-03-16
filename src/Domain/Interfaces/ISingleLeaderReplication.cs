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
namespace Boutquin.Storage.Domain.Interfaces;

/// <summary>
/// Defines a single-leader (master-slave) replication system.
///
/// <para>All writes go through one leader node which replicates changes to followers via a replication log.
/// Followers serve read-only queries and may return stale data until they sync with the leader.
/// This is the simplest replication model — it provides strong consistency for writes but eventual
/// consistency for reads from followers.</para>
///
/// <para><b>Trade-offs:</b> Simple consistency model, but the leader is a single point of failure.
/// Failover requires leader election (see Raft in Phase 4).</para>
///
/// <para><b>Reference:</b> Kleppmann, <i>Designing Data-Intensive Applications</i> (O'Reilly, 2017), Ch. 5 —
/// "Leaders and Followers".</para>
/// </summary>
/// <typeparam name="TKey">The key type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
public interface ISingleLeaderReplication<TKey, TValue>
    where TKey : IComparable<TKey>
{
    /// <summary>
    /// Writes a key-value pair to the leader and appends it to the replication log.
    /// </summary>
    Task WriteAsync(TKey key, TValue value, CancellationToken ct = default);

    /// <summary>
    /// Reads a value from the leader or a specified follower replica.
    /// </summary>
    /// <param name="key">The key to read.</param>
    /// <param name="preferredReplica">
    /// The follower ID to read from, or null to read from the leader.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A tuple of (Value, Found).</returns>
    /// <exception cref="ArgumentException">Thrown if the specified replica does not exist.</exception>
    Task<(TValue Value, bool Found)> ReadAsync(TKey key, string? preferredReplica = null, CancellationToken ct = default);

    /// <summary>
    /// Registers a new follower replica.
    /// </summary>
    void AddFollower(string followerId);

    /// <summary>
    /// Removes a follower replica.
    /// </summary>
    void RemoveFollower(string followerId);

    /// <summary>
    /// Pulls missing entries from the replication log to the specified follower, applying them in order.
    /// </summary>
    /// <param name="followerId">The follower to sync.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The follower's new high-water mark (latest applied sequence number).</returns>
    Task<long> SyncFollowerAsync(string followerId, CancellationToken ct = default);

    /// <summary>
    /// Returns the replication lag (in sequence numbers) for each follower.
    /// </summary>
    IReadOnlyDictionary<string, long> GetReplicationLag();
}
