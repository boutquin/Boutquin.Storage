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
/// Unit tests for the <see cref="WriteAheadLog{TKey, TValue}"/> class.
/// Each test follows the Arrange-Act-Assert pattern.
/// </summary>
public sealed class WriteAheadLogTests : IDisposable
{
    private readonly string _testDirectory = Path.Combine(Path.GetTempPath(), $"WriteAheadLogTests_{Guid.NewGuid():N}");
    private string WriteAheadLogFilePath => Path.Combine(_testDirectory, "test.wal");

    public WriteAheadLogTests()
    {
        Directory.CreateDirectory(_testDirectory);
    }

    /// <summary>
    /// Test to ensure that AppendAsync writes entries that can be recovered.
    /// </summary>
    [Fact]
    public async Task AppendAsync_ShouldWriteEntries()
    {
        // Arrange
        using var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(WriteAheadLogFilePath);
        var key = new SerializableWrapper<int>(1);
        var value = new SerializableWrapper<string>("value1");

        // Act
        await wal.AppendAsync(key, value);

        // Assert: The WriteAheadLog file should exist and have content.
        File.Exists(WriteAheadLogFilePath).Should().BeTrue();
        new FileInfo(WriteAheadLogFilePath).Length.Should().BeGreaterThan(0);
    }

    /// <summary>
    /// Test to ensure that RecoverAsync reads back all appended entries.
    /// </summary>
    [Fact]
    public async Task RecoverAsync_ShouldReadBackAllAppendedEntries()
    {
        // Arrange
        using var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(WriteAheadLogFilePath);
        await wal.AppendAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("value1"));
        await wal.AppendAsync(new SerializableWrapper<int>(2), new SerializableWrapper<string>("value2"));
        await wal.AppendAsync(new SerializableWrapper<int>(3), new SerializableWrapper<string>("value3"));

        // Act
        var entries = await wal.RecoverAsync();

        // Assert
        entries.Should().HaveCount(3);
        entries[0].Key.Value.Should().Be(1);
        entries[0].Value.Value.Should().Be("value1");
        entries[1].Key.Value.Should().Be(2);
        entries[1].Value.Value.Should().Be("value2");
        entries[2].Key.Value.Should().Be(3);
        entries[2].Value.Value.Should().Be("value3");
    }

    /// <summary>
    /// Test to ensure that RecoverAsync skips corrupted entries by truncating the file mid-entry.
    /// </summary>
    [Fact]
    public async Task RecoverAsync_ShouldSkipCorruptedEntries()
    {
        // Arrange: Write two valid entries, then corrupt the file by truncating mid-entry.
        // Dispose the WriteAheadLog to release the persistent append stream before file manipulation.
        long fullSize;
        using (var walWriter = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(WriteAheadLogFilePath))
        {
            await walWriter.AppendAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("value1"));
            await walWriter.AppendAsync(new SerializableWrapper<int>(2), new SerializableWrapper<string>("value2"));

            fullSize = new FileInfo(WriteAheadLogFilePath).Length;

            await walWriter.AppendAsync(new SerializableWrapper<int>(3), new SerializableWrapper<string>("value3"));
        }

        // Truncate the file to remove part of the third entry (corrupt it).
        using (var fs = new FileStream(WriteAheadLogFilePath, FileMode.Open, FileAccess.Write))
        {
            fs.SetLength(fullSize + 5);
        }

        // Act: Create a new WriteAheadLog instance for recovery.
        using var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(WriteAheadLogFilePath);
        var entries = await wal.RecoverAsync();

        // Assert: Only the two valid entries should be recovered.
        entries.Should().HaveCount(2);
        entries[0].Key.Value.Should().Be(1);
        entries[1].Key.Value.Should().Be(2);
    }

    /// <summary>
    /// Test to ensure that RecoverAsync detects checksum corruption.
    /// </summary>
    [Fact]
    public async Task RecoverAsync_ShouldDetectChecksumCorruption()
    {
        // Arrange: Write one valid entry, then corrupt a byte in the payload.
        // Dispose the WriteAheadLog to release the persistent append stream before file manipulation.
        using (var walWriter = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(WriteAheadLogFilePath))
        {
            await walWriter.AppendAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("value1"));
        }

        // Corrupt a byte in the payload area (after the 4-byte length prefix).
        var fileBytes = await File.ReadAllBytesAsync(WriteAheadLogFilePath);
        fileBytes[5] ^= 0xFF; // Flip bits in a payload byte.
        await File.WriteAllBytesAsync(WriteAheadLogFilePath, fileBytes);

        // Act: Create a new WriteAheadLog instance for recovery.
        using var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(WriteAheadLogFilePath);
        var entries = await wal.RecoverAsync();

        // Assert: The corrupted entry should be skipped.
        entries.Should().BeEmpty();
    }

    /// <summary>
    /// Test to ensure that TruncateAsync clears the WriteAheadLog file.
    /// </summary>
    [Fact]
    public async Task TruncateAsync_ShouldClearTheLog()
    {
        // Arrange
        using var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(WriteAheadLogFilePath);
        await wal.AppendAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("value1"));
        await wal.AppendAsync(new SerializableWrapper<int>(2), new SerializableWrapper<string>("value2"));

        // Act
        await wal.TruncateAsync();

        // Assert: Recovery should return an empty list.
        var entries = await wal.RecoverAsync();
        entries.Should().BeEmpty();
        new FileInfo(WriteAheadLogFilePath).Length.Should().Be(0);
    }

    /// <summary>
    /// Test to ensure that multiple append + recover round-trips work correctly.
    /// </summary>
    [Fact]
    public async Task MultipleRoundTrips_ShouldWorkCorrectly()
    {
        // Arrange & Act: First round-trip.
        using var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(WriteAheadLogFilePath);
        await wal.AppendAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("first"));

        var entries1 = await wal.RecoverAsync();
        entries1.Should().HaveCount(1);
        entries1[0].Value.Value.Should().Be("first");

        // Truncate and start a second round-trip.
        await wal.TruncateAsync();

        await wal.AppendAsync(new SerializableWrapper<int>(10), new SerializableWrapper<string>("second_a"));
        await wal.AppendAsync(new SerializableWrapper<int>(20), new SerializableWrapper<string>("second_b"));

        var entries2 = await wal.RecoverAsync();
        entries2.Should().HaveCount(2);
        entries2[0].Key.Value.Should().Be(10);
        entries2[0].Value.Value.Should().Be("second_a");
        entries2[1].Key.Value.Should().Be(20);
        entries2[1].Value.Value.Should().Be("second_b");

        // Truncate again and verify empty.
        await wal.TruncateAsync();
        var entries3 = await wal.RecoverAsync();
        entries3.Should().BeEmpty();
    }

    /// <summary>
    /// Test to ensure that RecoverAsync returns an empty list when the WriteAheadLog file does not exist.
    /// </summary>
    [Fact]
    public async Task RecoverAsync_ShouldReturnEmptyWhenFileDoesNotExist()
    {
        // Arrange: Use a path that doesn't have a WriteAheadLog file yet.
        var nonExistentPath = Path.Combine(_testDirectory, "nonexistent.wal");
        using var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(nonExistentPath);

        // Act
        var entries = await wal.RecoverAsync();

        // Assert
        entries.Should().BeEmpty();
    }

    /// <summary>
    /// Test to ensure that the constructor throws when given a null or empty file path.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_ShouldThrowOnNullOrEmptyPath(string filePath)
    {
        // Act & Assert
        var act = () => new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(filePath);
        act.Should().Throw<ArgumentNullException>();
    }

    /// <summary>
    /// Verifies that AppendAsync throws ObjectDisposedException after the WriteAheadLog has been disposed.
    /// This validates the thread-safe dispose pattern using Interlocked.Exchange.
    /// </summary>
    [Fact]
    public async Task AppendAsync_AfterDispose_ShouldThrowObjectDisposedException()
    {
        // Arrange
        var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(WriteAheadLogFilePath);
        wal.Dispose();

        // Act & Assert
        var act = () => wal.AppendAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("value"));
        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    /// <summary>
    /// Verifies that RecoverAsync throws ObjectDisposedException after the WriteAheadLog has been disposed.
    /// </summary>
    [Fact]
    public async Task RecoverAsync_AfterDispose_ShouldThrowObjectDisposedException()
    {
        // Arrange
        var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(WriteAheadLogFilePath);
        wal.Dispose();

        // Act & Assert
        var act = () => wal.RecoverAsync();
        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    /// <summary>
    /// Verifies that TruncateAsync throws ObjectDisposedException after the WriteAheadLog has been disposed.
    /// </summary>
    [Fact]
    public async Task TruncateAsync_AfterDispose_ShouldThrowObjectDisposedException()
    {
        // Arrange
        var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(WriteAheadLogFilePath);
        wal.Dispose();

        // Act & Assert
        var act = () => wal.TruncateAsync();
        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    /// <summary>
    /// Verifies that recovery skips a corrupt entry (checksum mismatch) and recovers
    /// subsequent valid entries. This validates the "continue past corruption" semantics:
    /// the length-prefix format allows the reader to advance past a corrupt entry because
    /// the payload bytes are consumed before the checksum is validated.
    /// </summary>
    [Fact]
    public async Task RecoverAsync_ShouldSkipCorruptEntryAndRecoverSubsequentValidEntries()
    {
        // Arrange: Write 3 entries, then dispose the WriteAheadLog so the file is not locked.
        var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(WriteAheadLogFilePath);
        await wal.AppendAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("first"));
        await wal.AppendAsync(new SerializableWrapper<int>(2), new SerializableWrapper<string>("second"));
        await wal.AppendAsync(new SerializableWrapper<int>(3), new SerializableWrapper<string>("third"));
        wal.Dispose();

        // Read the raw file bytes to locate and corrupt the second entry's payload.
        // Entry format: [4 bytes length][N bytes payload][4 bytes CRC32]
        var fileBytes = await File.ReadAllBytesAsync(WriteAheadLogFilePath);

        // Parse the first entry to find where the second entry starts.
        var firstPayloadLen = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(
            fileBytes.AsSpan(0, 4));
        var secondEntryStart = 4 + firstPayloadLen + 4; // length + payload + checksum

        // Corrupt a byte in the second entry's payload (after its 4-byte length prefix).
        // This will cause a checksum mismatch but won't break the length framing,
        // so the reader can still advance past it and find the third entry.
        fileBytes[secondEntryStart + 5] ^= 0xFF;
        await File.WriteAllBytesAsync(WriteAheadLogFilePath, fileBytes);

        // Act: Create a new WriteAheadLog instance for recovery.
        using var recoveryWal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(WriteAheadLogFilePath);
        var entries = await recoveryWal.RecoverAsync();

        // Assert: entries 1 and 3 should be recovered, entry 2 skipped.
        entries.Should().HaveCount(2);
        entries[0].Key.Value.Should().Be(1);
        entries[0].Value.Value.Should().Be("first");
        entries[1].Key.Value.Should().Be(3);
        entries[1].Value.Value.Should().Be("third");
    }

    /// <summary>
    /// Clean up the temporary test directory after each test.
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
            // Best-effort cleanup.
        }
    }
}
