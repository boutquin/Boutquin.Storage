// Copyright (c) 2024-2026 Pierre G. Boutquin. All rights reserved.
//
//   Licensed under the Apache License, Version 2.0 (the "License").
//   You may not use this file except in compliance with the License.
//   You may obtain a copy of the License at
//
//       http://www.apache.org/licenses/LICENSE-2.0
//
//   Unless required by applicable law or agreed to in writing, software
//   distributed under the License is distributed on an "AS IS" BASIS,
//   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//
//   See the License for the specific language governing permissions and
//   limitations under the License.
//

using Boutquin.Storage.Domain.ValueObjects.ValueLog;
using Boutquin.Storage.Infrastructure.ValueLog;

namespace Boutquin.Storage.Infrastructure.Tests.ValueLog;

public sealed class ValueLogWriterTests : IAsyncDisposable
{
    private readonly string _dir;
    private readonly string _manifestPath;
    private ValueLogManifest? _manifest;

    public ValueLogWriterTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vlw-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _manifestPath = Path.Combine(_dir, "valuelog.manifest");
    }

    public async ValueTask DisposeAsync()
    {
        if (_manifest is not null)
        {
            await _manifest.DisposeAsync();
        }
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private async Task<(ValueLogWriter writer, ValueLogManifest manifest)> OpenWriterAsync(ValueLogOptions? options = null)
    {
        _manifest = new ValueLogManifest(_manifestPath);
        var writer = await ValueLogWriter.OpenAsync(_dir, _manifest, options ?? ValueLogOptions.Default);
        return (writer, _manifest);
    }

    private static byte[] ReadActiveSegment(string path)
    {
        // The writer remains open for writes. Windows readers must allow that existing access;
        // changing the writer's sharing contract would hide a fixture error in production code.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }

    [Fact]
    public async Task OpenAsync_OnEmptyDirectory_CreatesFirstSegmentWithHeader()
    {
        var (writer, manifest) = await OpenWriterAsync();
        await using var _ = writer;

        writer.CurrentFileIndex.Should().Be(0u);

        var path = ValueLogWriter.ResolveSegmentPath(_dir, 0);
        var bytes = ReadActiveSegment(path);
        bytes.Length.Should().Be(ValueLogFrame.HeaderSize);
        bytes[0..4].Should().Equal(ValueLogFrame.Magic);
        BitConverter.ToUInt32(bytes, 4).Should().Be(ValueLogFrame.CurrentFormatVersion);

        (await manifest.GetLiveFilesAsync()).Should().Contain(0u);
    }

    /// <summary>
    /// Opening over a missing segment directory (the manifest lives in its parent) creates that
    /// directory; its entry must be made durable before an acknowledged value lands in a segment
    /// inside it, or a crash can drop the directory and every acknowledged frame with it. Expected:
    /// one fsync for the created directory level plus one for the first segment's entry.
    /// </summary>
    [Fact]
    public async Task OpenAsync_OverMissingSegmentDirectory_FsyncsTheCreatedDirectoryEntry()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var segmentDir = Path.Combine(_dir, "vlog");
        _manifest = new ValueLogManifest(_manifestPath);
        await using var writer = await ValueLogWriter.OpenAsync(segmentDir, _manifest, ValueLogOptions.Default);
        await writer.AppendAsync(new byte[] { 1, 2, 3 }, 1);

        Directory.Exists(segmentDir).Should().BeTrue();
        writer._directoryFsyncCount.Should().Be(2,
            "the created segment directory's entry and the first segment's entry are each fsync'd");
    }

    [Fact]
    public async Task AppendAsync_EncodesFrameWithLengthHeaderCrcKindSeqnoPayloadCrc()
    {
        var (writer, _) = await OpenWriterAsync();
        await using var _w = writer;

        var payload = Encoding.UTF8.GetBytes("hello value log");
        var pointer = await writer.AppendAsync(payload, sequenceNumber: 42ul);

        pointer.FileIndex.Should().Be(0u);
        pointer.Offset.Should().Be((ulong)ValueLogFrame.HeaderSize);

        var path = ValueLogWriter.ResolveSegmentPath(_dir, 0);
        var bytes = ReadActiveSegment(path);

        // Frame at offset HeaderSize:
        // [4B length][4B header CRC32C over length ‖ kind ‖ seqno][1B kind][8B seqno][payload][4B payload CRC32C].
        var frame = bytes.AsSpan(ValueLogFrame.HeaderSize);
        BitConverter.ToUInt32(frame[..4]).Should().Be((uint)payload.Length);
        frame[8].Should().Be(ValueLogFrame.KindPayload);
        BitConverter.ToUInt64(frame.Slice(9, 8)).Should().Be(42ul);
        var expectedHeaderCrc = Crc32C.HashToUInt32(frame[..4], frame[8..17]);
        BitConverter.ToUInt32(frame.Slice(4, 4)).Should().Be(expectedHeaderCrc);
        frame.Slice(ValueLogFrame.EntryHeaderSize, payload.Length).ToArray().Should().Equal(payload);

        var expectedCrc = Crc32C.HashToUInt32(payload);
        BitConverter.ToUInt32(frame.Slice(ValueLogFrame.EntryHeaderSize + payload.Length, 4)).Should().Be(expectedCrc);
    }

    [Fact]
    public async Task AppendAsync_AdvancesPointerAcrossMultipleWrites()
    {
        var (writer, _) = await OpenWriterAsync();
        await using var _w = writer;

        var first = await writer.AppendAsync(new byte[] { 1, 2, 3 }, 1);
        var second = await writer.AppendAsync(new byte[] { 4, 5, 6, 7 }, 2);

        first.Offset.Should().Be((ulong)ValueLogFrame.HeaderSize);
        second.Offset.Should().Be(first.Offset + (ulong)ValueLogFrame.FrameSize(3));
    }

    [Fact]
    public async Task AppendAsync_RotatesWhenSegmentExceedsMax()
    {
        var payload = new byte[200];
        var smallSegmentOptions = new ValueLogOptions(
            ValueLogThresholdBytes: 64,
            SegmentMaxBytes: 300, // forces rotation after first append
            ReadHandleCacheSize: 16,
            MaxEntryBytes: 64L * 1024 * 1024);

        var (writer, manifest) = await OpenWriterAsync(smallSegmentOptions);
        await using var _w = writer;

        var first = await writer.AppendAsync(payload, 1);
        first.FileIndex.Should().Be(0u);

        var second = await writer.AppendAsync(payload, 2);
        second.FileIndex.Should().Be(1u);

        writer.CurrentFileIndex.Should().Be(1u);
        (await manifest.GetLiveFilesAsync()).Should().Contain(0u).And.Contain(1u);
    }

    [Fact]
    public async Task AppendAsync_OversizePayload_ThrowsValueTooLarge()
    {
        var options = new ValueLogOptions(MaxEntryBytes: 64);
        var (writer, _) = await OpenWriterAsync(options);
        await using var _w = writer;

        Func<Task> act = () => writer.AppendAsync(new byte[65], 1);

        await act.Should().ThrowAsync<ValueTooLargeException>();
    }

    [Fact]
    public async Task OpenAsync_AfterReopen_ResumesInHighestSegment()
    {
        ValueLogPointer? recorded = null;
        {
            var (writer, _) = await OpenWriterAsync();
            await using var _w = writer;
            recorded = await writer.AppendAsync("\t\t\t"u8.ToArray(), 7);
            // dispose
        }

        await _manifest!.DisposeAsync();
        _manifest = null;

        var (reopenedWriter, reopenedManifest) = await OpenWriterAsync();
        await using var _r = reopenedWriter;

        reopenedWriter.CurrentFileIndex.Should().Be(0u);
        (await reopenedManifest.GetLiveFilesAsync()).Should().Contain(0u);

        // Effective length preserved — further appends land after the prior frame.
        var second = await reopenedWriter.AppendAsync(new byte[] { 1 }, 8);
        second.Offset.Should().Be(recorded!.Value.Offset + (ulong)ValueLogFrame.FrameSize(3));
    }

    [Fact]
    public async Task OpenAsync_TruncatesTrailingPartialFrame()
    {
        {
            var (writer, _) = await OpenWriterAsync();
            await using var _w = writer;
            await writer.AppendAsync(new byte[] { 1, 2, 3 }, 1);
        }

        await _manifest!.DisposeAsync();
        _manifest = null;

        // Append 5 junk bytes to the segment file to simulate an aborted second append.
        var path = ValueLogWriter.ResolveSegmentPath(_dir, 0);
        var originalLength = new FileInfo(path).Length;
        using (var junk = new FileStream(path, FileMode.Append, FileAccess.Write))
        {
            junk.Write([0xAA, 0xBB, 0xCC, 0xDD, 0xEE]);
        }

        var (reopened, _) = await OpenWriterAsync();
        await using var _r = reopened;

        new FileInfo(path).Length.Should().Be(originalLength);
    }

    [Fact]
    public async Task OpenAsync_MidFileCrcFailure_IsFatal()
    {
        {
            var (writer, _) = await OpenWriterAsync();
            await using var _w = writer;
            await writer.AppendAsync(new byte[] { 1, 2, 3 }, 1);
            await writer.AppendAsync(new byte[] { 4, 5, 6 }, 2);
        }

        await _manifest!.DisposeAsync();
        _manifest = null;

        var path = ValueLogWriter.ResolveSegmentPath(_dir, 0);

        // Flip a byte inside the FIRST frame's payload so the second frame remains intact.
        using (var rw = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
        {
            rw.Seek(ValueLogFrame.HeaderSize + ValueLogFrame.EntryHeaderSize + 1, SeekOrigin.Begin);
            rw.WriteByte(0xFF);
        }

        Func<Task> act = async () =>
        {
            var (writer, _) = await OpenWriterAsync();
            await using var _w = writer;
        };

        await act.Should().ThrowAsync<CorruptValueLogException>();
    }

    [Fact]
    public async Task OpenAsync_MidFileCorruptLengthField_IsFatal_AndDoesNotTruncateValidFrames()
    {
        // A single flipped bit in a mid-file LENGTH field is corruption of exactly the same
        // shape as a mid-file CRC failure — but the scan classified an implausible length as
        // a trailing partial frame and SetLength-truncated the file there, physically
        // destroying every valid, CRC-intact, pointer-referenced frame after it. Open must
        // fail loudly instead, and the file must keep its original length.
        {
            var (writer, _) = await OpenWriterAsync();
            await using var _w = writer;
            await writer.AppendAsync(new byte[] { 1, 2, 3 }, 1);
            await writer.AppendAsync(new byte[] { 4, 5, 6 }, 2);
            await writer.AppendAsync(new byte[] { 7, 8, 9 }, 3);
        }

        await _manifest!.DisposeAsync();
        _manifest = null;

        var path = ValueLogWriter.ResolveSegmentPath(_dir, 0);
        var originalLength = new FileInfo(path).Length;

        // Frame layout: file header (8) + frame1 (17 + 3 + 4 = 24) → frame2's entry header
        // starts at 32; its 4-byte little-endian length field's HIGH byte is at offset 35.
        var frame2LengthHighByte = ValueLogFrame.HeaderSize
            + ValueLogFrame.EntryHeaderSize + 3 + ValueLogFrame.EntryCrcSize
            + 3;
        using (var rw = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
        {
            rw.Seek(frame2LengthHighByte, SeekOrigin.Begin);
            rw.WriteByte(0xFF);
        }

        Func<Task> act = async () =>
        {
            var (writer, _) = await OpenWriterAsync();
            await using var _w = writer;
        };

        await act.Should().ThrowAsync<CorruptValueLogException>(
            "an implausible mid-file length with valid frames after it is corruption, not a torn tail")
            .ConfigureAwait(true);

        new FileInfo(path).Length.Should().Be(originalLength,
            "open must never truncate away frames that still parse as valid — frame 3 is live data");
    }

    [Fact]
    public async Task OpenAsync_DeletesOrphanedTombstonedSegmentFile()
    {
        // The GC's documented crash contract: the manifest delete-tombstone is fsync'd BEFORE
        // the segment file is unlinked, and "a crash between the two leaves an orphan file the
        // next startup's reconciliation sweep removes". Simulate that crash on a LOWER-indexed
        // file (one the fresh-segment path can never reuse): tombstone file 0 in the manifest
        // but leave its bytes on disk; the next open must remove the orphan — no pointer can
        // reference it, the manifest says it is dead.
        var smallSegmentOptions = new ValueLogOptions(
            ValueLogThresholdBytes: 64,
            SegmentMaxBytes: 300, // forces rotation after first append
            ReadHandleCacheSize: 16,
            MaxEntryBytes: 64L * 1024 * 1024);

        {
            var (writer, manifest) = await OpenWriterAsync(smallSegmentOptions);
            await using var _w = writer;
            await writer.AppendAsync(new byte[200], 1); // file 0
            await writer.AppendAsync(new byte[200], 2); // rotates → file 1 (live append target)
            // Crash window: GC tombstones file 0, unlink never happens.
            await manifest.RecordDeleteAsync(0);
        }

        await _manifest!.DisposeAsync();
        _manifest = null;

        var orphanPath = ValueLogWriter.ResolveSegmentPath(_dir, 0);
        var livePath = ValueLogWriter.ResolveSegmentPath(_dir, 1);
        File.Exists(orphanPath).Should().BeTrue("the arrange step leaves the tombstoned file on disk");

        var (reopened, _) = await OpenWriterAsync(smallSegmentOptions);
        await using var _r = reopened;

        File.Exists(orphanPath).Should().BeFalse(
            "startup must reconcile orphaned (tombstoned-but-present) value-log files, " +
            "as the GC's crash-safety contract documents — otherwise the disk space leaks forever");
        File.Exists(livePath).Should().BeTrue("the manifest-live segment must survive the sweep");
    }

    // ── Recovery / format-error paths ────────────────────────────────────────

    [Fact]
    public async Task OpenAsync_MissingSegmentFileNamedByManifest_Throws()
    {
        // Seed the manifest with a fileIndex, then delete that segment file before reopen.
        {
            var (writer, _) = await OpenWriterAsync();
            await using var _w = writer;
            await writer.AppendAsync(new byte[] { 1 }, 1);
        }
        await _manifest!.DisposeAsync();
        _manifest = null;

        var path = ValueLogWriter.ResolveSegmentPath(_dir, 0);
        File.Delete(path);

        Func<Task> act = async () =>
        {
            var (writer, _) = await OpenWriterAsync();
            await using var _w = writer;
        };

        await act.Should().ThrowAsync<MissingValueLogFileException>();
    }

    [Fact]
    public async Task OpenAsync_SegmentShorterThanHeader_Throws()
    {
        // Seed manifest, then truncate the segment to below the header length.
        {
            var (writer, _) = await OpenWriterAsync();
            await using var _w = writer;
        }
        await _manifest!.DisposeAsync();
        _manifest = null;

        var path = ValueLogWriter.ResolveSegmentPath(_dir, 0);
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write))
        {
            fs.SetLength(2); // less than HeaderSize
        }

        Func<Task> act = async () =>
        {
            var (writer, _) = await OpenWriterAsync();
            await using var _w = writer;
        };

        await act.Should().ThrowAsync<UnrecognisedValueLogFormatException>();
    }

    [Fact]
    public async Task OpenAsync_BadMagic_Throws()
    {
        {
            var (writer, _) = await OpenWriterAsync();
            await using var _w = writer;
        }
        await _manifest!.DisposeAsync();
        _manifest = null;

        var path = ValueLogWriter.ResolveSegmentPath(_dir, 0);
        // Overwrite the 'BSVL' magic with garbage.
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write))
        {
            fs.Write([0xDE, 0xAD, 0xBE, 0xEF]);
        }

        Func<Task> act = async () =>
        {
            var (writer, _) = await OpenWriterAsync();
            await using var _w = writer;
        };

        await act.Should().ThrowAsync<UnrecognisedValueLogFormatException>().WithMessage("*magic*");
    }

    [Fact]
    public async Task OpenAsync_UnsupportedFormatVersion_Throws()
    {
        {
            var (writer, _) = await OpenWriterAsync();
            await using var _w = writer;
        }
        await _manifest!.DisposeAsync();
        _manifest = null;

        var path = ValueLogWriter.ResolveSegmentPath(_dir, 0);
        // Replace bytes [4..8] (format_version) with version 99.
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write))
        {
            fs.Seek(4, SeekOrigin.Begin);
            Span<byte> v = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(v, 99u);
            fs.Write(v);
        }

        Func<Task> act = async () =>
        {
            var (writer, _) = await OpenWriterAsync();
            await using var _w = writer;
        };

        await act.Should().ThrowAsync<UnrecognisedValueLogFormatException>().WithMessage("*format_version*");
    }

    [Fact]
    public async Task OpenAsync_StaleFileFromCrashedRun_Replaced()
    {
        // A file already exists at the next-segment path (orphan from a crashed prior run)
        // but is NOT in the manifest. OpenFreshSegmentAsync deletes it before creating.
        var stalePath = ValueLogWriter.ResolveSegmentPath(_dir, 0);
        File.WriteAllBytes(stalePath, [0x99, 0x99, 0x99, 0x99]);

        var (writer, _) = await OpenWriterAsync();
        await using var _w = writer;

        // The fresh segment must be opened with the proper magic, not the stale bytes.
        var bytes = ReadActiveSegment(stalePath);
        bytes[..4].Should().Equal(ValueLogFrame.Magic);
    }

    // ── Disposal / state-after-dispose ───────────────────────────────────────

    [Fact]
    public async Task AppendAsync_AfterDispose_ThrowsObjectDisposed()
    {
        var (writer, _) = await OpenWriterAsync();
        await writer.DisposeAsync();
        Func<Task> act = async () => await writer.AppendAsync(new byte[] { 1 }, 1);
        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent()
    {
        var (writer, _) = await OpenWriterAsync();
        await writer.DisposeAsync();
        Func<Task> act = async () => await writer.DisposeAsync();
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task CurrentFileBytes_AfterDispose_Throws()
    {
        var (writer, _) = await OpenWriterAsync();
        await writer.DisposeAsync();
        Action act = () => _ = writer.CurrentFileBytes;
        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public async Task FileSizes_AfterDispose_Throws()
    {
        var (writer, _) = await OpenWriterAsync();
        await writer.DisposeAsync();
        Action act = () => _ = writer.FileSizes;
        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public async Task FileSizes_TracksCurrentFile()
    {
        var (writer, _) = await OpenWriterAsync();
        await using var _w = writer;
        await writer.AppendAsync(new byte[] { 1, 2, 3 }, 1);

        var sizes = writer.FileSizes;
        sizes.Should().ContainKey(0u);
        sizes[0u].Should().BeGreaterThan(ValueLogFrame.HeaderSize);
        writer.CurrentFileBytes.Should().Be(sizes[0u]);
    }
}
