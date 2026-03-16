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
/// Defines a Dynamo-style quorum replication system.
///
/// <para>Writes are sent to W replicas and reads from R replicas. The quorum condition W + R &gt; N
/// guarantees that at least one replica participating in a read has the latest write, ensuring
/// read-after-write consistency. Sloppy quorums, read repair, and anti-entropy are further
/// refinements not covered by this interface.</para>
///
/// <para><b>Reference:</b> Kleppmann, <i>Designing Data-Intensive Applications</i> (O'Reilly, 2017), Ch. 5 —
/// "Leaderless Replication" and quorum consistency (Dynamo, Cassandra, Riak).</para>
/// </summary>
/// <typeparam name="TKey">The key type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
public interface IQuorumReplication<TKey, TValue>
    where TKey : IComparable<TKey>
{
    /// <summary>
    /// Writes a key-value pair to W replicas. Returns true if the write quorum was met.
    /// </summary>
    Task<bool> WriteAsync(TKey key, TValue value, CancellationToken ct = default);

    /// <summary>
    /// Reads from R replicas and returns the value with the highest version.
    /// Performs read repair on stale replicas.
    /// </summary>
    /// <returns>A tuple of (Value, Found, Version).</returns>
    Task<(TValue Value, bool Found, long Version)> ReadAsync(TKey key, CancellationToken ct = default);

    /// <summary>Total number of replicas.</summary>
    int N { get; }

    /// <summary>Write quorum size.</summary>
    int W { get; }

    /// <summary>Read quorum size.</summary>
    int R { get; }

    /// <summary>
    /// Simulates a node going up or down.
    /// </summary>
    /// <param name="replicaId">The replica identifier.</param>
    /// <param name="isAvailable">True if the replica is available, false if it's down.</param>
    void SetReplicaAvailability(string replicaId, bool isAvailable);
}
