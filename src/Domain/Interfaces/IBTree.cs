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
namespace Boutquin.Storage.Domain.Interfaces;

/// <summary>
/// Interface for a B-tree data structure.
/// </summary>
/// <typeparam name="TKey">The type of the keys in the tree.</typeparam>
/// <typeparam name="TValue">The type of the values in the tree.</typeparam>
/// <remarks>
/// <para><b>Theory of B-Trees:</b></para>
/// <para>A B-tree is a self-balancing search tree that maintains sorted data and allows searches, sequential access,
/// insertions, and deletions in logarithmic time. Unlike binary search trees, B-tree nodes can have many children,
/// ranging from a minimum degree <c>t</c> to <c>2t</c> children per node (except the root which may have fewer).</para>
///
/// <para><b>B-Tree Properties (order t, minimum degree):</b></para>
/// <para>1. Every node has at most <c>2t - 1</c> keys.</para>
/// <para>2. Every internal node (except root) has at least <c>t - 1</c> keys.</para>
/// <para>3. The root has at least 1 key (if non-empty).</para>
/// <para>4. All leaves appear at the same depth (perfectly balanced).</para>
/// <para>5. A non-leaf node with <c>k</c> keys has <c>k + 1</c> children.</para>
///
/// <para><b>Advantages over binary search trees:</b></para>
/// <para>- Fewer levels due to high branching factor, reducing disk I/O for on-disk implementations.</para>
/// <para>- Better cache locality due to storing multiple keys per node.</para>
/// <para>- Guaranteed O(log n) operations with balanced height.</para>
///
/// <para><b>Application in storage engines:</b></para>
/// <para>B-trees are the foundation of most database indexing systems (e.g., PostgreSQL, MySQL InnoDB).
/// They provide efficient point queries and range scans, making them suitable for both read-heavy and
/// write-moderate workloads. In contrast to LSM-trees which optimize for writes, B-trees provide
/// consistent read performance at the cost of write amplification.</para>
///
/// <para><b>Complexity (where t = minimum degree, n = number of keys):</b></para>
/// <para>- <b>Search (TryGetValueAsync, ContainsKeyAsync):</b> O(log_t n) — each level narrows the search by a factor of t.</para>
/// <para>- <b>Insert (SetAsync):</b> O(log_t n) — descends the tree and may split nodes.</para>
/// <para>- <b>Delete (RemoveAsync):</b> O(log_t n) — descends the tree and may merge or redistribute nodes.</para>
/// <para>- <b>GetAllItemsAsync:</b> O(n) — in-order traversal of all keys.</para>
/// <para>- <b>Space:</b> O(n).</para>
///
/// <para><b>Reference:</b> Kleppmann, <i>Designing Data-Intensive Applications</i> (O'Reilly, 2017), Ch. 3 — "Storage and Retrieval",
/// section on B-trees. B-trees are the most widely used indexing structure in databases, providing consistent
/// O(log n) reads and writes by keeping the tree balanced at all times.</para>
/// </remarks>
public interface IBTree<TKey, TValue> : IBulkKeyValueStore<TKey, TValue>
    where TKey : IComparable<TKey>
{
    /// <summary>
    /// Gets the order (minimum degree) of the B-tree.
    /// </summary>
    /// <value>
    /// The minimum degree <c>t</c>. Each node can hold between <c>t - 1</c> and <c>2t - 1</c> keys.
    /// </value>
    int Order { get; }

    /// <summary>
    /// Gets the current height of the B-tree.
    /// </summary>
    /// <value>
    /// The number of levels in the tree. An empty tree has height 0; a tree with only a root has height 1.
    /// </value>
    int Height { get; }
}
