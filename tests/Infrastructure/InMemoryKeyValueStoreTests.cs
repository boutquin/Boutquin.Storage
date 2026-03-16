// Copyright (c) 2024-2026 Pierre G. Boutquin. All rights reserved.
//
//   Licensed under the Apache License, Version 2.0 (the "License").
//   You may not use this file except in compliance with the License.
//   You may obtain a copy of the License at
//
//       http://www.apache.org/licenses/LICENSE-2.0
//
//   Unless required by applicable law or agreed to in writing, software
//   distributed under the License is distributed on an "AS IS" BASIS,
//   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//
//   See the License for the specific language governing permissions and
//   limitations under the License.
//

#nullable disable

namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// This class contains unit tests for the <see cref="InMemoryKeyValueStore{TKey, TValue}"/> class.
/// Each test follows the Arrange-Act-Assert pattern.
/// </summary>
public sealed class InMemoryKeyValueStoreTests
{
    /// <summary>
    /// Test to ensure that SetAsync correctly adds a new key-value pair to the store.
    /// </summary>
    [Fact]
    public async Task SetAsync_ShouldAddNewKeyValuePair()
    {
        // Arrange: Create a new InMemoryKeyValueStore.
        var store = new InMemoryKeyValueStore<SerializableWrapper<int>, SerializableWrapper<string>>();

        // Act: Add a key-value pair to the store.
        await store.SetAsync(1, "value1");

        // Assert: Verify the key-value pair was added correctly.
        var (value, found) = await store.TryGetValueAsync(1);
        found.Should().BeTrue();
        ((string)value).Should().Be("value1");
    }

    /// <summary>
    /// Test to ensure that SetAsync correctly updates the value for an existing key.
    /// </summary>
    [Fact]
    public async Task SetAsync_ShouldUpdateExistingKey()
    {
        // Arrange: Create a store with an existing key-value pair.
        var store = new InMemoryKeyValueStore<SerializableWrapper<int>, SerializableWrapper<string>>();
        await store.SetAsync(1, "original");

        // Act: Update the value for the existing key.
        await store.SetAsync(1, "updated");

        // Assert: Verify the value was updated.
        var (value, found) = await store.TryGetValueAsync(1);
        found.Should().BeTrue();
        ((string)value).Should().Be("updated");
    }

    /// <summary>
    /// Test to ensure that SetAsync throws OperationCanceledException when the cancellation token is cancelled.
    /// </summary>
    [Fact]
    public async Task SetAsync_ShouldThrowOnCancellation()
    {
        // Arrange: Create a store and a cancelled cancellation token.
        var store = new InMemoryKeyValueStore<SerializableWrapper<int>, SerializableWrapper<string>>();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act: Attempt to set a value with the cancelled token.
        Func<Task> act = async () => await store.SetAsync(1, "value1", cts.Token);

        // Assert: Verify that the operation throws OperationCanceledException.
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>
    /// Test to ensure that TryGetValueAsync returns the value and true when the key exists.
    /// </summary>
    [Fact]
    public async Task TryGetValueAsync_ShouldReturnValueWhenKeyExists()
    {
        // Arrange: Create a store with a key-value pair.
        var store = new InMemoryKeyValueStore<SerializableWrapper<int>, SerializableWrapper<string>>();
        await store.SetAsync(1, "value1");

        // Act: Retrieve the value for the existing key.
        var (value, found) = await store.TryGetValueAsync(1);

        // Assert: Verify the value and found flag.
        found.Should().BeTrue();
        ((string)value).Should().Be("value1");
    }

    /// <summary>
    /// Test to ensure that TryGetValueAsync returns false when the key does not exist.
    /// </summary>
    [Fact]
    public async Task TryGetValueAsync_ShouldReturnFalseWhenKeyNotFound()
    {
        // Arrange: Create an empty store.
        var store = new InMemoryKeyValueStore<SerializableWrapper<int>, SerializableWrapper<string>>();

        // Act: Attempt to retrieve a non-existent key.
        var (_, found) = await store.TryGetValueAsync(42);

        // Assert: Verify that the key was not found.
        found.Should().BeFalse();
    }

    /// <summary>
    /// Test to ensure that ContainsKeyAsync returns true when the key exists in the store.
    /// </summary>
    [Fact]
    public async Task ContainsKeyAsync_ShouldReturnTrueWhenKeyExists()
    {
        // Arrange: Create a store with a key-value pair.
        var store = new InMemoryKeyValueStore<SerializableWrapper<int>, SerializableWrapper<string>>();
        await store.SetAsync(1, "value1");

        // Act: Check if the key exists.
        var contains = await store.ContainsKeyAsync(1);

        // Assert: Verify the key exists.
        contains.Should().BeTrue();
    }

    /// <summary>
    /// Test to ensure that ContainsKeyAsync returns false when the key does not exist in the store.
    /// </summary>
    [Fact]
    public async Task ContainsKeyAsync_ShouldReturnFalseWhenKeyNotExists()
    {
        // Arrange: Create an empty store.
        var store = new InMemoryKeyValueStore<SerializableWrapper<int>, SerializableWrapper<string>>();

        // Act: Check for a non-existent key.
        var contains = await store.ContainsKeyAsync(99);

        // Assert: Verify the key does not exist.
        contains.Should().BeFalse();
    }

    /// <summary>
    /// Test to ensure that RemoveAsync always throws NotSupportedException (append-only semantics).
    /// </summary>
    [Fact]
    public async Task RemoveAsync_ShouldThrowNotSupportedException()
    {
        // Arrange: Create a store with a key-value pair.
        var store = new InMemoryKeyValueStore<SerializableWrapper<int>, SerializableWrapper<string>>();
        await store.SetAsync(1, "value1");

        // Act: Attempt to remove the key.
        Func<Task> act = async () => await store.RemoveAsync(1);

        // Assert: Verify that NotSupportedException is thrown.
        await act.Should().ThrowAsync<NotSupportedException>();
    }

    /// <summary>
    /// Test to ensure that ClearAsync removes all items from the store.
    /// </summary>
    [Fact]
    public async Task ClearAsync_ShouldRemoveAllItems()
    {
        // Arrange: Create a store with multiple key-value pairs.
        var store = new InMemoryKeyValueStore<SerializableWrapper<int>, SerializableWrapper<string>>();
        await store.SetAsync(1, "value1");
        await store.SetAsync(2, "value2");
        await store.SetAsync(3, "value3");

        // Act: Clear the store.
        await store.ClearAsync();

        // Assert: Verify the store is empty.
        var items = await store.GetAllItemsAsync();
        items.Should().BeEmpty();
    }

    /// <summary>
    /// Test to ensure that GetAllItemsAsync returns all items in sorted key order.
    /// </summary>
    [Fact]
    public async Task GetAllItemsAsync_ShouldReturnItemsInSortedOrder()
    {
        // Arrange: Create a store and add items in non-sorted order.
        var store = new InMemoryKeyValueStore<SerializableWrapper<int>, SerializableWrapper<string>>();
        await store.SetAsync(3, "value3");
        await store.SetAsync(1, "value1");
        await store.SetAsync(2, "value2");

        // Act: Retrieve all items.
        var items = (await store.GetAllItemsAsync()).ToList();

        // Assert: Verify items are returned in sorted key order.
        items.Should().HaveCount(3);
        ((int)items[0].Key).Should().Be(1);
        ((string)items[0].Value).Should().Be("value1");
        ((int)items[1].Key).Should().Be(2);
        ((string)items[1].Value).Should().Be("value2");
        ((int)items[2].Key).Should().Be(3);
        ((string)items[2].Value).Should().Be("value3");
    }

    /// <summary>
    /// Test to ensure that GetAllItemsAsync returns an empty collection for an empty store.
    /// </summary>
    [Fact]
    public async Task GetAllItemsAsync_ShouldReturnEmptyForEmptyStore()
    {
        // Arrange: Create an empty store.
        var store = new InMemoryKeyValueStore<SerializableWrapper<int>, SerializableWrapper<string>>();

        // Act: Retrieve all items.
        var items = await store.GetAllItemsAsync();

        // Assert: Verify the result is empty.
        items.Should().BeEmpty();
    }

    /// <summary>
    /// Test to ensure that SetBulkAsync correctly adds multiple items to the store.
    /// </summary>
    [Fact]
    public async Task SetBulkAsync_ShouldAddMultipleItems()
    {
        // Arrange: Create a store and a collection of items to add.
        var store = new InMemoryKeyValueStore<SerializableWrapper<int>, SerializableWrapper<string>>();
        var items = new List<KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>>
        {
            new(1, "value1"),
            new(2, "value2"),
            new(3, "value3")
        };

        // Act: Add items in bulk.
        await store.SetBulkAsync(items);

        // Assert: Verify all items were added.
        var allItems = (await store.GetAllItemsAsync()).ToList();
        allItems.Should().HaveCount(3);
        ((string)allItems[0].Value).Should().Be("value1");
        ((string)allItems[1].Value).Should().Be("value2");
        ((string)allItems[2].Value).Should().Be("value3");
    }

    /// <summary>
    /// Test to ensure that SetBulkAsync correctly updates values for existing keys.
    /// </summary>
    [Fact]
    public async Task SetBulkAsync_ShouldUpdateExistingKeys()
    {
        // Arrange: Create a store with existing items.
        var store = new InMemoryKeyValueStore<SerializableWrapper<int>, SerializableWrapper<string>>();
        await store.SetAsync(1, "original1");
        await store.SetAsync(2, "original2");

        var updates = new List<KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>>
        {
            new(1, "updated1"),
            new(2, "updated2"),
            new(3, "new3")
        };

        // Act: Bulk set with updates and a new item.
        await store.SetBulkAsync(updates);

        // Assert: Verify existing keys were updated and new key was added.
        var allItems = (await store.GetAllItemsAsync()).ToList();
        allItems.Should().HaveCount(3);
        ((string)allItems[0].Value).Should().Be("updated1");
        ((string)allItems[1].Value).Should().Be("updated2");
        ((string)allItems[2].Value).Should().Be("new3");
    }
}
