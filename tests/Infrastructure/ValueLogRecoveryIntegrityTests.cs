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

using System.Buffers.Binary;

using Boutquin.Storage.Domain.ValueObjects.ValueLog;
using Boutquin.Storage.Infrastructure.ValueLog;

namespace Boutquin.Storage.Infrastructure.Tests.ValueLog;

/// <summary>
/// Recovery integrity of <see cref="ValueLogWriter.OpenAsync"/>: a torn final frame is repaired by
/// truncation, while damage that hides acknowledged frames fails closed and leaves the file intact.
/// The two must be told apart even when the torn frame's own payload holds a CRC-valid frame
/// (value-log payloads are arbitrary caller bytes) and even when a corrupt length field makes a
/// mid-file frame look like the last one — which the frame header's own checksum detects.
/// </summary>
public sealed class ValueLogRecoveryIntegrityTests : IAsyncDisposable
{
    private readonly string _dir;
    private readonly string _manifestPath;
    private ValueLogManifest? _manifest;
    private ValueLogWriter? _writer;

    public ValueLogRecoveryIntegrityTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vl-rint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _manifestPath = Path.Combine(_dir, "valuelog.manifest");
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(true);
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task OpenAsync_WithLoweredMaxEntryBytes_KeepsAcknowledgedLargerFrame()
    {
        var original = new ValueLogOptions(MaxEntryBytes: 1024 * 1024);
        var value = RandomBytes(200_000, seed: 11);
        var pointer = await AppendAllAsync(original, value).ConfigureAwait(true);
        var segPath = ValueLogWriter.ResolveSegmentPath(_dir, 0);
        var lengthBefore = new FileInfo(segPath).Length;

        // The cap only governs what the writer accepts from now on; a frame written and
        // acknowledged under the earlier, larger cap is live data, not a torn tail.
        await OpenAsync(original with { MaxEntryBytes = 100_000 }).ConfigureAwait(true);

        new FileInfo(segPath).Length.Should().Be(lengthBefore,
            "recovery must not truncate an acknowledged frame because the append cap was lowered");
        using var reader = new ValueLogReader(_dir, ValueLogOptions.Default);
        reader.Read(pointer[0]).Should().Equal(value);
    }

    [Fact]
    public async Task OpenAsync_TornFinalFrameWhosePayloadEmbedsAValidFrame_TruncatesToLastCleanFrame()
    {
        var embedded = await ProduceWriterFrameAsync(RandomBytes(40, seed: 5), sequenceNumber: 9).ConfigureAwait(true);
        var payload = RandomBytes(4096, seed: 6);
        embedded.CopyTo(payload.AsSpan(64));
        var first = RandomBytes(300, seed: 7);
        var pointers = await AppendAllAsync(ValueLogOptions.Default, first, payload).ConfigureAwait(true);
        var segPath = ValueLogWriter.ResolveSegmentPath(_dir, 0);

        // Crash mid-append of the final frame: its last 2048 bytes never reached the disk.
        Truncate(segPath, new FileInfo(segPath).Length - 2048);

        var openAct = () => OpenAsync(ValueLogOptions.Default);
        await openAct.Should().NotThrowAsync(
            "a frame embedded in the torn frame's own payload is caller data, not an acknowledged follower").ConfigureAwait(true);

        new FileInfo(segPath).Length.Should().Be((long)pointers[1].Offset,
            "a torn final frame is cut back to the last clean frame boundary, whatever its payload holds");
        using var reader = new ValueLogReader(_dir, ValueLogOptions.Default);
        reader.Read(pointers[0]).Should().Equal(first);
    }

    [Fact]
    public async Task OpenAsync_SingleBitLengthRotLandingOnEof_FailsClosedAndKeepsFrames()
    {
        // Frame A carries 3 payload bytes; frame B is exactly 1024 bytes on disk, so setting bit 10
        // of A's length (3 → 1027) makes A's declared extent end exactly at end-of-file.
        var valueA = RandomBytes(3, seed: 1);
        var valueB = RandomBytes(1024 - ValueLogFrame.EntryHeaderSize - ValueLogFrame.EntryCrcSize, seed: 2);
        var pointers = await AppendAllAsync(ValueLogOptions.Default, valueA, valueB).ConfigureAwait(true);
        var segPath = ValueLogWriter.ResolveSegmentPath(_dir, 0);
        var bytes = File.ReadAllBytes(segPath);
        bytes[(int)pointers[0].Offset + 1] ^= 0x04;
        File.WriteAllBytes(segPath, bytes);

        await AssertOpenFailsClosedAsync(segPath, bytes.Length, pointers[1], valueB).ConfigureAwait(true);
    }

    [Fact]
    public async Task OpenAsync_LengthAndPayloadRotHidingLiveFramesPastEof_FailsClosedAndKeepsFrames()
    {
        // Rot that overstates A's length past end-of-file AND damages a payload byte: A can no
        // longer be re-framed at B's boundary, but B and C still tile the file exactly to EOF, which
        // only acknowledged frames can do. An inside-extent exemption must not swallow them.
        var valueA = RandomBytes(100, seed: 3);
        var valueB = RandomBytes(500, seed: 4);
        var valueC = RandomBytes(700, seed: 8);
        var pointers = await AppendAllAsync(ValueLogOptions.Default, valueA, valueB, valueC).ConfigureAwait(true);
        var segPath = ValueLogWriter.ResolveSegmentPath(_dir, 0);
        var bytes = File.ReadAllBytes(segPath);
        var a = (int)pointers[0].Offset;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(a, 4), (uint)(bytes.Length - a));
        bytes[a + ValueLogFrame.EntryHeaderSize + 10] ^= 0xFF;
        File.WriteAllBytes(segPath, bytes);

        await AssertOpenFailsClosedAsync(segPath, bytes.Length, pointers[2], valueC).ConfigureAwait(true);
    }

    private async Task AssertOpenFailsClosedAsync(string segPath, long lengthBefore, ValueLogPointer survivor, byte[] survivorValue)
    {
        _manifest = new ValueLogManifest(_manifestPath);
        var openAct = async () =>
        {
            _writer = await ValueLogWriter.OpenAsync(_dir, _manifest, ValueLogOptions.Default).ConfigureAwait(false);
        };

        await openAct.Should().ThrowAsync<CorruptValueLogException>(
            "acknowledged frames behind the damage prove mid-file corruption").ConfigureAwait(true);
        new FileInfo(segPath).Length.Should().Be(lengthBefore, "fail-closed recovery must not truncate live frames");
        using var reader = new ValueLogReader(_dir, ValueLogOptions.Default);
        reader.Read(survivor).Should().Equal(survivorValue);
    }

    private async Task<ValueLogPointer[]> AppendAllAsync(ValueLogOptions options, params byte[][] values)
    {
        await OpenAsync(options).ConfigureAwait(true);
        var pointers = new ValueLogPointer[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            pointers[i] = await _writer!.AppendAsync(values[i], (ulong)(i + 1)).ConfigureAwait(true);
        }
        await CloseAsync().ConfigureAwait(true);
        return pointers;
    }

    private async Task OpenAsync(ValueLogOptions options)
    {
        await CloseAsync().ConfigureAwait(true);
        _manifest = new ValueLogManifest(_manifestPath);
        _writer = await ValueLogWriter.OpenAsync(_dir, _manifest, options).ConfigureAwait(true);
    }

    private async Task CloseAsync()
    {
        if (_writer is not null)
        {
            await _writer.DisposeAsync().ConfigureAwait(true);
            _writer = null;
        }
        if (_manifest is not null)
        {
            await _manifest.DisposeAsync().ConfigureAwait(true);
            _manifest = null;
        }
    }

    // A genuine frame produced by the production writer in a separate scratch log, so the
    // embedded bytes are exactly what a writer emits (not a hand-rolled imitation).
    private static async Task<byte[]> ProduceWriterFrameAsync(byte[] value, ulong sequenceNumber)
    {
        var scratch = Path.Combine(Path.GetTempPath(), "vl-rint-src-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var manifest = new ValueLogManifest(Path.Combine(scratch, "valuelog.manifest"));
            await using (var writer = await ValueLogWriter.OpenAsync(scratch, manifest, ValueLogOptions.Default).ConfigureAwait(true))
            {
                await writer.AppendAsync(value, sequenceNumber).ConfigureAwait(true);
            }
            return File.ReadAllBytes(ValueLogWriter.ResolveSegmentPath(scratch, 0))[ValueLogFrame.HeaderSize..];
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); } catch { /* best effort */ }
        }
    }

    private static void Truncate(string path, long length)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
        fs.SetLength(length);
    }

    private static byte[] RandomBytes(int count, int seed)
    {
        var bytes = new byte[count];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }
}
