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
/// Unit tests for the BPlusTree class.
/// Each test follows the Arrange-Act-Assert pattern.
/// </summary>
public sealed class BPlusTreeTests
{
    /// <summary>
    /// Test insert and retrieve single item.
    /// </summary>
    [Fact]
    public async Task Insert_AndRetrieve_SingleItem()
    {
        // Arrange
        var tree = new BPlusTree<int, string>(3);

        // Act
        await tree.SetAsync(1, "value1");
        var (value, found) = await tree.TryGetValueAsync(1);

        // Assert
        Assert.True(found);
        Assert.Equal("value1", value);
    }

    /// <summary>
    /// Test insert many items and retrieve all sorted.
    /// </summary>
    [Fact]
    public async Task Insert_ManyItems_RetrieveAllSorted()
    {
        // Arrange
        var tree = new BPlusTree<int, string>(2);

        // Act
        for (var i = 10; i >= 1; i--)
        {
            await tree.SetAsync(i, $"value{i}");
        }

        var items = (await tree.GetAllItemsAsync()).ToList();

        // Assert
        Assert.Equal(10, items.Count);
        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(i + 1, items[i].Key);
        }
    }

    /// <summary>
    /// Test update existing key.
    /// </summary>
    [Fact]
    public async Task Update_ExistingKey()
    {
        // Arrange
        var tree = new BPlusTree<int, string>(3);
        await tree.SetAsync(1, "original");

        // Act
        await tree.SetAsync(1, "updated");
        var (value, found) = await tree.TryGetValueAsync(1);

        // Assert
        Assert.True(found);
        Assert.Equal("updated", value);
    }

    /// <summary>
    /// Test remove existing key.
    /// </summary>
    [Fact]
    public async Task Remove_ExistingKey()
    {
        // Arrange
        var tree = new BPlusTree<int, string>(3);
        await tree.SetAsync(1, "value1");

        // Act
        await tree.RemoveAsync(1);

        // Assert
        Assert.False(await tree.ContainsKeyAsync(1));
    }

    /// <summary>
    /// Test ContainsKey returns true and false correctly.
    /// </summary>
    [Fact]
    public async Task ContainsKey_ReturnsCorrectResult()
    {
        // Arrange
        var tree = new BPlusTree<int, string>(3);
        await tree.SetAsync(1, "value1");

        // Assert
        Assert.True(await tree.ContainsKeyAsync(1));
        Assert.False(await tree.ContainsKeyAsync(2));
    }

    /// <summary>
    /// Test RangeQuery returns correct subset.
    /// </summary>
    [Fact]
    public async Task RangeQuery_ReturnsCorrectSubset()
    {
        // Arrange
        var tree = new BPlusTree<int, string>(2);
        for (var i = 1; i <= 10; i++)
        {
            await tree.SetAsync(i, $"value{i}");
        }

        // Act
        var range = (await tree.RangeQueryAsync(3, 7)).ToList();

        // Assert
        Assert.Equal(5, range.Count);
        Assert.Equal(3, range[0].Key);
        Assert.Equal(7, range[4].Key);
    }

    /// <summary>
    /// Test RangeQuery with no matches returns empty.
    /// </summary>
    [Fact]
    public async Task RangeQuery_NoMatches_ReturnsEmpty()
    {
        // Arrange
        var tree = new BPlusTree<int, string>(3);
        await tree.SetAsync(1, "one");
        await tree.SetAsync(2, "two");

        // Act
        var range = (await tree.RangeQueryAsync(10, 20)).ToList();

        // Assert
        Assert.Empty(range);
    }

    /// <summary>
    /// Test that node splitting works correctly by inserting more than 2t-1 keys.
    /// </summary>
    [Fact]
    public async Task NodeSplitting_WorksCorrectly()
    {
        // Arrange — order 2 means max 3 keys per node, split at 4
        var tree = new BPlusTree<int, string>(2);

        // Act — insert enough keys to trigger splits
        for (var i = 1; i <= 20; i++)
        {
            await tree.SetAsync(i, $"value{i}");
        }

        // Assert — all 20 keys are retrievable and sorted
        var items = (await tree.GetAllItemsAsync()).ToList();
        Assert.Equal(20, items.Count);
        for (var i = 0; i < 20; i++)
        {
            Assert.Equal(i + 1, items[i].Key);
        }
    }

    /// <summary>
    /// Test that Height increases after sufficient inserts.
    /// </summary>
    [Fact]
    public async Task Height_Increases_AfterSufficientInserts()
    {
        // Arrange
        var tree = new BPlusTree<int, string>(2);
        var initialHeight = tree.Height;

        // Act — insert enough keys to trigger root split
        for (var i = 1; i <= 10; i++)
        {
            await tree.SetAsync(i, $"value{i}");
        }

        // Assert
        Assert.True(tree.Height > initialHeight);
    }

    /// <summary>
    /// Test that GetAllItems returns sorted order.
    /// </summary>
    [Fact]
    public async Task GetAllItems_ReturnsSortedOrder()
    {
        // Arrange
        var tree = new BPlusTree<int, string>(3);
        await tree.SetAsync(5, "five");
        await tree.SetAsync(1, "one");
        await tree.SetAsync(3, "three");

        // Act
        var items = (await tree.GetAllItemsAsync()).ToList();

        // Assert
        Assert.Equal(3, items.Count);
        Assert.Equal(1, items[0].Key);
        Assert.Equal(3, items[1].Key);
        Assert.Equal(5, items[2].Key);
    }

    /// <summary>
    /// Test that order less than 2 throws ArgumentOutOfRangeException.
    /// </summary>
    [Fact]
    public void Constructor_OrderLessThan2_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BPlusTree<int, string>(1));
    }

    /// <summary>
    /// Test that RangeQuery with equal start and end returns the single matching key.
    /// This validates the boundary case where the range collapses to a point query.
    /// </summary>
    [Fact]
    public async Task RangeQuery_EqualStartAndEnd_ReturnsSingleKey()
    {
        // Arrange
        var tree = new BPlusTree<int, string>(3);
        for (var i = 1; i <= 5; i++)
        {
            await tree.SetAsync(i, $"value{i}");
        }

        // Act
        var range = (await tree.RangeQueryAsync(3, 3)).ToList();

        // Assert
        Assert.Single(range);
        Assert.Equal(3, range[0].Key);
        Assert.Equal("value3", range[0].Value);
    }

    /// <summary>
    /// Test that items can be added after Clear, verifying the tree is fully reset.
    /// </summary>
    [Fact]
    public async Task Clear_ThenReAdd_WorksCorrectly()
    {
        // Arrange
        var tree = new BPlusTree<int, string>(3);
        await tree.SetAsync(1, "one");
        await tree.SetAsync(2, "two");
        await tree.SetAsync(3, "three");

        // Act — clear and re-add different items
        await tree.ClearAsync();
        await tree.SetAsync(10, "ten");
        await tree.SetAsync(20, "twenty");

        // Assert — only the new items should exist
        Assert.False(await tree.ContainsKeyAsync(1));
        Assert.False(await tree.ContainsKeyAsync(2));
        Assert.False(await tree.ContainsKeyAsync(3));

        var items = (await tree.GetAllItemsAsync()).ToList();
        Assert.Equal(2, items.Count);
        Assert.Equal(10, items[0].Key);
        Assert.Equal(20, items[1].Key);
    }
}
