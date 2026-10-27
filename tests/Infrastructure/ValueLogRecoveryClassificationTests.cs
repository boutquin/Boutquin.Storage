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
/// Torn-tail vs mid-file corruption classification during <see cref="ValueLogWriter.OpenAsync"/>
/// recovery. Each frame header carries a checksum over its length, kind and sequence number, so a
/// damaged length prefix with bytes after it is mid-file corruption and must fail closed rather
/// than truncate acknowledged, pointer-referenced values — however far away the next frame starts.
/// Value-log frames are large by design (only values above the separation threshold are written
/// here), so the successor of a damaged frame routinely starts many KiB after the damage site.
/// </summary>
public sealed class ValueLogRecoveryClassificationTests : IAsyncDisposable
{
    private readonly string _dir;
    private readonly string _manifestPath;
    private ValueLogManifest? _manifest;
    private ValueLogWriter? _writer;

    public ValueLogRecoveryClassificationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vl-rcls-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _manifestPath = Path.Combine(_dir, "valuelog.manifest");
    }

    public async ValueTask DisposeAsync()
    {
        if (_writer is not null)
        {
            await _writer.DisposeAsync().ConfigureAwait(true);
        }
        if (_manifest is not null)
        {
            await _manifest.DisposeAsync().ConfigureAwait(true);
        }
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    // 10 KiB and 100 KiB successors: the distance to the next live frame plays no part.
    [Theory]
    [InlineData(10 * 1024)]
    [InlineData(100 * 1024)]
    public async Task OpenAsync_ImplausibleLengthBeforeLargeLiveFrames_FailsClosedAndKeepsLiveValues(int valueBytes)
    {
        var (first, second, valueB) = await WriteTwoLargeValuesAsync(valueBytes).ConfigureAwait(true);
        var segPath = ValueLogWriter.ResolveSegmentPath(_dir, 0);
        OverwriteLength(segPath, (long)first.Offset, uint.MaxValue);
        var lengthBefore = new FileInfo(segPath).Length;

        await AssertOpenFailsClosedAsync(segPath, lengthBefore, second, valueB).ConfigureAwait(true);
    }

    // 10 KiB and 100 KiB successors: the distance to the next live frame plays no part.
    [Theory]
    [InlineData(10 * 1024)]
    [InlineData(100 * 1024)]
    public async Task OpenAsync_OverstatedPlausibleLengthBeforeLargeLiveFrames_FailsClosedAndKeepsLiveValues(int valueBytes)
    {
        var (first, second, valueB) = await WriteTwoLargeValuesAsync(valueBytes).ConfigureAwait(true);
        var segPath = ValueLogWriter.ResolveSegmentPath(_dir, 0);
        var total = new FileInfo(segPath).Length;
        // Within MaxEntryBytes and the file length, but the frame it declares runs past EOF: the
        // shape a torn final append has, detected here because the header checksum fails.
        OverwriteLength(segPath, (long)first.Offset, (uint)(total - (long)first.Offset));
        var lengthBefore = new FileInfo(segPath).Length;

        await AssertOpenFailsClosedAsync(segPath, lengthBefore, second, valueB).ConfigureAwait(true);
    }

    [Fact]
    public async Task OpenAsync_GenuineTornTailAfterLargeFrames_TruncatesToLastCleanFrame()
    {
        var (_, second, _) = await WriteTwoLargeValuesAsync(10 * 1024).ConfigureAwait(true);
        var segPath = ValueLogWriter.ResolveSegmentPath(_dir, 0);
        var secondFrameStart = (long)second.Offset;

        // Crash mid-append of the final frame: keep only part of its payload.
        using (var fs = new FileStream(segPath, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            fs.SetLength(secondFrameStart + ValueLogFrame.EntryHeaderSize + 100);
        }

        _manifest = new ValueLogManifest(_manifestPath);
        _writer = await ValueLogWriter.OpenAsync(_dir, _manifest, ValueLogOptions.Default).ConfigureAwait(true);

        new FileInfo(segPath).Length.Should().Be(secondFrameStart,
            "a torn final frame is repaired by truncating at the last clean frame boundary");
    }

    /// <summary>
    /// A zero-filled tail — the file length reached disk, the frame bytes did not — is a torn tail
    /// at any length, including the realistic shape a torn value-log frame leaves, whose zero run
    /// is longer than one empty frame. Reopen truncates it, both acknowledged values stay readable,
    /// and the next append survives a further reopen.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(17)]
    [InlineData(21)]
    [InlineData(22)]
    [InlineData(64)]
    [InlineData(4096)]
    [InlineData(100 * 1024)]
    public async Task OpenAsync_ZeroFilledTail_TruncatesToLastCleanFrame(int zeros)
    {
        var (first, second, valueB) = await WriteTwoLargeValuesAsync(10 * 1024).ConfigureAwait(true);
        var segPath = ValueLogWriter.ResolveSegmentPath(_dir, 0);
        var cleanEnd = new FileInfo(segPath).Length;
        using (var fs = new FileStream(segPath, FileMode.Append, FileAccess.Write))
        {
            fs.Write(new byte[zeros]);
        }

        _manifest = new ValueLogManifest(_manifestPath);
        var open = async () =>
        {
            _writer = await ValueLogWriter.OpenAsync(_dir, _manifest, ValueLogOptions.Default).ConfigureAwait(false);
        };

        await open.Should().NotThrowAsync("an all-zero run to end of file holds no frame").ConfigureAwait(true);
        new FileInfo(segPath).Length.Should().Be(cleanEnd, "the zero-filled tail is truncated at the last clean frame");

        var third = await _writer!.AppendAsync(new byte[] { 1, 2, 3 }, 3ul).ConfigureAwait(true);
        await _writer.DisposeAsync().ConfigureAwait(true);
        _writer = null;
        await _manifest.DisposeAsync().ConfigureAwait(true);
        _manifest = new ValueLogManifest(_manifestPath);
        _writer = await ValueLogWriter.OpenAsync(_dir, _manifest, ValueLogOptions.Default).ConfigureAwait(true);

        using var reader = new ValueLogReader(_dir, ValueLogOptions.Default);
        reader.Read(second).Should().Equal(valueB);
        reader.Read(first).Should().HaveCount(10 * 1024);
        reader.Read(third).Should().Equal(new byte[] { 1, 2, 3 });
    }

    /// <summary>
    /// The must-fail-loud pair: the same zero run with one non-zero byte at its end is not an
    /// unwritten extent — something was written after the damaged frame — so reopen fails closed.
    /// </summary>
    [Theory]
    [InlineData(22)]
    [InlineData(4096)]
    public async Task OpenAsync_ZeroRunEndingInNonZeroByte_FailsClosed(int length)
    {
        await WriteTwoLargeValuesAsync(10 * 1024).ConfigureAwait(true);
        var segPath = ValueLogWriter.ResolveSegmentPath(_dir, 0);
        var tail = new byte[length];
        tail[^1] = 0x01;
        using (var fs = new FileStream(segPath, FileMode.Append, FileAccess.Write))
        {
            fs.Write(tail);
        }
        var lengthBefore = new FileInfo(segPath).Length;

        _manifest = new ValueLogManifest(_manifestPath);
        var open = async () =>
        {
            _writer = await ValueLogWriter.OpenAsync(_dir, _manifest, ValueLogOptions.Default).ConfigureAwait(false);
        };

        await open.Should().ThrowAsync<CorruptValueLogException>().ConfigureAwait(true);
        new FileInfo(segPath).Length.Should().Be(lengthBefore);
    }

    private async Task AssertOpenFailsClosedAsync(string segPath, long lengthBefore, ValueLogPointer second, byte[] valueB)
    {
        _manifest = new ValueLogManifest(_manifestPath);
        var openAct = async () =>
        {
            _writer = await ValueLogWriter.OpenAsync(_dir, _manifest, ValueLogOptions.Default).ConfigureAwait(false);
        };

        await openAct.Should().ThrowAsync<CorruptValueLogException>(
            "a damaged header with live frames after it is mid-file corruption, whatever their distance").ConfigureAwait(true);

        new FileInfo(segPath).Length.Should().Be(lengthBefore, "fail-closed recovery must not truncate live frames");

        using var reader = new ValueLogReader(_dir, ValueLogOptions.Default);
        reader.Read(second).Should().Equal(valueB, "the acknowledged second value must remain readable");
    }

    private async Task<(ValueLogPointer First, ValueLogPointer Second, byte[] ValueB)> WriteTwoLargeValuesAsync(int valueBytes)
    {
        var rng = new Random(20261017);
        var valueA = new byte[valueBytes];
        var valueB = new byte[valueBytes];
        rng.NextBytes(valueA);
        rng.NextBytes(valueB);

        _manifest = new ValueLogManifest(_manifestPath);
        _writer = await ValueLogWriter.OpenAsync(_dir, _manifest, ValueLogOptions.Default).ConfigureAwait(true);
        var first = await _writer.AppendAsync(valueA, 1ul).ConfigureAwait(true);
        var second = await _writer.AppendAsync(valueB, 2ul).ConfigureAwait(true);
        await _writer.DisposeAsync().ConfigureAwait(true);
        _writer = null;
        await _manifest.DisposeAsync().ConfigureAwait(true);
        _manifest = null;

        ((long)second.Offset - (long)first.Offset).Should().BeGreaterThan(8192,
            "the successor frame must start well beyond any small fixed scan window");
        return (first, second, valueB);
    }

    private static void OverwriteLength(string segPath, long frameStart, uint length)
    {
        var bytes = File.ReadAllBytes(segPath);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan((int)frameStart, 4), length);
        File.WriteAllBytes(segPath, bytes);
    }
}
