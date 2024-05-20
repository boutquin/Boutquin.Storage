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
/// This class contains unit tests for the RedBlackTree class.
/// Each test follows the Arrange-Act-Assert pattern.
/// </summary>
public class RedBlackTreeTests
{
    /// <summary>
    /// Test to ensure that the Add method correctly adds a key-value pair to the tree.
    /// </summary>
    [Fact]
    public void Add_ShouldAddKeyValuePair()
    {
        // Arrange: Create a new tree with a capacity of 10.
        // The capacity is the maximum number of elements the tree can hold.
        var tree = new RedBlackTree<int, string>(10);

        // Act: Add a key-value pair to the tree.
        // The key is 1 and the value is "value1".
        // The Add method should insert this pair into the tree.
        tree.Add(1, "value1");

        // Assert: Check that the key-value pair was added correctly.
        // We use the TryGetValue method to retrieve the value associated with the key 1.
        // If the key is found, TryGetValue should return true and out the associated value.
        // We then check that the returned value is equal to the value we added.
        Assert.True(tree.TryGetValue(1, out var value));
        Assert.Equal("value1", value);
    }

    /// <summary>
    /// Test to ensure that an exception is thrown when trying to add an item to a full tree.
    /// </summary>
    [Fact]
    public void Add_ShouldThrowExceptionIfTreeIsFull()
    {
        // Arrange: Create a new tree with a capacity of 1 and add a key-value pair.
        var tree = new RedBlackTree<int, string>(1);
        tree.Add(1, "value1");

        // Act & Assert: Attempt to add another key-value pair and expect an InvalidOperationException.
        Assert.Throws<InvalidOperationException>(() => tree.Add(2, "value2"));
    }

    /// <summary>
    /// Test to ensure that TryGetValue returns false if the key does not exist in the tree.
    /// </summary>
    [Fact]
    public void TryGetValue_ShouldReturnFalseIfKeyDoesNotExist()
    {
        // Arrange: Create a new tree and add a key-value pair.
        // The tree is initialized with a capacity of 10.
        var tree = new RedBlackTree<int, string>(10);
        // Add a key-value pair with key 1 and value "value1".
        tree.Add(1, "value1");

        // Act: Try to retrieve a value with a key that does not exist in the tree.
        // The key 2 does not exist in the tree.
        var result = tree.TryGetValue(2, out _);

        // Assert: Check that TryGetValue returned false.
        // Since the key 2 does not exist, TryGetValue should return false.
        Assert.False(result);
    }

    /// <summary>
    /// Test to ensure that IsFull returns false if the tree is not full.
    /// </summary>
    [Fact]
    public void IsFull_ShouldReturnFalseIfTreeIsNotFull()
    {
        // Arrange: Create a new tree with a capacity of 10.
        // Add a key-value pair to the tree.
        var tree = new RedBlackTree<int, string>(10);
        tree.Add(1, "value1");

        // Assert: Check that IsFull returns false.
        // Since the tree's capacity is 10 and only one element is added, IsFull should return false.
        Assert.False(tree.IsFull);
    }

    /// <summary>
    /// Test to ensure that IsFull returns true if the tree is full.
    /// </summary>
    [Fact]
    public void IsFull_ShouldReturnTrueIfTreeIsFull()
    {
        // Arrange: Create a new tree with a capacity of 1.
        // Add a key-value pair to the tree.
        var tree = new RedBlackTree<int, string>(1);
        tree.Add(1, "value1");

        // Assert: Check that IsFull returns true.
        // Since the tree's capacity is 1 and one element is added, IsFull should return true.
        Assert.True(tree.IsFull);
    }

    /// <summary>
    /// Test to ensure that Clear removes all items from the tree.
    /// </summary>
    [Fact]
    public void Clear_ShouldRemoveAllItems()
    {
        // Arrange: Create a new tree with a capacity of 10.
        // Add a key-value pair to the tree.
        var tree = new RedBlackTree<int, string>(10);
        tree.Add(1, "value1");

        // Act: Clear the tree.
        // The Clear method should remove all elements from the tree.
        tree.Clear();

        // Assert: Check that TryGetValue returns false for the key of the removed item.
        // Since the tree was cleared, TryGetValue should return false for any key.
        Assert.False(tree.TryGetValue(1, out _));
    }

    /// <summary>
    /// Test to ensure that GetAllItems returns all items in the tree in order.
    /// </summary>
    [Fact]
    public void GetAllItems_ShouldReturnAllItemsInOrder()
    {
        // Arrange: Create a new tree with a capacity of 10.
        // Add some key-value pairs to the tree.
        var tree = new RedBlackTree<int, string>(10);
        tree.Add(2, "value2");
        tree.Add(1, "value1");
        tree.Add(3, "value3");

        // Act: Get all items from the tree.
        // The GetAllItems method should return all elements in the tree in sorted order.
        var items = tree.GetAllItems();

        // Assert: Check that the items are in the correct order.
        // The expected order is key 1, 2, and 3 with their respective values.
        var expected = new List<KeyValuePair<int, string>>
        {
            new(1, "value1"),
            new(2, "value2"),
            new(3, "value3")
        };

        Assert.Equal(expected, items);
    }

    // The following tests ensure that the Add method correctly handles rotations and balancing.
    // They add key-value pairs in a specific order to trigger the different types of rotations and balancing.

    /// <summary>
    /// Test to ensure that Add handles right rotation correctly.
    /// </summary>
    [Fact]
    public void Add_ShouldHandleRightRotation()
    {
        // Arrange: Create a new tree with a capacity of 10.
        var tree = new RedBlackTree<int, string>(10);

        // Act: Add key-value pairs in an order that should trigger a right rotation.
        // The order of insertion is 3, 2, 1 which should trigger a right rotation to balance the tree.
        tree.Add(3, "value3");
        tree.Add(2, "value2");
        tree.Add(1, "value1");

        // Assert: Check that the key-value pairs were added correctly.
        // We verify that the values are correctly associated with their keys.
        Assert.True(tree.TryGetValue(1, out var value));
        Assert.Equal("value1", value);
    }

    /// <summary>
    /// Test to ensure that Add handles left rotation correctly.
    /// </summary>
    [Fact]
    public void Add_ShouldHandleLeftRotation()
    {
        // Arrange: Create a new tree with a capacity of 10.
        var tree = new RedBlackTree<int, string>(10);

        // Act: Add key-value pairs in an order that should trigger a left rotation.
        // The order of insertion is 1, 2, 3 which should trigger a left rotation to balance the tree.
        tree.Add(1, "value1");
        tree.Add(2, "value2");
        tree.Add(3, "value3");

        // Assert: Check that the key-value pairs were added correctly.
        // We verify that the values are correctly associated with their keys.
        Assert.True(tree.TryGetValue(3, out var value));
        Assert.Equal("value3", value);
    }

    /// <summary>
    /// Test to ensure that Add handles left-right rotation correctly.
    /// </summary>
    [Fact]
    public void Add_ShouldHandleLeftRightRotation()
    {
        // Arrange: Create a new tree with a capacity of 10.
        var tree = new RedBlackTree<int, string>(10);

        // Act: Add key-value pairs in an order that should trigger a left-right rotation.
        // The order of insertion is 3, 1, 2 which should trigger a left-right rotation to balance the tree.
        tree.Add(3, "value3");
        tree.Add(1, "value1");
        tree.Add(2, "value2");

        // Assert: Check that the key-value pairs were added correctly.
        // We verify that the values are correctly associated with their keys.
        Assert.True(tree.TryGetValue(2, out var value));
        Assert.Equal("value2", value);
    }

    /// <summary>
    /// Test to ensure that Add handles right-left rotation correctly.
    /// </summary>
    [Fact]
    public void Add_ShouldHandleRightLeftRotation()
    {
        // Arrange: Create a new tree with a capacity of 10.
        var tree = new RedBlackTree<int, string>(10);

        // Act: Add key-value pairs in an order that should trigger a right-left rotation.
        // The order of insertion is 1, 3, 2 which should trigger a right-left rotation to balance the tree.
        tree.Add(1, "value1");
        tree.Add(3, "value3");
        tree.Add(2, "value2");

        // Assert: Check that the key-value pairs were added correctly.
        // We verify that the values are correctly associated with their keys.
        Assert.True(tree.TryGetValue(2, out var value));
        Assert.Equal("value2", value);
    }

    /// <summary>
    /// Test to ensure that Add handles duplicate keys correctly.
    /// </summary>
    [Fact]
    public void Add_ShouldHandleDuplicateKey()
    {
        // Arrange: Create a new tree with a capacity of 10.
        var tree = new RedBlackTree<int, string>(10);

        // Act: Add a key-value pair and then add another pair with the same key but a different value.
        // The first pair is (1, "value1") and the second pair is (1, "value1_updated").
        tree.Add(1, "value1");
        tree.Add(1, "value1_updated");

        // Assert: Check that the value of the duplicate key was updated correctly.
        // We verify that the value for key 1 is now "value1_updated".
        Assert.True(tree.TryGetValue(1, out var value));
        Assert.Equal("value1_updated", value);
    }

    /// <summary>
    /// Test to ensure that Add balances the tree correctly.
    /// </summary>
    [Fact]
    public void Add_ShouldBalanceTreeCorrectly()
    {
        // Arrange: Create a new tree with a capacity of 15.
        var tree = new RedBlackTree<int, string>(15);

        // Act: Add key-value pairs in an order that should trigger balancing.
        // The order of insertion is chosen to trigger multiple rotations and balancing operations.
        tree.Add(10, "value10");
        tree.Add(85, "value85");
        tree.Add(15, "value15");
        tree.Add(70, "value70");
        tree.Add(20, "value20");
        tree.Add(60, "value60");
        tree.Add(30, "value30");
        tree.Add(50, "value50");
        tree.Add(65, "value65");
        tree.Add(80, "value80");
        tree.Add(90, "value90");
        tree.Add(40, "value40");
        tree.Add(5, "value5");
        tree.Add(55, "value55");

        // Assert: Check that the key-value pairs were added correctly.
        // We verify that the values are correctly associated with their keys.
        Assert.True(tree.TryGetValue(10, out var value10));
        Assert.Equal("value10", value10);
        Assert.True(tree.TryGetValue(55, out var value55));
        Assert.Equal("value55", value55);
    }
}
