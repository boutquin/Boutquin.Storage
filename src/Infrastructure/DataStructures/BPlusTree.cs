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
namespace Boutquin.Storage.Infrastructure.DataStructures;

/// <summary>
/// A B+ tree implementation where all values are stored in leaf nodes and leaves are linked for efficient range scans.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why leaf-linked structure?</b> Range queries are the primary advantage of B+ trees over B-trees.
/// Once the start leaf is found via tree traversal (O(log n)), the query follows the leaf linked list
/// to collect results (O(k) for k results) without backtracking up the tree. This is critical for
/// database index scans.
/// </para>
///
/// <para>
/// <b>Why all values in leaves?</b> Internal nodes contain only keys and child pointers, maximizing the
/// branching factor. A higher branching factor means fewer levels (shorter tree), reducing the number of
/// page reads for on-disk implementations.
/// </para>
///
/// <para>
/// <b>Thread safety:</b> This class is not thread-safe. External synchronization is required for concurrent use.
/// </para>
/// </remarks>
/// <typeparam name="TKey">The type of the keys.</typeparam>
/// <typeparam name="TValue">The type of the values.</typeparam>
public sealed class BPlusTree<TKey, TValue> : IBPlusTree<TKey, TValue>
    where TKey : IComparable<TKey>, IEquatable<TKey>
{
    private abstract class BPlusTreeNode(bool isLeaf)
    {
        public List<TKey> Keys { get; } = [];
        public bool IsLeaf { get; } = isLeaf;
    }

    private sealed class InternalNode : BPlusTreeNode
    {
        public List<BPlusTreeNode> Children { get; } = [];

        public InternalNode() : base(false) { }
    }

    private sealed class LeafNode : BPlusTreeNode
    {
        public List<TValue> Values { get; } = [];
        public LeafNode? Next { get; set; }

        public LeafNode() : base(true) { }
    }

    private BPlusTreeNode _root;
    private readonly int _minimumDegree;

    /// <inheritdoc/>
    public int MinimumDegree => _minimumDegree;

    /// <inheritdoc/>
    public int Height => _empty ? 0 : _height;

    private int _height;
    private bool _empty = true;

    /// <summary>
    /// Initializes a new instance of the <see cref="BPlusTree{TKey, TValue}"/> class.
    /// </summary>
    /// <param name="minimumDegree">The minimum degree t of the B+ tree (every node holds at most 2t − 1 keys). Must be at least 2.</param>
    public BPlusTree(int minimumDegree)
    {
        if (minimumDegree < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumDegree), "Minimum degree must be at least 2.");
        }

        _minimumDegree = minimumDegree;
        _root = new LeafNode();
        _height = 1;
    }

    /// <summary>
    /// Structural invariant checker (RB-tree precedent): validates strictly-ascending keys
    /// within every node, separator-bound containment (every subtree's keys lie within the
    /// parent's [lower, upper) window — inclusive lower to match the copy-up convention),
    /// the occupancy bounds (≤ 2·minimumDegree − 1 keys per node, and ≥ minimumDegree − 1 for non-root nodes
    /// since splits never emit thinner nodes and no delete path exists), internal nodes
    /// carrying exactly keys+1 children, uniform leaf depth, the leaf chain visiting exactly
    /// the root-reachable leaves in left-to-right order, and global cross-leaf ascending
    /// order along the chain. Test-only via InternalsVisibleTo.
    /// </summary>
    internal bool SatisfiesBPlusTreeInvariants()
    {
        var leafDepth = -1;
        var leavesInOrder = new List<LeafNode>();

        // Bounds use presence flags (BTree-checker style) rather than default(TKey) sentinels,
        // and follow this tree's routing convention: lower bound INCLUSIVE (a separator equal
        // to the key routes right; leaf splits copy the promoted key up AND retain it in the
        // right leaf), upper bound EXCLUSIVE.
        bool Walk(BPlusTreeNode node, int depth, bool isRoot, bool hasLower, TKey? lower, bool hasUpper, TKey? upper)
        {
            // Maximum occupancy — mirrors SatisfiesBTreeInvariants' upper bound.
            if (node.Keys.Count > (2 * _minimumDegree) - 1)
            {
                return false;
            }

            // Minimum occupancy: splits emit nodes with ≥ minimumDegree−1 keys and no delete path
            // exists (RemoveAsync is unsupported by design), so a thinner non-root node is a
            // split bug. A non-empty root internal node must carry at least one separator.
            if (!isRoot && node.Keys.Count < _minimumDegree - 1)
            {
                return false;
            }
            if (isRoot && node is InternalNode && node.Keys.Count < 1)
            {
                return false;
            }

            for (var i = 1; i < node.Keys.Count; i++)
            {
                if (KeyOrder<TKey>.Compare(node.Keys[i - 1], node.Keys[i]) >= 0)
                {
                    return false;
                }
            }

            // Separator-bound containment: every key in this subtree lies in [lower, upper).
            // Without this, a mis-promoted separator that misroutes keys into a neighbouring
            // subtree keeps every node locally sorted and escapes detection.
            foreach (var key in node.Keys)
            {
                if (hasLower && KeyOrder<TKey>.Compare(key, lower!) < 0)
                {
                    return false;
                }
                if (hasUpper && KeyOrder<TKey>.Compare(key, upper!) >= 0)
                {
                    return false;
                }
            }

            if (node is LeafNode leafNode)
            {
                if (leafNode.Values.Count != leafNode.Keys.Count)
                {
                    return false;
                }
                if (leafDepth == -1)
                {
                    leafDepth = depth;
                }
                if (leafDepth != depth)
                {
                    return false;
                }
                leavesInOrder.Add(leafNode);
                return true;
            }

            var internalNode = (InternalNode)node;
            if (internalNode.Children.Count != internalNode.Keys.Count + 1)
            {
                return false;
            }
            for (var i = 0; i < internalNode.Children.Count; i++)
            {
                var childHasLower = i > 0 || hasLower;
                var childLower = i > 0 ? internalNode.Keys[i - 1] : lower;
                var childHasUpper = i < internalNode.Keys.Count || hasUpper;
                var childUpper = i < internalNode.Keys.Count ? internalNode.Keys[i] : upper;
                if (!Walk(internalNode.Children[i], depth + 1, isRoot: false, childHasLower, childLower, childHasUpper, childUpper))
                {
                    return false;
                }
            }
            return true;
        }

        if (!Walk(_root, 0, isRoot: true, hasLower: false, lower: default, hasUpper: false, upper: default))
        {
            return false;
        }

        // Cross-leaf global ordering: the concatenated leaf chain must be globally ascending —
        // per-node local sortedness alone cannot see an inversion across a leaf boundary.
        for (var i = 1; i < leavesInOrder.Count; i++)
        {
            if (leavesInOrder[i - 1].Keys.Count > 0 && leavesInOrder[i].Keys.Count > 0
                && KeyOrder<TKey>.Compare(leavesInOrder[i - 1].Keys[^1], leavesInOrder[i].Keys[0]) >= 0)
            {
                return false;
            }
        }

        // The leaf chain must visit exactly the root-reachable leaves, in minimumDegree.
        if (leavesInOrder.Count == 0)
        {
            return true;
        }
        var chain = leavesInOrder[0];
        foreach (var expected in leavesInOrder)
        {
            if (!ReferenceEquals(chain, expected))
            {
                return false;
            }
            chain = chain.Next!;
        }
        return chain is null;
    }

    /// <inheritdoc/>
    public Task SetAsync(TKey key, TValue value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Guard.AgainstNullOrDefault(() => key);
        Guard.AgainstNullOrDefault(() => value);
        _empty = false;

        var result = InsertIntoNode(_root, key, value);
        if (result != null)
        {
            // Root was split — create a new root
            var newRoot = new InternalNode();
            newRoot.Keys.Add(result.Value.PromotedKey);
            newRoot.Children.Add(_root);
            newRoot.Children.Add(result.Value.NewNode);
            _root = newRoot;
            _height++;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<(TValue Value, bool Found)> TryGetValueAsync(TKey key, CancellationToken cancellationToken = default)
    {
        Guard.AgainstNullOrDefault(() => key);

        var leaf = FindLeaf(key);
        for (var i = 0; i < leaf.Keys.Count; i++)
        {
            if (KeyOrder<TKey>.Compare(leaf.Keys[i], key) == 0)
            {
                return Task.FromResult((leaf.Values[i], true));
            }
        }

        return Task.FromResult((default(TValue)!, false));
    }

    /// <inheritdoc/>
    public async Task<bool> ContainsKeyAsync(TKey key, CancellationToken cancellationToken = default)
    {
        var (_, found) = await TryGetValueAsync(key, cancellationToken).ConfigureAwait(false);
        return found;
    }

    /// <inheritdoc/>
    public Task RemoveAsync(TKey key, CancellationToken cancellationToken = default)
    {
        Guard.AgainstNullOrDefault(() => key);

        // Why NotSupportedException? This B+tree is the read-optimised on-disk index analogue
        // (DDIA 2e Ch. 4); like its in-memory siblings BTree, RedBlackTree, and SkipListMemTable,
        // it follows append-only / tombstone semantics — deletes are represented as tombstone
        // entries in the LSM layer, not by removing keys from the tree. A balanced B+tree delete
        // (borrow / merge / separator-key fix-up) is deliberately not implemented; a leaf-only
        // unlink would silently violate the balance invariant and the interface contract.
        throw new NotSupportedException(
            "Remove is not supported in this B+tree. Deletes are tombstone entries in the LSM layer; "
            + "use ClearAsync to reset the tree.");
    }

    /// <inheritdoc/>
    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        _root = new LeafNode();
        _height = 1;
        _empty = true;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<IEnumerable<(TKey Key, TValue Value)>> GetAllItemsAsync(CancellationToken cancellationToken = default)
    {
        var items = new List<(TKey Key, TValue Value)>();
        var leaf = GetLeftmostLeaf();
        while (leaf != null)
        {
            for (var i = 0; i < leaf.Keys.Count; i++)
            {
                items.Add((leaf.Keys[i], leaf.Values[i]));
            }
            leaf = leaf.Next;
        }

        return Task.FromResult<IEnumerable<(TKey Key, TValue Value)>>(items);
    }

    /// <inheritdoc/>
    public async Task SetBulkAsync(IEnumerable<KeyValuePair<TKey, TValue>> items, CancellationToken cancellationToken = default)
    {
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await SetAsync(item.Key, item.Value, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public Task<IEnumerable<(TKey Key, TValue Value)>> RangeQueryAsync(TKey start, TKey end, CancellationToken cancellationToken = default)
    {
        Guard.AgainstNullOrDefault(() => start);
        Guard.AgainstNullOrDefault(() => end);

        if (KeyOrder<TKey>.Compare(start, end) > 0)
        {
            throw new ArgumentException(
                $"Range start must be less than or equal to end. Got start={start}, end={end}.",
                nameof(start));
        }

        var items = new List<(TKey Key, TValue Value)>();
        var leaf = FindLeaf(start);

        while (leaf != null)
        {
            for (var i = 0; i < leaf.Keys.Count; i++)
            {
                if (KeyOrder<TKey>.Compare(leaf.Keys[i], start) >= 0 && KeyOrder<TKey>.Compare(leaf.Keys[i], end) <= 0)
                {
                    items.Add((leaf.Keys[i], leaf.Values[i]));
                }
                else if (KeyOrder<TKey>.Compare(leaf.Keys[i], end) > 0)
                {
                    return Task.FromResult<IEnumerable<(TKey Key, TValue Value)>>(items);
                }
            }
            leaf = leaf.Next;
        }

        return Task.FromResult<IEnumerable<(TKey Key, TValue Value)>>(items);
    }

    private readonly record struct SplitResult(TKey PromotedKey, BPlusTreeNode NewNode);

    private SplitResult? InsertIntoNode(BPlusTreeNode node, TKey key, TValue value)
    {
        if (node.IsLeaf)
        {
            return InsertIntoLeaf((LeafNode)node, key, value);
        }

        return InsertIntoInternal((InternalNode)node, key, value);
    }

    private SplitResult? InsertIntoLeaf(LeafNode leaf, TKey key, TValue value)
    {
        var insertIndex = 0;
        while (insertIndex < leaf.Keys.Count && KeyOrder<TKey>.Compare(leaf.Keys[insertIndex], key) < 0)
        {
            insertIndex++;
        }

        // Update existing key
        if (insertIndex < leaf.Keys.Count && KeyOrder<TKey>.Compare(leaf.Keys[insertIndex], key) == 0)
        {
            leaf.Values[insertIndex] = value;
            return null;
        }

        leaf.Keys.Insert(insertIndex, key);
        leaf.Values.Insert(insertIndex, value);

        // Split if overflow: a leaf can hold at most 2*minimumDegree - 1 keys
        if (leaf.Keys.Count >= 2 * _minimumDegree)
        {
            return SplitLeaf(leaf);
        }

        return null;
    }

    private static SplitResult SplitLeaf(LeafNode leaf)
    {
        var mid = leaf.Keys.Count / 2;
        var newLeaf = new LeafNode();

        newLeaf.Keys.AddRange(leaf.Keys.GetRange(mid, leaf.Keys.Count - mid));
        newLeaf.Values.AddRange(leaf.Values.GetRange(mid, leaf.Values.Count - mid));

        leaf.Keys.RemoveRange(mid, leaf.Keys.Count - mid);
        leaf.Values.RemoveRange(mid, leaf.Values.Count - mid);

        // Maintain leaf linked list
        newLeaf.Next = leaf.Next;
        leaf.Next = newLeaf;

        // Promote the first key of the new leaf to the parent
        return new SplitResult(newLeaf.Keys[0], newLeaf);
    }

    private SplitResult? InsertIntoInternal(InternalNode node, TKey key, TValue value)
    {
        var childIndex = 0;
        while (childIndex < node.Keys.Count && KeyOrder<TKey>.Compare(node.Keys[childIndex], key) <= 0)
        {
            childIndex++;
        }

        var result = InsertIntoNode(node.Children[childIndex], key, value);
        if (result == null)
        {
            return null;
        }

        // Insert promoted key and new child into this node
        node.Keys.Insert(childIndex, result.Value.PromotedKey);
        node.Children.Insert(childIndex + 1, result.Value.NewNode);

        // Split if overflow
        if (node.Keys.Count >= 2 * _minimumDegree)
        {
            return SplitInternal(node);
        }

        return null;
    }

    private static SplitResult SplitInternal(InternalNode node)
    {
        var mid = node.Keys.Count / 2;
        var promotedKey = node.Keys[mid];
        var newNode = new InternalNode();

        newNode.Keys.AddRange(node.Keys.GetRange(mid + 1, node.Keys.Count - mid - 1));
        newNode.Children.AddRange(node.Children.GetRange(mid + 1, node.Children.Count - mid - 1));

        node.Keys.RemoveRange(mid, node.Keys.Count - mid);
        node.Children.RemoveRange(mid + 1, node.Children.Count - mid - 1);

        return new SplitResult(promotedKey, newNode);
    }

    private LeafNode FindLeaf(TKey key)
    {
        var node = _root;
        while (!node.IsLeaf)
        {
            var internalNode = (InternalNode)node;
            var childIndex = 0;
            while (childIndex < internalNode.Keys.Count && KeyOrder<TKey>.Compare(internalNode.Keys[childIndex], key) <= 0)
            {
                childIndex++;
            }
            node = internalNode.Children[childIndex];
        }

        return (LeafNode)node;
    }

    private LeafNode GetLeftmostLeaf()
    {
        var node = _root;
        while (!node.IsLeaf)
        {
            node = ((InternalNode)node).Children[0];
        }

        return (LeafNode)node;
    }

}
