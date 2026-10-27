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
using System.Reflection;
using Boutquin.Storage.Domain.ValueObjects.ValueLog;
using Boutquin.Storage.Infrastructure.ValueLog;

using WalType = Boutquin.Storage.Infrastructure.WriteAheadLog.WriteAheadLog<
    Boutquin.Storage.Domain.Helpers.SerializableWrapper<int>,
    Boutquin.Storage.Domain.Helpers.SerializableWrapper<string>>;

namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// The final-frame tear rule for logs whose frames end in a payload checksum trailer: a final frame
/// that fails its payload checksum is a torn tail only when its trailer is what an interrupted write
/// leaves — its last byte unlanded (zero) and every trailer byte that did land equal to the checksum
/// the landed payload produces — with nothing but zeros after it. The value log appends and fsyncs
/// one frame before returning its pointer, so a final frame whose last byte landed was acknowledged:
/// a checksum failure there fails loud with <see cref="CorruptValueLogException"/> and the segment
/// keeps every byte. The write-ahead log still repairs a final frame whose checksum trailer only
/// partly landed, and never lets a batch whose fsync failed — reported to its callers as failed —
/// become durable history on the next recovery.
/// </summary>
public sealed class RecoveryFinalFrameTearRuleContractTests : IDisposable
{
    private const int WalFileHeaderSize = 8;
    private const int CrcSize = 4;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"FinalFrameTearRule_{Guid.NewGuid():N}");

    public RecoveryFinalFrameTearRuleContractTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }

    private string WalDirectory => Path.Combine(_dir, "wal");

    private string WalSegment => Path.Combine(WalDirectory, "wal_000001.log");

    // ── write-ahead log ─────────────────────────────────────────────────────────

    /// <summary>
    /// Must-recover: the interrupted append landed its whole payload and only a prefix of its
    /// checksum trailer (the landed checksum bytes match the payload), so the frame is a torn tail.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(CrcSize)]
    public async Task Wal_FinalFrameWhoseChecksumTrailerOnlyPartlyLanded_IsATornTail(int unlandedBytes)
    {
        var thirdStart = await WriteWalFramesAsync("value-1", "value-2", "value-three").ConfigureAwait(true);
        var bytes = await File.ReadAllBytesAsync(WalSegment).ConfigureAwait(true);
        Array.Clear(bytes, bytes.Length - unlandedBytes, unlandedBytes);
        await File.WriteAllBytesAsync(WalSegment, bytes).ConfigureAwait(true);

        using var wal = new WalType(WalDirectory);
        var entries = await wal.RecoverAsync().ConfigureAwait(true);

        entries.Select(e => e.Key.Value).Should().Equal([1, 2], "the frame's trailer is an unlanded suffix");
        new FileInfo(WalSegment).Length.Should().Be(thirdStart, "the torn tail is truncated at the last whole frame");
    }

    /// <summary>
    /// A batch whose frames reached the segment but whose fsync failed is reported failed to every
    /// caller in it. Its frames must not become durable history: the next recovery returns only the
    /// acknowledged writes before it.
    /// </summary>
    [Fact]
    public async Task Wal_BatchWhoseFsyncFailed_IsNotReplayedByTheNextRecovery()
    {
        var options = new WriteAheadLogOptions { GroupCommitBatchSize = 1 };
        Exception failed;
        using (var wal = new WalType(WalDirectory, options))
        {
            await wal.AppendAsync(
                new SerializableWrapper<int>(1), new SerializableWrapper<string>("acknowledged-1"), 1UL).ConfigureAwait(true);

            var streamField = typeof(WalType).GetField("_appendStream", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var original = (FileStream)streamField.GetValue(wal)!;
            original.Dispose();
            streamField.SetValue(wal, new FsyncFailingStream(WalSegment));

            failed = await Record.ExceptionAsync(() => wal.AppendAsync(
                new SerializableWrapper<int>(2), new SerializableWrapper<string>("reported-failed-2"), 2UL)).ConfigureAwait(true);
        }

        failed.Should().BeOfType<WriteAheadLogCorruptException>("the batch's fsync failed, so its append is reported failed");

        using var reader = new WalType(WalDirectory);
        var entries = await reader.RecoverAsync().ConfigureAwait(true);
        entries.Select(e => e.Key.Value).Should().Equal([1],
            "an append its caller saw fail must not reappear as acknowledged history after a restart");
    }

    // ── value log ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Must-fail-loud: the value log appends and fsyncs one frame per call before returning its
    /// pointer, so a final frame whose last byte landed was acknowledged. Payload rot there throws
    /// <see cref="CorruptValueLogException"/> on open and the segment keeps every byte.
    /// </summary>
    [Theory]
    [InlineData("payload-first-byte")]
    [InlineData("payload-middle-byte")]
    [InlineData("checksum-low-byte")]
    public async Task ValueLog_AcknowledgedFinalFrameWithLandedLastByte_FailingItsPayloadChecksum_FailsLoud(string damage)
    {
        var (valueLogDir, manifestPath, segment) = await WriteValueLogFramesAsync().ConfigureAwait(true);
        var bytes = await File.ReadAllBytesAsync(segment).ConfigureAwait(true);
        bytes[^1].Should().NotBe(0, "the fixture needs a final frame whose last byte landed");
        var finalStart = bytes.Length - (int)ValueLogFrame.FrameSize(40);
        bytes[damage switch
        {
            "payload-first-byte" => finalStart + ValueLogFrame.EntryHeaderSize,
            "payload-middle-byte" => finalStart + ValueLogFrame.EntryHeaderSize + 20,
            _ => bytes.Length - ValueLogFrame.EntryCrcSize,
        }] ^= 0x01;
        await File.WriteAllBytesAsync(segment, bytes).ConfigureAwait(true);

        await FluentActions.Awaiting(async () =>
            {
                await using var manifest = new ValueLogManifest(manifestPath);
                await using var writer = await ValueLogWriter.OpenAsync(
                    valueLogDir, manifest, ValueLogOptions.Default).ConfigureAwait(true);
            })
            .Should().ThrowAsync<CorruptValueLogException>(
                "a frame whose last byte landed was fsync'd before its pointer was returned; failing its checksum is damage")
            .ConfigureAwait(true);
        (await File.ReadAllBytesAsync(segment).ConfigureAwait(true)).Should().Equal(bytes,
            "a fail-loud open leaves the acknowledged final frame in place");
    }

    /// <summary>
    /// Must-recover: the interrupted value-log append landed its payload and only a prefix of its
    /// checksum trailer, so the frame is a torn tail and the earlier pointers stay readable.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(ValueLogFrame.EntryCrcSize)]
    public async Task ValueLog_FinalFrameWhoseChecksumTrailerOnlyPartlyLanded_IsATornTail(int unlandedBytes)
    {
        var (valueLogDir, manifestPath, segment) = await WriteValueLogFramesAsync().ConfigureAwait(true);
        var bytes = await File.ReadAllBytesAsync(segment).ConfigureAwait(true);
        var finalStart = bytes.Length - (int)ValueLogFrame.FrameSize(40);
        Array.Clear(bytes, bytes.Length - unlandedBytes, unlandedBytes);
        await File.WriteAllBytesAsync(segment, bytes).ConfigureAwait(true);

        await using (var manifest = new ValueLogManifest(manifestPath))
        await using (await ValueLogWriter.OpenAsync(
            valueLogDir, manifest, ValueLogOptions.Default).ConfigureAwait(true))
        {
        }

        new FileInfo(segment).Length.Should().Be(finalStart, "the torn final frame is truncated back to the last good frame");
    }

    /// <summary>
    /// Must-fail-loud: the value-log frame's last byte is zero but the checksum byte that landed
    /// before it disagrees with the payload.
    /// </summary>
    [Fact]
    public async Task ValueLog_FinalFrameWithZeroLastByteAndADisagreeingLandedChecksumByte_FailsLoud()
    {
        var (valueLogDir, manifestPath, segment) = await WriteValueLogFramesAsync().ConfigureAwait(true);
        var bytes = await File.ReadAllBytesAsync(segment).ConfigureAwait(true);
        bytes[^1] = 0;
        bytes[^ValueLogFrame.EntryCrcSize] ^= 0x01;
        await File.WriteAllBytesAsync(segment, bytes).ConfigureAwait(true);

        await FluentActions.Awaiting(async () =>
            {
                await using var manifest = new ValueLogManifest(manifestPath);
                await using var writer = await ValueLogWriter.OpenAsync(
                    valueLogDir, manifest, ValueLogOptions.Default).ConfigureAwait(true);
            })
            .Should().ThrowAsync<CorruptValueLogException>()
            .ConfigureAwait(true);
        new FileInfo(segment).Length.Should().Be(bytes.Length, "nothing is truncated");
    }

    // ── helpers ─────────────────────────────────────────────────────────────────

    /// <summary>Appends one frame per value (keys 1..n) and returns the final frame's start offset.</summary>
    private async Task<int> WriteWalFramesAsync(params string[] values)
    {
        var finalStart = 0;
        using var wal = new WalType(WalDirectory);
        for (var i = 0; i < values.Length; i++)
        {
            finalStart = File.Exists(WalSegment) ? (int)new FileInfo(WalSegment).Length : WalFileHeaderSize;
            await wal.AppendAsync(
                new SerializableWrapper<int>(i + 1), new SerializableWrapper<string>(values[i]), (ulong)(i + 1)).ConfigureAwait(true);
        }

        return finalStart;
    }

    private async Task<(string Dir, string Manifest, string Segment)> WriteValueLogFramesAsync()
    {
        var valueLogDir = Path.Combine(_dir, "vlog");
        var manifestPath = Path.Combine(valueLogDir, "valuelog.manifest");
        await using (var manifest = new ValueLogManifest(manifestPath))
        await using (var writer = await ValueLogWriter.OpenAsync(
            valueLogDir, manifest, ValueLogOptions.Default).ConfigureAwait(true))
        {
            for (var i = 1; i <= 3; i++)
            {
                await writer.AppendAsync(Enumerable.Range(0, 40).Select(b => (byte)(b + i + 1)).ToArray(), (ulong)i)
                    .ConfigureAwait(true);
            }
        }

        return (valueLogDir, manifestPath, ValueLogWriter.ResolveSegmentPath(valueLogDir, 0));
    }

    /// <summary>
    /// Append stream whose data reaches the file but whose fsync fails — the EIO an fsync returns
    /// after the written pages are already in the OS cache.
    /// </summary>
    private sealed class FsyncFailingStream(string path)
        : FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 4096, useAsync: true)
    {
        public override void Flush(bool flushToDisk)
        {
            base.Flush(false);
            if (flushToDisk)
            {
                throw new IOException("injected fsync failure (EIO)");
            }
        }
    }
}
