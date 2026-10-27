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
/// This class contains unit tests for the AppendOnlyFileStorageEngine class.
/// Each test follows the Arrange-Act-Assert pattern.
/// </summary>
public sealed class AppendOnlyFileStorageEngineTests : IDisposable
{
    private readonly string _testDirectory = Path.Combine(Path.GetTempPath(), $"StorageTests_{Guid.NewGuid():N}");
    private readonly string _testFileName = "AppendOnlyFileStorageEngineTests.dat";
    private string _testFileLocation => _testDirectory;
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

        // Assert: round-trip the written file through the same entry serializer and compare
        // values. Raw-byte BinaryReader assertions would couple this test to the wrapper's
        // internal wire format; the engine contract we care about is "write-then-read" fidelity.
        var entry = Assert.Single(AppendOnlyFrames.ReadEntries(entrySerializer, TestFilePath));
        Assert.Equal(key.Value, entry.Key.Value);
        Assert.Equal(value.Value, entry.Value.Value);
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
        // Arrange: Create a storage file mock that exists but throws on read — a transient
        // I/O failure on a present file must propagate, not read as an empty store.
        var storageFileMock = new Mock<IStorageFile>();
        storageFileMock.Setup(sf => sf.Exists()).Returns(true);
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

        // Assert: round-trip via the same entry serializer rather than raw BinaryReader bytes,
        // which would be coupled to the wrapper's internal wire format.
        var entries = AppendOnlyFrames.ReadEntries(entrySerializer, TestFilePath);
        Assert.Equal(items.Count, entries.Count);
        for (var i = 0; i < items.Count; i++)
        {
            Assert.Equal(items[i].Key.Value, entries[i].Key.Value);
            Assert.Equal(items[i].Value.Value, entries[i].Value.Value);
        }
    }

    /// <summary>
    /// A bulk write whose LATER item is invalid must be rejected as a whole before any byte is
    /// written: no earlier item of the rejected call may become readable, through this instance or
    /// a reopened one.
    /// </summary>
    [Fact]
    public async Task SetBulkAsync_LaterItemInvalid_RejectsTheWholeBatchBeforeWritingAnything()
    {
        var entrySerializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        var storageEngine = new AppendOnlyFileStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            new StorageFile(_testFileLocation, _testFileName), entrySerializer);
        var items = new List<KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>>
        {
            new(new SerializableWrapper<int>(1), new SerializableWrapper<string>("a")),
            new(new SerializableWrapper<int>(2), new SerializableWrapper<string>("b")),
            new(null, new SerializableWrapper<string>("c")),
        };

        await Assert.ThrowsAnyAsync<ArgumentException>(() => storageEngine.SetBulkAsync(items));

        var (_, found1) = await storageEngine.TryGetValueAsync(new SerializableWrapper<int>(1));
        Assert.False(found1, "an item of a rejected batch must not become readable");
        Assert.Empty(await storageEngine.GetAllItemsAsync());
        Assert.True(!File.Exists(TestFilePath) || new FileInfo(TestFilePath).Length == 0,
            "a rejected batch must not leave bytes in the log");
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
    /// DDIA 2e Ch. 4: a crashed write "could contain incomplete records … these issues are
    /// typically detected by including checksums in the log and discarding corrupted or
    /// incomplete log entries" — discarding the TORN entry, not failing every read of the
    /// fully durable entries before it. A torn final record (crash mid-append) must be
    /// treated as end-of-log, not poison the whole file.
    /// </summary>
    [Fact]
    public async Task TryGetValueAsync_WithTornFinalRecord_StillReadsEarlierEntries()
    {
        var storageFile = new StorageFile(_testFileLocation, _testFileName);
        var entrySerializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        var storageEngine = new AppendOnlyFileStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(storageFile, entrySerializer);

        await storageEngine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("value1"));

        // Simulate a crash mid-append: the first bytes of a real frame (a partial header).
        var torn = AppendOnlyFrames.Encode(entrySerializer, new SerializableWrapper<int>(2), new SerializableWrapper<string>("value2"));
        using (var fs = new FileStream(TestFilePath, FileMode.Append, FileAccess.Write))
        {
            fs.Write(torn.AsSpan(0, 7));
        }

        var (value, found) = await storageEngine.TryGetValueAsync(new SerializableWrapper<int>(1));
        Assert.True(found, "a torn tail must not make durable earlier entries unreadable");
        Assert.Equal("value1", value.Value);

        var items = await storageEngine.GetAllItemsAsync();
        Assert.Single(items);
    }

    /// <summary>
    /// The CSV serializer signals a torn final record with <see cref="DeserializationException"/>
    /// (corrupt Base64 / wrong field count), not <see cref="EndOfStreamException"/>. A crash that
    /// leaves an incomplete final CSV line must still be treated as end-of-log so the durable
    /// earlier entries remain readable — same DDIA 2e Ch. 4 torn-tail contract as the binary path.
    /// </summary>
    [Fact]
    public async Task GetAllItemsAsync_WithTornFinalCsvRecord_StillReadsEarlierEntries()
    {
        var storageFile = new StorageFile(_testFileLocation, _testFileName);
        var entrySerializer = new CsvEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        var storageEngine = new AppendOnlyFileStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(storageFile, entrySerializer);

        await storageEngine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("value1"));
        await storageEngine.SetAsync(new SerializableWrapper<int>(2), new SerializableWrapper<string>("value2"));

        // Simulate a crash mid-append: a CSV frame whose payload reached disk only in part
        // (valid header, payload short at end-of-file).
        var torn = AppendOnlyFrames.Encode(entrySerializer, new SerializableWrapper<int>(3), new SerializableWrapper<string>("value3"));
        using (var fs = new FileStream(TestFilePath, FileMode.Append, FileAccess.Write))
        {
            fs.Write(torn.AsSpan(0, torn.Length - 5));
        }

        var items = (await storageEngine.GetAllItemsAsync()).ToList();
        Assert.Equal(2, items.Count);
        Assert.Contains(items, i => i.Key.Value == 1 && i.Value.Value == "value1");
        Assert.Contains(items, i => i.Key.Value == 2 && i.Value.Value == "value2");

        var (value, found) = await storageEngine.TryGetValueAsync(new SerializableWrapper<int>(2));
        Assert.True(found, "a torn CSV tail must not make durable earlier entries unreadable");
        Assert.Equal("value2", value.Value);
    }

    /// <summary>
    /// The torn-tail tolerance must NOT swallow genuine mid-file corruption: a corrupt record with
    /// valid complete records after it is real data loss (disk rot), not a crash-truncated tail —
    /// mirroring ValueLogWriter.ThrowIfValidFrameFollows and the FileRaftLog.Replay mid-file guard.
    /// Recovery must fail loud rather than silently drop the records before the corruption.
    /// </summary>
    [Fact]
    public async Task GetAllItemsAsync_WithMidFileCorruption_StillThrows()
    {
        var storageFile = new StorageFile(_testFileLocation, _testFileName);
        var entrySerializer = new CsvEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        var storageEngine = new AppendOnlyFileStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(storageFile, entrySerializer);

        await storageEngine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("value1"));

        // Inject a checksum-valid frame whose CSV payload is not a key-value line BEFORE a valid
        // trailing record — mid-file corruption, not a torn tail, and must surface as an error.
        using (var fs = new FileStream(TestFilePath, FileMode.Append, FileAccess.Write))
        {
            fs.Write(AppendOnlyFrames.Encode(Encoding.UTF8.GetBytes("!!corrupt-midfile-line\n")));
        }

        await storageEngine.SetAsync(new SerializableWrapper<int>(2), new SerializableWrapper<string>("value2"));

        // Mid-file corruption surfaces uniformly as InvalidDataException regardless of which
        // serializer raised the underlying parse fault (the CSV path's DeserializationException is
        // carried as the inner exception). A checksum-valid frame the serializer cannot decode is
        // never a torn tail.
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => storageEngine.GetAllItemsAsync());
        Assert.IsType<DeserializationException>(ex.InnerException);
    }

    /// <summary>
    /// Reads after ClearAsync must observe an empty store — not throw
    /// <see cref="FileNotFoundException"/> until the next write happens to recreate the file.
    /// </summary>
    [Fact]
    public async Task TryGetValueAsync_AfterClear_ReturnsNotFoundWithoutThrowing()
    {
        var storageFile = new StorageFile(_testFileLocation, _testFileName);
        var entrySerializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        var storageEngine = new AppendOnlyFileStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(storageFile, entrySerializer);

        await storageEngine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("value1"));
        await storageEngine.ClearAsync();

        var (_, found) = await storageEngine.TryGetValueAsync(new SerializableWrapper<int>(1));
        Assert.False(found);

        var items = await storageEngine.GetAllItemsAsync();
        Assert.Empty(items);
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
        var entry = Assert.Single(AppendOnlyFrames.ReadEntries(entrySerializer, TestFilePath));
        Assert.Equal(1, entry.Key.Value);
        Assert.Equal("value2", entry.Value.Value);
    }

    /// <summary>
    /// Test to ensure that the CompactAsync method throws an exception when an IStorageFile method throws an exception.
    /// </summary>
    [Fact]
    public async Task CompactAsync_ShouldThrowExceptionIfStorageFileThrowsException()
    {
        // Arrange: a storage file whose atomic replace fails. Compaction serializes the deduplicated
        // entries in memory and replaces the file via WriteAllBytesAsync (crash-atomic temp+rename),
        // so a failure there must propagate to the caller.
        var storageFileMock = new Mock<IStorageFile>();
        storageFileMock.Setup(sf => sf.WriteAllBytesAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException());

        var entrySerializer = new Mock<IEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>>().Object;

        var storageEngine = new AppendOnlyFileStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(storageFileMock.Object, entrySerializer);

        // Act & Assert: Check that an IOException is thrown.
        await Assert.ThrowsAsync<IOException>(() => storageEngine.CompactAsync());
    }

    /// <summary>
    /// Test to ensure that TryGetValueAsync returns the LAST written value for a key
    /// in an append-only log (last-writer-wins semantics).
    /// </summary>
    [Fact]
    public async Task TryGetValueAsync_ShouldReturnLastWrittenValue_WhenKeyWrittenMultipleTimes()
    {
        // Arrange
        var storageFile = new StorageFile(_testFileLocation, _testFileName);
        var entrySerializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        var storageEngine = new AppendOnlyFileStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(storageFile, entrySerializer);

        var key = new SerializableWrapper<int>(1);
        await storageEngine.SetAsync(key, new SerializableWrapper<string>("old_value"));
        await storageEngine.SetAsync(key, new SerializableWrapper<string>("new_value"));

        // Act
        var (retrievedValue, found) = await storageEngine.TryGetValueAsync(key);

        // Assert: Must return the latest value, not the first one.
        Assert.True(found);
        Assert.Equal("new_value", retrievedValue.Value);
    }

    /// <summary>
    /// Test to ensure TryGetValueAsync returns the last value even with many overwrites.
    /// </summary>
    [Fact]
    public async Task TryGetValueAsync_ShouldReturnLastValue_AfterManyOverwrites()
    {
        // Arrange
        var storageFile = new StorageFile(_testFileLocation, _testFileName);
        var entrySerializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        var storageEngine = new AppendOnlyFileStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(storageFile, entrySerializer);

        var key = new SerializableWrapper<int>(42);
        for (var i = 0; i < 10; i++)
        {
            await storageEngine.SetAsync(key, new SerializableWrapper<string>($"version_{i}"));
        }

        // Act
        var (retrievedValue, found) = await storageEngine.TryGetValueAsync(key);

        // Assert: Must return the very last version
        Assert.True(found);
        Assert.Equal("version_9", retrievedValue.Value);
    }

    /// <summary>
    /// Clean up the test files after each test.
    /// </summary>
    public void Dispose()
    {
        CleanupTestFiles();
    }
}
