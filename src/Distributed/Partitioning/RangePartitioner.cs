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
using System.Collections;
using System.Runtime.CompilerServices;

namespace Boutquin.Storage.Distributed.Partitioning;

/// <summary>
/// A range-based partitioner that assigns keys to partitions using sorted boundary keys.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope:</b> In-process reference implementation in <c>Boutquin.Storage.Distributed</c>,
/// which is not published to NuGet (see <c>src/Distributed/README.md</c>). Not durable
/// across process restart, not intended for production deployment. Consistent / range / rendezvous partitioning
/// is implemented for educational fidelity to DDIA 2e Ch. 7, but production-grade extensions
/// — fixed-shard-count routing with dynamic split/merge, locality-aware rebalancing, and partition-movement coordination — are not included. For production use, prefer a purpose-built sharding layer such as Vitess or the partitioner stack of Cassandra / ScyllaDB.
/// </para>
///
/// <para>
/// <b>How it works:</b> Given N sorted boundary keys, the partitioner creates N+1 partitions.
/// A key is assigned to partition i if it falls between boundary[i-1] (inclusive) and boundary[i] (exclusive),
/// with partition 0 covering everything below boundary[0] and partition N covering everything at or above boundary[N-1].
/// </para>
///
/// <para>
/// <b>Why binary search?</b> The boundaries are sorted, so <c>Array.BinarySearch&lt;T&gt;</c> provides O(log n)
/// lookup — the same approach used by HBase region servers to route requests to the correct region.
/// </para>
///
/// <para>
/// <b>Thread safety:</b> This class is thread-safe for concurrent reads. The boundaries are immutable after construction.
/// </para>
///
/// <para>
/// <b>String keys use ordinal order.</b> <c>string.CompareTo</c> follows the calling thread's culture, so
/// one boundary array would be sorted under one culture and unsorted under another, and a key would
/// route to a different partition per host. String boundaries and keys are therefore compared with
/// <see cref="StringComparer.Ordinal"/>. Tuple keys (<see cref="ValueTuple"/> and <see cref="Tuple"/>, the
/// natural compound partition key) compare component by component in their own order, except that string
/// components — including those of nested tuples — compare ordinally for the same reason. Every other key
/// type uses its <see cref="IComparable{T}"/> order.
/// </para>
/// </remarks>
/// <typeparam name="TKey">The key type.</typeparam>
public sealed class RangePartitioner<TKey> : IRangePartitioner<TKey>
    where TKey : IComparable<TKey>
{
    private static readonly IComparer<TKey> s_comparer = typeof(TKey) == typeof(string)
        ? (IComparer<TKey>)StringComparer.Ordinal
        : typeof(ITuple).IsAssignableFrom(typeof(TKey)) && typeof(IStructuralComparable).IsAssignableFrom(typeof(TKey))
            ? new OrdinalTupleComparer()
            : Comparer<TKey>.Default;

    // A tuple's default comparer orders its string components with the calling thread's culture.
    // Its structural comparison takes the component comparer as an argument, so the ordinal relation
    // reaches every component, nested tuples included.
    private sealed class OrdinalTupleComparer : IComparer<TKey>
    {
        public int Compare(TKey? x, TKey? y) => OrdinalComponentComparer.Instance.Compare(x, y);
    }

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
            if (s_comparer.Compare(boundaries[i], boundaries[i - 1]) <= 0)
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
        // Null-guard at the boundary, matching HashPartitioner.GetPartition and this type's own
        // GetPartitionRange: without it a null key reaches Array.BinarySearch/CompareTo and throws an
        // undocumented NullReferenceException instead of a contract ArgumentNullException.
        ArgumentNullException.ThrowIfNull(key);

        var index = Array.BinarySearch(_boundaries, key, s_comparer);

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
    // Return a defensive copy: handing out the internal array lets a caller cast the
    // IReadOnlyList back to TKey[] and mutate the boundaries, breaking the documented
    // immutable-after-construction / thread-safe-for-concurrent-reads guarantee (matches the
    // defensive copies in GSet.Value, VectorClock.GetClock, MerkleTree.RootHash).
    public IReadOnlyList<TKey> GetBoundaries() => (TKey[])_boundaries.Clone();

    /// <inheritdoc />
    public (int StartPartition, int EndPartition) GetPartitionRange(TKey startKey, TKey endKey)
    {
        ArgumentNullException.ThrowIfNull(startKey);
        ArgumentNullException.ThrowIfNull(endKey);

        // Reject reversed bounds. Previously the method silently returned a partition
        // tuple where StartPartition > EndPartition, which downstream consumers would
        // interpret as an empty range — masking a caller bug as a no-op result. DDIA 2e
        // Ch. 7 ("Sharding by Key Range") treats range partitioning as an ordered key-space;
        // the contract requires start ≤ end.
        if (s_comparer.Compare(startKey, endKey) > 0)
        {
            throw new ArgumentException(
                $"GetPartitionRange requires startKey <= endKey; got startKey={startKey}, endKey={endKey}.",
                nameof(startKey));
        }

        return (GetPartition(startKey), GetPartition(endKey));
    }
}

/// <summary>
/// Compares tuple components in their natural order, except that strings compare ordinally and nested
/// tuples recurse with this comparer, so a compound key never orders by the calling thread's culture.
/// </summary>
internal sealed class OrdinalComponentComparer : IComparer
{
    internal static OrdinalComponentComparer Instance { get; } = new();

    private static readonly ConcurrentDictionary<Type, IComparer> s_typedComparers = new();

    private OrdinalComponentComparer()
    {
    }

    public int Compare(object? x, object? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        if (x is string left && y is string right)
        {
            return string.CompareOrdinal(left, right);
        }

        if (x is ITuple && x is IStructuralComparable structural)
        {
            return structural.CompareTo(y, this);
        }

        if (x is IComparable)
        {
            return Comparer.Default.Compare(x, y);
        }

        // A component comparable only through IComparable<T>: use the same Comparer<T>.Default the
        // tuple's own CompareTo would have used.
        return s_typedComparers
            .GetOrAdd(x.GetType(), static type =>
                (IComparer)typeof(Comparer<>).MakeGenericType(type).GetProperty(nameof(Comparer<object>.Default))!.GetValue(null)!)
            .Compare(x, y);
    }
}
