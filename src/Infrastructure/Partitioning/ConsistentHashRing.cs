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
/// A consistent hash ring implementation using virtual nodes for uniform key distribution.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why SortedDictionary?</b> The ring requires ordered traversal (finding the next clockwise node)
/// and efficient insertion/removal. SortedDictionary provides O(log n) for all these operations.
/// An alternative would be a sorted array with binary search, but it has O(n) insertion cost.
/// </para>
///
/// <para>
/// <b>Why virtual nodes?</b> With only physical nodes on the ring, a small number of nodes (say 3)
/// can lead to highly uneven partitions. Virtual nodes (e.g., 150 per physical node) smooth out
/// the distribution. Each virtual node maps back to its physical node.
/// </para>
///
/// <para>
/// <b>Thread safety:</b> This class is not thread-safe. External synchronization is required for concurrent use.
/// </para>
/// </remarks>
/// <typeparam name="TNode">The type representing a node.</typeparam>
public sealed class ConsistentHashRing<TNode> : IConsistentHashRing<TNode>
    where TNode : notnull
{
    private readonly SortedDictionary<int, TNode> _ring = new();
    private readonly HashSet<TNode> _physicalNodes = new();
    private readonly int _virtualNodeCount;
    private readonly IHashAlgorithm _hashAlgorithm;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConsistentHashRing{TNode}"/> class.
    /// </summary>
    /// <param name="virtualNodeCount">
    /// The number of virtual nodes per physical node. Defaults to 150.
    /// Recommended: 100–200 for production. Higher values improve load distribution uniformity
    /// but increase memory usage (each virtual node is a point on the ring). For 3–5 physical
    /// nodes, 150 virtual nodes per node typically achieves &lt;10% load variance.
    /// </param>
    /// <param name="hashAlgorithm">The hash algorithm to use. Defaults to Murmur3.</param>
    public ConsistentHashRing(int virtualNodeCount = 150, IHashAlgorithm? hashAlgorithm = null)
    {
        if (virtualNodeCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(virtualNodeCount), "Virtual node count must be at least 1.");
        }

        _virtualNodeCount = virtualNodeCount;
        _hashAlgorithm = hashAlgorithm ?? new Murmur3();
    }

    /// <inheritdoc/>
    public void AddNode(TNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (!_physicalNodes.Add(node))
        {
            return; // Already added
        }

        for (var i = 0; i < _virtualNodeCount; i++)
        {
            var virtualKey = $"{node}#{i}";
            var hash = ComputeHash(virtualKey);
            _ring[hash] = node;
        }
    }

    /// <inheritdoc/>
    public void RemoveNode(TNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (!_physicalNodes.Remove(node))
        {
            return; // Not present
        }

        for (var i = 0; i < _virtualNodeCount; i++)
        {
            var virtualKey = $"{node}#{i}";
            var hash = ComputeHash(virtualKey);
            _ring.Remove(hash);
        }
    }

    /// <inheritdoc/>
    public TNode GetNode(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (_ring.Count == 0)
        {
            throw new InvalidOperationException("The hash ring is empty. Add at least one node before looking up keys.");
        }

        var hash = ComputeHash(key);

        // Find the first node clockwise from the hash position.
        // Walk the ring looking for the first key >= hash.
        foreach (var kvp in _ring)
        {
            if (kvp.Key >= hash)
            {
                return kvp.Value;
            }
        }

        // Wrap around — return the first node on the ring
        return _ring.First().Value;
    }

    /// <inheritdoc/>
    public IReadOnlyList<TNode> GetNodes(string key, int count)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (_ring.Count == 0)
        {
            throw new InvalidOperationException("The hash ring is empty. Add at least one node before looking up keys.");
        }

        var distinctNodes = new List<TNode>();
        var seen = new HashSet<TNode>();
        var hash = ComputeHash(key);

        // Walk clockwise from the hash position, collecting distinct physical nodes
        var started = false;
        foreach (var kvp in _ring)
        {
            if (kvp.Key >= hash)
            {
                started = true;
            }

            if (started && seen.Add(kvp.Value))
            {
                distinctNodes.Add(kvp.Value);
                if (distinctNodes.Count >= count)
                {
                    return distinctNodes;
                }
            }
        }

        // Wrap around from the beginning of the ring
        foreach (var kvp in _ring)
        {
            if (kvp.Key >= hash)
            {
                break; // We've already processed these
            }

            if (seen.Add(kvp.Value))
            {
                distinctNodes.Add(kvp.Value);
                if (distinctNodes.Count >= count)
                {
                    return distinctNodes;
                }
            }
        }

        return distinctNodes;
    }

    private int ComputeHash(string key)
    {
        var bytes = Encoding.UTF8.GetBytes(key);
        // Why cast to int? SortedDictionary<int, TNode> uses int keys. The hash algorithm returns uint,
        // but the ordering of int values after casting from uint preserves the relative order needed for
        // clockwise traversal. We just need a consistent total order, not unsigned semantics.
        return unchecked((int)_hashAlgorithm.ComputeHash(bytes));
    }
}
