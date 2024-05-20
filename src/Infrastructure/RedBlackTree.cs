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
//  See the License for the specific language governing permissions and
//  limitations under the License.
//
namespace Boutquin.Storage.Infrastructure;

/// <summary>
/// Implementation of a red-black tree data structure.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="RedBlackTree{TKey, TValue}"/> class.
/// </remarks>
/// <param name="maxSize">The maximum size of the red-black tree.</param>
/// <typeparam name="TKey">The type of the keys in the tree.</typeparam>
/// <typeparam name="TValue">The type of the values in the tree.</typeparam>
/// <remarks>
/// <b>Theory of Red-Black Tree:</b>
/// A red-black tree is a self-balancing binary search tree that ensures the tree remains balanced, providing O(log n) time complexity for 
/// insertion, deletion, and lookup operations. It achieves balance by enforcing properties on the nodes, such as node color (red or black) 
/// and constraints on the relationships between parent and child nodes. The key properties include:
/// - Each node is either red or black.
/// - The root is always black.
/// - Red nodes cannot have red children (no two red nodes can be adjacent).
/// - Every path from a node to its descendant null nodes must have the same number of black nodes.
/// 
/// <b>Pros and Cons in the Context of an LSM-tree:</b>
/// 
/// Pros:
/// - **Efficient Operations**: The self-balancing nature ensures O(log n) complexity for insertions, deletions, and lookups, making it efficient 
///   for handling dynamic data.
/// - **Balanced Structure**: Maintains a balanced tree, preventing the performance degradation that can occur with unbalanced trees.
/// - **Sorted Order**: Keeps key-value pairs in sorted order, which is essential for the efficient creation of SSTables during the flush process.
/// 
/// Cons:
/// - **Complexity**: Red-black trees are more complex to implement and manage compared to simpler data structures like hash tables or linked lists.
/// - **Overhead**: The balancing operations (rotations and color changes) introduce some overhead during insertions and deletions, although this 
///   is generally mitigated by the balanced structure.
/// 
/// In the context of an LSM-tree, the red-black tree is particularly effective for optimizing write operations. By maintaining an efficient, 
/// balanced, in-memory structure, it allows for quick write operations and provides an ordered set of data that can be efficiently flushed 
/// to disk as a sorted SSTable, improving the overall performance of the LSM-tree.
/// </remarks>
public class RedBlackTree<TKey, TValue>(int maxSize) : IRedBlackTree<TKey, TValue> 
    where TKey : IComparable<TKey>
{
    // Enumeration for node colors used in the red-black tree
    private enum Color { Red, Black }

    // Class representing a node in the red-black tree
    private class Node(TKey key, TValue value, Color nodeColor)
    {
        public TKey Key { get; } = key;               // The key associated with this node
        public TValue Value { get; set; } = value;           // The value associated with this node
        public Node Left { get; set; }
        public Node Right { get; set; }
        public Node Parent { get; set; }
        public Color NodeColor { get; set; } = nodeColor;   // The color of this node (Red or Black)
    }

    private Node _root;       // The root node of the red-black tree
    private int _count;       // The current number of elements in the tree

    /// <inheritdoc />
    public bool IsFull => _count >= maxSize; // Check if the tree has reached its maximum size

    /// <inheritdoc />
    public void Add(TKey key, TValue value)
    {
        // Check if the tree is full before adding a new node.
        if (IsFull)
        {
            throw new InvalidOperationException("The red-black tree is full.");
        }

        // Create a new node with the given key and value.
        var newNode = new Node(key, value, Color.Red);

        // If the tree is empty, set the root to the new node and color it black.
        if (_root == null)
        {
            _root = newNode;
            _root.NodeColor = Color.Black; // The root node must always be black
            _count++;
        }
        else
        {
            // Otherwise, insert the new node into the tree.
            var existingNode = InsertOrUpdate(_root, newNode);
            if (existingNode == null) // new node added
            {
                // Fix any red-black tree property violations after insertion.
                FixAfterInsertion(newNode);
                _count++;
            }
        }
    }

    /// <inheritdoc />
    public bool TryGetValue(TKey key, out TValue value)
    {
        // Find the node with the specified key.
        var node = FindNode(key);
        if (node is not null)
        {
            value = node.Value; // Return the value associated with the key
            return true; // The key was found
        }
        value = default;
        return false; // The key was not found
    }

    /// <inheritdoc />
    public void Clear()
    {
        _root = null; // Reset the root to null
        _count = 0; // Reset the count to 0
    }

    /// <inheritdoc />
    public IEnumerable<KeyValuePair<TKey, TValue>> GetAllItems()
    {
        // Collect all items in the tree using in-order traversal.
        var items = new List<KeyValuePair<TKey, TValue>>();
        InOrderTraversal(_root, items);
        return items;
    }

    /// <summary>
    /// Inserts a new node into the tree or updates an existing node with the same key.
    /// </summary>
    /// <param name="root">The root node to start the insertion.</param>
    /// <param name="newNode">The new node to be inserted.</param>
    /// <returns>The existing node if the key already exists, otherwise null.</returns>
    private Node InsertOrUpdate(Node root, Node newNode)
    {
        while (true)
        {
            var comparison = newNode.Key.CompareTo(root.Key);
            switch (comparison)
            {
                case < 0 when root.Left == null:
                    // Insert as the left child.
                    root.Left = newNode;
                    newNode.Parent = root;
                    return null; // New node was inserted
                case < 0:
                    root = root.Left; // Move to the left child
                    break;
                case > 0 when root.Right == null:
                    // Insert as the right child.
                    root.Right = newNode;
                    newNode.Parent = root;
                    return null; // New node was inserted
                case > 0:
                    root = root.Right; // Move to the right child
                    break;
                default:
                    // Key already exists, update the value.
                    root.Value = newNode.Value;
                    return root; // Existing node was updated
            }
        }
    }

    /// <summary>
    /// Fixes the red-black tree properties after an insertion.
    /// </summary>
    /// <param name="node">The node that was inserted.</param>
    private void FixAfterInsertion(Node node)
    {
        // Fix the tree by performing rotations and recoloring nodes.
        while (node != _root && node.Parent.NodeColor == Color.Red)
        {
            if (node.Parent == node.Parent.Parent.Left)
            {
                // Parent is the left child.
                var uncle = node.Parent.Parent.Right;
                if (uncle is not null && uncle.NodeColor == Color.Red)
                {
                    // Case 1 & 2: uncle is red.
                    node.Parent.NodeColor = Color.Black;
                    uncle.NodeColor = Color.Black;
                    node.Parent.Parent.NodeColor = Color.Red;
                    node = node.Parent.Parent; // Move up to the grandparent
                }
                else
                {
                    // Case 3 & 4: uncle is black.
                    if (node == node.Parent.Right)
                    {
                        // Case 3: node is right child.
                        node = node.Parent;
                        RotateLeft(node); // Perform left rotation
                    }
                    // Case 4: node is left child.
                    node.Parent.NodeColor = Color.Black;
                    node.Parent.Parent.NodeColor = Color.Red;
                    RotateRight(node.Parent.Parent); // Perform right rotation
                }
            }
            else
            {
                // Parent is the right child.
                var uncle = node.Parent.Parent.Left;
                if (uncle is not null && uncle.NodeColor == Color.Red)
                {
                    // Case 1 & 2: uncle is red.
                    node.Parent.NodeColor = Color.Black;
                    uncle.NodeColor = Color.Black;
                    node.Parent.Parent.NodeColor = Color.Red;
                    node = node.Parent.Parent; // Move up to the grandparent
                }
                else
                {
                    // Case 3 & 4: uncle is black.
                    if (node == node.Parent.Left)
                    {
                        // Case 3: node is left child.
                        node = node.Parent;
                        RotateRight(node); // Perform right rotation
                    }
                    // Case 4: node is right child.
                    node.Parent.NodeColor = Color.Black;
                    node.Parent.Parent.NodeColor = Color.Red;
                    RotateLeft(node.Parent.Parent); // Perform left rotation
                }
            }
        }
        // Ensure the root is always black.
        _root.NodeColor = Color.Black;
    }

    /// <summary>
    /// Performs a left rotation on the specified node.
    /// </summary>
    /// <param name="node">The node to be rotated.</param>
    private void RotateLeft(Node node)
    {
        // Rotate the node to the left.
        var rightNode = node.Right;
        node.Right = rightNode.Left;
        if (rightNode.Left is not null)
        {
            rightNode.Left.Parent = node;
        }
        rightNode.Parent = node.Parent;
        if (node.Parent == null)
        {
            _root = rightNode; // Update root if node is the root
        }
        else if (node == node.Parent.Left)
        {
            node.Parent.Left = rightNode;
        }
        else
        {
            node.Parent.Right = rightNode;
        }
        rightNode.Left = node;
        node.Parent = rightNode;
    }

    /// <summary>
    /// Performs a right rotation on the specified node.
    /// </summary>
    /// <param name="node">The node to be rotated.</param>
    private void RotateRight(Node node)
    {
        // Rotate the node to the right.
        var leftNode = node.Left;
        node.Left = leftNode.Right;
        if (leftNode.Right is not null)
        {
            leftNode.Right.Parent = node;
        }
        leftNode.Parent = node.Parent;
        if (node.Parent == null)
        {
            _root = leftNode; // Update root if node is the root
        }
        else if (node == node.Parent.Right)
        {
            node.Parent.Right = leftNode;
        }
        else
        {
            node.Parent.Left = leftNode;
        }
        leftNode.Right = node;
        node.Parent = leftNode;
    }

    /// <summary>
    /// Finds the node with the specified key.
    /// </summary>
    /// <param name="key">The key to find.</param>
    /// <returns>The node with the specified key, or null if not found.</returns>
    private Node FindNode(TKey key)
    {
        // Traverse the tree to find the node with the specified key.
        var current = _root;
        while (current is not null)
        {
            var comparison = key.CompareTo(current.Key);
            switch (comparison)
            {
                case < 0:
                    current = current.Left; // Move to the left child
                    break;
                case > 0:
                    current = current.Right; // Move to the right child
                    break;
                default:
                    return current; // Key found
            }
        }
        return null; // Key not found
    }

    /// <summary>
    /// Performs an in-order traversal of the tree to collect all items.
    /// </summary>
    /// <param name="node">The starting node for traversal.</param>
    /// <param name="items">The list to collect the key-value pairs.</param>
    private void InOrderTraversal(Node node, List<KeyValuePair<TKey, TValue>> items)
    {
        // Perform in-order traversal to retrieve all items in sorted order.
        if (node == null) return;

        InOrderTraversal(node.Left, items); // Traverse left subtree
        items.Add(new(node.Key, node.Value)); // Visit node
        InOrderTraversal(node.Right, items); // Traverse right subtree
    }
}