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
/// The value-log segment's deterministic tear rule, as must-recover / must-fail-loud pairs. Every
/// frame header (length, kind, seqno) carries its own checksum, so recovery decides on bytes the
/// writer authenticated rather than on a guess about which bytes might be frames: a torn final
/// append is truncated whatever its payload holds, and damage to an acknowledged frame's header
/// fails closed even when nothing follows it.
/// </summary>
public sealed class ValueLogSegmentHeaderChecksumTests : IAsyncDisposable
{
    private readonly string _dir;
    private readonly string _manifestPath;
    private ValueLogManifest? _manifest;
    private ValueLogWriter? _writer;

    public ValueLogSegmentHeaderChecksumTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vl-hcrc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _manifestPath = Path.Combine(_dir, "valuelog.manifest");
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(true);
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// Must recover: a crash cut the final append's payload short at end of file. Its payload holds
    /// a copy of real value-log frames (a value is arbitrary caller bytes), and the cut lands exactly
    /// where that copy ends, so CRC-valid frame images tile the file to end of file. The final frame's
    /// header is authentic and its payload is short at end of file: a torn tail, truncated.
    /// </summary>
    [Fact]
    public async Task OpenAsync_TornFinalFrameCutAtEndOfEmbeddedFrameCopy_TruncatesToLastCleanFrame()
    {
        var embedded = await ProduceWriterFramesAsync(RandomBytes(200, seed: 31), RandomBytes(900, seed: 32)).ConfigureAwait(true);
        var prefix = RandomBytes(2000, seed: 33);
        var suffix = RandomBytes(3000, seed: 34);
        var payload = new byte[prefix.Length + embedded.Length + suffix.Length];
        prefix.CopyTo(payload, 0);
        embedded.CopyTo(payload, prefix.Length);
        suffix.CopyTo(payload, prefix.Length + embedded.Length);

        var first = RandomBytes(300, seed: 35);
        var pointers = await AppendAllAsync(first, payload).ConfigureAwait(true);
        var segPath = ValueLogWriter.ResolveSegmentPath(_dir, 0);
        Truncate(segPath, (long)pointers[1].Offset + ValueLogFrame.EntryHeaderSize + prefix.Length + embedded.Length);

        _manifest = new ValueLogManifest(_manifestPath);
        var open = async () =>
        {
            _writer = await ValueLogWriter.OpenAsync(_dir, _manifest, ValueLogOptions.Default).ConfigureAwait(false);
        };

        await open.Should().NotThrowAsync(
            "the final frame's authenticated header says its payload is short at end of file, which is a torn tail " +
            "whatever frame images the payload holds").ConfigureAwait(true);
        new FileInfo(segPath).Length.Should().Be((long)pointers[1].Offset, "the torn final frame is cut back to the last clean frame");
        using var reader = new ValueLogReader(_dir, ValueLogOptions.Default);
        reader.Read(pointers[0]).Should().Equal(first);
    }

    /// <summary>
    /// Must recover: the final append's header reached the disk but its payload and checksum did not
    /// (zeros to end of file). The header is authentic; the payload checksum fails on the frame that
    /// ends the file: a torn tail.
    /// </summary>
    [Fact]
    public async Task OpenAsync_FinalFrameHeaderLandedPayloadZero_TruncatesToLastCleanFrame()
    {
        var first = RandomBytes(300, seed: 41);
        var pointers = await AppendAllAsync(first, RandomBytes(5000, seed: 42)).ConfigureAwait(true);
        var segPath = ValueLogWriter.ResolveSegmentPath(_dir, 0);
        var bytes = File.ReadAllBytes(segPath);
        Array.Clear(bytes, (int)pointers[1].Offset + ValueLogFrame.EntryHeaderSize, bytes.Length - (int)pointers[1].Offset - ValueLogFrame.EntryHeaderSize);
        File.WriteAllBytes(segPath, bytes);

        _manifest = new ValueLogManifest(_manifestPath);
        _writer = await ValueLogWriter.OpenAsync(_dir, _manifest, ValueLogOptions.Default).ConfigureAwait(true);

        new FileInfo(segPath).Length.Should().Be((long)pointers[1].Offset);
        using var reader = new ValueLogReader(_dir, ValueLogOptions.Default);
        reader.Read(pointers[0]).Should().Equal(first);
    }

    /// <summary>
    /// Must recover: the final, unacknowledged append landed only a prefix of its 17-byte header and
    /// the rest of the file — the header's remainder, the payload and any extent past it — reads as
    /// zeros (the file length reached the disk, the later bytes did not). Nothing acknowledged lies at
    /// or after that frame, so it is a torn tail: truncated back to the last clean frame.
    /// </summary>
    [Theory]
    [InlineData(9, 0)]
    [InlineData(9, 4096)]
    [InlineData(13, 4096)]
    [InlineData(17, 4096)]
    [InlineData(1, 0)]
    [InlineData(6, 0)]
    public async Task OpenAsync_FinalHeaderPrefixThenZerosToEof_TruncatesToLastCleanFrame(int landedHeaderBytes, int extraZeros)
    {
        var values = Enumerable.Range(0, 5).Select(i => RandomBytes(300, seed: 70 + i)).ToArray();
        var pointers = await AppendAllAsync(values).ConfigureAwait(true);
        var segPath = ValueLogWriter.ResolveSegmentPath(_dir, 0);
        var bytes = File.ReadAllBytes(segPath);
        var finalStart = (int)pointers[4].Offset;
        Array.Clear(bytes, finalStart + landedHeaderBytes, bytes.Length - finalStart - landedHeaderBytes);
        File.WriteAllBytes(segPath, [.. bytes, .. new byte[extraZeros]]);

        _manifest = new ValueLogManifest(_manifestPath);
        var open = async () =>
        {
            _writer = await ValueLogWriter.OpenAsync(_dir, _manifest, ValueLogOptions.Default).ConfigureAwait(false);
        };

        await open.Should().NotThrowAsync(
            "a header prefix followed only by zeros to end of file is the unwritten remainder of the final append")
            .ConfigureAwait(true);
        new FileInfo(segPath).Length.Should().Be(finalStart, "the torn final append is cut back to the last clean frame");
        using var reader = new ValueLogReader(_dir, ValueLogOptions.Default);
        for (var i = 0; i < 4; i++)
        {
            reader.Read(pointers[i]).Should().Equal(values[i]);
        }
    }

    /// <summary>
    /// Must recover: an unacknowledged append whose file extent is shorter than the frame landed only
    /// its first bytes (here 4 of the 17-byte header) and zeros cover the rest of the extent. The
    /// header checksum fails, but every byte after the landed prefix is zero to end of file: a torn
    /// tail, not corruption.
    /// </summary>
    [Fact]
    public async Task OpenAsync_UnacknowledgedAppendHeaderPrefixThenShortZeroExtent_TruncatesToLastCleanFrame()
    {
        var values = new[] { RandomBytes(300, seed: 81), RandomBytes(300, seed: 82), RandomBytes(500, seed: 83) };
        var pointers = await AppendAllAsync(values).ConfigureAwait(true);
        var segPath = ValueLogWriter.ResolveSegmentPath(_dir, 0);
        var bytes = File.ReadAllBytes(segPath);
        var finalStart = (int)pointers[2].Offset;
        File.WriteAllBytes(segPath, [.. bytes.AsSpan(0, finalStart + 4), .. new byte[40]]);

        _manifest = new ValueLogManifest(_manifestPath);
        var open = async () =>
        {
            _writer = await ValueLogWriter.OpenAsync(_dir, _manifest, ValueLogOptions.Default).ConfigureAwait(false);
        };

        await open.Should().NotThrowAsync("the final append's landed header prefix is followed only by zeros")
            .ConfigureAwait(true);
        new FileInfo(segPath).Length.Should().Be(finalStart);
        using var reader = new ValueLogReader(_dir, ValueLogOptions.Default);
        reader.Read(pointers[0]).Should().Equal(values[0]);
        reader.Read(pointers[1]).Should().Equal(values[1]);
    }

    /// <summary>
    /// Must fail loud: the landed prefix must be a prefix of a frame this writer could have written.
    /// A kind byte no writer emits is not one, even with zeros after it.
    /// </summary>
    [Fact]
    public async Task OpenAsync_FinalHeaderPrefixWithUnknownKindThenZeros_FailsClosed()
    {
        var pointers = await AppendAllAsync(RandomBytes(300, seed: 91), RandomBytes(300, seed: 92)).ConfigureAwait(true);
        var segPath = ValueLogWriter.ResolveSegmentPath(_dir, 0);
        var bytes = File.ReadAllBytes(segPath);
        var finalStart = (int)pointers[1].Offset;
        Array.Clear(bytes, finalStart + ValueLogFrame.EntryHeaderSize, bytes.Length - finalStart - ValueLogFrame.EntryHeaderSize);
        bytes[finalStart + 8] = 0x7E;
        Array.Clear(bytes, finalStart + 9, 8);
        File.WriteAllBytes(segPath, bytes);

        _manifest = new ValueLogManifest(_manifestPath);
        var open = async () =>
        {
            _writer = await ValueLogWriter.OpenAsync(_dir, _manifest, ValueLogOptions.Default).ConfigureAwait(false);
        };

        await open.Should().ThrowAsync<CorruptValueLogException>().ConfigureAwait(true);
        File.ReadAllBytes(segPath).Should().Equal(bytes);
    }

    /// <summary>
    /// Must fail loud: rot in any header field of the final acknowledged frame — the length (which
    /// may now reach past end of file), the header checksum, the kind byte or the sequence number —
    /// is detected by the header checksum. Nothing follows the frame, but a damaged header with bytes
    /// after it is not a torn append: recovery refuses and keeps every byte.
    /// </summary>
    [Theory]
    [InlineData(2, 0x80)]
    [InlineData(3, 0x01)]
    [InlineData(5, 0x01)]
    [InlineData(8, 0x01)]
    [InlineData(9, 0x01)]
    [InlineData(16, 0x80)]
    public async Task OpenAsync_HeaderRotOnFinalAcknowledgedFrame_FailsClosedAndKeepsFrame(int byteInHeader, int mask)
    {
        var second = RandomBytes(5000, seed: 52);
        var pointers = await AppendAllAsync(RandomBytes(300, seed: 51), second).ConfigureAwait(true);
        var segPath = ValueLogWriter.ResolveSegmentPath(_dir, 0);
        var bytes = File.ReadAllBytes(segPath);
        bytes[(int)pointers[1].Offset + byteInHeader] ^= (byte)mask;
        File.WriteAllBytes(segPath, bytes);

        _manifest = new ValueLogManifest(_manifestPath);
        var open = async () =>
        {
            _writer = await ValueLogWriter.OpenAsync(_dir, _manifest, ValueLogOptions.Default).ConfigureAwait(false);
        };

        await open.Should().ThrowAsync<CorruptValueLogException>(
            "a damaged header on an acknowledged frame is corruption, not a torn tail, even when nothing follows it")
            .ConfigureAwait(true);
        File.ReadAllBytes(segPath).Should().Equal(bytes, "fail-closed recovery keeps the acknowledged frame byte for byte");
    }

    /// <summary>
    /// The reader authenticates the header before trusting it: the payload checksum covers the payload
    /// only, so without the header checksum a kind byte rotted from Payload to Reserved (both accepted
    /// kinds) would be served as if intact. A damaged header checksum field alone is refused too.
    /// </summary>
    [Theory]
    [InlineData(8, 0x01)]
    [InlineData(5, 0x01)]
    public async Task Read_HeaderRot_ThrowsCorruptValueLogPointerException(int byteInHeader, int mask)
    {
        var pointers = await AppendAllAsync(RandomBytes(300, seed: 61), RandomBytes(2000, seed: 62)).ConfigureAwait(true);
        var segPath = ValueLogWriter.ResolveSegmentPath(_dir, 0);
        var bytes = File.ReadAllBytes(segPath);
        bytes[(int)pointers[0].Offset + byteInHeader] ^= (byte)mask;
        File.WriteAllBytes(segPath, bytes);

        using var reader = new ValueLogReader(_dir, ValueLogOptions.Default);
        var read = () => reader.Read(pointers[0]);

        read.Should().Throw<CorruptValueLogPointerException>("a frame whose header fails its checksum is not served");
        reader.Read(pointers[1]).Should().HaveCount(2000, "the neighbouring frame is unaffected");
    }

    private async Task<ValueLogPointer[]> AppendAllAsync(params byte[][] values)
    {
        await CloseAsync().ConfigureAwait(true);
        _manifest = new ValueLogManifest(_manifestPath);
        _writer = await ValueLogWriter.OpenAsync(_dir, _manifest, ValueLogOptions.Default).ConfigureAwait(true);
        var pointers = new ValueLogPointer[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            pointers[i] = await _writer.AppendAsync(values[i], (ulong)(i + 1)).ConfigureAwait(true);
        }

        await CloseAsync().ConfigureAwait(true);
        return pointers;
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

    // Real frames emitted by the production writer into a separate scratch log, so the embedded
    // bytes are exactly what a writer produces in whatever frame layout it uses.
    private static async Task<byte[]> ProduceWriterFramesAsync(params byte[][] values)
    {
        var scratch = Path.Combine(Path.GetTempPath(), "vl-hcrc-src-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var manifest = new ValueLogManifest(Path.Combine(scratch, "valuelog.manifest"));
            await using (var writer = await ValueLogWriter.OpenAsync(scratch, manifest, ValueLogOptions.Default).ConfigureAwait(true))
            {
                for (var i = 0; i < values.Length; i++)
                {
                    await writer.AppendAsync(values[i], (ulong)(100 + i)).ConfigureAwait(true);
                }
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
