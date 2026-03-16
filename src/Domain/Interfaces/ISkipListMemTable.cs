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
/// Interface for a skip list-based MemTable, an alternative to the red-black tree MemTable in an LSM-tree.
///
/// <para>A skip list is a probabilistic data structure that uses multiple levels of linked lists to achieve
/// O(log n) average-case search, insertion, and deletion. Each element is promoted to higher levels with
/// a fixed probability (typically 0.5), creating express lanes that allow binary search-like traversal.</para>
///
/// <para><b>Skip list vs Red-Black tree for MemTable:</b></para>
/// <para>- <b>Implementation complexity:</b> Skip lists are simpler to implement correctly than red-black trees
///   (no rotation logic, no color invariants).</para>
/// <para>- <b>Concurrency:</b> Skip lists are more amenable to lock-free concurrent implementations because
///   insertions only modify local pointers, whereas red-black tree rotations touch distant nodes.</para>
/// <para>- <b>Cache performance:</b> Red-black trees have better cache locality due to contiguous node layout.
///   Skip list nodes are scattered in memory due to variable-height forward pointer arrays.</para>
/// <para>- <b>Worst case:</b> Skip list O(log n) is probabilistic (expected), not guaranteed. A red-black tree
///   guarantees O(log n) worst-case. In practice, the probability of degradation is negligible.</para>
///
/// <para><b>Complexity (where n = number of entries, expected with probability 1/2):</b></para>
/// <para>- <b>SetAsync (insert/update):</b> O(log n) expected.</para>
/// <para>- <b>TryGetValueAsync / ContainsKeyAsync:</b> O(log n) expected.</para>
/// <para>- <b>RemoveAsync:</b> Not supported (LSM-tree uses tombstones instead of deletion).</para>
/// <para>- <b>GetAllItemsAsync:</b> O(n) — traverses the bottom-level linked list.</para>
/// <para>- <b>SetBulkAsync:</b> O(k log n) where k = number of items inserted.</para>
/// <para>- <b>ClearAsync:</b> O(1) — resets head node and count.</para>
/// <para>- <b>Space:</b> O(n) expected.</para>
///
/// <para><b>Reference:</b> Kleppmann, <i>Designing Data-Intensive Applications</i> (O'Reilly, 2017), Ch. 3 — "Storage and Retrieval",
/// section on memtables. Skip lists are used by LevelDB and RocksDB as their default memtable implementation
/// due to their simplicity and good concurrent performance.</para>
/// </summary>
/// <typeparam name="TKey">The type of the keys in the skip list.</typeparam>
/// <typeparam name="TValue">The type of the values in the skip list.</typeparam>
public interface ISkipListMemTable<TKey, TValue>
    : IMemTable<TKey, TValue>
    where TKey : IComparable<TKey>
{
    /// <summary>
    /// Gets a value indicating whether the skip list MemTable has reached its maximum capacity.
    /// </summary>
    bool IsFull { get; }

    /// <summary>
    /// Gets the maximum number of levels in the skip list.
    /// </summary>
    int MaxLevel { get; }
}
