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
/// Unit tests for the CountingBloomFilter class.
/// Each test follows the Arrange-Act-Assert pattern.
/// </summary>
public sealed class CountingBloomFilterTests
{
    /// <summary>
    /// Test that Add then Contains returns true.
    /// </summary>
    [Fact]
    public void Add_ThenContains_ReturnsTrue()
    {
        // Arrange
        var filter = new CountingBloomFilter<string>(100, 0.01);

        // Act
        filter.Add("key1");

        // Assert
        Assert.True(filter.Contains("key1"));
    }

    /// <summary>
    /// Test that Contains returns false for a non-added element.
    /// </summary>
    [Fact]
    public void Contains_ReturnsFalse_ForNonAddedElement()
    {
        // Arrange
        var filter = new CountingBloomFilter<string>(100, 0.01);

        // Assert
        Assert.False(filter.Contains("nonexistent"));
    }

    /// <summary>
    /// Test that Remove decrements and eventually returns false for Contains.
    /// </summary>
    [Fact]
    public void Remove_Decrements_AndEventuallyReturnsFalse()
    {
        // Arrange
        var filter = new CountingBloomFilter<string>(100, 0.01);
        filter.Add("key1");

        // Act
        filter.Remove("key1");

        // Assert
        Assert.False(filter.Contains("key1"));
    }

    /// <summary>
    /// Test that Remove on a non-existent item is a no-op (counters stay at 0).
    /// </summary>
    [Fact]
    public void Remove_NonExistentItem_IsNoOp()
    {
        // Arrange
        var filter = new CountingBloomFilter<string>(100, 0.01);

        // Act — should not throw or corrupt state
        filter.Remove("nonexistent");

        // Assert — filter still works correctly
        filter.Add("key1");
        Assert.True(filter.Contains("key1"));
    }

    /// <summary>
    /// Test that Clear resets all counters.
    /// </summary>
    [Fact]
    public void Clear_ResetsAllCounters()
    {
        // Arrange
        var filter = new CountingBloomFilter<string>(100, 0.01);
        filter.Add("key1");
        filter.Add("key2");

        // Act
        filter.Clear();

        // Assert
        Assert.False(filter.Contains("key1"));
        Assert.False(filter.Contains("key2"));
    }

    /// <summary>
    /// Test that multiple adds require multiple removes.
    /// </summary>
    [Fact]
    public void MultipleAdds_RequireMultipleRemoves()
    {
        // Arrange
        var filter = new CountingBloomFilter<string>(100, 0.01);
        filter.Add("key1");
        filter.Add("key1");
        filter.Add("key1");

        // Act — remove once
        filter.Remove("key1");

        // Assert — still present after 1 remove (was added 3 times)
        Assert.True(filter.Contains("key1"));

        // Act — remove twice more
        filter.Remove("key1");
        filter.Remove("key1");

        // Assert — now gone
        Assert.False(filter.Contains("key1"));
    }

    /// <summary>
    /// Test that null item throws ArgumentNullException for all methods.
    /// </summary>
    [Fact]
    public void NullItem_ThrowsArgumentNullException()
    {
        // Arrange
        var filter = new CountingBloomFilter<string>(100, 0.01);

        // Assert
        Assert.Throws<ArgumentNullException>(() => filter.Add(null!));
        Assert.Throws<ArgumentNullException>(() => filter.Contains(null!));
        Assert.Throws<ArgumentNullException>(() => filter.Remove(null!));
        Assert.Throws<ArgumentNullException>(() => filter.GetCount(null!));
    }

    /// <summary>
    /// Test that GetCount returns correct count after multiple adds and removes.
    /// </summary>
    [Fact]
    public void GetCount_ReturnsCorrectCount()
    {
        // Arrange
        var filter = new CountingBloomFilter<string>(100, 0.01);

        // Act & Assert — initial count should be 0
        Assert.Equal(0, filter.GetCount("key1"));

        // Act — add 3 times
        filter.Add("key1");
        filter.Add("key1");
        filter.Add("key1");

        // Assert
        Assert.Equal(3, filter.GetCount("key1"));

        // Act — remove once
        filter.Remove("key1");

        // Assert
        Assert.Equal(2, filter.GetCount("key1"));
    }

    /// <summary>
    /// Test that counters don't go below zero on excess removes.
    /// </summary>
    [Fact]
    public void Remove_ExcessRemoves_CounterFloorsAtZero()
    {
        // Arrange
        var filter = new CountingBloomFilter<string>(100, 0.01);
        filter.Add("key1");

        // Act — remove twice (only added once)
        filter.Remove("key1");
        filter.Remove("key1");

        // Assert — counter should be at 0, not negative
        Assert.Equal(0, filter.GetCount("key1"));
        Assert.False(filter.Contains("key1"));
    }

    /// <summary>
    /// Test that after removing, re-adding works correctly.
    /// </summary>
    [Fact]
    public void Remove_ThenReadd_WorksCorrectly()
    {
        // Arrange
        var filter = new CountingBloomFilter<string>(100, 0.01);
        filter.Add("key1");
        filter.Remove("key1");

        // Act — re-add the key
        filter.Add("key1");

        // Assert
        Assert.True(filter.Contains("key1"));
        Assert.Equal(1, filter.GetCount("key1"));
    }

    /// <summary>
    /// Test that clear then re-add works correctly.
    /// </summary>
    [Fact]
    public void Clear_ThenReadd_WorksCorrectly()
    {
        // Arrange
        var filter = new CountingBloomFilter<string>(100, 0.01);
        filter.Add("key1");
        filter.Add("key1");
        filter.Clear();

        // Act
        filter.Add("key2");

        // Assert
        Assert.False(filter.Contains("key1"));
        Assert.Equal(0, filter.GetCount("key1"));
        Assert.True(filter.Contains("key2"));
        Assert.Equal(1, filter.GetCount("key2"));
    }

    /// <summary>
    /// Test that multiple independent items don't interfere with each other's counts.
    /// </summary>
    [Fact]
    public void MultipleItems_IndependentCounts()
    {
        // Arrange
        var filter = new CountingBloomFilter<string>(1000, 0.01);

        // Act
        filter.Add("alpha");
        filter.Add("alpha");
        filter.Add("beta");
        filter.Add("beta");
        filter.Add("beta");
        filter.Remove("alpha");

        // Assert
        Assert.Equal(1, filter.GetCount("alpha"));
        Assert.Equal(3, filter.GetCount("beta"));
        Assert.True(filter.Contains("alpha"));
        Assert.True(filter.Contains("beta"));
    }

    /// <summary>
    /// Test that empty string works as an item.
    /// </summary>
    [Fact]
    public void EmptyString_WorksAsItem()
    {
        // Arrange
        var filter = new CountingBloomFilter<string>(100, 0.01);

        // Act
        filter.Add(string.Empty);

        // Assert
        Assert.True(filter.Contains(string.Empty));
        Assert.Equal(1, filter.GetCount(string.Empty));
    }

    /// <summary>
    /// Test false positive rate stays reasonable at capacity.
    /// </summary>
    [Fact]
    public void FalsePositiveRate_StaysReasonable()
    {
        // Arrange
        var filter = new CountingBloomFilter<string>(500, 0.05);

        // Act — fill to capacity
        for (var i = 0; i < 500; i++)
        {
            filter.Add($"added-{i}");
        }

        // Check FP rate
        var falsePositives = 0;
        var testCount = 5000;
        for (var i = 0; i < testCount; i++)
        {
            if (filter.Contains($"not-added-{i}"))
            {
                falsePositives++;
            }
        }

        var actualFpRate = (double)falsePositives / testCount;

        // Assert — allow 2x target as upper bound
        Assert.True(actualFpRate < 0.10,
            $"False positive rate {actualFpRate:P2} exceeds 2x target 5%");
    }
}
