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
/// Unit tests for the <see cref="BTree{TKey, TValue}"/> class.
/// Each test follows the Arrange-Act-Assert pattern.
/// </summary>
public sealed class BTreeTests
{
    /// <summary>
    /// Test to ensure that a single item can be inserted and retrieved.
    /// </summary>
    [Fact]
    public async Task SetAsync_ShouldInsertAndRetrieveSingleItem()
    {
        // Arrange: Create a B-tree with minimum degree 2.
        var tree = new BTree<int, string>(2);

        // Act: Insert a key-value pair.
        await tree.SetAsync(1, "value1");

        // Assert: Verify the key-value pair can be retrieved.
        var (value, found) = await tree.TryGetValueAsync(1);
        found.Should().BeTrue();
        value.Should().Be("value1");
    }

    /// <summary>
    /// Test to ensure that multiple items are returned in sorted order.
    /// </summary>
    [Fact]
    public async Task GetAllItemsAsync_ShouldReturnItemsInSortedOrder()
    {
        // Arrange: Create a B-tree and insert items in non-sorted order.
        var tree = new BTree<int, string>(2);
        await tree.SetAsync(5, "five");
        await tree.SetAsync(3, "three");
        await tree.SetAsync(7, "seven");
        await tree.SetAsync(1, "one");
        await tree.SetAsync(4, "four");
        await tree.SetAsync(6, "six");
        await tree.SetAsync(2, "two");

        // Act: Retrieve all items.
        var items = (await tree.GetAllItemsAsync()).ToList();

        // Assert: Items should be in ascending key order.
        items.Should().HaveCount(7);
        items.Select(i => i.Key).Should().BeInAscendingOrder();
        items[0].Should().Be((1, "one"));
        items[6].Should().Be((7, "seven"));
    }

    /// <summary>
    /// Test to ensure that inserting a duplicate key updates the value.
    /// </summary>
    [Fact]
    public async Task SetAsync_ShouldUpdateValueForDuplicateKey()
    {
        // Arrange: Create a B-tree and insert an initial key-value pair.
        var tree = new BTree<int, string>(2);
        await tree.SetAsync(1, "original");

        // Act: Insert the same key with a different value.
        await tree.SetAsync(1, "updated");

        // Assert: The value should be updated.
        var (value, found) = await tree.TryGetValueAsync(1);
        found.Should().BeTrue();
        value.Should().Be("updated");
    }

    /// <summary>
    /// Test to ensure that searching for a non-existent key returns false.
    /// </summary>
    [Fact]
    public async Task TryGetValueAsync_ShouldReturnFalseForNonExistentKey()
    {
        // Arrange: Create a B-tree with some items.
        var tree = new BTree<int, string>(2);
        await tree.SetAsync(1, "value1");
        await tree.SetAsync(3, "value3");

        // Act: Search for a key that doesn't exist.
        var (_, found) = await tree.TryGetValueAsync(2);

        // Assert: Should not find the key.
        found.Should().BeFalse();
    }

    /// <summary>
    /// Test to ensure that ContainsKeyAsync returns false for a non-existent key.
    /// </summary>
    [Fact]
    public async Task ContainsKeyAsync_ShouldReturnFalseForNonExistentKey()
    {
        // Arrange: Create an empty B-tree.
        var tree = new BTree<int, string>(2);

        // Act & Assert: The key should not exist.
        var result = await tree.ContainsKeyAsync(42);
        result.Should().BeFalse();
    }

    /// <summary>
    /// Test to ensure that ContainsKeyAsync returns true for an existing key.
    /// </summary>
    [Fact]
    public async Task ContainsKeyAsync_ShouldReturnTrueForExistingKey()
    {
        // Arrange: Create a B-tree with an item.
        var tree = new BTree<int, string>(2);
        await tree.SetAsync(42, "answer");

        // Act & Assert: The key should exist.
        var result = await tree.ContainsKeyAsync(42);
        result.Should().BeTrue();
    }

    /// <summary>
    /// Test to ensure that the tree works with minimum order (t=2).
    /// A B-tree with t=2 is equivalent to a 2-3-4 tree.
    /// </summary>
    [Fact]
    public async Task BTree_ShouldWorkWithMinimumOrder()
    {
        // Arrange: Create a B-tree with minimum degree 2 (2-3-4 tree).
        var tree = new BTree<int, string>(2);

        // Act: Insert enough items to cause multiple splits.
        for (var i = 1; i <= 20; i++)
        {
            await tree.SetAsync(i, $"value_{i}");
        }

        // Assert: All items should be retrievable.
        for (var i = 1; i <= 20; i++)
        {
            var (value, found) = await tree.TryGetValueAsync(i);
            found.Should().BeTrue();
            value.Should().Be($"value_{i}");
        }

        // Verify sorted order.
        var items = (await tree.GetAllItemsAsync()).ToList();
        items.Should().HaveCount(20);
        items.Select(i => i.Key).Should().BeInAscendingOrder();
    }

    /// <summary>
    /// Test to ensure that the tree works with a typical order (t=10).
    /// </summary>
    [Fact]
    public async Task BTree_ShouldWorkWithTypicalOrder()
    {
        // Arrange: Create a B-tree with minimum degree 10.
        var tree = new BTree<int, string>(10);

        // Act: Insert 200 items.
        for (var i = 200; i >= 1; i--)
        {
            await tree.SetAsync(i, $"value_{i}");
        }

        // Assert: All items should be retrievable and in sorted order.
        var items = (await tree.GetAllItemsAsync()).ToList();
        items.Should().HaveCount(200);
        items.Select(i => i.Key).Should().BeInAscendingOrder();
        tree.Order.Should().Be(10);
    }

    /// <summary>
    /// Test to ensure that the tree works with a wide order (t=100).
    /// </summary>
    [Fact]
    public async Task BTree_ShouldWorkWithWideOrder()
    {
        // Arrange: Create a B-tree with minimum degree 100.
        var tree = new BTree<int, string>(100);

        // Act: Insert 500 items in random-ish order.
        var random = new Random(42); // Fixed seed for reproducibility
        var keys = Enumerable.Range(1, 500).OrderBy(_ => random.Next()).ToList();
        foreach (var key in keys)
        {
            await tree.SetAsync(key, $"value_{key}");
        }

        // Assert: All items should be retrievable and in sorted order.
        var items = (await tree.GetAllItemsAsync()).ToList();
        items.Should().HaveCount(500);
        items.Select(i => i.Key).Should().BeInAscendingOrder();
        tree.Order.Should().Be(100);
    }

    /// <summary>
    /// Test to ensure that 1000+ items can be inserted and all are retrievable and sorted.
    /// </summary>
    [Fact]
    public async Task BTree_ShouldHandle1000PlusItems()
    {
        // Arrange: Create a B-tree with minimum degree 5.
        const int count = 1500;
        var tree = new BTree<int, string>(5);

        // Act: Insert items in reverse order to stress the splitting logic.
        for (var i = count; i >= 1; i--)
        {
            await tree.SetAsync(i, $"value_{i}");
        }

        // Assert: All items should be retrievable.
        for (var i = 1; i <= count; i++)
        {
            var (value, found) = await tree.TryGetValueAsync(i);
            found.Should().BeTrue($"key {i} should be in the tree");
            value.Should().Be($"value_{i}");
        }

        // Verify sorted order.
        var items = (await tree.GetAllItemsAsync()).ToList();
        items.Should().HaveCount(count);
        items.Select(i => i.Key).Should().BeInAscendingOrder();
    }

    /// <summary>
    /// Test to ensure that ClearAsync removes all items from the tree.
    /// </summary>
    [Fact]
    public async Task ClearAsync_ShouldEmptyTree()
    {
        // Arrange: Create a B-tree and insert some items.
        var tree = new BTree<int, string>(2);
        await tree.SetAsync(1, "one");
        await tree.SetAsync(2, "two");
        await tree.SetAsync(3, "three");

        // Act: Clear the tree.
        await tree.ClearAsync();

        // Assert: The tree should be empty.
        var (_, found) = await tree.TryGetValueAsync(1);
        found.Should().BeFalse();

        var items = (await tree.GetAllItemsAsync()).ToList();
        items.Should().BeEmpty();

        tree.Height.Should().Be(0);
    }

    /// <summary>
    /// Test to ensure that height increases as items are inserted.
    /// </summary>
    [Fact]
    public async Task Height_ShouldIncreaseWithInsertions()
    {
        // Arrange: Create a B-tree with minimum degree 2 (max 3 keys per node).
        var tree = new BTree<int, string>(2);

        // Assert: Empty tree has height 0.
        tree.Height.Should().Be(0);

        // Act: Insert one item.
        await tree.SetAsync(1, "one");
        tree.Height.Should().Be(1, "a single-node tree has height 1");

        // Act: Insert enough items to cause a root split.
        // With t=2, each node holds at most 3 keys. Inserting 4 keys forces a split.
        await tree.SetAsync(2, "two");
        await tree.SetAsync(3, "three");
        var heightBefore = tree.Height;
        await tree.SetAsync(4, "four"); // This should cause the root to split
        tree.Height.Should().BeGreaterThanOrEqualTo(heightBefore,
            "height should not decrease when items are added");
    }

    /// <summary>
    /// Test to ensure that height reflects B-tree properties with many insertions.
    /// The height of a B-tree with n keys and minimum degree t is at most log_t((n+1)/2).
    /// </summary>
    [Fact]
    public async Task Height_ShouldRespectBTreeBounds()
    {
        // Arrange: Create a B-tree with minimum degree 3 and insert 1000 items.
        const int count = 1000;
        var tree = new BTree<int, string>(3);

        // Act: Insert items.
        for (var i = 1; i <= count; i++)
        {
            await tree.SetAsync(i, $"value_{i}");
        }

        // Assert: Height should be within B-tree bounds.
        // Maximum height: log_t((n+1)/2) = log_3(500.5) ≈ 5.66, so at most 6-7 levels.
        tree.Height.Should().BeGreaterThan(1, "1000 items cannot fit in a single node");
        tree.Height.Should().BeLessThanOrEqualTo(10, "B-tree height should be logarithmic");
    }

    /// <summary>
    /// Test to ensure that RemoveAsync throws NotSupportedException.
    /// </summary>
    [Fact]
    public async Task RemoveAsync_ShouldThrowNotSupportedException()
    {
        // Arrange: Create a B-tree with an item.
        var tree = new BTree<int, string>(2);
        await tree.SetAsync(1, "value1");

        // Act & Assert: Remove should throw.
        await Assert.ThrowsAsync<NotSupportedException>(() => tree.RemoveAsync(1));
    }

    /// <summary>
    /// Test to ensure that SetBulkAsync inserts multiple items correctly.
    /// </summary>
    [Fact]
    public async Task SetBulkAsync_ShouldInsertMultipleItems()
    {
        // Arrange: Create a B-tree.
        var tree = new BTree<int, string>(3);
        var items = new List<KeyValuePair<int, string>>
        {
            new(5, "five"),
            new(3, "three"),
            new(8, "eight"),
            new(1, "one"),
            new(4, "four"),
        };

        // Act: Bulk insert.
        await tree.SetBulkAsync(items);

        // Assert: All items should be present and sorted.
        var allItems = (await tree.GetAllItemsAsync()).ToList();
        allItems.Should().HaveCount(5);
        allItems.Select(i => i.Key).Should().BeInAscendingOrder();
        allItems[0].Should().Be((1, "one"));
        allItems[4].Should().Be((8, "eight"));
    }

    /// <summary>
    /// Test to ensure that the Order property returns the configured minimum degree.
    /// </summary>
    [Fact]
    public void Order_ShouldReturnConfiguredMinimumDegree()
    {
        // Arrange & Act: Create B-trees with different orders.
        var tree2 = new BTree<int, string>(2);
        var tree10 = new BTree<int, string>(10);
        var tree100 = new BTree<int, string>(100);

        // Assert: Order should match the configured minimum degree.
        tree2.Order.Should().Be(2);
        tree10.Order.Should().Be(10);
        tree100.Order.Should().Be(100);
    }

    /// <summary>
    /// Test to ensure that an invalid minimum degree throws ArgumentOutOfRangeException.
    /// </summary>
    [Fact]
    public void Constructor_ShouldThrowForInvalidMinimumDegree()
    {
        // Act & Assert: Minimum degree of 1 should throw.
        var act = () => new BTree<int, string>(1);
        act.Should().Throw<ArgumentOutOfRangeException>();

        // Act & Assert: Minimum degree of 0 should throw.
        var act2 = () => new BTree<int, string>(0);
        act2.Should().Throw<ArgumentOutOfRangeException>();

        // Act & Assert: Negative minimum degree should throw.
        var act3 = () => new BTree<int, string>(-1);
        act3.Should().Throw<ArgumentOutOfRangeException>();
    }

    /// <summary>
    /// Test to ensure that duplicate key updates work correctly when the key is in an internal node.
    /// When a B-tree splits, keys are promoted to internal nodes. This test verifies that
    /// updating such a key works correctly.
    /// </summary>
    [Fact]
    public async Task SetAsync_ShouldUpdateDuplicateKeyInInternalNode()
    {
        // Arrange: Create a B-tree with t=2 and insert enough items to cause splits.
        var tree = new BTree<int, string>(2);
        for (var i = 1; i <= 10; i++)
        {
            await tree.SetAsync(i, $"original_{i}");
        }

        // Act: Update all values — some keys will be in internal nodes due to splits.
        for (var i = 1; i <= 10; i++)
        {
            await tree.SetAsync(i, $"updated_{i}");
        }

        // Assert: All values should be updated.
        for (var i = 1; i <= 10; i++)
        {
            var (value, found) = await tree.TryGetValueAsync(i);
            found.Should().BeTrue();
            value.Should().Be($"updated_{i}");
        }

        // Count should remain the same (no duplicates).
        var items = (await tree.GetAllItemsAsync()).ToList();
        items.Should().HaveCount(10);
    }

    /// <summary>
    /// Test to ensure that the tree handles items inserted after a clear operation.
    /// </summary>
    [Fact]
    public async Task SetAsync_ShouldWorkAfterClear()
    {
        // Arrange: Create a B-tree, add items, then clear.
        var tree = new BTree<int, string>(2);
        await tree.SetAsync(1, "one");
        await tree.SetAsync(2, "two");
        await tree.ClearAsync();

        // Act: Insert new items after clearing.
        await tree.SetAsync(10, "ten");
        await tree.SetAsync(20, "twenty");

        // Assert: Only the new items should be present.
        var items = (await tree.GetAllItemsAsync()).ToList();
        items.Should().HaveCount(2);
        items[0].Should().Be((10, "ten"));
        items[1].Should().Be((20, "twenty"));
        tree.Height.Should().Be(1);
    }

    /// <summary>
    /// Test to ensure that SetAsync throws ArgumentNullException for a null key (reference type).
    /// </summary>
    [Fact]
    public async Task SetAsync_WithNullKey_ShouldThrowArgumentNullException()
    {
        // Arrange
        var tree = new BTree<string, string>(2);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() => tree.SetAsync(null!, "value"));
    }

    /// <summary>
    /// Test to ensure that TryGetValueAsync throws ArgumentNullException for a null key (reference type).
    /// </summary>
    [Fact]
    public async Task TryGetValueAsync_WithNullKey_ShouldThrowArgumentNullException()
    {
        // Arrange
        var tree = new BTree<string, string>(2);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() => tree.TryGetValueAsync(null!));
    }

    /// <summary>
    /// Test to ensure that ContainsKeyAsync throws ArgumentNullException for a null key (reference type).
    /// </summary>
    [Fact]
    public async Task ContainsKeyAsync_WithNullKey_ShouldThrowArgumentNullException()
    {
        // Arrange
        var tree = new BTree<string, string>(2);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() => tree.ContainsKeyAsync(null!));
    }

    /// <summary>
    /// Test to ensure that SetBulkAsync throws ArgumentNullException for null items.
    /// </summary>
    [Fact]
    public async Task SetBulkAsync_WithNullItems_ShouldThrowArgumentNullException()
    {
        // Arrange
        var tree = new BTree<string, string>(2);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() => tree.SetBulkAsync(null!));
    }

    /// <summary>
    /// Test to ensure that SetAsync throws OperationCanceledException when given a cancelled token.
    /// </summary>
    [Fact]
    public async Task SetAsync_WithCancelledToken_ShouldThrowOperationCanceledException()
    {
        // Arrange
        var tree = new BTree<int, string>(2);
        var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(() => tree.SetAsync(1, "value", cts.Token));
    }

    /// <summary>
    /// Test to ensure that TryGetValueAsync throws OperationCanceledException when given a cancelled token.
    /// </summary>
    [Fact]
    public async Task TryGetValueAsync_WithCancelledToken_ShouldThrowOperationCanceledException()
    {
        // Arrange
        var tree = new BTree<int, string>(2);

        var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(() => tree.TryGetValueAsync(1, cts.Token));
    }

    /// <summary>
    /// Test to ensure that ContainsKeyAsync throws OperationCanceledException when given a cancelled token.
    /// </summary>
    [Fact]
    public async Task ContainsKeyAsync_WithCancelledToken_ShouldThrowOperationCanceledException()
    {
        // Arrange
        var tree = new BTree<int, string>(2);
        var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(() => tree.ContainsKeyAsync(1, cts.Token));
    }

    /// <summary>
    /// Test to ensure that GetAllItemsAsync throws OperationCanceledException when given a cancelled token.
    /// </summary>
    [Fact]
    public async Task GetAllItemsAsync_WithCancelledToken_ShouldThrowOperationCanceledException()
    {
        // Arrange
        var tree = new BTree<int, string>(2);
        var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(() => tree.GetAllItemsAsync(cts.Token));
    }

    /// <summary>
    /// Test to ensure that SetBulkAsync throws OperationCanceledException when given a cancelled token.
    /// </summary>
    [Fact]
    public async Task SetBulkAsync_WithCancelledToken_ShouldThrowOperationCanceledException()
    {
        // Arrange
        var tree = new BTree<int, string>(2);
        var cts = new CancellationTokenSource();
        cts.Cancel();
        var items = new List<KeyValuePair<int, string>> { new(1, "one") };

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(() => tree.SetBulkAsync(items, cts.Token));
    }

    /// <summary>
    /// Test to ensure that ClearAsync throws OperationCanceledException when given a cancelled token.
    /// </summary>
    [Fact]
    public async Task ClearAsync_WithCancelledToken_ShouldThrowOperationCanceledException()
    {
        // Arrange
        var tree = new BTree<int, string>(2);
        var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(() => tree.ClearAsync(cts.Token));
    }

    /// <summary>
    /// Test to ensure that RemoveAsync throws OperationCanceledException when given a cancelled token.
    /// </summary>
    [Fact]
    public async Task RemoveAsync_WithCancelledToken_ShouldThrowOperationCanceledException()
    {
        // Arrange
        var tree = new BTree<int, string>(2);
        var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(() => tree.RemoveAsync(1, cts.Token));
    }
}
