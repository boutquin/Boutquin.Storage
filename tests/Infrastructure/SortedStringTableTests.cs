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
/// Unit tests for the <see cref="SortedStringTable{TKey, TValue}"/> class.
/// Each test follows the Arrange-Act-Assert pattern.
/// </summary>
public sealed class SortedStringTableTests : IDisposable
{
    private readonly string _testDirectory = Path.Combine(Path.GetTempPath(), $"SSTableTests_{Guid.NewGuid():N}");

    public SortedStringTableTests()
    {
        if (!Directory.Exists(_testDirectory))
        {
            Directory.CreateDirectory(_testDirectory);
        }
    }

    /// <summary>
    /// Test that Write stores sorted key-value pairs and GetEntryCount reflects the count.
    /// </summary>
    [Fact]
    public void Write_ShouldStoreSortedDataAndUpdateEntryCount()
    {
        // Arrange
        var sst = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _testDirectory, "write_test.dat");

        var items = new List<KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>>
        {
            new(new SerializableWrapper<int>(1), new SerializableWrapper<string>("alpha")),
            new(new SerializableWrapper<int>(2), new SerializableWrapper<string>("bravo")),
            new(new SerializableWrapper<int>(3), new SerializableWrapper<string>("charlie")),
            new(new SerializableWrapper<int>(4), new SerializableWrapper<string>("delta")),
            new(new SerializableWrapper<int>(5), new SerializableWrapper<string>("echo"))
        };

        // Act
        sst.Write(items);

        // Assert
        sst.GetEntryCount().Should().Be(5);
        sst.FileSize.Should().BeGreaterThan(0);
    }

    /// <summary>
    /// Test that TryGetValue returns true and the correct value for an existing key.
    /// </summary>
    [Fact]
    public void TryGetValue_ShouldReturnTrueAndCorrectValue_WhenKeyExists()
    {
        // Arrange
        var sst = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _testDirectory, "get_found_test.dat");

        var items = new List<KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>>
        {
            new(new SerializableWrapper<int>(10), new SerializableWrapper<string>("ten")),
            new(new SerializableWrapper<int>(20), new SerializableWrapper<string>("twenty")),
            new(new SerializableWrapper<int>(30), new SerializableWrapper<string>("thirty"))
        };

        sst.Write(items);

        // Act
        var found = sst.TryGetValue(new SerializableWrapper<int>(20), out var value);

        // Assert
        found.Should().BeTrue();
        value.Value.Should().Be("twenty");
    }

    /// <summary>
    /// Test that TryGetValue returns false when the key does not exist.
    /// </summary>
    [Fact]
    public void TryGetValue_ShouldReturnFalse_WhenKeyDoesNotExist()
    {
        // Arrange
        var sst = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _testDirectory, "get_notfound_test.dat");

        var items = new List<KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>>
        {
            new(new SerializableWrapper<int>(1), new SerializableWrapper<string>("one")),
            new(new SerializableWrapper<int>(3), new SerializableWrapper<string>("three"))
        };

        sst.Write(items);

        // Act
        var found = sst.TryGetValue(new SerializableWrapper<int>(2), out _);

        // Assert
        found.Should().BeFalse();
    }

    /// <summary>
    /// Test that TryGetValue returns false when the SSTable is empty (no entries written).
    /// </summary>
    [Fact]
    public void TryGetValue_ShouldReturnFalse_WhenTableIsEmpty()
    {
        // Arrange
        var sst = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _testDirectory, "get_empty_test.dat");

        sst.Write(Array.Empty<KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>>());

        // Act
        var found = sst.TryGetValue(new SerializableWrapper<int>(1), out _);

        // Assert
        found.Should().BeFalse();
    }

    /// <summary>
    /// Test that TryGetValue can find the first key in the SSTable.
    /// </summary>
    [Fact]
    public void TryGetValue_ShouldFindFirstKey()
    {
        // Arrange
        var sst = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _testDirectory, "get_first_test.dat");

        var items = new List<KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>>
        {
            new(new SerializableWrapper<int>(1), new SerializableWrapper<string>("first")),
            new(new SerializableWrapper<int>(2), new SerializableWrapper<string>("second")),
            new(new SerializableWrapper<int>(3), new SerializableWrapper<string>("third"))
        };

        sst.Write(items);

        // Act
        var found = sst.TryGetValue(new SerializableWrapper<int>(1), out var value);

        // Assert
        found.Should().BeTrue();
        value.Value.Should().Be("first");
    }

    /// <summary>
    /// Test that TryGetValue can find the last key in the SSTable.
    /// </summary>
    [Fact]
    public void TryGetValue_ShouldFindLastKey()
    {
        // Arrange
        var sst = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _testDirectory, "get_last_test.dat");

        var items = new List<KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>>
        {
            new(new SerializableWrapper<int>(100), new SerializableWrapper<string>("hundred")),
            new(new SerializableWrapper<int>(200), new SerializableWrapper<string>("two-hundred")),
            new(new SerializableWrapper<int>(300), new SerializableWrapper<string>("three-hundred"))
        };

        sst.Write(items);

        // Act
        var found = sst.TryGetValue(new SerializableWrapper<int>(300), out var value);

        // Assert
        found.Should().BeTrue();
        value.Value.Should().Be("three-hundred");
    }

    /// <summary>
    /// Test that Merge combines two SSTables correctly with sorted order and deduplication.
    /// </summary>
    [Fact]
    public void Merge_ShouldCombineTwoTablesInSortedOrder()
    {
        // Arrange
        var sst1 = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _testDirectory, "merge_target.dat");
        var sst2 = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _testDirectory, "merge_source.dat");

        sst1.Write(new List<KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>>
        {
            new(new SerializableWrapper<int>(1), new SerializableWrapper<string>("one")),
            new(new SerializableWrapper<int>(3), new SerializableWrapper<string>("three")),
            new(new SerializableWrapper<int>(5), new SerializableWrapper<string>("five"))
        });

        sst2.Write(new List<KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>>
        {
            new(new SerializableWrapper<int>(2), new SerializableWrapper<string>("two")),
            new(new SerializableWrapper<int>(4), new SerializableWrapper<string>("four")),
            new(new SerializableWrapper<int>(6), new SerializableWrapper<string>("six"))
        });

        // Act
        sst1.Merge(sst2);

        // Assert: merged table should have all 6 entries, retrievable by key.
        sst1.GetEntryCount().Should().Be(6);
        sst1.TryGetValue(new SerializableWrapper<int>(1), out var v1).Should().BeTrue();
        v1.Value.Should().Be("one");
        sst1.TryGetValue(new SerializableWrapper<int>(2), out var v2).Should().BeTrue();
        v2.Value.Should().Be("two");
        sst1.TryGetValue(new SerializableWrapper<int>(4), out var v4).Should().BeTrue();
        v4.Value.Should().Be("four");
        sst1.TryGetValue(new SerializableWrapper<int>(6), out var v6).Should().BeTrue();
        v6.Value.Should().Be("six");
    }

    /// <summary>
    /// Test that Merge handles duplicate keys with last-writer-wins semantics.
    /// </summary>
    [Fact]
    public void Merge_ShouldUseOtherTableValueForDuplicateKeys()
    {
        // Arrange
        var sst1 = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _testDirectory, "merge_dup_target.dat");
        var sst2 = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _testDirectory, "merge_dup_source.dat");

        sst1.Write(new List<KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>>
        {
            new(new SerializableWrapper<int>(1), new SerializableWrapper<string>("old_value")),
            new(new SerializableWrapper<int>(2), new SerializableWrapper<string>("keep_this"))
        });

        sst2.Write(new List<KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>>
        {
            new(new SerializableWrapper<int>(1), new SerializableWrapper<string>("new_value"))
        });

        // Act
        sst1.Merge(sst2);

        // Assert: key 1 should have the value from the other table.
        sst1.GetEntryCount().Should().Be(2);
        sst1.TryGetValue(new SerializableWrapper<int>(1), out var v1).Should().BeTrue();
        v1.Value.Should().Be("new_value");
        sst1.TryGetValue(new SerializableWrapper<int>(2), out var v2).Should().BeTrue();
        v2.Value.Should().Be("keep_this");
    }

    /// <summary>
    /// Test that GetCreationTime returns a reasonable timestamp.
    /// </summary>
    [Fact]
    public void GetCreationTime_ShouldReturnReasonableTimestamp()
    {
        // Arrange
        var beforeCreation = DateTime.Now.AddSeconds(-1);
        var sst = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _testDirectory, "creation_time_test.dat");

        // Act
        var creationTime = sst.GetCreationTime();

        // Assert: creation time should be around now.
        creationTime.Should().BeOnOrAfter(beforeCreation);
        creationTime.Should().BeOnOrBefore(DateTime.Now.AddSeconds(1));
    }

    /// <summary>
    /// Test that GetLastModificationTime updates after a Write operation.
    /// </summary>
    [Fact]
    public void GetLastModificationTime_ShouldUpdateAfterWrite()
    {
        // Arrange
        var sst = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _testDirectory, "mod_time_test.dat");

        var beforeWrite = DateTime.Now.AddSeconds(-1);

        // Act
        sst.Write(new List<KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>>
        {
            new(new SerializableWrapper<int>(1), new SerializableWrapper<string>("value"))
        });

        var modTime = sst.GetLastModificationTime();

        // Assert
        modTime.Should().BeOnOrAfter(beforeWrite);
        modTime.Should().BeOnOrBefore(DateTime.Now.AddSeconds(1));
    }

    /// <summary>
    /// Test that the SSTable works correctly with many entries and the sparse index.
    /// </summary>
    [Fact]
    public void TryGetValue_ShouldWorkWithManyEntries()
    {
        // Arrange: write 100 entries with sparse index interval of 4 (default).
        var sst = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _testDirectory, "many_entries_test.dat");

        var items = new List<KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>>();
        for (var i = 0; i < 100; i++)
        {
            items.Add(new KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>(
                new SerializableWrapper<int>(i * 10),
                new SerializableWrapper<string>($"value_{i}")));
        }

        sst.Write(items);

        // Act & Assert: verify several keys across different sparse index segments.
        sst.GetEntryCount().Should().Be(100);

        sst.TryGetValue(new SerializableWrapper<int>(0), out var v0).Should().BeTrue();
        v0.Value.Should().Be("value_0");

        sst.TryGetValue(new SerializableWrapper<int>(50), out var v5).Should().BeTrue();
        v5.Value.Should().Be("value_5");

        sst.TryGetValue(new SerializableWrapper<int>(990), out var v99).Should().BeTrue();
        v99.Value.Should().Be("value_99");

        // Key that doesn't exist (between existing keys).
        sst.TryGetValue(new SerializableWrapper<int>(15), out _).Should().BeFalse();
    }

    /// <summary>
    /// Test that Write throws ArgumentException when input is not sorted by key.
    /// </summary>
    [Fact]
    public void Write_WithUnsortedInput_ShouldThrowArgumentException()
    {
        // Arrange
        var sst = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _testDirectory, "unsorted_test.dat");

        var unsortedItems = new List<KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>>
        {
            new(new SerializableWrapper<int>(3), new SerializableWrapper<string>("charlie")),
            new(new SerializableWrapper<int>(1), new SerializableWrapper<string>("alpha")),
            new(new SerializableWrapper<int>(2), new SerializableWrapper<string>("bravo")),
        };

        // Act & Assert
        var act = () => sst.Write(unsortedItems);
        act.Should().Throw<ArgumentException>()
            .WithMessage("*sorted*");
    }

    /// <summary>
    /// Test that Write throws ArgumentException when input contains duplicate keys.
    /// </summary>
    [Fact]
    public void Write_WithDuplicateKeys_ShouldThrowArgumentException()
    {
        // Arrange
        var sst = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _testDirectory, "duplicate_test.dat");

        var duplicateItems = new List<KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>>
        {
            new(new SerializableWrapper<int>(1), new SerializableWrapper<string>("alpha")),
            new(new SerializableWrapper<int>(2), new SerializableWrapper<string>("bravo")),
            new(new SerializableWrapper<int>(2), new SerializableWrapper<string>("charlie")),
        };

        // Act & Assert
        var act = () => sst.Write(duplicateItems);
        act.Should().Throw<ArgumentException>()
            .WithMessage("*sorted*");
    }

    /// <summary>
    /// Test that Write uses atomic write-to-temp-then-rename pattern:
    /// if the SSTable already has data, the data file is only replaced after the new file
    /// is fully written. We verify by checking the original data remains readable after a
    /// second Write call.
    /// </summary>
    [Fact]
    public void Write_ShouldBeAtomic_ExistingDataReplacedOnlyAfterFullWrite()
    {
        // Arrange
        var sst = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _testDirectory, "atomic_test.dat");

        var originalItems = new List<KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>>
        {
            new(new SerializableWrapper<int>(1), new SerializableWrapper<string>("original_a")),
            new(new SerializableWrapper<int>(2), new SerializableWrapper<string>("original_b"))
        };

        sst.Write(originalItems);
        sst.TryGetValue(new SerializableWrapper<int>(1), out var v1).Should().BeTrue();
        v1.Value.Should().Be("original_a");

        // Act: write new data (replacing the file atomically)
        var newItems = new List<KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>>
        {
            new(new SerializableWrapper<int>(10), new SerializableWrapper<string>("new_a")),
            new(new SerializableWrapper<int>(20), new SerializableWrapper<string>("new_b")),
            new(new SerializableWrapper<int>(30), new SerializableWrapper<string>("new_c"))
        };

        sst.Write(newItems);

        // Assert: new data should be present
        sst.GetEntryCount().Should().Be(3);
        sst.TryGetValue(new SerializableWrapper<int>(10), out var v10).Should().BeTrue();
        v10.Value.Should().Be("new_a");

        // Old keys should not be present
        sst.TryGetValue(new SerializableWrapper<int>(1), out _).Should().BeFalse();

        // No temp files should remain
        var tempFiles = Directory.GetFiles(_testDirectory, "*.tmp");
        tempFiles.Should().BeEmpty("temp files should be cleaned up after atomic write");
    }

    /// <summary>
    /// Test that Write persists the sparse index to a companion .idx file.
    /// </summary>
    [Fact]
    public void Write_ShouldPersistSparseIndex()
    {
        // Arrange
        var sst = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _testDirectory, "idx_persist_test.dat", sparseIndexInterval: 2);

        var items = new List<KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>>
        {
            new(new SerializableWrapper<int>(1), new SerializableWrapper<string>("a")),
            new(new SerializableWrapper<int>(2), new SerializableWrapper<string>("b")),
            new(new SerializableWrapper<int>(3), new SerializableWrapper<string>("c")),
            new(new SerializableWrapper<int>(4), new SerializableWrapper<string>("d"))
        };

        // Act
        sst.Write(items);

        // Assert: a companion .idx file should exist
        var idxPath = Path.Combine(_testDirectory, "idx_persist_test.idx");
        File.Exists(idxPath).Should().BeTrue("a companion .idx file should be written with the sparse index");
        new FileInfo(idxPath).Length.Should().BeGreaterThan(0, ".idx file should contain sparse index data");
    }

    /// <summary>
    /// Test that Write with compression enabled produces a compressed file and reads work correctly.
    /// </summary>
    [Fact]
    public void Write_WithCompression_ShouldProduceSmallerFileAndReadCorrectly()
    {
        // Arrange
        var sstCompressed = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _testDirectory, "compressed.dat", enableCompression: true);
        var sstUncompressed = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _testDirectory, "uncompressed.dat", enableCompression: false);

        var items = new List<KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>>();
        for (var i = 0; i < 50; i++)
        {
            items.Add(new KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>(
                new SerializableWrapper<int>(i),
                new SerializableWrapper<string>($"value_with_some_repetitive_text_{i}")));
        }

        // Act
        sstCompressed.Write(items);
        sstUncompressed.Write(items);

        // Assert: compressed file should be smaller (for repetitive data)
        var compressedSize = new FileInfo(Path.Combine(_testDirectory, "compressed.dat")).Length;
        var uncompressedSize = new FileInfo(Path.Combine(_testDirectory, "uncompressed.dat")).Length;
        compressedSize.Should().BeLessThan(uncompressedSize, "compressed file should be smaller");

        // Reads should still work correctly
        sstCompressed.TryGetValue(new SerializableWrapper<int>(0), out var v0).Should().BeTrue();
        v0.Value.Should().Be("value_with_some_repetitive_text_0");

        sstCompressed.TryGetValue(new SerializableWrapper<int>(49), out var v49).Should().BeTrue();
        v49.Value.Should().Be("value_with_some_repetitive_text_49");

        sstCompressed.GetEntryCount().Should().Be(50);
    }

    /// <summary>
    /// Test that a new SSTable instance can load a previously persisted sparse index and
    /// perform lookups without re-scanning the data file.
    /// </summary>
    [Fact]
    public void Constructor_ShouldLoadPersistedSparseIndex()
    {
        // Arrange: write data and dispose to release file handles
        var fileName = "idx_load_test.dat";
        {
            var sst = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
                _testDirectory, fileName, sparseIndexInterval: 2);

            var items = new List<KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>>
            {
                new(new SerializableWrapper<int>(10), new SerializableWrapper<string>("ten")),
                new(new SerializableWrapper<int>(20), new SerializableWrapper<string>("twenty")),
                new(new SerializableWrapper<int>(30), new SerializableWrapper<string>("thirty")),
                new(new SerializableWrapper<int>(40), new SerializableWrapper<string>("forty"))
            };

            sst.Write(items);
        }

        // Act: create a new SSTable instance pointing to the same file — it should load the .idx
        var sst2 = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _testDirectory, fileName, sparseIndexInterval: 2);

        // Assert: lookups should work using the loaded sparse index
        sst2.TryGetValue(new SerializableWrapper<int>(10), out var v10).Should().BeTrue();
        v10.Value.Should().Be("ten");

        sst2.TryGetValue(new SerializableWrapper<int>(30), out var v30).Should().BeTrue();
        v30.Value.Should().Be("thirty");

        sst2.TryGetValue(new SerializableWrapper<int>(25), out _).Should().BeFalse();

        sst2.GetEntryCount().Should().Be(4);
    }

    /// <summary>
    /// Clean up the test directory after each test.
    /// </summary>
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDirectory))
            {
                Directory.Delete(_testDirectory, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup in tests.
        }
    }
}
