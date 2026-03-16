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
/// Unit tests for the LsmStorageEngine class.
/// Each test follows the Arrange-Act-Assert pattern.
/// </summary>
public sealed class LsmStorageEngineTests : IDisposable
{
    private readonly string _testDirectory = Path.Combine(Path.GetTempPath(), $"LsmTests_{Guid.NewGuid():N}");

    public LsmStorageEngineTests()
    {
        if (!Directory.Exists(_testDirectory))
        {
            Directory.CreateDirectory(_testDirectory);
        }
    }

    /// <summary>
    /// Test that writing and reading a single item works when data stays in MemTable.
    /// </summary>
    [Fact]
    public async Task SetAsync_And_TryGetValueAsync_ShouldWorkForSingleItem()
    {
        // Arrange
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 10,
            segmentFolder: _testDirectory,
            segmentPrefix: "seg",
            entrySerializer: serializer);

        var key = new SerializableWrapper<int>(1);
        var value = new SerializableWrapper<string>("hello");

        // Act
        await engine.SetAsync(key, value);
        var (retrieved, found) = await engine.TryGetValueAsync(key);

        // Assert
        found.Should().BeTrue();
        retrieved.Value.Should().Be("hello");
        engine.SegmentCount.Should().Be(0, "no flush should have occurred");
    }

    /// <summary>
    /// Test that writing enough items to fill the MemTable triggers a flush,
    /// and data is still readable from the on-disk segment.
    /// </summary>
    [Fact]
    public async Task SetAsync_ShouldFlushToSegment_WhenMemTableIsFull()
    {
        // Arrange: capacity of 3, write 4 items to force a flush
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 3,
            segmentFolder: _testDirectory,
            segmentPrefix: "seg",
            entrySerializer: serializer);

        // Act: write 3 items to fill MemTable, then a 4th triggers flush
        await engine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("a"));
        await engine.SetAsync(new SerializableWrapper<int>(2), new SerializableWrapper<string>("b"));
        await engine.SetAsync(new SerializableWrapper<int>(3), new SerializableWrapper<string>("c"));
        // MemTable is now full. Next write triggers flush.
        await engine.SetAsync(new SerializableWrapper<int>(4), new SerializableWrapper<string>("d"));

        // Assert: one segment should have been created
        engine.SegmentCount.Should().Be(1);

        // All items should be readable
        var (val1, found1) = await engine.TryGetValueAsync(new SerializableWrapper<int>(1));
        found1.Should().BeTrue();
        val1.Value.Should().Be("a");

        var (val4, found4) = await engine.TryGetValueAsync(new SerializableWrapper<int>(4));
        found4.Should().BeTrue();
        val4.Value.Should().Be("d");
    }

    /// <summary>
    /// Test that reads check the MemTable before on-disk segments.
    /// If a key exists in both, the MemTable value (most recent) wins.
    /// </summary>
    [Fact]
    public async Task TryGetValueAsync_ShouldPreferMemTableOverSegments()
    {
        // Arrange: capacity of 2
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 2,
            segmentFolder: _testDirectory,
            segmentPrefix: "seg",
            entrySerializer: serializer);

        // Write key=1 with "old" value, then fill MemTable to flush it to segment
        await engine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("old"));
        await engine.SetAsync(new SerializableWrapper<int>(2), new SerializableWrapper<string>("filler"));
        // MemTable is now full (2 items). Next write triggers flush.
        await engine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("new"));

        // Act
        var (retrieved, found) = await engine.TryGetValueAsync(new SerializableWrapper<int>(1));

        // Assert: should get the MemTable value ("new"), not the segment value ("old")
        found.Should().BeTrue();
        retrieved.Value.Should().Be("new");
    }

    /// <summary>
    /// Test that multiple flushes create multiple segments.
    /// </summary>
    [Fact]
    public async Task MultipleFlushes_ShouldCreateMultipleSegments()
    {
        // Arrange: capacity of 2
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 2,
            segmentFolder: _testDirectory,
            segmentPrefix: "seg",
            entrySerializer: serializer);

        // Act: write 6 items with capacity 2 => 2 auto-flushes (at items 3 and 5)
        for (var i = 1; i <= 6; i++)
        {
            await engine.SetAsync(new SerializableWrapper<int>(i), new SerializableWrapper<string>($"val{i}"));
        }

        // Assert: should have 2 segments (flush at write 3 and write 5)
        engine.SegmentCount.Should().Be(2);

        // All 6 items should be readable
        for (var i = 1; i <= 6; i++)
        {
            var (val, found) = await engine.TryGetValueAsync(new SerializableWrapper<int>(i));
            found.Should().BeTrue();
            val.Value.Should().Be($"val{i}");
        }
    }

    /// <summary>
    /// Test that GetAllItemsAsync returns all items sorted by key, with deduplication.
    /// </summary>
    [Fact]
    public async Task GetAllItemsAsync_ShouldReturnAllItemsSorted()
    {
        // Arrange: capacity of 3
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 3,
            segmentFolder: _testDirectory,
            segmentPrefix: "seg",
            entrySerializer: serializer);

        // Write items in non-sorted order; some will be flushed to segments
        await engine.SetAsync(new SerializableWrapper<int>(3), new SerializableWrapper<string>("c"));
        await engine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("a"));
        await engine.SetAsync(new SerializableWrapper<int>(5), new SerializableWrapper<string>("e"));
        // MemTable is full, next write flushes
        await engine.SetAsync(new SerializableWrapper<int>(2), new SerializableWrapper<string>("b"));
        await engine.SetAsync(new SerializableWrapper<int>(4), new SerializableWrapper<string>("d"));

        // Act
        var allItems = (await engine.GetAllItemsAsync()).ToList();

        // Assert: 5 items, sorted by key
        allItems.Should().HaveCount(5);
        allItems.Select(i => i.Key.Value).Should().BeInAscendingOrder();
        allItems.Select(i => i.Value.Value).Should().ContainInOrder("a", "b", "c", "d", "e");
    }

    /// <summary>
    /// Test that overwriting a key returns the latest value, even across segments.
    /// </summary>
    [Fact]
    public async Task OverwriteKey_ShouldReturnLatestValue()
    {
        // Arrange: capacity of 2
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 2,
            segmentFolder: _testDirectory,
            segmentPrefix: "seg",
            entrySerializer: serializer);

        // Write key=1 with "v1", then a filler to fill MemTable
        await engine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("v1"));
        await engine.SetAsync(new SerializableWrapper<int>(2), new SerializableWrapper<string>("filler"));
        // MemTable is full. Next write triggers flush.
        // Overwrite key=1 with "v2" (now in MemTable while "v1" is in segment)
        await engine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("v2"));

        // Act
        var (retrieved, found) = await engine.TryGetValueAsync(new SerializableWrapper<int>(1));

        // Assert
        found.Should().BeTrue();
        retrieved.Value.Should().Be("v2");

        // GetAllItemsAsync should also reflect the latest value
        var allItems = (await engine.GetAllItemsAsync()).ToList();
        var key1Item = allItems.First(i => i.Key.Value == 1);
        key1Item.Value.Value.Should().Be("v2");
    }

    /// <summary>
    /// Test that ClearAsync empties everything — MemTable and all segments.
    /// </summary>
    [Fact]
    public async Task ClearAsync_ShouldEmptyEverything()
    {
        // Arrange
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 2,
            segmentFolder: _testDirectory,
            segmentPrefix: "seg",
            entrySerializer: serializer);

        await engine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("a"));
        await engine.SetAsync(new SerializableWrapper<int>(2), new SerializableWrapper<string>("b"));
        // Flush triggered by next write
        await engine.SetAsync(new SerializableWrapper<int>(3), new SerializableWrapper<string>("c"));

        engine.SegmentCount.Should().Be(1, "precondition: should have 1 segment");

        // Act
        await engine.ClearAsync();

        // Assert
        engine.SegmentCount.Should().Be(0);
        var (_, found) = await engine.TryGetValueAsync(new SerializableWrapper<int>(1));
        found.Should().BeFalse();

        var allItems = (await engine.GetAllItemsAsync()).ToList();
        allItems.Should().BeEmpty();
    }

    /// <summary>
    /// Test that RemoveAsync throws NotSupportedException.
    /// </summary>
    [Fact]
    public async Task RemoveAsync_ShouldThrowNotSupportedException()
    {
        // Arrange
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 10,
            segmentFolder: _testDirectory,
            segmentPrefix: "seg",
            entrySerializer: serializer);

        // Act & Assert
        await Assert.ThrowsAsync<NotSupportedException>(
            () => engine.RemoveAsync(new SerializableWrapper<int>(1)));
    }

    /// <summary>
    /// Test that FlushAsync forces a flush even when the MemTable is not full.
    /// </summary>
    [Fact]
    public async Task FlushAsync_ShouldForceFlushWhenMemTableNotFull()
    {
        // Arrange
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 100,
            segmentFolder: _testDirectory,
            segmentPrefix: "seg",
            entrySerializer: serializer);

        await engine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("a"));

        // Act
        await engine.FlushAsync();

        // Assert
        engine.SegmentCount.Should().Be(1);
        var (val, found) = await engine.TryGetValueAsync(new SerializableWrapper<int>(1));
        found.Should().BeTrue();
        val.Value.Should().Be("a");
    }

    /// <summary>
    /// Test that FlushAsync on empty MemTable is a no-op.
    /// </summary>
    [Fact]
    public async Task FlushAsync_ShouldBeNoOp_WhenMemTableIsEmpty()
    {
        // Arrange
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 10,
            segmentFolder: _testDirectory,
            segmentPrefix: "seg",
            entrySerializer: serializer);

        // Act
        await engine.FlushAsync();

        // Assert
        engine.SegmentCount.Should().Be(0);
    }

    /// <summary>
    /// Test that ContainsKeyAsync returns true for existing keys and false for missing ones.
    /// </summary>
    [Fact]
    public async Task ContainsKeyAsync_ShouldReturnCorrectResults()
    {
        // Arrange
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 10,
            segmentFolder: _testDirectory,
            segmentPrefix: "seg",
            entrySerializer: serializer);

        await engine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("a"));

        // Act & Assert
        var exists = await engine.ContainsKeyAsync(new SerializableWrapper<int>(1));
        exists.Should().BeTrue();

        var missing = await engine.ContainsKeyAsync(new SerializableWrapper<int>(999));
        missing.Should().BeFalse();
    }

    /// <summary>
    /// Test that SetBulkAsync writes multiple items correctly.
    /// </summary>
    [Fact]
    public async Task SetBulkAsync_ShouldWriteMultipleItems()
    {
        // Arrange
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 5,
            segmentFolder: _testDirectory,
            segmentPrefix: "seg",
            entrySerializer: serializer);

        var items = new List<KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>>
        {
            new(new SerializableWrapper<int>(1), new SerializableWrapper<string>("a")),
            new(new SerializableWrapper<int>(2), new SerializableWrapper<string>("b")),
            new(new SerializableWrapper<int>(3), new SerializableWrapper<string>("c"))
        };

        // Act
        await engine.SetBulkAsync(items);

        // Assert
        var allItems = (await engine.GetAllItemsAsync()).ToList();
        allItems.Should().HaveCount(3);
    }

    /// <summary>
    /// Test that TryGetValueAsync returns false for a non-existent key.
    /// </summary>
    [Fact]
    public async Task TryGetValueAsync_ShouldReturnFalse_WhenKeyDoesNotExist()
    {
        // Arrange
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 10,
            segmentFolder: _testDirectory,
            segmentPrefix: "seg",
            entrySerializer: serializer);

        // Act
        var (_, found) = await engine.TryGetValueAsync(new SerializableWrapper<int>(42));

        // Assert
        found.Should().BeFalse();
    }

    /// <summary>
    /// Test that SetAsync throws ObjectDisposedException after Dispose (H10).
    /// </summary>
    [Fact]
    public async Task SetAsync_AfterDispose_ShouldThrowObjectDisposedException()
    {
        // Arrange
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 10,
            segmentFolder: _testDirectory,
            segmentPrefix: "seg",
            entrySerializer: serializer);
        engine.Dispose();

        // Act & Assert
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => engine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("val")));
    }

    /// <summary>
    /// Test that TryGetValueAsync throws ObjectDisposedException after Dispose (H10).
    /// </summary>
    [Fact]
    public async Task TryGetValueAsync_AfterDispose_ShouldThrowObjectDisposedException()
    {
        // Arrange
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 10,
            segmentFolder: _testDirectory,
            segmentPrefix: "seg",
            entrySerializer: serializer);
        engine.Dispose();

        // Act & Assert
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => engine.TryGetValueAsync(new SerializableWrapper<int>(1)));
    }

    /// <summary>
    /// Test that ContainsKeyAsync throws ObjectDisposedException after Dispose (H10).
    /// </summary>
    [Fact]
    public async Task ContainsKeyAsync_AfterDispose_ShouldThrowObjectDisposedException()
    {
        // Arrange
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 10,
            segmentFolder: _testDirectory,
            segmentPrefix: "seg",
            entrySerializer: serializer);
        engine.Dispose();

        // Act & Assert
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => engine.ContainsKeyAsync(new SerializableWrapper<int>(1)));
    }

    /// <summary>
    /// Test that RemoveAsync throws ObjectDisposedException after Dispose (H10).
    /// </summary>
    [Fact]
    public async Task RemoveAsync_AfterDispose_ShouldThrowObjectDisposedException()
    {
        // Arrange
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 10,
            segmentFolder: _testDirectory,
            segmentPrefix: "seg",
            entrySerializer: serializer);
        engine.Dispose();

        // Act & Assert
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => engine.RemoveAsync(new SerializableWrapper<int>(1)));
    }

    /// <summary>
    /// Test that ClearAsync throws ObjectDisposedException after Dispose (H10).
    /// </summary>
    [Fact]
    public async Task ClearAsync_AfterDispose_ShouldThrowObjectDisposedException()
    {
        // Arrange
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 10,
            segmentFolder: _testDirectory,
            segmentPrefix: "seg",
            entrySerializer: serializer);
        engine.Dispose();

        // Act & Assert
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => engine.ClearAsync());
    }

    /// <summary>
    /// Test that GetAllItemsAsync throws ObjectDisposedException after Dispose (H10).
    /// </summary>
    [Fact]
    public async Task GetAllItemsAsync_AfterDispose_ShouldThrowObjectDisposedException()
    {
        // Arrange
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 10,
            segmentFolder: _testDirectory,
            segmentPrefix: "seg",
            entrySerializer: serializer);
        engine.Dispose();

        // Act & Assert
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => engine.GetAllItemsAsync());
    }

    /// <summary>
    /// Test that SetBulkAsync throws ObjectDisposedException after Dispose (H10).
    /// </summary>
    [Fact]
    public async Task SetBulkAsync_AfterDispose_ShouldThrowObjectDisposedException()
    {
        // Arrange
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 10,
            segmentFolder: _testDirectory,
            segmentPrefix: "seg",
            entrySerializer: serializer);
        engine.Dispose();

        // Act & Assert
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => engine.SetBulkAsync(new List<KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>>()));
    }

    /// <summary>
    /// Test that FlushAsync throws ObjectDisposedException after Dispose (H10).
    /// </summary>
    [Fact]
    public async Task FlushAsync_AfterDispose_ShouldThrowObjectDisposedException()
    {
        // Arrange
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 10,
            segmentFolder: _testDirectory,
            segmentPrefix: "seg",
            entrySerializer: serializer);
        engine.Dispose();

        // Act & Assert
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => engine.FlushAsync());
    }

    // ---- Compaction tests ----

    /// <summary>
    /// Test that CompactAsync merges multiple segments into one.
    /// </summary>
    [Fact]
    public async Task CompactAsync_ShouldMergeMultipleSegmentsIntoOne()
    {
        // Arrange: capacity of 2, write 6 items => 2 auto-flushes => 2 segments + MemTable
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 2,
            segmentFolder: _testDirectory,
            segmentPrefix: "seg",
            entrySerializer: serializer);

        for (var i = 1; i <= 6; i++)
        {
            await engine.SetAsync(new SerializableWrapper<int>(i), new SerializableWrapper<string>($"val{i}"));
        }

        // Flush remaining MemTable to get all data on disk
        await engine.FlushAsync();
        engine.SegmentCount.Should().BeGreaterThan(1, "precondition: need multiple segments");
        var segmentsBefore = engine.SegmentCount;

        // Act
        await engine.CompactAsync();

        // Assert: all segments merged into one
        engine.SegmentCount.Should().Be(1);

        // All data should still be readable
        for (var i = 1; i <= 6; i++)
        {
            var (val, found) = await engine.TryGetValueAsync(new SerializableWrapper<int>(i));
            found.Should().BeTrue($"key {i} should be found after compaction");
            val.Value.Should().Be($"val{i}");
        }
    }

    /// <summary>
    /// Test that CompactAsync preserves last-writer-wins semantics for duplicate keys across segments.
    /// </summary>
    [Fact]
    public async Task CompactAsync_ShouldPreserveLastWriterWins()
    {
        // Arrange: capacity of 2
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 2,
            segmentFolder: _testDirectory,
            segmentPrefix: "seg",
            entrySerializer: serializer);

        // Write key=1 with "v1", then filler to flush
        await engine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("v1"));
        await engine.SetAsync(new SerializableWrapper<int>(2), new SerializableWrapper<string>("filler1"));
        // MemTable full, next write flushes. Segment 0 has: {1=v1, 2=filler1}

        // Overwrite key=1 with "v2", then filler to flush
        await engine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("v2"));
        await engine.SetAsync(new SerializableWrapper<int>(3), new SerializableWrapper<string>("filler2"));
        // MemTable full again. Segment 1 has: {1=v2, 3=filler2}

        // Flush remaining
        await engine.FlushAsync();
        engine.SegmentCount.Should().BeGreaterThanOrEqualTo(2, "precondition: need multiple segments");

        // Act
        await engine.CompactAsync();

        // Assert: key=1 should have the latest value "v2" (from the newer segment)
        var (val, found) = await engine.TryGetValueAsync(new SerializableWrapper<int>(1));
        found.Should().BeTrue();
        val.Value.Should().Be("v2", "compaction must preserve last-writer-wins");

        // Other keys should still be present
        var (val2, found2) = await engine.TryGetValueAsync(new SerializableWrapper<int>(2));
        found2.Should().BeTrue();
        val2.Value.Should().Be("filler1");

        var (val3, found3) = await engine.TryGetValueAsync(new SerializableWrapper<int>(3));
        found3.Should().BeTrue();
        val3.Value.Should().Be("filler2");
    }

    /// <summary>
    /// Test that CompactAsync is a no-op when there are zero or one segments.
    /// </summary>
    [Theory]
    [InlineData(0)] // no segments at all
    [InlineData(1)] // single segment (nothing to merge)
    public async Task CompactAsync_ShouldBeNoOp_WhenSegmentCountIsLessThanTwo(int segmentsToCreate)
    {
        // Arrange
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 10,
            segmentFolder: _testDirectory,
            segmentPrefix: "seg",
            entrySerializer: serializer);

        if (segmentsToCreate == 1)
        {
            await engine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("a"));
            await engine.FlushAsync();
        }

        engine.SegmentCount.Should().Be(segmentsToCreate, "precondition");

        // Act
        await engine.CompactAsync();

        // Assert: segment count unchanged
        engine.SegmentCount.Should().Be(segmentsToCreate);

        // Data should still be readable if any exists
        if (segmentsToCreate == 1)
        {
            var (val, found) = await engine.TryGetValueAsync(new SerializableWrapper<int>(1));
            found.Should().BeTrue();
            val.Value.Should().Be("a");
        }
    }

    /// <summary>
    /// Test that CompactAsync removes old segment files from disk.
    /// </summary>
    [Fact]
    public async Task CompactAsync_ShouldDeleteOldSegmentFiles()
    {
        // Arrange: capacity of 2, create multiple segments
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 2,
            segmentFolder: _testDirectory,
            segmentPrefix: "seg",
            entrySerializer: serializer);

        for (var i = 1; i <= 6; i++)
        {
            await engine.SetAsync(new SerializableWrapper<int>(i), new SerializableWrapper<string>($"val{i}"));
        }

        await engine.FlushAsync();

        // Count .dat files before compaction
        var filesBefore = Directory.GetFiles(_testDirectory, "seg_*.dat");
        filesBefore.Length.Should().BeGreaterThan(1, "precondition: multiple segment files");

        // Act
        await engine.CompactAsync();

        // Assert: only 1 segment file remains (the compacted one)
        var filesAfter = Directory.GetFiles(_testDirectory, "seg_*.dat");
        filesAfter.Length.Should().Be(1, "old segment files should be deleted after compaction");
    }

    /// <summary>
    /// Test that CompactAsync throws ObjectDisposedException after Dispose.
    /// </summary>
    [Fact]
    public async Task CompactAsync_AfterDispose_ShouldThrowObjectDisposedException()
    {
        // Arrange
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 10,
            segmentFolder: _testDirectory,
            segmentPrefix: "seg",
            entrySerializer: serializer);
        engine.Dispose();

        // Act & Assert
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => engine.CompactAsync());
    }

    /// <summary>
    /// Test that CompactAsync respects CancellationToken.
    /// </summary>
    [Fact]
    public async Task CompactAsync_WithCancelledToken_ShouldThrowOperationCanceledException()
    {
        // Arrange
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 2,
            segmentFolder: _testDirectory,
            segmentPrefix: "seg",
            entrySerializer: serializer);

        // Create multiple segments
        for (var i = 1; i <= 4; i++)
        {
            await engine.SetAsync(new SerializableWrapper<int>(i), new SerializableWrapper<string>($"val{i}"));
        }

        await engine.FlushAsync();
        var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => engine.CompactAsync(cts.Token));
    }

    /// <summary>
    /// Test that writes and reads continue to work correctly after compaction.
    /// </summary>
    [Fact]
    public async Task CompactAsync_ShouldAllowContinuedWritesAndReads()
    {
        // Arrange
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 2,
            segmentFolder: _testDirectory,
            segmentPrefix: "seg",
            entrySerializer: serializer);

        // Create some segments
        for (var i = 1; i <= 4; i++)
        {
            await engine.SetAsync(new SerializableWrapper<int>(i), new SerializableWrapper<string>($"val{i}"));
        }

        await engine.FlushAsync();
        await engine.CompactAsync();
        engine.SegmentCount.Should().Be(1, "post-compaction should have 1 segment");

        // Act: write more data after compaction
        await engine.SetAsync(new SerializableWrapper<int>(5), new SerializableWrapper<string>("val5"));
        await engine.SetAsync(new SerializableWrapper<int>(6), new SerializableWrapper<string>("val6"));
        // This flush creates a new segment alongside the compacted one
        await engine.FlushAsync();

        // Assert: all data (pre- and post-compaction) should be readable
        for (var i = 1; i <= 6; i++)
        {
            var (val, found) = await engine.TryGetValueAsync(new SerializableWrapper<int>(i));
            found.Should().BeTrue($"key {i} should be found");
            val.Value.Should().Be($"val{i}");
        }

        engine.SegmentCount.Should().Be(2, "compacted + new segment");
    }

    /// <summary>
    /// Test that GetAllItemsAsync returns correct results after compaction.
    /// </summary>
    [Fact]
    public async Task GetAllItemsAsync_ShouldReturnCorrectResults_AfterCompaction()
    {
        // Arrange
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 2,
            segmentFolder: _testDirectory,
            segmentPrefix: "seg",
            entrySerializer: serializer);

        // Write with overwrites: key=1 written twice
        await engine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("old"));
        await engine.SetAsync(new SerializableWrapper<int>(2), new SerializableWrapper<string>("b"));
        // Flush triggered, then overwrite key=1
        await engine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("new"));
        await engine.FlushAsync();

        await engine.CompactAsync();

        // Act
        var allItems = (await engine.GetAllItemsAsync()).ToList();

        // Assert: key=1 should have "new", key=2 should have "b", no duplicates
        allItems.Should().HaveCount(2);
        allItems.Select(i => i.Key.Value).Should().BeInAscendingOrder();
        allItems.First(i => i.Key.Value == 1).Value.Value.Should().Be("new");
        allItems.First(i => i.Key.Value == 2).Value.Value.Should().Be("b");
    }

    /// <summary>
    /// Test that auto-compaction triggers when segment count reaches the threshold.
    /// </summary>
    [Fact]
    public async Task AutoCompaction_ShouldTrigger_WhenSegmentCountReachesThreshold()
    {
        // Arrange: capacity of 2, compaction threshold of 3
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 2,
            segmentFolder: _testDirectory,
            segmentPrefix: "seg",
            entrySerializer: serializer,
            compactionThreshold: 3);

        // Act: write enough to create 3+ segments (capacity 2, so flush every 2 items)
        // Items 1-2 => flush (segment 0)
        // Items 3-4 => flush (segment 1)
        // Items 5-6 => flush (segment 2) => triggers auto-compaction (3 >= threshold 3)
        for (var i = 1; i <= 7; i++)
        {
            await engine.SetAsync(new SerializableWrapper<int>(i), new SerializableWrapper<string>($"val{i}"));
        }

        // Assert: auto-compaction should have reduced segments
        engine.SegmentCount.Should().BeLessThan(3, "auto-compaction should have merged segments");

        // All data should be intact
        for (var i = 1; i <= 7; i++)
        {
            var (val, found) = await engine.TryGetValueAsync(new SerializableWrapper<int>(i));
            found.Should().BeTrue($"key {i} should be found after auto-compaction");
            val.Value.Should().Be($"val{i}");
        }
    }

    /// <summary>
    /// Test that multiple rounds of compaction produce correct results.
    /// </summary>
    [Fact]
    public async Task CompactAsync_MultipleTimes_ShouldProduceCorrectResults()
    {
        // Arrange
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 2,
            segmentFolder: _testDirectory,
            segmentPrefix: "seg",
            entrySerializer: serializer);

        // Round 1: write and compact
        await engine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("a"));
        await engine.SetAsync(new SerializableWrapper<int>(2), new SerializableWrapper<string>("b"));
        await engine.FlushAsync();
        await engine.SetAsync(new SerializableWrapper<int>(3), new SerializableWrapper<string>("c"));
        await engine.SetAsync(new SerializableWrapper<int>(4), new SerializableWrapper<string>("d"));
        await engine.FlushAsync();
        await engine.CompactAsync();

        // Round 2: write more and compact again
        await engine.SetAsync(new SerializableWrapper<int>(5), new SerializableWrapper<string>("e"));
        await engine.SetAsync(new SerializableWrapper<int>(6), new SerializableWrapper<string>("f"));
        await engine.FlushAsync();
        await engine.CompactAsync();

        // Assert: should have exactly 1 segment
        engine.SegmentCount.Should().Be(1);

        // All 6 items should be correct
        var allItems = (await engine.GetAllItemsAsync()).ToList();
        allItems.Should().HaveCount(6);
        allItems.Select(i => i.Key.Value).Should().BeInAscendingOrder();
    }

    // ---- WriteAheadLog integration tests ----

    /// <summary>
    /// Test that when a WriteAheadLog is provided, writes are logged to the WriteAheadLog before being applied to the MemTable.
    /// After flush, the WriteAheadLog is truncated.
    /// </summary>
    [Fact]
    public async Task WithWal_SetAsync_ShouldLogToWal_AndTruncateAfterFlush()
    {
        // Arrange
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        var walPath = Path.Combine(_testDirectory, "test.wal");
        using var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(walPath);
        using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 10,
            segmentFolder: _testDirectory,
            segmentPrefix: "seg",
            entrySerializer: serializer,
            writeAheadLog: wal);

        // Act: write an item
        await engine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("hello"));

        // Assert: WriteAheadLog file should exist and contain data
        File.Exists(walPath).Should().BeTrue("WriteAheadLog file should be created on write");
        new FileInfo(walPath).Length.Should().BeGreaterThan(0, "WriteAheadLog should contain the logged entry");

        // Act: flush to disk
        await engine.FlushAsync();

        // Assert: WriteAheadLog should be truncated after flush
        new FileInfo(walPath).Length.Should().Be(0, "WriteAheadLog should be truncated after flush");
    }

    /// <summary>
    /// Test that the engine works correctly without a WriteAheadLog (backward compatibility).
    /// </summary>
    [Fact]
    public async Task WithoutWal_ShouldWorkNormally()
    {
        // Arrange
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 10,
            segmentFolder: _testDirectory,
            segmentPrefix: "seg",
            entrySerializer: serializer);

        // Act
        await engine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("hello"));
        var (val, found) = await engine.TryGetValueAsync(new SerializableWrapper<int>(1));

        // Assert
        found.Should().BeTrue();
        val.Value.Should().Be("hello");
    }

    // ---- Recovery tests ----

    /// <summary>
    /// Test that a new engine instance recovers existing on-disk segments from a previous instance.
    /// </summary>
    [Fact]
    public async Task Recovery_ShouldLoadExistingSegmentsFromDisk()
    {
        // Arrange: create an engine, write data, flush to disk, then dispose
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        {
            using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
                memTableCapacity: 2,
                segmentFolder: _testDirectory,
                segmentPrefix: "seg",
                entrySerializer: serializer);

            await engine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("a"));
            await engine.SetAsync(new SerializableWrapper<int>(2), new SerializableWrapper<string>("b"));
            await engine.FlushAsync();
            engine.SegmentCount.Should().BeGreaterThan(0);
        }

        // Act: create a new engine instance pointing to the same directory
        using var recovered = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 10,
            segmentFolder: _testDirectory,
            segmentPrefix: "seg",
            entrySerializer: serializer);

        // Assert: the new engine should have found the existing segments
        recovered.SegmentCount.Should().BeGreaterThan(0, "engine should recover existing segment files");

        var (val1, found1) = await recovered.TryGetValueAsync(new SerializableWrapper<int>(1));
        found1.Should().BeTrue("key 1 should be readable from recovered segments");
        val1.Value.Should().Be("a");

        var (val2, found2) = await recovered.TryGetValueAsync(new SerializableWrapper<int>(2));
        found2.Should().BeTrue("key 2 should be readable from recovered segments");
        val2.Value.Should().Be("b");
    }

    /// <summary>
    /// Test that a new engine instance replays WriteAheadLog entries into the MemTable on startup.
    /// </summary>
    [Fact]
    public async Task Recovery_ShouldReplayWalIntoMemTable()
    {
        // Arrange: create an engine with WriteAheadLog, write data (not flushed), then dispose
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        var walPath = Path.Combine(_testDirectory, "test.wal");

        {
            using var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(walPath);
            using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
                memTableCapacity: 10,
                segmentFolder: _testDirectory,
                segmentPrefix: "seg",
                entrySerializer: serializer,
                writeAheadLog: wal);

            await engine.SetAsync(new SerializableWrapper<int>(42), new SerializableWrapper<string>("unflushed_value"));
            // Do NOT flush — simulate crash by disposing without flush
        }

        // Act: create a new engine with the same WriteAheadLog — it should replay
        using var wal2 = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(walPath);
        using var recovered = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 10,
            segmentFolder: _testDirectory,
            segmentPrefix: "seg",
            entrySerializer: serializer,
            writeAheadLog: wal2);

        // Assert: the unflushed key should be recovered from the WriteAheadLog
        var (val, found) = await recovered.TryGetValueAsync(new SerializableWrapper<int>(42));
        found.Should().BeTrue("key 42 should be recovered from WriteAheadLog replay");
        val.Value.Should().Be("unflushed_value");
    }

    /// <summary>
    /// Test that recovery handles both existing segments AND WriteAheadLog replay together.
    /// </summary>
    [Fact]
    public async Task Recovery_ShouldHandleBothSegmentsAndWalReplay()
    {
        // Arrange: create engine, write some data, flush (creates segment), write more (WriteAheadLog only)
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        var walPath = Path.Combine(_testDirectory, "test.wal");

        {
            using var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(walPath);
            using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
                memTableCapacity: 10,
                segmentFolder: _testDirectory,
                segmentPrefix: "seg",
                entrySerializer: serializer,
                writeAheadLog: wal);

            // This will be flushed to a segment
            await engine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("flushed"));
            await engine.FlushAsync();

            // This remains in MemTable/WriteAheadLog only
            await engine.SetAsync(new SerializableWrapper<int>(2), new SerializableWrapper<string>("wal_only"));
        }

        // Act: recover
        using var wal2 = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(walPath);
        using var recovered = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 10,
            segmentFolder: _testDirectory,
            segmentPrefix: "seg",
            entrySerializer: serializer,
            writeAheadLog: wal2);

        // Assert: both keys should be recoverable
        var (val1, found1) = await recovered.TryGetValueAsync(new SerializableWrapper<int>(1));
        found1.Should().BeTrue("key 1 should be in recovered segment");
        val1.Value.Should().Be("flushed");

        var (val2, found2) = await recovered.TryGetValueAsync(new SerializableWrapper<int>(2));
        found2.Should().BeTrue("key 2 should be recovered from WriteAheadLog");
        val2.Value.Should().Be("wal_only");
    }

    // ---- Tombstone/delete tests ----

    /// <summary>
    /// Test that RemoveAsync with tombstone support marks a key as deleted.
    /// </summary>
    [Fact]
    public async Task RemoveAsync_WithTombstoneSupport_ShouldDeleteKey()
    {
        // Arrange
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 10,
            segmentFolder: _testDirectory,
            segmentPrefix: "seg",
            entrySerializer: serializer,
            enableTombstones: true);

        await engine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("value"));

        // Act
        await engine.RemoveAsync(new SerializableWrapper<int>(1));

        // Assert
        var (_, found) = await engine.TryGetValueAsync(new SerializableWrapper<int>(1));
        found.Should().BeFalse("key should be deleted after RemoveAsync with tombstones");
    }

    /// <summary>
    /// Test that tombstones survive flushes — a deleted key should not reappear.
    /// </summary>
    [Fact]
    public async Task RemoveAsync_TombstoneShouldSurviveFlush()
    {
        // Arrange
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 2,
            segmentFolder: _testDirectory,
            segmentPrefix: "seg",
            entrySerializer: serializer,
            enableTombstones: true);

        // Write key=1 then flush to create a segment
        await engine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("value"));
        await engine.SetAsync(new SerializableWrapper<int>(2), new SerializableWrapper<string>("filler"));
        // MemTable full, next write triggers flush

        // Delete key=1 (tombstone in MemTable)
        await engine.RemoveAsync(new SerializableWrapper<int>(1));

        // Flush the tombstone to disk
        await engine.FlushAsync();

        // Assert: key=1 should still be deleted
        var (_, found) = await engine.TryGetValueAsync(new SerializableWrapper<int>(1));
        found.Should().BeFalse("tombstone should survive flush to prevent deleted key from reappearing");
    }

    /// <summary>
    /// Test that compaction strips tombstones and reclaims space.
    /// </summary>
    [Fact]
    public async Task CompactAsync_ShouldStripTombstones()
    {
        // Arrange
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 2,
            segmentFolder: _testDirectory,
            segmentPrefix: "seg",
            entrySerializer: serializer,
            enableTombstones: true);

        // Write 4 items (creates 2 segments)
        await engine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("a"));
        await engine.SetAsync(new SerializableWrapper<int>(2), new SerializableWrapper<string>("b"));
        await engine.SetAsync(new SerializableWrapper<int>(3), new SerializableWrapper<string>("c"));
        await engine.SetAsync(new SerializableWrapper<int>(4), new SerializableWrapper<string>("d"));

        // Delete key=1, then flush
        await engine.RemoveAsync(new SerializableWrapper<int>(1));
        await engine.FlushAsync();

        // Act: compact — should strip the tombstone for key=1
        await engine.CompactAsync();

        // Assert: key=1 should not exist, keys 2-4 should
        var (_, found1) = await engine.TryGetValueAsync(new SerializableWrapper<int>(1));
        found1.Should().BeFalse("tombstone should be stripped during compaction");

        var allItems = (await engine.GetAllItemsAsync()).ToList();
        allItems.Should().HaveCount(3, "only keys 2, 3, 4 should remain after compaction strips tombstone");
    }

    /// <summary>
    /// Test that RemoveAsync still throws NotSupportedException when tombstones are not enabled (backward compat).
    /// </summary>
    [Fact]
    public async Task RemoveAsync_WithoutTombstones_ShouldThrowNotSupportedException()
    {
        // Arrange
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 10,
            segmentFolder: _testDirectory,
            segmentPrefix: "seg",
            entrySerializer: serializer,
            enableTombstones: false);

        // Act & Assert
        await Assert.ThrowsAsync<NotSupportedException>(
            () => engine.RemoveAsync(new SerializableWrapper<int>(1)));
    }

    // ---- Range query tests ----

    /// <summary>
    /// Test that GetRangeAsync returns items within the specified key range (inclusive).
    /// </summary>
    [Fact]
    public async Task GetRangeAsync_ShouldReturnItemsInRange()
    {
        // Arrange
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 10,
            segmentFolder: _testDirectory,
            segmentPrefix: "seg",
            entrySerializer: serializer);

        for (var i = 1; i <= 10; i++)
        {
            await engine.SetAsync(new SerializableWrapper<int>(i), new SerializableWrapper<string>($"val{i}"));
        }

        // Act
        var range = (await engine.GetRangeAsync(
            new SerializableWrapper<int>(3),
            new SerializableWrapper<int>(7))).ToList();

        // Assert
        range.Should().HaveCount(5);
        range.Select(r => r.Key.Value).Should().BeEquivalentTo([3, 4, 5, 6, 7]);
    }

    /// <summary>
    /// Test that GetRangeAsync returns items across MemTable and segments.
    /// </summary>
    [Fact]
    public async Task GetRangeAsync_ShouldSpanMemTableAndSegments()
    {
        // Arrange: capacity of 3
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 3,
            segmentFolder: _testDirectory,
            segmentPrefix: "seg",
            entrySerializer: serializer);

        // Write 5 items: keys 1-3 flushed to segment, keys 4-5 in MemTable
        for (var i = 1; i <= 5; i++)
        {
            await engine.SetAsync(new SerializableWrapper<int>(i), new SerializableWrapper<string>($"val{i}"));
        }

        engine.SegmentCount.Should().BeGreaterThan(0, "precondition: some data should be on disk");

        // Act: range spanning both segment and MemTable
        var range = (await engine.GetRangeAsync(
            new SerializableWrapper<int>(2),
            new SerializableWrapper<int>(5))).ToList();

        // Assert
        range.Should().HaveCount(4);
        range.Select(r => r.Key.Value).Should().BeEquivalentTo([2, 3, 4, 5]);
    }

    /// <summary>
    /// Test that GetRangeAsync returns empty when no keys are in range.
    /// </summary>
    [Fact]
    public async Task GetRangeAsync_ShouldReturnEmpty_WhenNoKeysInRange()
    {
        // Arrange
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 10,
            segmentFolder: _testDirectory,
            segmentPrefix: "seg",
            entrySerializer: serializer);

        await engine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("a"));
        await engine.SetAsync(new SerializableWrapper<int>(10), new SerializableWrapper<string>("b"));

        // Act: range between existing keys with no matches
        var range = (await engine.GetRangeAsync(
            new SerializableWrapper<int>(5),
            new SerializableWrapper<int>(8))).ToList();

        // Assert
        range.Should().BeEmpty();
    }

    // ---- Bloom filter integration tests ----

    /// <summary>
    /// Test that providing a bloom filter factory enables per-segment bloom filters
    /// and reads still return correct results.
    /// </summary>
    [Fact]
    public async Task WithBloomFilter_ReadsShouldReturnCorrectResults()
    {
        // Arrange
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 2,
            segmentFolder: _testDirectory,
            segmentPrefix: "seg",
            entrySerializer: serializer,
            bloomFilterFactory: () => new BloomFilter<SerializableWrapper<int>>(100, 0.01));

        // Write enough to create segments
        for (var i = 1; i <= 6; i++)
        {
            await engine.SetAsync(new SerializableWrapper<int>(i), new SerializableWrapper<string>($"val{i}"));
        }

        await engine.FlushAsync();
        engine.SegmentCount.Should().BeGreaterThan(0);

        // Act & Assert: all keys should be findable
        for (var i = 1; i <= 6; i++)
        {
            var (val, found) = await engine.TryGetValueAsync(new SerializableWrapper<int>(i));
            found.Should().BeTrue($"key {i} should be found with bloom filter");
            val.Value.Should().Be($"val{i}");
        }

        // Non-existent key
        var (_, notFound) = await engine.TryGetValueAsync(new SerializableWrapper<int>(999));
        notFound.Should().BeFalse();
    }

    /// <summary>
    /// Test that bloom filter correctly skips segments (negative lookup).
    /// </summary>
    [Fact]
    public async Task WithBloomFilter_ShouldSkipSegmentsForMissingKeys()
    {
        // Arrange
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 2,
            segmentFolder: _testDirectory,
            segmentPrefix: "seg",
            entrySerializer: serializer,
            bloomFilterFactory: () => new BloomFilter<SerializableWrapper<int>>(100, 0.01));

        // Create a segment with keys 1, 2
        await engine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("a"));
        await engine.SetAsync(new SerializableWrapper<int>(2), new SerializableWrapper<string>("b"));
        await engine.FlushAsync();

        // Act: query for a key that definitely doesn't exist
        var (_, found) = await engine.TryGetValueAsync(new SerializableWrapper<int>(999));

        // Assert: should return false (bloom filter should have short-circuited the segment check)
        found.Should().BeFalse();
    }

    // ---- Compaction strategy integration tests ----

    /// <summary>
    /// Test that FullCompactionStrategy triggers auto-compaction when the segment count reaches the threshold.
    /// </summary>
    [Fact]
    public async Task WithCompactionStrategy_FullCompaction_ShouldTriggerAtThreshold()
    {
        // Arrange: capacity of 2, FullCompactionStrategy with threshold=3
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        var strategy = new FullCompactionStrategy(threshold: 3);
        using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 2,
            segmentFolder: _testDirectory,
            segmentPrefix: "seg",
            entrySerializer: serializer,
            compactionStrategy: strategy);

        // Act: write enough to create 3 segments (capacity 2, so flush every 2 items)
        // Items 1-2 => flush (segment 0)
        // Items 3-4 => flush (segment 1)
        // Items 5-6 => flush (segment 2) => triggers auto-compaction (3 >= threshold 3)
        for (var i = 1; i <= 7; i++)
        {
            await engine.SetAsync(new SerializableWrapper<int>(i), new SerializableWrapper<string>($"val{i}"));
        }

        // Assert: auto-compaction should have reduced segments
        engine.SegmentCount.Should().BeLessThan(3, "FullCompactionStrategy should have triggered compaction");

        // All data should be intact
        for (var i = 1; i <= 7; i++)
        {
            var (val, found) = await engine.TryGetValueAsync(new SerializableWrapper<int>(i));
            found.Should().BeTrue($"key {i} should be found after strategy-triggered compaction");
            val.Value.Should().Be($"val{i}");
        }
    }

    /// <summary>
    /// Test that SizeTieredCompactionStrategy triggers when segment count reaches minSegments.
    /// </summary>
    [Fact]
    public async Task WithCompactionStrategy_SizeTiered_ShouldTriggerAtMinSegments()
    {
        // Arrange: capacity of 2, SizeTieredCompactionStrategy with minSegments=4
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        var strategy = new SizeTieredCompactionStrategy(minSegments: 4);
        using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 2,
            segmentFolder: _testDirectory,
            segmentPrefix: "seg",
            entrySerializer: serializer,
            compactionStrategy: strategy);

        // Act: write enough to create 4+ segments
        for (var i = 1; i <= 9; i++)
        {
            await engine.SetAsync(new SerializableWrapper<int>(i), new SerializableWrapper<string>($"val{i}"));
        }

        // Assert: auto-compaction should have reduced segments below 4
        engine.SegmentCount.Should().BeLessThan(4, "SizeTieredCompactionStrategy should have triggered compaction");

        // All data should be intact
        for (var i = 1; i <= 9; i++)
        {
            var (val, found) = await engine.TryGetValueAsync(new SerializableWrapper<int>(i));
            found.Should().BeTrue($"key {i} should be found after size-tiered compaction");
            val.Value.Should().Be($"val{i}");
        }
    }

    /// <summary>
    /// Test that compaction strategy takes precedence over compactionThreshold when both are provided.
    /// </summary>
    [Fact]
    public async Task WithCompactionStrategy_ShouldTakePrecedenceOverThreshold()
    {
        // Arrange: compactionThreshold=0 (disabled), but strategy with threshold=2
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        var strategy = new FullCompactionStrategy(threshold: 2);
        using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 2,
            segmentFolder: _testDirectory,
            segmentPrefix: "seg",
            entrySerializer: serializer,
            compactionThreshold: 0, // disabled
            compactionStrategy: strategy); // enabled at threshold 2

        // Act: write enough to create 2+ segments
        for (var i = 1; i <= 5; i++)
        {
            await engine.SetAsync(new SerializableWrapper<int>(i), new SerializableWrapper<string>($"val{i}"));
        }

        // Assert: strategy should have triggered despite compactionThreshold=0
        engine.SegmentCount.Should().BeLessThan(2, "strategy should override disabled threshold");
    }

    /// <summary>
    /// Test that without a compaction strategy, the legacy compactionThreshold still works.
    /// </summary>
    [Fact]
    public async Task WithoutCompactionStrategy_LegacyThreshold_ShouldStillWork()
    {
        // Arrange: compactionThreshold=3, no strategy
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 2,
            segmentFolder: _testDirectory,
            segmentPrefix: "seg",
            entrySerializer: serializer,
            compactionThreshold: 3);

        // Act
        for (var i = 1; i <= 7; i++)
        {
            await engine.SetAsync(new SerializableWrapper<int>(i), new SerializableWrapper<string>($"val{i}"));
        }

        // Assert: legacy auto-compaction should still trigger
        engine.SegmentCount.Should().BeLessThan(3);
    }

    /// <summary>
    /// Clean up the test directory after each test.
    /// </summary>
    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }
}
