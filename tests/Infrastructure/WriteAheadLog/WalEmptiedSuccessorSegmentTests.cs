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
/// A segment numbered above 1 is only ever published by rotation, which writes and fsyncs its header
/// under a pending name before it seals the predecessor. Such a segment emptied, cut inside its header
/// or zero-filled is therefore damage, never a crash inside segment creation: recovery and the append
/// path fail loud with <see cref="WriteAheadLogCorruptException"/> and leave the segment untouched,
/// exactly as they do when the same segment is deleted. Only the first segment, created in place by
/// the append path, can hold a torn creation, and that crash state still recovers.
/// </summary>
public sealed class WalEmptiedSuccessorSegmentTests : IDisposable
{
    // 1 MiB (the smallest the validator allows) with 300,000-character values: the fourth frame
    // crosses the threshold, so every sealed segment holds four frames.
    private const int FramesPerSegment = 4;
    private static readonly WalOptions s_options = new() { SegmentMaxBytes = 1L * 1024L * 1024L };
    private static readonly string s_value = new('v', 300_000);
    private static readonly byte[] s_header = [(byte)'B', (byte)'S', (byte)'W', (byte)'L', 0x01, 0x00, 0x00, 0x00];

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), $"WalEmptiedSuccessorSegmentTests_{Guid.NewGuid():N}");

    public WalEmptiedSuccessorSegmentTests() => Directory.CreateDirectory(_dir);

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

    // ── Must fail loud ───────────────────────────────────────────────────────

    /// <summary>
    /// Segment 1 is sealed and segment 2 holds two acknowledged frames. Emptying segment 2 or cutting
    /// it inside its durable header must not read as a torn creation that silently drops keys 5 and 6.
    /// </summary>
    [Theory]
    [InlineData("empty")]
    [InlineData("prefix-4")]
    [InlineData("prefix-5")]
    [InlineData("zeros-8")]
    [InlineData("magic-then-zeros")]
    public async Task RecoverAsync_SuccessorOfSealedSegmentEmptiedBelowItsHeader_ThrowsTypedAndPreservesSegment(string shape)
    {
        await WriteFramesAsync(FramesPerSegment + 2).ConfigureAwait(true);
        var damaged = Damage(Segment(2), shape);

        using var wal = new Wal(_dir, s_options);
        var caught = await Record.ExceptionAsync(() => wal.RecoverAsync()).ConfigureAwait(true);

        caught.Should().BeOfType<WriteAheadLogCorruptException>(
            "a segment rotation published held a durable header; losing it lost acknowledged frames");
        ((WriteAheadLogCorruptException)caught).SegmentPath.Should().Be(Segment(2));
        (await File.ReadAllBytesAsync(Segment(2)).ConfigureAwait(true)).Should().Equal(
            damaged, "fail-loud recovery leaves the damaged segment untouched");
    }

    /// <summary>
    /// The same images, appended to without a prior recovery: the append path must refuse rather
    /// than re-initialise the segment over the lost frames.
    /// </summary>
    [Theory]
    [InlineData("empty")]
    [InlineData("prefix-4")]
    [InlineData("zeros-8")]
    public async Task StandaloneAppend_OntoSuccessorOfSealedSegmentEmptiedBelowItsHeader_ThrowsTypedAndPreservesSegment(string shape)
    {
        await WriteFramesAsync(FramesPerSegment + 2).ConfigureAwait(true);
        var damaged = Damage(Segment(2), shape);

        using (var wal = new Wal(_dir, s_options))
        {
            var caught = await Record.ExceptionAsync(
                () => wal.AppendAsync(new SerializableWrapper<int>(99), new SerializableWrapper<string>("x"), 99UL))
                .ConfigureAwait(true);
            caught.Should().BeOfType<WriteAheadLogCorruptException>(
                "the append path must not re-initialise a published segment over its lost frames");
            ((WriteAheadLogCorruptException)caught).SegmentPath.Should().Be(Segment(2));
        }

        (await File.ReadAllBytesAsync(Segment(2)).ConfigureAwait(true)).Should().Equal(damaged);
    }

    /// <summary>
    /// Reclamation deletes segments 1 and 2, leaving segment 3 (two acknowledged frames) alone in the
    /// directory. Segment 3 was still published by rotation, so emptying it is damage on both paths.
    /// </summary>
    [Theory]
    [InlineData("recover")]
    [InlineData("append")]
    public async Task LoneLaterSegmentLeftByReclamation_Emptied_FailsLoudAndPreservesSegment(string path)
    {
        using (var wal = new Wal(_dir, s_options))
        {
            for (var k = 1; k <= (2 * FramesPerSegment) + 2; k++)
            {
                await wal.AppendAsync(new SerializableWrapper<int>(k), new SerializableWrapper<string>(s_value), (ulong)k).ConfigureAwait(true);
            }

            await wal.TruncateCompletedSegmentsAsync((ulong)(2 * FramesPerSegment)).ConfigureAwait(true);
        }

        File.Exists(Segment(1)).Should().BeFalse("precondition: reclamation removed segment 1");
        File.Exists(Segment(2)).Should().BeFalse("precondition: reclamation removed segment 2");
        Damage(Segment(3), "empty");

        using (var wal = new Wal(_dir, s_options))
        {
            var caught = path == "recover"
                ? await Record.ExceptionAsync(() => wal.RecoverAsync()).ConfigureAwait(true)
                : await Record.ExceptionAsync(
                    () => wal.AppendAsync(new SerializableWrapper<int>(99), new SerializableWrapper<string>("x"), 99UL))
                    .ConfigureAwait(true);
            caught.Should().BeOfType<WriteAheadLogCorruptException>(
                "segment 3 was published by rotation with a durable header; emptying it lost keys 9 and 10");
            ((WriteAheadLogCorruptException)caught).SegmentPath.Should().Be(Segment(3));
        }

        new FileInfo(Segment(3)).Length.Should().Be(0, "fail-loud handling leaves the segment untouched");
    }

    // ── Must recover ─────────────────────────────────────────────────────────

    /// <summary>
    /// The first segment is created in place by the append path, so a crash inside its creation can
    /// leave it empty, holding a header prefix, or zeros. That holds no acknowledged frame: recovery
    /// returns an empty log and the next append lands normally.
    /// </summary>
    [Theory]
    [InlineData("empty")]
    [InlineData("prefix-4")]
    [InlineData("zeros-8")]
    public async Task FirstSegmentTornAtCreation_RecoversEmptyAndAcceptsAppends(string shape)
    {
        await File.WriteAllBytesAsync(Segment(1), Shape(shape)).ConfigureAwait(true);

        using (var wal = new Wal(_dir, s_options))
        {
            IReadOnlyList<WalEntry<SerializableWrapper<int>, SerializableWrapper<string>>> entries = null;
            var caught = await Record.ExceptionAsync(async () => entries = await wal.RecoverAsync().ConfigureAwait(true))
                .ConfigureAwait(true);
            caught.Should().BeNull("a torn creation of the first segment is a crash state");
            entries.Should().BeEmpty();

            await wal.AppendAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("a"), 1UL).ConfigureAwait(true);
        }

        using var reader = new Wal(_dir, s_options);
        (await reader.RecoverAsync().ConfigureAwait(true)).Select(e => e.Key.Value).Should().Equal(1);
    }

    /// <summary>
    /// The declared boundary: a successor cut exactly to its bare, valid header is indistinguishable
    /// from "no frame was written yet" and still recovers the predecessor's entries.
    /// </summary>
    [Fact]
    public async Task RecoverAsync_SuccessorCutToItsBareValidHeader_RecoversPredecessorEntries()
    {
        await WriteFramesAsync(FramesPerSegment + 2).ConfigureAwait(true);
        Damage(Segment(2), "bare-header");

        using var wal = new Wal(_dir, s_options);
        var entries = await wal.RecoverAsync().ConfigureAwait(true);
        entries.Select(e => e.Key.Value).Should().Equal(1, 2, 3, 4);
    }

    /// <summary>
    /// A reclamation that unlinks every segment, ending with the current one, and then fails to make
    /// that last unlink durable must not leave the writer pointed at the deleted segment's number: the
    /// next append would re-create segment 3 in place, and a crash inside that creation would leave a
    /// torn segment 3 that recovery must refuse. The next append starts the log at segment 1 instead.
    /// </summary>
    [Theory]
    [InlineData("truncate-completed")]
    [InlineData("truncate")]
    public async Task Reclamation_FsyncFailsAfterCurrentSegmentUnlinked_NextAppendStartsAtFirstSegment(string reclamation)
    {
        const int Frames = (2 * FramesPerSegment) + 1;
        using (var wal = new Wal(_dir, s_options))
        {
            for (var k = 1; k <= Frames; k++)
            {
                await wal.AppendAsync(new SerializableWrapper<int>(k), new SerializableWrapper<string>(s_value), (ulong)k).ConfigureAwait(true);
            }

            File.Exists(Segment(3)).Should().BeTrue("precondition: the log spans three segments");
            var fsyncs = 0;
            wal._faultInjector = step =>
            {
                if (step == "deletion-directory-fsync" && ++fsyncs == 3)
                {
                    throw new IOException("injected directory fsync failure (EIO)");
                }
            };

            var caught = reclamation == "truncate"
                ? await Record.ExceptionAsync(() => wal.TruncateAsync()).ConfigureAwait(true)
                : await Record.ExceptionAsync(() => wal.TruncateCompletedSegmentsAsync(Frames)).ConfigureAwait(true);
            caught.Should().BeOfType<IOException>("precondition: the third unlink's directory fsync fails");
            File.Exists(Segment(3)).Should().BeFalse("precondition: the current segment's unlink landed");

            await wal.AppendAsync(new SerializableWrapper<int>(100), new SerializableWrapper<string>("a"), 100UL).ConfigureAwait(true);
        }

        File.Exists(Segment(1)).Should().BeTrue("an emptied log restarts at segment 1, the only segment created in place");
        File.Exists(Segment(3)).Should().BeFalse("segment 3 must not be re-created in place");

        using var reader = new Wal(_dir, s_options);
        (await reader.RecoverAsync().ConfigureAwait(true)).Select(e => e.Key.Value).Should().Equal(100);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task WriteFramesAsync(int frames)
    {
        using var wal = new Wal(_dir, s_options);
        for (var k = 1; k <= frames; k++)
        {
            await wal.AppendAsync(new SerializableWrapper<int>(k), new SerializableWrapper<string>(s_value), (ulong)k).ConfigureAwait(true);
        }
    }

    private static byte[] Shape(string shape) => shape switch
    {
        "empty" => [],
        "prefix-4" => s_header[..4],
        "prefix-5" => s_header[..5],
        "zeros-8" => new byte[8],
        "magic-then-zeros" => [.. s_header[..4], 0x00, 0x00, 0x00, 0x00],
        "bare-header" => [.. s_header],
        _ => throw new ArgumentOutOfRangeException(nameof(shape)),
    };

    private static byte[] Damage(string path, string shape)
    {
        var bytes = Shape(shape);
        File.WriteAllBytes(path, bytes);
        return bytes;
    }
}
