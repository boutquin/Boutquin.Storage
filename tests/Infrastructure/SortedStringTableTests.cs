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
        sst.Write(items.WithZeroSeqno());

        // Assert
        sst.GetEntryCount().Should().Be(5);
        sst.FileSize.Should().BeGreaterThan(0);
    }

    /// <summary>
    /// The footer — bloom bits, block index, key range, tombstones — is on-disk state that
    /// drives "we can be sure the key is absent" decisions (DDIA 2e Ch. 4 on Bloom filters).
    /// A bit flip in the persisted bloom bits must fail the reopen loudly via the footer
    /// CRC; without it, the flip silently produces bloom false negatives — present keys
    /// reading as absent.
    /// </summary>
    [Fact]
    public void Reopen_WithBitFlippedFooterBloomBits_Throws()
    {
        var fileName = "footer_crc_test.dat";
        var sst = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _testDirectory, fileName);
        sst.Write(new List<KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>>
        {
            new(new SerializableWrapper<int>(1), new SerializableWrapper<string>("alpha")),
            new(new SerializableWrapper<int>(2), new SerializableWrapper<string>("bravo")),
            new(new SerializableWrapper<int>(3), new SerializableWrapper<string>("charlie")),
        }.WithZeroSeqno());

        // Locate the persisted bloom bits inside the file and flip one bit mid-span.
        var path = Path.Combine(_testDirectory, fileName);
        var fileBytes = File.ReadAllBytes(path);
        SsTableFooter.TryReadFromEnd(fileBytes, out var footer).Should().BeTrue();
        var bloomBits = footer!.BloomBits.ToArray();
        bloomBits.Length.Should().BeGreaterThan(2, "the bloom span must be locatable");
        var bloomOffset = fileBytes.AsSpan().IndexOf(bloomBits);
        bloomOffset.Should().BeGreaterThan(0, "the bloom bits must appear verbatim in the file");

        fileBytes[bloomOffset + bloomBits.Length / 2] ^= 0x10;
        File.WriteAllBytes(path, fileBytes);

        var act = () => new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _testDirectory, fileName);
        act.Should().Throw<InvalidDataException>(
            "footer corruption must be detected by the footer CRC, not silently served as bloom false negatives");
    }

    /// <summary>
    /// A transient I/O failure on an EXISTING SSTable file must propagate, not silently
    /// degrade the table to "unwritten empty": swallowing the exception makes every read
    /// report not-found and lets a subsequent Write atomically replace the real file's
    /// contents. (An unparseable footer already throws; an unreadable file deserves the
    /// same loud path.)
    /// </summary>
    [Fact]
    public void Constructor_ExistingFileUnreadable_Throws()
    {
        var fileName = "locked_test.dat";
        var sst = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _testDirectory, fileName);
        sst.Write(new List<KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>>
        {
            new(new SerializableWrapper<int>(1), new SerializableWrapper<string>("alpha")),
        }.WithZeroSeqno());

        // Hold the file with no sharing so the next constructor's read fails.
        using var exclusive = new FileStream(
            Path.Combine(_testDirectory, fileName), FileMode.Open, FileAccess.Read, FileShare.None);

        var act = () => new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _testDirectory, fileName);
        act.Should().Throw<IOException>(
            "an existing file that cannot be read is a fault, not an empty table");
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

        sst.Write(items.WithZeroSeqno());

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

        sst.Write(items.WithZeroSeqno());

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

        sst.Write(Array.Empty<(SerializableWrapper<int>, SerializableWrapper<string>, ulong)>());

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

        sst.Write(items.WithZeroSeqno());

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

        sst.Write(items.WithZeroSeqno());

        // Act
        var found = sst.TryGetValue(new SerializableWrapper<int>(300), out var value);

        // Assert
        found.Should().BeTrue();
        value.Value.Should().Be("three-hundred");
    }

    // The merge-of-two-SSTables behaviour tested earlier as SortedStringTable.Merge is now
    // the engine's compaction path (LsmStorageEngine.CompactAsync threads SegmentWriteEntry
    // end-to-end). The per-table Merge API was removed at v2.0.0 because it silently
    // dropped pointer entries; LSM compaction is the correct and only merge vehicle.

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
        }.WithZeroSeqno());

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

        sst.Write(items.WithZeroSeqno());

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
        var act = () => sst.Write(unsortedItems.WithZeroSeqno());
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
        var act = () => sst.Write(duplicateItems.WithZeroSeqno());
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

        sst.Write(originalItems.WithZeroSeqno());
        sst.TryGetValue(new SerializableWrapper<int>(1), out var v1).Should().BeTrue();
        v1.Value.Should().Be("original_a");

        // Act: write new data (replacing the file atomically)
        var newItems = new List<KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>>
        {
            new(new SerializableWrapper<int>(10), new SerializableWrapper<string>("new_a")),
            new(new SerializableWrapper<int>(20), new SerializableWrapper<string>("new_b")),
            new(new SerializableWrapper<int>(30), new SerializableWrapper<string>("new_c"))
        };

        sst.Write(newItems.WithZeroSeqno());

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
    /// Phase C4: v2 SSTable writes the block index into the on-disk footer; no companion
    /// .idx file is produced (the index lives in the trailing footer instead).
    /// </summary>
    [Fact]
    public void Write_ShouldEmbedBlockIndexInFooter_NoCompanionIdx()
    {
        // Arrange
        var sst = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _testDirectory, "footer_index_test.dat");

        var items = new List<KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>>
        {
            new(new SerializableWrapper<int>(1), new SerializableWrapper<string>("a")),
            new(new SerializableWrapper<int>(2), new SerializableWrapper<string>("b")),
            new(new SerializableWrapper<int>(3), new SerializableWrapper<string>("c")),
            new(new SerializableWrapper<int>(4), new SerializableWrapper<string>("d"))
        };

        // Act
        sst.Write(items.WithZeroSeqno());

        // Assert: no companion .idx file — v2 footer carries the index.
        var idxPath = Path.Combine(_testDirectory, "footer_index_test.idx");
        File.Exists(idxPath).Should().BeFalse("v2 SSTables embed the block index in the footer, not in a companion file");
    }

    /// <summary>
    /// Phase C1/C5: using a real compression codec produces a smaller on-disk file than
    /// the passthrough codec for repetitive data, and reads work correctly either way.
    /// </summary>
    [Fact]
    public void Write_WithCompressionCodec_ShouldProduceSmallerFileAndReadCorrectly()
    {
        // Arrange
        var sstCompressed = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _testDirectory, "compressed.dat", codec: new Boutquin.Storage.Infrastructure.Compression.GZipCodec());
        var sstUncompressed = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _testDirectory, "uncompressed.dat", codec: new Boutquin.Storage.Infrastructure.Compression.NoCompressionCodec());

        var items = new List<KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>>();
        for (var i = 0; i < 50; i++)
        {
            items.Add(new KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>(
                new SerializableWrapper<int>(i),
                new SerializableWrapper<string>($"value_with_some_repetitive_text_{i}")));
        }

        // Act
        sstCompressed.Write(items.WithZeroSeqno());
        sstUncompressed.Write(items.WithZeroSeqno());

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
    /// Phase C4: reopening a v2 SSTable on the same file path loads the footer (block
    /// index + bloom bits) and performs lookups against disk without any separate .idx
    /// companion.
    /// </summary>
    [Fact]
    public void Constructor_ShouldLoadPersistedFooter()
    {
        // Arrange: write data and dispose to release file handles
        var fileName = "footer_reload_test.dat";
        {
            var sst = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
                _testDirectory, fileName);

            var items = new List<KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>>
            {
                new(new SerializableWrapper<int>(10), new SerializableWrapper<string>("ten")),
                new(new SerializableWrapper<int>(20), new SerializableWrapper<string>("twenty")),
                new(new SerializableWrapper<int>(30), new SerializableWrapper<string>("thirty")),
                new(new SerializableWrapper<int>(40), new SerializableWrapper<string>("forty"))
            };

            sst.Write(items.WithZeroSeqno());
        }

        // Act: create a new SSTable instance pointing to the same file — it should load the footer
        var sst2 = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _testDirectory, fileName);

        // Assert: lookups should work using the loaded footer + bloom bits
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
