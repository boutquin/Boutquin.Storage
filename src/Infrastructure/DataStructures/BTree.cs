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

using Boutquin.Storage.Domain.Interfaces;

/// <summary>
/// In-memory implementation of a B-tree data structure.
/// </summary>
/// <typeparam name="TKey">The type of the keys in the tree.</typeparam>
/// <typeparam name="TValue">The type of the values in the tree.</typeparam>
/// <remarks>
/// <para>A B-tree of minimum degree <c>t</c> (also called order) has the following properties:</para>
/// <para>- Every node has at most <c>2t - 1</c> keys and <c>2t</c> children.</para>
/// <para>- Every non-root node has at least <c>t - 1</c> keys.</para>
/// <para>- The root has at least 1 key (when non-empty).</para>
/// <para>- All leaves are at the same depth.</para>
///
/// <para><b>Why wrap synchronous operations in Tasks?</b> The <see cref="IBTree{TKey, TValue}"/> interface
/// extends <see cref="IBulkKeyValueStore{TKey, TValue}"/> which is async to support implementations backed
/// by I/O (e.g., on-disk B-trees). This in-memory implementation completes synchronously, so we wrap
/// results in completed tasks to satisfy the interface contract without incurring async state machine overhead.</para>
///
/// <para><b>Insertion strategy:</b> This implementation uses proactive splitting (split-on-the-way-down).
/// When traversing from root to leaf during insertion, any full node encountered is split before descending
/// into it. This guarantees that when we reach the leaf, it has room for the new key, requiring only a
/// single pass down the tree. This is the approach described in CLRS (Introduction to Algorithms).</para>
///
/// <para><b>Deletion:</b> Not supported in this implementation (throws <see cref="NotSupportedException"/>),
/// matching the pattern used by <see cref="RedBlackTree{TKey, TValue}"/> for append-only storage engines.
/// B-tree deletion is complex (6+ cases with key redistribution and node merging) and not needed when
/// deletes are handled via tombstone markers in an LSM-tree architecture.</para>
///
/// <para><b>Thread safety:</b> This class is not thread-safe. External synchronization is required for concurrent access.</para>
/// </remarks>
public sealed class BTree<TKey, TValue> : IBTree<TKey, TValue>
    where TKey : IComparable<TKey>
{
    /// <summary>
    /// Represents a node in the B-tree. Each node contains a list of keys and values,
    /// and (for internal nodes) a list of child node references.
    /// </summary>
    private sealed class Node
    {
        /// <summary>The keys stored in this node, maintained in sorted order.</summary>
        public List<TKey> Keys { get; } = [];

        /// <summary>The values corresponding to each key, in the same positional order.</summary>
        public List<TValue> Values { get; } = [];

        /// <summary>
        /// Child node references. An internal node with <c>k</c> keys has <c>k + 1</c> children.
        /// Leaf nodes have an empty children list.
        /// </summary>
        public List<Node> Children { get; } = [];

        /// <summary>Whether this node is a leaf (has no children).</summary>
        public bool IsLeaf => Children.Count == 0;
    }

    private readonly int _minimumDegree; // The minimum degree t of the B-tree
    private Node? _root; // The root node of the B-tree
    private int _height; // The current height of the tree

    /// <summary>
    /// Initializes a new instance of the <see cref="BTree{TKey, TValue}"/> class.
    /// </summary>
    /// <param name="minimumDegree">
    /// The minimum degree (order) of the B-tree. Must be at least 2.
    /// Each node can hold between <c>t - 1</c> and <c>2t - 1</c> keys.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="minimumDegree"/> is less than 2.
    /// </exception>
    public BTree(int minimumDegree)
    {
        if (minimumDegree < 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minimumDegree),
                minimumDegree,
                "Minimum degree must be at least 2.");
        }

        _minimumDegree = minimumDegree;
    }

    /// <inheritdoc />
    public int Order => _minimumDegree;

    /// <inheritdoc />
    public int Height => _height;

    /// <inheritdoc />
    public async Task SetAsync(TKey key, TValue value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Guard.AgainstNullOrDefault(() => key);
        Guard.AgainstNullOrDefault(() => value);
        Insert(key, value);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<(TValue Value, bool Found)> TryGetValueAsync(TKey key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Guard.AgainstNullOrDefault(() => key);
        var result = Search(key);
        return await Task.FromResult(result).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> ContainsKeyAsync(TKey key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Guard.AgainstNullOrDefault(() => key);
        var (_, found) = Search(key);
        return await Task.FromResult(found).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RemoveAsync(TKey key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Guard.AgainstNullOrDefault(() => key);
        // Why not implemented? In an LSM-tree architecture, deletes are handled by writing tombstone
        // markers, not by removing keys from the tree. B-tree deletion requires complex key redistribution
        // and node merging (6+ cases) for no architectural benefit in this context.
        throw new NotSupportedException("Remove operation is not supported in an append-only storage engine.");
    }

    /// <inheritdoc />
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _root = null;
        _height = 0;
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<(TKey Key, TValue Value)>> GetAllItemsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var items = new List<(TKey Key, TValue Value)>();
        if (_root != null)
        {
            InOrderTraversal(_root, items);
        }

        return await Task.FromResult<IEnumerable<(TKey Key, TValue Value)>>(items).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SetBulkAsync(IEnumerable<KeyValuePair<TKey, TValue>> items, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Guard.AgainstNullOrDefault(() => items);
        foreach (var item in items)
        {
            Insert(item.Key, item.Value);
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>
    /// Searches for a key in the B-tree.
    /// </summary>
    /// <param name="key">The key to search for.</param>
    /// <returns>A tuple containing the value and a boolean indicating whether the key was found.</returns>
    private (TValue Value, bool Found) Search(TKey key)
    {
        if (_root == null)
        {
            return (default!, false);
        }

        return SearchNode(_root, key);
    }

    /// <summary>
    /// Iteratively searches for a key starting from the given node.
    /// </summary>
    /// <param name="node">The node to start searching from.</param>
    /// <param name="key">The key to search for.</param>
    /// <returns>A tuple containing the value and a boolean indicating whether the key was found.</returns>
    /// <remarks>
    /// Uses iterative descent rather than recursion to avoid stack overflow on deep trees,
    /// matching the defensive approach used in <see cref="RedBlackTree{TKey, TValue}"/>.
    /// </remarks>
    private static (TValue Value, bool Found) SearchNode(Node node, TKey key)
    {
        var current = node;

        while (true)
        {
            // Find the first key greater than or equal to the search key
            var i = 0;
            while (i < current.Keys.Count && key.CompareTo(current.Keys[i]) > 0)
            {
                i++;
            }

            // If we found the exact key, return the associated value
            if (i < current.Keys.Count && key.CompareTo(current.Keys[i]) == 0)
            {
                return (current.Values[i], true);
            }

            // If this is a leaf node, the key doesn't exist
            if (current.IsLeaf)
            {
                return (default!, false);
            }

            // Descend into the appropriate child
            current = current.Children[i];
        }
    }

    /// <summary>
    /// Inserts a key-value pair into the B-tree, or updates the value if the key already exists.
    /// </summary>
    /// <param name="key">The key to insert.</param>
    /// <param name="value">The value to associate with the key.</param>
    /// <remarks>
    /// <para>Uses proactive splitting (split-on-the-way-down): when traversing from root to leaf,
    /// any full node encountered is split before descending into it. This guarantees the leaf
    /// has room for insertion and requires only a single downward pass.</para>
    ///
    /// <para>If the root is full, it is split first, creating a new root and increasing the tree height by 1.
    /// This is the only operation that increases the height of a B-tree.</para>
    /// </remarks>
    private void Insert(TKey key, TValue value)
    {
        // If the tree is empty, create a new root
        if (_root == null)
        {
            _root = new Node();
            _root.Keys.Add(key);
            _root.Values.Add(value);
            _height = 1;
            return;
        }

        // If root is full, split it before descending
        if (_root.Keys.Count == 2 * _minimumDegree - 1)
        {
            var newRoot = new Node();
            newRoot.Children.Add(_root);
            SplitChild(newRoot, 0);
            _root = newRoot;
            _height++;
        }

        InsertNonFull(_root, key, value);
    }

    /// <summary>
    /// Inserts a key-value pair into a node that is guaranteed to be non-full.
    /// </summary>
    /// <param name="node">The non-full node to insert into.</param>
    /// <param name="key">The key to insert.</param>
    /// <param name="value">The value to associate with the key.</param>
    /// <remarks>
    /// If the node is a leaf, the key is inserted directly. If the node is internal,
    /// the appropriate child is found; if that child is full, it is split first, then
    /// the descent continues into the (now non-full) correct child.
    /// </remarks>
    private void InsertNonFull(Node node, TKey key, TValue value)
    {
        var current = node;

        while (true)
        {
            var i = current.Keys.Count - 1;

            if (current.IsLeaf)
            {
                // Check for duplicate key in this leaf
                var insertPos = 0;
                while (insertPos < current.Keys.Count && key.CompareTo(current.Keys[insertPos]) > 0)
                {
                    insertPos++;
                }

                // If key already exists, update the value
                if (insertPos < current.Keys.Count && key.CompareTo(current.Keys[insertPos]) == 0)
                {
                    current.Values[insertPos] = value;
                    return;
                }

                // Insert the key-value pair at the correct position to maintain sorted order
                current.Keys.Insert(insertPos, key);
                current.Values.Insert(insertPos, value);
                return;
            }

            // Find the child that will receive the new key
            while (i >= 0 && key.CompareTo(current.Keys[i]) < 0)
            {
                i--;
            }

            // Check for duplicate key in this internal node
            if (i >= 0 && key.CompareTo(current.Keys[i]) == 0)
            {
                current.Values[i] = value;
                return;
            }

            i++;

            // If the child is full, split it before descending
            if (current.Children[i].Keys.Count == 2 * _minimumDegree - 1)
            {
                SplitChild(current, i);

                // After splitting, determine which of the two children to descend into
                if (key.CompareTo(current.Keys[i]) == 0)
                {
                    // The median key from the split matches our key — update the value
                    current.Values[i] = value;
                    return;
                }

                if (key.CompareTo(current.Keys[i]) > 0)
                {
                    i++;
                }
            }

            current = current.Children[i];
        }
    }

    /// <summary>
    /// Splits a full child node into two nodes, promoting the median key to the parent.
    /// </summary>
    /// <param name="parent">The parent node containing the full child.</param>
    /// <param name="childIndex">The index of the full child in the parent's children list.</param>
    /// <remarks>
    /// <para>The full child has <c>2t - 1</c> keys. After splitting:</para>
    /// <para>- The first <c>t - 1</c> keys remain in the original child.</para>
    /// <para>- The median key (at index <c>t - 1</c>) is promoted to the parent.</para>
    /// <para>- The last <c>t - 1</c> keys move to a new sibling node.</para>
    /// <para>- If the child is internal, its children are also distributed between the two nodes.</para>
    /// </remarks>
    private void SplitChild(Node parent, int childIndex)
    {
        var fullChild = parent.Children[childIndex];
        var newChild = new Node();
        var t = _minimumDegree;

        // Copy the last (t - 1) keys and values to the new child
        for (var j = 0; j < t - 1; j++)
        {
            newChild.Keys.Add(fullChild.Keys[t + j]);
            newChild.Values.Add(fullChild.Values[t + j]);
        }

        // If the full child is not a leaf, copy the last t children to the new child
        if (!fullChild.IsLeaf)
        {
            for (var j = 0; j < t; j++)
            {
                newChild.Children.Add(fullChild.Children[t + j]);
            }
        }

        // The median key and value to promote
        var medianKey = fullChild.Keys[t - 1];
        var medianValue = fullChild.Values[t - 1];

        // Remove the keys, values, and children that were moved or promoted
        // Remove from the end to avoid index shifting issues
        if (!fullChild.IsLeaf)
        {
            fullChild.Children.RemoveRange(t, t);
        }

        fullChild.Keys.RemoveRange(t - 1, t);
        fullChild.Values.RemoveRange(t - 1, t);

        // Insert the new child into the parent's children list
        parent.Children.Insert(childIndex + 1, newChild);

        // Promote the median key and value to the parent
        parent.Keys.Insert(childIndex, medianKey);
        parent.Values.Insert(childIndex, medianValue);
    }

    /// <summary>
    /// Performs a recursive in-order traversal of the B-tree to collect all key-value pairs in sorted order.
    /// </summary>
    /// <param name="node">The current node to traverse.</param>
    /// <param name="items">The list to collect the key-value pairs into.</param>
    /// <remarks>
    /// <para>For a B-tree node with keys [k0, k1, ..., kn-1] and children [c0, c1, ..., cn],
    /// the in-order traversal visits: c0, k0, c1, k1, ..., cn-1, kn-1, cn.</para>
    ///
    /// <para>B-tree depth is O(log_t n). With minimum degree t = 2, a tree with 1 billion elements
    /// has at most ~30 levels. Stack overflow from recursion is not a practical concern for B-trees,
    /// unlike binary search trees where sequential insertion can produce O(n) depth. The logarithmic
    /// depth guarantee makes recursion both safe and clearer than an iterative approach.</para>
    /// </remarks>
    private static void InOrderTraversal(Node node, List<(TKey Key, TValue Value)> items)
    {
        for (var i = 0; i < node.Keys.Count; i++)
        {
            // Visit the i-th child before the i-th key
            if (!node.IsLeaf)
            {
                InOrderTraversal(node.Children[i], items);
            }

            items.Add((node.Keys[i], node.Values[i]));
        }

        // Visit the last child (rightmost)
        if (!node.IsLeaf)
        {
            InOrderTraversal(node.Children[node.Keys.Count], items);
        }
    }
}
