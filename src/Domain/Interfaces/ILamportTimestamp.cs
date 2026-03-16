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
/// Defines a Lamport logical timestamp for establishing total ordering of events across distributed nodes.
///
/// <para>Lamport timestamps provide a total ordering of events: if event a causally precedes event b,
/// then L(a) &lt; L(b). However, L(a) &lt; L(b) does NOT imply a→b — concurrent events may be
/// ordered arbitrarily. Ties are broken by node ID for a deterministic total order.</para>
///
/// <para>Unlike vector clocks (which detect concurrency), Lamport timestamps impose a total order
/// suitable for total order broadcast and uniqueness constraints.</para>
///
/// <para><b>Reference:</b> Kleppmann, <i>Designing Data-Intensive Applications</i> (O'Reilly, 2017), Ch. 8 —
/// "Ordering Guarantees" and Lamport (1978), "Time, Clocks, and the Ordering of Events in a Distributed System".</para>
/// </summary>
public interface ILamportTimestamp
{
    /// <summary>
    /// Returns the current counter value.
    /// </summary>
    long GetCurrentTimestamp();

    /// <summary>
    /// Increments the local counter (local event) and returns the new value.
    /// </summary>
    long Increment();

    /// <summary>
    /// Updates the counter to max(local, received) + 1 (message receipt) and returns the new value.
    /// </summary>
    /// <param name="receivedTimestamp">The timestamp received from another node.</param>
    long Update(long receivedTimestamp);

    /// <summary>
    /// Gets the unique node identifier, used for tie-breaking in total ordering.
    /// </summary>
    string NodeId { get; }

    /// <summary>
    /// Compares this node's timestamp with another node's timestamp for total ordering.
    /// Compares timestamps first; breaks ties by lexicographic comparison of node IDs.
    /// </summary>
    /// <param name="otherTimestamp">The other node's timestamp value.</param>
    /// <param name="otherNodeId">The other node's ID.</param>
    /// <returns>Negative if this comes first, positive if other comes first, zero if equal.</returns>
    int CompareTo(long otherTimestamp, string otherNodeId);
}
