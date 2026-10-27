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
namespace Boutquin.Storage.Distributed.Interfaces.Partitioning;

/// <summary>
/// Represents a consistent hash ring for distributing keys across nodes with minimal redistribution
/// when nodes are added or removed.
///
/// <para>Consistent hashing maps both keys and nodes onto a circular hash space (ring). Each key is
/// assigned to the next node clockwise on the ring. When a node is added or removed, only the keys
/// between the new/removed node and its predecessor are affected — all other key assignments remain stable.</para>
///
/// <para><b>Virtual nodes:</b> To achieve uniform distribution, each physical node is mapped to multiple
/// positions on the ring (virtual nodes). Without virtual nodes, random placement of a small number of
/// nodes on the ring can lead to highly uneven load distribution.</para>
///
/// <para><b>Complexity:</b></para>
/// <para>- <b>AddNode:</b> O(v log n) where v = virtual nodes per physical node, n = total virtual nodes on ring.</para>
/// <para>- <b>RemoveNode:</b> O(v log n).</para>
/// <para>- <b>GetNode:</b> O(log n) — binary search on the sorted ring.</para>
/// <para>- <b>GetNodes:</b> O(k log n) where k = requested node count.</para>
///
/// <para><b>Reference:</b> Kleppmann, <i>Designing Data-Intensive Applications</i> (O'Reilly, 2nd ed., 2026), Ch. 7 — "Sharding",
/// section on consistent hashing. Used in Dynamo, Cassandra, and Riak for partition assignment.</para>
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope:</b> In-process reference implementation in <c>Boutquin.Storage.Distributed</c>,
/// which is not published to NuGet (see <c>src/Distributed/README.md</c>). Not durable
/// across process restart, not intended for production deployment. The consistent-hashing contract
/// is implemented for educational fidelity to DDIA 2e Ch. 7, but production-grade extensions
/// — weighted virtual-node layouts and rebalancing-event subscriptions — are not included. For production use, prefer the sharding layer of Cassandra / ScyllaDB.
/// </para>
/// </remarks>
/// <typeparam name="TNode">The type representing a node (e.g., server name, IP address).</typeparam>
public interface IConsistentHashRing<TNode>
    where TNode : notnull
{
    /// <summary>
    /// Adds a node to the hash ring with its virtual node replicas.
    /// </summary>
    /// <param name="node">The node to add.</param>
    void AddNode(TNode node);

    /// <summary>
    /// Removes a node and all its virtual node replicas from the hash ring.
    /// </summary>
    /// <param name="node">The node to remove.</param>
    void RemoveNode(TNode node);

    /// <summary>
    /// Returns the node responsible for the given key (the next node clockwise on the ring).
    /// </summary>
    /// <param name="key">The key to look up.</param>
    /// <returns>The node responsible for the key.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the ring is empty.</exception>
    TNode GetNode(string key);

    /// <summary>
    /// Returns multiple distinct physical nodes for the given key, walking clockwise around the ring.
    /// Useful for replication — the key is stored on the first N distinct physical nodes.
    /// </summary>
    /// <param name="key">The key to look up.</param>
    /// <param name="count">The number of distinct physical nodes to return.</param>
    /// <returns>A list of distinct physical nodes, up to the requested count or the total number of physical nodes.</returns>
    IReadOnlyList<TNode> GetNodes(string key, int count);
}
