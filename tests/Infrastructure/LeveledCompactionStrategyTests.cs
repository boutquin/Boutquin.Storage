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
namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// Unit tests for the LeveledCompactionStrategy class.
/// Each test follows the Arrange-Act-Assert pattern.
/// </summary>
public sealed class LeveledCompactionStrategyTests
{
    /// <summary>
    /// Test that ShouldCompact returns false below threshold.
    /// </summary>
    [Fact]
    public void ShouldCompact_ReturnsFalse_BelowThreshold()
    {
        // Arrange
        var strategy = new LeveledCompactionStrategy(level0Threshold: 4);

        // Assert
        Assert.False(strategy.ShouldCompact(3));
    }

    /// <summary>
    /// Test that ShouldCompact returns true at threshold.
    /// </summary>
    [Fact]
    public void ShouldCompact_ReturnsTrue_AtThreshold()
    {
        // Arrange
        var strategy = new LeveledCompactionStrategy(level0Threshold: 4);

        // Assert
        Assert.True(strategy.ShouldCompact(4));
    }

    /// <summary>
    /// Test that ShouldCompact returns true above threshold.
    /// </summary>
    [Fact]
    public void ShouldCompact_ReturnsTrue_AboveThreshold()
    {
        // Arrange
        var strategy = new LeveledCompactionStrategy(level0Threshold: 4);

        // Assert
        Assert.True(strategy.ShouldCompact(10));
    }

    /// <summary>
    /// Test that SelectSegments returns Level 0 segments.
    /// </summary>
    [Fact]
    public void SelectSegments_ReturnsLevel0Segments()
    {
        // Arrange
        var strategy = new LeveledCompactionStrategy(level0Threshold: 4);

        // Act
        var segments = strategy.SelectSegments(6);

        // Assert — should select up to threshold count (4)
        Assert.Equal(4, segments.Count);
        Assert.Equal(new[] { 0, 1, 2, 3 }, segments);
    }

    /// <summary>
    /// Test that constructor throws for threshold less than 2.
    /// </summary>
    [Fact]
    public void Constructor_ThresholdLessThan2_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LeveledCompactionStrategy(level0Threshold: 1));
    }

    /// <summary>
    /// Test that constructor throws for multiplier less than 2.
    /// </summary>
    [Fact]
    public void Constructor_MultiplierLessThan2_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LeveledCompactionStrategy(levelSizeMultiplier: 1));
    }

    /// <summary>
    /// Test that default parameters work correctly.
    /// </summary>
    [Fact]
    public void DefaultParameters_WorkCorrectly()
    {
        // Arrange
        var strategy = new LeveledCompactionStrategy();

        // Assert — defaults: threshold=4, multiplier=10, baseSizeMB=10
        Assert.False(strategy.ShouldCompact(3));
        Assert.True(strategy.ShouldCompact(4));
        Assert.Equal(10, strategy.LevelSizeMultiplier);
        Assert.Equal(10, strategy.BaseLevelSizeMB);
    }

    /// <summary>
    /// Test SelectSegments when segment count is less than threshold.
    /// </summary>
    [Fact]
    public void SelectSegments_WhenBelowThreshold_ReturnsAllSegments()
    {
        // Arrange
        var strategy = new LeveledCompactionStrategy(level0Threshold: 4);

        // Act
        var segments = strategy.SelectSegments(2);

        // Assert
        Assert.Equal(2, segments.Count);
        Assert.Equal(new[] { 0, 1 }, segments);
    }

    // ========== Tier 3: Behavioral difference from SizeTieredCompactionStrategy ==========

    /// <summary>
    /// Test that LeveledCompactionStrategy caps selected segments at threshold,
    /// unlike SizeTieredCompactionStrategy which selects all segments.
    /// </summary>
    [Fact]
    public void SelectSegments_CapsAtThreshold_UnlikeSizeTiered()
    {
        // Arrange
        var leveled = new LeveledCompactionStrategy(level0Threshold: 4);
        var sizeTiered = new SizeTieredCompactionStrategy(minSegments: 4);

        // Act — both with 10 segments
        var leveledSegments = leveled.SelectSegments(10);
        var sizeTieredSegments = sizeTiered.SelectSegments(10);

        // Assert — leveled caps at threshold (4), size-tiered selects all (10)
        Assert.Equal(4, leveledSegments.Count);
        Assert.Equal(10, sizeTieredSegments.Count);
    }
}
