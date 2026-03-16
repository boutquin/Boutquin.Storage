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
/// Represents a vector clock for tracking causal ordering of events in a distributed system.
///
/// <para>A vector clock is a data structure that assigns a logical timestamp to each event in a distributed
/// system. Each node maintains a vector of counters, one per node. When a node performs an operation, it
/// increments its own counter. When nodes communicate, they merge their vectors by taking the element-wise
/// maximum.</para>
///
/// <para><b>Causal ordering:</b> Vector clocks can determine whether two events are causally related or
/// concurrent. Event A happened-before event B if A's vector is component-wise &lt;= B's vector with at least
/// one strict &lt;. If neither A &lt;= B nor B &lt;= A, the events are concurrent (conflicting).</para>
///
/// <para><b>Applications:</b></para>
/// <para>- <b>Conflict detection:</b> In Dynamo-style databases, vector clocks detect conflicting writes
///   from different replicas that must be resolved (e.g., by last-writer-wins or application-level merging).</para>
/// <para>- <b>Causal consistency:</b> Ensuring that if operation A caused operation B, all nodes see A before B.</para>
///
/// <para><b>Limitations:</b></para>
/// <para>- Vector size grows with the number of nodes. In large clusters, truncation strategies (removing
///   entries for nodes that haven't communicated recently) are used, at the cost of losing some ordering info.</para>
///
/// <para><b>Reference:</b> Kleppmann, <i>Designing Data-Intensive Applications</i> (O'Reilly, 2017), Ch. 5 — "Replication",
/// section on "Detecting Concurrent Writes". Vector clocks are used in Dynamo-style databases (Riak, Voldemort)
/// to detect and resolve conflicting writes.</para>
/// </summary>
public interface IVectorClock
{
    /// <summary>
    /// Increments the counter for the specified node, recording that the node performed an operation.
    /// </summary>
    /// <param name="nodeId">The identifier of the node performing the operation.</param>
    void Increment(string nodeId);

    /// <summary>
    /// Returns a read-only snapshot of the current vector clock state.
    /// </summary>
    /// <returns>A dictionary mapping node identifiers to their counter values.</returns>
    IReadOnlyDictionary<string, long> GetClock();

    /// <summary>
    /// Compares this vector clock with another to determine causal ordering.
    /// </summary>
    /// <param name="other">The other vector clock to compare against.</param>
    /// <returns>The causal relationship between the two clocks.</returns>
    VectorClockComparison CompareTo(IVectorClock other);

    /// <summary>
    /// Merges this vector clock with another by taking the element-wise maximum of all counters.
    /// </summary>
    /// <param name="other">The other vector clock to merge with.</param>
    /// <returns>A new vector clock representing the merged state.</returns>
    IVectorClock Merge(IVectorClock other);
}
