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
/// Unit tests for the VectorClock class.
/// Each test follows the Arrange-Act-Assert pattern.
/// </summary>
public sealed class VectorClockTests
{
    /// <summary>
    /// Test that Increment increases counter for a node.
    /// </summary>
    [Fact]
    public void Increment_IncreasesCounter()
    {
        // Arrange
        var clock = new VectorClock();

        // Act
        clock.Increment("nodeA");
        clock.Increment("nodeA");
        clock.Increment("nodeA");

        // Assert
        var state = clock.GetClock();
        Assert.Equal(3, state["nodeA"]);
    }

    /// <summary>
    /// Test that Increment on a new node starts at 1.
    /// </summary>
    [Fact]
    public void Increment_NewNode_StartsAt1()
    {
        // Arrange
        var clock = new VectorClock();

        // Act
        clock.Increment("nodeA");

        // Assert
        var state = clock.GetClock();
        Assert.Equal(1, state["nodeA"]);
    }

    /// <summary>
    /// Test CompareTo returns Equal for identical clocks.
    /// </summary>
    [Fact]
    public void CompareTo_Equal()
    {
        // Arrange
        var clock1 = new VectorClock();
        clock1.Increment("nodeA");
        clock1.Increment("nodeB");

        var clock2 = new VectorClock();
        clock2.Increment("nodeA");
        clock2.Increment("nodeB");

        // Act & Assert
        Assert.Equal(VectorClockComparison.Equal, clock1.CompareTo(clock2));
    }

    /// <summary>
    /// Test CompareTo returns Before when this clock is causally before other.
    /// </summary>
    [Fact]
    public void CompareTo_Before()
    {
        // Arrange
        var clock1 = new VectorClock();
        clock1.Increment("nodeA");

        var clock2 = new VectorClock();
        clock2.Increment("nodeA");
        clock2.Increment("nodeA");
        clock2.Increment("nodeB");

        // Act & Assert
        Assert.Equal(VectorClockComparison.Before, clock1.CompareTo(clock2));
    }

    /// <summary>
    /// Test CompareTo returns After when this clock is causally after other.
    /// </summary>
    [Fact]
    public void CompareTo_After()
    {
        // Arrange
        var clock1 = new VectorClock();
        clock1.Increment("nodeA");
        clock1.Increment("nodeA");
        clock1.Increment("nodeB");

        var clock2 = new VectorClock();
        clock2.Increment("nodeA");

        // Act & Assert
        Assert.Equal(VectorClockComparison.After, clock1.CompareTo(clock2));
    }

    /// <summary>
    /// Test CompareTo returns Concurrent when clocks are concurrent.
    /// </summary>
    [Fact]
    public void CompareTo_Concurrent()
    {
        // Arrange — clock1 has more of nodeA, clock2 has more of nodeB
        var clock1 = new VectorClock();
        clock1.Increment("nodeA");
        clock1.Increment("nodeA");

        var clock2 = new VectorClock();
        clock2.Increment("nodeB");
        clock2.Increment("nodeB");

        // Act & Assert
        Assert.Equal(VectorClockComparison.Concurrent, clock1.CompareTo(clock2));
    }

    /// <summary>
    /// Test that Merge takes element-wise max.
    /// </summary>
    [Fact]
    public void Merge_TakesElementWiseMax()
    {
        // Arrange
        var clock1 = new VectorClock();
        clock1.Increment("nodeA");
        clock1.Increment("nodeA"); // nodeA=2

        var clock2 = new VectorClock();
        clock2.Increment("nodeA"); // nodeA=1
        clock2.Increment("nodeB");
        clock2.Increment("nodeB"); // nodeB=2

        // Act
        var merged = clock1.Merge(clock2);
        var state = merged.GetClock();

        // Assert
        Assert.Equal(2, state["nodeA"]);
        Assert.Equal(2, state["nodeB"]);
    }

    /// <summary>
    /// Test that Merge with disjoint keys includes all.
    /// </summary>
    [Fact]
    public void Merge_DisjointKeys_IncludesAll()
    {
        // Arrange
        var clock1 = new VectorClock();
        clock1.Increment("nodeA");

        var clock2 = new VectorClock();
        clock2.Increment("nodeB");

        // Act
        var merged = clock1.Merge(clock2);
        var state = merged.GetClock();

        // Assert
        Assert.Equal(1, state["nodeA"]);
        Assert.Equal(1, state["nodeB"]);
    }

    /// <summary>
    /// Test that GetClock returns a snapshot (not a mutable reference).
    /// </summary>
    [Fact]
    public void GetClock_ReturnsSnapshot_NotMutableReference()
    {
        // Arrange
        var clock = new VectorClock();
        clock.Increment("nodeA");

        // Act — get snapshot, then modify clock
        var snapshot = clock.GetClock();
        clock.Increment("nodeA");

        // Assert — snapshot should still show old value
        Assert.Equal(1, snapshot["nodeA"]);
        Assert.Equal(2, clock.GetClock()["nodeA"]);
    }
}
