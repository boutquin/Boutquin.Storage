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
/// Unit tests for the FullCompactionStrategy class.
/// Each test follows the Arrange-Act-Assert pattern.
/// </summary>
public sealed class FullCompactionStrategyTests
{
    /// <summary>
    /// Test that ShouldCompact returns false when segment count is below threshold.
    /// </summary>
    [Fact]
    public void ShouldCompact_ReturnsFalse_BelowThreshold()
    {
        // Arrange
        var strategy = new FullCompactionStrategy(threshold: 4);

        // Assert
        Assert.False(strategy.ShouldCompact(3));
    }

    /// <summary>
    /// Test that ShouldCompact returns true when segment count equals threshold.
    /// </summary>
    [Fact]
    public void ShouldCompact_ReturnsTrue_AtThreshold()
    {
        // Arrange
        var strategy = new FullCompactionStrategy(threshold: 4);

        // Assert
        Assert.True(strategy.ShouldCompact(4));
    }

    /// <summary>
    /// Test that ShouldCompact returns true when segment count exceeds threshold.
    /// </summary>
    [Fact]
    public void ShouldCompact_ReturnsTrue_AboveThreshold()
    {
        // Arrange
        var strategy = new FullCompactionStrategy(threshold: 3);

        // Assert
        Assert.True(strategy.ShouldCompact(10));
    }

    /// <summary>
    /// Test that SelectSegments returns all segments (full compaction merges everything).
    /// </summary>
    [Fact]
    public void SelectSegments_ReturnsAllSegments()
    {
        // Arrange
        var strategy = new FullCompactionStrategy(threshold: 3);

        // Act
        var segments = strategy.SelectSegments(5);

        // Assert — full compaction always selects ALL segments
        Assert.Equal(5, segments.Count);
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, segments);
    }

    /// <summary>
    /// Test that SelectSegments with 0 segments returns empty list.
    /// </summary>
    [Fact]
    public void SelectSegments_WithZeroSegments_ReturnsEmpty()
    {
        // Arrange
        var strategy = new FullCompactionStrategy(threshold: 2);

        // Act
        var segments = strategy.SelectSegments(0);

        // Assert
        Assert.Empty(segments);
    }

    /// <summary>
    /// Test that SelectSegments with 1 segment returns single segment.
    /// </summary>
    [Fact]
    public void SelectSegments_WithOneSegment_ReturnsSingleSegment()
    {
        // Arrange
        var strategy = new FullCompactionStrategy(threshold: 2);

        // Act
        var segments = strategy.SelectSegments(1);

        // Assert
        Assert.Single(segments);
        Assert.Equal(0, segments[0]);
    }

    /// <summary>
    /// Test that constructor throws for threshold less than 2.
    /// </summary>
    [Fact]
    public void Constructor_ThresholdLessThan2_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FullCompactionStrategy(threshold: 1));
    }

    /// <summary>
    /// Test that constructor throws for threshold of 0.
    /// </summary>
    [Fact]
    public void Constructor_ThresholdZero_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FullCompactionStrategy(threshold: 0));
    }

    /// <summary>
    /// Test that constructor throws for negative threshold.
    /// </summary>
    [Fact]
    public void Constructor_NegativeThreshold_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FullCompactionStrategy(threshold: -5));
    }

    /// <summary>
    /// Test that threshold of 2 (minimum valid) works correctly.
    /// </summary>
    [Fact]
    public void Constructor_MinimumValidThreshold_Works()
    {
        // Arrange & Act
        var strategy = new FullCompactionStrategy(threshold: 2);

        // Assert
        Assert.False(strategy.ShouldCompact(1));
        Assert.True(strategy.ShouldCompact(2));
    }

    /// <summary>
    /// Test that SelectSegments returns contiguous zero-based indices.
    /// </summary>
    [Fact]
    public void SelectSegments_ReturnsContiguousZeroBasedIndices()
    {
        // Arrange
        var strategy = new FullCompactionStrategy(threshold: 2);

        // Act
        var segments = strategy.SelectSegments(100);

        // Assert — indices should be 0, 1, 2, ..., 99
        for (var i = 0; i < 100; i++)
        {
            Assert.Equal(i, segments[i]);
        }
    }
}
