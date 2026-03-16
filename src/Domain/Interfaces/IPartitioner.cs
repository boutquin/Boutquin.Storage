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
/// Defines a strategy for mapping keys to partition indices.
///
/// <para>Partitioning splits a dataset across multiple nodes or storage units so that each partition
/// holds a subset of the data. The two primary strategies are key-range partitioning (preserves ordering,
/// enables range scans, but risks hot spots on sequential keys) and hash partitioning (uniform distribution,
/// but destroys key ordering).</para>
///
/// <para><b>Complexity:</b></para>
/// <para>- <b>GetPartition:</b> Implementation-dependent — O(log n) for range partitioning (binary search on boundaries),
/// O(1) for hash partitioning (hash + mod).</para>
///
/// <para><b>Reference:</b> Kleppmann, <i>Designing Data-Intensive Applications</i> (O'Reilly, 2017), Ch. 6 — "Partitioning",
/// covering key-range vs. hash partitioning trade-offs, rebalancing strategies, and secondary index partitioning.</para>
/// </summary>
/// <typeparam name="TKey">The key type. Must be comparable for range-based partitioning.</typeparam>
public interface IPartitioner<in TKey>
    where TKey : IComparable<TKey>
{
    /// <summary>
    /// Returns the zero-based partition index for the given key.
    /// </summary>
    /// <param name="key">The key to partition.</param>
    /// <returns>A partition index in the range [0, <see cref="PartitionCount"/>).</returns>
    int GetPartition(TKey key);

    /// <summary>
    /// Gets the total number of partitions.
    /// </summary>
    int PartitionCount { get; }
}
