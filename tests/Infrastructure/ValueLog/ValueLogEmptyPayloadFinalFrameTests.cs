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
/// The value-log tear rule reads a header checksum failure followed only by zeros as an unwritten
/// tail. That is sound only if no complete frame is zero after its header. A frame with an empty
/// payload would be: its payload checksum is CRC32C of zero bytes, which is 0, so a single bit of
/// rot in the header of an acknowledged final empty frame would be truncated as a crash tear. The
/// writer therefore refuses empty payloads, and every complete frame carries a non-zero byte after
/// its header — so a complete header that fails its checksum on the final acknowledged frame fails
/// loud, while a genuinely torn header prefix still recovers.
/// </summary>
public sealed class ValueLogEmptyPayloadFinalFrameTests : IAsyncDisposable
{
    private readonly string _dir;
    private readonly string _manifestPath;
    private ValueLogManifest? _manifest;
    private ValueLogWriter? _writer;

    public ValueLogEmptyPayloadFinalFrameTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vl-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _manifestPath = Path.Combine(_dir, "valuelog.manifest");
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(true);
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task AppendAsync_EmptyPayload_IsRefusedAndLeavesSegmentUnchanged()
    {
        _manifest = new ValueLogManifest(_manifestPath);
        _writer = await ValueLogWriter.OpenAsync(_dir, _manifest, ValueLogOptions.Default).ConfigureAwait(true);
        var first = await _writer.AppendAsync(new byte[] { 1, 2, 3 }, 1ul).ConfigureAwait(true);
        var segPath = ValueLogWriter.ResolveSegmentPath(_dir, 0);
        var bytesBefore = _writer.CurrentFileBytes;

        var append = async () => await _writer.AppendAsync(ReadOnlyMemory<byte>.Empty, 2ul).ConfigureAwait(false);

        await append.Should().ThrowAsync<ArgumentException>(
            "an empty frame is zero after its header, so header rot on it would read as a crash tear").ConfigureAwait(true);
        _writer.CurrentFileBytes.Should().Be(bytesBefore, "a refused append writes nothing");

        // The writer stays usable, and nothing of the refused append reached the segment.
        var second = await _writer.AppendAsync(new byte[] { 4 }, 3ul).ConfigureAwait(true);
        await CloseAsync().ConfigureAwait(true);
        new FileInfo(segPath).Length.Should().Be(bytesBefore + ValueLogFrame.FrameSize(1));

        using var reader = new ValueLogReader(_dir, ValueLogOptions.Default);
        reader.Read(first).Should().Equal(1, 2, 3);
        reader.Read(second).Should().Equal(4);
    }

    /// <summary>
    /// Any single-bit rot in the complete header of the final acknowledged frame — including the
    /// smallest frame the writer accepts and one whose payload is all zeros — fails loud and leaves
    /// the file untouched.
    /// </summary>
    [Theory]
    [InlineData(1, (byte)0)]
    [InlineData(1, (byte)0x5A)]
    [InlineData(64, (byte)0)]
    public async Task OpenAsync_HeaderBitRotOnFinalAcknowledgedFrame_FailsLoudAndKeepsFile(int payloadBytes, byte fill)
    {
        var (finalFrameStart, segPath) = await WriteValuesThenFinalAsync(payloadBytes, fill).ConfigureAwait(true);
        var original = await File.ReadAllBytesAsync(segPath).ConfigureAwait(true);

        for (var bit = 0; bit < ValueLogFrame.EntryHeaderSize * 8; bit++)
        {
            var damaged = (byte[])original.Clone();
            damaged[finalFrameStart + (bit / 8)] ^= (byte)(1 << (bit % 8));
            await File.WriteAllBytesAsync(segPath, damaged).ConfigureAwait(true);

            _manifest = new ValueLogManifest(_manifestPath);
            var open = async () =>
            {
                _writer = await ValueLogWriter.OpenAsync(_dir, _manifest, ValueLogOptions.Default).ConfigureAwait(false);
            };

            await open.Should().ThrowAsync<CorruptValueLogException>(
                $"header bit {bit} of a complete, acknowledged final frame is damage, not a crash tear").ConfigureAwait(true);
            await CloseAsync().ConfigureAwait(true);
            (await File.ReadAllBytesAsync(segPath).ConfigureAwait(true)).Should().Equal(damaged,
                $"fail-loud recovery leaves the file untouched (header bit {bit})");
        }
    }

    /// <summary>
    /// A torn final append whose landed bytes stop anywhere inside its header — the rest of the
    /// frame zeros to end of file — is a crash tear: reopen truncates it and every earlier
    /// acknowledged value stays readable.
    /// </summary>
    [Fact]
    public async Task OpenAsync_TornHeaderPrefixOfFinalAppend_RecoversEarlierFrames()
    {
        var (finalFrameStart, segPath) = await WriteValuesThenFinalAsync(1, 0x5A).ConfigureAwait(true);
        var original = await File.ReadAllBytesAsync(segPath).ConfigureAwait(true);

        for (var landed = 0; landed < ValueLogFrame.EntryHeaderSize; landed++)
        {
            var torn = (byte[])original.Clone();
            Array.Clear(torn, finalFrameStart + landed, torn.Length - (finalFrameStart + landed));
            await File.WriteAllBytesAsync(segPath, torn).ConfigureAwait(true);

            _manifest = new ValueLogManifest(_manifestPath);
            _writer = await ValueLogWriter.OpenAsync(_dir, _manifest, ValueLogOptions.Default).ConfigureAwait(true);
            await CloseAsync().ConfigureAwait(true);

            new FileInfo(segPath).Length.Should().Be(finalFrameStart,
                $"a header prefix of {landed} landed bytes followed by zeros is the interrupted final append");
            using var reader = new ValueLogReader(_dir, ValueLogOptions.Default);
            for (var i = 0; i < 3; i++)
            {
                reader.Read(new ValueLogPointer(0, (ulong)(ValueLogFrame.HeaderSize + (i * ValueLogFrame.FrameSize(300))), (ulong)(i + 1)))
                    .Should().Equal(Value(i));
            }
        }
    }

    private async Task<(int FinalFrameStart, string SegPath)> WriteValuesThenFinalAsync(int payloadBytes, byte fill)
    {
        _manifest = new ValueLogManifest(_manifestPath);
        _writer = await ValueLogWriter.OpenAsync(_dir, _manifest, ValueLogOptions.Default).ConfigureAwait(true);
        for (var i = 0; i < 3; i++)
        {
            await _writer.AppendAsync(Value(i), (ulong)(i + 1)).ConfigureAwait(true);
        }

        var payload = new byte[payloadBytes];
        Array.Fill(payload, fill);
        var final = await _writer.AppendAsync(payload, 4ul).ConfigureAwait(true);
        await CloseAsync().ConfigureAwait(true);
        return ((int)final.Offset, ValueLogWriter.ResolveSegmentPath(_dir, 0));
    }

    private static byte[] Value(int i)
    {
        var value = new byte[300];
        new Random(20261019 + i).NextBytes(value);
        return value;
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
}
