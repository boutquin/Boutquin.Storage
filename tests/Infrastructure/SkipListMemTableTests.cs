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
#nullable disable

namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// Unit tests for the SkipListMemTable class.
/// Each test follows the Arrange-Act-Assert pattern.
/// </summary>
public sealed class SkipListMemTableTests
{
    /// <summary>
    /// Test that Set and retrieve single item works.
    /// </summary>
    [Fact]
    public async Task Set_AndRetrieve_SingleItem()
    {
        // Arrange
        var skipList = new SkipListMemTable<int, string>(10);

        // Act
        await skipList.SetAsync(1, "value1");
        var (value, found) = await skipList.TryGetValueAsync(1);

        // Assert
        Assert.True(found);
        Assert.Equal("value1", value);
    }

    /// <summary>
    /// Test that Set overwrites existing key.
    /// </summary>
    [Fact]
    public async Task Set_OverwritesExistingKey()
    {
        // Arrange
        var skipList = new SkipListMemTable<int, string>(10);
        await skipList.SetAsync(1, "original");

        // Act
        await skipList.SetAsync(1, "updated");
        var (value, found) = await skipList.TryGetValueAsync(1);

        // Assert
        Assert.True(found);
        Assert.Equal("updated", value);
    }

    /// <summary>
    /// Test that TryGetValue returns false for missing key.
    /// </summary>
    [Fact]
    public async Task TryGetValue_ReturnsFalse_ForMissingKey()
    {
        // Arrange
        var skipList = new SkipListMemTable<int, string>(10);

        // Act
        var (_, found) = await skipList.TryGetValueAsync(42);

        // Assert
        Assert.False(found);
    }

    /// <summary>
    /// Test ContainsKey returns true and false correctly.
    /// </summary>
    [Fact]
    public async Task ContainsKey_ReturnsCorrectResult()
    {
        // Arrange
        var skipList = new SkipListMemTable<int, string>(10);
        await skipList.SetAsync(1, "value1");

        // Assert
        Assert.True(await skipList.ContainsKeyAsync(1));
        Assert.False(await skipList.ContainsKeyAsync(2));
    }

    /// <summary>
    /// Test that RemoveAsync throws NotSupportedException.
    /// </summary>
    [Fact]
    public async Task RemoveAsync_ThrowsNotSupportedException()
    {
        // Arrange
        var skipList = new SkipListMemTable<int, string>(10);

        // Act & Assert
        await Assert.ThrowsAsync<NotSupportedException>(() => skipList.RemoveAsync(1));
    }

    /// <summary>
    /// Test that GetAllItems returns sorted order.
    /// </summary>
    [Fact]
    public async Task GetAllItems_ReturnsSortedOrder()
    {
        // Arrange
        var skipList = new SkipListMemTable<int, string>(10);
        await skipList.SetAsync(5, "five");
        await skipList.SetAsync(1, "one");
        await skipList.SetAsync(3, "three");
        await skipList.SetAsync(7, "seven");
        await skipList.SetAsync(2, "two");

        // Act
        var items = (await skipList.GetAllItemsAsync()).ToList();

        // Assert
        Assert.Equal(5, items.Count);
        Assert.Equal(1, items[0].Key);
        Assert.Equal(2, items[1].Key);
        Assert.Equal(3, items[2].Key);
        Assert.Equal(5, items[3].Key);
        Assert.Equal(7, items[4].Key);
    }

    /// <summary>
    /// Test that SetBulkAsync adds multiple items.
    /// </summary>
    [Fact]
    public async Task SetBulkAsync_AddsMultipleItems()
    {
        // Arrange
        var skipList = new SkipListMemTable<int, string>(10);
        var items = new[]
        {
            new KeyValuePair<int, string>(1, "one"),
            new KeyValuePair<int, string>(2, "two"),
            new KeyValuePair<int, string>(3, "three"),
        };

        // Act
        await skipList.SetBulkAsync(items);

        // Assert
        Assert.True(await skipList.ContainsKeyAsync(1));
        Assert.True(await skipList.ContainsKeyAsync(2));
        Assert.True(await skipList.ContainsKeyAsync(3));
    }

    /// <summary>
    /// Test that ClearAsync empties the table.
    /// </summary>
    [Fact]
    public async Task ClearAsync_EmptiesTheTable()
    {
        // Arrange
        var skipList = new SkipListMemTable<int, string>(10);
        await skipList.SetAsync(1, "value1");
        await skipList.SetAsync(2, "value2");

        // Act
        await skipList.ClearAsync();

        // Assert
        Assert.False(await skipList.ContainsKeyAsync(1));
        Assert.False(await skipList.ContainsKeyAsync(2));
    }

    /// <summary>
    /// Test that IsFull returns true at capacity.
    /// </summary>
    [Fact]
    public async Task IsFull_ReturnsTrue_AtCapacity()
    {
        // Arrange
        var skipList = new SkipListMemTable<int, string>(2);
        await skipList.SetAsync(1, "one");

        // Assert — not full yet
        Assert.False(skipList.IsFull);

        // Act
        await skipList.SetAsync(2, "two");

        // Assert — now full
        Assert.True(skipList.IsFull);
    }

    /// <summary>
    /// Test that adding to a full MemTable throws InvalidOperationException.
    /// </summary>
    [Fact]
    public async Task Set_WhenFull_ThrowsInvalidOperationException()
    {
        // Arrange
        var skipList = new SkipListMemTable<int, string>(1);
        await skipList.SetAsync(1, "value1");

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => skipList.SetAsync(2, "value2"));
    }

    /// <summary>
    /// Test that IsFull resets to false after ClearAsync, allowing the table to be re-filled.
    /// This validates that Clear properly resets the internal count.
    /// </summary>
    [Fact]
    public async Task IsFull_ResetsAfterClear_AllowsRefill()
    {
        // Arrange — fill to capacity
        var skipList = new SkipListMemTable<int, string>(2);
        await skipList.SetAsync(1, "one");
        await skipList.SetAsync(2, "two");
        Assert.True(skipList.IsFull);

        // Act — clear and re-fill
        await skipList.ClearAsync();

        // Assert — no longer full, can add again
        Assert.False(skipList.IsFull);
        await skipList.SetAsync(10, "ten");
        await skipList.SetAsync(20, "twenty");
        Assert.True(skipList.IsFull);

        var (value, found) = await skipList.TryGetValueAsync(10);
        Assert.True(found);
        Assert.Equal("ten", value);
    }
}
