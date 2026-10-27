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

    // Directory holding the rolling WAL segments (D2). The WAL constructor takes a directory
    // path; segments inside are wal_NNNNNN.log.
    private string WriteAheadLogFilePath => _testDirectory;

    // First segment path inside the directory — used by tests that inspect / corrupt raw bytes.
    private string FirstSegmentPath => Path.Combine(_testDirectory, "wal_000001.log");

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
        await wal.AppendAsync(key, value, 1UL);

        // Assert: The first segment file inside the WAL directory should exist and have content.
        File.Exists(FirstSegmentPath).Should().BeTrue();
        new FileInfo(FirstSegmentPath).Length.Should().BeGreaterThan(0);
    }

    /// <summary>
    /// Test to ensure that RecoverAsync reads back all appended entries.
    /// </summary>
    [Fact]
    public async Task RecoverAsync_ShouldReadBackAllAppendedEntries()
    {
        // Arrange
        using var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(WriteAheadLogFilePath);
        await wal.AppendAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("value1"), 1UL);
        await wal.AppendAsync(new SerializableWrapper<int>(2), new SerializableWrapper<string>("value2"), 2UL);
        await wal.AppendAsync(new SerializableWrapper<int>(3), new SerializableWrapper<string>("value3"), 3UL);

        // Act
        var entries = await wal.RecoverAsync();

        // Assert
        entries.Should().HaveCount(3);
        entries[0].Key.Value.Should().Be(1);
        entries[0].InlineValue!.Value.Should().Be("value1");
        entries[0].Kind.Should().Be(EntryKind.Set);
        entries[1].Key.Value.Should().Be(2);
        entries[1].InlineValue!.Value.Should().Be("value2");
        entries[2].Key.Value.Should().Be(3);
        entries[2].InlineValue!.Value.Should().Be("value3");
    }

    /// <summary>
    /// An incomplete final header is a torn append; recovery retains the complete prefix.
    /// </summary>
    [Fact]
    public async Task RecoverAsync_IncompleteFinalHeader_RecoversCompletePrefix()
    {
        // Write two complete entries followed by an incomplete final frame header.
        // Dispose the WriteAheadLog to release the persistent append stream before file manipulation.
        long fullSize;
        using (var walWriter = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(WriteAheadLogFilePath))
        {
            await walWriter.AppendAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("value1"), 1UL);
            await walWriter.AppendAsync(new SerializableWrapper<int>(2), new SerializableWrapper<string>("value2"), 2UL);

            fullSize = new FileInfo(FirstSegmentPath).Length;

            await walWriter.AppendAsync(new SerializableWrapper<int>(3), new SerializableWrapper<string>("value3"), 3UL);
        }

        // Keep only a header prefix of the final append.
        using (var fs = new FileStream(FirstSegmentPath, FileMode.Open, FileAccess.Write))
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
    /// A torn tail must be REPAIRED by recovery, not merely skipped: if the partial frame's
    /// garbage bytes are left in place, post-recovery appends land after them at physical
    /// EOF, and the NEXT recovery — stopping at the first unreadable frame — silently drops
    /// every acknowledged, fsync'd entry written after the repair point (DDIA 2e Ch. 4:
    /// checksums let the log discard the corrupted/incomplete entries — the torn entry,
    /// never later valid ones).
    /// </summary>
    [Fact]
    public async Task RecoverAsync_AfterTornTail_RepairsSoLaterAppendsSurviveNextRecovery()
    {
        // Arrange: three entries, then cut the file mid-way through the third frame.
        long twoEntriesSize;
        using (var walWriter = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(WriteAheadLogFilePath))
        {
            await walWriter.AppendAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("value1"), 1UL);
            await walWriter.AppendAsync(new SerializableWrapper<int>(2), new SerializableWrapper<string>("value2"), 2UL);
            twoEntriesSize = new FileInfo(FirstSegmentPath).Length;
            await walWriter.AppendAsync(new SerializableWrapper<int>(3), new SerializableWrapper<string>("value3"), 3UL);
        }

        using (var fs = new FileStream(FirstSegmentPath, FileMode.Open, FileAccess.Write))
        {
            fs.SetLength(twoEntriesSize + 5);
        }

        // Act: recover (drops the torn third entry), then append two acknowledged entries.
        using (var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(WriteAheadLogFilePath))
        {
            var recovered = await wal.RecoverAsync();
            recovered.Should().HaveCount(2, "the torn third frame is tail damage and is discarded");

            await wal.AppendAsync(new SerializableWrapper<int>(4), new SerializableWrapper<string>("value4"), 4UL);
            await wal.AppendAsync(new SerializableWrapper<int>(5), new SerializableWrapper<string>("value5"), 5UL);
        }

        // Assert: the next recovery must return the surviving prefix PLUS the post-repair appends.
        using var wal2 = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(WriteAheadLogFilePath);
        var final = await wal2.RecoverAsync();
        final.Select(e => e.Key.Value).Should().Equal([1, 2, 4, 5],
            "entries 4 and 5 were acknowledged with fsync after recovery repaired the torn tail; " +
            "losing them on the next recovery is acked-write loss");
    }

    /// <summary>
    /// A standalone consumer that appends WITHOUT first calling <see cref="WriteAheadLog{TKey,TValue}.RecoverAsync"/>
    /// must still be safe against a torn tail left by a prior crash. When the WAL opens a
    /// pre-existing segment for append the first time, it repairs the torn tail (scans to the
    /// last clean frame boundary and truncates there) so the new frame lands at a clean
    /// boundary. Without the repair, the append lands after the garbage bytes at physical EOF,
    /// and the NEXT recovery — which stops at the first unreadable frame — silently drops the
    /// freshly-acknowledged append along with the garbage.
    /// </summary>
    [Fact]
    public async Task AppendWithoutRecover_AfterTornTail_RepairsSoAppendAndPriorEntriesSurvive()
    {
        // Arrange: two clean entries, then a third frame torn mid-way. No RecoverAsync runs
        // anywhere in this lifecycle except the final assertion read.
        long twoEntriesSize;
        using (var walWriter = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(WriteAheadLogFilePath))
        {
            await walWriter.AppendAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("value1"), 1UL);
            await walWriter.AppendAsync(new SerializableWrapper<int>(2), new SerializableWrapper<string>("value2"), 2UL);
            twoEntriesSize = new FileInfo(FirstSegmentPath).Length;
            await walWriter.AppendAsync(new SerializableWrapper<int>(3), new SerializableWrapper<string>("value3"), 3UL);
        }

        using (var fs = new FileStream(FirstSegmentPath, FileMode.Open, FileAccess.Write))
        {
            fs.SetLength(twoEntriesSize + 5); // leave 5 garbage bytes of the third frame
        }

        // Act: fresh WAL, append directly (NO RecoverAsync). The first open of the pre-existing
        // segment must repair the torn tail before the append is written.
        using (var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(WriteAheadLogFilePath))
        {
            await wal.AppendAsync(new SerializableWrapper<int>(4), new SerializableWrapper<string>("value4"), 4UL);
        }

        // Assert: recovery returns the surviving prefix [1, 2] plus the new append [4]. The torn
        // third frame is gone. Before the fix, recovery returns only [1, 2] (the append landed
        // after the garbage and is unreachable past the first unreadable frame).
        using var wal2 = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(WriteAheadLogFilePath);
        var final = await wal2.RecoverAsync();
        final.Select(e => e.Key.Value).Should().Equal([1, 2, 4],
            "the append-side torn-tail repair must let the freshly-acknowledged entry 4 survive " +
            "the next recovery even though RecoverAsync never ran before the append");
    }

    /// <summary>
    /// Test to ensure that RecoverAsync detects checksum corruption. The CRC scope covers
    /// <c>[entry_kind][value_kind][seqno][payload]</c>, so any bit-flip in those bytes
    /// surfaces as a CRC mismatch. The only frame was acknowledged after its fsync and every one
    /// of its bytes landed, so the mismatch is damage to acknowledged history: recovery throws
    /// and leaves the segment untouched rather than dropping the write.
    /// </summary>
    [Fact]
    public async Task RecoverAsync_ShouldDetectChecksumCorruption()
    {
        // Arrange: Write one valid entry, then corrupt a byte in the payload.
        // Dispose the WriteAheadLog to release the persistent append stream before file manipulation.
        using (var walWriter = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(WriteAheadLogFilePath))
        {
            await walWriter.AppendAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("value1"), 1UL);
        }

        // Corrupt a byte in the payload area. Frame layout from offset 0:
        //   [0..7]   file header (magic + format_version=1)
        //   [8..11]  payload-length LE int32  ┐
        //   [12]     EntryKind byte           │ header CRC scope
        //   [13]     ValueKind byte           │
        //   [14..21] SequenceNumber LE u64    ┘
        //   [22..25] header CRC32C LE uint32
        //   [26..]   payload bytes            ] payload CRC scope
        //   [tail-4..tail] payload CRC32C LE uint32 (Castagnoli polynomial)
        // Index 26 is the first payload byte; flipping it forces a payload CRC mismatch on the
        // only (final) frame without breaking the frame structure, which is what the test is
        // validating.
        var fileBytes = await File.ReadAllBytesAsync(FirstSegmentPath);
        fileBytes[26] ^= 0xFF;
        await File.WriteAllBytesAsync(FirstSegmentPath, fileBytes);

        // Act: Create a new WriteAheadLog instance for recovery.
        using var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(WriteAheadLogFilePath);

        // Assert: the damaged acknowledged frame fails loud; nothing is truncated.
        await FluentActions.Awaiting(() => wal.RecoverAsync()).Should().ThrowAsync<WriteAheadLogCorruptException>();
        (await File.ReadAllBytesAsync(FirstSegmentPath)).Should().Equal(fileBytes);
    }

    /// <summary>
    /// Test to ensure that TruncateAsync clears the WriteAheadLog file.
    /// </summary>
    [Fact]
    public async Task TruncateAsync_ShouldClearTheLog()
    {
        // Arrange
        using var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(WriteAheadLogFilePath);
        await wal.AppendAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("value1"), 1UL);
        await wal.AppendAsync(new SerializableWrapper<int>(2), new SerializableWrapper<string>("value2"), 2UL);

        // Act
        await wal.TruncateAsync();

        // Assert: Recovery should return an empty list, and TruncateAsync should have deleted
        // every wal_*.log segment file in the directory.
        var entries = await wal.RecoverAsync();
        entries.Should().BeEmpty();
        Directory.GetFiles(WriteAheadLogFilePath, "wal_*.log").Should().BeEmpty();
    }

    /// <summary>
    /// Test to ensure that multiple append + recover round-trips work correctly.
    /// </summary>
    [Fact]
    public async Task MultipleRoundTrips_ShouldWorkCorrectly()
    {
        // Arrange & Act: First round-trip.
        using var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(WriteAheadLogFilePath);
        await wal.AppendAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("first"), 1UL);

        var entries1 = await wal.RecoverAsync();
        entries1.Should().HaveCount(1);
        entries1[0].InlineValue!.Value.Should().Be("first");

        // Truncate and start a second round-trip.
        await wal.TruncateAsync();

        await wal.AppendAsync(new SerializableWrapper<int>(10), new SerializableWrapper<string>("second_a"), 10UL);
        await wal.AppendAsync(new SerializableWrapper<int>(20), new SerializableWrapper<string>("second_b"), 20UL);

        var entries2 = await wal.RecoverAsync();
        entries2.Should().HaveCount(2);
        entries2[0].Key.Value.Should().Be(10);
        entries2[0].InlineValue!.Value.Should().Be("second_a");
        entries2[1].Key.Value.Should().Be(20);
        entries2[1].InlineValue!.Value.Should().Be("second_b");

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
        var act = () => wal.AppendAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("value"), 1UL);
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
    /// Verifies that a corrupt frame with an acknowledged, CRC-valid frame after it is treated as
    /// mid-file corruption rather than a torn tail. The damaged frame's payload checksum fails
    /// with bytes after it, so recovery does not skip the damage and resume; and because a torn
    /// append can only sit at end of file, truncating at the damage would destroy entry 3.
    /// Recovery fails closed and leaves the segment untouched.
    /// </summary>
    [Fact]
    public async Task RecoverAsync_CorruptFrameFollowedByValidFrame_FailsClosed()
    {
        // Arrange: Write 3 entries, then dispose the WriteAheadLog so the file is not locked.
        var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(WriteAheadLogFilePath);
        await wal.AppendAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("first"), 1UL);
        await wal.AppendAsync(new SerializableWrapper<int>(2), new SerializableWrapper<string>("second"), 2UL);
        await wal.AppendAsync(new SerializableWrapper<int>(3), new SerializableWrapper<string>("third"), 3UL);
        wal.Dispose();

        // Read the raw segment-file bytes to locate and corrupt the second entry's payload.
        // Frame layout from offset 0:
        //   [0..7]   file header (magic + format_version=1)
        //   then repeating entries of
        //     [4B length][1B entry_kind][1B value_kind][8B seqno][4B header CRC32C][payload][4B payload CRC32C]
        var fileBytes = await File.ReadAllBytesAsync(FirstSegmentPath);

        const int fileHeader = 8;
        const int perFrameHeader = 4 + 1 + 1 + 8 + 4; // length + entry_kind + value_kind + seqno + header CRC
        const int crcSize = 4;

        // Parse the first entry to find where the second entry starts.
        var firstPayloadLen = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(
            fileBytes.AsSpan(fileHeader, 4));
        var secondEntryStart = fileHeader + perFrameHeader + firstPayloadLen + crcSize;

        // Corrupt a byte in the second entry's payload (after its length + kind prefix).
        // Entry 3 is an intact, acknowledged frame after the damage.
        fileBytes[secondEntryStart + perFrameHeader + 2] ^= 0xFF;
        await File.WriteAllBytesAsync(FirstSegmentPath, fileBytes);

        // Act: Create a new WriteAheadLog instance for recovery.
        using var recoveryWal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(WriteAheadLogFilePath);
        var act = () => recoveryWal.RecoverAsync();

        // Assert: recovery refuses to treat the damage as a torn tail, and the segment keeps
        // every byte — entry 3 is not truncated away.
        var thrown = await act.Should().ThrowAsync<WriteAheadLogCorruptException>();
        thrown.Which.LastGoodOffset.Should().Be(secondEntryStart);
        new FileInfo(FirstSegmentPath).Length.Should().Be(fileBytes.Length);
    }

    /// <summary>
    /// Verifies the WAL frame's two checksums: the header CRC field matches a Crc32C (Castagnoli)
    /// hash of <c>[length][entry_kind][value_kind][seqno]</c> — the length prefix included — and
    /// the payload CRC field matches a Crc32C hash of the payload; the Ethernet polynomial (Crc32)
    /// produces different values. A single comparison per field is sufficient because the two
    /// polynomials produce distinct digests for the same input.
    /// </summary>
    [Fact]
    public async Task FrameChecksum_UsesCrc32C_OverHeaderAndPayload()
    {
        // Arrange — append one entry and dispose to flush the segment.
        using (var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(WriteAheadLogFilePath))
        {
            await wal.AppendAsync(new SerializableWrapper<int>(42), new SerializableWrapper<string>("crc-probe"), 7UL);
        }

        // Act — read the raw segment bytes and locate both checksum scopes.
        var fileBytes = await File.ReadAllBytesAsync(FirstSegmentPath);
        const int fileHeader = 8;
        const int headerFields = 4 + 1 + 1 + 8; // length + entry_kind + value_kind + seqno
        const int crcSize = 4;
        var payloadLength = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(fileBytes.AsSpan(fileHeader, 4));
        var headerScope = fileBytes.AsSpan(fileHeader, headerFields);
        var storedHeaderCrc = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
            fileBytes.AsSpan(fileHeader + headerFields, crcSize));
        var payloadStart = fileHeader + headerFields + crcSize;
        var payloadScope = fileBytes.AsSpan(payloadStart, payloadLength);
        var storedPayloadCrc = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
            fileBytes.AsSpan(payloadStart + payloadLength, crcSize));

        // Assert — Castagnoli matches over each scope; Ethernet polynomial does not.
        storedHeaderCrc.Should().Be(
            Boutquin.Storage.Infrastructure.Algorithms.Crc32C.HashToUInt32(headerScope),
            "WAL format specifies a Crc32C header checksum over [length][entry_kind][value_kind][seqno]");
        storedHeaderCrc.Should().NotBe(System.IO.Hashing.Crc32.HashToUInt32(headerScope));
        storedPayloadCrc.Should().Be(
            Boutquin.Storage.Infrastructure.Algorithms.Crc32C.HashToUInt32(payloadScope),
            "WAL format specifies a Crc32C payload checksum over the payload bytes");
        storedPayloadCrc.Should().NotBe(System.IO.Hashing.Crc32.HashToUInt32(payloadScope));
        fileBytes.Length.Should().Be(payloadStart + payloadLength + crcSize, "one frame follows the file header");
    }

    // ── TruncateAsync semantics ──────────────────────────────────────────────

    [Fact]
    public async Task TruncateAsync_DeletesAllSegmentFiles()
    {
        using var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(
            WriteAheadLogFilePath);

        await wal.AppendAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("a"), 1UL);
        await wal.AppendAsync(new SerializableWrapper<int>(2), new SerializableWrapper<string>("b"), 2UL);

        await wal.TruncateAsync();

        Directory.GetFiles(_testDirectory, "wal_*.log").Should().BeEmpty(
            "TruncateAsync must delete all segment files");

        var recovered = await wal.RecoverAsync();
        recovered.Should().BeEmpty();
    }

    [Fact]
    public async Task TruncateAsync_AfterTruncate_NextAppendCreatesSegmentOne()
    {
        using var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(
            WriteAheadLogFilePath);

        await wal.AppendAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("x"), 1UL);
        await wal.TruncateAsync();

        await wal.AppendAsync(new SerializableWrapper<int>(2), new SerializableWrapper<string>("y"), 2UL);

        File.Exists(FirstSegmentPath).Should().BeTrue(
            "after truncate, the next append must create wal_000001.log");
    }

    [Fact]
    public async Task AppendAsync_PersistedEntriesRecoverable()
    {
        using (var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(
            WriteAheadLogFilePath))
        {
            await wal.AppendAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("one"), 1UL);
            await wal.AppendAsync(new SerializableWrapper<int>(2), new SerializableWrapper<string>("two"), 2UL);
            await wal.AppendAsync(new SerializableWrapper<int>(3), new SerializableWrapper<string>("three"), 3UL);
        }

        using var wal2 = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(
            WriteAheadLogFilePath);
        var recovered = await wal2.RecoverAsync();

        recovered.Should().HaveCount(3);
        recovered.Select(e => e.Key.Value).Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task AppendAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(
            WriteAheadLogFilePath);
        wal.Dispose();

        var act = async () => await wal.AppendAsync(
            new SerializableWrapper<int>(1), new SerializableWrapper<string>("v"), 1UL);

        await act.Should().ThrowAsync<ObjectDisposedException>().ConfigureAwait(true);
    }

    // ── ReadAndValidateHeaderAsync version/magic path ─────────────────────────

    [Fact]
    public async Task RecoverAsync_SegmentWithWrongFormatVersion_ThrowsWriteAheadLogCorruptException()
    {
        using (var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(
            WriteAheadLogFilePath))
        {
            await wal.AppendAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("v"), 1UL);
        }

        // Overwrite format-version field (bytes 4–7) with version 2.
        using (var fs = new FileStream(FirstSegmentPath, FileMode.Open, FileAccess.ReadWrite))
        {
            Span<byte> ver = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(ver, 2u);
            fs.Seek(4, SeekOrigin.Begin);
            fs.Write(ver);
        }

        using var wal2 = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(
            WriteAheadLogFilePath);

        var act = async () => await wal2.RecoverAsync();

        var thrown = await act.Should().ThrowAsync<WriteAheadLogCorruptException>()
            .WithMessage("*unknown format version*").ConfigureAwait(true);
        thrown.Which.SegmentPath.Should().Be(FirstSegmentPath);
    }

    [Fact]
    public async Task RecoverAsync_SegmentWithBadMagic_ThrowsWriteAheadLogCorruptException()
    {
        using (var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(
            WriteAheadLogFilePath))
        {
            await wal.AppendAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("v"), 1UL);
        }

        // Overwrite magic bytes (bytes 0–3) with zeros.
        using (var fs = new FileStream(FirstSegmentPath, FileMode.Open, FileAccess.ReadWrite))
        {
            fs.Seek(0, SeekOrigin.Begin);
            fs.Write([0x00, 0x00, 0x00, 0x00]);
        }

        using var wal2 = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(
            WriteAheadLogFilePath);

        var act = async () => await wal2.RecoverAsync();

        var thrown = await act.Should().ThrowAsync<WriteAheadLogCorruptException>()
            .WithMessage("*BSWL*magic*").ConfigureAwait(true);
        thrown.Which.SegmentPath.Should().Be(FirstSegmentPath);
    }

    // ── Frame-corruption fault injection ─────────────────────────────────────

    /// <summary>
    /// Length-prefix in a WAL frame is read as Int32 LE. Flipping it to a negative value
    /// surfaces as "corrupt frame" which stops the segment scan; recovered entries are those
    /// preceding the corruption.
    /// </summary>
    [Fact]
    public async Task RecoverAsync_NegativeFramePrefix_StopsAtCorruption()
    {
        {
            using var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(WriteAheadLogFilePath);
            await wal.AppendAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("a"), 1ul);
            await wal.AppendAsync(new SerializableWrapper<int>(2), new SerializableWrapper<string>("b"), 2ul);
        }

        // Append a 4-byte negative length prefix as a "third frame", followed by garbage.
        using (var fs = new FileStream(FirstSegmentPath, FileMode.Append, FileAccess.Write))
        {
            Span<byte> negLen = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(negLen, -1);
            fs.Write(negLen);
            fs.Write([0xAA, 0xBB, 0xCC]);
        }

        using var recovered = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(WriteAheadLogFilePath);
        var entries = await recovered.RecoverAsync().ConfigureAwait(true);

        // Pre-corruption entries are recovered; the corrupt frame stops the scan.
        entries.Should().HaveCount(2);
        entries[0].Key.Value.Should().Be(1);
        entries[1].Key.Value.Should().Be(2);
    }

    [Fact]
    public async Task RecoverAsync_OversizedFramePrefix_StopsAtCorruption()
    {
        {
            using var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(WriteAheadLogFilePath);
            await wal.AppendAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("a"), 1ul);
        }

        // Append a length prefix exceeding any reasonable payload (e.g., 2 GiB).
        using (var fs = new FileStream(FirstSegmentPath, FileMode.Append, FileAccess.Write))
        {
            Span<byte> bigLen = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bigLen, int.MaxValue);
            fs.Write(bigLen);
            fs.Write([0xAA]);
        }

        using var recovered = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(WriteAheadLogFilePath);
        var entries = await recovered.RecoverAsync().ConfigureAwait(true);

        entries.Should().HaveCount(1);
        entries[0].Key.Value.Should().Be(1);
    }

    [Fact]
    public async Task RecoverAsync_TruncatedFrame_StopsCleanly()
    {
        {
            using var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(WriteAheadLogFilePath);
            await wal.AppendAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("a"), 1ul);
        }

        // Append a length prefix declaring 100 bytes, followed by only 5 bytes — the
        // frame is structurally truncated. Reader stops cleanly at end-of-segment.
        using (var fs = new FileStream(FirstSegmentPath, FileMode.Append, FileAccess.Write))
        {
            Span<byte> len = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(len, 100);
            fs.Write(len);
            fs.Write([0x01, 0x02, 0x03, 0x04, 0x05]);
        }

        using var recovered = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(WriteAheadLogFilePath);
        var entries = await recovered.RecoverAsync().ConfigureAwait(true);

        // Pre-truncation entry is recovered; truncated tail is silently dropped.
        entries.Should().HaveCount(1);
    }

    [Fact]
    public async Task RecoverAsync_CorruptCrcMidFrame_StopsAtCorruption()
    {
        // Two valid frames; flip a single byte in the second frame's payload.
        {
            using var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(WriteAheadLogFilePath);
            await wal.AppendAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("a"), 1ul);
            await wal.AppendAsync(new SerializableWrapper<int>(2), new SerializableWrapper<string>("b"), 2ul);
        }

        // Flip a payload byte of the second frame (close to the end, inside the last frame). The
        // second frame was acknowledged after its fsync and every one of its bytes landed, so the
        // CRC mismatch is damage to acknowledged history, not a torn tail.
        var bytes = File.ReadAllBytes(FirstSegmentPath);
        bytes[^5] ^= 0xFF; // flip a byte close to the end (inside last frame)
        File.WriteAllBytes(FirstSegmentPath, bytes);

        using var recovered = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(WriteAheadLogFilePath);

        // The scan stops at the damaged frame and fails loud; nothing is truncated.
        await FluentActions.Awaiting(() => recovered.RecoverAsync()).Should()
            .ThrowAsync<WriteAheadLogCorruptException>().ConfigureAwait(true);
        File.ReadAllBytes(FirstSegmentPath).Should().Equal(bytes);
    }

    [Fact]
    public async Task RecoverAsync_AfterCorruption_PreservesIsCorruptStateForFutureAppends()
    {
        // After a corrupt-frame recovery, subsequent appends should still work — recovery
        // does NOT poison the WAL's _isCorrupt state (only group-commit fsync failure does).
        {
            using var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(WriteAheadLogFilePath);
            await wal.AppendAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("a"), 1ul);
        }
        // Append corrupt tail.
        using (var fs = new FileStream(FirstSegmentPath, FileMode.Append, FileAccess.Write))
        {
            Span<byte> negLen = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(negLen, -1);
            fs.Write(negLen);
        }

        using var recovered = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(WriteAheadLogFilePath);
        await recovered.RecoverAsync().ConfigureAwait(true);

        // Append after corruption recovery still works.
        Func<Task> act = async () => await recovered.AppendAsync(
            new SerializableWrapper<int>(99), new SerializableWrapper<string>("after"), 99ul);
        await act.Should().NotThrowAsync();
    }

    // ── Multi-segment recovery with a corrupt middle segment ─────────────────

    /// <summary>
    /// Damage in the middle of the LAST segment, with acknowledged frames after it, is mid-file
    /// corruption: recovery fails closed instead of truncating the rest of the segment away.
    /// </summary>
    [Fact]
    public async Task RecoverAsync_TwoSegmentsCorruptMiddleFrame_FailsClosed()
    {
        // Force a tiny segment so a few large entries spill into multiple files.
        var options = new global::Boutquin.Storage.Infrastructure.WriteAheadLog.WriteAheadLogOptions
        {
            SegmentMaxBytes = 1L * 1024L * 1024L,
        };

        // Write enough entries to populate at least two segments.
        {
            using var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(_testDirectory, options);
            for (var i = 0; i < 1000; i++)
            {
                await wal.AppendAsync(
                    new SerializableWrapper<int>(i),
                    new SerializableWrapper<string>(new string('x', 1024)),
                    (ulong)(i + 1));
            }
        }

        // Find the segment files; corrupt the second segment's middle.
        var segmentFiles = Directory.GetFiles(_testDirectory, "wal_*.log").OrderBy(p => p).ToList();
        if (segmentFiles.Count < 2)
        {
            return; // not enough segments to exercise the multi-segment path
        }

        var middleSegment = segmentFiles[1];
        var bytes = File.ReadAllBytes(middleSegment);
        bytes[bytes.Length / 2] ^= 0xFF;
        File.WriteAllBytes(middleSegment, bytes);

        // Recover — the damage in the last segment is followed by valid frames, so it is not
        // a torn tail; recovery throws and the segment keeps its bytes.
        using var recovered = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(_testDirectory, options);
        var act = () => recovered.RecoverAsync();

        await act.Should().ThrowAsync<WriteAheadLogCorruptException>(
            "acknowledged frames follow the damage in the last segment");
        new FileInfo(middleSegment).Length.Should().Be(bytes.Length);
    }

    /// <summary>
    /// A corrupt frame in a NON-last segment is real corruption, not tail damage: writes
    /// demonstrably continued past it (later segments exist). Skipping the rest of the
    /// segment while still replaying later segments would reconstruct a state with a hole
    /// in the middle of history — a state that never existed. Recovery must fail loudly
    /// instead (DDIA 2e Ch. 4 licenses discarding the corrupted/incomplete entries at the
    /// tail, not resuming after a hole).
    /// </summary>
    [Fact]
    public async Task RecoverAsync_CorruptFrameInNonLastSegment_ThrowsInsteadOfNonPrefixReplay()
    {
        var options = new global::Boutquin.Storage.Infrastructure.WriteAheadLog.WriteAheadLogOptions
        {
            SegmentMaxBytes = 1L * 1024L * 1024L,
        };

        // Write enough to populate at least three segments so segment[0] is unambiguously
        // followed by readable history.
        {
            using var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(_testDirectory, options);
            for (var i = 0; i < 2500; i++)
            {
                await wal.AppendAsync(
                    new SerializableWrapper<int>(i),
                    new SerializableWrapper<string>(new string('x', 1024)),
                    (ulong)(i + 1));
            }
        }

        var segmentFiles = Directory.GetFiles(_testDirectory, "wal_*.log").OrderBy(p => p).ToList();
        segmentFiles.Count.Should().BeGreaterThanOrEqualTo(3, "the arrange step must span three segments");

        // Corrupt the middle of the FIRST segment — mid-history damage.
        var firstSegment = segmentFiles[0];
        var bytes = File.ReadAllBytes(firstSegment);
        bytes[bytes.Length / 2] ^= 0xFF;
        File.WriteAllBytes(firstSegment, bytes);

        using var recovered = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(_testDirectory, options);
        var act = () => recovered.RecoverAsync();
        await act.Should().ThrowAsync<WriteAheadLogCorruptException>(
            "mid-history corruption followed by readable segments must fail recovery loudly, " +
            "not silently splice pre-hole and post-hole entries together").ConfigureAwait(true);
    }

    // ── Multi-recover idempotency ────────────────────────────────────────────

    [Fact]
    public async Task RecoverAsync_CalledTwice_BothReturnSameEntries()
    {
        using var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(WriteAheadLogFilePath);
        await wal.AppendAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("a"), 1ul);
        await wal.AppendAsync(new SerializableWrapper<int>(2), new SerializableWrapper<string>("b"), 2ul);

        var first = await wal.RecoverAsync().ConfigureAwait(true);
        var second = await wal.RecoverAsync().ConfigureAwait(true);

        first.Should().HaveCount(2);
        second.Should().HaveCount(2);
        first.Select(e => e.Key.Value).Should().Equal(second.Select(e => e.Key.Value));
    }

    [Fact]
    public async Task TruncateAsync_ClearsAllEntries()
    {
        using var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(WriteAheadLogFilePath);
        await wal.AppendAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("a"), 1ul);
        await wal.AppendAsync(new SerializableWrapper<int>(2), new SerializableWrapper<string>("b"), 2ul);

        await wal.TruncateAsync().ConfigureAwait(true);

        var entries = await wal.RecoverAsync().ConfigureAwait(true);
        entries.Should().BeEmpty();
    }

    [Fact]
    public async Task AppendDeleteAsync_TombstoneRoundTripsThroughRecovery()
    {
        {
            using var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(WriteAheadLogFilePath);
            await wal.AppendAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("a"), 1ul);
            await wal.AppendDeleteAsync(new SerializableWrapper<int>(1), 2ul);
        }

        using var recovered = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(WriteAheadLogFilePath);
        var entries = await recovered.RecoverAsync().ConfigureAwait(true);
        entries.Should().HaveCount(2);
        // The second entry is a delete record.
        entries[1].Kind.Should().Be(global::Boutquin.Storage.Domain.Enums.EntryKind.Delete);
    }

    /// <summary>
    /// A torn tail consisting of only the length prefix (payload bytes removed) is discarded:
    /// the reader detects that the payload+CRC bytes are missing after reading the 4-byte length
    /// field and treats the frame as a truncated tail — returning null rather than IsCorrupt.
    /// Recovery stops cleanly; no entry is surfaced for the partial frame.
    /// </summary>
    [Fact]
    public async Task RecoverFromTornTail_SingleEntryTruncated_RecoveredLogIsEmpty()
    {
        // Arrange: write one entry and close the WAL so the segment is fully flushed.
        using (var walWriter = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(WriteAheadLogFilePath))
        {
            await walWriter.AppendAsync(new SerializableWrapper<int>(42), new SerializableWrapper<string>("torn"), 1UL);
        }

        // Keep the file header (8 bytes) and the 4-byte length prefix intact, but remove
        // everything after it (entry_kind, value_kind, seqno, payload, CRC).  The reader will
        // read the length prefix, discover there are fewer bytes in the file than the frame
        // requires, and return null — the truncated-tail path, not IsCorrupt.
        const int FileHeaderSize = 8;
        const int LengthPrefixSize = 4;
        var segmentPath = FirstSegmentPath;
        using (var fs = new FileStream(segmentPath, FileMode.Open, FileAccess.Write))
        {
            fs.SetLength(FileHeaderSize + LengthPrefixSize);
        }

        // Act: open a new WAL on the same directory and recover.
        using var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(WriteAheadLogFilePath);
        var entries = await wal.RecoverAsync();

        // Assert: the torn entry is discarded; the log is empty.
        entries.Should().BeEmpty("a torn tail with only the length prefix must be discarded, not surfaced");
    }

    // The WAL reader treats any length prefix above its 256 MiB payload ceiling as a corrupt
    // frame. The writer must therefore refuse such a payload before acknowledging it;
    // otherwise an fsync-acknowledged write is unrecoverable by construction.
    private const int WalMaxPayloadSize = 256 * 1024 * 1024;

    /// <summary>
    /// A serialized payload one byte above the reader's 256 MiB ceiling is rejected with a typed
    /// range exception before anything is written or acknowledged, and the WAL stays usable: the
    /// earlier and later small appends both recover.
    /// </summary>
    [Fact]
    public async Task AppendAsync_PayloadAboveMaxPayloadSize_IsRejectedBeforeAcknowledgement()
    {
        using (var wal = new WriteAheadLog<SerializableWrapper<int>, RawBytesValue>(WriteAheadLogFilePath))
        {
            await wal.AppendAsync(new SerializableWrapper<int>(1), new RawBytesValue(8), 1UL).ConfigureAwait(true);

            // Size the value so serialized key + value is exactly one byte above the ceiling.
            var key = new SerializableWrapper<int>(2);
            using var keyBytes = new MemoryStream();
            key.Serialize(keyBytes);
            var oversized = new RawBytesValue(WalMaxPayloadSize - (int)keyBytes.Length + 1);
            var act = () => wal.AppendAsync(key, oversized, 2UL);

            await act.Should().ThrowAsync<ArgumentOutOfRangeException>(
                "a payload the reader would classify as corrupt must never be acknowledged").ConfigureAwait(true);

            await wal.AppendAsync(new SerializableWrapper<int>(3), new RawBytesValue(8), 3UL).ConfigureAwait(true);
        }

        using var reader = new WriteAheadLog<SerializableWrapper<int>, RawBytesValue>(WriteAheadLogFilePath);
        var entries = await reader.RecoverAsync().ConfigureAwait(true);

        entries.Select(e => e.Key.Value).Should().Equal(
            [1, 3], "the rejected append wrote nothing and the appends around it are durable");
    }

    /// <summary>
    /// <c>WriteQueueCapacity</c> bounds the frames producers have built and pinned, not just the
    /// requests admitted to the queue. While the flusher is stalled (here: inside a recovery
    /// whose deserializer is gated), at most <c>WriteQueueCapacity</c> producers may serialize a
    /// frame; the rest must wait without holding one. Every append still completes once the
    /// flusher resumes.
    /// </summary>
    [Fact]
    public async Task AppendAsync_StalledFlusher_FramesBuiltNeverExceedWriteQueueCapacity()
    {
        const int capacity = 2;
        const int producers = 24;
        var options = new WriteAheadLogOptions { WriteQueueCapacity = capacity, GroupCommitBatchSize = 1 };

        GatedValue.Reset();
        using var wal = new WriteAheadLog<SerializableWrapper<int>, GatedValue>(WriteAheadLogFilePath, options);
        await wal.AppendAsync(new SerializableWrapper<int>(0), new GatedValue(), 1UL).ConfigureAwait(true);

        // Stall the flusher: recovery deserializes entry 0 and blocks on the closed gate.
        var recovery = wal.RecoverAsync();
        GatedValue.DeserializeEntered.Wait(TimeSpan.FromSeconds(30)).Should().BeTrue();
        Interlocked.Exchange(ref GatedValue.SerializeCount, 0);

        var appends = Enumerable.Range(1, producers)
            .Select(i => Task.Run(() => wal.AppendAsync(new SerializableWrapper<int>(i), new GatedValue(), (ulong)(i + 1))))
            .ToArray();

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (Volatile.Read(ref GatedValue.SerializeCount) <= capacity && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20).ConfigureAwait(true);
        }

        var builtWhileStalled = Volatile.Read(ref GatedValue.SerializeCount);
        GatedValue.Gate.Set();

        builtWhileStalled.Should().BeLessThanOrEqualTo(
            capacity, "frames are built only after a queue slot is reserved, so a stalled flusher pins at most WriteQueueCapacity frames");

        await recovery.ConfigureAwait(true);
        await Task.WhenAll(appends).ConfigureAwait(true);
        var entries = await wal.RecoverAsync().ConfigureAwait(true);
        entries.Should().HaveCount(producers + 1, "back-pressure delays producers but never drops a frame");
    }

    /// <summary>
    /// One-byte test value that counts serializations and, while <see cref="Gate"/> is closed,
    /// blocks deserialization — a way to hold the WAL flusher inside a recovery request.
    /// </summary>
    private sealed class GatedValue : ISerializable<GatedValue>
    {
        public static int SerializeCount;

        public static ManualResetEventSlim Gate { get; private set; } = new(initialState: false);

        public static ManualResetEventSlim DeserializeEntered { get; private set; } = new(initialState: false);

        public static void Reset()
        {
            SerializeCount = 0;
            Gate = new ManualResetEventSlim(initialState: false);
            DeserializeEntered = new ManualResetEventSlim(initialState: false);
        }

        public void Serialize(Stream stream)
        {
            Interlocked.Increment(ref SerializeCount);
            stream.WriteByte(0x5A);
        }

        public static GatedValue Deserialize(Stream stream)
        {
            _ = stream.ReadByte();
            DeserializeEntered.Set();
            Gate.Wait(TimeSpan.FromSeconds(30));
            return new GatedValue();
        }
    }

    /// <summary>
    /// Test value that serializes to exactly <see cref="Length"/> zero bytes and deserializes the
    /// remainder of the frame payload. Pre-sizes a <see cref="MemoryStream"/> target so a
    /// near-ceiling payload costs one allocation rather than a doubling cascade.
    /// </summary>
    private sealed class RawBytesValue : ISerializable<RawBytesValue>
    {
        public RawBytesValue()
        {
        }

        public RawBytesValue(int length) => Length = length;

        public int Length { get; private set; }

        public void Serialize(Stream stream)
        {
            if (stream is MemoryStream memory && memory.Capacity < memory.Position + Length)
            {
                memory.Capacity = checked((int)(memory.Position + Length));
            }

            var chunk = new byte[Math.Min(Length, 1 << 20)];
            var remaining = Length;
            while (remaining > 0)
            {
                var count = Math.Min(remaining, chunk.Length);
                stream.Write(chunk, 0, count);
                remaining -= count;
            }
        }

        public static RawBytesValue Deserialize(Stream stream)
        {
            var length = 0;
            var buffer = new byte[4096];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                length += read;
            }

            return new RawBytesValue(length);
        }
    }

    /// <summary>
    /// The segment counter widens past six digits at index 1,000,000 (<c>wal_1000000.log</c>).
    /// Discovery must order and resume by the whole numeric index, so recovery replays
    /// <c>wal_999999.log</c> before <c>wal_1000000.log</c> and appends continue in the newest one.
    /// </summary>
    [Fact]
    public async Task SegmentIndexPastSixDigits_RecoversInIndexOrderAndAppendsToNewestSegment()
    {
        await WriteSealedAndActiveSegmentsAsync(_testDirectory, firstIndex: 999_999);
        var newestPath = Path.Combine(_testDirectory, "wal_1000000.log");
        var olderPath = Path.Combine(_testDirectory, "wal_999999.log");
        var olderLength = new FileInfo(olderPath).Length;
        var newestLength = new FileInfo(newestPath).Length;

        using (var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(WriteAheadLogFilePath))
        {
            var entries = await wal.RecoverAsync();
            entries.Select(e => e.SequenceNumber).Should().Equal(
                [1UL, 2UL, 3UL, 4UL, 5UL, 6UL], "recovery replays segments in index order");

            await wal.AppendAsync(new SerializableWrapper<int>(7), new SerializableWrapper<string>("v7"), 7UL);
        }

        new FileInfo(olderPath).Length.Should().Be(olderLength, "appends never resume in an older segment");
        new FileInfo(newestPath).Length.Should().BeGreaterThan(newestLength, "appends resume in the newest segment");
    }

    /// <summary>
    /// Writes, through the production writer, a sealed segment holding seqnos 1..4 at index
    /// <paramref name="firstIndex"/> and the active segment after it holding 5..6 (300,000-character
    /// values with 1 MiB segments make the fourth append seal the first segment). A seal binds the
    /// index in its file's name, so the segments are written at their final indices: the writer
    /// starts from a bare segment header placed at <paramref name="firstIndex"/>.
    /// </summary>
    private static async Task WriteSealedAndActiveSegmentsAsync(string dir, int firstIndex)
    {
        await File.WriteAllBytesAsync(
            Path.Combine(dir, $"wal_{firstIndex:D6}.log"),
            [(byte)'B', (byte)'S', (byte)'W', (byte)'L', 1, 0, 0, 0]);
        var options = new WriteAheadLogOptions { SegmentMaxBytes = 1L * 1024L * 1024L };
        var value = new string('v', 300_000);
        using var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(dir, options);
        for (var seqno = 1UL; seqno <= 6UL; seqno++)
        {
            await wal.AppendAsync(new SerializableWrapper<int>((int)seqno), new SerializableWrapper<string>(value), seqno);
        }
    }

    /// <summary>
    /// Clean up the temporary test directory after each test.
    /// </summary>
    /// <summary>
    /// Constructing the WAL over a directory tree that does not exist yet creates every missing
    /// level with its entry fsync'd into the parent: the WAL fsyncs only its own directory when it
    /// creates a segment, so a bare directory creation would leave the WAL directory's own entry —
    /// and every acknowledged frame beneath it — able to vanish in a crash.
    /// </summary>
    [Fact]
    public void Constructor_OverAMissingNestedTree_CreatesEveryLevelDurably()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var nested = Path.Combine(_testDirectory, "a", "b", "wal");
        Directory.Exists(Path.Combine(_testDirectory, "a")).Should().BeFalse("precondition: three levels are missing");

        using var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(nested);

        Directory.Exists(nested).Should().BeTrue();
        wal._durablyCreatedDirectoryLevels.Should().Be(3,
            "a/, a/b/ and a/b/wal/ were all created, and each new entry must be fsync'd into its parent");
    }

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
