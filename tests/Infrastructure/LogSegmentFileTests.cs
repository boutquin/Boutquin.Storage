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
namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// This class contains unit tests for the LogSegmentFile class.
/// Each test follows the Arrange-Act-Assert pattern.
/// </summary>
public sealed class LogSegmentFileTests : IDisposable
{
    private readonly string _testFilePath = "LogSegmentFileTests.dat";
    private readonly ITestOutputHelper _output;

    public LogSegmentFileTests(ITestOutputHelper output)
    {
        _output = output;
        CleanupTestFiles();
    }

    private void CleanupTestFiles()
    {
        if (File.Exists(_testFilePath))
        {
            File.Delete(_testFilePath);
        }
    }

    /// <summary>
    /// Test to ensure that the SetAsync method correctly appends a key-value pair to the segment file.
    /// </summary>
    [Fact]
    public async Task SetAsync_ShouldAppendKeyValuePair()
    {
        // Arrange: Create a storage file, serializer, and storage engine.
        var storageFile = new StorageFile(_testFilePath);
        var entrySerializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        var segmentFile = new LogSegmentFile<SerializableWrapper<int>, SerializableWrapper<string>>(storageFile, entrySerializer, 1024);

        var key = new SerializableWrapper<int>(1);
        var value = new SerializableWrapper<string>("value1");

        // Act: Write the key-value pair to the segment file.
        await segmentFile.SetAsync(key, value);

        // Assert: Verify that the segment file contains the expected serialized key-value pair.
        await using var fileStream = File.OpenRead(_testFilePath);
        var reader = new BinaryReader(fileStream, Encoding.UTF8, leaveOpen: true);
        Assert.Equal(1, reader.ReadInt32());
        Assert.Equal("value1", reader.ReadString());
    }

    /// <summary>
    /// Test to ensure that the TryGetValueAsync method correctly retrieves a value by its key.
    /// </summary>
    [Fact]
    public async Task TryGetValueAsync_ShouldRetrieveValueByKey()
    {
        // Arrange: Create a storage file, serializer, and storage engine.
        var storageFile = new StorageFile(_testFilePath);
        var entrySerializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        var segmentFile = new LogSegmentFile<SerializableWrapper<int>, SerializableWrapper<string>>(storageFile, entrySerializer, 1024);

        var key = new SerializableWrapper<int>(1);
        var value = new SerializableWrapper<string>("value1");

        await segmentFile.SetAsync(key, value);

        // Act: Retrieve the value by key.
        var (retrievedValue, found) = await segmentFile.TryGetValueAsync(key);

        // Assert: Check that the value was retrieved correctly.
        Assert.True(found);
        Assert.Equal("value1", retrievedValue.Value);
    }

    /// <summary>
    /// Test to ensure that the ContainsKeyAsync method returns true if the key exists in the segment file.
    /// </summary>
    [Fact]
    public async Task ContainsKeyAsync_ShouldReturnTrueIfKeyExists()
    {
        // Arrange: Create a storage file, serializer, and storage engine.
        var storageFile = new StorageFile(_testFilePath);
        var entrySerializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        var segmentFile = new LogSegmentFile<SerializableWrapper<int>, SerializableWrapper<string>>(storageFile, entrySerializer, 1024);

        var key = new SerializableWrapper<int>(1);
        var value = new SerializableWrapper<string>("value1");

        await segmentFile.SetAsync(key, value);

        // Act: Check if the key exists.
        var exists = await segmentFile.ContainsKeyAsync(key);

        // Assert: Check that the key exists.
        Assert.True(exists);
    }

    /// <summary>
    /// Test to ensure that the GetAllItemsAsync method retrieves all key-value pairs from the segment file.
    /// </summary>
    [Fact]
    public async Task GetAllItemsAsync_ShouldRetrieveAllKeyValuePairs()
    {
        // Arrange: Create a storage file, serializer, and storage engine.
        var storageFile = new StorageFile(_testFilePath);
        var entrySerializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        var segmentFile = new LogSegmentFile<SerializableWrapper<int>, SerializableWrapper<string>>(storageFile, entrySerializer, 1024);

        var items = new List<KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>>
        {
            new KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>(new SerializableWrapper<int>(1), new SerializableWrapper<string>("value1")),
            new KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>(new SerializableWrapper<int>(2), new SerializableWrapper<string>("value2")),
            new KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>(new SerializableWrapper<int>(3), new SerializableWrapper<string>("value3"))
        };

        foreach (var item in items)
        {
            await segmentFile.SetAsync(item.Key, item.Value);
        }

        // Act: Retrieve all key-value pairs from the segment file.
        var retrievedItems = (await segmentFile.GetAllItemsAsync()).ToList();

        // Assert: Check that all key-value pairs were retrieved correctly.
        Assert.Equal(items.Count, retrievedItems.Count);
        foreach (var item in items)
        {
            Assert.Contains(retrievedItems, i => i.Key.Value == item.Key.Value && i.Value.Value == item.Value.Value);
        }
    }

    /// <summary>
    /// Test to ensure that the SetAsync method throws an exception when the segment size is exceeded.
    /// </summary>
    [Fact]
    public async Task SetAsync_ShouldThrowExceptionIfSegmentSizeExceeded()
    {
        // Arrange: Create a storage file mock that exceeds the segment size.
        var storageFileMock = new Mock<IStorageFile>();
        storageFileMock.Setup(sf => sf.Length).Returns(2048);

        var entrySerializer = new Mock<IEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>>().Object;
        var storageEngine = new Mock<ICompactableBulkStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>>().Object;

        var segmentFile = new LogSegmentFile<SerializableWrapper<int>, SerializableWrapper<string>>(storageFileMock.Object, entrySerializer, 1024);

        // Act & Assert: Check that an InvalidOperationException is thrown.
        await Assert.ThrowsAsync<InvalidOperationException>(() => segmentFile.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("value1")));
    }

    /// <summary>
    /// Test to ensure that the ClearAsync method clears the segment file.
    /// </summary>
    [Fact]
    public async Task ClearAsync_ShouldClearSegmentFile()
    {
        // Arrange: Create a storage file, serializer, and storage engine.
        var storageFile = new StorageFile(_testFilePath);
        var entrySerializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        var segmentFile = new LogSegmentFile<SerializableWrapper<int>, SerializableWrapper<string>>(storageFile, entrySerializer, 1024);

        var key = new SerializableWrapper<int>(1);
        var value = new SerializableWrapper<string>("value1");

        await segmentFile.SetAsync(key, value);

        // Act: Clear the segment file.
        await segmentFile.ClearAsync();

        // Assert: Verify that the segment file was cleared.
        Assert.False(File.Exists(_testFilePath));
    }

    /// <summary>
    /// Test to ensure that the CompactAsync method compacts the segment file.
    /// </summary>
    [Fact]
    public async Task CompactAsync_ShouldCompactSegmentFile()
    {
        // Arrange: Create a storage file, serializer, and storage engine.
        var storageFile = new StorageFile(_testFilePath);
        var entrySerializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        var segmentFile = new LogSegmentFile<SerializableWrapper<int>, SerializableWrapper<string>>(storageFile, entrySerializer, 1024);

        await segmentFile.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("value1"));
        await segmentFile.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("value2"));

        // Act: Compact the segment file.
        await segmentFile.CompactAsync();

        // Assert: Verify that the segment file was compacted.
        var items = await segmentFile.GetAllItemsAsync();
        _output.WriteLine("Items after compaction:");
        foreach (var item in items)
        {
            _output.WriteLine($"Key: {item.Key.Value}, Value: {item.Value.Value}");
        }

        await using var fileStream = File.OpenRead(_testFilePath);
        var reader = new BinaryReader(fileStream, Encoding.UTF8, leaveOpen: true);
        Assert.Equal(1, reader.ReadInt32());
        Assert.Equal("value2", reader.ReadString());
    }

    /// <summary>
    /// Test to ensure that the RemoveAsync method throws a NotSupportedException.
    /// </summary>
    [Fact]
    public async Task RemoveAsync_ShouldThrowNotSupportedException()
    {
        // Arrange: Create a storage file, serializer, and storage engine mock.
        var storageFile = new StorageFile(_testFilePath);
        var entrySerializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        var storageEngine = new Mock<ICompactableBulkStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>>();

        storageEngine.Setup(e => e.RemoveAsync(It.IsAny<SerializableWrapper<int>>(), It.IsAny<CancellationToken>()))
            .Throws<NotSupportedException>();

        var segmentFile = new LogSegmentFile<SerializableWrapper<int>, SerializableWrapper<string>>(storageFile, entrySerializer, 1024);

        // Act & Assert: Check that a NotSupportedException is thrown.
        await Assert.ThrowsAsync<NotSupportedException>(() => segmentFile.RemoveAsync(new SerializableWrapper<int>(1)));
    }

    /// <summary>
    /// Clean up the test files after each test.
    /// </summary>
    public void Dispose()
    {
        CleanupTestFiles();
    }
}