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
/// Unit tests for the HashPartitioner class.
/// Each test follows the Arrange-Act-Assert pattern.
/// </summary>
public sealed class HashPartitionerTests
{
    /// <summary>
    /// Test that GetPartition returns a value in [0, partitionCount).
    /// </summary>
    [Fact]
    public void GetPartition_ReturnsValueInValidRange()
    {
        // Arrange
        var partitioner = new HashPartitioner<string>(5);

        // Act
        var partition = partitioner.GetPartition("testKey");

        // Assert
        Assert.InRange(partition, 0, 4);
    }

    /// <summary>
    /// Test that the same key always maps to the same partition.
    /// </summary>
    [Fact]
    public void GetPartition_SameKey_ReturnsSamePartition()
    {
        // Arrange
        var partitioner = new HashPartitioner<string>(10);

        // Act
        var partition1 = partitioner.GetPartition("consistentKey");
        var partition2 = partitioner.GetPartition("consistentKey");

        // Assert
        Assert.Equal(partition1, partition2);
    }

    /// <summary>
    /// Test that keys distribute across partitions (statistical: 1000 keys, all partitions used).
    /// </summary>
    [Fact]
    public void GetPartition_ManyKeys_DistributeAcrossPartitions()
    {
        // Arrange
        const int partitionCount = 5;
        var partitioner = new HashPartitioner<string>(partitionCount);
        var partitionsUsed = new HashSet<int>();

        // Act — hash 1000 distinct keys
        for (var i = 0; i < 1000; i++)
        {
            partitionsUsed.Add(partitioner.GetPartition($"key-{i}"));
        }

        // Assert — all partitions should be hit
        Assert.Equal(partitionCount, partitionsUsed.Count);
    }

    /// <summary>
    /// Test that PartitionCount matches the constructor parameter.
    /// </summary>
    [Fact]
    public void PartitionCount_MatchesConstructorParameter()
    {
        // Arrange & Act
        var partitioner = new HashPartitioner<int>(7);

        // Assert
        Assert.Equal(7, partitioner.PartitionCount);
    }

    /// <summary>
    /// Test that partitionCount less than 1 throws ArgumentOutOfRangeException.
    /// </summary>
    [Fact]
    public void Constructor_PartitionCountLessThanOne_ThrowsArgumentOutOfRangeException()
    {
        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => new HashPartitioner<string>(0));
    }

    /// <summary>
    /// Test that a negative partition count throws ArgumentOutOfRangeException.
    /// </summary>
    [Fact]
    public void Constructor_NegativePartitionCount_ThrowsArgumentOutOfRangeException()
    {
        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => new HashPartitioner<string>(-1));
    }

    /// <summary>
    /// Test that a custom hash algorithm is used when provided.
    /// </summary>
    [Fact]
    public void GetPartition_CustomHashAlgorithm_IsUsed()
    {
        // Arrange — use Fnv1a instead of the default Murmur3
        var fnv = new Fnv1aHash();
        var partitioner = new HashPartitioner<string>(10, fnv);

        // Act — should produce a valid partition
        var partition = partitioner.GetPartition("testKey");

        // Assert
        Assert.InRange(partition, 0, 9);
    }

    /// <summary>
    /// Test that different hash algorithms can produce different partition assignments.
    /// </summary>
    [Fact]
    public void GetPartition_DifferentHashAlgorithms_MayProduceDifferentPartitions()
    {
        // Arrange
        var murmurPartitioner = new HashPartitioner<string>(100);
        var fnvPartitioner = new HashPartitioner<string>(100, new Fnv1aHash());

        // Act — check many keys; at least some should differ
        var differenceFound = false;
        for (var i = 0; i < 100; i++)
        {
            var key = $"key-{i}";
            if (murmurPartitioner.GetPartition(key) != fnvPartitioner.GetPartition(key))
            {
                differenceFound = true;
                break;
            }
        }

        // Assert
        Assert.True(differenceFound, "Different hash algorithms should produce different partition assignments for at least some keys.");
    }

    /// <summary>
    /// Test with integer keys to verify generic constraint works.
    /// </summary>
    [Fact]
    public void GetPartition_WithIntegerKeys_ReturnsValidPartition()
    {
        // Arrange
        var partitioner = new HashPartitioner<int>(5);

        // Act
        var partition = partitioner.GetPartition(42);

        // Assert
        Assert.InRange(partition, 0, 4);
    }
}
