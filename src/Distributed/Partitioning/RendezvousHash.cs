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
namespace Boutquin.Storage.Distributed.Partitioning;

/// <summary>
/// Rendezvous (Highest Random Weight) hashing implementation.
/// For each key, computes a hash for every node and selects the node with the highest hash.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope:</b> In-process reference implementation in <c>Boutquin.Storage.Distributed</c>,
/// which is not published to NuGet (see <c>src/Distributed/README.md</c>). Not durable
/// across process restart, not intended for production deployment. Consistent / range / rendezvous partitioning
/// is implemented for educational fidelity to DDIA 2e Ch. 7, but production-grade extensions
/// — fixed-shard-count routing with dynamic split/merge, locality-aware rebalancing, and partition-movement coordination — are not included. For production use, prefer a purpose-built sharding layer such as Vitess or the partitioner stack of Cassandra / ScyllaDB.
/// </para>
///
/// <para>
/// <b>Why hash(key + node) instead of separate hashes?</b> Combining key and node into a single hash
/// input ensures that the score for a (key, node) pair is deterministic and independent of other nodes.
/// This is what makes rendezvous hashing work: adding or removing a node doesn't change the scores
/// of existing (key, node) pairs, so only keys assigned to the affected node move.
/// </para>
///
/// <para>
/// <b>Node identity is its canonical text.</b> Scores hash, and the deterministic tie-break ordinally
/// compares, the node's canonical, culture-invariant text — the form <see cref="HashPartitioner{TKey}"/>
/// hashes keys by (tuples component by component, other types through <c>ToString()</c> under the
/// invariant culture) — so two node objects with equal canonical text are ONE node to
/// this algorithm regardless of their <c>Equals</c> — supplying distinct nodes that share a
/// string form degrades the documented determinism to insertion order. Give every node a
/// unique, stable string identity.
/// </para>
///
/// <para>
/// <b>Thread safety:</b> This class is not thread-safe. External synchronization is required for concurrent use.
/// </para>
/// </remarks>
/// <typeparam name="TNode">The type representing a node.</typeparam>
/// <remarks>
/// Initializes a new instance of the <see cref="RendezvousHash{TNode}"/> class.
/// </remarks>
/// <param name="hashAlgorithm">The hash algorithm to use. Defaults to Murmur3.</param>
public sealed class RendezvousHash<TNode>(IHashAlgorithm? hashAlgorithm = null) : IRendezvousHash<TNode>
    where TNode : notnull
{
    // Why HashSet + List? HashSet provides O(1) duplicate detection in AddNode (vs O(n) for
    // List.Contains). The List preserves iteration order for deterministic GetNode results.
    // Both are needed: HashSet for membership checks, List for ordered enumeration.
    private readonly HashSet<TNode> _nodeSet = [];
    private readonly List<TNode> _nodes = [];
    private readonly IHashAlgorithm _hashAlgorithm = hashAlgorithm ?? new Murmur3();

    /// <inheritdoc/>
    public void AddNode(TNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (_nodeSet.Add(node))
        {
            _nodes.Add(node);
        }
    }

    /// <inheritdoc/>
    public void RemoveNode(TNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        _nodeSet.Remove(node);
        _nodes.Remove(node);
    }

    /// <inheritdoc/>
    public TNode GetNode(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (_nodes.Count == 0)
        {
            throw new InvalidOperationException("No nodes available. Add at least one node before looking up keys.");
        }

        TNode? bestNode = default;
        var bestHash = uint.MinValue;
        var hasBest = false;

        foreach (var node in _nodes)
        {
            var hash = ComputeScore(key, node);
            // The first candidate always seeds the maximum — seeding bestHash = 0 with a
            // strict > alone would return null (no node selected) when every candidate
            // legitimately scores 0.
            // On a tie, prefer the node whose identity is ordinally smaller so the winner is
            // independent of node addition order — the same relation GetNodes ranks ties by.
            if (!hasBest || hash > bestHash
                || (hash == bestHash
                    && string.CompareOrdinal(NodeIdentity(node), NodeIdentity(bestNode!)) < 0))
            {
                bestHash = hash;
                bestNode = node;
                hasBest = true;
            }
        }

        return bestNode!;
    }

    /// <inheritdoc/>
    public IReadOnlyList<TNode> GetNodes(string key, int count)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        // Reject count <= 0, matching ConsistentHashRing.GetNodes: a zero or negative request is a
        // caller error, not a request for an empty set. Without this guard RendezvousHash silently
        // returned an empty list (.Take(0)/.Take(-1)) while its sibling threw — a divergent contract.
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        if (_nodes.Count == 0)
        {
            throw new InvalidOperationException("No nodes available. Add at least one node before looking up keys.");
        }

        // Strict fan-out contract, matching ConsistentHashRing.GetNodes: refuse to silently return
        // fewer nodes than requested. Downstream replication tiers rely on getting a node-set of the
        // requested size; silently downgrading masks a configuration mismatch (DDIA 2e Ch. 7, "Sharding").
        if (count > _nodes.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(count),
                count,
                $"Requested {count} distinct nodes but only {_nodes.Count} are registered. "
                + "Either reduce the request count or add more nodes.");
        }

        // Sort all nodes by their hash score for this key (descending), return top N.
        // ThenBy on the node's identity breaks ties deterministically regardless of node addition
        // order. It must be GetNode's ordinal relation: the default string comparer is
        // culture-sensitive, so the primary would disagree with GetNode and vary by thread culture.
        var scored = _nodes
            .Select(node => (Node: node, Hash: ComputeScore(key, node)))
            .OrderByDescending(x => x.Hash)
            .ThenBy(x => NodeIdentity(x.Node), StringComparer.Ordinal)
            .Take(count)
            .Select(x => x.Node)
            .ToList();

        return scored;
    }

    // Canonical, culture-invariant text: a culture-sensitive ToString (dates, numbers, and every tuple,
    // whose components format with the thread culture) would reorder tied nodes and rescore the same
    // (key, node) pair by the calling thread's culture.
    private static string NodeIdentity(TNode node) => CanonicalKeyText.Of(node);

    private uint ComputeScore(string key, TNode node)
    {
        // The node's canonical identity: a culture-sensitive node text would score the same
        // (key, node) pair differently depending on the calling thread's culture, breaking
        // deterministic highest-random-weight routing.
        var combined = string.Concat(key, ":", NodeIdentity(node));
        var bytes = Encoding.UTF8.GetBytes(combined);
        return _hashAlgorithm.ComputeHash(bytes);
    }
}
