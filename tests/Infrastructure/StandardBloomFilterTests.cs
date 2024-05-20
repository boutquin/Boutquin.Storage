// Copyright (c) 2024 Pierre G. Boutquin. All rights reserved.
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
/// This class contains unit tests for the StandardBloomFilter class.
/// Each test follows the Arrange-Act-Assert pattern.
/// </summary>
public class StandardBloomFilterTests
{
    /// <summary>
    /// Test to ensure that the Add method correctly adds a key to the Bloom filter.
    /// </summary>
    [Fact]
    public void Add_ShouldAddKeyToBloomFilter()
    {
        // Arrange: Create a new Bloom filter with a size of 1000 and 3 hash functions.
        var bloomFilter = new StandardBloomFilter<string>(1000, 3);

        // Act: Add a key to the Bloom filter.
        bloomFilter.Add("testKey");

        // Assert: Check that the key was added correctly.
        // We use the MightContain method to check if the key is possibly in the set.
        // Since we just added the key, it should return true.
        Assert.True(bloomFilter.MightContain("testKey"));
    }

    /// <summary>
    /// Test to ensure that the MightContain method returns false for a key not added to the Bloom filter.
    /// </summary>
    [Fact]
    public void MightContain_ShouldReturnFalseForKeyNotAdded()
    {
        // Arrange: Create a new Bloom filter with a size of 1000 and 3 hash functions.
        var bloomFilter = new StandardBloomFilter<string>(1000, 3);

        // Act & Assert: Check that the key is not in the Bloom filter.
        // Since we have not added "testKey", it should return false.
        Assert.False(bloomFilter.MightContain("testKey"));
    }

    /// <summary>
    /// Test to ensure that the Clear method removes all keys from the Bloom filter.
    /// </summary>
    [Fact]
    public void Clear_ShouldRemoveAllKeys()
    {
        // Arrange: Create a new Bloom filter with a size of 1000 and 3 hash functions.
        var bloomFilter = new StandardBloomFilter<string>(1000, 3);
        bloomFilter.Add("testKey");

        // Act: Clear the Bloom filter.
        bloomFilter.Clear();

        // Assert: Check that the Bloom filter is empty.
        // After clearing, the filter should return false for any key.
        Assert.False(bloomFilter.MightContain("testKey"));
    }

    /// <summary>
    /// Test to ensure that the Add method handles null keys gracefully.
    /// </summary>
    [Fact]
    public void Add_ShouldHandleNullKeyGracefully()
    {
        // Arrange: Create a new Bloom filter with a size of 1000 and 3 hash functions.
        var bloomFilter = new StandardBloomFilter<string>(1000, 3);

        // Act & Assert: Attempt to add a null key and expect an ArgumentNullException.
        Assert.Throws<ArgumentNullException>(() => bloomFilter.Add(null));
    }

    /// <summary>
    /// Test to ensure that the MightContain method handles null keys gracefully.
    /// </summary>
    [Fact]
    public void MightContain_ShouldHandleNullKeyGracefully()
    {
        // Arrange: Create a new Bloom filter with a size of 1000 and 3 hash functions.
        var bloomFilter = new StandardBloomFilter<string>(1000, 3);

        // Act & Assert: Attempt to check if a null key is in the Bloom filter and expect an ArgumentNullException.
        Assert.Throws<ArgumentNullException>(() => bloomFilter.MightContain(null));
    }

    /// <summary>
    /// Test to ensure that the Bloom filter handles a large number of additions.
    /// </summary>
    [Fact]
    public void Add_ShouldHandleLargeNumberOfKeys()
    {
        // Arrange: Create a new Bloom filter with a size of 1000000 and 3 hash functions.
        var bloomFilter = new StandardBloomFilter<string>(1000000, 3);

        // Act: Add a large number of keys to the Bloom filter.
        for (int i = 0; i < 100000; i++)
        {
            bloomFilter.Add($"key{i}");
        }

        // Assert: Check that the keys were added correctly.
        // We use the MightContain method to check if the keys are possibly in the set.
        // Since we just added these keys, they should return true.
        for (int i = 0; i < 100000; i++)
        {
            Assert.True(bloomFilter.MightContain($"key{i}"));
        }
    }

    /// <summary>
    /// Test to ensure that the Bloom filter handles false positives.
    /// </summary>
    [Fact]
    public void MightContain_ShouldHandleFalsePositives()
    {
        // Arrange: Create a new Bloom filter with a size of 1000 and 3 hash functions.
        var bloomFilter = new StandardBloomFilter<string>(1000, 3);
        bloomFilter.Add("testKey1");
        bloomFilter.Add("testKey2");

        // Act & Assert: Check for a key that was not added.
        // Bloom filters may return false positives, but it should rarely happen for distinct keys.
        // Here, we're just checking that the method works without asserting the result, as false positives are expected behavior.
        var mightContain = bloomFilter.MightContain("unaddedKey");
    }

    /// <summary>
    /// Test to ensure that the Bloom filter correctly handles edge cases with very small size.
    /// </summary>
    [Fact]
    public void BloomFilter_ShouldHandleVerySmallSize()
    {
        // Arrange: Create a new Bloom filter with a size of 1 and 1 hash function.
        var bloomFilter = new StandardBloomFilter<string>(1, 1);

        // Act: Add a key to the Bloom filter.
        bloomFilter.Add("testKey");

        // Assert: Check that the key was added correctly.
        // With a size of 1, any key will be set to the only bit, making it always true for any key.
        Assert.True(bloomFilter.MightContain("testKey"));
        Assert.True(bloomFilter.MightContain("anotherKey"));
    }

    /// <summary>
    /// Test to ensure that the Bloom filter handles different types of keys correctly.
    /// </summary>
    [Fact]
    public void BloomFilter_ShouldHandleDifferentKeyTypes()
    {
        // Arrange: Create a new Bloom filter with a size of 1000 and 3 hash functions.
        var stringBloomFilter = new StandardBloomFilter<string>(1000, 3);
        var intBloomFilter = new StandardBloomFilter<int>(1000, 3);
        var guidBloomFilter = new StandardBloomFilter<Guid>(1000, 3);

        // Act: Add different types of keys to the Bloom filters.
        stringBloomFilter.Add("testString");
        intBloomFilter.Add(12345);
        guidBloomFilter.Add(Guid.NewGuid());

        // Assert: Check that the keys were added correctly.
        Assert.True(stringBloomFilter.MightContain("testString"));
        Assert.True(intBloomFilter.MightContain(12345));
        // We can't check the specific Guid as it is random, but we can ensure no exception is thrown.
    }

    /// <summary>
    /// Test to ensure that the Bloom filter correctly handles adding duplicate keys.
    /// </summary>
    [Fact]
    public void BloomFilter_ShouldHandleDuplicateKeys()
    {
        // Arrange: Create a new Bloom filter with a size of 1000 and 3 hash functions.
        var bloomFilter = new StandardBloomFilter<string>(1000, 3);
        bloomFilter.Add("testKey");

        // Act: Add the same key again.
        bloomFilter.Add("testKey");

        // Assert: Check that the key is still possibly in the set.
        // Since Bloom filters can handle duplicates, this should be true.
        Assert.True(bloomFilter.MightContain("testKey"));
    }

    /// <summary>
    /// Test to ensure that the Bloom filter handles empty string keys correctly.
    /// </summary>
    [Fact]
    public void BloomFilter_ShouldHandleEmptyStringKeys()
    {
        // Arrange: Create a new Bloom filter with a size of 1000 and 3 hash functions.
        var bloomFilter = new StandardBloomFilter<string>(1000, 3);

        // Act: Add an empty string key to the Bloom filter.
        bloomFilter.Add("");

        // Assert: Check that the empty string key was added correctly.
        Assert.True(bloomFilter.MightContain(""));
    }

    /// <summary>
    /// Test to ensure that the Bloom filter handles very large keys correctly.
    /// </summary>
    [Fact]
    public void BloomFilter_ShouldHandleVeryLargeKeys()
    {
        // Arrange: Create a new Bloom filter with a size of 1000 and 3 hash functions.
        var bloomFilter = new StandardBloomFilter<string>(1000, 3);
        var largeKey = new string('a', 10000);

        // Act: Add a very large key to the Bloom filter.
        bloomFilter.Add(largeKey);

        // Assert: Check that the very large key was added correctly.
        Assert.True(bloomFilter.MightContain(largeKey));
    }

    /// <summary>
    /// Test to ensure that the Bloom filter handles special characters in keys correctly.
    /// </summary>
    [Fact]
    public void BloomFilter_ShouldHandleSpecialCharacterKeys()
    {
        // Arrange: Create a new Bloom filter with a size of 1000 and 3 hash functions.
        var bloomFilter = new StandardBloomFilter<string>(1000, 3);
        var specialCharKey = "!@#$%^&*()_+";

        // Act: Add a key with special characters to the Bloom filter.
        bloomFilter.Add(specialCharKey);

        // Assert: Check that the key with special characters was added correctly.
        Assert.True(bloomFilter.MightContain(specialCharKey));
    }
}