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
/// Leveled compaction — organises segments into size-capped levels with non-overlapping key
/// ranges per level ≥ 1 (LevelDB/RocksDB default).
/// </summary>
/// <typeparam name="TKey">Key type used for key-range overlap detection between L_i and
/// L_(i+1).</typeparam>
/// <remarks>
/// <para>
/// <b>Theory:</b> DDIA 2e p. 124 — "All levels beyond L0 contain key range–partitioned
/// SSTables", and leveled compaction "is more efficient for reads than size-tiered compaction
/// because the storage engine needs to read fewer SSTables to check whether they contain the
/// key." The non-overlapping invariant is what delivers that: a reader probes O(1) segments
/// per level (plus the O(|L0|) overlapping L0 check), while writes pay O(levels) write
/// amplification as each entry migrates level-by-level down the pyramid.
/// </para>
/// <para>
/// <b>Read-path caveat (v2.0.0):</b> this strategy delivers the leveled <i>write</i>-amplification
/// shape, but the engine's point/range read path still walks segments by recency position
/// (<c>O(total segments)</c>); it does not yet exploit per-level key-range partitioning to prune to
/// one segment per level. Levels currently drive compaction selection only — the read-amplification
/// payoff above describes the algorithm, not the v2.0.0 engine read path.
/// </para>
/// <para>
/// <b>Behaviour:</b>
/// </para>
/// <list type="number">
/// <item>If L0 has at least <see cref="Level0Threshold"/> segments, pick one L0 segment (the
/// oldest) plus every L1 segment whose <c>[MinKey, MaxKey]</c> range overlaps that L0 segment's
/// range. L0 is always treated as potentially-overlapping and all of L0 may be picked if their
/// ranges intersect with the chosen L0 segment — this preserves last-writer-wins when the L0
/// key space itself overlaps between files.</item>
/// <item>Otherwise, for each level <c>L_i</c> starting at the highest non-empty level,
/// recompute the level's total <see cref="SegmentMetadata{TKey}.SizeBytes"/>; if it exceeds
/// <see cref="BaseLevelSizeMB"/> × <see cref="LevelSizeMultiplier"/>^i (LevelDB / RocksDB
/// convention), pick one L_i segment plus every overlapping L_(i+1) segment.</item>
/// <item>Return the picked set oldest → newest so the engine's k-way merge emits newest-value
/// wins consistently.</item>
/// </list>
/// <para>
/// <b>Reference:</b> Kleppmann &amp; Riccomini, <i>Designing Data-Intensive Applications</i>,
/// 2nd ed. (O'Reilly, 2026), Ch. 4 — "Leveled compaction" pp. 124–125. LevelDB/RocksDB size
/// schedules documented in the RocksDB wiki
/// (<c>Leveled-Compaction.md</c>).
/// </para>
/// </remarks>
public sealed class LeveledCompactionStrategy<TKey> : ICompactionStrategy<TKey>
    where TKey : IComparable<TKey>
{
    /// <summary>Minimum L0 segments before compaction triggers on L0.</summary>
    public int Level0Threshold { get; }

    /// <summary>Per-level size schedule multiplier (LevelDB default: 10).</summary>
    public int LevelSizeMultiplier { get; }

    /// <summary>L1 size cap in MiB; L_i cap = <c>BaseLevelSizeMB × LevelSizeMultiplier^(i−1)</c>
    /// for <c>i ≥ 1</c>.</summary>
    public int BaseLevelSizeMB { get; }

    /// <summary>Creates a strategy with LevelDB-style defaults.</summary>
    /// <param name="level0Threshold">L0 segment count that triggers L0→L1 compaction. Must be ≥ 2.</param>
    /// <param name="levelSizeMultiplier">Per-level size multiplier. Must be ≥ 2.</param>
    /// <param name="baseLevelSizeMB">L1 size cap in MiB. Must be positive.</param>
    public LeveledCompactionStrategy(
        int level0Threshold = 4,
        int levelSizeMultiplier = 10,
        int baseLevelSizeMB = 10)
    {
        if (level0Threshold < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(level0Threshold), "Level 0 threshold must be at least 2.");
        }

        if (levelSizeMultiplier < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(levelSizeMultiplier), "Level size multiplier must be at least 2.");
        }

        if (baseLevelSizeMB < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(baseLevelSizeMB), "Base level size must be at least 1 MB.");
        }

        Level0Threshold = level0Threshold;
        LevelSizeMultiplier = levelSizeMultiplier;
        BaseLevelSizeMB = baseLevelSizeMB;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Callers that need both the yes/no answer and the segment list should call
    /// <see cref="SelectSegments"/> once and check whether the result is non-empty —
    /// calling <c>ShouldCompact</c> followed by <c>SelectSegments</c> runs the
    /// selection algorithm twice.
    /// </remarks>
    public bool ShouldCompact(IReadOnlyList<SegmentMetadata<TKey>> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        return SelectInternal(segments).Count > 0;
    }

    /// <inheritdoc/>
    public IReadOnlyList<SegmentMetadata<TKey>> SelectSegments(
        IReadOnlyList<SegmentMetadata<TKey>> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        return SelectInternal(segments);
    }

    /// <summary>
    /// Actual algorithm. Returns empty when nothing needs compacting.
    /// </summary>
    private IReadOnlyList<SegmentMetadata<TKey>> SelectInternal(
        IReadOnlyList<SegmentMetadata<TKey>> segments)
    {
        if (segments.Count < 2)
        {
            return [];
        }

        // Bucket by level once; strategy is pure wrt the input so local mutation is safe.
        var byLevel = new Dictionary<int, List<SegmentMetadata<TKey>>>();
        foreach (var metadata in segments)
        {
            if (!byLevel.TryGetValue(metadata.Level, out var list))
            {
                list = [];
                byLevel[metadata.Level] = list;
            }
            list.Add(metadata);
        }

        // 1. L0 trigger — pick oldest L0 segment + overlapping L1 + any L0 segments whose range
        //    intersects (L0 is inherently overlap-tolerant — it receives direct memtable flushes).
        if (byLevel.TryGetValue(0, out var l0) && l0.Count >= Level0Threshold)
        {
            return PickForLevel(segments, byLevel, sourceLevel: 0);
        }

        // 2. Higher-level triggers — walk from the highest populated level downward so
        //    over-sized levels get drained before L0 floods them further.
        var orderedLevels = byLevel.Keys.OrderByDescending(l => l).ToList();
        foreach (var level in orderedLevels)
        {
            if (level == 0)
            {
                continue; // already handled above.
            }

            var levelBytes = byLevel[level].Sum(m => m.SizeBytes);
            var cap = LevelSizeCapBytes(level);
            if (levelBytes > cap)
            {
                return PickForLevel(segments, byLevel, sourceLevel: level);
            }
        }

        return [];
    }

    private static IReadOnlyList<SegmentMetadata<TKey>> PickForLevel(
        IReadOnlyList<SegmentMetadata<TKey>> segments,
        Dictionary<int, List<SegmentMetadata<TKey>>> byLevel,
        int sourceLevel)
    {
        // A format without segment-wide bounds can overlap any key window. Compact the
        // complete ordered set rather than comparing invalid default keys or dropping overlap.
        if (segments.Any(segment => !segment.HasKeyRange))
        {
            return segments;
        }
        var sourceBucket = byLevel[sourceLevel];

        // L0 picks one segment (the oldest); higher levels do the same.
        var picked = new HashSet<SegmentMetadata<TKey>>();
        var sourceSegment = sourceBucket[0];
        picked.Add(sourceSegment);

        var minKey = sourceSegment.MinKey;
        var maxKey = sourceSegment.MaxKey;

        if (sourceLevel == 0 && sourceBucket.Count > 1)
        {
            // Collect transitive overlaps within L0 so any ranges that touch the chosen key
            // window migrate to L1 together. This is LevelDB's "overlapping L0 set."
            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var candidate in sourceBucket)
                {
                    if (picked.Contains(candidate))
                    {
                        continue;
                    }
                    if (RangesOverlap(candidate.MinKey, candidate.MaxKey, minKey, maxKey))
                    {
                        picked.Add(candidate);
                        if (candidate.MinKey.CompareTo(minKey) < 0) { minKey = candidate.MinKey; }
                        if (candidate.MaxKey.CompareTo(maxKey) > 0) { maxKey = candidate.MaxKey; }
                        changed = true;
                    }
                }
            }
        }

        // Fold in every next-level segment that overlaps the current key window.
        var nextLevel = sourceLevel + 1;
        if (byLevel.TryGetValue(nextLevel, out var nextBucket))
        {
            foreach (var candidate in nextBucket)
            {
                if (RangesOverlap(candidate.MinKey, candidate.MaxKey, minKey, maxKey))
                {
                    picked.Add(candidate);
                }
            }
        }

        // Preserve oldest → newest input ordering for last-writer-wins stability.
        var ordered = new List<SegmentMetadata<TKey>>(picked.Count);
        foreach (var metadata in segments)
        {
            if (picked.Contains(metadata))
            {
                ordered.Add(metadata);
            }
        }
        return ordered;
    }

    private static bool RangesOverlap(TKey aMin, TKey aMax, TKey bMin, TKey bMax)
        // An empty segment carries default (null for reference key types) bounds and has no
        // key range, so it cannot overlap anything — treat a null-bounded operand as
        // non-overlapping rather than dereferencing it (NullReferenceException).
        => aMin is not null && aMax is not null && bMin is not null && bMax is not null
            && aMin.CompareTo(bMax) <= 0 && aMax.CompareTo(bMin) >= 0;

    /// <summary>L_i size cap in bytes. L0 is count-triggered so it returns 0 (unused).</summary>
    internal long LevelSizeCapBytes(int level)
    {
        if (level <= 0)
        {
            return 0;
        }

        var capBytes = (long)BaseLevelSizeMB * 1024L * 1024L;
        for (var i = 1; i < level; i++)
        {
            // Guard against long overflow: if the next multiplication would exceed long.MaxValue,
            // saturate at long.MaxValue. At multiplier=10, overflow occurs around level 20+;
            // no compaction path can meaningfully distinguish a level at 10^20 bytes vs. capped.
            if (capBytes > long.MaxValue / LevelSizeMultiplier)
            {
                return long.MaxValue;
            }
            capBytes *= LevelSizeMultiplier;
        }
        return capBytes;
    }
}
