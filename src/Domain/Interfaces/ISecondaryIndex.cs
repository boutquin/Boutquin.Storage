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
/// Represents a secondary index that maps derived index keys back to primary keys.
///
/// <para>A secondary index allows looking up records by attributes other than the primary key.
/// For example, in a user store keyed by user ID, a secondary index on email would allow
/// finding the user ID for a given email address.</para>
///
/// <para><b>Document-partitioned vs term-partitioned:</b></para>
/// <para>- <b>Document-partitioned (local index):</b> Each partition maintains its own secondary index
///   covering only the documents in that partition. Writes are fast (update one partition), but reads
///   require scatter-gather across all partitions.</para>
/// <para>- <b>Term-partitioned (global index):</b> The index is partitioned by the index term itself.
///   Reads hit a single partition, but writes may need to update multiple index partitions.</para>
///
/// <para>This implementation is an in-memory local secondary index suitable for single-node use.</para>
///
/// <para><b>Complexity:</b></para>
/// <para>- <b>IndexAsync:</b> O(1) amortized — hash-based lookup and set insertion.</para>
/// <para>- <b>LookupAsync:</b> O(1) amortized — hash-based lookup.</para>
/// <para>- <b>RemoveAsync:</b> O(1) amortized — hash-based lookup and set removal.</para>
/// <para>- <b>ClearAsync:</b> O(n) — clears the entire index.</para>
///
/// <para><b>Reference:</b> Kleppmann, <i>Designing Data-Intensive Applications</i> (O'Reilly, 2017), Ch. 3 — "Storage and Retrieval",
/// section on secondary indexes in storage engines; Ch. 6 — "Partitioning and Secondary Indexes".</para>
/// </summary>
/// <typeparam name="TKey">The type of the primary key.</typeparam>
/// <typeparam name="TValue">The type of the value being indexed.</typeparam>
/// <typeparam name="TIndexKey">The type of the derived index key.</typeparam>
public interface ISecondaryIndex<TKey, TValue, TIndexKey>
    where TKey : IComparable<TKey>
    where TIndexKey : IComparable<TIndexKey>
{
    /// <summary>
    /// Indexes a value by extracting its index key and associating it with the primary key.
    /// </summary>
    /// <param name="primaryKey">The primary key of the record.</param>
    /// <param name="value">The value to extract the index key from.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    Task IndexAsync(TKey primaryKey, TValue value, CancellationToken cancellationToken = default);

    /// <summary>
    /// Looks up all primary keys associated with the given index key.
    /// </summary>
    /// <param name="indexKey">The index key to look up.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>An enumerable of primary keys matching the index key, or empty if none found.</returns>
    Task<IEnumerable<TKey>> LookupAsync(TIndexKey indexKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the association between a primary key and its derived index key.
    /// </summary>
    /// <param name="primaryKey">The primary key to remove from the index.</param>
    /// <param name="value">The value to extract the index key from.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    Task RemoveAsync(TKey primaryKey, TValue value, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears all entries from the secondary index.
    /// </summary>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    Task ClearAsync(CancellationToken cancellationToken = default);
}
