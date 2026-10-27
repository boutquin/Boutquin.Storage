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
/// Unit tests for <see cref="SizeTieredCompactionStrategy{TKey}"/> — Phase B4 real
/// tier-bucketing behaviour. Each <see cref="SegmentMetadata{TKey}.SizeBytes"/> maps to a tier
/// under the <c>TierBaseBytes × TierMultiplier^i</c> schedule; a tier with at least
/// <c>MinSegmentsPerTier</c> members is merged.
/// </summary>
public sealed class SizeTieredCompactionStrategyTests
{
    private static SegmentMetadata<int> Seg(long size) =>
        new(Level: 0, MinKey: 0, MaxKey: 0, SizeBytes: size, Crc32C: 0u);

    [Fact]
    public void Constructor_MinSegmentsLessThan2_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SizeTieredCompactionStrategy<int>(minSegments: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SizeTieredCompactionStrategy<int>(minSegments: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SizeTieredCompactionStrategy<int>(minSegments: -3));
    }

    [Fact]
    public void Constructor_MultiplierLessThan2_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SizeTieredCompactionStrategy<int>(tierMultiplier: 1));
    }

    [Fact]
    public void Constructor_TierBaseBytesLessThan1_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SizeTieredCompactionStrategy<int>(tierBaseBytes: 0));
    }

    [Fact]
    public void ShouldCompact_SingleTierAboveThreshold_True()
    {
        var strategy = new SizeTieredCompactionStrategy<int>(
            minSegments: 4, tierMultiplier: 4, tierBaseBytes: 1024);

        // 4 segments, all at ~1 KiB — same tier.
        var segments = new[] { Seg(1024), Seg(1024), Seg(1024), Seg(1024) };

        strategy.ShouldCompact(segments).Should().BeTrue();
    }

    [Fact]
    public void ShouldCompact_NoTierReachesThreshold_False()
    {
        var strategy = new SizeTieredCompactionStrategy<int>(
            minSegments: 4, tierMultiplier: 4, tierBaseBytes: 1024);

        // Spread across four tiers, 1 per tier — nothing to merge yet.
        var segments = new[]
        {
            Seg(1024),         // tier 0
            Seg(1024 * 4),     // tier 1
            Seg(1024 * 16),    // tier 2
            Seg(1024 * 64),    // tier 3
        };

        strategy.ShouldCompact(segments).Should().BeFalse();
    }

    [Fact]
    public void SelectSegments_PicksSmallestFullTier()
    {
        var strategy = new SizeTieredCompactionStrategy<int>(
            minSegments: 3, tierMultiplier: 4, tierBaseBytes: 1024);

        // Both tier 0 and tier 2 have 3 members. Tier 0 should be picked (smallest first).
        var tier0A = Seg(500);
        var tier0B = Seg(1024);
        var tier0C = Seg(800);
        var tier2A = Seg(20_000);
        var tier2B = Seg(20_000);
        var tier2C = Seg(20_000);
        var segments = new[] { tier0A, tier2A, tier0B, tier2B, tier0C, tier2C };

        var selected = strategy.SelectSegments(segments);

        selected.Should().BeEquivalentTo([tier0A, tier0B, tier0C],
            because: "the smallest tier that reaches minSegmentsPerTier must merge first.");
    }

    [Fact]
    public void SelectSegments_BelowThreshold_ReturnsEmpty()
    {
        var strategy = new SizeTieredCompactionStrategy<int>(minSegments: 4);
        var segments = new[] { Seg(100), Seg(100) };

        var selected = strategy.SelectSegments(segments);

        selected.Should().BeEmpty();
    }

    [Fact]
    public void SelectSegments_PreservesInputOrder()
    {
        var strategy = new SizeTieredCompactionStrategy<int>(
            minSegments: 3, tierMultiplier: 4, tierBaseBytes: 1024);

        var a = Seg(1024);
        var b = Seg(1024);
        var c = Seg(1024);
        var segments = new[] { a, b, c };

        var selected = strategy.SelectSegments(segments);

        selected.Should().Equal(a, b, c);
    }

    // ---- F-70 / AC-6.6: exact integer-loop tiering (no Math.Log FP rounding) ----

    /// <summary>
    /// At an EXACT power of the multiplier the floating-point form
    /// <c>floor(log(size/base) / log(mult))</c> can return one less than the true tier:
    /// <c>Math.Log(2^n)/Math.Log(2)</c> evaluates to <c>n - ε</c>, so <c>Math.Floor</c>
    /// drops to <c>n-1</c>. The exact integer-loop form must place a segment of size
    /// <c>base·mult^n</c> in tier <c>n</c>, not <c>n-1</c>. (F-70)
    /// </summary>
    [Theory]
    [InlineData(1L, 2, 2L, 1)]     // base 1, mult 2: 2 == 2^1 → tier 1
    [InlineData(1L, 2, 4L, 2)]     // 4 == 2^2 → tier 2
    [InlineData(1L, 2, 8L, 3)]     // 8 == 2^3 → tier 3
    [InlineData(1L, 2, 16L, 4)]    // 16 == 2^4 → tier 4
    [InlineData(1L, 2, 1024L, 10)] // 1024 == 2^10 → tier 10
    [InlineData(1L, 10, 1000L, 3)] // 1000 == 10^3 → tier 3
    [InlineData(1024L, 4, 16384L, 2)] // base·mult^2 == 1024·16 → tier 2
    public void TierOf_ExactPowerOfMultiplier_LandsInExactTier(
        long tierBase, int multiplier, long sizeBytes, int expectedTier)
    {
        var strategy = new SizeTieredCompactionStrategy<int>(
            minSegments: 2, tierMultiplier: multiplier, tierBaseBytes: tierBase);

        strategy.TierOf(sizeBytes).Should().Be(expectedTier,
            because: "an exact power of the multiplier must not be mis-tiered by FP log rounding.");
    }

    /// <summary>
    /// Spot-checks the in-tier (non-boundary) and below-base cases against the exact
    /// integer-loop schedule, so the boundary fix does not regress ordinary bucketing.
    /// </summary>
    [Theory]
    [InlineData(1024L, 4, 500L, 0)]    // below base → tier 0
    [InlineData(1024L, 4, 1024L, 0)]   // == base → tier 0 (range [base, base·mult))
    [InlineData(1024L, 4, 3000L, 0)]   // within tier 0 range [1024, 4096)
    [InlineData(1024L, 4, 5000L, 1)]   // within tier 1 range [4096, 16384)
    [InlineData(1024L, 4, 20000L, 2)]  // within tier 2 range [16384, 65536)
    public void TierOf_InTierAndBelowBase_MatchesExactSchedule(
        long tierBase, int multiplier, long sizeBytes, int expectedTier)
    {
        var strategy = new SizeTieredCompactionStrategy<int>(
            minSegments: 2, tierMultiplier: multiplier, tierBaseBytes: tierBase);

        strategy.TierOf(sizeBytes).Should().Be(expectedTier);
    }
}
