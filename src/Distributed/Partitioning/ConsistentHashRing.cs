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
namespace Boutquin.Storage.Distributed.Partitioning;

/// <summary>
/// A consistent hash ring implementation using virtual nodes for uniform key distribution.
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
/// <b>Lookup structure.</b> Slots live in a hash map; lookups binary-search a sorted array of
/// slot positions that is rebuilt lazily after a topology change (add/remove node). Lookups
/// are O(log V) for V total virtual nodes — the classic consistent-hashing search (Karger et
/// al. 1997) — and the rebuild cost is paid once per topology change rather than on the hot
/// lookup path. (A SortedDictionary cannot do this: it offers no "first key &#8805; x" search,
/// so finding the next clockwise slot in one would be a linear walk.)
/// </para>
///
/// <para>
/// <b>Why virtual nodes?</b> With only physical nodes on the ring, a small number of nodes (say 3)
/// can lead to highly uneven partitions. Virtual nodes (e.g., 150 per physical node) smooth out
/// the distribution. Each virtual node maps back to its physical node.
/// </para>
///
/// <para>
/// <b>Node identity is its canonical text.</b> Virtual-node ring positions derive from the
/// node's canonical, culture-invariant text — the form <see cref="HashPartitioner{TKey}"/> hashes
/// keys by (tuples component by component, other types through <c>ToString()</c> under the invariant
/// culture) — so every host places a node at the same positions. Two node objects with equal
/// canonical text collide on every virtual key (the probe chain keeps lookups correct, but the
/// distribution claim silently degrades). Give every node a unique, stable string identity.
/// </para>
///
/// <para>
/// <b>Thread safety:</b> This class is not thread-safe. External synchronization is required for concurrent use.
/// </para>
/// </remarks>
/// <typeparam name="TNode">The type representing a node.</typeparam>
public sealed class ConsistentHashRing<TNode> : IConsistentHashRing<TNode>
    where TNode : notnull
{
    // 64-bit ring key. Widened from the original 32-bit space because birthday-paradox
    // collision probability on a 32-bit space with 5,000 virtual nodes is ≈ 0.17%, enough
    // that a real cluster would occasionally misroute traffic. On 64 bits, the same collision
    // probability drops below 10⁻¹². Karger et al. 1997 § 4 is the authority for the widening.
    private readonly Dictionary<long, TNode> _ring = [];
    private readonly HashSet<TNode> _physicalNodes = [];

    // Sorted slot positions for binary-search lookups; invalidated by AddNode/RemoveNode and
    // rebuilt lazily on the next lookup, so a burst of topology changes pays one sort.
    private long[]? _sortedSlots;

    // Ledger of the slots each node ACTUALLY claimed, including collision-probed ones.
    // Removal deletes exactly these slots instead of replaying the probe sequence —
    // probe replay stops at the first empty slot, so removing the probe-chain head first
    // would orphan dependent claimants' slots as permanent ghost entries (the classic
    // open-addressing deletion problem; a tombstone-free ledger sidesteps it entirely).
    private readonly Dictionary<TNode, List<long>> _claimedSlots = [];
    private readonly int _virtualNodeCount;
    private readonly IHashAlgorithm _hashAlgorithm;

    // Second seed for the composite 64-bit hash. Uses the golden-ratio fractional constant
    // (0x9E3779B97F4A7C15) as a well-distributed XOR salt — the same constant Java's HashMap
    // and Linux's kernel use as a mixing seed. This keeps H₁ and H₂ statistically independent
    // so the composed key behaves like a real 64-bit Murmur rather than two correlated 32-bit
    // hashes concatenated.
    private const ulong SecondHashSalt = 0x9E3779B97F4A7C15UL;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConsistentHashRing{TNode}"/> class.
    /// </summary>
    /// <param name="virtualNodeCount">
    /// The number of virtual nodes per physical node. Defaults to 150.
    /// Recommended: 100–200 for production. Higher values improve load distribution uniformity
    /// but increase memory usage (each virtual node is a point on the ring). For 3–5 physical
    /// nodes, 150 virtual nodes per node typically achieves &lt;10% load variance.
    /// </param>
    /// <param name="hashAlgorithm">The hash algorithm to use. Defaults to Murmur3.</param>
    public ConsistentHashRing(int virtualNodeCount = 150, IHashAlgorithm? hashAlgorithm = null)
    {
        if (virtualNodeCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(virtualNodeCount), "Virtual node count must be at least 1.");
        }

        _virtualNodeCount = virtualNodeCount;
        _hashAlgorithm = hashAlgorithm ?? new Murmur3();
    }

    /// <inheritdoc/>
    public void AddNode(TNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (!_physicalNodes.Add(node))
        {
            return; // Already added
        }

        _sortedSlots = null;
        var slots = new List<long>(_virtualNodeCount);
        _claimedSlots[node] = slots;

        // Canonical, culture-invariant node text so a node's virtual slots land at the same ring
        // positions regardless of the culture active at AddNode time: a tuple's ToString formats its
        // components with the thread culture, which would build a different ring for the same topology.
        var identity = CanonicalKeyText.Of(node);
        for (var i = 0; i < _virtualNodeCount; i++)
        {
            var virtualKey = string.Create(CultureInfo.InvariantCulture, $"{identity}#{i}");
            var hash = ComputeHash(virtualKey);

            // Defence-in-depth against the (< 10⁻¹²) residual collision probability: if the
            // 64-bit slot is already claimed by a different node, probe with double-hashing
            // (Knuth, The Art of Computer Programming Vol. 3 § 6.4). Same-node collisions are
            // a no-op — the slot is already ours.
            var probe = 0;
            while (_ring.TryGetValue(hash, out var existing) && !existing.Equals(node))
            {
                probe++;
                hash = PerturbHash(hash, probe);
            }

            // TryAdd: when the probe walk stopped on a slot this node already owns (two of
            // its own virtual keys collided), the slot is already recorded — don't double-add.
            if (_ring.TryAdd(hash, node))
            {
                slots.Add(hash);
            }
        }
    }

    /// <inheritdoc/>
    public void RemoveNode(TNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (!_physicalNodes.Remove(node))
        {
            return; // Not present
        }

        _sortedSlots = null;

        // Delete exactly the slots this node claimed at AddNode time (ledger lookup, never a
        // probe replay): replaying the probe sequence stops at the first empty slot, so if the
        // probe-chain head's owner was removed earlier this node's probed slots would survive
        // as ghosts and keep routing keys to a removed node.
        foreach (var slot in _claimedSlots[node])
        {
            _ring.Remove(slot);
        }

        _claimedSlots.Remove(node);
    }

    /// <inheritdoc/>
    public TNode GetNode(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (_ring.Count == 0)
        {
            throw new InvalidOperationException("The hash ring is empty. Add at least one node before looking up keys.");
        }

        var hash = ComputeHash(key);

        // First slot clockwise from the hash position: binary search for the smallest slot
        // >= hash (an exact hit IS that slot); past the last slot, wrap to the smallest.
        var slots = SortedSlots();
        var index = Array.BinarySearch(slots, hash);
        if (index < 0)
        {
            index = ~index;
        }

        if (index == slots.Length)
        {
            index = 0; // Wrap around — the smallest slot on the ring
        }

        return _ring[slots[index]];
    }

    /// <inheritdoc/>
    public IReadOnlyList<TNode> GetNodes(string key, int count)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        if (_ring.Count == 0)
        {
            throw new InvalidOperationException("The hash ring is empty. Add at least one node before looking up keys.");
        }

        // Strict contract: refuse to silently return fewer nodes than requested.
        // DDIA 2e Ch. 7 ("Sharding") treats the partitioner as a deterministic
        // mapping from key to a node-set of the requested size; downstream replication
        // tiers rely on that contract — silently downgrading the node-set
        // size masks a configuration mismatch as a correctness issue.
        if (count > _physicalNodes.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(count),
                count,
                $"Requested {count} distinct nodes but the ring has only {_physicalNodes.Count}. "
                + "Either reduce the request count or add more nodes to the ring.");
        }

        var distinctNodes = new List<TNode>();
        var seen = new HashSet<TNode>();
        var hash = ComputeHash(key);

        // Walk clockwise from the first slot >= hash, wrapping modularly, collecting
        // distinct physical nodes. Bounded by one full revolution.
        var slots = SortedSlots();
        var start = Array.BinarySearch(slots, hash);
        if (start < 0)
        {
            start = ~start;
        }

        for (var i = 0; i < slots.Length && distinctNodes.Count < count; i++)
        {
            var node = _ring[slots[(start + i) % slots.Length]];
            if (seen.Add(node))
            {
                distinctNodes.Add(node);
            }
        }

        return distinctNodes;
    }

    /// <summary>
    /// The ring's slot positions in ascending order, rebuilt lazily after a topology change.
    /// </summary>
    private long[] SortedSlots()
    {
        if (_sortedSlots is null)
        {
            _sortedSlots = [.. _ring.Keys];
            Array.Sort(_sortedSlots);
        }

        return _sortedSlots;
    }

    /// <summary>
    /// Composes a 64-bit ring key from two 32-bit hashes of the same input — one using the
    /// configured algorithm directly, the other applying a golden-ratio salt to the input
    /// bytes. The composition behaves like a single 64-bit Murmur: two distinct inputs have a
    /// collision probability bounded by the birthday-paradox approximation on 2⁶⁴ slots
    /// (&lt; 10⁻¹² at 5,000 virtual nodes), which is negligible for any realistic cluster.
    /// </summary>
    private long ComputeHash(string key)
    {
        var bytes = Encoding.UTF8.GetBytes(key);

        var hi = _hashAlgorithm.ComputeHash(bytes);

        // Second hash: XOR each byte with a salt byte derived from the FULL 64-bit salt so the
        // low 32 bits are statistically independent of the high 32 bits. The salt byte for
        // position i is the most-significant byte of (salt × (i + 1)) — the multiply spreads all
        // 64 salt bits into the top byte, so even a 1-byte key folds in the whole golden-ratio
        // constant rather than only salt byte 0. The earlier `salt >> ((i & 7) * 8)` fold
        // selected salt byte i for input byte i, so a key shorter than 8 bytes never touched the
        // well-mixed high salt bytes — short keys (shard ids, region codes) saw a weakly-salted
        // low half. Using a salted copy rather than the same bytes still guarantees
        // H₂(key) ≠ H₁(key), so the composite is not biased toward 0xAAAAAAAA_AAAAAAAA values.
        var salted = new byte[bytes.Length];
        for (var i = 0; i < bytes.Length; i++)
        {
            var saltByte = (byte)((SecondHashSalt * (ulong)(i + 1)) >> 56);
            salted[i] = (byte)(bytes[i] ^ saltByte);
        }
        var lo = _hashAlgorithm.ComputeHash(salted);

        return unchecked((long)(((ulong)hi << 32) | lo));
    }

    /// <summary>
    /// Test-only observability (<c>InternalsVisibleTo</c>): exposes the composite 64-bit ring
    /// key for a given input so a distribution test can measure high-/low-32-bit independence
    /// over short keys, which the public routing surface only reflects indirectly.
    /// </summary>
    internal long ComputeHashForTesting(string key) => ComputeHash(key);

    /// <summary>
    /// Probes a new 64-bit slot in the event a collision is observed at <paramref name="hash"/>.
    /// The perturbation sequence <c>hash ⊕ (salt × probe)</c> visits statistically independent
    /// slots on each retry, matching the double-hashing pattern recommended for open-addressed
    /// hashing. The probability of reaching probe &gt; 1 is itself &lt; 10⁻¹² so the loop
    /// terminates after at most one step in practice.
    /// </summary>
    private static long PerturbHash(long hash, int probe)
    {
        unchecked
        {
            return (long)((ulong)hash ^ (SecondHashSalt * (ulong)probe));
        }
    }
}
