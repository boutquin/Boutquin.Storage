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

namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// Unit tests for <see cref="LeveledCompactionStrategy{TKey}"/> — Phase B3. Validates the
/// DDIA 2e p. 124 contract: L0 triggers on count, higher levels trigger on size cap, picked
/// set bridges L_i → L_(i+1) using key-range overlap.
/// </summary>
public sealed class LeveledCompactionStrategyTests
{
    private static SegmentMetadata<int> Seg(int level, int min, int max, long size = 1024) =>
        new(Level: level, MinKey: min, MaxKey: max, SizeBytes: size, Crc32C: 0u);

    [Fact]
    public void Constructor_ThresholdLessThan2_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LeveledCompactionStrategy<int>(level0Threshold: 1));
    }

    [Fact]
    public void Constructor_MultiplierLessThan2_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LeveledCompactionStrategy<int>(levelSizeMultiplier: 1));
    }

    [Fact]
    public void Constructor_BaseLevelSizeLessThan1_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LeveledCompactionStrategy<int>(baseLevelSizeMB: 0));
    }

    [Fact]
    public void Defaults_MatchLevelDb()
    {
        var strategy = new LeveledCompactionStrategy<int>();

        strategy.Level0Threshold.Should().Be(4);
        strategy.LevelSizeMultiplier.Should().Be(10);
        strategy.BaseLevelSizeMB.Should().Be(10);
    }

    [Fact]
    public void ShouldCompact_FewL0Segments_False()
    {
        var strategy = new LeveledCompactionStrategy<int>(level0Threshold: 4);

        var segments = new[]
        {
            Seg(level: 0, min: 0, max: 100),
            Seg(level: 0, min: 50, max: 150),
        };

        strategy.ShouldCompact(segments).Should().BeFalse();
    }

    [Fact]
    public void ShouldCompact_L0ReachedThreshold_True()
    {
        var strategy = new LeveledCompactionStrategy<int>(level0Threshold: 2);

        var segments = new[]
        {
            Seg(level: 0, min: 0, max: 100),
            Seg(level: 0, min: 50, max: 150),
        };

        strategy.ShouldCompact(segments).Should().BeTrue();
    }

    [Fact]
    public void SelectSegments_L0Trigger_PicksOldestL0PlusOverlappingL1()
    {
        var strategy = new LeveledCompactionStrategy<int>(level0Threshold: 2);

        var l0a = Seg(level: 0, min: 10, max: 50);   // oldest L0
        var l0b = Seg(level: 0, min: 200, max: 250); // second L0 — non-overlapping
        var l1Overlap = Seg(level: 1, min: 30, max: 70);
        var l1Miss = Seg(level: 1, min: 500, max: 600);

        var segments = new[] { l0a, l0b, l1Overlap, l1Miss };

        var selected = strategy.SelectSegments(segments);

        selected.Should().Contain(l0a);
        selected.Should().Contain(l1Overlap);
        selected.Should().NotContain(l1Miss);
    }

    [Fact]
    public void SelectSegments_L0Trigger_FoldsOverlappingL0SegmentsTogether()
    {
        var strategy = new LeveledCompactionStrategy<int>(level0Threshold: 2);

        // All three L0 segments overlap transitively via the middle one.
        var l0a = Seg(level: 0, min: 0, max: 40);
        var l0b = Seg(level: 0, min: 30, max: 80);
        var l0c = Seg(level: 0, min: 70, max: 120);

        var segments = new[] { l0a, l0b, l0c };

        var selected = strategy.SelectSegments(segments);

        selected.Should().BeEquivalentTo([l0a, l0b, l0c],
            because: "L0 treats transitively-overlapping ranges as one merge group.");
    }

    [Fact]
    public void SelectSegments_HigherLevelOverflow_PicksLevelPlusOverlappingNext()
    {
        // Only L1 is over-sized (cap = 1 MiB × 1 = 1 MiB); L2 cap = 10 MiB.
        var strategy = new LeveledCompactionStrategy<int>(
            level0Threshold: 100, levelSizeMultiplier: 10, baseLevelSizeMB: 1);

        var l1Big = Seg(level: 1, min: 100, max: 200, size: 2L * 1024 * 1024); // 2 MiB
        var l2Overlap = Seg(level: 2, min: 150, max: 250, size: 5L * 1024 * 1024);
        var l2Miss = Seg(level: 2, min: 1000, max: 1100, size: 5L * 1024 * 1024);

        var segments = new[] { l1Big, l2Overlap, l2Miss };

        var selected = strategy.SelectSegments(segments);

        selected.Should().Contain(l1Big);
        selected.Should().Contain(l2Overlap);
        selected.Should().NotContain(l2Miss);
    }

    [Fact]
    public void SelectSegments_BelowTriggerCriteria_ReturnsEmpty()
    {
        var strategy = new LeveledCompactionStrategy<int>(level0Threshold: 4);

        var segments = new[]
        {
            Seg(level: 0, min: 0, max: 100, size: 100),
            Seg(level: 1, min: 0, max: 100, size: 100),
        };

        strategy.SelectSegments(segments).Should().BeEmpty();
    }

    [Fact]
    public void SelectSegments_PreservesOldestToNewestOrder()
    {
        var strategy = new LeveledCompactionStrategy<int>(level0Threshold: 2);

        var l0a = Seg(level: 0, min: 10, max: 50);
        var l0b = Seg(level: 0, min: 20, max: 60);
        var l1a = Seg(level: 1, min: 15, max: 45);

        var segments = new[] { l0a, l0b, l1a };

        var selected = strategy.SelectSegments(segments);

        selected.Should().Equal([l0a, l0b, l1a],
            because: "SelectSegments must emit input order so last-writer-wins stays stable.");
    }

    [Fact]
    public void SelectSegments_EmptySegmentWithNullBounds_DoesNotThrow()
    {
        // An empty segment carries default (null) Min/Max key bounds for a reference key
        // type. Such a segment must not cause PickForLevel to dereference a null key during
        // range-overlap detection — it has no key range to overlap, so it participates only
        // as the trigger source (when it is the oldest) and is skipped as an overlap candidate.
        var strategy = new LeveledCompactionStrategy<string>(level0Threshold: 2);

        var l0Real = new SegmentMetadata<string>(
            Level: 0, MinKey: "a", MaxKey: "m", SizeBytes: 1024L, Crc32C: 0u);
        var l0Empty = new SegmentMetadata<string>(
            Level: 0, MinKey: default!, MaxKey: default!, SizeBytes: 0L, Crc32C: 0u);
        var l1 = new SegmentMetadata<string>(
            Level: 1, MinKey: "b", MaxKey: "f", SizeBytes: 1024L, Crc32C: 0u);

        // l0Real is oldest (the trigger source). l0Empty is a later L0 candidate whose null
        // MinKey is dereferenced by RangesOverlap during the transitive-overlap sweep.
        var segments = new[] { l0Real, l0Empty, l1 };

        // Before the fix this NREs inside RangesOverlap when the null-bounded segment's keys
        // are compared. After the fix it returns a sensible candidate set without throwing.
        var act = () => strategy.SelectSegments(segments);

        act.Should().NotThrow();
    }

    [Fact]
    public void PickForLevel_EqualMetadataSegments_AreNotCollapsed()
    {
        // Two L0 segments with identical metadata (same level, key range, size, CRC) must both
        // appear in the picked set. A HashSet-based implementation collapses them to one because
        // record-struct equality treats equal values as the same element — the second flush's
        // data would be silently dropped from the compaction input.
        var strategy = new LeveledCompactionStrategy<int>(level0Threshold: 2);

        var seg1 = new SegmentMetadata<int>(Level: 0, MinKey: 1, MaxKey: 10, SizeBytes: 1024L, Crc32C: 0u);
        var seg2 = new SegmentMetadata<int>(Level: 0, MinKey: 1, MaxKey: 10, SizeBytes: 1024L, Crc32C: 0u);
        var segments = new[] { seg1, seg2 };

        var picked = strategy.SelectSegments(segments);

        // Both segments must be returned; collapsing to 1 silently drops one flush's data.
        picked.Should().HaveCount(2);
    }

    [Fact]
    public void LevelSizeCapBytes_FollowsLevelDbSchedule()
    {
        var strategy = new LeveledCompactionStrategy<int>(
            levelSizeMultiplier: 10, baseLevelSizeMB: 10);

        strategy.LevelSizeCapBytes(0).Should().Be(0);
        strategy.LevelSizeCapBytes(1).Should().Be(10L * 1024 * 1024);
        strategy.LevelSizeCapBytes(2).Should().Be(100L * 1024 * 1024);
        strategy.LevelSizeCapBytes(3).Should().Be(1_000L * 1024 * 1024);
    }

    [Fact]
    public void SelectSegments_TwoSegmentsWithIdenticalMetadata_BothSelected()
    {
        // Two L0 segments with identical field values (same MinKey/MaxKey/Level/SizeBytes) — a
        // HashSet<SegmentMetadata<TKey>> collapses them to one entry because record-struct equality
        // is value-based, silently dropping one from compaction. Index-based tracking avoids this.
        var strategy = new LeveledCompactionStrategy<string>(level0Threshold: 2);

        var seg1 = new SegmentMetadata<string>(Level: 0, MinKey: "a", MaxKey: "z", SizeBytes: 512L, Crc32C: 0u);
        var seg2 = new SegmentMetadata<string>(Level: 0, MinKey: "a", MaxKey: "z", SizeBytes: 512L, Crc32C: 0u);
        var segments = new[] { seg1, seg2 };

        var picked = strategy.SelectSegments(segments);

        picked.Should().HaveCount(2,
            "both segments must be returned; collapsing to one silently drops one flush's data from compaction");
    }

    [Fact]
    public void LevelSizeCapBytes_VeryHighLevel_ReturnsLongMaxValueWithoutException()
    {
        // With multiplier=10 and baseLevelSizeMB=10, LevelSizeCapBytes overflows long at level ~20
        // (10 MiB × 10^19 > long.MaxValue). Before the fix, the checked arithmetic block throws
        // OverflowException; after the fix it saturates at long.MaxValue.
        var strategy = new LeveledCompactionStrategy<int>(
            levelSizeMultiplier: 10, baseLevelSizeMB: 10);

        var act = () => strategy.LevelSizeCapBytes(25);

        act.Should().NotThrow<OverflowException>("overflow must be clamped to long.MaxValue");
        strategy.LevelSizeCapBytes(25).Should().Be(long.MaxValue);
    }
}
