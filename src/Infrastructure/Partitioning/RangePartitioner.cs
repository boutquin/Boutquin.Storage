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
namespace Boutquin.Storage.Infrastructure.Partitioning;

/// <summary>
/// A range-based partitioner that assigns keys to partitions using sorted boundary keys.
/// </summary>
/// <remarks>
/// <para>
/// <b>How it works:</b> Given N sorted boundary keys, the partitioner creates N+1 partitions.
/// A key is assigned to partition i if it falls between boundary[i-1] (inclusive) and boundary[i] (exclusive),
/// with partition 0 covering everything below boundary[0] and partition N covering everything at or above boundary[N-1].
/// </para>
///
/// <para>
/// <b>Why binary search?</b> The boundaries are sorted, so <see cref="Array.BinarySearch{T}"/> provides O(log n)
/// lookup — the same approach used by HBase region servers to route requests to the correct region.
/// </para>
///
/// <para>
/// <b>Thread safety:</b> This class is thread-safe for concurrent reads. The boundaries are immutable after construction.
/// </para>
/// </remarks>
/// <typeparam name="TKey">The key type.</typeparam>
public sealed class RangePartitioner<TKey> : IRangePartitioner<TKey>
    where TKey : IComparable<TKey>
{
    private readonly TKey[] _boundaries;

    /// <summary>
    /// Initializes a new instance of the <see cref="RangePartitioner{TKey}"/> class.
    /// </summary>
    /// <param name="boundaries">
    /// Sorted boundary keys defining the partition splits. N boundaries create N+1 partitions.
    /// </param>
    /// <exception cref="ArgumentException">Thrown if boundaries is empty, unsorted, or contains duplicates.</exception>
    public RangePartitioner(IReadOnlyList<TKey> boundaries)
    {
        ArgumentNullException.ThrowIfNull(boundaries);

        if (boundaries.Count == 0)
        {
            throw new ArgumentException("Boundaries must not be empty.", nameof(boundaries));
        }

        // Validate sorted order and no duplicates
        for (var i = 1; i < boundaries.Count; i++)
        {
            if (boundaries[i].CompareTo(boundaries[i - 1]) <= 0)
            {
                throw new ArgumentException(
                    "Boundaries must be in strictly ascending order with no duplicates.",
                    nameof(boundaries));
            }
        }

        _boundaries = [.. boundaries];
    }

    /// <inheritdoc />
    public int PartitionCount => _boundaries.Length + 1;

    /// <inheritdoc />
    public int GetPartition(TKey key)
    {
        var index = Array.BinarySearch(_boundaries, key);

        // BinarySearch returns:
        //   >= 0: exact match at that index → key equals boundary → falls into next partition
        //   < 0: bitwise complement of the index of the first element larger than key
        if (index >= 0)
        {
            return index + 1;
        }

        return ~index;
    }

    /// <inheritdoc />
    public IReadOnlyList<TKey> GetBoundaries() => _boundaries;

    /// <inheritdoc />
    public (int StartPartition, int EndPartition) GetPartitionRange(TKey startKey, TKey endKey)
    {
        return (GetPartition(startKey), GetPartition(endKey));
    }
}
