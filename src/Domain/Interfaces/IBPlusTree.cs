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
/// Interface for a B+ tree data structure, an extension of the B-tree optimized for range queries.
///
/// <para>A B+ tree differs from a B-tree in two key ways:</para>
/// <para>1. <b>All values are stored in leaf nodes.</b> Internal nodes contain only keys and child pointers,
///   acting purely as a routing structure. This means internal nodes can hold more keys per node (higher
///   branching factor), reducing tree height.</para>
/// <para>2. <b>Leaf nodes are linked.</b> Each leaf has a pointer to the next leaf, forming a linked list.
///   This enables efficient range queries — once the start leaf is found, the query follows the linked list
///   without backtracking up the tree.</para>
///
/// <para><b>B+ tree vs B-tree trade-offs:</b></para>
/// <para>- <b>Range queries:</b> B+ trees are significantly faster because leaf linking avoids tree traversal.
///   B-trees must perform an in-order traversal involving parent-child navigation.</para>
/// <para>- <b>Point queries:</b> B+ trees always descend to a leaf (consistent depth), while B-trees may find
///   the value in an internal node (shorter path). In practice, the difference is one comparison.</para>
/// <para>- <b>Space:</b> B+ trees duplicate keys in internal nodes (keys appear in both internal and leaf nodes).
///   B-trees store each key exactly once.</para>
///
/// <para><b>Complexity (where t = order/minimum degree, n = number of keys):</b></para>
/// <para>- <b>Search (TryGetValueAsync, ContainsKeyAsync):</b> O(log_t n) — always descends to a leaf.</para>
/// <para>- <b>Insert (SetAsync):</b> O(log_t n) — descends to leaf, may split nodes upward.</para>
/// <para>- <b>Delete (RemoveAsync):</b> O(log_t n) — descends to leaf, may merge/redistribute.</para>
/// <para>- <b>Range query (RangeQueryAsync):</b> O(log_t n + k) where k = number of results — find start leaf, then follow links.</para>
/// <para>- <b>GetAllItemsAsync:</b> O(n) — traverse linked leaves from leftmost.</para>
/// <para>- <b>Space:</b> O(n).</para>
///
/// <para><b>Reference:</b> Kleppmann, <i>Designing Data-Intensive Applications</i> (O'Reilly, 2017), Ch. 3 — "Storage and Retrieval",
/// section on B-trees. B+ trees are the standard on-disk index structure in most relational databases (PostgreSQL,
/// MySQL InnoDB, SQLite) because their leaf-linked structure makes range scans efficient.</para>
/// </summary>
/// <typeparam name="TKey">The type of the keys in the tree.</typeparam>
/// <typeparam name="TValue">The type of the values in the tree.</typeparam>
public interface IBPlusTree<TKey, TValue> : IBulkKeyValueStore<TKey, TValue>
    where TKey : IComparable<TKey>
{
    /// <summary>
    /// Gets the order (minimum degree) of the B+ tree.
    /// </summary>
    int Order { get; }

    /// <summary>
    /// Gets the current height of the B+ tree.
    /// </summary>
    int Height { get; }

    /// <summary>
    /// Performs a range query, returning all key-value pairs where the key is between start and end (inclusive).
    /// </summary>
    /// <param name="start">The lower bound of the range (inclusive).</param>
    /// <param name="end">The upper bound of the range (inclusive).</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>An enumerable of key-value pairs within the specified range, in sorted order.</returns>
    Task<IEnumerable<(TKey Key, TValue Value)>> RangeQueryAsync(TKey start, TKey end, CancellationToken cancellationToken = default);
}
