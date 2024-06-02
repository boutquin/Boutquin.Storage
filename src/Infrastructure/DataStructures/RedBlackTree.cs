// Copyright (c) 2024 Pierre G. Boutquin. All rights reserved.
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
/// </remarks>
public sealed class RedBlackTree<TKey, TValue>(int maxSize) : IRedBlackTree<TKey, TValue>
    where TKey : IComparable<TKey>
{
    // Enumeration for node colors used in the red-black tree
    private enum Color { Red, Black }

    // Class representing a node in the red-black tree
    private class Node
    {
        public TKey Key { get; }
        public TValue Value { get; set; }
        public Node Left { get; set; }
        public Node Right { get; set; }
        public Node Parent { get; set; }
        public Color NodeColor { get; set; }

        public Node(TKey key, TValue value, Color nodeColor)
        {
            Key = key;
            Value = value;
            NodeColor = nodeColor;
        }
    }

    private Node _root; // The root node of the red-black tree
    private int _count; // The current number of elements in the tree

    /// <inheritdoc />
    public bool IsFull => _count >= maxSize; // Check if the tree has reached its maximum size

    /// <inheritdoc />
    public async Task SetAsync(TKey key, TValue value, CancellationToken cancellationToken = default)
    {
        AddNode(key, value);
        await Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<(TValue Value, bool Found)> TryGetValueAsync(TKey key, CancellationToken cancellationToken = default)
    {
        var found = TryGetValue(key, out var value);
        return await Task.FromResult((value, found));
    }

    /// <inheritdoc />
    public async Task<bool> ContainsKeyAsync(TKey key, CancellationToken cancellationToken = default)
    {
        var result = FindNode(key) != null;
        return await Task.FromResult(result);
    }

    /// <inheritdoc />
    public async Task RemoveAsync(TKey key, CancellationToken cancellationToken = default)
    {
        // Red-Black tree deletion is not implemented in this example
        // as it requires a complex set of operations.
        throw new NotSupportedException("Remove operation is not supported in an append-only storage engine.");
    }

    /// <inheritdoc />
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        // Clear the tree by setting the root to null and resetting the count.
        _root = null;
        _count = 0;

        await Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<(TKey Key, TValue Value)>> GetAllItemsAsync(CancellationToken cancellationToken = default)
    {
        // Collect all items in the tree using in-order traversal
        // and return them as an async operation.
        var items = new List<KeyValuePair<TKey, TValue>>();
        InOrderTraversal(_root, items);

        return await Task.FromResult(items.Select(i => (i.Key, i.Value)));
    }

    /// <inheritdoc />
    public async Task SetBulkAsync(IEnumerable<KeyValuePair<TKey, TValue>> items, CancellationToken cancellationToken = default)
    {
        // Add each item to the tree. This operation is performed synchronously for each item.
        foreach (var item in items)
        {
            AddNode(item.Key, item.Value);
        }
        await Task.CompletedTask;
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
    private Node InsertOrUpdate(Node root, Node newNode)
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
        while (node != _root && node.Parent.NodeColor == Color.Red)
        {
            if (node.Parent == node.Parent.Parent.Left)
            {
                // Parent is the left child.
                var uncle = node.Parent.Parent.Right;
                if (uncle != null && uncle.NodeColor == Color.Red)
                {
                    // Case 1: Uncle is red, recolor and move up the tree.
                    node.Parent.NodeColor = Color.Black;
                    uncle.NodeColor = Color.Black;
                    node.Parent.Parent.NodeColor = Color.Red;
                    node = node.Parent.Parent;
                }
                else
                {
                    // Case 2: Uncle is black and node is a right child, perform left rotation.
                    if (node == node.Parent.Right)
                    {
                        node = node.Parent;
                        RotateLeft(node);
                    }
                    // Case 3: Uncle is black and node is a left child, perform right rotation.
                    node.Parent.NodeColor = Color.Black;
                    node.Parent.Parent.NodeColor = Color.Red;
                    RotateRight(node.Parent.Parent);
                }
            }
            else
            {
                // Parent is the right child.
                var uncle = node.Parent.Parent.Left;
                if (uncle != null && uncle.NodeColor == Color.Red)
                {
                    // Case 1: Uncle is red, recolor and move up the tree.
                    node.Parent.NodeColor = Color.Black;
                    uncle.NodeColor = Color.Black;
                    node.Parent.Parent.NodeColor = Color.Red;
                    node = node.Parent.Parent;
                }
                else
                {
                    // Case 2: Uncle is black and node is a left child, perform right rotation.
                    if (node == node.Parent.Left)
                    {
                        node = node.Parent;
                        RotateRight(node);
                    }
                    // Case 3: Uncle is black and node is a right child, perform left rotation.
                    node.Parent.NodeColor = Color.Black;
                    node.Parent.Parent.NodeColor = Color.Red;
                    RotateLeft(node.Parent.Parent);
                }
            }
        }
        // Ensure the root is always black to satisfy property 2.
        _root.NodeColor = Color.Black;
    }

    /// <summary>
    /// Performs a left rotation on the specified node.
    /// </summary>
    /// <param name="node">The node to be rotated.</param>
    /// <remarks>
    /// Left rotation is used to maintain the red-black tree properties,
    /// specifically during insertion and deletion operations.
    /// It shifts nodes to the left, balancing the tree by rotating the right child
    /// of the given node to the position of the node itself.
    /// </remarks>
    private void RotateLeft(Node node)
    {
        // Store the right child of the node
        var rightNode = node.Right;

        // Make the left child of the right child the new right child of the node
        node.Right = rightNode.Left;

        // Update the parent reference of the new right child
        if (rightNode.Left != null)
        {
            rightNode.Left.Parent = node;
        }

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
    /// Right rotation is used to maintain the red-black tree properties,
    /// specifically during insertion and deletion operations.
    /// It shifts nodes to the right, balancing the tree by rotating the left child
    /// of the given node to the position of the node itself.
    /// </remarks>
    private void RotateRight(Node node)
    {
        // Store the left child of the node
        var leftNode = node.Left;

        // Make the right child of the left child the new left child of the node
        node.Left = leftNode.Right;

        // Update the parent reference of the new left child
        if (leftNode.Right != null)
        {
            leftNode.Right.Parent = node;
        }

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
    private Node FindNode(TKey key)
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

        value = default;
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
    private void InOrderTraversal(Node node, List<KeyValuePair<TKey, TValue>> items)
    {
        // Perform in-order traversal to retrieve all items in sorted order.
        if (node == null) return;

        InOrderTraversal(node.Left, items);
        items.Add(new KeyValuePair<TKey, TValue>(node.Key, node.Value));
        InOrderTraversal(node.Right, items);
    }

    /// <summary>
    /// Adds a node to the red-black tree.
    /// </summary>
    /// <param name="key">The key of the node to add.</param>
    /// <param name="value">The value of the node to add.</param>
    private void AddNode(TKey key, TValue value)
    {
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