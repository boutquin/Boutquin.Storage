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
        static IFileBasedStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>> storageEngineFactory(string fileLocation, string fileName, IEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>> serializer, long maxSize) => new AppendOnlyFileStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(new StorageFile(fileLocation, fileName), serializer);

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
        var (_, found) = await _storageEngine.TryGetValueAsync(key);
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
    /// Regression: when a key is overwritten in a <b>newer</b> segment than its original write,
    /// compaction must keep the newest value (last-writer-wins), not the older segment's value.
    /// The bug: the compaction snapshot enumerates segments newest-first (Stack order) while items
    /// within a segment are append-ordered, so <c>GroupBy(...).Select(g =&gt; g.Last())</c> picked the
    /// oldest segment's value, inverting last-writer-wins across segments
    /// (DDIA Ch. 4 — "if the same key appears in several segments, keep only the more recent value").
    /// </summary>
    [Fact]
    public async Task CompactAsync_ShouldKeepNewestValue_WhenKeyOverwrittenInLaterSegment()
    {
        // Arrange: write key 1 = "old" to segment 0, fill segment 0 past its size limit with a large
        // unrelated value, then overwrite key 1 = "new" — which now lands in a fresh segment 1.
        var key = new SerializableWrapper<int>(1);
        await _storageEngine.SetAsync(key, new SerializableWrapper<string>("old"));
        await _storageEngine.SetAsync(
            new SerializableWrapper<int>(2),
            new SerializableWrapper<string>(new string('a', (int)_maxSegmentSize)));
        await _storageEngine.SetAsync(key, new SerializableWrapper<string>("new"));

        // Sanity: the read path (newest-first) already returns the newest value pre-compaction.
        var (before, _) = await _storageEngine.TryGetValueAsync(key);
        Assert.Equal("new", before.Value);

        // Act
        await _storageEngine.CompactAsync();

        // Assert: compaction must not regress key 1 to the older segment's "old" value.
        var (after, found) = await _storageEngine.TryGetValueAsync(key);
        Assert.True(found);
        Assert.Equal("new", after.Value);
    }

    /// <summary>
    /// GetAllItemsAsync must emit a chronologically-ordered stream. The documented append-log
    /// reading discipline is "the last occurrence reflects the current state" (last-writer-wins),
    /// but the snapshot enumerated segments newest-first while items within a segment are
    /// oldest-first — so a caller applying the documented rule to the flattened stream got the
    /// OLDEST segment's value for a key written across segments (DDIA 2e Ch. 4: keep only the
    /// more recent value). CompactAsync was fixed for exactly this in an earlier pass; the
    /// public surface must agree.
    /// </summary>
    [Fact]
    public async Task GetAllItemsAsync_AcrossSegments_IsChronological_SoLastOccurrenceWins()
    {
        // key 1 = "old" lands in segment 0; oversized filler rolls the segment; key 1 = "new"
        // lands in segment 1.
        var key = new SerializableWrapper<int>(1);
        await _storageEngine.SetAsync(key, new SerializableWrapper<string>("old"));
        await _storageEngine.SetAsync(
            new SerializableWrapper<int>(2),
            new SerializableWrapper<string>(new string('a', (int)_maxSegmentSize)));
        await _storageEngine.SetAsync(key, new SerializableWrapper<string>("new"));

        var items = await _storageEngine.GetAllItemsAsync();
        var deduped = items.GroupBy(x => x.Key).ToDictionary(g => g.Key, g => g.Last().Value);

        Assert.Equal("new", deduped[key].Value);
    }

    /// <summary>
    /// The engine must remain writable after compaction. The bug: MergeSmallSegmentsAsync
    /// treats every post-rewrite segment as "small", copies them into fresh merged segments
    /// and Clear-deletes the sources — but never reassigns <c>_currentSegment</c>, which is
    /// left pointing at a deleted-file segment that is no longer on the stack. The next
    /// SetAsync then throws <see cref="FileNotFoundException"/> (an in-process wedge needing
    /// no crash), and the write would land in an orphaned segment invisible to reads even
    /// if it succeeded.
    /// </summary>
    [Fact]
    public async Task SetAsync_AfterCompactAsync_WritesAndReadsBack()
    {
        // Arrange: two writes (one overwrite) so compaction has work to do.
        await _storageEngine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("v1a"));
        await _storageEngine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("v1b"));

        // Act
        await _storageEngine.CompactAsync();
        await _storageEngine.SetAsync(new SerializableWrapper<int>(2), new SerializableWrapper<string>("v2"));

        // Assert: both the compacted survivor and the post-compaction write are readable.
        var (v1, found1) = await _storageEngine.TryGetValueAsync(new SerializableWrapper<int>(1));
        Assert.True(found1);
        Assert.Equal("v1b", v1.Value);

        var (v2, found2) = await _storageEngine.TryGetValueAsync(new SerializableWrapper<int>(2));
        Assert.True(found2, "a write accepted after compaction must be visible to reads");
        Assert.Equal("v2", v2.Value);

        var items = await _storageEngine.GetAllItemsAsync();
        Assert.Equal(2, items.Count());
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
    /// Regression: MergeSmallSegmentsAsync must preserve last-writer-wins when a full segment
    /// sits chronologically between two small segments.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The bug: <c>MergeSmallSegmentsAsync</c> collects ALL small segments (regardless of their
    /// position in the stack) and merges them into new segments, then pushes every merged segment
    /// on top of every retained full segment. When the original stack is
    /// <c>[S3-small, S2-full(K="new"), S1-small(K="old")]</c> (newest→oldest), the merge
    /// incorrectly produces <c>[merged(S1+S3), S2]</c> — merged on top means S1's stale K="old"
    /// is probed first, hiding S2's K="new".
    /// </para>
    /// <para>
    /// The fix: only merge <b>consecutive</b> runs of small segments, then place each run's merged
    /// output back in the chronological position the run occupied, preserving the full segments'
    /// relative positions.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task MergeSmallSegments_FullSegmentNewerThanSmall_ReadsNewerValue()
    {
        // Use a dedicated folder and a fresh engine so the injected stack is isolated.
        const string folder = "TestSegments_MergeLww";
        const string prefix = "MergeLww";
        const long maxSize = 200L; // small enough to force clear segment boundaries

        IEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>> serializer =
            new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();

        static IFileBasedStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>> Factory(
            string fileLocation, string fileName,
            IEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>> s,
            long maxSz)
            => new AppendOnlyFileStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
                new StorageFile(fileLocation, fileName), s);

        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, true);
        }
        Directory.CreateDirectory(folder);

        try
        {
            // Build three segments directly via the factory so we can control size.
            var s1 = Factory(folder, $"{prefix}_s1.log", serializer, maxSize);
            var s2 = Factory(folder, $"{prefix}_s2.log", serializer, maxSize);
            var s3 = Factory(folder, $"{prefix}_s3.log", serializer, maxSize);

            var key = new SerializableWrapper<int>(42);

            // S1 (oldest, small): key 42 = "old"
            await s1.SetAsync(key, new SerializableWrapper<string>("old"));

            // S2 (middle, full): key 42 = "new" + large filler so FileSize >= maxSize
            await s2.SetAsync(key, new SerializableWrapper<string>("new"));
            await s2.SetAsync(
                new SerializableWrapper<int>(99),
                new SerializableWrapper<string>(new string('x', (int)maxSize)));

            // S3 (newest, small): unrelated key — no key 42
            await s3.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("s3data"));

            Assert.True(s1.FileSize < maxSize, "S1 must be small for the bug to fire");
            Assert.True(s2.FileSize >= maxSize, "S2 must be full (the retained segment)");
            Assert.True(s3.FileSize < maxSize, "S3 must be small for the bug to fire");

            // Create a fresh engine and inject the crafted stack via reflection.
            // InternalsVisibleTo(Infrastructure.Tests) is set on the Infrastructure project,
            // so internal members are accessible; _segments and _currentSegment are private and
            // require reflection.
            var engine = new LogSegmentedStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
                serializer, folder, prefix, maxSize, Factory);

            var engineType = typeof(LogSegmentedStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>);
            var segmentsField = engineType.GetField("_segments",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            var currentSegmentField = engineType.GetField("_currentSegment",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

            // Replace the engine's stack: S1 at bottom (oldest), S2 in middle, S3 on top (newest).
            var stack = (Stack<IFileBasedStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>>)
                segmentsField.GetValue(engine)!;
            stack.Clear();
            stack.Push(s1); // push oldest first → lands at bottom
            stack.Push(s2);
            stack.Push(s3); // push newest last → lands on top
            currentSegmentField.SetValue(engine, s3); // write cursor at newest

            // Pre-merge sanity: the read path (newest-first) probes S3 (miss) then S2 (hit: "new").
            var (preMerge, preMergeFound) = await engine.TryGetValueAsync(key);
            Assert.True(preMergeFound, "key must be readable before merge");
            Assert.Equal("new", preMerge.Value);

            // Invoke the private MergeSmallSegmentsAsync directly so we can observe its
            // LWW behaviour in isolation, without CompactAsync's prior deduplication step
            // (which would remove the duplicate key and hide the ordering bug).
            var mergeMethod = engineType.GetMethod(
                "MergeSmallSegmentsAsync",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            await (Task)mergeMethod.Invoke(engine, [CancellationToken.None])!;

            // Act: read key 42 after the merge.
            var (postMerge, postMergeFound) = await engine.TryGetValueAsync(key);

            // Assert: the full segment S2 (K="new") must still win over the merged S1 (K="old").
            // The buggy code places the merged output on top of S2, so postMerge == "old".
            // The fixed code preserves chronological order, so postMerge == "new".
            Assert.True(postMergeFound, "key must still be readable after merge");
            Assert.Equal("new", postMerge.Value);
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, true);
            }
        }
    }

    /// <summary>
    /// Clean up the test files after each test.
    /// </summary>
    public void Dispose()
    {
        CleanupTestFiles();
    }
}
