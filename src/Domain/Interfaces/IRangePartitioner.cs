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
/// Extends <see cref="IPartitioner{TKey}"/> with range-aware operations.
///
/// <para>Range partitioning assigns contiguous key ranges to partitions, enabling efficient range scans
/// (the client can determine which partitions a range query must touch). This is the strategy used by
/// Bigtable, HBase, and RethinkDB. The downside is hot spots when keys arrive in sorted order
/// (e.g., timestamps) — all writes hit the same partition.</para>
///
/// <para><b>Complexity:</b></para>
/// <para>- <b>GetPartition:</b> O(log n) where n = number of boundaries (binary search).</para>
/// <para>- <b>GetPartitionRange:</b> O(log n) — two binary searches.</para>
/// <para>- <b>GetBoundaries:</b> O(1) — returns the stored boundary list.</para>
///
/// <para><b>Reference:</b> Kleppmann, <i>Designing Data-Intensive Applications</i> (O'Reilly, 2017), Ch. 6 —
/// range partitioning enables efficient range scans but risks hot spots if keys are sequential.</para>
/// </summary>
/// <typeparam name="TKey">The key type.</typeparam>
public interface IRangePartitioner<TKey> : IPartitioner<TKey>
    where TKey : IComparable<TKey>
{
    /// <summary>
    /// Returns the sorted partition boundary keys.
    /// N boundaries define N+1 partitions.
    /// </summary>
    /// <returns>An immutable list of boundary keys in ascending order.</returns>
    IReadOnlyList<TKey> GetBoundaries();

    /// <summary>
    /// Returns the range of partition indices that a key-range query must touch.
    /// </summary>
    /// <param name="startKey">The inclusive start of the key range.</param>
    /// <param name="endKey">The inclusive end of the key range.</param>
    /// <returns>A tuple of (StartPartition, EndPartition) indices, both inclusive.</returns>
    (int StartPartition, int EndPartition) GetPartitionRange(TKey startKey, TKey endKey);
}
