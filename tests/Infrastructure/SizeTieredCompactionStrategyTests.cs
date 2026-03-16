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
/// Unit tests for the SizeTieredCompactionStrategy class.
/// Each test follows the Arrange-Act-Assert pattern.
/// </summary>
public sealed class SizeTieredCompactionStrategyTests
{
    /// <summary>
    /// Test that ShouldCompact returns false when segment count is below minimum.
    /// </summary>
    [Fact]
    public void ShouldCompact_ReturnsFalse_BelowMinSegments()
    {
        // Arrange
        var strategy = new SizeTieredCompactionStrategy(minSegments: 4);

        // Assert
        Assert.False(strategy.ShouldCompact(3));
    }

    /// <summary>
    /// Test that ShouldCompact returns true when segment count equals minimum.
    /// </summary>
    [Fact]
    public void ShouldCompact_ReturnsTrue_AtMinSegments()
    {
        // Arrange
        var strategy = new SizeTieredCompactionStrategy(minSegments: 4);

        // Assert
        Assert.True(strategy.ShouldCompact(4));
    }

    /// <summary>
    /// Test that ShouldCompact returns true when segment count exceeds minimum.
    /// </summary>
    [Fact]
    public void ShouldCompact_ReturnsTrue_AboveMinSegments()
    {
        // Arrange
        var strategy = new SizeTieredCompactionStrategy(minSegments: 4);

        // Assert
        Assert.True(strategy.ShouldCompact(10));
    }

    /// <summary>
    /// Test that SelectSegments returns all segments (simplified STCS merges everything).
    /// </summary>
    [Fact]
    public void SelectSegments_ReturnsAllSegments()
    {
        // Arrange
        var strategy = new SizeTieredCompactionStrategy(minSegments: 4);

        // Act
        var segments = strategy.SelectSegments(6);

        // Assert — simplified STCS selects ALL segments
        Assert.Equal(6, segments.Count);
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, segments);
    }

    /// <summary>
    /// Test that SelectSegments with zero segments returns empty.
    /// </summary>
    [Fact]
    public void SelectSegments_WithZeroSegments_ReturnsEmpty()
    {
        // Arrange
        var strategy = new SizeTieredCompactionStrategy(minSegments: 2);

        // Act
        var segments = strategy.SelectSegments(0);

        // Assert
        Assert.Empty(segments);
    }

    /// <summary>
    /// Test that constructor throws for minSegments less than 2.
    /// </summary>
    [Fact]
    public void Constructor_MinSegmentsLessThan2_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SizeTieredCompactionStrategy(minSegments: 1));
    }

    /// <summary>
    /// Test that constructor throws for zero minSegments.
    /// </summary>
    [Fact]
    public void Constructor_ZeroMinSegments_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SizeTieredCompactionStrategy(minSegments: 0));
    }

    /// <summary>
    /// Test that constructor throws for negative minSegments.
    /// </summary>
    [Fact]
    public void Constructor_NegativeMinSegments_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SizeTieredCompactionStrategy(minSegments: -3));
    }

    /// <summary>
    /// Test that default parameter (minSegments=4) works correctly.
    /// </summary>
    [Fact]
    public void DefaultMinSegments_IsCorrectlyApplied()
    {
        // Arrange — default minSegments is 4
        var strategy = new SizeTieredCompactionStrategy();

        // Assert
        Assert.False(strategy.ShouldCompact(3));
        Assert.True(strategy.ShouldCompact(4));
    }

    /// <summary>
    /// Test that minimum valid minSegments (2) works correctly.
    /// </summary>
    [Fact]
    public void Constructor_MinimumValidMinSegments_Works()
    {
        // Arrange & Act
        var strategy = new SizeTieredCompactionStrategy(minSegments: 2);

        // Assert
        Assert.False(strategy.ShouldCompact(1));
        Assert.True(strategy.ShouldCompact(2));
    }

    /// <summary>
    /// Test that STCS selects all segments unlike LeveledCompactionStrategy which caps at threshold.
    /// </summary>
    [Fact]
    public void SelectSegments_SelectsAll_UnlikeLeveled()
    {
        // Arrange
        var stcs = new SizeTieredCompactionStrategy(minSegments: 4);
        var leveled = new LeveledCompactionStrategy(level0Threshold: 4);

        // Act
        var stcsSegments = stcs.SelectSegments(10);
        var leveledSegments = leveled.SelectSegments(10);

        // Assert — STCS selects all (10), leveled caps at threshold (4)
        Assert.Equal(10, stcsSegments.Count);
        Assert.Equal(4, leveledSegments.Count);
    }
}
