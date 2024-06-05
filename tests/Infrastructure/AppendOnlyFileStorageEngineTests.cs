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
/// This class contains unit tests for the AppendOnlyFileStorageEngine class.
/// Each test follows the Arrange-Act-Assert pattern.
/// </summary>
public sealed class AppendOnlyFileStorageEngineTests : IDisposable
{
    private readonly string _testFileLocation = Path.Combine(Directory.GetCurrentDirectory(), "AppendOnlyFileStorageEngineTestFiles");
    private readonly string _testFileName = "AppendOnlyFileStorageEngineTests.dat";
    private string TestFilePath => Path.Combine(_testFileLocation, _testFileName);

    private readonly ITestOutputHelper _output;

    public AppendOnlyFileStorageEngineTests(ITestOutputHelper output)
    {
        _output = output;
        CleanupTestFiles();
    }

    private void CleanupTestFiles()
    {
        if (File.Exists(TestFilePath))
        {
            File.Delete(TestFilePath);
        }
    }

    /// <summary>
    /// Test to ensure that the SetAsync method correctly appends a key-value pair to the storage file.
    /// </summary>
    [Fact]
    public async Task SetAsync_ShouldAppendKeyValuePair()
    {
        // Arrange: Create a memory stream to write to and a serializer.
        using var memoryStream = new MemoryStream();
        var storageFile = new StorageFile(_testFileLocation, _testFileName);
        var entrySerializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();

        var storageEngine = new AppendOnlyFileStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(storageFile, entrySerializer);

        var key = new SerializableWrapper<int>(1);
        var value = new SerializableWrapper<string>("value1");

        // Act: Write the key-value pair to the stream.
        await storageEngine.SetAsync(key, value);

        // Assert: Verify that the stream contains the expected serialized key-value pair.
        await using var fileStream = File.OpenRead(TestFilePath);
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
        // Arrange: Create a memory stream with a serialized key-value pair.
        var storageFile = new StorageFile(_testFileLocation, _testFileName);
        var entrySerializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        var storageEngine = new AppendOnlyFileStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(storageFile, entrySerializer);

        var key = new SerializableWrapper<int>(1);
        var value = new SerializableWrapper<string>("value1");

        await storageEngine.SetAsync(key, value);

        // Act: Retrieve the value by key.
        var (retrievedValue, found) = await storageEngine.TryGetValueAsync(key);

        // Assert: Check that the value was retrieved correctly.
        Assert.True(found);
        Assert.Equal("value1", retrievedValue.Value);
    }

    /// <summary>
    /// Test to ensure that the ContainsKeyAsync method returns true if the key exists in the storage.
    /// </summary>
    [Fact]
    public async Task ContainsKeyAsync_ShouldReturnTrueIfKeyExists()
    {
        // Arrange: Create a memory stream with a serialized key-value pair.
        var storageFile = new StorageFile(_testFileLocation, _testFileName);
        var entrySerializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        var storageEngine = new AppendOnlyFileStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(storageFile, entrySerializer);

        var key = new SerializableWrapper<int>(1);
        var value = new SerializableWrapper<string>("value1");

        await storageEngine.SetAsync(key, value);

        // Act: Check if the key exists.
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
        // Arrange: Create a memory stream with serialized key-value pairs.
        var storageFile = new StorageFile(_testFileLocation, _testFileName);
        var entrySerializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        var storageEngine = new AppendOnlyFileStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(storageFile, entrySerializer);

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
    /// Test to ensure that the SetAsync method throws an exception when the storage file is null.
    /// </summary>
    [Fact]
    public void SetAsync_ShouldThrowExceptionIfStorageFileIsNull()
    {
        // Arrange: Create a new storage engine with a null storage file.
        var entrySerializer = new Mock<IEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>>().Object;
        IStorageFile storageFile = null;

        // Act & Assert: Check that an ArgumentNullException is thrown.
        Assert.Throws<ArgumentNullException>(() => new AppendOnlyFileStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(storageFile, entrySerializer));
    }

    /// <summary>
    /// Test to ensure that the SetAsync method throws an exception when the entry serializer is null.
    /// </summary>
    [Fact]
    public void SetAsync_ShouldThrowExceptionIfEntrySerializerIsNull()
    {
        // Arrange: Create a new storage engine with a null entry serializer.
        IEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>> entrySerializer = null;
        var storageFile = new StorageFile(_testFileLocation, _testFileName);

        // Act & Assert: Check that an ArgumentNullException is thrown.
        Assert.Throws<ArgumentNullException>(() => new AppendOnlyFileStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(storageFile, entrySerializer));
    }

    /// <summary>
    /// Test to ensure that the SetAsync method throws an exception when an IStorageFile method throws an exception.
    /// </summary>
    [Fact]
    public async Task SetAsync_ShouldThrowExceptionIfStorageFileThrowsException()
    {
        // Arrange: Create a storage file mock that throws an exception.
        var storageFileMock = new Mock<IStorageFile>();
        storageFileMock.Setup(sf => sf.Open(It.IsAny<FileMode>())).Throws<IOException>();

        var entrySerializer = new Mock<IEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>>().Object;

        var storageEngine = new AppendOnlyFileStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(storageFileMock.Object, entrySerializer);

        // Act & Assert: Check that an IOException is thrown.
        await Assert.ThrowsAsync<IOException>(() => storageEngine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("value1")));
    }

    /// <summary>
    /// Test to ensure that the TryGetValueAsync method returns false if the key does not exist.
    /// </summary>
    [Fact]
    public async Task TryGetValueAsync_ShouldReturnFalseIfKeyDoesNotExist()
    {
        // Arrange: Create a memory stream with a serialized key-value pair.
        var storageFile = new StorageFile(_testFileLocation, _testFileName);
        var entrySerializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        var storageEngine = new AppendOnlyFileStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(storageFile, entrySerializer);

        var key = new SerializableWrapper<int>(1);
        var value = new SerializableWrapper<string>("value1");

        await storageEngine.SetAsync(key, value);

        // Act: Try to retrieve a value by a key that does not exist.
        var (_, found) = await storageEngine.TryGetValueAsync(new SerializableWrapper<int>(2));

        // Assert: Check that the key was not found.
        Assert.False(found);
    }

    /// <summary>
    /// Test to ensure that the TryGetValueAsync method throws an exception when an IStorageFile method throws an exception.
    /// </summary>
    [Fact]
    public async Task TryGetValueAsync_ShouldThrowExceptionIfStorageFileThrowsException()
    {
        // Arrange: Create a storage file mock that throws an exception.
        var storageFileMock = new Mock<IStorageFile>();
        storageFileMock.Setup(sf => sf.ReadAllBytesAsync(It.IsAny<CancellationToken>())).Throws<IOException>();

        var entrySerializer = new Mock<IEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>>().Object;

        var storageEngine = new AppendOnlyFileStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(storageFileMock.Object, entrySerializer);

        // Act & Assert: Check that an IOException is thrown.
        await Assert.ThrowsAsync<IOException>(() => storageEngine.TryGetValueAsync(new SerializableWrapper<int>(1)));
    }

    /// <summary>
    /// Test to ensure that the RemoveAsync method throws a NotSupportedException.
    /// </summary>
    [Fact]
    public async Task RemoveAsync_ShouldThrowNotSupportedException()
    {
        // Arrange: Create a memory stream with a serialized key-value pair.
        var storageFile = new StorageFile(_testFileLocation, _testFileName);
        var entrySerializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        var storageEngine = new AppendOnlyFileStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(storageFile, entrySerializer);

        // Act & Assert: Check that a NotSupportedException is thrown.
        await Assert.ThrowsAsync<NotSupportedException>(() => storageEngine.RemoveAsync(new SerializableWrapper<int>(1)));
    }

    /// <summary>
    /// Test to ensure that the SetBulkAsync method correctly appends multiple key-value pairs to the storage file.
    /// </summary>
    [Fact]
    public async Task SetBulkAsync_ShouldAppendMultipleKeyValuePairs()
    {
        // Arrange: Create a memory stream with serialized key-value pairs.
        var storageFile = new StorageFile(_testFileLocation, _testFileName);
        var entrySerializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        var storageEngine = new AppendOnlyFileStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(storageFile, entrySerializer);

        var items = new List<KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>>
        {
            new(new SerializableWrapper<int>(1), new SerializableWrapper<string>("value1")),
            new(new SerializableWrapper<int>(2), new SerializableWrapper<string>("value2")),
            new(new SerializableWrapper<int>(3), new SerializableWrapper<string>("value3"))
        };

        // Act: Append multiple key-value pairs to the storage file.
        await storageEngine.SetBulkAsync(items);

        // Assert: Verify that the stream contains the expected serialized key-value pairs.
        await using var fileStream = File.OpenRead(TestFilePath);
        var reader = new BinaryReader(fileStream, Encoding.UTF8, leaveOpen: true);
        foreach (var item in items)
        {
            Assert.Equal(item.Key.Value, reader.ReadInt32());
            Assert.Equal(item.Value.Value, reader.ReadString());
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

        var storageEngine = new AppendOnlyFileStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(storageFileMock.Object, entrySerializer);

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
    /// Test to ensure that the ClearAsync method clears the storage file.
    /// </summary>
    [Fact]
    public async Task ClearAsync_ShouldClearStorageFile()
    {
        // Arrange: Create a memory stream with a serialized key-value pair.
        var storageFile = new StorageFile(_testFileLocation, _testFileName);
        var entrySerializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        var storageEngine = new AppendOnlyFileStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(storageFile, entrySerializer);

        var key = new SerializableWrapper<int>(1);
        var value = new SerializableWrapper<string>("value1");

        await storageEngine.SetAsync(key, value);

        // Act: Clear the storage file.
        await storageEngine.ClearAsync();

        // Assert: Check that the storage file was cleared.
        Assert.False(File.Exists(TestFilePath));
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

        var storageEngine = new AppendOnlyFileStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(storageFileMock.Object, entrySerializer);

        // Act & Assert: Check that an IOException is thrown.
        await Assert.ThrowsAsync<IOException>(() => storageEngine.ClearAsync());
    }

    /// <summary>
    /// Test to ensure that the CompactAsync method compacts the storage file.
    /// </summary>
    /// <summary>
    /// Test to ensure that the CompactAsync method compacts the storage file.
    /// </summary>
    [Fact]
    public async Task CompactAsync_ShouldCompactStorageFile()
    {
        // Arrange: Create a memory stream with serialized key-value pairs.
        var storageFile = new StorageFile(_testFileLocation, _testFileName);
        var entrySerializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        var storageEngine = new AppendOnlyFileStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(storageFile, entrySerializer);

        await storageEngine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("value1"));
        await storageEngine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("value2"));

        // Act: Compact the storage file.
        await storageEngine.CompactAsync();

        // Assert: Verify that the stream contains only the latest key-value pair.
        var items = await storageEngine.GetAllItemsAsync();
#if DEBUG_TEST
        _output.WriteLine("Items after compaction:");
        foreach (var item in items)
        {
            _output.WriteLine($"Key: {item.Key}, Value: {item.Value}");
        }
#endif
        await using var fileStream = File.OpenRead(TestFilePath);
        var reader = new BinaryReader(fileStream, Encoding.UTF8, leaveOpen: true);
        Assert.Equal(1, reader.ReadInt32());
        Assert.Equal("value2", reader.ReadString());
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

        var storageEngine = new AppendOnlyFileStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(storageFileMock.Object, entrySerializer);

        // Act & Assert: Check that an IOException is thrown.
        await Assert.ThrowsAsync<IOException>(() => storageEngine.CompactAsync());
    }

    /// <summary>
    /// Clean up the test files after each test.
    /// </summary>
    public void Dispose()
    {
        CleanupTestFiles();
    }
}