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
/// Implementation of a red-black tree data structure.
/// </summary>
/// <typeparam name="TKey">The type of the keys in the tree.</typeparam>
/// <typeparam name="TValue">The type of the values in the tree.</typeparam>
/// <remarks>
/// <para>A red-black tree is a self-balancing binary search tree that ensures the tree remains balanced, providing O(log n) time complexity for
/// insertion, deletion, and lookup operations. It achieves balance by enforcing properties on the nodes, such as node color (red or black)
/// and constraints on the relationships between parent and child nodes.</para>
///
/// <para><b>Red-Black Tree Properties:</b></para>
/// <para>1. Every node is either red or black.</para>
/// <para>2. The root is always black.</para>
/// <para>3. All leaves (null nodes) are considered black.</para>
/// <para>4. Red nodes cannot have red children (no two red nodes can be adjacent).</para>
/// <para>5. Every path from a given node to its descendant null nodes must have the same number of black nodes.</para>
///
/// <para>
/// <b>Why a red-black tree for the MemTable?</b> In LSM-tree architectures, the MemTable must support
/// O(log n) sorted insertion and in-order traversal for flushing to SSTables. A red-black tree provides
/// these guarantees with lower constant factors than AVL trees (fewer rotations on insert — at most 2
/// vs AVL's up to O(log n)). Since MemTables are write-heavy and only traversed on flush, the red-black
/// tree's cheaper inserts outweigh AVL's slightly faster lookups.
/// </para>
///
/// <para>
/// <b>Why a maximum size (maxSize)?</b> The MemTable has a fixed capacity because it represents the
/// in-memory buffer in an LSM-tree. When full, it must be flushed to an SSTable on disk. Without a size
/// limit, memory would grow unbounded. The size is set by the caller based on available memory and
/// desired flush frequency.
/// </para>
///
/// <para>
/// <b>Thread safety:</b> This class is not thread-safe. External synchronization is required if instances
/// are accessed from multiple threads concurrently. In an LSM-tree, the memtable is typically protected
/// by a lock or used in a single-writer pattern.
/// </para>
///
/// <para>
/// <b>Why new nodes are always Red?</b> Inserting a red node cannot violate the black-height property
/// (Property 5), so the only possible violation is a red-red parent-child (Property 4). This is easier
/// to fix (recolor + at most 2 rotations) than violations of black-height, which would require
/// restructuring entire subtrees.
/// </para>
/// </remarks>
public sealed class RedBlackTree<TKey, TValue>(int maxSize) : IRedBlackTree<TKey, TValue>
    where TKey : IComparable<TKey>
{
    // Enumeration for node colors used in the red-black tree
    private enum Color { Red, Black }

    // Class representing a node in the red-black tree
    private sealed class Node
    {
        public TKey Key { get; }
        public TValue Value { get; set; }
        public Node? Left { get; set; }
        public Node? Right { get; set; }
        public Node? Parent { get; set; }
        public Color NodeColor { get; set; }

        public Node(TKey key, TValue value, Color nodeColor)
        {
            Key = key;
            Value = value;
            NodeColor = nodeColor;
        }
    }

    private Node? _root; // The root node of the red-black tree
    private int _count; // The current number of elements in the tree

    /// <inheritdoc />
    public bool IsFull => _count >= maxSize; // Check if the tree has reached its maximum size

    /// <inheritdoc />
    public async Task SetAsync(TKey key, TValue value, CancellationToken cancellationToken = default)
    {
        // Why synchronous operations wrapped in Task? The IRedBlackTree interface is async to support
        // implementations backed by I/O (e.g., on-disk B-trees). This in-memory implementation completes
        // synchronously, so we wrap results in completed tasks to satisfy the interface contract without
        // incurring async state machine overhead.
        AddNode(key, value);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<(TValue Value, bool Found)> TryGetValueAsync(TKey key, CancellationToken cancellationToken = default)
    {
        // Why synchronous operations wrapped in Task? The IRedBlackTree interface is async to support
        // implementations backed by I/O (e.g., on-disk B-trees). This in-memory implementation completes
        // synchronously, so we wrap results in completed tasks to satisfy the interface contract without
        // incurring async state machine overhead.
        var found = TryGetValue(key, out var value);
        return await Task.FromResult((value, found)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> ContainsKeyAsync(TKey key, CancellationToken cancellationToken = default)
    {
        // Why synchronous operations wrapped in Task? The IRedBlackTree interface is async to support
        // implementations backed by I/O (e.g., on-disk B-trees). This in-memory implementation completes
        // synchronously, so we wrap results in completed tasks to satisfy the interface contract without
        // incurring async state machine overhead.
        var result = FindNode(key) != null;
        return await Task.FromResult(result).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RemoveAsync(TKey key, CancellationToken cancellationToken = default)
    {
        // Why not implemented? In an LSM-tree MemTable, deletes are handled by writing tombstone markers
        // (a special "deleted" value), not by removing nodes. The MemTable is append-only — it captures the
        // intent to delete, which is resolved during compaction when SSTables are merged. True node removal
        // would require red-black deletion (6 cases, O(log n) restructuring) for no architectural benefit.
        throw new NotSupportedException("Remove operation is not supported in an append-only storage engine.");
    }

    /// <inheritdoc />
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        // Why synchronous operations wrapped in Task? The IRedBlackTree interface is async to support
        // implementations backed by I/O (e.g., on-disk B-trees). This in-memory implementation completes
        // synchronously, so we wrap results in completed tasks to satisfy the interface contract without
        // incurring async state machine overhead.

        // Clear the tree by setting the root to null and resetting the count.
        _root = null;
        _count = 0;

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<(TKey Key, TValue Value)>> GetAllItemsAsync(CancellationToken cancellationToken = default)
    {
        // Why synchronous operations wrapped in Task? The IRedBlackTree interface is async to support
        // implementations backed by I/O (e.g., on-disk B-trees). This in-memory implementation completes
        // synchronously, so we wrap results in completed tasks to satisfy the interface contract without
        // incurring async state machine overhead.

        // Collect all items in the tree using in-order traversal
        // and return them as an async operation.
        var items = new List<KeyValuePair<TKey, TValue>>();
        InOrderTraversal(_root, items);

        return await Task.FromResult(items.Select(i => (i.Key, i.Value))).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SetBulkAsync(IEnumerable<KeyValuePair<TKey, TValue>> items, CancellationToken cancellationToken = default)
    {
        // Why synchronous operations wrapped in Task? The IRedBlackTree interface is async to support
        // implementations backed by I/O (e.g., on-disk B-trees). This in-memory implementation completes
        // synchronously, so we wrap results in completed tasks to satisfy the interface contract without
        // incurring async state machine overhead.

        // Add each item to the tree. This operation is performed synchronously for each item.
        foreach (var item in items)
        {
            AddNode(item.Key, item.Value);
        }
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>
    /// Inserts a new node into the tree or updates an existing node with the same key.
    /// </summary>
    /// <param name="root">The root node to start the insertion.</param>
    /// <param name="newNode">The new node to be inserted.</param>
    /// <returns>The existing node if the key already exists, otherwise null.</returns>
    /// <remarks>
    /// This method ensures that the binary search tree properties are maintained.
    /// The key comparisons guide the placement of the new node.
    /// If a node with the same key already exists, its value is updated.
    /// </remarks>
    private static Node? InsertOrUpdate(Node root, Node newNode)
    {
        while (true)
        {
            var comparison = newNode.Key.CompareTo(root.Key);
            if (comparison < 0)
            {
                if (root.Left == null)
                {
                    // Insert new node as the left child
                    root.Left = newNode;
                    newNode.Parent = root;
                    return null; // New node inserted
                }
                // Move to the left child
                root = root.Left;
            }
            else if (comparison > 0)
            {
                if (root.Right == null)
                {
                    // Insert new node as the right child
                    root.Right = newNode;
                    newNode.Parent = root;
                    return null; // New node inserted
                }
                // Move to the right child
                root = root.Right;
            }
            else
            {
                // Node with the same key exists, update its value
                root.Value = newNode.Value;
                return root; // Existing node updated
            }
        }
    }

    /// <summary>
    /// Fixes the red-black tree properties after an insertion.
    /// </summary>
    /// <param name="node">The node that was inserted.</param>
    /// <remarks>
    /// This method ensures that the red-black tree properties are restored
    /// after inserting a new node. It involves re-coloring and performing
    /// rotations to maintain balance.
    /// </remarks>
    private void FixAfterInsertion(Node node)
    {
        // Fix the tree by performing rotations and recoloring nodes to restore red-black properties.
        while (node != _root && node.Parent!.NodeColor == Color.Red)
        {
            if (node.Parent == node.Parent.Parent!.Left)
            {
                // Parent is the left child.
                var uncle = node.Parent.Parent.Right;
                if (uncle != null && uncle.NodeColor == Color.Red)
                {
                    // Why recolor? When uncle is red, we can fix the red-red violation without
                    // rotations by pushing the "extra" red up to the grandparent. This may create
                    // a new violation higher up, so we loop.

                    // Case 1: Uncle is red, recolor and move up the tree.
                    node.Parent.NodeColor = Color.Black;
                    uncle.NodeColor = Color.Black;
                    node.Parent.Parent.NodeColor = Color.Red;
                    node = node.Parent.Parent;
                }
                else
                {
                    // Why left-rotate first? The node is a right child of a left child, forming a
                    // "zig-zag" shape. Rotating converts it to a "zig-zig" (Case 3) that a single
                    // right rotation can fix.

                    // Case 2: Uncle is black and node is a right child, perform left rotation.
                    if (node == node.Parent.Right)
                    {
                        node = node.Parent;
                        RotateLeft(node);
                    }

                    // Why right-rotate and recolor? The "zig-zig" shape (node and parent are both
                    // left children) is resolved by rotating the grandparent right and swapping
                    // colors between parent and grandparent. This restores all 5 red-black
                    // properties in the subtree.

                    // Case 3: Uncle is black and node is a left child, perform right rotation.
                    node.Parent!.NodeColor = Color.Black;
                    node.Parent.Parent!.NodeColor = Color.Red;
                    RotateRight(node.Parent.Parent);
                }
            }
            else
            {
                // Parent is the right child (mirror cases).
                var uncle = node.Parent.Parent.Left;
                if (uncle != null && uncle.NodeColor == Color.Red)
                {
                    // Why recolor? When uncle is red, we can fix the red-red violation without
                    // rotations by pushing the "extra" red up to the grandparent. This may create
                    // a new violation higher up, so we loop.

                    // Case 1: Uncle is red, recolor and move up the tree.
                    node.Parent.NodeColor = Color.Black;
                    uncle.NodeColor = Color.Black;
                    node.Parent.Parent.NodeColor = Color.Red;
                    node = node.Parent.Parent;
                }
                else
                {
                    // Why right-rotate first? Mirror of the left case: the node is a left child of
                    // a right child, forming a "zig-zag" shape. Rotating converts it to a "zig-zig"
                    // (Case 3) that a single left rotation can fix.

                    // Case 2: Uncle is black and node is a left child, perform right rotation.
                    if (node == node.Parent.Left)
                    {
                        node = node.Parent;
                        RotateRight(node);
                    }

                    // Why left-rotate and recolor? Mirror of the left case: the "zig-zig" shape
                    // (node and parent are both right children) is resolved by rotating the
                    // grandparent left and swapping colors. This restores all 5 red-black
                    // properties in the subtree.

                    // Case 3: Uncle is black and node is a right child, perform left rotation.
                    node.Parent!.NodeColor = Color.Black;
                    node.Parent.Parent!.NodeColor = Color.Red;
                    RotateLeft(node.Parent.Parent);
                }
            }
        }
        // Ensure the root is always black to satisfy property 2.
        _root!.NodeColor = Color.Black;
    }

    /// <summary>
    /// Performs a left rotation on the specified node.
    /// </summary>
    /// <param name="node">The node to be rotated.</param>
    /// <remarks>
    /// <para>Left rotation is used to maintain the red-black tree properties,
    /// specifically during insertion and deletion operations.
    /// It shifts nodes to the left, balancing the tree by rotating the right child
    /// of the given node to the position of the node itself.</para>
    ///
    /// <para>
    /// <b>Why rotations?</b> Rotations are the fundamental rebalancing operation in red-black trees.
    /// They change the tree's structure without violating the binary search tree ordering
    /// (left &lt; parent &lt; right). A rotation takes O(1) time and adjusts at most 3 parent-child
    /// links, making it the cheapest way to restore balance after an insertion.
    /// </para>
    /// </remarks>
    private void RotateLeft(Node node)
    {
        // Store the right child of the node
        var rightNode = node.Right!;

        // Make the left child of the right child the new right child of the node
        node.Right = rightNode.Left;

        // Update the parent reference of the new right child
        rightNode.Left?.Parent = node;

        // Update the parent reference of the right node
        rightNode.Parent = node.Parent;

        // If the node is the root, update the root reference
        if (node.Parent == null)
        {
            _root = rightNode;
        }
        else if (node == node.Parent.Left)
        {
            // If the node is the left child, update the left child reference of the parent
            node.Parent.Left = rightNode;
        }
        else
        {
            // If the node is the right child, update the right child reference of the parent
            node.Parent.Right = rightNode;
        }

        // Make the node the left child of the right node
        rightNode.Left = node;

        // Update the parent reference of the node
        node.Parent = rightNode;
    }

    /// <summary>
    /// Performs a right rotation on the specified node.
    /// </summary>
    /// <param name="node">The node to be rotated.</param>
    /// <remarks>
    /// <para>Right rotation is used to maintain the red-black tree properties,
    /// specifically during insertion and deletion operations.
    /// It shifts nodes to the right, balancing the tree by rotating the left child
    /// of the given node to the position of the node itself.</para>
    ///
    /// <para>
    /// <b>Why rotations?</b> Rotations are the fundamental rebalancing operation in red-black trees.
    /// They change the tree's structure without violating the binary search tree ordering
    /// (left &lt; parent &lt; right). A rotation takes O(1) time and adjusts at most 3 parent-child
    /// links, making it the cheapest way to restore balance after an insertion.
    /// </para>
    /// </remarks>
    private void RotateRight(Node node)
    {
        // Store the left child of the node
        var leftNode = node.Left!;

        // Make the right child of the left child the new left child of the node
        node.Left = leftNode.Right;

        // Update the parent reference of the new left child
        leftNode.Right?.Parent = node;

        // Update the parent reference of the left node
        leftNode.Parent = node.Parent;

        // If the node is the root, update the root reference
        if (node.Parent == null)
        {
            _root = leftNode;
        }
        else if (node == node.Parent.Right)
        {
            // If the node is the right child, update the right child reference of the parent
            node.Parent.Right = leftNode;
        }
        else
        {
            // If the node is the left child, update the left child reference of the parent
            node.Parent.Left = leftNode;
        }

        // Make the node the right child of the left node
        leftNode.Right = node;

        // Update the parent reference of the node
        node.Parent = leftNode;
    }

    /// <summary>
    /// Finds the node with the specified key.
    /// </summary>
    /// <param name="key">The key to find.</param>
    /// <returns>The node with the specified key, or null if not found.</returns>
    /// <remarks>
    /// This method performs a binary search in the tree to locate the node
    /// with the specified key, adhering to the binary search tree properties.
    /// </remarks>
    private Node? FindNode(TKey key)
    {
        // Start from the root node and traverse the tree
        var current = _root;

        // Loop until the node is found or we reach the end of the tree
        while (current != null)
        {
            // Compare the key with the current node's key
            var comparison = key.CompareTo(current.Key);

            if (comparison < 0)
            {
                // If the key is less than the current node's key, move to the left child
                current = current.Left;
            }
            else if (comparison > 0)
            {
                // If the key is greater than the current node's key, move to the right child
                current = current.Right;
            }
            else
            {
                // If the keys are equal, the node is found
                return current;
            }
        }

        // If the loop exits, the key is not in the tree
        return null;
    }

    /// <summary>
    /// Attempts to retrieve the value associated with the specified key.
    /// </summary>
    /// <param name="key">The key to retrieve.</param>
    /// <param name="value">The value associated with the key if found.</param>
    /// <returns>True if the key is found, otherwise false.</returns>
    private bool TryGetValue(TKey key, out TValue value)
    {
        // Find the node with the specified key.
        var node = FindNode(key);
        if (node != null)
        {
            value = node.Value;
            return true;
        }

        value = default!;
        return false;
    }

    /// <summary>
    /// Performs an in-order traversal of the tree to collect all items.
    /// </summary>
    /// <param name="node">The starting node for traversal.</param>
    /// <param name="items">The list to collect the key-value pairs.</param>
    /// <remarks>
    /// This method uses in-order traversal to visit nodes in ascending order
    /// of their keys, ensuring that the collected items are sorted.
    /// </remarks>
    private static void InOrderTraversal(Node? root, List<KeyValuePair<TKey, TValue>> items)
    {
        // Why iterative instead of recursive? Recursive in-order traversal uses O(h) stack frames
        // where h is tree height. For a red-black tree with maxSize elements, h ≤ 2*log2(n+1).
        // At 10M elements h ≈ 47 — safe for recursion. But defensive coding avoids StackOverflowException
        // risk entirely, and an explicit stack has the same O(h) memory cost without the call overhead.
        var stack = new Stack<Node>();
        var current = root;

        while (current != null || stack.Count > 0)
        {
            while (current != null)
            {
                stack.Push(current);
                current = current.Left;
            }

            current = stack.Pop();
            items.Add(new KeyValuePair<TKey, TValue>(current.Key, current.Value));
            current = current.Right;
        }
    }

    /// <summary>
    /// Adds a node to the red-black tree.
    /// </summary>
    /// <param name="key">The key of the node to add.</param>
    /// <param name="value">The value of the node to add.</param>
    private void AddNode(TKey key, TValue value)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (IsFull)
        {
            throw new InvalidOperationException("The red-black tree is full.");
        }

        // Create a new node with the given key and value. New nodes are initially red.
        var newNode = new Node(key, value, Color.Red);

        // If the tree is empty, set the new node as the root and color it black.
        if (_root == null)
        {
            _root = newNode;
            _root.NodeColor = Color.Black;
            _count++;
        }
        else
        {
            // Otherwise, insert the new node into the tree and fix any violations of the red-black properties.
            var existingNode = InsertOrUpdate(_root, newNode);
            if (existingNode == null)
            {
                FixAfterInsertion(newNode);
                _count++;
            }
        }
    }
}
