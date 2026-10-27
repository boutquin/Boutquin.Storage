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

    // The racy instance-form `CompareTo(long, string)` was removed; every test that
    // exercised it has been folded into the static `CompareEvents(long, string, long, string)`
    // suite further down (timestamp-ordering, antisymmetric, equal-timestamp tie-break,
    // identical-events-zero, consistency-with-instance-state). DDIA Ch. 9 (p. 342) describes
    // Lamport ordering as a comparison over two `(counter, nodeId)` pairs; there is no
    // canonical "current node's timestamp" mutating mid-compare.

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

    // ========== CompareEvents (static two-event total-ordering form) ==========

    /// <summary>
    /// CompareEvents orders by timestamp first — earlier timestamp comes first regardless of node.
    /// </summary>
    [Fact]
    public void CompareEvents_DifferentTimestamps_OrdersByTimestamp()
    {
        // event at t=5 on node-b vs event at t=10 on node-a
        var result = LamportTimestamp.CompareEvents(5L, "node-b", 10L, "node-a");
        Assert.True(result < 0);
    }

    /// <summary>
    /// CompareEvents is antisymmetric: swapping arguments negates the sign.
    /// </summary>
    [Fact]
    public void CompareEvents_Antisymmetric_SwappingNegatesSign()
    {
        var fwd = LamportTimestamp.CompareEvents(10L, "node-a", 5L, "node-b");
        var rev = LamportTimestamp.CompareEvents(5L, "node-b", 10L, "node-a");
        Assert.True(fwd > 0);
        Assert.True(rev < 0);
    }

    /// <summary>
    /// CompareEvents breaks ties by lexicographic node ID.
    /// </summary>
    [Fact]
    public void CompareEvents_EqualTimestamps_BrokenByNodeId()
    {
        // same timestamp, "node-a" < "node-b" lexicographically
        var result = LamportTimestamp.CompareEvents(5L, "node-a", 5L, "node-b");
        Assert.True(result < 0);
    }

    /// <summary>
    /// CompareEvents returns zero for identical (timestamp, nodeId) pairs.
    /// </summary>
    [Fact]
    public void CompareEvents_IdenticalEvents_ReturnsZero()
    {
        var result = LamportTimestamp.CompareEvents(7L, "node-x", 7L, "node-x");
        Assert.Equal(0, result);
    }

    /// <summary>
    /// CompareEvents reads on a clock's own (counter, nodeId) pair are consistent with the
    /// clock's current state — feeding the clock's GetCurrentTimestamp() into the static form
    /// must produce the same total order as feeding any other event with a higher counter.
    /// This replaces the earlier instance-form consistency check.
    /// </summary>
    [Fact]
    public void CompareEvents_OwnClockState_OrdersConsistently()
    {
        var clock = new LamportTimestamp("node-a", initialCounter: 5);
        var myTimestamp = clock.GetCurrentTimestamp();

        // (own_counter, own_nodeId) vs (higher counter, different nodeId)
        var result = LamportTimestamp.CompareEvents(myTimestamp, clock.NodeId, 10L, "node-b");

        Assert.True(result < 0);
    }

    /// <summary>
    /// The node id is the total-order tiebreaker, so a null, empty, or whitespace id degrades
    /// ordering. The sibling convention across src/Distributed (GCounter, ORSet, VectorClock,
    /// RaftNode, ConsistentHashRing) is ThrowIfNullOrWhiteSpace at construction.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_EmptyOrWhitespaceNodeId_ThrowsArgumentException(string nodeId)
    {
        var act = () => new LamportTimestamp(nodeId);
        act.Should().Throw<ArgumentException>(
            "an empty node id degrades the total-order tiebreak; the repo convention rejects it at construction");
    }
    [Fact]
    public void Update_WithReceivedMaxValue_FailsLoudlyWithoutRegressingTheClock()
    {
        var clock = new LamportTimestamp("node-a", initialCounter: 1);

        // max(local, received) + 1 has no representable value; wrapping to long.MinValue would
        // order every later local event before every earlier one.
        var act = () => clock.Update(long.MaxValue);

        act.Should().Throw<OverflowException>("the receiver clock cannot exceed long.MaxValue");
        clock.GetCurrentTimestamp().Should().Be(1, "a rejected update must leave the clock unchanged");
    }

    [Fact]
    public void Increment_AtMaxValue_FailsLoudlyWithoutRegressingTheClock()
    {
        var clock = new LamportTimestamp("node-a", initialCounter: long.MaxValue);

        Func<long> act = clock.Increment;

        act.Should().Throw<OverflowException>("the next local timestamp is not representable");
        clock.GetCurrentTimestamp().Should().Be(long.MaxValue, "a rejected increment must leave the clock unchanged");
    }
}
