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
using Boutquin.Storage.Domain.ValueObjects.LsmTree;

namespace Boutquin.Storage.Infrastructure.LsmTree;

/// <summary>
/// Groups segments into size tiers and merges a tier once it holds enough members. Cassandra's
/// default strategy (DDIA 2e pp. 124–125).
/// </summary>
/// <typeparam name="TKey">Key type — carried so the strategy satisfies
/// <see cref="ICompactionStrategy{TKey}"/>. STCS does not inspect keys.</typeparam>
/// <remarks>
/// <para>
/// <b>Theory:</b> DDIA 2e p. 124 — STCS groups SSTables by size, merging those in the same tier
/// so small recently-flushed segments merge with similarly small siblings instead of the whole
/// dataset. Write amplification is lower than leveled compaction at the cost of higher read
/// amplification (more segments to check per read).
/// </para>
/// <para>
/// <b>Phase B4 implementation:</b> this class implements real tier bucketing — segments whose
/// <see cref="SegmentMetadata{TKey}.SizeBytes"/> fall within
/// <c>[tier_base · multiplier^i, tier_base · multiplier^(i+1))</c> form tier <c>i</c>. When a
/// tier accumulates <see cref="MinSegmentsPerTier"/> or more members the oldest tier that
/// satisfies the criterion is selected; ties broken toward the smaller tier so small, cheap
/// merges run first.
/// </para>
/// <para>
/// <b>Parameters:</b>
/// </para>
/// <list type="bullet">
/// <item><c>minSegmentsPerTier</c> — Cassandra default is 4 segments per tier.</item>
/// <item><c>tierMultiplier</c> — each tier spans a size range <c>tierMultiplier×</c> wider than
/// the previous. Default 4 matches Cassandra's <c>min_sstable_size</c> × <c>bucket_high</c>
/// convention.</item>
/// <item><c>tierBaseBytes</c> — lower bound of tier 0. Default 1 MiB; a segment below this
/// size is treated as belonging to tier 0 so very early flushes still participate.</item>
/// </list>
/// <para>
/// <b>Reference:</b> Kleppmann &amp; Riccomini, <i>Designing Data-Intensive Applications</i>,
/// 2nd ed. (O'Reilly, 2026), Ch. 4 — "Size-tiered compaction" p. 124.
/// </para>
/// </remarks>
public sealed class SizeTieredCompactionStrategy<TKey> : ICompactionStrategy<TKey>
{
    /// <summary>Minimum segments in a tier before STCS will merge it.</summary>
    public int MinSegmentsPerTier { get; }

    /// <summary>Per-tier size multiplier — tier <c>i</c> starts at
    /// <c>TierBaseBytes × TierMultiplier^i</c>.</summary>
    public int TierMultiplier { get; }

    /// <summary>Lower bound of tier 0 in bytes.</summary>
    public long TierBaseBytes { get; }

    /// <summary>Creates a strategy with the Cassandra-style defaults.</summary>
    /// <param name="minSegments">Segments per tier required to trigger a merge. Must be ≥ 2.</param>
    /// <param name="tierMultiplier">Per-tier size multiplier. Must be ≥ 2.</param>
    /// <param name="tierBaseBytes">Lower bound of tier 0 in bytes. Must be ≥ 1.</param>
    public SizeTieredCompactionStrategy(
        int minSegments = 4,
        int tierMultiplier = 4,
        long tierBaseBytes = 1L * 1024 * 1024)
    {
        if (minSegments < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(minSegments), "Minimum segments must be at least 2.");
        }
        if (tierMultiplier < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(tierMultiplier), "Tier multiplier must be at least 2.");
        }
        if (tierBaseBytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(tierBaseBytes), "Tier base bytes must be at least 1.");
        }

        MinSegmentsPerTier = minSegments;
        TierMultiplier = tierMultiplier;
        TierBaseBytes = tierBaseBytes;
    }

    /// <inheritdoc/>
    public bool ShouldCompact(IReadOnlyList<SegmentMetadata<TKey>> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        return FindFullestTier(segments, out _, out var indexes) && indexes.Count >= MinSegmentsPerTier;
    }

    /// <inheritdoc/>
    public IReadOnlyList<SegmentMetadata<TKey>> SelectSegments(
        IReadOnlyList<SegmentMetadata<TKey>> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (!FindFullestTier(segments, out _, out var indexes) || indexes.Count < MinSegmentsPerTier)
        {
            // Below threshold — caller should not have invoked SelectSegments here, but we
            // honour the contract by returning an empty list instead of an arbitrary subset.
            return [];
        }

        // Preserve oldest → newest input ordering for last-writer-wins stability. Indexes are
        // already ascending by construction.
        var picked = new List<SegmentMetadata<TKey>>(indexes.Count);
        foreach (var i in indexes)
        {
            picked.Add(segments[i]);
        }

        return picked;
    }

    /// <summary>
    /// Bucket <paramref name="segments"/> by size tier and return the smallest tier that has
    /// reached <see cref="MinSegmentsPerTier"/>. Returns the member indexes (not the metadata
    /// values themselves) so identical-metadata segments — rare but possible — count correctly.
    /// </summary>
    private bool FindFullestTier(
        IReadOnlyList<SegmentMetadata<TKey>> segments,
        out int tier,
        out List<int> memberIndexes)
    {
        var buckets = new Dictionary<int, List<int>>();
        for (var i = 0; i < segments.Count; i++)
        {
            var bucket = TierOf(segments[i].SizeBytes);
            if (!buckets.TryGetValue(bucket, out var list))
            {
                list = [];
                buckets[bucket] = list;
            }
            list.Add(i);
        }

        // Merge the smallest-tier bucket that is full. Preferring small tiers first keeps the
        // per-merge cost bounded — larger tiers merge when they fill up on their own cadence.
        foreach (var entry in buckets.OrderBy(kvp => kvp.Key))
        {
            if (entry.Value.Count >= MinSegmentsPerTier)
            {
                tier = entry.Key;
                memberIndexes = entry.Value;
                return true;
            }
        }

        tier = 0;
        memberIndexes = [];
        return false;
    }

    /// <summary>
    /// Maps a segment size (bytes) to its tier index using the configured base and multiplier:
    /// the largest <c>i</c> such that <c>TierBaseBytes · TierMultiplier^i ≤ sizeBytes</c>.
    /// </summary>
    /// <remarks>
    /// Computed by an exact integer multiply-loop rather than
    /// <c>floor(log(size/base) / log(mult))</c>. At an exact power of the multiplier the
    /// floating-point form can round to one tier too low — e.g. <c>Math.Log(1000)/Math.Log(10)</c>
    /// evaluates to <c>2.9999…</c>, so <c>Math.Floor</c> yields tier 2 for a size that is exactly
    /// <c>10³</c> and belongs in tier 3 (finding F-70). The loop carries <c>threshold</c> as a
    /// <see cref="long"/> and stops the moment the next multiply would exceed
    /// <paramref name="sizeBytes"/>, so it is overflow-safe (the guard reads
    /// <c>sizeBytes / TierMultiplier</c> to avoid multiplying past <see cref="long.MaxValue"/>)
    /// and never depends on <see cref="double"/> precision.
    /// </remarks>
    internal int TierOf(long sizeBytes)
    {
        if (sizeBytes <= TierBaseBytes)
        {
            return 0;
        }

        var tier = 0;
        var threshold = TierBaseBytes;
        // Advance while the NEXT tier's lower bound (threshold · multiplier) still fits within
        // sizeBytes. The division guard keeps the comparison overflow-safe for very large
        // segments without ever forming threshold · TierMultiplier past long.MaxValue.
        while (threshold <= sizeBytes / TierMultiplier)
        {
            threshold *= TierMultiplier;
            tier++;
        }

        return tier;
    }
}
