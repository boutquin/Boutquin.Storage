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

using System.Buffers.Binary;

using WalType = Boutquin.Storage.Infrastructure.WriteAheadLog.WriteAheadLog<
    Boutquin.Storage.Domain.Helpers.SerializableWrapper<int>,
    Boutquin.Storage.Domain.Helpers.SerializableWrapper<string>>;

namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// Torn-tail versus mid-file corruption on the LAST WAL segment. A torn append sits at end of
/// file by construction, so damage followed by any CRC-valid frame is mid-file corruption of
/// acknowledged history: recovery (and the standalone-append tail repair) must fail closed with
/// <see cref="WriteAheadLogCorruptException"/> and leave the segment bytes untouched, never
/// truncate the valid frames away. Damage with nothing valid after it remains a torn tail.
/// </summary>
public sealed class WriteAheadLogRecoveryClassificationTests : IDisposable
{
    private const int FileHeaderSize = 8;
    private const int FrameHeaderSize = 4 + 1 + 1 + 8 + 4; // length + entry_kind + value_kind + seqno + header CRC
    private const int CrcSize = 4;

    private readonly string _walDirectory = Path.Combine(
        Path.GetTempPath(), $"WriteAheadLogRecoveryClassificationTests_{Guid.NewGuid():N}");

    public WriteAheadLogRecoveryClassificationTests() => Directory.CreateDirectory(_walDirectory);

    private string SegmentPath => Path.Combine(_walDirectory, "wal_000001.log");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_walDirectory))
            {
                Directory.Delete(_walDirectory, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    /// <summary>
    /// A payload bit-flip in the first of three acknowledged frames is mid-file corruption: two
    /// CRC-valid frames follow it. Recovery throws and the segment keeps every byte.
    /// </summary>
    [Fact]
    public async Task RecoverAsync_CrcDamageFollowedByValidFrames_ThrowsAndPreservesSegment()
    {
        await WriteThreeFramesAsync().ConfigureAwait(true);
        var bytes = await File.ReadAllBytesAsync(SegmentPath).ConfigureAwait(true);
        bytes[FileHeaderSize + FrameHeaderSize] ^= 0xFF; // first payload byte of frame 1
        await File.WriteAllBytesAsync(SegmentPath, bytes).ConfigureAwait(true);

        using var wal = new WalType(_walDirectory);
        var act = () => wal.RecoverAsync();

        await act.Should().ThrowAsync<WriteAheadLogCorruptException>(
            "CRC-valid acknowledged frames follow the damage, so it is not a torn tail");
        new FileInfo(SegmentPath).Length.Should().Be(
            bytes.Length, "fail-closed recovery must not truncate the valid frames after the damage");
    }

    /// <summary>
    /// An overstated (but in-range) length prefix on the first frame makes that frame appear to
    /// run past end of file — the shape the reader reports as a truncated tail. Valid frames
    /// still follow, so recovery throws and preserves the segment.
    /// </summary>
    [Fact]
    public async Task RecoverAsync_OverstatedLengthFollowedByValidFrames_ThrowsAndPreservesSegment()
    {
        await WriteThreeFramesAsync().ConfigureAwait(true);
        var bytes = await File.ReadAllBytesAsync(SegmentPath).ConfigureAwait(true);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(FileHeaderSize, 4), 1_000_000);
        await File.WriteAllBytesAsync(SegmentPath, bytes).ConfigureAwait(true);

        using var wal = new WalType(_walDirectory);
        var act = () => wal.RecoverAsync();

        await act.Should().ThrowAsync<WriteAheadLogCorruptException>(
            "an overstated length prefix with valid frames after it is mid-file corruption");
        new FileInfo(SegmentPath).Length.Should().Be(
            bytes.Length, "fail-closed recovery must not truncate the valid frames after the damage");
    }

    /// <summary>
    /// The standalone-append tail repair (first append to a pre-existing segment without
    /// <c>RecoverAsync</c>) applies the same discrimination: it refuses to truncate valid frames
    /// that follow damage, fails the append, and leaves the segment bytes untouched.
    /// </summary>
    [Fact]
    public async Task StandaloneAppend_DamageFollowedByValidFrames_FailsWithoutTruncating()
    {
        await WriteThreeFramesAsync().ConfigureAwait(true);
        var bytes = await File.ReadAllBytesAsync(SegmentPath).ConfigureAwait(true);
        bytes[FileHeaderSize + FrameHeaderSize] ^= 0xFF;
        await File.WriteAllBytesAsync(SegmentPath, bytes).ConfigureAwait(true);

        using (var wal = new WalType(_walDirectory))
        {
            var act = () => wal.AppendAsync(
                new SerializableWrapper<int>(4), new SerializableWrapper<string>("fourth"), 4UL);

            await act.Should().ThrowAsync<WriteAheadLogCorruptException>(
                "the tail repair must not destroy acknowledged frames that follow the damage");
        }

        new FileInfo(SegmentPath).Length.Should().Be(
            bytes.Length, "the refused repair leaves every byte of the damaged segment in place");
    }

    /// <summary>
    /// Damage in the LAST frame, with nothing after it, is not a torn tail when every byte of that
    /// frame landed: the frame was acknowledged after its fsync, so recovery fails loud and leaves
    /// the segment untouched. (An interrupted final append is a prefix followed by zeros; the
    /// threat-model tests pin that it is still repaired.)
    /// </summary>
    [Fact]
    public async Task RecoverAsync_DamageInFullyLandedFinalFrame_FailsLoudAndLeavesTheSegmentUntouched()
    {
        await WriteThreeFramesAsync().ConfigureAwait(true);
        var bytes = await File.ReadAllBytesAsync(SegmentPath).ConfigureAwait(true);
        var firstLen = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(FileHeaderSize, 4));
        var secondStart = FileHeaderSize + FrameHeaderSize + firstLen + CrcSize;
        var secondLen = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(secondStart, 4));
        var thirdStart = secondStart + FrameHeaderSize + secondLen + CrcSize;
        bytes[thirdStart + FrameHeaderSize] ^= 0xFF;
        await File.WriteAllBytesAsync(SegmentPath, bytes).ConfigureAwait(true);

        using var wal = new WalType(_walDirectory);
        await FluentActions.Awaiting(() => wal.RecoverAsync()).Should().ThrowAsync<WriteAheadLogCorruptException>(
            "the final frame's last byte landed, so its checksum failure is damage to an acknowledged write")
            .ConfigureAwait(true);
        (await File.ReadAllBytesAsync(SegmentPath).ConfigureAwait(true)).Should().Equal(bytes,
            "a fail-loud recovery keeps every byte of the acknowledged final frame");
    }

    /// <summary>
    /// A torn final append whose value bytes hold a complete, CRC-valid WAL frame (a value that
    /// carries serialised log records) is still a torn tail: the embedded frame is followed by the
    /// rest of the torn value, not by end-of-file. Recovery must return the durable prefix and
    /// truncate the torn frame, and the first append on a fresh instance must succeed.
    /// </summary>
    [Fact]
    public async Task RecoverAsync_TornFinalFrameEmbeddingACrcValidFrame_TruncatesTornTail()
    {
        var embedded = await AsciiEncodableFrameAsync().ConfigureAwait(true);
        var value = "AAAA" + Encoding.ASCII.GetString(embedded) + new string('B', 64);

        using (var wal = new WalType(_walDirectory))
        {
            await wal.AppendAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("first"), 1UL).ConfigureAwait(true);
        }
        var durableLength = new FileInfo(SegmentPath).Length;
        using (var wal = new WalType(_walDirectory))
        {
            await wal.AppendAsync(new SerializableWrapper<int>(2), new SerializableWrapper<string>(value), 2UL).ConfigureAwait(true);
        }

        // Crash mid-append: the final frame lost its last 32 bytes — a cut inside the 'B' filler
        // that follows the embedded frame, which itself is intact.
        using (var fs = new FileStream(SegmentPath, FileMode.Open, FileAccess.ReadWrite))
        {
            fs.SetLength(fs.Length - 32);
        }

        using (var wal = new WalType(_walDirectory))
        {
            var recover = () => wal.RecoverAsync();
            var entries = (await recover.Should().NotThrowAsync(
                "an embedded frame inside the torn final append is not a following frame").ConfigureAwait(true)).Subject;
            entries.Select(e => e.Key.Value).Should().Equal([1]);
        }
        new FileInfo(SegmentPath).Length.Should().Be(durableLength, "the torn final frame is truncated");

        using (var wal = new WalType(_walDirectory))
        {
            var append = () => wal.AppendAsync(new SerializableWrapper<int>(3), new SerializableWrapper<string>("third"), 3UL);
            await append.Should().NotThrowAsync().ConfigureAwait(true);
        }
    }

    /// <summary>
    /// The standalone-append tail repair (first append to a pre-existing segment, no
    /// <c>RecoverAsync</c>) applies the same discrimination to a torn final frame embedding a
    /// CRC-valid frame: it truncates the torn frame and the append lands after the durable prefix.
    /// </summary>
    [Fact]
    public async Task StandaloneAppend_TornFinalFrameEmbeddingACrcValidFrame_RepairsAndAppends()
    {
        var embedded = await AsciiEncodableFrameAsync().ConfigureAwait(true);
        var value = "AAAA" + Encoding.ASCII.GetString(embedded) + new string('B', 64);
        using (var wal = new WalType(_walDirectory))
        {
            await wal.AppendAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("first"), 1UL).ConfigureAwait(true);
            await wal.AppendAsync(new SerializableWrapper<int>(2), new SerializableWrapper<string>(value), 2UL).ConfigureAwait(true);
        }
        using (var fs = new FileStream(SegmentPath, FileMode.Open, FileAccess.ReadWrite))
        {
            fs.SetLength(fs.Length - 32);
        }

        using (var wal = new WalType(_walDirectory))
        {
            var append = () => wal.AppendAsync(new SerializableWrapper<int>(3), new SerializableWrapper<string>("third"), 3UL);
            await append.Should().NotThrowAsync(
                "the embedded frame inside the torn final append is not a following frame").ConfigureAwait(true);
        }

        using (var wal = new WalType(_walDirectory))
        {
            var entries = await wal.RecoverAsync().ConfigureAwait(true);
            entries.Select(e => e.Key.Value).Should().Equal([1, 3], "the torn frame is gone and the new append follows the prefix");
        }
    }

    /// <summary>
    /// The on-disk bytes of one WAL frame written by the production writer, chosen (by seqno and
    /// value text, which together vary both checksums) so every byte is 7-bit ASCII and the frame
    /// survives embedding in a UTF-8 string value.
    /// </summary>
    private static async Task<byte[]> AsciiEncodableFrameAsync()
    {
        for (var seqno = 1UL; ; seqno++)
        {
            var dir = Path.Combine(Path.GetTempPath(), $"WalEmbeddedFrame_{Guid.NewGuid():N}");
            try
            {
                using (var inner = new WalType(dir))
                {
                    await inner.AppendAsync(
                        new SerializableWrapper<int>(9), new SerializableWrapper<string>($"inner-{seqno}"), seqno).ConfigureAwait(true);
                }
                var frame = (await File.ReadAllBytesAsync(Path.Combine(dir, "wal_000001.log")).ConfigureAwait(true))[FileHeaderSize..];
                if (frame.All(b => b < 0x80))
                {
                    return frame;
                }
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    private async Task WriteThreeFramesAsync()
    {
        using var wal = new WalType(_walDirectory);
        await wal.AppendAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("first"), 1UL).ConfigureAwait(true);
        await wal.AppendAsync(new SerializableWrapper<int>(2), new SerializableWrapper<string>("second"), 2UL).ConfigureAwait(true);
        await wal.AppendAsync(new SerializableWrapper<int>(3), new SerializableWrapper<string>("third"), 3UL).ConfigureAwait(true);
    }
}
