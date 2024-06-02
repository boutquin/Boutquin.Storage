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
/// This class contains unit tests for the AppendOnlyFileStorageEngineWithIndex class.
/// Each test follows the Arrange-Act-Assert pattern.
/// </summary>
public sealed class AppendOnlyFileStorageEngineWithIndexTests : IDisposable
{
    private readonly string _testFilePath = "AppendOnlyFileStorageEngineWithIndexTests.dat";
    private readonly ITestOutputHelper _output;

    public AppendOnlyFileStorageEngineWithIndexTests(ITestOutputHelper output)
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
    /// Test to ensure that the SetAsync method correctly appends a key-value pair to the storage file and updates the index.
    /// </summary>
    [Fact]
    public async Task SetAsync_ShouldAppendKeyValuePairAndUpdateIndex()
    {
        // Arrange: Create a storage file, serializer, and index mock.
        var storageFile = new StorageFile(_testFilePath);
        var entrySerializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        var indexMock = new Mock<IFileStorageIndex<SerializableWrapper<int>>>();

        var storageEngine = new AppendOnlyFileStorageEngineWithIndex<SerializableWrapper<int>, SerializableWrapper<string>>(storageFile, entrySerializer, indexMock.Object);

        var key = new SerializableWrapper<int>(1);
        var value = new SerializableWrapper<string>("value1");

        // Act: Write the key-value pair to the stream and update the index.
        await storageEngine.SetAsync(key, value);

        // Assert: Verify that the stream contains the expected serialized key-value pair and index is updated.
        await using var fileStream = File.OpenRead(_testFilePath);
        var reader = new BinaryReader(fileStream, Encoding.UTF8, leaveOpen: true);
        Assert.Equal(1, reader.ReadInt32());
        Assert.Equal("value1", reader.ReadString());

        indexMock.Verify(i => i.SetAsync(key, It.IsAny<FileLocation>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Test to ensure that the TryGetValueAsync method correctly retrieves a value by its key using the index.
    /// </summary>
    [Fact]
    public async Task TryGetValueAsync_ShouldRetrieveValueByKeyUsingIndex()
    {
        // Arrange: Create a storage file, serializer, and index mock with a predefined FileLocation.
        var storageFile = new StorageFile(_testFilePath);
        var entrySerializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        var indexMock = new Mock<IFileStorageIndex<SerializableWrapper<int>>>();

        var key = new SerializableWrapper<int>(1);
        var value = new SerializableWrapper<string>("value1");

        await using (var stream = storageFile.Open(FileMode.Append))
        {
            await entrySerializer.WriteEntryAsync(stream, key, value);
        }

        indexMock.Setup(i => i.TryGetValueAsync(key, It.IsAny<CancellationToken>()))
            .ReturnsAsync((new FileLocation(0, (int)new FileInfo(_testFilePath).Length), true));

        var storageEngine = new AppendOnlyFileStorageEngineWithIndex<SerializableWrapper<int>, SerializableWrapper<string>>(storageFile, entrySerializer, indexMock.Object);

        // Act: Retrieve the value by key using the index.
        var (retrievedValue, found) = await storageEngine.TryGetValueAsync(key);

        // Assert: Check that the value was retrieved correctly using the index.
        Assert.True(found);
        Assert.Equal("value1", retrievedValue.Value);
    }

    /// <summary>
    /// Test to ensure that the ContainsKeyAsync method returns true if the key exists in the index.
    /// </summary>
    [Fact]
    public async Task ContainsKeyAsync_ShouldReturnTrueIfKeyExistsInIndex()
    {
        // Arrange: Create a storage file, serializer, and index mock with a predefined FileLocation.
        var storageFile = new StorageFile(_testFilePath);
        var entrySerializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        var indexMock = new Mock<IFileStorageIndex<SerializableWrapper<int>>>();

        var key = new SerializableWrapper<int>(1);
        var value = new SerializableWrapper<string>("value1");

        await using (var stream = storageFile.Open(FileMode.Append))
        {
            await entrySerializer.WriteEntryAsync(stream, key, value);
        }

        indexMock.Setup(i => i.TryGetValueAsync(key, It.IsAny<CancellationToken>()))
            .ReturnsAsync((new FileLocation(0, (int)new FileInfo(_testFilePath).Length), true));

        var storageEngine = new AppendOnlyFileStorageEngineWithIndex<SerializableWrapper<int>, SerializableWrapper<string>>(storageFile, entrySerializer, indexMock.Object);

        // Act: Check if the key exists using the index.
        var exists = await storageEngine.ContainsKeyAsync(key);

        // Assert: Check that the key exists.
        Assert.True(exists);
    }

    /// <summary>
    /// Test to ensure that the GetAllItemsAsync method retrieves all key-value pairs from the storage file.
    /// </summary>
    [Fact]
    public async Task GetAllItemsAsync_ShouldRetrieveAllKeyValuePairs()
    {
        // Arrange: Create a storage file, serializer, and index mock.
        var storageFile = new StorageFile(_testFilePath);
        var entrySerializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        var indexMock = new Mock<IFileStorageIndex<SerializableWrapper<int>>>();

        var storageEngine = new AppendOnlyFileStorageEngineWithIndex<SerializableWrapper<int>, SerializableWrapper<string>>(storageFile, entrySerializer, indexMock.Object);

        var items = new List<KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>>
        {
            new(new SerializableWrapper<int>(1), new SerializableWrapper<string>("value1")),
            new(new SerializableWrapper<int>(2), new SerializableWrapper<string>("value2")),
            new(new SerializableWrapper<int>(3), new SerializableWrapper<string>("value3"))
        };

        foreach (var item in items)
        {
            await storageEngine.SetAsync(item.Key, item.Value);
        }

        // Act: Retrieve all key-value pairs from the storage file.
        var retrievedItems = (await storageEngine.GetAllItemsAsync()).ToList();

        // Assert: Check that all key-value pairs were retrieved correctly.
        Assert.Equal(items.Count, retrievedItems.Count);
        foreach (var item in items)
        {
            Assert.Contains(retrievedItems, i => i.Key.Value == item.Key.Value && i.Value.Value == item.Value.Value);
        }
    }

    /// <summary>
    /// Test to ensure that the ClearAsync method clears both the storage file and the index.
    /// </summary>
    [Fact]
    public async Task ClearAsync_ShouldClearStorageFileAndIndex()
    {
        // Arrange: Create a storage file, serializer, and index mock.
        var storageFile = new StorageFile(_testFilePath);
        var entrySerializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        var indexMock = new Mock<IFileStorageIndex<SerializableWrapper<int>>>();

        var storageEngine = new AppendOnlyFileStorageEngineWithIndex<SerializableWrapper<int>, SerializableWrapper<string>>(storageFile, entrySerializer, indexMock.Object);

        var key = new SerializableWrapper<int>(1);
        var value = new SerializableWrapper<string>("value1");

        await storageEngine.SetAsync(key, value);

        // Act: Clear the storage file and the index.
        await storageEngine.ClearAsync();

        // Assert: Check that the storage file and the index were cleared.
        Assert.False(File.Exists(_testFilePath));
        indexMock.Verify(i => i.ClearAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Test to ensure that the ClearAsync method throws an exception when an IStorageFile method throws an exception.
    /// </summary>
    [Fact]
    public async Task ClearAsync_ShouldThrowExceptionIfStorageFileThrowsException()
    {
        // Arrange: Create a storage file mock that throws an exception.
        var storageFileMock = new Mock<IStorageFile>();
        storageFileMock.Setup(sf => sf.Delete(It.IsAny<FileDeletionHandling>())).Throws<IOException>();

        var entrySerializer = new Mock<IEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>>().Object;
        var indexMock = new Mock<IFileStorageIndex<SerializableWrapper<int>>>().Object;

        var storageEngine = new AppendOnlyFileStorageEngineWithIndex<SerializableWrapper<int>, SerializableWrapper<string>>(storageFileMock.Object, entrySerializer, indexMock);

        // Act & Assert: Check that an IOException is thrown.
        await Assert.ThrowsAsync<IOException>(() => storageEngine.ClearAsync());
    }

    /// <summary>
    /// Test to ensure that the SetBulkAsync method correctly appends multiple key-value pairs to the storage file and updates the index.
    /// </summary>
    [Fact]
    public async Task SetBulkAsync_ShouldAppendMultipleKeyValuePairsAndUpdateIndex()
    {
        // Arrange: Create a storage file, serializer, and index mock.
        var storageFile = new StorageFile(_testFilePath);
        var entrySerializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        var indexMock = new Mock<IFileStorageIndex<SerializableWrapper<int>>>();

        var storageEngine = new AppendOnlyFileStorageEngineWithIndex<SerializableWrapper<int>, SerializableWrapper<string>>(storageFile, entrySerializer, indexMock.Object);

        var items = new List<KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>>
        {
            new(new SerializableWrapper<int>(1), new SerializableWrapper<string>("value1")),
            new(new SerializableWrapper<int>(2), new SerializableWrapper<string>("value2")),
            new(new SerializableWrapper<int>(3), new SerializableWrapper<string>("value3"))
        };

        // Act: Append multiple key-value pairs to the storage file and update the index.
        await storageEngine.SetBulkAsync(items);

        // Assert: Verify that the stream contains the expected serialized key-value pairs and index is updated.
        await using var fileStream = File.OpenRead(_testFilePath);
        var reader = new BinaryReader(fileStream, Encoding.UTF8, leaveOpen: true);
        foreach (var item in items)
        {
            Assert.Equal(item.Key.Value, reader.ReadInt32());
            Assert.Equal(item.Value.Value, reader.ReadString());
            indexMock.Verify(i => i.SetAsync(item.Key, It.IsAny<FileLocation>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        }
    }

    /// <summary>
    /// Test to ensure that the SetBulkAsync method throws an exception when an IStorageFile method throws an exception.
    /// </summary>
    [Fact]
    public async Task SetBulkAsync_ShouldThrowExceptionIfStorageFileThrowsException()
    {
        // Arrange: Create a storage file mock that throws an exception.
        var storageFileMock = new Mock<IStorageFile>();
        storageFileMock.Setup(sf => sf.Open(It.IsAny<FileMode>())).Throws<IOException>();

        var entrySerializer = new Mock<IEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>>().Object;
        var indexMock = new Mock<IFileStorageIndex<SerializableWrapper<int>>>();

        var storageEngine = new AppendOnlyFileStorageEngineWithIndex<SerializableWrapper<int>, SerializableWrapper<string>>(storageFileMock.Object, entrySerializer, indexMock.Object);

        var items = new List<KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>>
        {
            new(new SerializableWrapper<int>(1), new SerializableWrapper<string>("value1")),
            new(new SerializableWrapper<int>(2), new SerializableWrapper<string>("value2")),
            new(new SerializableWrapper<int>(3), new SerializableWrapper<string>("value3"))
        };

        // Act & Assert: Check that an IOException is thrown.
        await Assert.ThrowsAsync<IOException>(() => storageEngine.SetBulkAsync(items));
    }

    /// <summary>
    /// Test to ensure that the CompactAsync method compacts the storage file and updates the index.
    /// </summary>
    [Fact]
    public async Task CompactAsync_ShouldCompactStorageFileAndUpdateIndex()
    {
        // Arrange: Create a storage file, serializer, and index mock.
        var storageFile = new StorageFile(_testFilePath);
        var entrySerializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        var indexMock = new Mock<IFileStorageIndex<SerializableWrapper<int>>>();

        var storageEngine = new AppendOnlyFileStorageEngineWithIndex<SerializableWrapper<int>, SerializableWrapper<string>>(storageFile, entrySerializer, indexMock.Object);

        await storageEngine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("value1"));
        await storageEngine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("value2"));

        // Act: Compact the storage file and update the index.
        await storageEngine.CompactAsync();

        // Assert: Verify that the stream contains only the latest key-value pair and index is updated.
        var items = await storageEngine.GetAllItemsAsync();
        await using var fileStream = File.OpenRead(_testFilePath);
        var reader = new BinaryReader(fileStream, Encoding.UTF8, leaveOpen: true);
        Assert.Equal(1, reader.ReadInt32());
        Assert.Equal("value2", reader.ReadString());

        indexMock.Verify(i => i.SetAsync(It.IsAny<SerializableWrapper<int>>(), It.IsAny<FileLocation>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    /// <summary>
    /// Test to ensure that the CompactAsync method throws an exception when an IStorageFile method throws an exception.
    /// </summary>
    [Fact]
    public async Task CompactAsync_ShouldThrowExceptionIfStorageFileThrowsException()
    {
        // Arrange: Create a storage file mock that throws an exception.
        var storageFileMock = new Mock<IStorageFile>();
        storageFileMock.Setup(sf => sf.Open(It.IsAny<FileMode>())).Throws<IOException>();

        var entrySerializer = new Mock<IEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>>().Object;
        var indexMock = new Mock<IFileStorageIndex<SerializableWrapper<int>>>().Object;

        var storageEngine = new AppendOnlyFileStorageEngineWithIndex<SerializableWrapper<int>, SerializableWrapper<string>>(storageFileMock.Object, entrySerializer, indexMock);

        // Act & Assert: Check that an IOException is thrown.
        await Assert.ThrowsAsync<ArgumentException>(() => storageEngine.CompactAsync());
    }

    /// <summary>
    /// Clean up the test files after each test.
    /// </summary>
    public void Dispose()
    {
        CleanupTestFiles();
    }
}