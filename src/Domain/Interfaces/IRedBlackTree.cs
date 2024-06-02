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
namespace Boutquin.Storage.Domain.Interfaces;

/// <summary>
/// Interface for a red-black tree data structure.
/// </summary>
/// <typeparam name="TKey">The type of the keys in the tree.</typeparam>
/// <typeparam name="TValue">The type of the values in the tree.</typeparam>
/// <remarks>
/// <para><b>Theory of Red-Black Tree:</b></para>
/// <para>A red-black tree is a self-balancing binary search tree that ensures the tree remains balanced, providing O(log n) time complexity for 
/// insertion, deletion, and lookup operations. It achieves balance by enforcing properties on the nodes, such as node color (red or black) 
/// and constraints on the relationships between parent and child nodes. The key properties include:</para>
/// <para>- Each node is either red or black.</para>
/// <para>- The root is always black.</para>
/// <para>- Red nodes cannot have red children (no two red nodes can be adjacent).</para>
/// <para>- Every path from a node to its descendant null nodes must have the same number of black nodes.</para>
/// 
/// <para><b>Pros and Cons in the Context of an LSM-tree:</b></para>
/// 
/// <para><b>Pros:</b></para>
/// <para>- <b>Efficient Operations:</b> The self-balancing nature ensures O(log n) complexity for insertions, deletions, and lookups, making it efficient 
///   for handling dynamic data.</para>
/// <para>- <b>Balanced Structure:</b> Maintains a balanced tree, preventing the performance degradation that can occur with unbalanced trees.</para>
/// <para>- <b>Sorted Order:</b> Keeps key-value pairs in sorted order, which is essential for the efficient creation of SSTables during the flush process.</para>
/// 
/// <para><b>Cons:</b></para>
/// <para>- <b>Complexity:</b> Red-black trees are more complex to implement and manage compared to simpler data structures like hash tables or linked lists.</para>
/// <para>- <b>Overhead:</b> The balancing operations (rotations and color changes) introduce some overhead during insertions and deletions, although this 
///   is generally mitigated by the balanced structure.</para>
/// 
/// <para>In the context of an LSM-tree, the red-black tree is particularly effective for optimizing write operations. By maintaining an efficient, 
/// balanced, in-memory structure, it allows for quick write operations and provides an ordered set of data that can be efficiently flushed 
/// to disk as a sorted SSTable, improving the overall performance of the LSM-tree.</para>
/// </remarks>
public interface IRedBlackTree<TKey, TValue> 
    : IMemTable<TKey, TValue>
    where TKey : IComparable<TKey>
{
    /// <summary>
    /// Gets a value indicating whether the red-black tree (MemTable) is full.
    /// </summary>
    /// <value>
    /// <c>true</c> if the red-black tree is full; otherwise, <c>false</c>.
    /// </value>
    /// <remarks>
    /// The IsFull property indicates whether the red-black tree has reached its maximum capacity.
    /// This is particularly useful in the context of an LSM-tree where the MemTable needs to be flushed to disk
    /// as an SSTable when it becomes full. By checking this property, the system can decide when to trigger the flush operation.
    /// </remarks>
    bool IsFull { get; }
}