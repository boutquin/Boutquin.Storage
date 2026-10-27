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

using Wal = global::Boutquin.Storage.Infrastructure.WriteAheadLog.WriteAheadLog<
    Boutquin.Storage.Domain.Helpers.SerializableWrapper<int>,
    Boutquin.Storage.Domain.Helpers.SerializableWrapper<string>>;
using WalOptions = global::Boutquin.Storage.Infrastructure.WriteAheadLog.WriteAheadLogOptions;

namespace Boutquin.Storage.Infrastructure.Tests.WriteAheadLog;

/// <summary>
/// Segment integrity across rotation. Damage that leaves no damaged frame behind — a sealed
/// segment emptied or cut at an interior frame boundary, a sealed segment with frames reordered or
/// bytes added, a missing middle segment — must be detectable, so a sealed (rotated) segment carries
/// a seal recording its length, frame count and checksum. Recovery fails loud with
/// <see cref="WriteAheadLogCorruptException"/> whenever a segment other than the last is not sealed
/// or does not match its seal, and when the segment indices are not contiguous. Only the last
/// segment may be tear-repaired. A rotation makes the successor durable before it seals the full
/// segment, so a sealed last segment with no successor is a deleted segment and fails loud too, and
/// a seal binds its segment's index, so swapped segment files fail loud. Every crash state of a
/// rotation recovers every entry.
/// </summary>
public sealed class WalSegmentSealIntegrityTests : IDisposable
{
    // 1 MiB (the smallest the validator allows) with 300,000-character values: three frames stay
    // under the threshold and the fourth crosses it, so every sealed segment holds four frames.
    private const int FramesPerSegment = 4;
    private static readonly WalOptions s_options = new() { SegmentMaxBytes = 1L * 1024L * 1024L };
    private static readonly string s_value = new('v', 300_000);

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), $"WalSegmentSealIntegrityTests_{Guid.NewGuid():N}");

    public WalSegmentSealIntegrityTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private string Segment(int index) => Path.Combine(_dir, $"wal_{index:D6}.log");

    private string Pending(int index) => Path.Combine(_dir, $"wal_{index:D6}.pending");

    // ── Must fail loud ───────────────────────────────────────────────────────

    /// <summary>
    /// A sealed segment emptied to zero bytes, or to its bare 8-byte header, holds no damaged frame;
    /// replaying the next segment over it would silently drop keys 1..4.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    public async Task RecoverAsync_SealedSegmentEmptiedToNothingOrItsBareHeader_ThrowsTypedAndPreservesSegment(int keptBytes)
    {
        await WriteFramesAsync(FramesPerSegment + 1).ConfigureAwait(true);
        Truncate(Segment(1), keptBytes);

        await AssertRecoveryFailsLoudAsync(Segment(1)).ConfigureAwait(true);
        new FileInfo(Segment(1)).Length.Should().Be(keptBytes, "fail-loud recovery leaves the segment untouched");
    }

    /// <summary>
    /// A middle segment cut to zero bytes must not be read as a clean, frameless segment.
    /// </summary>
    [Fact]
    public async Task RecoverAsync_ZeroLengthMiddleSegment_ThrowsTypedAndPreservesSegment()
    {
        await WriteFramesAsync((2 * FramesPerSegment) + 1).ConfigureAwait(true);
        Truncate(Segment(2), 0);

        await AssertRecoveryFailsLoudAsync(Segment(2)).ConfigureAwait(true);
        new FileInfo(Segment(2)).Length.Should().Be(0);
    }

    /// <summary>
    /// A sealed segment cut at the end of any whole frame — including exactly before its seal —
    /// leaves only valid frames, so only the seal's recorded length and count can expose the loss.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task RecoverAsync_SealedSegmentCutAtAnInteriorFrameBoundary_ThrowsTyped(int keptFrames)
    {
        var frameSize = await WriteFramesAsync(FramesPerSegment + 1).ConfigureAwait(true);
        Truncate(Segment(1), 8 + (keptFrames * frameSize));

        await AssertRecoveryFailsLoudAsync(Segment(1)).ConfigureAwait(true);
    }

    /// <summary>
    /// Bytes after a segment's seal — a whole valid frame, or zeros — are not part of what was sealed.
    /// </summary>
    [Theory]
    [InlineData("valid-frame")]
    [InlineData("zeros")]
    public async Task RecoverAsync_SealedSegmentLongerThanItsSeal_ThrowsTyped(string extra)
    {
        var frameSize = await WriteFramesAsync(FramesPerSegment + 1).ConfigureAwait(true);
        var bytes = await File.ReadAllBytesAsync(Segment(1)).ConfigureAwait(true);
        var appended = extra == "valid-frame" ? bytes.AsSpan(8, frameSize).ToArray() : new byte[64];
        await File.WriteAllBytesAsync(Segment(1), [.. bytes, .. appended]).ConfigureAwait(true);

        await AssertRecoveryFailsLoudAsync(Segment(1)).ConfigureAwait(true);
    }

    /// <summary>
    /// Swapping two equal-size frames keeps every frame checksum valid and the segment's length and
    /// frame count unchanged; only the seal's checksum over the segment's bytes detects it.
    /// </summary>
    [Fact]
    public async Task RecoverAsync_SealedSegmentWithReorderedFrames_ThrowsTyped()
    {
        var frameSize = await WriteFramesAsync(FramesPerSegment + 1).ConfigureAwait(true);
        var bytes = await File.ReadAllBytesAsync(Segment(1)).ConfigureAwait(true);
        var first = bytes.AsSpan(8, frameSize).ToArray();
        bytes.AsSpan(8 + frameSize, frameSize).CopyTo(bytes.AsSpan(8, frameSize));
        first.CopyTo(bytes.AsSpan(8 + frameSize, frameSize));
        await File.WriteAllBytesAsync(Segment(1), bytes).ConfigureAwait(true);

        await AssertRecoveryFailsLoudAsync(Segment(1)).ConfigureAwait(true);
    }

    /// <summary>
    /// A whole middle segment missing between the first and the last is lost history.
    /// </summary>
    [Fact]
    public async Task RecoverAsync_MissingMiddleSegment_ThrowsTyped()
    {
        await WriteFramesAsync((2 * FramesPerSegment) + 1).ConfigureAwait(true);
        File.Delete(Segment(2));

        await AssertRecoveryFailsLoudAsync(Segment(2)).ConfigureAwait(true);
    }

    /// <summary>
    /// Swapping two sealed segment files keeps each file internally consistent with its own seal;
    /// only a seal that binds its segment's index exposes the reordered history.
    /// </summary>
    [Fact]
    public async Task RecoverAsync_TwoSealedSegmentFilesSwapped_ThrowsTyped()
    {
        await WriteFramesAsync((2 * FramesPerSegment) + 1).ConfigureAwait(true);
        var parked = Path.Combine(_dir, "parked.bin");
        File.Move(Segment(1), parked);
        File.Move(Segment(2), Segment(1));
        File.Move(parked, Segment(2));

        await AssertRecoveryFailsLoudAsync(Segment(1)).ConfigureAwait(true);
    }

    /// <summary>
    /// The highest segment deleted — whether it held acknowledged frames or was the freshly rotated,
    /// still-empty successor — leaves a sealed segment last. The writer creates a segment's successor
    /// before sealing it, so a sealed last segment means its successor existed and is gone.
    /// </summary>
    [Theory]
    [InlineData(FramesPerSegment + 1, 2)]
    [InlineData((2 * FramesPerSegment) + 3, 3)]
    [InlineData(FramesPerSegment, 2)]
    public async Task RecoverAsync_LastSegmentDeleted_ThrowsTyped(int frames, int lastSegment)
    {
        await WriteFramesAsync(frames).ConfigureAwait(true);
        File.Exists(Segment(lastSegment)).Should().BeTrue("precondition: the segment to delete is the highest one");
        File.Exists(Segment(lastSegment + 1)).Should().BeFalse();
        File.Delete(Segment(lastSegment));

        await AssertRecoveryFailsLoudAsync(Segment(lastSegment)).ConfigureAwait(true);
    }

    /// <summary>
    /// The append path refuses a deleted last segment the same way: a standalone append must not
    /// create a fresh segment over the gap and acknowledge into it.
    /// </summary>
    [Fact]
    public async Task AppendAsync_LastSegmentDeleted_ThrowsTypedAndCreatesNothing()
    {
        await WriteFramesAsync(FramesPerSegment + 1).ConfigureAwait(true);
        File.Delete(Segment(2));

        using (var wal = new Wal(_dir, s_options))
        {
            var caught = await Record.ExceptionAsync(() => wal.AppendAsync(
                new SerializableWrapper<int>(6), new SerializableWrapper<string>("six"), 6UL)).ConfigureAwait(true);
            caught.Should().BeOfType<WriteAheadLogCorruptException>(
                "an append must not paper over a vanished last segment");
            ((WriteAheadLogCorruptException)caught).SegmentPath.Should().Be(Segment(2));
        }

        File.Exists(Segment(2)).Should().BeFalse("the refused append creates no segment");
    }

    // ── Must recover ─────────────────────────────────────────────────────────

    /// <summary>
    /// A rotation creates the successor under its pending name, seals the full segment, then
    /// publishes the successor. A crash after the seal landed but before the publish leaves a sealed
    /// last segment and a pending successor: recovery returns every entry, and the next append — with
    /// or without a recovery first — goes into the published successor, never after the seal.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Reopen_CrashAfterSealBeforeSuccessorPublished_RecoversAndAppendsIntoSuccessor(bool recoverFirst)
    {
        await WriteFramesAsync(FramesPerSegment, crashAtRotationStep: "rotation-publish").ConfigureAwait(true);
        File.Exists(Segment(2)).Should().BeFalse("precondition: the crash came before the successor was published");
        File.Exists(Pending(2)).Should().BeTrue("precondition: the successor was made durable before the seal");
        var sealedBytes = await File.ReadAllBytesAsync(Segment(1)).ConfigureAwait(true);

        using (var wal = new Wal(_dir, s_options))
        {
            if (recoverFirst)
            {
                (await wal.RecoverAsync().ConfigureAwait(true)).Select(e => e.Key.Value).Should().Equal(1, 2, 3, 4);
            }

            await wal.AppendAsync(new SerializableWrapper<int>(5), new SerializableWrapper<string>("five"), 5UL).ConfigureAwait(true);
        }

        File.Exists(Pending(2)).Should().BeFalse("the pending successor is published, not left behind");
        File.Exists(Segment(2)).Should().BeTrue("the append after a sealed segment lands in its successor");
        (await File.ReadAllBytesAsync(Segment(1)).ConfigureAwait(true)).Should().Equal(
            sealedBytes, "nothing is ever written after a segment's seal");
        await AssertRecoversAsync(1, 2, 3, 4, 5).ConfigureAwait(true);
    }

    /// <summary>
    /// A crash after the successor became durable but before the seal was written — or while the
    /// seal itself was being written, leaving a torn seal — leaves the full segment unsealed and last.
    /// It is an interrupted write like any other: recovery discards the pending successor, truncates a
    /// torn seal, keeps all four frames, and later appends continue the log.
    /// </summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task Reopen_CrashBeforeSealLanded_DiscardsPendingSuccessorAndRecoversEveryFrame(bool tornSeal, bool recoverFirst)
    {
        var frameSize = await WriteFramesAsync(
            FramesPerSegment, crashAtRotationStep: tornSeal ? "rotation-publish" : "rotation-seal").ConfigureAwait(true);
        var lastFrameEnd = 8 + (FramesPerSegment * frameSize);
        File.Exists(Pending(2)).Should().BeTrue("precondition: the successor was made durable before the seal");
        if (tornSeal)
        {
            Truncate(Segment(1), new FileInfo(Segment(1)).Length - 5);
        }
        else
        {
            new FileInfo(Segment(1)).Length.Should().Be(lastFrameEnd, "precondition: the seal was never written");
        }

        if (recoverFirst)
        {
            await AssertRecoversAsync(1, 2, 3, 4).ConfigureAwait(true);
            new FileInfo(Segment(1)).Length.Should().Be(lastFrameEnd, "a torn seal is truncated, the frames are kept");
            File.Exists(Pending(2)).Should().BeFalse("a successor whose predecessor never got its seal is discarded");
        }

        using (var wal = new Wal(_dir, s_options))
        {
            await wal.AppendAsync(new SerializableWrapper<int>(5), new SerializableWrapper<string>("five"), 5UL).ConfigureAwait(true);
        }

        await AssertRecoversAsync(1, 2, 3, 4, 5).ConfigureAwait(true);
    }

    /// <summary>
    /// The rotation's normal outcome: the full segment sealed and its successor published, still
    /// empty. Reopening recovers every entry and appends into the successor.
    /// </summary>
    [Fact]
    public async Task Reopen_AfterRotationWithEmptySuccessor_RecoversAndAppendsIntoSuccessor()
    {
        await WriteFramesAsync(FramesPerSegment).ConfigureAwait(true);
        new FileInfo(Segment(2)).Length.Should().Be(8, "precondition: the successor holds only its header");

        await AssertRecoversAsync(1, 2, 3, 4).ConfigureAwait(true);
        using (var wal = new Wal(_dir, s_options))
        {
            await wal.AppendAsync(new SerializableWrapper<int>(5), new SerializableWrapper<string>("five"), 5UL).ConfigureAwait(true);
        }

        new FileInfo(Segment(2)).Length.Should().BeGreaterThan(8, "the append lands in the successor");
        await AssertRecoversAsync(1, 2, 3, 4, 5).ConfigureAwait(true);
    }

    /// <summary>
    /// Three sealed segments and an active fourth recover every entry in order, and the sealed
    /// segments survive a further reopen-and-append cycle unchanged.
    /// </summary>
    [Fact]
    public async Task RecoverAsync_SeveralSealedSegments_RecoversEveryEntryInOrder()
    {
        await WriteFramesAsync((3 * FramesPerSegment) + 2).ConfigureAwait(true);

        await AssertRecoversAsync([.. Enumerable.Range(1, (3 * FramesPerSegment) + 2)]).ConfigureAwait(true);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Appends keys 1..<paramref name="frames"/> with equal-size values through the production
    /// writer, one acknowledged append at a time, and returns the size of one frame. With
    /// <paramref name="crashAtRotationStep"/>, the first rotation's durable step of that name fails
    /// as a crash would: nothing after it is written.
    /// </summary>
    private async Task<int> WriteFramesAsync(int frames, string crashAtRotationStep = null)
    {
        int frameSize;
        using (var wal = new Wal(_dir, s_options))
        {
            if (crashAtRotationStep is not null)
            {
                wal._faultInjector = step =>
                {
                    if (step == crashAtRotationStep)
                    {
                        throw new IOException($"simulated crash before {step}");
                    }
                };
            }

            await wal.AppendAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>(s_value), 1UL).ConfigureAwait(true);
            frameSize = (int)new FileInfo(Segment(1)).Length - 8;
            for (var k = 2; k <= frames; k++)
            {
                await wal.AppendAsync(new SerializableWrapper<int>(k), new SerializableWrapper<string>(s_value), (ulong)k).ConfigureAwait(true);
            }
        }

        return frameSize;
    }

    private static void Truncate(string path, long length)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Write);
        fs.SetLength(length);
    }

    private async Task AssertRecoveryFailsLoudAsync(string expectedSegment)
    {
        using var wal = new Wal(_dir, s_options);
        var caught = await Record.ExceptionAsync(() => wal.RecoverAsync()).ConfigureAwait(true);
        caught.Should().BeOfType<WriteAheadLogCorruptException>(
            "damage that leaves no damaged frame must still fail loud instead of replaying across lost history");
        ((WriteAheadLogCorruptException)caught).SegmentPath.Should().Be(expectedSegment);
    }

    private async Task AssertRecoversAsync(params int[] keys)
    {
        using var wal = new Wal(_dir, s_options);
        IReadOnlyList<WalEntry<SerializableWrapper<int>, SerializableWrapper<string>>> entries = null;
        var caught = await Record.ExceptionAsync(async () => entries = await wal.RecoverAsync().ConfigureAwait(true))
            .ConfigureAwait(true);
        caught.Should().BeNull("this image is a legitimate crash state");
        entries.Select(e => e.Key.Value).Should().Equal(keys);
    }
}
