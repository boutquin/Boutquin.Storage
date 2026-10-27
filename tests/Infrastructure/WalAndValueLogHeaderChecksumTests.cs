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

using WalType = Boutquin.Storage.Infrastructure.WriteAheadLog.WriteAheadLog<
    Boutquin.Storage.Domain.Helpers.SerializableWrapper<int>,
    Boutquin.Storage.Domain.Helpers.SerializableWrapper<string>>;

namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// The WAL frame header carries its own checksum, separate from the payload checksum, so a
/// rotted length prefix is detected as corruption instead of being inferred from the bytes
/// around it. Recovery's tear rule is then deterministic: a partial header at end of file, a
/// valid header whose payload is short at end of file, a final frame whose payload checksum
/// fails, or an all-zero run to end of file is a torn tail and is truncated; any other damage
/// fails loud with <see cref="WriteAheadLogCorruptException"/> and leaves the segment untouched.
/// Each case below is either a must-recover or a must-fail-loud shape. Frame boundaries come from
/// the segment length after each acknowledged append, so the tests do not depend on the frame
/// layout beyond the length prefix opening every frame.
/// </summary>
public sealed class WalAndValueLogHeaderChecksumTests : IDisposable
{
    private const int AcknowledgedFrames = 5;

    private readonly string _walDirectory = Path.Combine(
        Path.GetTempPath(), $"WalAndValueLogHeaderChecksumTests_{Guid.NewGuid():N}");

    public WalAndValueLogHeaderChecksumTests() => Directory.CreateDirectory(_walDirectory);

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

    // ── Must fail loud ───────────────────────────────────────────────────────

    /// <summary>
    /// One bit of rot in frame 2's length prefix (+65536) plus a torn sixth append. Frames 3..5 are
    /// acknowledged history behind the rot, so recovery must throw and keep every byte rather than
    /// truncate at frame 2 and silently discard keys 2..5.
    /// </summary>
    [Fact]
    public async Task RecoverAsync_LengthRotPlusTornFinalAppend_ThrowsAndPreservesSegment()
    {
        var ends = await WriteAcknowledgedFramesAsync(tornSixth: true).ConfigureAwait(true);
        var bytes = await File.ReadAllBytesAsync(SegmentPath).ConfigureAwait(true);
        bytes[ends[0] + 2] ^= 0x01;
        var damaged = bytes[..^150];
        await File.WriteAllBytesAsync(SegmentPath, damaged).ConfigureAwait(true);

        using (var wal = new WalType(_walDirectory))
        {
            var act = () => wal.RecoverAsync();
            await act.Should().ThrowAsync<WriteAheadLogCorruptException>(
                "frames 3..5 are acknowledged history behind the rotted length prefix").ConfigureAwait(true);
        }

        (await File.ReadAllBytesAsync(SegmentPath).ConfigureAwait(true)).Should().Equal(
            damaged, "fail-loud recovery must not truncate the acknowledged frames behind the rot");
    }

    /// <summary>
    /// Same image, but the consumer appends without recovering first: the append-side tail repair
    /// applies the same rule and refuses instead of truncating keys 2..5 away.
    /// </summary>
    [Fact]
    public async Task StandaloneAppend_LengthRotPlusTornFinalAppend_FailsWithoutTruncating()
    {
        var ends = await WriteAcknowledgedFramesAsync(tornSixth: true).ConfigureAwait(true);
        var bytes = await File.ReadAllBytesAsync(SegmentPath).ConfigureAwait(true);
        bytes[ends[0] + 2] ^= 0x01;
        var damaged = bytes[..^150];
        await File.WriteAllBytesAsync(SegmentPath, damaged).ConfigureAwait(true);

        using (var wal = new WalType(_walDirectory))
        {
            var act = () => wal.AppendAsync(new SerializableWrapper<int>(7), new SerializableWrapper<string>("seven"), 7UL);
            await act.Should().ThrowAsync<WriteAheadLogCorruptException>(
                "the tail repair must not destroy acknowledged frames behind the rot").ConfigureAwait(true);
        }

        (await File.ReadAllBytesAsync(SegmentPath).ConfigureAwait(true)).Should().Equal(damaged);
    }

    /// <summary>
    /// Header rot in a non-final frame — length overstated, length understated, kind byte, or seqno
    /// byte — with or without a torn final append, is corruption of acknowledged history.
    /// </summary>
    [Theory]
    [InlineData("length-overstated", false)]
    [InlineData("length-overstated", true)]
    [InlineData("length-understated", false)]
    [InlineData("length-understated", true)]
    [InlineData("kind", true)]
    [InlineData("seqno", true)]
    [InlineData("payload", true)]
    public async Task RecoverAsync_RotInNonFinalFrame_ThrowsAndPreservesSegment(string rot, bool tornSixth)
    {
        var ends = await WriteAcknowledgedFramesAsync(tornSixth).ConfigureAwait(true);
        var bytes = await File.ReadAllBytesAsync(SegmentPath).ConfigureAwait(true);
        var frame2 = (int)ends[0];
        switch (rot)
        {
            case "length-overstated":
                bytes[frame2 + 1] ^= 0x01; // +256
                break;
            case "length-understated":
                bytes[frame2] -= 2; // the payloads are longer than 2 bytes, so this lowers the length
                break;
            case "kind":
                bytes[frame2 + 4] ^= 0x01;
                break;
            case "seqno":
                bytes[frame2 + 6] ^= 0x10;
                break;
            default:
                bytes[(int)ends[1] - 5] ^= 0x01; // the last payload byte of frame 2
                break;
        }

        var damaged = tornSixth ? bytes[..^150] : bytes;
        await File.WriteAllBytesAsync(SegmentPath, damaged).ConfigureAwait(true);

        using (var wal = new WalType(_walDirectory))
        {
            var act = () => wal.RecoverAsync();
            await act.Should().ThrowAsync<WriteAheadLogCorruptException>().ConfigureAwait(true);
        }

        (await File.ReadAllBytesAsync(SegmentPath).ConfigureAwait(true)).Should().Equal(damaged);
    }

    /// <summary>
    /// A rotted length prefix on the FINAL acknowledged frame fails its header checksum; the header
    /// is complete, so it is not a tear. Recovery fails loud instead of dropping the frame.
    /// </summary>
    [Fact]
    public async Task RecoverAsync_LengthRotOnFinalFrame_ThrowsAndPreservesSegment()
    {
        var ends = await WriteAcknowledgedFramesAsync(tornSixth: false).ConfigureAwait(true);
        var bytes = await File.ReadAllBytesAsync(SegmentPath).ConfigureAwait(true);
        bytes[ends[^2] + 1] ^= 0x01;
        await File.WriteAllBytesAsync(SegmentPath, bytes).ConfigureAwait(true);

        using (var wal = new WalType(_walDirectory))
        {
            var act = () => wal.RecoverAsync();
            await act.Should().ThrowAsync<WriteAheadLogCorruptException>().ConfigureAwait(true);
        }

        (await File.ReadAllBytesAsync(SegmentPath).ConfigureAwait(true)).Should().Equal(bytes);
    }

    // ── Must recover ─────────────────────────────────────────────────────────

    /// <summary>
    /// A torn sixth append, cut anywhere — inside its header or inside its payload — is a torn
    /// tail: recovery returns keys 1..5 and truncates at the end of frame 5.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(17)]
    [InlineData(18)]
    [InlineData(19)]
    [InlineData(150)]
    [InlineData(300)]
    public async Task RecoverAsync_TornSixthAppend_RecoversAcknowledgedPrefix(int keptBytesOfSixth)
    {
        var ends = await WriteAcknowledgedFramesAsync(tornSixth: true).ConfigureAwait(true);
        var bytes = await File.ReadAllBytesAsync(SegmentPath).ConfigureAwait(true);
        var cleanEnd = (int)ends[^1];
        await File.WriteAllBytesAsync(SegmentPath, bytes[..(cleanEnd + keptBytesOfSixth)]).ConfigureAwait(true);

        await AssertRecoversKeysOneToFiveAsync(cleanEnd).ConfigureAwait(true);
    }

    /// <summary>
    /// A zero-filled tail of any length — the file length reached disk, the frame bytes did not —
    /// is a torn tail, including runs longer than a frame header.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(17)]
    [InlineData(18)]
    [InlineData(22)]
    [InlineData(64)]
    [InlineData(4096)]
    public async Task RecoverAsync_ZeroFilledTail_RecoversAcknowledgedPrefix(int zeros)
    {
        var ends = await WriteAcknowledgedFramesAsync(tornSixth: false).ConfigureAwait(true);
        using (var fs = new FileStream(SegmentPath, FileMode.Append, FileAccess.Write))
        {
            fs.Write(new byte[zeros]);
        }

        await AssertRecoversKeysOneToFiveAsync(ends[^1]).ConfigureAwait(true);
    }

    /// <summary>
    /// A torn sixth append whose header landed intact but whose payload is partial — its suffix and
    /// checksum trailer still zero — cut exactly at the frame's full length, fails only its payload
    /// checksum at end of file: torn.
    /// </summary>
    [Fact]
    public async Task RecoverAsync_FullLengthFinalFrameWithBadPayloadChecksum_TruncatesTornTail()
    {
        var ends = await WriteAcknowledgedFramesAsync(tornSixth: true).ConfigureAwait(true);
        var bytes = await File.ReadAllBytesAsync(SegmentPath).ConfigureAwait(true);
        const int unlanded = 150;
        Array.Clear(bytes, bytes.Length - unlanded, unlanded);
        await File.WriteAllBytesAsync(SegmentPath, bytes).ConfigureAwait(true);

        await AssertRecoversKeysOneToFiveAsync(ends[^1]).ConfigureAwait(true);
    }

    // ── Interrupted write whose landed bytes stop inside a frame, zeros after ─

    /// <summary>
    /// A final append whose landed bytes stop inside its 18-byte frame header, with zeros from there
    /// to end of file (the file length reached disk, the rest of the frame did not), is a torn tail —
    /// with or without further zero bytes beyond the frame's own extent.
    /// </summary>
    [Theory]
    [InlineData(1, 0)]
    [InlineData(9, 0)]
    [InlineData(9, 4096)]
    [InlineData(13, 64)]
    [InlineData(17, 0)]
    public async Task RecoverAsync_FinalAppendTornInsideItsHeaderThenZeros_RecoversAcknowledgedPrefix(
        int landedHeaderBytes, int extraZeros)
    {
        var ends = await WriteAcknowledgedFramesAsync(tornSixth: false).ConfigureAwait(true);
        var cleanEnd = (int)ends[^1];
        await AppendFramesAsync(firstKey: 6, count: 1).ConfigureAwait(true);

        var bytes = await File.ReadAllBytesAsync(SegmentPath).ConfigureAwait(true);
        Array.Clear(bytes, cleanEnd + landedHeaderBytes, bytes.Length - (cleanEnd + landedHeaderBytes));
        await File.WriteAllBytesAsync(SegmentPath, [.. bytes, .. new byte[extraZeros]]).ConfigureAwait(true);

        await AssertRecoversKeysOneToFiveAsync(cleanEnd).ConfigureAwait(true);
    }

    /// <summary>
    /// Group commit writes every frame of a batch and then fsyncs once. A crash before that fsync can
    /// leave the batch's first frame with an intact header and a partial payload, and zeros through
    /// the rest of the batch. Nothing in the batch was acknowledged and no frame follows the damage,
    /// so it is a torn tail, not "a payload checksum mismatch with frames after it".
    /// </summary>
    [Fact]
    public async Task RecoverAsync_GroupCommitBatchTornAfterFirstFrameHeader_RecoversAcknowledgedPrefix()
    {
        var ends = await WriteAcknowledgedFramesAsync(tornSixth: false).ConfigureAwait(true);
        var cleanEnd = (int)ends[^1];
        await AppendFramesAsync(firstKey: 6, count: 3).ConfigureAwait(true);

        var bytes = await File.ReadAllBytesAsync(SegmentPath).ConfigureAwait(true);
        const int landed = 18 + 2; // the first frame's whole header and two payload bytes
        Array.Clear(bytes, cleanEnd + landed, bytes.Length - (cleanEnd + landed));
        await File.WriteAllBytesAsync(SegmentPath, bytes).ConfigureAwait(true);

        await AssertRecoversKeysOneToFiveAsync(cleanEnd).ConfigureAwait(true);
    }

    /// <summary>
    /// The landed prefix of an unacknowledged two-frame batch may end anywhere inside its first frame —
    /// in the header or in the payload — with zeros covering the rest of that frame and all of its
    /// batch-mate. Every such image is a single interrupted write and recovers the acknowledged prefix.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(17)]
    [InlineData(68)]
    public async Task RecoverAsync_BatchLandedPrefixEndingInsideFirstFrameThenZeros_RecoversAcknowledgedPrefix(int landed)
    {
        var ends = await WriteAcknowledgedFramesAsync(tornSixth: false).ConfigureAwait(true);
        var cleanEnd = (int)ends[^1];
        await AppendFramesAsync(firstKey: 6, count: 2).ConfigureAwait(true);

        var bytes = await File.ReadAllBytesAsync(SegmentPath).ConfigureAwait(true);
        Array.Clear(bytes, cleanEnd + landed, bytes.Length - (cleanEnd + landed));
        await File.WriteAllBytesAsync(SegmentPath, bytes).ConfigureAwait(true);

        await AssertRecoversKeysOneToFiveAsync(cleanEnd).ConfigureAwait(true);
    }

    /// <summary>
    /// The zero-tail tear rule is not a licence to skip: a header prefix followed by zeros and then
    /// NON-zero bytes (a later frame of the batch landed while an earlier page did not) is not a single
    /// interrupted write, and a complete header that fails its checksum with zeros after it is header
    /// rot, not a strict prefix. Both still fail loud and leave the segment untouched.
    /// </summary>
    [Theory]
    [InlineData("prefix-zeros-then-frame")]
    [InlineData("complete-header-rot-then-zeros")]
    public async Task RecoverAsync_DamageThatIsNotASingleInterruptedWrite_ThrowsAndPreservesSegment(string shape)
    {
        var ends = await WriteAcknowledgedFramesAsync(tornSixth: false).ConfigureAwait(true);
        var cleanEnd = (int)ends[^1];
        await AppendFramesAsync(firstKey: 6, count: 2).ConfigureAwait(true);
        var bytes = await File.ReadAllBytesAsync(SegmentPath).ConfigureAwait(true);
        var frameSize = (bytes.Length - cleanEnd) / 2;

        if (shape == "prefix-zeros-then-frame")
        {
            // Frame 6 keeps 9 header bytes, the rest of frame 6 is zero, frame 7 is intact.
            Array.Clear(bytes, cleanEnd + 9, frameSize - 9);
        }
        else
        {
            // Frame 6's whole header landed (its last checksum byte is non-zero) but a length bit
            // rotted; everything after the header is zero.
            bytes[cleanEnd + 17].Should().NotBe(0, "precondition: the header's last byte distinguishes a complete header");
            bytes[cleanEnd + 1] ^= 0x01;
            Array.Clear(bytes, cleanEnd + 18, bytes.Length - (cleanEnd + 18));
        }

        await File.WriteAllBytesAsync(SegmentPath, bytes).ConfigureAwait(true);

        using (var wal = new WalType(_walDirectory))
        {
            var act = () => wal.RecoverAsync();
            await act.Should().ThrowAsync<WriteAheadLogCorruptException>().ConfigureAwait(true);
        }

        (await File.ReadAllBytesAsync(SegmentPath).ConfigureAwait(true)).Should().Equal(bytes);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Appends <paramref name="count"/> equal-size frames (keys from <paramref name="firstKey"/>, 60-character
    /// values) through the production writer; the caller then shapes the bytes into a crash image.
    /// </summary>
    private async Task AppendFramesAsync(int firstKey, int count)
    {
        using var wal = new WalType(_walDirectory);
        for (var k = firstKey; k < firstKey + count; k++)
        {
            await wal.AppendAsync(
                new SerializableWrapper<int>(k), new SerializableWrapper<string>(new string('u', 60)), (ulong)k).ConfigureAwait(true);
        }
    }

    private async Task AssertRecoversKeysOneToFiveAsync(long cleanEnd)
    {
        using (var wal = new WalType(_walDirectory))
        {
            IReadOnlyList<WalEntry<SerializableWrapper<int>, SerializableWrapper<string>>> entries = null;
            var caught = await Record.ExceptionAsync(async () => entries = await wal.RecoverAsync().ConfigureAwait(true))
                .ConfigureAwait(true);
            caught.Should().BeNull("an interrupted final write is a torn tail, not corruption of acknowledged history");
            entries.Select(e => e.Key.Value).Should().Equal([1, 2, 3, 4, 5]);
        }

        new FileInfo(SegmentPath).Length.Should().Be(cleanEnd, "the torn tail is truncated at the last acknowledged frame");

        using (var wal = new WalType(_walDirectory))
        {
            await wal.AppendAsync(new SerializableWrapper<int>(8), new SerializableWrapper<string>("eight"), 8UL).ConfigureAwait(true);
        }

        using (var wal = new WalType(_walDirectory))
        {
            var entries = await wal.RecoverAsync().ConfigureAwait(true);
            entries.Select(e => e.Key.Value).Should().Equal([1, 2, 3, 4, 5, 8]);
        }
    }

    /// <summary>
    /// Appends keys 1..5 (and, when <paramref name="tornSixth"/>, a 300-character sixth frame) and
    /// returns the segment length after each of the first five appends — frame k+1 starts at
    /// element k.
    /// </summary>
    private async Task<List<long>> WriteAcknowledgedFramesAsync(bool tornSixth)
    {
        var ends = new List<long>();
        using var wal = new WalType(_walDirectory);
        for (var k = 1; k <= AcknowledgedFrames; k++)
        {
            await wal.AppendAsync(
                new SerializableWrapper<int>(k), new SerializableWrapper<string>($"acknowledged-value-{k}"), (ulong)k).ConfigureAwait(true);
            ends.Add(new FileInfo(SegmentPath).Length);
        }

        if (tornSixth)
        {
            await wal.AppendAsync(
                new SerializableWrapper<int>(6), new SerializableWrapper<string>(new string('t', 300)), 6UL).ConfigureAwait(true);
        }

        return ends;
    }
}
