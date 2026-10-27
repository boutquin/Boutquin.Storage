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

/// <summary>
/// Damaged length prefix followed by a valid frame during <see cref="ValueLogWriter.OpenAsync"/>
/// recovery.
/// <para>
/// The frame header carries its own checksum over the length, kind and sequence number, so a
/// damaged length prefix is detected as corruption directly — recovery never has to search for the
/// next valid frame, and its distance from the damage site is irrelevant. Both tests below place a
/// valid frame after the damage (near and far) and expect <see cref="CorruptValueLogException"/>
/// and an untouched file.
/// </para>
/// </summary>
public sealed class ValueLogForwardScanWindowTests : IAsyncDisposable
{
    private readonly string _dir;
    private readonly string _manifestPath;
    private ValueLogManifest? _manifest;
    private ValueLogWriter? _writer;

    public ValueLogForwardScanWindowTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vl-fswnd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _manifestPath = Path.Combine(_dir, "valuelog.manifest");
    }

    public async ValueTask DisposeAsync()
    {
        if (_writer is not null)
        {
            await _writer.DisposeAsync().ConfigureAwait(false);
        }
        if (_manifest is not null)
        {
            await _manifest.DisposeAsync().ConfigureAwait(false);
        }
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// A valid frame placed more than 8 KiB after the damage site: the damaged header fails its
    /// checksum, so <see cref="ValueLogWriter.OpenAsync"/> fails closed instead of truncating it.
    /// </summary>
    [Fact]
    public async Task OpenAsync_ValidFrameBeyond8KiB_ThrowsCorruptValueLogExceptionAndPreservesFile()
    {
        // ── Step 1: write one clean entry and close ───────────────────────────────────────
        _manifest = new ValueLogManifest(_manifestPath);
        _writer = await ValueLogWriter.OpenAsync(_dir, _manifest, ValueLogOptions.Default).ConfigureAwait(true);
        var cleanPayload = new byte[] { 0x01, 0x02, 0x03 };
        await _writer.AppendAsync(cleanPayload, 1ul).ConfigureAwait(true);
        await _writer.DisposeAsync().ConfigureAwait(true);
        _writer = null;
        await _manifest.DisposeAsync().ConfigureAwait(true);
        _manifest = null;

        // ── Step 2: corrupt the segment ───────────────────────────────────────────────────
        // Layout of a value-log segment with one entry:
        //   [0..7]    file header (magic 'BSVL' + format_version=1)                      = 8 bytes
        //   [8..24]   entry header (length u32 + header CRC32C u32 + kind u8 + seqno u64) = 17 bytes
        //   [25..27]  payload (3 bytes)
        //   [28..31]  payload CRC32C trailer (4 bytes)
        // Total = 32 bytes
        //
        // We OVERWRITE the entry header's length field (offset 8) with a large implausible value.
        // The header checksum no longer matches, so the damage is detected as corruption.
        //
        // We then APPEND a valid frame at an offset MORE THAN 8 KiB past the damaged position.

        var segPath = ValueLogWriter.ResolveSegmentPath(_dir, 0);
        var segBytes = await File.ReadAllBytesAsync(segPath).ConfigureAwait(true);

        // Overwrite the length field at offset 8 with uint.MaxValue (clearly implausible).
        var corrupted = (byte[])segBytes.Clone();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            corrupted.AsSpan(ValueLogFrame.HeaderSize, 4), uint.MaxValue);

        // Pad the file to place a valid frame more than 8 KiB (8192 bytes) past the damage site
        // (offset 8). The valid frame must start at offset >= 8 + 1 + 8192 = 8201.
        // We write 8200 bytes of filler after the corrupted header, then a minimal valid frame.
        const int ScanWindowBytes = 8192;
        const int fillerSize = ScanWindowBytes + 10; // well beyond the window

        // Build a valid minimal entry frame and write it at the end (well past the window).
        var validFrameData = BuildValidFrame([0xDE, 0xAD], sequenceNumber: 999ul);

        var frameOffset = corrupted.Length + fillerSize;
        // Allocate exactly the bytes needed: filler up to frameOffset + the valid frame.
        var finalBytes = new byte[frameOffset + validFrameData.Length];
        corrupted.CopyTo(finalBytes.AsSpan());
        // Middle bytes (corrupted.Length .. frameOffset-1) remain 0x00 (zero-initialized).
        validFrameData.CopyTo(finalBytes.AsSpan(frameOffset));

        await File.WriteAllBytesAsync(segPath, finalBytes).ConfigureAwait(true);

        // ── Step 3: open writer — must fail closed and leave the file untouched ──────────
        _manifest = new ValueLogManifest(_manifestPath);
        var openAct = async () =>
        {
            _writer = await ValueLogWriter.OpenAsync(_dir, _manifest, ValueLogOptions.Default).ConfigureAwait(false);
        };

        await openAct.Should().ThrowAsync<CorruptValueLogException>(
            "a damaged header with a valid frame after it is mid-file corruption, however far away that frame starts").ConfigureAwait(true);

        new FileInfo(segPath).Length.Should().Be(finalBytes.Length,
            "fail-closed recovery must not truncate the valid frame");
    }

    /// <summary>
    /// A valid frame placed close to the damage site (100 bytes after it): the damaged header
    /// fails its checksum and recovery throws <see cref="CorruptValueLogException"/>.
    /// This is the complementary near-distance case to the beyond-8-KiB test above.
    /// </summary>
    [Fact]
    public async Task OpenAsync_ValidFrameWithin8KiBWindow_ThrowsCorruptValueLogException()
    {
        // Write one clean entry then corrupt.
        _manifest = new ValueLogManifest(_manifestPath);
        _writer = await ValueLogWriter.OpenAsync(_dir, _manifest, ValueLogOptions.Default).ConfigureAwait(true);
        var cleanPayload = new byte[] { 0xAA, 0xBB };
        await _writer.AppendAsync(cleanPayload, 1ul).ConfigureAwait(true);
        await _writer.DisposeAsync().ConfigureAwait(true);
        _writer = null;
        await _manifest.DisposeAsync().ConfigureAwait(true);
        _manifest = null;

        var segPath = ValueLogWriter.ResolveSegmentPath(_dir, 0);
        var segBytes = await File.ReadAllBytesAsync(segPath).ConfigureAwait(true);

        // Corrupt the length field of the first entry.
        var corrupted = (byte[])segBytes.Clone();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            corrupted.AsSpan(ValueLogFrame.HeaderSize, 4), uint.MaxValue);

        // Place a valid frame WITHIN the 8 KiB window (100 bytes after damage site).
        const int smallOffset = 100;
        var frameBytes = BuildValidFrame([0x11], sequenceNumber: 77ul);

        var frameStart = ValueLogFrame.HeaderSize + smallOffset;
        var totalLength = frameStart + frameBytes.Length;
        var finalBytes = new byte[totalLength];
        corrupted.CopyTo(finalBytes.AsSpan());
        frameBytes.CopyTo(finalBytes.AsSpan(frameStart));
        await File.WriteAllBytesAsync(segPath, finalBytes).ConfigureAwait(true);

        // The damaged header fails its checksum with bytes after it → CorruptValueLogException.
        _manifest = new ValueLogManifest(_manifestPath);
        Func<Task> openAct = async () =>
        {
            _writer = await ValueLogWriter.OpenAsync(_dir, _manifest, ValueLogOptions.Default).ConfigureAwait(false);
        };

        await openAct.Should().ThrowAsync<CorruptValueLogException>(
            "a damaged header with a valid frame after it is mid-file corruption").ConfigureAwait(true);
    }

    // A frame in the writer's layout: [4B length][4B header CRC32C over length ‖ kind ‖ seqno]
    // [1B kind][8B seqno][payload][4B payload CRC32C].
    private static byte[] BuildValidFrame(byte[] payload, ulong sequenceNumber)
    {
        var frame = new byte[ValueLogFrame.EntryHeaderSize + payload.Length + ValueLogFrame.EntryCrcSize];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(0, 4), (uint)payload.Length);
        frame[8] = ValueLogFrame.KindPayload;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(9, 8), sequenceNumber);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            frame.AsSpan(4, 4), Crc32C.HashToUInt32(frame.AsSpan(0, 4), frame.AsSpan(8, 9)));
        payload.CopyTo(frame.AsSpan(ValueLogFrame.EntryHeaderSize));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            frame.AsSpan(ValueLogFrame.EntryHeaderSize + payload.Length), Crc32C.HashToUInt32(payload));
        return frame;
    }
}
