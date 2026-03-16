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
/// Unit tests for the LamportTimestamp class.
/// Each test follows the Arrange-Act-Assert pattern.
/// </summary>
public sealed class LamportTimestampTests
{
    /// <summary>
    /// Test that Increment increases the counter.
    /// </summary>
    [Fact]
    public void Increment_IncreasesCounter()
    {
        // Arrange
        var clock = new LamportTimestamp("node-1");

        // Act
        var t1 = clock.Increment();
        var t2 = clock.Increment();

        // Assert
        Assert.Equal(1, t1);
        Assert.Equal(2, t2);
    }

    /// <summary>
    /// Test that Update with a higher timestamp advances the local counter past it.
    /// </summary>
    [Fact]
    public void Update_WithHigherTimestamp_AdvancesLocalCounter()
    {
        // Arrange
        var clock = new LamportTimestamp("node-1");
        clock.Increment(); // counter = 1

        // Act — receive timestamp 10 from another node
        var result = clock.Update(10);

        // Assert — should be max(1, 10) + 1 = 11
        Assert.Equal(11, result);
        Assert.Equal(11, clock.GetCurrentTimestamp());
    }

    /// <summary>
    /// Test that Update with a lower timestamp still increments the local counter.
    /// </summary>
    [Fact]
    public void Update_WithLowerTimestamp_StillIncrements()
    {
        // Arrange
        var clock = new LamportTimestamp("node-1", initialCounter: 10);

        // Act — receive timestamp 3 from another node
        var result = clock.Update(3);

        // Assert — should be max(10, 3) + 1 = 11
        Assert.Equal(11, result);
    }

    /// <summary>
    /// Test that CompareTo returns negative for lower timestamp.
    /// </summary>
    [Fact]
    public void CompareTo_LowerTimestamp_ReturnsNegative()
    {
        // Arrange
        var clock = new LamportTimestamp("node-1", initialCounter: 5);

        // Act — compare with higher timestamp
        var result = clock.CompareTo(10, "node-2");

        // Assert
        Assert.True(result < 0);
    }

    /// <summary>
    /// Test that CompareTo returns positive for higher timestamp.
    /// </summary>
    [Fact]
    public void CompareTo_HigherTimestamp_ReturnsPositive()
    {
        // Arrange
        var clock = new LamportTimestamp("node-1", initialCounter: 10);

        // Act — compare with lower timestamp
        var result = clock.CompareTo(5, "node-2");

        // Assert
        Assert.True(result > 0);
    }

    /// <summary>
    /// Test that CompareTo breaks ties by nodeId (lexicographic).
    /// </summary>
    [Fact]
    public void CompareTo_EqualTimestamps_BrokenByNodeId()
    {
        // Arrange
        var clock = new LamportTimestamp("node-a", initialCounter: 5);

        // Act — same timestamp, different node ID
        var result = clock.CompareTo(5, "node-b");

        // Assert — "node-a" < "node-b" lexicographically
        Assert.True(result < 0);
    }

    /// <summary>
    /// Test thread safety: concurrent increments don't lose updates.
    /// </summary>
    [Fact]
    public void Increment_ConcurrentIncrements_NoLostUpdates()
    {
        // Arrange
        var clock = new LamportTimestamp("node-1");
        const int threadCount = 100;
        const int incrementsPerThread = 1000;

        // Act
        Parallel.For(0, threadCount, _ =>
        {
            for (var i = 0; i < incrementsPerThread; i++)
            {
                clock.Increment();
            }
        });

        // Assert
        Assert.Equal(threadCount * incrementsPerThread, clock.GetCurrentTimestamp());
    }

    /// <summary>
    /// Test that GetCurrentTimestamp returns the latest value.
    /// </summary>
    [Fact]
    public void GetCurrentTimestamp_ReturnsLatestValue()
    {
        // Arrange
        var clock = new LamportTimestamp("node-1");

        // Act
        clock.Increment();
        clock.Increment();
        clock.Increment();

        // Assert
        Assert.Equal(3, clock.GetCurrentTimestamp());
    }

    /// <summary>
    /// Test that NodeId is immutable after construction.
    /// </summary>
    [Fact]
    public void NodeId_IsImmutable()
    {
        // Arrange & Act
        var clock = new LamportTimestamp("node-1");
        clock.Increment();
        clock.Update(100);

        // Assert
        Assert.Equal("node-1", clock.NodeId);
    }
}
