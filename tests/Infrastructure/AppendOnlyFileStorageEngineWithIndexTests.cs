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
namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// This class contains unit tests for the AppendOnlyFileStorageEngineWithIndex class.
/// Each test follows the Arrange-Act-Assert pattern.
/// </summary>
public sealed class AppendOnlyFileStorageEngineWithIndexTests : IDisposable
{
    private readonly string _testDirectory = Path.Combine(Path.GetTempPath(), $"StorageTests_{Guid.NewGuid():N}");
    private readonly string _testFileName = "AppendOnlyFileStorageEngineWithIndexTests.dat";
    private readonly ITestOutputHelper _output;
    private string _testFileLocation => _testDirectory;
    private string TestFilePath => Path.Combine(_testFileLocation, _testFileName);

    public AppendOnlyFileStorageEngineWithIndexTests(ITestOutputHelper output)
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
    /// Test to ensure that the SetAsync method correctly appends a key-value pair to the storage file and updates the index.
    /// </summary>
    [Fact]
    public async Task SetAsync_ShouldAppendKeyValuePairAndUpdateIndex()
    {
        // Arrange: Create a storage file, serializer, and index mock.
        var storageFile = new StorageFile(_testFileLocation, _testFileName);
        var entrySerializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        var indexMock = new Mock<IFileStorageIndex<SerializableWrapper<int>>>();

        var storageEngine = new AppendOnlyFileStorageEngineWithIndex<SerializableWrapper<int>, SerializableWrapper<string>>(storageFile, entrySerializer, indexMock.Object);

        var key = new SerializableWrapper<int>(1);
        var value = new SerializableWrapper<string>("value1");

        // Act: Write the key-value pair to the stream and update the index.
        await storageEngine.SetAsync(key, value);

        // Assert: round-trip via the same entry serializer and confirm the index was updated.
        // BinaryReader byte-shape assertions would pin this test to the wrapper's 1.x wire.
        var entry = Assert.Single(AppendOnlyFrames.ReadEntries(entrySerializer, TestFilePath));
        Assert.Equal(key.Value, entry.Key.Value);
        Assert.Equal(value.Value, entry.Value.Value);

        indexMock.Verify(i => i.SetAsync(key, It.IsAny<FileLocation>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// DDIA 2e Ch. 4: "The hash map is not persisted, so you have to rebuild it when you
    /// restart the database — for example, by scanning the whole log file to find the latest
    /// byte offset for each key." A new engine instance over an existing data file (with a
    /// fresh, empty index) must therefore rebuild the index and serve point reads of durable
    /// data — not report every previously-acknowledged key as missing.
    /// </summary>
    [Fact]
    public async Task TryGetValueAsync_AfterReopen_RebuildsIndexFromLog()
    {
        var entrySerializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();

        // First engine: write two keys (one overwritten) and let them become durable.
        {
            var storageFile = new StorageFile(_testFileLocation, _testFileName);
            var engine = new AppendOnlyFileStorageEngineWithIndex<SerializableWrapper<int>, SerializableWrapper<string>>(
                storageFile, entrySerializer,
                new InMemoryFileIndex<SerializableWrapper<int>>());
            await engine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("stale"));
            await engine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("current"));
            await engine.SetAsync(new SerializableWrapper<int>(2), new SerializableWrapper<string>("v2"));
        }

        // Second engine over the same file, fresh index — simulates a process restart.
        var reopened = new AppendOnlyFileStorageEngineWithIndex<SerializableWrapper<int>, SerializableWrapper<string>>(
            new StorageFile(_testFileLocation, _testFileName), entrySerializer,
            new InMemoryFileIndex<SerializableWrapper<int>>());

        var (v1, found1) = await reopened.TryGetValueAsync(new SerializableWrapper<int>(1));
        Assert.True(found1, "durable data must be visible to point reads after a restart (index rebuilt from the log)");
        Assert.Equal("current", v1.Value);

        var (v2, found2) = await reopened.TryGetValueAsync(new SerializableWrapper<int>(2));
        Assert.True(found2);
        Assert.Equal("v2", v2.Value);
    }

    /// <summary>
    /// Test to ensure that the TryGetValueAsync method correctly retrieves a value by its key using the index.
    /// </summary>
    [Fact]
    public async Task TryGetValueAsync_ShouldRetrieveValueByKeyUsingIndex()
    {
        // Arrange: Create a storage file, serializer, and index mock with a predefined FileLocation.
        var storageFile = new StorageFile(_testFileLocation, _testFileName);
        var entrySerializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        var indexMock = new Mock<IFileStorageIndex<SerializableWrapper<int>>>();

        var key = new SerializableWrapper<int>(1);
        var value = new SerializableWrapper<string>("value1");

        await using (var stream = storageFile.Open(FileMode.Append))
        {
            await stream.WriteAsync(AppendOnlyFrames.Encode(entrySerializer, key, value));
        }

        indexMock.Setup(i => i.TryGetValueAsync(key, It.IsAny<CancellationToken>()))
            .ReturnsAsync((new FileLocation(0, (int)new FileInfo(TestFilePath).Length), true));

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
        var storageFile = new StorageFile(_testFileLocation, _testFileName);
        var entrySerializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        var indexMock = new Mock<IFileStorageIndex<SerializableWrapper<int>>>();

        var key = new SerializableWrapper<int>(1);
        var value = new SerializableWrapper<string>("value1");

        await using (var stream = storageFile.Open(FileMode.Append))
        {
            await stream.WriteAsync(AppendOnlyFrames.Encode(entrySerializer, key, value));
        }

        indexMock.Setup(i => i.TryGetValueAsync(key, It.IsAny<CancellationToken>()))
            .ReturnsAsync((new FileLocation(0, (int)new FileInfo(TestFilePath).Length), true));

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
        var storageFile = new StorageFile(_testFileLocation, _testFileName);
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
        var storageFile = new StorageFile(_testFileLocation, _testFileName);
        var entrySerializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        var indexMock = new Mock<IFileStorageIndex<SerializableWrapper<int>>>();

        var storageEngine = new AppendOnlyFileStorageEngineWithIndex<SerializableWrapper<int>, SerializableWrapper<string>>(storageFile, entrySerializer, indexMock.Object);

        var key = new SerializableWrapper<int>(1);
        var value = new SerializableWrapper<string>("value1");

        await storageEngine.SetAsync(key, value);

        // Act: Clear the storage file and the index.
        await storageEngine.ClearAsync();

        // Assert: Check that the storage file and the index were cleared.
        Assert.False(File.Exists(TestFilePath));
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
        var storageFile = new StorageFile(_testFileLocation, _testFileName);
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

        // Assert: round-trip via the same entry serializer rather than pinning raw bytes.
        var entries = AppendOnlyFrames.ReadEntries(entrySerializer, TestFilePath);
        Assert.Equal(items.Count, entries.Count);
        foreach (var (item, entry) in items.Zip(entries))
        {
            Assert.Equal(item.Key.Value, entry.Key.Value);
            Assert.Equal(item.Value.Value, entry.Value.Value);
            indexMock.Verify(i => i.SetAsync(item.Key, It.IsAny<FileLocation>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
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
        var storageEngine = new AppendOnlyFileStorageEngineWithIndex<SerializableWrapper<int>, SerializableWrapper<string>>(
            new StorageFile(_testFileLocation, _testFileName), entrySerializer, new InMemoryFileIndex<SerializableWrapper<int>>());
        var items = new List<KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>>
        {
            new(new SerializableWrapper<int>(1), new SerializableWrapper<string>("a")),
            new(new SerializableWrapper<int>(2), new SerializableWrapper<string>("b")),
            new(null!, new SerializableWrapper<string>("c")),
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
    /// Durability regression (DDIA 2e — durable-before-ack): <see cref="AppendOnlyFileStorageEngineWithIndex{TKey,TValue}.SetBulkAsync"/>
    /// must flush (fsync) the data stream before the index records any offset, mirroring
    /// <see cref="AppendOnlyFileStorageEngine{TKey,TValue}.SetAsync"/> and the base
    /// <see cref="AppendOnlyFileStorageEngine{TKey,TValue}.SetBulkAsync"/>. The override previously
    /// skipped the flush entirely, so a crash could leave an index entry pointing past the durable
    /// end of the file. The data is flushed via <c>FlushAsync</c>; the serializer's per-entry
    /// <c>BinaryWriter.Flush()</c> is synchronous and is not counted here.
    /// </summary>
    [Fact]
    public async Task SetBulkAsync_FlushesDataBeforeRecordingIndexOffsets()
    {
        // Arrange: a stream that counts only async flushes (the durability fsync), and an index spy
        // that captures how many async flushes had happened by the time the FIRST offset was recorded.
        var stream = new AsyncFlushTrackingStream();
        var storageFileMock = new Mock<IStorageFile>();
        storageFileMock.Setup(sf => sf.Open(It.IsAny<FileMode>())).Returns(stream);

        var entrySerializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        var indexMock = new Mock<IFileStorageIndex<SerializableWrapper<int>>>();

        var asyncFlushesAtFirstIndexWrite = -1;
        indexMock
            .Setup(i => i.SetAsync(It.IsAny<SerializableWrapper<int>>(), It.IsAny<FileLocation>(), It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                if (asyncFlushesAtFirstIndexWrite < 0)
                {
                    asyncFlushesAtFirstIndexWrite = stream.AsyncFlushCount;
                }
            })
            .Returns(Task.CompletedTask);

        var storageEngine = new AppendOnlyFileStorageEngineWithIndex<SerializableWrapper<int>, SerializableWrapper<string>>(
            storageFileMock.Object, entrySerializer, indexMock.Object);

        // Act
        await storageEngine.SetBulkAsync(
        [
            new(new SerializableWrapper<int>(1), new SerializableWrapper<string>("v1")),
            new(new SerializableWrapper<int>(2), new SerializableWrapper<string>("v2")),
        ]);

        // Assert: the data was fsync'd, and the fsync preceded the first index offset record.
        Assert.True(stream.AsyncFlushCount > 0, "SetBulkAsync must FlushAsync (fsync) the data stream for durability.");
        Assert.True(asyncFlushesAtFirstIndexWrite > 0, "data must be flushed before the index records any offset (durable-before-ack).");
    }

    /// <summary>
    /// A <see cref="MemoryStream"/> that counts asynchronous flushes only. The append-only engine's
    /// durability flush is <c>FlushAsync</c>; the entry serializer's per-write
    /// <c>BinaryWriter.Flush()</c> is synchronous, so it is deliberately not counted.
    /// </summary>
    private sealed class AsyncFlushTrackingStream : MemoryStream
    {
        public int AsyncFlushCount { get; private set; }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            AsyncFlushCount++;
            return base.FlushAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Test to ensure that the CompactAsync method compacts the storage file and updates the index.
    /// </summary>
    [Fact]
    public async Task CompactAsync_ShouldCompactStorageFileAndUpdateIndex()
    {
        // Arrange: Create a storage file, serializer, and index mock.
        var storageFile = new StorageFile(_testFileLocation, _testFileName);
        var entrySerializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        var indexMock = new Mock<IFileStorageIndex<SerializableWrapper<int>>>();

        var storageEngine = new AppendOnlyFileStorageEngineWithIndex<SerializableWrapper<int>, SerializableWrapper<string>>(storageFile, entrySerializer, indexMock.Object);

        await storageEngine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("value1"));
        await storageEngine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("value2"));

        // Act: Compact the storage file and update the index.
        await storageEngine.CompactAsync();

        // Assert: Verify that the stream contains only the latest key-value pair and index is updated.
        var items = await storageEngine.GetAllItemsAsync();
        var entry = Assert.Single(AppendOnlyFrames.ReadEntries(entrySerializer, TestFilePath));
        Assert.Equal(1, entry.Key.Value);
        Assert.Equal("value2", entry.Value.Value);

        indexMock.Verify(i => i.SetAsync(It.IsAny<SerializableWrapper<int>>(), It.IsAny<FileLocation>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    /// <summary>
    /// Test to ensure that the CompactAsync method throws an exception when an IStorageFile method throws an exception.
    /// </summary>
    [Fact]
    public async Task CompactAsync_ShouldThrowExceptionIfStorageFileThrowsException()
    {
        // Arrange: a storage file whose atomic replace fails. Compaction serializes the deduplicated
        // entries in memory (capturing offsets), replaces the file via WriteAllBytesAsync (crash-atomic
        // temp+rename), then rebuilds the index — so a failure at the file replace must propagate.
        var storageFileMock = new Mock<IStorageFile>();
        storageFileMock.Setup(sf => sf.WriteAllBytesAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException());

        var entrySerializer = new Mock<IEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>>().Object;
        var indexMock = new Mock<IFileStorageIndex<SerializableWrapper<int>>>().Object;

        var storageEngine = new AppendOnlyFileStorageEngineWithIndex<SerializableWrapper<int>, SerializableWrapper<string>>(storageFileMock.Object, entrySerializer, indexMock);

        // Act & Assert: Check that an IOException is thrown.
        await Assert.ThrowsAsync<IOException>(() => storageEngine.CompactAsync());
    }

    /// <summary>
    /// Lazy index recovery must run exactly once even under concurrent first access. The cached
    /// task field was published with a non-atomic <c>??=</c> (check-then-act), so two concurrent
    /// first operations could both observe the field null and both kick off a full
    /// <c>RebuildIndexFromLogAsync</c> — a redundant whole-log scan, and worse, duplicate index
    /// writes racing into the same index. The fix publishes the task atomically
    /// (<see cref="Interlocked.CompareExchange{T}(ref T, T, T)"/>), so only one rebuild ever runs.
    /// This test fans out many first operations concurrently and asserts the log was scanned
    /// (<c>ReadAllBytesAsync</c>) exactly once.
    /// </summary>
    [Fact]
    public async Task EnsureIndexRecovered_UnderConcurrentFirstAccess_RebuildsExactlyOnce()
    {
        var entrySerializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();

        // Build a real on-disk log so the rebuild has something to scan.
        {
            var seed = new StorageFile(_testFileLocation, _testFileName);
            var engine = new AppendOnlyFileStorageEngineWithIndex<SerializableWrapper<int>, SerializableWrapper<string>>(
                seed, entrySerializer, new InMemoryFileIndex<SerializableWrapper<int>>());
            await engine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("v1"));
            await engine.SetAsync(new SerializableWrapper<int>(2), new SerializableWrapper<string>("v2"));
        }

        var logBytes = await File.ReadAllBytesAsync(TestFilePath);

        // A storage file whose ReadAllBytesAsync (the rebuild's whole-log scan) is counted and
        // gated: every caller blocks on the gate, so concurrent first calls overlap INSIDE the
        // rebuild window — the exact interleaving the non-atomic ??= mishandles. The gate opens
        // only after all racers have entered, guaranteeing they all passed the (pre-fix) null
        // check before any rebuild completes.
        var readCount = 0;
        const int racers = 16;
        using var allEntered = new CountdownEvent(racers);
        using var release = new ManualResetEventSlim(false);

        var fileMock = new Mock<IStorageFile>();
        fileMock.Setup(f => f.Exists()).Returns(true);
        fileMock.SetupGet(f => f.FileSize).Returns(logBytes.Length);
        fileMock.Setup(f => f.ReadAllBytesAsync(It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                Interlocked.Increment(ref readCount);
                allEntered.Signal();
                release.Wait(TimeSpan.FromSeconds(5));
                return Task.FromResult(logBytes);
            });
        // The point read slices the serialized entry out of the log by (offset, count).
        fileMock.Setup(f => f.ReadBytes(It.IsAny<long>(), It.IsAny<long>()))
            .Returns<long, long>((offset, count) => logBytes.AsSpan((int)offset, (int)count).ToArray());
        // The point read after recovery routes through a real in-memory index, which the rebuild
        // populates while scanning the gated bytes.
        var realIndex = new InMemoryFileIndex<SerializableWrapper<int>>();

        var engineUnderTest = new AppendOnlyFileStorageEngineWithIndex<SerializableWrapper<int>, SerializableWrapper<string>>(
            fileMock.Object, entrySerializer, realIndex);

        // Release the gate once every racer that managed to start a rebuild has entered, or after
        // a short grace period (the fix means only one racer ever enters, so the countdown will
        // not reach zero — fall back to releasing on a timer).
        _ = Task.Run(() =>
        {
            allEntered.Wait(TimeSpan.FromMilliseconds(500));
            release.Set();
        });

        // Act: fan out concurrent first reads — all hit EnsureIndexRecoveredAsync simultaneously.
        var tasks = Enumerable.Range(0, racers)
            .Select(_ => Task.Run(() => engineUnderTest.TryGetValueAsync(new SerializableWrapper<int>(1))))
            .ToArray();
        await Task.WhenAll(tasks);

        // Assert: the whole-log scan ran exactly once despite concurrent first access.
        readCount.Should().Be(1, "lazy index recovery must publish its task atomically so only one rebuild runs");
        foreach (var (value, found) in await Task.WhenAll(tasks))
        {
            found.Should().BeTrue();
            value.Value.Should().Be("v1");
        }
    }

    /// <summary>
    /// Index rebuild fidelity over a crash-torn log (DDIA 2e Ch. 4 hash-index model: "you have
    /// to rebuild it when you restart the database — for example, by scanning the whole log
    /// file"). A torn FINAL record (crash mid-append) must not poison the rebuild: every
    /// durably-written key before the tear must resolve through the INDEXED read path, and the
    /// torn key must read as missing.
    /// </summary>
    [Fact]
    public async Task TryGetValueAsync_AfterReopenOverTornTailLog_ServesAllPreTearKeys()
    {
        var entrySerializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();

        // First engine: three durable records.
        {
            var storageFile = new StorageFile(_testFileLocation, _testFileName);
            var engine = new AppendOnlyFileStorageEngineWithIndex<SerializableWrapper<int>, SerializableWrapper<string>>(
                storageFile, entrySerializer,
                new InMemoryFileIndex<SerializableWrapper<int>>());
            await engine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("v1"));
            await engine.SetAsync(new SerializableWrapper<int>(2), new SerializableWrapper<string>("v2"));
            await engine.SetAsync(new SerializableWrapper<int>(3), new SerializableWrapper<string>("v3"));
        }

        // Crash mid-append of record 3: truncate one byte into the final record.
        var boundaries = AppendOnlyFrames.Boundaries(File.ReadAllBytes(TestFilePath));
        boundaries.Count.Should().Be(4, "three records produce boundaries [0, b1, b2, end]");
        using (var truncate = new FileStream(TestFilePath, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            truncate.SetLength(boundaries[2] + 1); // partial final frame
        }

        // Reopen with a fresh index — the rebuild scans the torn log.
        var reopened = new AppendOnlyFileStorageEngineWithIndex<SerializableWrapper<int>, SerializableWrapper<string>>(
            new StorageFile(_testFileLocation, _testFileName), entrySerializer,
            new InMemoryFileIndex<SerializableWrapper<int>>());

        var (v1, found1) = await reopened.TryGetValueAsync(new SerializableWrapper<int>(1));
        found1.Should().BeTrue("record 1 is durable before the tear and must resolve through the rebuilt index");
        v1.Value.Should().Be("v1");

        var (v2, found2) = await reopened.TryGetValueAsync(new SerializableWrapper<int>(2));
        found2.Should().BeTrue("record 2 is durable before the tear and must resolve through the rebuilt index");
        v2.Value.Should().Be("v2");

        var (_, found3) = await reopened.TryGetValueAsync(new SerializableWrapper<int>(3));
        found3.Should().BeFalse("the torn final record was never durably complete — it must read as missing, not garbage");
    }

    /// <summary>
    /// Clean up the test files after each test.
    /// </summary>
    public void Dispose()
    {
        CleanupTestFiles();
    }
}
