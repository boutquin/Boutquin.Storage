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
/// This class contains unit tests for the LogSegmentedStorageEngine class.
/// Each test follows the Arrange-Act-Assert pattern.
/// </summary>
public sealed class LogSegmentedStorageEngineTests : IDisposable
{
    private readonly string _testFolderPath = "TestSegments";
    private readonly string _testFilePrefix = "TestSegment";
    private readonly long _maxSegmentSize = 1024; // 1 KB for test purposes
    private readonly IEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>> _entrySerializer;
    private readonly LogSegmentedStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>> _storageEngine;

    /// <summary>
    /// Initializes a new instance of the <see cref="LogSegmentedStorageEngineTests"/> class.
    /// </summary>
    public LogSegmentedStorageEngineTests()
    {
        _entrySerializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        Func<string, string, IEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>, long, IFileBasedStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>> storageEngineFactory =
            (fileLocation, fileName, serializer, maxSize) => new AppendOnlyFileStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(new StorageFile(fileLocation, fileName), serializer);

        _storageEngine = new LogSegmentedStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            _entrySerializer,
            _testFolderPath,
            _testFilePrefix,
            _maxSegmentSize,
            storageEngineFactory);
    }

    private void CleanupTestFiles()
    {
        if (Directory.Exists(_testFolderPath))
        {
            Directory.Delete(_testFolderPath, true);
        }
    }

    /// <summary>
    /// Test to ensure that the SetAsync method correctly appends a key-value pair to the storage engine.
    /// </summary>
    [Fact]
    public async Task SetAsync_ShouldAppendKeyValuePair()
    {
        // Arrange: Create a key-value pair to add.
        var key = new SerializableWrapper<int>(1);
        var value = new SerializableWrapper<string>("value1");

        // Act: Add the key-value pair to the storage engine.
        await _storageEngine.SetAsync(key, value);

        // Assert: Verify that the key-value pair was added correctly.
        var (retrievedValue, found) = await _storageEngine.TryGetValueAsync(key);
        Assert.True(found);
        Assert.Equal("value1", retrievedValue.Value);
    }

    /// <summary>
    /// Test to ensure that the TryGetValueAsync method correctly retrieves a value by its key.
    /// </summary>
    [Fact]
    public async Task TryGetValueAsync_ShouldRetrieveValueByKey()
    {
        // Arrange: Create and add a key-value pair to the storage engine.
        var key = new SerializableWrapper<int>(1);
        var value = new SerializableWrapper<string>("value1");
        await _storageEngine.SetAsync(key, value);

        // Act: Retrieve the value by key.
        var (retrievedValue, found) = await _storageEngine.TryGetValueAsync(key);

        // Assert: Verify that the value was retrieved correctly.
        Assert.True(found);
        Assert.Equal("value1", retrievedValue.Value);
    }

    /// <summary>
    /// Test to ensure that the ContainsKeyAsync method returns true if the key exists in the storage.
    /// </summary>
    [Fact]
    public async Task ContainsKeyAsync_ShouldReturnTrueIfKeyExists()
    {
        // Arrange: Create and add a key-value pair to the storage engine.
        var key = new SerializableWrapper<int>(1);
        var value = new SerializableWrapper<string>("value1");
        await _storageEngine.SetAsync(key, value);

        // Act: Check if the key exists.
        var exists = await _storageEngine.ContainsKeyAsync(key);

        // Assert: Verify that the key exists.
        Assert.True(exists);
    }

    /// <summary>
    /// Test to ensure that the SetBulkAsync method correctly appends multiple key-value pairs to the storage engine.
    /// </summary>
    [Fact]
    public async Task SetBulkAsync_ShouldAppendMultipleKeyValuePairs()
    {
        // Arrange: Create multiple key-value pairs to add.
        var items = new List<KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>>
        {
            new(new SerializableWrapper<int>(1), new SerializableWrapper<string>("value1")),
            new(new SerializableWrapper<int>(2), new SerializableWrapper<string>("value2")),
            new(new SerializableWrapper<int>(3), new SerializableWrapper<string>("value3"))
        };

        // Act: Add multiple key-value pairs to the storage engine.
        await _storageEngine.SetBulkAsync(items);

        // Assert: Verify that the key-value pairs were added correctly.
        foreach (var item in items)
        {
            var (retrievedValue, found) = await _storageEngine.TryGetValueAsync(item.Key);
            Assert.True(found);
            Assert.Equal(item.Value.Value, retrievedValue.Value);
        }
    }

    /// <summary>
    /// Test to ensure that the GetAllItemsAsync method retrieves all key-value pairs from the storage engine.
    /// </summary>
    [Fact]
    public async Task GetAllItemsAsync_ShouldRetrieveAllKeyValuePairs()
    {
        // Arrange: Create and add multiple key-value pairs to the storage engine.
        var items = new List<KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>>
        {
            new(new SerializableWrapper<int>(1), new SerializableWrapper<string>("value1")),
            new(new SerializableWrapper<int>(2), new SerializableWrapper<string>("value2")),
            new(new SerializableWrapper<int>(3), new SerializableWrapper<string>("value3"))
        };

        await _storageEngine.SetBulkAsync(items);

        // Act: Retrieve all key-value pairs from the storage engine.
        var retrievedItems = (await _storageEngine.GetAllItemsAsync()).ToList();

        // Assert: Verify that all key-value pairs were retrieved correctly.
        Assert.Equal(items.Count, retrievedItems.Count);
        foreach (var item in items)
        {
            Assert.Contains(retrievedItems, i => i.Key.Value == item.Key.Value && i.Value.Value == item.Value.Value);
        }
    }

    /// <summary>
    /// Test to ensure that the ClearAsync method clears the storage engine.
    /// </summary>
    [Fact]
    public async Task ClearAsync_ShouldClearStorageEngine()
    {
        // Arrange: Create and add a key-value pair to the storage engine.
        var key = new SerializableWrapper<int>(1);
        var value = new SerializableWrapper<string>("value1");
        await _storageEngine.SetAsync(key, value);

        // Act: Clear the storage engine.
        await _storageEngine.ClearAsync();

        // Assert: Verify that the storage engine was cleared.
        var (retrievedValue, found) = await _storageEngine.TryGetValueAsync(key);
        Assert.False(found);
    }

    /// <summary>
    /// Test to ensure that the CompactAsync method compacts the storage engine.
    /// </summary>
    [Fact]
    public async Task CompactAsync_ShouldCompactStorageEngine()
    {
        // Arrange: Create and add multiple key-value pairs with duplicate keys to the storage engine.
        await _storageEngine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("value1"));
        await _storageEngine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("value2"));

        // Act: Compact the storage engine.
        await _storageEngine.CompactAsync();

        // Assert: Verify that the storage engine contains only the latest key-value pair.
        var items = await _storageEngine.GetAllItemsAsync();
        Assert.Single(items);
        Assert.Equal("value2", items.First().Value.Value);
    }

    /// <summary>
    /// Test to ensure that the SetAsync method throws an exception when a segment size is exceeded.
    /// </summary>
    [Fact]
    public async Task SetAsync_ShouldCreateNewSegmentWhenSegmentSizeExceeded()
    {
        // Arrange: Create a key-value pair to add.
        var key = new SerializableWrapper<int>(1);
        var value = new SerializableWrapper<string>(new string('a', (int)_maxSegmentSize));

        // Act: Add the key-value pair to the storage engine.
        await _storageEngine.SetAsync(key, value);

        // Assert: Verify that a new segment was created.
        var newSegmentKey = new SerializableWrapper<int>(2);
        await _storageEngine.SetAsync(newSegmentKey, new SerializableWrapper<string>("value2"));
        var (retrievedValue, found) = await _storageEngine.TryGetValueAsync(newSegmentKey);
        Assert.True(found);
        Assert.Equal("value2", retrievedValue.Value);
    }

    /// <summary>
    /// Test to ensure that the SetAsync method throws an exception when an argument is null.
    /// </summary>
    [Fact]
    public async Task SetAsync_ShouldThrowExceptionIfArgumentIsNull()
    {
        // Arrange, Act & Assert: Check that an ArgumentNullException is thrown.
        await Assert.ThrowsAsync<ArgumentNullException>(() => _storageEngine.SetAsync(null, new SerializableWrapper<string>("value")));
        await Assert.ThrowsAsync<ArgumentNullException>(() => _storageEngine.SetAsync(new SerializableWrapper<int>(1), null));
    }

    /// <summary>
    /// Test to ensure that the SetBulkAsync method throws an exception when an argument is null.
    /// </summary>
    [Fact]
    public async Task SetBulkAsync_ShouldThrowExceptionIfArgumentIsNull()
    {
        // Arrange: Create a list with a null key-value pair.
        var items = new List<KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>>
        {
            new(null, new SerializableWrapper<string>("value"))
        };

        // Act & Assert: Check that an ArgumentNullException is thrown.
        await Assert.ThrowsAsync<ArgumentNullException>(() => _storageEngine.SetBulkAsync(items));
    }

    /// <summary>
    /// Test to ensure that the RemoveAsync method throws a NotSupportedException.
    /// </summary>
    [Fact]
    public async Task RemoveAsync_ShouldThrowNotSupportedException()
    {
        // Arrange: Create a key to remove.
        var key = new SerializableWrapper<int>(1);

        // Act & Assert: Check that a NotSupportedException is thrown.
        await Assert.ThrowsAsync<NotSupportedException>(() => _storageEngine.RemoveAsync(key));
    }

    /// <summary>
    /// Clean up the test files after each test.
    /// </summary>
    public void Dispose()
    {
        //CleanupTestFiles();
    }
}