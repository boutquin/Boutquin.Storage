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
/// This class contains unit tests for the BloomFilter class.
/// Each test follows the Arrange-Act-Assert pattern.
/// </summary>
public sealed class BloomFilterTests
{
    /// <summary>
    /// Test to ensure that the Add method correctly adds an element to the Bloom filter.
    /// </summary>
    [Fact]
    public void Add_ShouldAddElement()
    {
        // Arrange: Create a Bloom filter with a capacity for 100 elements and a false positive probability of 1%.
        var expectedElements = 100;
        var falsePositiveProbability = 0.01;
        var bloomFilter = new BloomFilter<string>(expectedElements, falsePositiveProbability);

        // Act: Add an element to the Bloom filter.
        var item = "exampleKey";
        bloomFilter.Add(item);

        // Assert: Check that the Bloom filter indicates the element is possibly present.
        Assert.True(bloomFilter.Contains(item));
    }

    /// <summary>
    /// Test to ensure that the Contains method returns false for an element not added to the Bloom filter.
    /// </summary>
    [Fact]
    public void Contains_ShouldReturnFalseForNonAddedElement()
    {
        // Arrange: Create a Bloom filter with a capacity for 100 elements and a false positive probability of 1%.
        var expectedElements = 100;
        var falsePositiveProbability = 0.01;
        var bloomFilter = new BloomFilter<string>(expectedElements, falsePositiveProbability);

        // Act: Check if the Bloom filter contains an element that has not been added.
        var item = "nonExistentKey";

        // Assert: The Bloom filter should return false for an element not added.
        Assert.False(bloomFilter.Contains(item));
    }

    /// <summary>
    /// Test to ensure that the Clear method correctly removes all elements from the Bloom filter.
    /// </summary>
    [Fact]
    public void Clear_ShouldRemoveAllElements()
    {
        // Arrange: Create a Bloom filter and add an element to it.
        var expectedElements = 100;
        var falsePositiveProbability = 0.01;
        var bloomFilter = new BloomFilter<string>(expectedElements, falsePositiveProbability);
        var item = "exampleKey";
        bloomFilter.Add(item);

        // Act: Clear the Bloom filter.
        bloomFilter.Clear();

        // Assert: Check that the Bloom filter indicates the previously added element is no longer present.
        Assert.False(bloomFilter.Contains(item));
    }

    /// <summary>
    /// Test to ensure that the Bloom filter handles multiple additions correctly.
    /// </summary>
    [Fact]
    public void Add_ShouldHandleMultipleAdditions()
    {
        // Arrange: Create a Bloom filter with a capacity for 100 elements and a false positive probability of 1%.
        var expectedElements = 100;
        var falsePositiveProbability = 0.01;
        var bloomFilter = new BloomFilter<string>(expectedElements, falsePositiveProbability);

        var items = new[] { "key1", "key2", "key3" };

        // Act: Add multiple elements to the Bloom filter.
        foreach (var item in items)
        {
            bloomFilter.Add(item);
        }

        // Assert: Check that the Bloom filter indicates each element is possibly present.
        foreach (var item in items)
        {
            Assert.True(bloomFilter.Contains(item));
        }
    }

    /// <summary>
    /// Test to ensure that the Bloom filter correctly handles adding duplicate elements.
    /// </summary>
    [Fact]
    public void Add_ShouldHandleDuplicateAdditions()
    {
        // Arrange: Create a Bloom filter with a capacity for 100 elements and a false positive probability of 1%.
        var expectedElements = 100;
        var falsePositiveProbability = 0.01;
        var bloomFilter = new BloomFilter<string>(expectedElements, falsePositiveProbability);
        var item = "duplicateKey";

        // Act: Add the same element multiple times to the Bloom filter.
        bloomFilter.Add(item);
        bloomFilter.Add(item);
        bloomFilter.Add(item);

        // Assert: Check that the Bloom filter indicates the element is possibly present.
        Assert.True(bloomFilter.Contains(item));
    }

    /// <summary>
    /// Test to ensure that the Bloom filter handles edge case with an empty string.
    /// </summary>
    [Fact]
    public void Add_ShouldHandleEmptyString()
    {
        // Arrange: Create a Bloom filter with a capacity for 100 elements and a false positive probability of 1%.
        var expectedElements = 100;
        var falsePositiveProbability = 0.01;
        var bloomFilter = new BloomFilter<string>(expectedElements, falsePositiveProbability);
        var item = string.Empty;

        // Act: Add an empty string to the Bloom filter.
        bloomFilter.Add(item);

        // Assert: Check that the Bloom filter indicates the empty string is possibly present.
        Assert.True(bloomFilter.Contains(item));
    }

    /// <summary>
    /// Test to ensure that the Bloom filter handles edge case with a null element.
    /// </summary>
    [Fact]
    public void Add_ShouldThrowExceptionForNullElement()
    {
        // Arrange: Create a Bloom filter with a capacity for 100 elements and a false positive probability of 1%.
        var expectedElements = 100;
        var falsePositiveProbability = 0.01;
        var bloomFilter = new BloomFilter<string>(expectedElements, falsePositiveProbability);

        // Act & Assert: Attempt to add a null element and expect an NullReferenceException.
        Assert.Throws<ArgumentNullException>(() => bloomFilter.Add(null!));
    }

    /// <summary>
    /// Test to ensure that the Bloom filter handles adding elements of varying lengths.
    /// </summary>
    [Fact]
    public void Add_ShouldHandleElementsOfVaryingLengths()
    {
        // Arrange: Create a Bloom filter with a capacity for 100 elements and a false positive probability of 1%.
        var expectedElements = 100;
        var falsePositiveProbability = 0.01;
        var bloomFilter = new BloomFilter<string>(expectedElements, falsePositiveProbability);

        var items = new[] { "a", "abc", "abcdef", new string('x', 1000) };

        // Act: Add elements of varying lengths to the Bloom filter.
        foreach (var item in items)
        {
            bloomFilter.Add(item);
        }

        // Assert: Check that the Bloom filter indicates each element is possibly present.
        foreach (var item in items)
        {
            Assert.True(bloomFilter.Contains(item));
        }
    }

    /// <summary>
    /// Test that the false positive rate stays within acceptable bounds at capacity.
    /// </summary>
    [Fact]
    public void FalsePositiveRate_StaysWithinBounds_AtCapacity()
    {
        // Arrange: Create a filter sized for 1000 elements at 5% FP rate
        var expectedElements = 1000;
        var targetFpRate = 0.05;
        var bloomFilter = new BloomFilter<string>(expectedElements, targetFpRate);

        // Act: Add exactly expectedElements items
        for (var i = 0; i < expectedElements; i++)
        {
            bloomFilter.Add($"added-{i}");
        }

        // Check FP rate with 10000 items that were NOT added
        var falsePositives = 0;
        var testCount = 10000;
        for (var i = 0; i < testCount; i++)
        {
            if (bloomFilter.Contains($"not-added-{i}"))
            {
                falsePositives++;
            }
        }

        var actualFpRate = (double)falsePositives / testCount;

        // Assert — allow 2x the target FP rate as upper bound (statistical variance)
        Assert.True(actualFpRate < targetFpRate * 2,
            $"False positive rate {actualFpRate:P2} exceeds 2x target {targetFpRate:P2}");
    }

    /// <summary>
    /// Test that Clear then re-add works correctly.
    /// </summary>
    [Fact]
    public void Clear_ThenReadd_WorksCorrectly()
    {
        // Arrange
        var bloomFilter = new BloomFilter<string>(100, 0.01);
        bloomFilter.Add("key1");
        bloomFilter.Clear();

        // Act — re-add a different key
        bloomFilter.Add("key2");

        // Assert
        Assert.False(bloomFilter.Contains("key1"));
        Assert.True(bloomFilter.Contains("key2"));
    }

    /// <summary>
    /// Test that filter works with integer type.
    /// </summary>
    [Fact]
    public void Add_IntegerType_WorksCorrectly()
    {
        // Arrange
        var bloomFilter = new BloomFilter<int>(100, 0.01);

        // Act
        bloomFilter.Add(42);
        bloomFilter.Add(100);

        // Assert
        Assert.True(bloomFilter.Contains(42));
        Assert.True(bloomFilter.Contains(100));
        Assert.False(bloomFilter.Contains(999));
    }

    /// <summary>
    /// Test filter behavior with large number of insertions beyond capacity — no false negatives.
    /// </summary>
    [Fact]
    public void Add_BeyondCapacity_NoFalseNegatives()
    {
        // Arrange — sized for 10 elements
        var bloomFilter = new BloomFilter<string>(10, 0.01);

        // Act — add 100 elements (10x capacity)
        for (var i = 0; i < 100; i++)
        {
            bloomFilter.Add($"item-{i}");
        }

        // Assert — all added items should still be detected (no false negatives guaranteed)
        for (var i = 0; i < 100; i++)
        {
            Assert.True(bloomFilter.Contains($"item-{i}"));
        }
    }

    /// <summary>
    /// Test that two distinct filters have independent state.
    /// </summary>
    [Fact]
    public void TwoFilters_HaveIndependentState()
    {
        // Arrange
        var filter1 = new BloomFilter<string>(100, 0.01);
        var filter2 = new BloomFilter<string>(100, 0.01);

        // Act
        filter1.Add("key1");
        filter2.Add("key2");

        // Assert — each filter only knows about its own keys
        Assert.True(filter1.Contains("key1"));
        Assert.False(filter1.Contains("key2"));
        Assert.False(filter2.Contains("key1"));
        Assert.True(filter2.Contains("key2"));
    }
}
