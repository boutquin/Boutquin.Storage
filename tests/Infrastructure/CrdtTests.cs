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
/// Unit tests for CRDT implementations: GCounter, PNCounter, GSet, ORSet.
/// Each test follows the Arrange-Act-Assert pattern.
/// </summary>
public sealed class CrdtTests
{
    // === GCounter Tests ===

    [Fact]
    public void GCounter_Increment_IncreasesValue()
    {
        // Arrange
        var counter = new GCounter();

        // Act
        counter.Increment("nodeA");
        counter.Increment("nodeA");
        counter.Increment("nodeB");

        // Assert
        Assert.Equal(3, counter.Value);
    }

    [Fact]
    public void GCounter_Merge_TakesElementWiseMax()
    {
        // Arrange
        var counter1 = new GCounter();
        counter1.Increment("nodeA");
        counter1.Increment("nodeA"); // nodeA=2

        var counter2 = new GCounter();
        counter2.Increment("nodeA"); // nodeA=1
        counter2.Increment("nodeB"); // nodeB=1

        // Act
        var merged = (GCounter)counter1.Merge(counter2);

        // Assert — max(2,1) + 1 = 3
        Assert.Equal(3, merged.Value);
    }

    [Fact]
    public void GCounter_Value_IsSumOfAllNodeCounters()
    {
        // Arrange
        var counter = new GCounter();
        counter.Increment("nodeA");
        counter.Increment("nodeB");
        counter.Increment("nodeC");

        // Assert
        Assert.Equal(3, counter.Value);
    }

    // === PNCounter Tests ===

    [Fact]
    public void PNCounter_Increment_IncreasesValue()
    {
        // Arrange
        var counter = new PNCounter();

        // Act
        counter.Increment("nodeA");
        counter.Increment("nodeA");

        // Assert
        Assert.Equal(2, counter.Value);
    }

    [Fact]
    public void PNCounter_Decrement_DecreasesValue()
    {
        // Arrange
        var counter = new PNCounter();
        counter.Increment("nodeA");
        counter.Increment("nodeA");
        counter.Increment("nodeA");

        // Act
        counter.Decrement("nodeA");

        // Assert
        Assert.Equal(2, counter.Value);
    }

    [Fact]
    public void PNCounter_Merge_CombinesBothCounters()
    {
        // Arrange
        var counter1 = new PNCounter();
        counter1.Increment("nodeA");
        counter1.Increment("nodeA"); // P: nodeA=2

        var counter2 = new PNCounter();
        counter2.Increment("nodeA"); // P: nodeA=1
        counter2.Decrement("nodeA"); // N: nodeA=1

        // Act
        var merged = (PNCounter)counter1.Merge(counter2);

        // Assert — P: max(2,1)=2, N: max(0,1)=1, Value=2-1=1
        Assert.Equal(1, merged.Value);
    }

    [Fact]
    public void PNCounter_Value_IsDifferenceOfPositiveAndNegative()
    {
        // Arrange
        var counter = new PNCounter();
        counter.Increment("nodeA"); // +1
        counter.Increment("nodeB"); // +1
        counter.Decrement("nodeA"); // -1

        // Assert
        Assert.Equal(1, counter.Value);
    }

    // === GSet Tests ===

    [Fact]
    public void GSet_Add_ElementIsContained()
    {
        // Arrange
        var set = new GSet<string>();

        // Act
        set.Add("item1");

        // Assert
        Assert.True(set.Contains("item1"));
        Assert.False(set.Contains("item2"));
    }

    [Fact]
    public void GSet_Merge_IsUnion()
    {
        // Arrange
        var set1 = new GSet<string>();
        set1.Add("item1");
        set1.Add("item2");

        var set2 = new GSet<string>();
        set2.Add("item2");
        set2.Add("item3");

        // Act
        var merged = (GSet<string>)set1.Merge(set2);

        // Assert
        Assert.True(merged.Contains("item1"));
        Assert.True(merged.Contains("item2"));
        Assert.True(merged.Contains("item3"));
        Assert.Equal(3, merged.Value.Count);
    }

    [Fact]
    public void GSet_Value_ReturnsAllElements()
    {
        // Arrange
        var set = new GSet<string>();
        set.Add("a");
        set.Add("b");
        set.Add("c");

        // Assert
        Assert.Equal(3, set.Value.Count);
    }

    // === ORSet Tests ===

    [Fact]
    public void ORSet_Add_ElementIsContained()
    {
        // Arrange
        var set = new ORSet<string>();

        // Act
        set.Add("nodeA", "item1");

        // Assert
        Assert.True(set.Contains("item1"));
    }

    [Fact]
    public void ORSet_Remove_ElementIsNotContained()
    {
        // Arrange
        var set = new ORSet<string>();
        set.Add("nodeA", "item1");

        // Act
        set.Remove("item1");

        // Assert
        Assert.False(set.Contains("item1"));
    }

    [Fact]
    public void ORSet_Merge_AddWinsOverConcurrentRemove()
    {
        // Arrange — set1 removes item1, set2 adds item1 concurrently
        var set1 = new ORSet<string>();
        set1.Add("nodeA", "item1");
        set1.Remove("item1"); // remove observed tag

        var set2 = new ORSet<string>();
        set2.Add("nodeB", "item1"); // concurrent add with new tag

        // Act
        var merged = (ORSet<string>)set1.Merge(set2);

        // Assert — add wins because set2's tag was not observed by set1's remove
        Assert.True(merged.Contains("item1"));
    }

    [Fact]
    public void ORSet_Contains_ReturnsFalse_AfterRemove()
    {
        // Arrange
        var set = new ORSet<string>();
        set.Add("nodeA", "item1");
        set.Add("nodeA", "item2");

        // Act
        set.Remove("item1");

        // Assert
        Assert.False(set.Contains("item1"));
        Assert.True(set.Contains("item2"));
    }

    [Fact]
    public void ORSet_Value_ReturnsActiveElements()
    {
        // Arrange
        var set = new ORSet<string>();
        set.Add("nodeA", "item1");
        set.Add("nodeA", "item2");
        set.Add("nodeA", "item3");
        set.Remove("item2");

        // Assert
        var value = set.Value;
        Assert.Equal(2, value.Count);
        Assert.Contains("item1", value);
        Assert.Contains("item3", value);
    }

    // ========== Tier 1: ORSet tag counter atomicity ==========

    /// <summary>
    /// Test that rapid sequential adds from the same node produce unique tags.
    /// </summary>
    [Fact]
    public void ORSet_SequentialAdds_ProduceUniqueTags()
    {
        // Arrange
        var set = new ORSet<string>();

        // Act — add many items from the same node
        for (var i = 0; i < 100; i++)
        {
            set.Add("nodeA", $"item-{i}");
        }

        // Assert — all 100 items should be present
        Assert.Equal(100, set.Value.Count);
    }

    // ========== Tier 1: PNCounter safe merge ==========

    /// <summary>
    /// Test that PNCounter.Merge works correctly with the GCounter merge return type.
    /// </summary>
    [Fact]
    public void PNCounter_Merge_ReturnsCorrectType()
    {
        // Arrange
        var counter1 = new PNCounter();
        counter1.Increment("nodeA");
        counter1.Increment("nodeA");

        var counter2 = new PNCounter();
        counter2.Decrement("nodeB");

        // Act
        var merged = counter1.Merge(counter2);

        // Assert — should be a PNCounter with correct value (2 - 1 = 1)
        Assert.IsType<PNCounter>(merged);
        Assert.Equal(1, merged.Value);
    }
}
