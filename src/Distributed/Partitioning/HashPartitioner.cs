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
using System.Runtime.CompilerServices;

namespace Boutquin.Storage.Distributed.Partitioning;

/// <summary>
/// A hash-based partitioner that distributes keys uniformly across partitions using a hash function.
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
/// <b>How it works:</b> The key is converted to a canonical, culture-invariant text form, encoded as
/// UTF-8, hashed using the configured <see cref="IHashAlgorithm"/>, and the hash is mapped to a partition via
/// modular arithmetic. This ensures uniform distribution regardless of key ordering.
/// </para>
///
/// <para>
/// <b>Equal keys share a partition.</b> The canonical form is identical for keys that compare equal:
/// <see cref="DateTimeOffset"/> hashes its UTC ticks (the same instant in different offsets is one key),
/// <see cref="DateTime"/> and <see cref="TimeOnly"/> hash their ticks, <see cref="decimal"/> drops trailing
/// zeros (<c>1.0m</c> equals <c>1.00m</c>), and floating-point zero hashes as <c>0</c> whether signed or not.
/// Tuple keys (<see cref="ValueTuple"/> and <see cref="Tuple"/>) hash the canonical form of each component,
/// so <c>(1.0m, 3)</c> and <c>(1.00m, 3)</c> share a partition. Other key types hash their
/// <see cref="object.ToString"/> text rendered under the invariant culture, so a custom key type whose
/// equal values print differently must override <see cref="object.ToString"/> to print a canonical form.
/// </para>
///
/// <para>
/// <b>Why hash partitioning?</b> Unlike range partitioning, hash partitioning distributes keys uniformly
/// even when keys arrive in sorted order (e.g., timestamps). The trade-off is that range queries become
/// inefficient — a range scan must touch all partitions.
/// </para>
///
/// <para>
/// <b>Fixed partition count, not hash-mod-N-nodes.</b> The modulo here maps to a partition
/// count that is immutable after construction — the "fixed number of partitions" sharding
/// strategy, where partitions are reassigned among nodes by a separate layer. DDIA 2e Ch. 7
/// warns against taking the hash modulo the (changing) <i>node</i> count, which reshuffles
/// almost every key on each topology change; an immutable partition count avoids that trap.
/// </para>
///
/// <para>
/// <b>Thread safety:</b> This class is thread-safe for concurrent reads. All state is immutable after construction.
/// </para>
/// </remarks>
/// <typeparam name="TKey">The key type.</typeparam>
public sealed class HashPartitioner<TKey> : IPartitioner<TKey>
    where TKey : IComparable<TKey>
{
    private readonly IHashAlgorithm _hashAlgorithm;

    /// <summary>
    /// Initializes a new instance of the <see cref="HashPartitioner{TKey}"/> class.
    /// </summary>
    /// <param name="partitionCount">The number of partitions. Must be at least 1.</param>
    /// <param name="hashAlgorithm">
    /// The hash algorithm to use. Defaults to <see cref="Murmur3"/> if not specified.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="partitionCount"/> is less than 1.</exception>
    public HashPartitioner(int partitionCount, IHashAlgorithm? hashAlgorithm = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(partitionCount, 1);

        PartitionCount = partitionCount;
        _hashAlgorithm = hashAlgorithm ?? new Murmur3();
    }

    /// <inheritdoc />
    public int PartitionCount { get; }

    /// <inheritdoc />
    public int GetPartition(TKey key)
    {
        ArgumentNullException.ThrowIfNull(key);

        // Canonical, culture-invariant text: keys that compare equal (one DateTimeOffset instant
        // in two offsets, 1.0m and 1.00m, 0.0 and -0.0) must hash the same bytes, and a
        // culture-sensitive ToString ("1.5" vs "1,5") must not move a key between partitions.
        var keyBytes = Encoding.UTF8.GetBytes(CanonicalKeyText.Of(key));
        var hash = _hashAlgorithm.ComputeHash(keyBytes);

        // Use unsigned modulo to avoid negative partition indices
        return (int)(hash % (uint)PartitionCount);
    }
}

/// <summary>
/// Canonical, culture-invariant text for a routing key, shared by <see cref="HashPartitioner{TKey}"/> and
/// <c>SaltedHotKeyStrategy</c> so a write and a read of one logical key resolve to the same bytes.
/// </summary>
/// <remarks>
/// Keys that compare equal must produce identical text. The default <c>ToString</c> of several BCL types
/// fails that: <see cref="DateTimeOffset"/> prints its offset and drops sub-second ticks, <see cref="decimal"/>
/// keeps its scale, floating-point negative zero prints <c>-0</c>, and <see cref="TimeOnly"/> drops seconds.
/// Tuples fail it twice: a tuple is not <see cref="IFormattable"/>, so its <c>ToString</c> prints every
/// component with the calling thread's culture, and with the component's non-canonical text. A tuple is
/// therefore rendered component by component, each component length-prefixed so the form stays
/// unambiguous. The same form is the node identity of <see cref="ConsistentHashRing{TNode}"/> and
/// <see cref="RendezvousHash{TNode}"/>, so every host places and scores a node identically.
/// Text for the common string and integer keys is unchanged.
/// </remarks>
internal static class CanonicalKeyText
{
    /// <summary>Returns the canonical invariant text of <paramref name="key"/>.</summary>
    internal static string Of<TKey>(TKey key)
    {
        ArgumentNullException.ThrowIfNull(key);

        return OfValue(key);
    }

    private static string OfValue(object item)
    {
        return item switch
        {
            string text => text,
            DateTimeOffset value => value.UtcTicks.ToString(CultureInfo.InvariantCulture),
            DateTime value => value.Ticks.ToString(CultureInfo.InvariantCulture),
            TimeOnly value => value.Ticks.ToString(CultureInfo.InvariantCulture),
            // Dividing by 1.0 scaled to the maximum precision strips trailing zeros without changing the value.
            decimal value => (value / 1.0000000000000000000000000000m).ToString(CultureInfo.InvariantCulture),
            double value => value == 0 ? "0" : value.ToString("R", CultureInfo.InvariantCulture),
            float value => value == 0 ? "0" : value.ToString("R", CultureInfo.InvariantCulture),
            Half value => value == Half.Zero ? "0" : value.ToString("R", CultureInfo.InvariantCulture),
            ITuple tuple => OfTuple(tuple),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => ToStringUnderInvariantCulture(item),
        };
    }

    // Component-wise: each component's canonical text, length-prefixed so a component containing the
    // separator cannot be confused with a component boundary; "~" marks a null component.
    private static string OfTuple(ITuple tuple)
    {
        var builder = new StringBuilder("(");
        for (var i = 0; i < tuple.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            var component = tuple[i];
            if (component is null)
            {
                builder.Append('~');
                continue;
            }

            var text = OfValue(component);
            builder.Append(CultureInfo.InvariantCulture, $"{text.Length}:").Append(text);
        }

        return builder.Append(')').ToString();
    }

    // A type that is not IFormattable (a record, a custom struct) cannot take a format provider, and its
    // ToString formats members with the thread culture; render it under the invariant culture instead.
    private static string ToStringUnderInvariantCulture(object value)
    {
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            return value.ToString() ?? string.Empty;
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
