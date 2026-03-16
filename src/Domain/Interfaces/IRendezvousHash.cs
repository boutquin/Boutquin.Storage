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
/// Represents a rendezvous (highest random weight) hash for distributing keys across nodes.
///
/// <para>Rendezvous hashing assigns each key to the node that produces the highest hash value when
/// the key and node identifier are combined. Unlike consistent hashing, it does not require a ring
/// data structure — each key independently computes a score for every node and picks the winner.</para>
///
/// <para><b>Advantages over consistent hashing:</b></para>
/// <para>- No virtual nodes needed for uniform distribution — the hash function naturally distributes load.</para>
/// <para>- Simpler implementation — no sorted ring, no binary search.</para>
/// <para>- Adding/removing a node only moves keys that were assigned to the affected node.</para>
///
/// <para><b>Disadvantages:</b></para>
/// <para>- O(n) per lookup where n = number of nodes (must evaluate all nodes), vs O(log n) for consistent hashing.</para>
/// <para>- Not suitable for very large node counts (thousands), where consistent hashing's O(log n) lookup matters.</para>
///
/// <para><b>Complexity:</b></para>
/// <para>- <b>GetNode:</b> O(n) where n = number of nodes.</para>
/// <para>- <b>GetNodes:</b> O(n log n) — sort all nodes by hash, return top k.</para>
/// <para>- <b>AddNode/RemoveNode:</b> O(1) — just add/remove from the node list.</para>
///
/// <para><b>Reference:</b> Kleppmann, <i>Designing Data-Intensive Applications</i> (O'Reilly, 2017), Ch. 6 — "Partitioning".
/// Rendezvous hashing is an alternative to consistent hashing used in distributed caches and CDNs.</para>
/// </summary>
/// <typeparam name="TNode">The type representing a node.</typeparam>
public interface IRendezvousHash<TNode>
    where TNode : notnull
{
    /// <summary>
    /// Adds a node to the set of available nodes.
    /// </summary>
    /// <param name="node">The node to add.</param>
    void AddNode(TNode node);

    /// <summary>
    /// Removes a node from the set of available nodes.
    /// </summary>
    /// <param name="node">The node to remove.</param>
    void RemoveNode(TNode node);

    /// <summary>
    /// Returns the node with the highest hash score for the given key.
    /// </summary>
    /// <param name="key">The key to look up.</param>
    /// <returns>The node with the highest hash for the key.</returns>
    /// <exception cref="InvalidOperationException">Thrown if no nodes are available.</exception>
    TNode GetNode(string key);

    /// <summary>
    /// Returns the top N nodes ranked by hash score for the given key.
    /// </summary>
    /// <param name="key">The key to look up.</param>
    /// <param name="count">The number of nodes to return.</param>
    /// <returns>A list of nodes sorted by descending hash score, up to the requested count.</returns>
    IReadOnlyList<TNode> GetNodes(string key, int count);
}
