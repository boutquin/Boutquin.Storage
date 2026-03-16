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
    where TKey : IComparable<TKey>
{
    private abstract class BPlusTreeNode
    {
        public List<TKey> Keys { get; } = new();
        public bool IsLeaf { get; }

        protected BPlusTreeNode(bool isLeaf)
        {
            IsLeaf = isLeaf;
        }
    }

    private sealed class InternalNode : BPlusTreeNode
    {
        public List<BPlusTreeNode> Children { get; } = new();

        public InternalNode() : base(false) { }
    }

    private sealed class LeafNode : BPlusTreeNode
    {
        public List<TValue> Values { get; } = new();
        public LeafNode? Next { get; set; }

        public LeafNode() : base(true) { }
    }

    private BPlusTreeNode _root;
    private readonly int _order;

    /// <inheritdoc/>
    public int Order => _order;

    /// <inheritdoc/>
    public int Height { get; private set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="BPlusTree{TKey, TValue}"/> class.
    /// </summary>
    /// <param name="order">The minimum degree (order) of the B+ tree. Must be at least 2.</param>
    public BPlusTree(int order)
    {
        if (order < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(order), "Order must be at least 2.");
        }

        _order = order;
        _root = new LeafNode();
        Height = 1;
    }

    /// <inheritdoc/>
    public Task SetAsync(TKey key, TValue value, CancellationToken cancellationToken = default)
    {
        Guard.AgainstNullOrDefault(() => key);

        var result = InsertIntoNode(_root, key, value);
        if (result != null)
        {
            // Root was split — create a new root
            var newRoot = new InternalNode();
            newRoot.Keys.Add(result.Value.PromotedKey);
            newRoot.Children.Add(_root);
            newRoot.Children.Add(result.Value.NewNode);
            _root = newRoot;
            Height++;
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
            if (leaf.Keys[i].CompareTo(key) == 0)
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

        RemoveFromNode(_root, key);
        // If root is an internal node with no keys, promote its only child
        if (!_root.IsLeaf && _root.Keys.Count == 0)
        {
            _root = ((InternalNode)_root).Children[0];
            Height--;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        _root = new LeafNode();
        Height = 1;
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
            await SetAsync(item.Key, item.Value, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public Task<IEnumerable<(TKey Key, TValue Value)>> RangeQueryAsync(TKey start, TKey end, CancellationToken cancellationToken = default)
    {
        Guard.AgainstNullOrDefault(() => start);
        Guard.AgainstNullOrDefault(() => end);

        var items = new List<(TKey Key, TValue Value)>();
        var leaf = FindLeaf(start);

        while (leaf != null)
        {
            for (var i = 0; i < leaf.Keys.Count; i++)
            {
                if (leaf.Keys[i].CompareTo(start) >= 0 && leaf.Keys[i].CompareTo(end) <= 0)
                {
                    items.Add((leaf.Keys[i], leaf.Values[i]));
                }
                else if (leaf.Keys[i].CompareTo(end) > 0)
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
        while (insertIndex < leaf.Keys.Count && leaf.Keys[insertIndex].CompareTo(key) < 0)
        {
            insertIndex++;
        }

        // Update existing key
        if (insertIndex < leaf.Keys.Count && leaf.Keys[insertIndex].CompareTo(key) == 0)
        {
            leaf.Values[insertIndex] = value;
            return null;
        }

        leaf.Keys.Insert(insertIndex, key);
        leaf.Values.Insert(insertIndex, value);

        // Split if overflow: a leaf can hold at most 2*order - 1 keys
        if (leaf.Keys.Count >= 2 * _order)
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
        while (childIndex < node.Keys.Count && node.Keys[childIndex].CompareTo(key) <= 0)
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
        if (node.Keys.Count >= 2 * _order)
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
            while (childIndex < internalNode.Keys.Count && internalNode.Keys[childIndex].CompareTo(key) <= 0)
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

    private static bool RemoveFromNode(BPlusTreeNode node, TKey key)
    {
        if (node.IsLeaf)
        {
            var leaf = (LeafNode)node;
            for (var i = 0; i < leaf.Keys.Count; i++)
            {
                if (leaf.Keys[i].CompareTo(key) == 0)
                {
                    leaf.Keys.RemoveAt(i);
                    leaf.Values.RemoveAt(i);
                    return true;
                }
            }
            return false;
        }

        var internalNode = (InternalNode)node;
        var childIndex = 0;
        while (childIndex < internalNode.Keys.Count && internalNode.Keys[childIndex].CompareTo(key) <= 0)
        {
            childIndex++;
        }

        return RemoveFromNode(internalNode.Children[childIndex], key);
    }
}
