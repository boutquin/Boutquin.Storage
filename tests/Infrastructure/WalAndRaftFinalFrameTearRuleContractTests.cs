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
using WalType = Boutquin.Storage.Infrastructure.WriteAheadLog.WriteAheadLog<
    Boutquin.Storage.Domain.Helpers.SerializableWrapper<int>,
    Boutquin.Storage.Domain.Helpers.SerializableWrapper<string>>;

namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// The single final-frame tear rule, applied to the write-ahead log and the Raft log: a final frame
/// that fails its checksum is a torn tail only when its bytes are what one interrupted write leaves,
/// a prefix of the frame followed by zeros, so its last byte is unlanded. Both logs acknowledge an
/// append only after its fsync, so a final frame whose last byte landed was written completely; a
/// checksum failure there is damage to acknowledged history and fails loud with nothing truncated.
/// </summary>
public sealed class WalAndRaftFinalFrameTearRuleContractTests : IDisposable
{
    private const int WalFileHeaderSize = 8;
    private const int WalFrameHeaderSize = 18;
    private const int CrcSize = 4;
    private const int RaftHeaderSize = 25;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"WalRaftTearRule_{Guid.NewGuid():N}");

    public WalAndRaftFinalFrameTearRuleContractTests() => Directory.CreateDirectory(_dir);

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

    private string RaftPath => Path.Combine(_dir, "raft.bin");

    // ── write-ahead log ─────────────────────────────────────────────────────────

    /// <summary>
    /// Must-fail-loud: the final frame was appended and fsync'd alone and every one of its bytes
    /// landed (its last byte is non-zero), then one byte of its payload or of its stored payload
    /// checksum rotted. Recovery and the standalone-append repair both refuse; nothing is truncated.
    /// </summary>
    [Theory]
    [InlineData("payload-first-byte")]
    [InlineData("payload-middle-byte")]
    [InlineData("checksum-low-byte")]
    public async Task Wal_AcknowledgedFinalFrameWithLandedLastByte_FailingItsPayloadChecksum_FailsLoud(string damage)
    {
        var thirdStart = await WriteWalFramesAsync("value-1", "value-2", "value-three").ConfigureAwait(true);
        var bytes = await File.ReadAllBytesAsync(WalSegment).ConfigureAwait(true);
        bytes[^1].Should().NotBe(0, "the fixture needs a final frame whose last byte landed");
        var payloadStart = thirdStart + WalFrameHeaderSize;
        var damagedAt = damage switch
        {
            "payload-first-byte" => payloadStart,
            "payload-middle-byte" => payloadStart + ((bytes.Length - CrcSize - payloadStart) / 2),
            _ => bytes.Length - CrcSize,
        };
        bytes[damagedAt] ^= 0x01;
        await File.WriteAllBytesAsync(WalSegment, bytes).ConfigureAwait(true);

        using (var wal = new WalType(WalDirectory))
        {
            await FluentActions.Awaiting(() => wal.RecoverAsync()).Should().ThrowAsync<WriteAheadLogCorruptException>(
                "a frame whose last byte landed was written completely; failing its checksum is damage to an acknowledged write")
                .ConfigureAwait(true);
        }

        using (var wal = new WalType(WalDirectory))
        {
            await FluentActions.Awaiting(() => wal.AppendAsync(
                    new SerializableWrapper<int>(4), new SerializableWrapper<string>("fourth"), 4UL))
                .Should().ThrowAsync<WriteAheadLogCorruptException>(
                    "the standalone-append tail repair applies the same rule and must not erase the acknowledged frame")
                .ConfigureAwait(true);
        }

        (await File.ReadAllBytesAsync(WalSegment).ConfigureAwait(true)).Should().Equal(bytes,
            "a fail-loud recovery leaves every byte of the acknowledged final frame in place");
    }

    /// <summary>
    /// Must-fail-loud even when the final frame's genuine last byte is zero: the payload checksum's
    /// high byte is zero for about one frame in 256, so a zero last byte alone does not make a tear.
    /// The checksum bytes that landed must agree with the checksum of the landed payload.
    /// </summary>
    [Fact]
    public async Task Wal_AcknowledgedFinalFrameWhoseChecksumEndsInZero_FailingItsPayloadChecksum_FailsLoud()
    {
        var thirdValue = WalValueWhosePayloadChecksumEndsInZero();
        var thirdStart = await WriteWalFramesAsync("value-1", "value-2", thirdValue).ConfigureAwait(true);
        var bytes = await File.ReadAllBytesAsync(WalSegment).ConfigureAwait(true);
        bytes[^1].Should().Be(0, "the fixture needs a final frame whose genuine checksum ends in a zero byte");
        bytes[^2].Should().NotBe(0, "the fixture needs a landed, non-zero checksum byte before the zero one");
        bytes[thirdStart + WalFrameHeaderSize + 6] ^= 0x01;
        await File.WriteAllBytesAsync(WalSegment, bytes).ConfigureAwait(true);

        using var wal = new WalType(WalDirectory);
        await FluentActions.Awaiting(() => wal.RecoverAsync()).Should().ThrowAsync<WriteAheadLogCorruptException>(
            "the landed checksum bytes disagree with the landed payload, so this is not a prefix of what the writer wrote")
            .ConfigureAwait(true);
        new FileInfo(WalSegment).Length.Should().Be(bytes.Length, "nothing is truncated");
    }

    /// <summary>
    /// Must-fail-loud: the final frame's last byte is zero, but the checksum byte that landed
    /// before it disagrees with the payload — damage, not a prefix the writer could have left.
    /// </summary>
    [Fact]
    public async Task Wal_FinalFrameWithZeroLastByteAndADisagreeingLandedChecksumByte_FailsLoud()
    {
        await WriteWalFramesAsync("value-1", "value-2", "value-three").ConfigureAwait(true);
        var bytes = await File.ReadAllBytesAsync(WalSegment).ConfigureAwait(true);
        bytes[^1] = 0;
        bytes[^CrcSize] ^= 0x01;
        await File.WriteAllBytesAsync(WalSegment, bytes).ConfigureAwait(true);

        using var wal = new WalType(WalDirectory);
        await FluentActions.Awaiting(() => wal.RecoverAsync()).Should().ThrowAsync<WriteAheadLogCorruptException>()
            .ConfigureAwait(true);
        new FileInfo(WalSegment).Length.Should().Be(bytes.Length, "nothing is truncated");
    }

    /// <summary>
    /// Must-recover: the interrupted append landed a prefix of its payload and nothing after it, so
    /// the frame is a prefix followed by zeros — the one shape the tear rule repairs.
    /// </summary>
    [Fact]
    public async Task Wal_FinalFrameWhosePayloadSuffixAndChecksumNeverLanded_IsATornTail()
    {
        var thirdStart = await WriteWalFramesAsync("value-1", "value-2", "value-three").ConfigureAwait(true);
        var bytes = await File.ReadAllBytesAsync(WalSegment).ConfigureAwait(true);
        var unlandedFrom = thirdStart + WalFrameHeaderSize + 3;
        Array.Clear(bytes, unlandedFrom, bytes.Length - unlandedFrom);
        await File.WriteAllBytesAsync(WalSegment, bytes).ConfigureAwait(true);

        using var wal = new WalType(WalDirectory);
        var entries = await wal.RecoverAsync().ConfigureAwait(true);

        entries.Select(e => e.Key.Value).Should().Equal([1, 2], "the final frame is a prefix followed by zeros");
        new FileInfo(WalSegment).Length.Should().Be(thirdStart, "the torn tail is truncated at the last whole frame");
    }

    // ── Raft log ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Must-fail-loud: an fsync'd final entry whose command genuinely ends in a zero byte (a small
    /// little-endian integer) has one command byte rotted. Every byte of the frame landed, so the
    /// reopen throws and leaves the file untouched; whether the frame's last byte landed is decided
    /// by the frame format, never by the caller's command encoding.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(200)]
    public void Raft_DamagedFinalCommandWhoseGenuineLastByteIsZero_FailsLoud(int value)
    {
        var finalStart = WriteRaftEntries(1000, 2000, value);
        var bytes = File.ReadAllBytes(RaftPath);
        bytes[finalStart + RaftHeaderSize] ^= 0x01;
        File.WriteAllBytes(RaftPath, bytes);

        var act = () => OpenRaft().Dispose();

        act.Should().Throw<InvalidDataException>("the entry was fsync'd and acknowledged; its bytes all landed");
        File.ReadAllBytes(RaftPath).Should().Equal(bytes, "fail-loud recovery leaves the file untouched");
    }

    /// <summary>
    /// Must-fail-loud: an fsync'd final entry with an empty command whose command checksum rotted.
    /// The frame has no command bytes to decide "landed" from, and every byte of it landed.
    /// </summary>
    [Fact]
    public void Raft_DamagedFinalEmptyCommandFrame_FailsLoud()
    {
        int finalStart;
        using (var log = new FileRaftLog<byte[]>(RaftPath, b => b, b => b))
        {
            log.Append((1, [1, 2, 3]));
            finalStart = (int)new FileInfo(RaftPath).Length;
            log.Append((1, []));
        }

        var bytes = File.ReadAllBytes(RaftPath);
        bytes[finalStart + 17] ^= 0x01;
        File.WriteAllBytes(RaftPath, bytes);

        var act = () => new FileRaftLog<byte[]>(RaftPath, b => b, b => b).Dispose();

        act.Should().Throw<InvalidDataException>("every byte of the acknowledged empty-command frame landed");
        File.ReadAllBytes(RaftPath).Should().Equal(bytes, "fail-loud recovery leaves the file untouched");
    }

    /// <summary>
    /// Must-recover: the interrupted final append landed its header and a prefix of its command and
    /// nothing after it, so the frame is a prefix followed by zeros and is truncated as a torn tail.
    /// </summary>
    [Fact]
    public void Raft_FinalFrameWhoseCommandSuffixNeverLanded_IsATornTail()
    {
        var finalStart = WriteRaftEntries(1000, 2000, 0x0A0B0C0D);
        var bytes = File.ReadAllBytes(RaftPath);
        var unlandedFrom = finalStart + RaftHeaderSize + 2;
        Array.Clear(bytes, unlandedFrom, bytes.Length - unlandedFrom);
        File.WriteAllBytes(RaftPath, bytes);

        using var log = OpenRaft();

        log.Count.Should().Be(2, "the final frame is a prefix followed by zeros");
        new FileInfo(RaftPath).Length.Should().Be(finalStart, "the torn tail is truncated at the last whole frame");
    }

    private FileRaftLog<int> OpenRaft() => new(RaftPath, BitConverter.GetBytes, b => BitConverter.ToInt32(b));

    private int WriteRaftEntries(params int[] commands)
    {
        var finalStart = 0;
        using var log = OpenRaft();
        foreach (var command in commands)
        {
            finalStart = (int)new FileInfo(RaftPath).Length;
            log.Append((1, command));
        }

        return finalStart;
    }

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

    /// <summary>
    /// A value for key 3 whose WAL payload checksum has a zero high byte and a non-zero byte below
    /// it. The payload layout is read from a frame the WAL itself wrote; the value's trailing
    /// ASCII characters are the payload's last bytes, so candidates are scored by recomputing the
    /// checksum over that payload with the last characters replaced.
    /// </summary>
    private string WalValueWhosePayloadChecksumEndsInZero()
    {
        var probeDir = Path.Combine(_dir, "wal-probe");
        using (var wal = new WalType(probeDir))
        {
            wal.AppendAsync(new SerializableWrapper<int>(3), new SerializableWrapper<string>("v-aaaa"), 3UL)
                .GetAwaiter().GetResult();
        }

        var frame = File.ReadAllBytes(Path.Combine(probeDir, "wal_000001.log"));
        var payload = frame.AsSpan(
            WalFileHeaderSize + WalFrameHeaderSize,
            frame.Length - WalFileHeaderSize - WalFrameHeaderSize - CrcSize).ToArray();
        payload.AsSpan(payload.Length - 4).SequenceEqual("aaaa"u8).Should().BeTrue("the value's characters end the payload");
        for (var n = 0; n < 26 * 26 * 26 * 26; n++)
        {
            var suffix = new[]
            {
                (byte)('a' + (n % 26)), (byte)('a' + (n / 26 % 26)), (byte)('a' + (n / 676 % 26)), (byte)('a' + (n / 17576)),
            };
            suffix.CopyTo(payload, payload.Length - 4);
            var crc = Crc32C.HashToUInt32(payload);
            if (crc >> 24 == 0 && ((crc >> 16) & 0xFF) != 0)
            {
                return "v-" + Encoding.ASCII.GetString(suffix);
            }
        }

        throw new InvalidOperationException("no candidate value found");
    }
}
