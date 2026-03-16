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
namespace Boutquin.Storage.Infrastructure.Partitioning;

/// <summary>
/// Rendezvous (Highest Random Weight) hashing implementation.
/// For each key, computes a hash for every node and selects the node with the highest hash.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why hash(key + node) instead of separate hashes?</b> Combining key and node into a single hash
/// input ensures that the score for a (key, node) pair is deterministic and independent of other nodes.
/// This is what makes rendezvous hashing work: adding or removing a node doesn't change the scores
/// of existing (key, node) pairs, so only keys assigned to the affected node move.
/// </para>
///
/// <para>
/// <b>Thread safety:</b> This class is not thread-safe. External synchronization is required for concurrent use.
/// </para>
/// </remarks>
/// <typeparam name="TNode">The type representing a node.</typeparam>
public sealed class RendezvousHash<TNode> : IRendezvousHash<TNode>
    where TNode : notnull
{
    // Why HashSet + List? HashSet provides O(1) duplicate detection in AddNode (vs O(n) for
    // List.Contains). The List preserves iteration order for deterministic GetNode results.
    // Both are needed: HashSet for membership checks, List for ordered enumeration.
    private readonly HashSet<TNode> _nodeSet = new();
    private readonly List<TNode> _nodes = new();
    private readonly IHashAlgorithm _hashAlgorithm;

    /// <summary>
    /// Initializes a new instance of the <see cref="RendezvousHash{TNode}"/> class.
    /// </summary>
    /// <param name="hashAlgorithm">The hash algorithm to use. Defaults to Murmur3.</param>
    public RendezvousHash(IHashAlgorithm? hashAlgorithm = null)
    {
        _hashAlgorithm = hashAlgorithm ?? new Murmur3();
    }

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

        foreach (var node in _nodes)
        {
            var hash = ComputeScore(key, node);
            if (hash > bestHash)
            {
                bestHash = hash;
                bestNode = node;
            }
        }

        return bestNode!;
    }

    /// <inheritdoc/>
    public IReadOnlyList<TNode> GetNodes(string key, int count)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (_nodes.Count == 0)
        {
            throw new InvalidOperationException("No nodes available. Add at least one node before looking up keys.");
        }

        // Sort all nodes by their hash score for this key (descending), return top N
        var scored = _nodes
            .Select(node => (Node: node, Hash: ComputeScore(key, node)))
            .OrderByDescending(x => x.Hash)
            .Take(count)
            .Select(x => x.Node)
            .ToList();

        return scored;
    }

    private uint ComputeScore(string key, TNode node)
    {
        var combined = $"{key}:{node}";
        var bytes = Encoding.UTF8.GetBytes(combined);
        return _hashAlgorithm.ComputeHash(bytes);
    }
}
