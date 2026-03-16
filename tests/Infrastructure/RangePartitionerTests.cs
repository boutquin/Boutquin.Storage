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
using Boutquin.Storage.Infrastructure.Partitioning;

namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// Unit tests for the RangePartitioner class.
/// Each test follows the Arrange-Act-Assert pattern.
/// </summary>
public sealed class RangePartitionerTests
{
    /// <summary>
    /// Test that GetPartition returns partition 0 for a key below the first boundary.
    /// </summary>
    [Fact]
    public void GetPartition_KeyBelowFirstBoundary_ReturnsPartitionZero()
    {
        // Arrange — boundaries [10, 20, 30] → partitions 0..3
        var partitioner = new RangePartitioner<int>([10, 20, 30]);

        // Act
        var partition = partitioner.GetPartition(5);

        // Assert
        Assert.Equal(0, partition);
    }

    /// <summary>
    /// Test that GetPartition returns correct partition for a key between boundaries.
    /// </summary>
    [Fact]
    public void GetPartition_KeyBetweenBoundaries_ReturnsCorrectPartition()
    {
        // Arrange
        var partitioner = new RangePartitioner<int>([10, 20, 30]);

        // Act
        var partition = partitioner.GetPartition(15);

        // Assert
        Assert.Equal(1, partition);
    }

    /// <summary>
    /// Test that GetPartition returns the last partition for a key above the last boundary.
    /// </summary>
    [Fact]
    public void GetPartition_KeyAboveLastBoundary_ReturnsLastPartition()
    {
        // Arrange
        var partitioner = new RangePartitioner<int>([10, 20, 30]);

        // Act
        var partition = partitioner.GetPartition(35);

        // Assert
        Assert.Equal(3, partition);
    }

    /// <summary>
    /// Test that a key equal to a boundary value falls into the next partition
    /// (boundary is the lower bound of the next partition).
    /// </summary>
    [Fact]
    public void GetPartition_KeyEqualsToBoundary_ReturnsNextPartition()
    {
        // Arrange
        var partitioner = new RangePartitioner<int>([10, 20, 30]);

        // Act
        var partition = partitioner.GetPartition(20);

        // Assert — boundary 20 is the start of partition 2
        Assert.Equal(2, partition);
    }

    /// <summary>
    /// Test that GetPartitionRange returns the correct span of partitions for a range query.
    /// </summary>
    [Fact]
    public void GetPartitionRange_ReturnsCorrectSpanOfPartitions()
    {
        // Arrange
        var partitioner = new RangePartitioner<int>([10, 20, 30]);

        // Act — range [5, 25] spans partitions 0..2
        var (start, end) = partitioner.GetPartitionRange(5, 25);

        // Assert
        Assert.Equal(0, start);
        Assert.Equal(2, end);
    }

    /// <summary>
    /// Test that GetPartitionRange for a range within a single partition returns same start and end.
    /// </summary>
    [Fact]
    public void GetPartitionRange_WithinSinglePartition_ReturnsSameStartAndEnd()
    {
        // Arrange
        var partitioner = new RangePartitioner<int>([10, 20, 30]);

        // Act — range [12, 18] is entirely within partition 1
        var (start, end) = partitioner.GetPartitionRange(12, 18);

        // Assert
        Assert.Equal(1, start);
        Assert.Equal(1, end);
    }

    /// <summary>
    /// Test that GetBoundaries returns the sorted boundary keys.
    /// </summary>
    [Fact]
    public void GetBoundaries_ReturnsSortedBoundaryKeys()
    {
        // Arrange
        var boundaries = new[] { 10, 20, 30 };
        var partitioner = new RangePartitioner<int>(boundaries);

        // Act
        var result = partitioner.GetBoundaries();

        // Assert
        Assert.Equal(boundaries, result);
    }

    /// <summary>
    /// Test that PartitionCount equals boundaries count + 1.
    /// </summary>
    [Fact]
    public void PartitionCount_EqualsBoundariesCountPlusOne()
    {
        // Arrange
        var partitioner = new RangePartitioner<int>([10, 20, 30]);

        // Act & Assert
        Assert.Equal(4, partitioner.PartitionCount);
    }

    /// <summary>
    /// Test that an empty boundaries list throws ArgumentException.
    /// </summary>
    [Fact]
    public void Constructor_EmptyBoundaries_ThrowsArgumentException()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => new RangePartitioner<int>([]));
    }

    /// <summary>
    /// Test that unsorted boundaries throw ArgumentException.
    /// </summary>
    [Fact]
    public void Constructor_UnsortedBoundaries_ThrowsArgumentException()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => new RangePartitioner<int>([30, 10, 20]));
    }

    /// <summary>
    /// Test that duplicate boundaries throw ArgumentException.
    /// </summary>
    [Fact]
    public void Constructor_DuplicateBoundaries_ThrowsArgumentException()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => new RangePartitioner<int>([10, 10, 20]));
    }

    /// <summary>
    /// Test with string keys to verify generic constraint works.
    /// </summary>
    [Fact]
    public void GetPartition_WithStringKeys_ReturnsCorrectPartition()
    {
        // Arrange — boundaries ["d", "m", "t"]
        var partitioner = new RangePartitioner<string>(["d", "m", "t"]);

        // Act & Assert
        Assert.Equal(0, partitioner.GetPartition("abc"));
        Assert.Equal(1, partitioner.GetPartition("hello"));
        Assert.Equal(2, partitioner.GetPartition("nope"));
        Assert.Equal(3, partitioner.GetPartition("xyz"));
    }

    /// <summary>
    /// Test that GetPartitionRange spanning all partitions returns full range.
    /// </summary>
    [Fact]
    public void GetPartitionRange_SpanningAllPartitions_ReturnsFullRange()
    {
        // Arrange
        var partitioner = new RangePartitioner<int>([10, 20, 30]);

        // Act
        var (start, end) = partitioner.GetPartitionRange(1, 100);

        // Assert
        Assert.Equal(0, start);
        Assert.Equal(3, end);
    }
}
