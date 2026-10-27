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

using System.Buffers.Binary;
using FaultingWal = Boutquin.Storage.Infrastructure.WriteAheadLog.WriteAheadLog<
    Boutquin.Storage.Domain.Helpers.SerializableWrapper<int>,
    Boutquin.Storage.Infrastructure.Tests.WriteAheadLogHeaderAndKindRecoveryTests.FaultingValue>;
using StrictWal = Boutquin.Storage.Infrastructure.WriteAheadLog.WriteAheadLog<
    Boutquin.Storage.Domain.Helpers.SerializableWrapper<int>,
    Boutquin.Storage.Infrastructure.Tests.WriteAheadLogHeaderAndKindRecoveryTests.StrictValue>;
using WalType = Boutquin.Storage.Infrastructure.WriteAheadLog.WriteAheadLog<
    Boutquin.Storage.Domain.Helpers.SerializableWrapper<int>,
    Boutquin.Storage.Domain.Helpers.SerializableWrapper<string>>;

namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// Two recovery classifications on the WAL that a torn-tail truncation must never absorb.
/// A complete, CRC-valid frame the reader rejects (unknown kind byte, value that will not
/// decode) is acknowledged bytes, not a torn append: recovery and the standalone-append repair
/// fail loud with <see cref="WriteAheadLogCorruptException"/> and leave the segment untouched.
/// Segment-header damage surfaces as that same typed exception, carrying the segment path —
/// except on a frameless FIRST segment whose bytes are a prefix of the header followed by
/// zeros (including all zeros), which is a crash inside segment creation: recovery truncates it
/// and the append path re-initialises it. Later segments are published by rotation with a durable
/// header, so the same shape there fails loud.
/// </summary>
public sealed class WriteAheadLogHeaderAndKindRecoveryTests : IDisposable
{
    private const int FileHeaderSize = 8;
    private const int FrameHeaderFieldsSize = 4 + 1 + 1 + 8; // length + entry_kind + value_kind + seqno
    private const int CrcSize = 4;
    private const int FrameHeaderSize = FrameHeaderFieldsSize + CrcSize; // header fields + header CRC

    private readonly string _walDirectory = Path.Combine(
        Path.GetTempPath(), $"WriteAheadLogHeaderAndKindRecoveryTests_{Guid.NewGuid():N}");

    public WriteAheadLogHeaderAndKindRecoveryTests() => Directory.CreateDirectory(_walDirectory);

    private string FirstSegment => Path.Combine(_walDirectory, "wal_000001.log");

    private string SecondSegment => Path.Combine(_walDirectory, "wal_000002.log");

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

    // ── CRC-valid final frame the reader rejects ────────────────────────────

    /// <summary>
    /// The final frame is complete and CRC-valid, but its value's own decoder rejects the bytes
    /// its encoder wrote. That frame is acknowledged history: recovery throws the typed exception
    /// pointing at the frame's start and keeps every byte.
    /// </summary>
    [Fact]
    public async Task RecoverAsync_FinalFrameCrcValidButUndecodable_ThrowsAndPreservesSegment()
    {
        var firstFrameEnd = await WriteStrictFramesAsync().ConfigureAwait(true);
        var bytes = await File.ReadAllBytesAsync(FirstSegment).ConfigureAwait(true);

        using var wal = new StrictWal(_walDirectory);
        Exception caught = await Record.ExceptionAsync(() => wal.RecoverAsync()).ConfigureAwait(true);

        caught.Should().BeOfType<WriteAheadLogCorruptException>(
            "a complete CRC-valid frame that fails to decode is not a torn append");
        var corrupt = (WriteAheadLogCorruptException)caught;
        corrupt.SegmentPath.Should().Be(FirstSegment);
        corrupt.LastGoodOffset.Should().Be(firstFrameEnd, "the rejected frame starts where the first one ends");
        corrupt.InnerException.Should().BeOfType<FormatException>();
        (await File.ReadAllBytesAsync(FirstSegment).ConfigureAwait(true)).Should().Equal(
            bytes, "fail-loud recovery must not truncate the acknowledged final frame");
    }

    /// <summary>
    /// Every decode fault a key or value decoder can raise on a CRC-valid frame — not only the
    /// stream/format family — surfaces as the typed rejected-frame exception naming the segment
    /// and the frame's offset, with the decoder's exception as the cause.
    /// </summary>
    [Theory]
    [InlineData(nameof(InvalidDataException))]
    [InlineData(nameof(UnsupportedFormatVersionException))]
    [InlineData(nameof(NotSupportedException))]
    [InlineData(nameof(OverflowException))]
    [InlineData(nameof(InvalidOperationException))]
    [InlineData(nameof(KeyNotFoundException))]
    public async Task RecoverAsync_CrcValidFrameWhoseDecoderThrows_ThrowsTypedRejectedFrame(string decodeFault)
    {
        long firstFrameEnd;
        using (var wal = new FaultingWal(_walDirectory))
        {
            await wal.AppendAsync(new SerializableWrapper<int>(1), new FaultingValue("ok"), 1UL).ConfigureAwait(true);
            firstFrameEnd = new FileInfo(FirstSegment).Length;
            await wal.AppendAsync(new SerializableWrapper<int>(2), new FaultingValue(decodeFault), 2UL).ConfigureAwait(true);
        }

        var bytes = await File.ReadAllBytesAsync(FirstSegment).ConfigureAwait(true);

        using (var wal = new FaultingWal(_walDirectory))
        {
            Exception caught = await Record.ExceptionAsync(() => wal.RecoverAsync()).ConfigureAwait(true);

            caught.Should().BeOfType<WriteAheadLogCorruptException>(
                "a CRC-valid frame whose decoder throws {0} is a rejected frame, not a raw decode leak", decodeFault);
            var corrupt = (WriteAheadLogCorruptException)caught;
            corrupt.SegmentPath.Should().Be(FirstSegment);
            corrupt.LastGoodOffset.Should().Be(firstFrameEnd);
            corrupt.InnerException.GetType().Name.Should().Be(decodeFault);
        }

        (await File.ReadAllBytesAsync(FirstSegment).ConfigureAwait(true)).Should().Equal(bytes);
    }

    /// <summary>
    /// A WAL directory written with string keys and reopened with int keys: the repository's own
    /// <c>SerializableWrapper</c> decoder refuses each key with its documented
    /// <see cref="InvalidDataException"/>. Recovery must surface the typed exception naming the
    /// segment and the first frame, not the raw decode exception.
    /// </summary>
    [Fact]
    public async Task RecoverAsync_KeyDecoderRaisesInvalidDataException_ThrowsTypedRejectedFrame()
    {
        await WriteStringKeyedFramesAsync().ConfigureAwait(true);

        using var wal = new WalType(_walDirectory);
        Exception caught = await Record.ExceptionAsync(() => wal.RecoverAsync()).ConfigureAwait(true);

        caught.Should().BeOfType<WriteAheadLogCorruptException>();
        var corrupt = (WriteAheadLogCorruptException)caught;
        corrupt.SegmentPath.Should().Be(FirstSegment);
        corrupt.LastGoodOffset.Should().Be(FileHeaderSize, "the first frame is the one the key decoder rejects");
        corrupt.InnerException.Should().BeOfType<InvalidDataException>();
    }

    /// <summary>
    /// The append-side repair refuses the same image with the same typed exception: it names the
    /// rejected frame's offset rather than reporting an unlocated group-commit failure.
    /// </summary>
    [Fact]
    public async Task StandaloneAppend_KeyDecoderRaisesInvalidDataException_ThrowsTypedRejectedFrame()
    {
        await WriteStringKeyedFramesAsync().ConfigureAwait(true);
        var bytes = await File.ReadAllBytesAsync(FirstSegment).ConfigureAwait(true);

        using (var wal = new WalType(_walDirectory))
        {
            Exception caught = await Record.ExceptionAsync(
                () => wal.AppendAsync(new SerializableWrapper<int>(3), new SerializableWrapper<string>("c"), 3UL)).ConfigureAwait(true);

            caught.Should().BeOfType<WriteAheadLogCorruptException>();
            var corrupt = (WriteAheadLogCorruptException)caught;
            corrupt.LastGoodOffset.Should().Be(FileHeaderSize, "the append-side repair names the rejected frame");
            corrupt.InnerException.Should().BeOfType<InvalidDataException>();
        }

        (await File.ReadAllBytesAsync(FirstSegment).ConfigureAwait(true)).Should().Equal(bytes);
    }

    /// <summary>
    /// Same image, but the consumer appends without recovering first: the standalone torn-tail
    /// repair must refuse rather than truncate the CRC-valid final frame away.
    /// </summary>
    [Fact]
    public async Task StandaloneAppend_AfterCrcValidButUndecodableFinalFrame_FailsAndPreservesSegment()
    {
        await WriteStrictFramesAsync().ConfigureAwait(true);
        var bytes = await File.ReadAllBytesAsync(FirstSegment).ConfigureAwait(true);

        using (var wal = new StrictWal(_walDirectory))
        {
            var act = () => wal.AppendAsync(new SerializableWrapper<int>(3), new StrictValue("c"), 3UL);
            await act.Should().ThrowAsync<WriteAheadLogCorruptException>().ConfigureAwait(true);
        }

        (await File.ReadAllBytesAsync(FirstSegment).ConfigureAwait(true)).Should().Equal(
            bytes, "the append-side repair must not truncate the acknowledged final frame");
    }

    /// <summary>
    /// A CRC-valid final frame whose entry-kind byte this reader does not know (a newer writer's
    /// frame) also stops the standalone-append repair loudly, leaving the segment intact.
    /// </summary>
    [Fact]
    public async Task StandaloneAppend_AfterCrcValidFinalFrameWithUnknownKind_FailsAndPreservesSegment()
    {
        using (var wal = new WalType(_walDirectory))
        {
            await wal.AppendAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("a"), 1UL).ConfigureAwait(true);
            await wal.AppendAsync(new SerializableWrapper<int>(2), new SerializableWrapper<string>("b"), 2UL).ConfigureAwait(true);
        }

        var bytes = await File.ReadAllBytesAsync(FirstSegment).ConfigureAwait(true);
        var firstPayload = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(FileHeaderSize, 4));
        var second = FileHeaderSize + FrameHeaderSize + firstPayload + CrcSize;
        bytes[second + 4] = 0x7F;
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(second + FrameHeaderFieldsSize, CrcSize),
            Boutquin.Storage.Infrastructure.Algorithms.Crc32C.HashToUInt32(bytes.AsSpan(second, FrameHeaderFieldsSize)));
        await File.WriteAllBytesAsync(FirstSegment, bytes).ConfigureAwait(true);

        using (var wal = new WalType(_walDirectory))
        {
            var act = () => wal.AppendAsync(new SerializableWrapper<int>(3), new SerializableWrapper<string>("c"), 3UL);
            await act.Should().ThrowAsync<WriteAheadLogCorruptException>().ConfigureAwait(true);
        }

        (await File.ReadAllBytesAsync(FirstSegment).ConfigureAwait(true)).Should().Equal(
            bytes, "the append-side repair must not truncate a CRC-valid frame of unknown kind");
    }

    // ── Segment-header damage and torn segment creation ─────────────────────

    /// <summary>
    /// Segment creation writes an 8-byte header into an empty file and fsyncs it; a crash inside
    /// that window leaves a frameless first segment holding a header prefix, zeros, or both. It
    /// holds no acknowledged frame, so recovery returns an empty log, truncates it to zero bytes,
    /// and the next append lands in it normally.
    /// </summary>
    [Theory]
    [InlineData("zero-3")]
    [InlineData("zero-8")]
    [InlineData("prefix-5")]
    [InlineData("prefix-7")]
    [InlineData("magic-then-zero")]
    public async Task RecoverAsync_FramelessFirstSegmentWithTornHeader_TruncatesAndAcceptsAppends(string shape)
    {
        await File.WriteAllBytesAsync(FirstSegment, TornHeader(shape)).ConfigureAwait(true);

        using (var wal = new WalType(_walDirectory))
        {
            Exception caught = null;
            IReadOnlyList<WalEntry<SerializableWrapper<int>, SerializableWrapper<string>>> entries = null;
            try
            {
                entries = await wal.RecoverAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                caught = ex;
            }

            caught.Should().BeNull("a torn segment creation holds no acknowledged frame");
            entries.Should().BeEmpty();
            new FileInfo(FirstSegment).Length.Should().Be(0, "recovery discards the torn header");

            await wal.AppendAsync(new SerializableWrapper<int>(5), new SerializableWrapper<string>("e"), 5UL).ConfigureAwait(true);
        }

        using var reader = new WalType(_walDirectory);
        var after = await reader.RecoverAsync().ConfigureAwait(true);
        after.Select(e => e.Key.Value).Should().Equal(5);
    }

    /// <summary>
    /// The same torn-header shapes on the segment that follows a sealed one are damage, not a crash:
    /// rotation writes and fsyncs the successor's header under a pending name before it seals the full
    /// segment, so the successor's header was durable. Recovery throws the typed exception naming the
    /// successor and leaves it intact.
    /// </summary>
    [Theory]
    [InlineData("zero-3")]
    [InlineData("zero-8")]
    [InlineData("prefix-5")]
    [InlineData("prefix-7")]
    [InlineData("magic-then-zero")]
    public async Task RecoverAsync_SuccessorOfSealedSegmentWithTornHeader_ThrowsTypedAndPreservesSegment(string shape)
    {
        await WriteSealedFirstSegmentAsync().ConfigureAwait(true);
        var torn = TornHeader(shape);
        await File.WriteAllBytesAsync(SecondSegment, torn).ConfigureAwait(true);

        using var wal = new WalType(_walDirectory);
        Exception caught = await Record.ExceptionAsync(() => wal.RecoverAsync()).ConfigureAwait(true);

        caught.Should().BeOfType<WriteAheadLogCorruptException>(
            "a successor published by rotation held a durable header; losing it is damage");
        ((WriteAheadLogCorruptException)caught).SegmentPath.Should().Be(SecondSegment);
        (await File.ReadAllBytesAsync(SecondSegment).ConfigureAwait(true)).Should().Equal(torn);
    }

    /// <summary>
    /// A frameless last segment whose header bytes are neither a header prefix nor zeros is not a
    /// torn creation: recovery throws the typed exception naming that segment and leaves it intact.
    /// </summary>
    [Fact]
    public async Task RecoverAsync_FramelessLastSegmentWithForeignHeader_ThrowsTypedAndPreservesSegment()
    {
        await WriteSealedFirstSegmentAsync().ConfigureAwait(true);
        byte[] foreign = [0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x00, 0x00, 0x00];
        await File.WriteAllBytesAsync(SecondSegment, foreign).ConfigureAwait(true);

        using var wal = new WalType(_walDirectory);
        Exception caught = await Record.ExceptionAsync(() => wal.RecoverAsync()).ConfigureAwait(true);

        caught.Should().BeOfType<WriteAheadLogCorruptException>();
        ((WriteAheadLogCorruptException)caught).SegmentPath.Should().Be(SecondSegment);
        (await File.ReadAllBytesAsync(SecondSegment).ConfigureAwait(true)).Should().Equal(foreign);
    }

    /// <summary>
    /// A torn-looking header on a segment that later segments follow is mid-history damage:
    /// rotation publishes segment N+1 only after segment N filled and was sealed, so recovery fails loud.
    /// </summary>
    [Fact]
    public async Task RecoverAsync_TornHeaderOnNonLastSegment_ThrowsTyped()
    {
        await WriteSealedFirstSegmentAsync().ConfigureAwait(true);
        File.Move(FirstSegment, SecondSegment, overwrite: true);
        await File.WriteAllBytesAsync(FirstSegment, new byte[3]).ConfigureAwait(true);

        using var wal = new WalType(_walDirectory);
        Exception caught = await Record.ExceptionAsync(() => wal.RecoverAsync()).ConfigureAwait(true);

        caught.Should().BeOfType<WriteAheadLogCorruptException>();
        ((WriteAheadLogCorruptException)caught).SegmentPath.Should().Be(FirstSegment);
        new FileInfo(FirstSegment).Length.Should().Be(3);
    }

    /// <summary>
    /// The append path, without a prior recovery, refuses a zero-filled successor of a sealed segment
    /// instead of re-initialising it over the frames it lost.
    /// </summary>
    [Fact]
    public async Task StandaloneAppend_OntoZeroFilledSuccessorOfSealedSegment_ThrowsTypedAndPreservesSegment()
    {
        await WriteSealedFirstSegmentAsync().ConfigureAwait(true);
        await File.WriteAllBytesAsync(SecondSegment, new byte[FileHeaderSize]).ConfigureAwait(true);

        using (var wal = new WalType(_walDirectory))
        {
            Exception caught = await Record.ExceptionAsync(
                () => wal.AppendAsync(new SerializableWrapper<int>(5), new SerializableWrapper<string>("e"), 5UL))
                .ConfigureAwait(true);
            caught.Should().BeOfType<WriteAheadLogCorruptException>(
                "a successor published by rotation held a durable header; re-initialising it would hide the loss");
            ((WriteAheadLogCorruptException)caught).SegmentPath.Should().Be(SecondSegment);
        }

        (await File.ReadAllBytesAsync(SecondSegment).ConfigureAwait(true)).Should().Equal(new byte[FileHeaderSize]);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static byte[] TornHeader(string shape)
    {
        byte[] header = [(byte)'B', (byte)'S', (byte)'W', (byte)'L', 0x01, 0x00, 0x00, 0x00];
        return shape switch
        {
            "zero-3" => new byte[3],
            "zero-8" => new byte[8],
            "prefix-5" => header[..5],
            "prefix-7" => header[..7],
            "magic-then-zero" => [.. header[..4], 0x00, 0x00, 0x00, 0x00],
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };
    }

    /// <summary>
    /// Writes keys 1..4 as 300,000-character values with 1 MiB segments: the fourth append crosses
    /// the threshold, so the writer creates <c>wal_000002.log</c> with a durable header (under its
    /// pending name), seals <c>wal_000001.log</c>, and publishes the frameless successor.
    /// </summary>
    private async Task WriteSealedFirstSegmentAsync()
    {
        using var wal = new WalType(
            _walDirectory,
            new Boutquin.Storage.Infrastructure.WriteAheadLog.WriteAheadLogOptions { SegmentMaxBytes = 1L * 1024L * 1024L });
        var value = new string('s', 300_000);
        for (var k = 1; k <= 4; k++)
        {
            await wal.AppendAsync(new SerializableWrapper<int>(k), new SerializableWrapper<string>(value), (ulong)k).ConfigureAwait(true);
        }
    }

    /// <summary>Writes (1,"a") then (2,"") and returns the byte offset where the second frame starts.</summary>
    private async Task<long> WriteStrictFramesAsync()
    {
        long firstFrameEnd;
        using (var wal = new StrictWal(_walDirectory))
        {
            await wal.AppendAsync(new SerializableWrapper<int>(1), new StrictValue("a"), 1UL).ConfigureAwait(true);
            firstFrameEnd = new FileInfo(FirstSegment).Length;
            await wal.AppendAsync(new SerializableWrapper<int>(2), new StrictValue(string.Empty), 2UL).ConfigureAwait(true);
        }

        return firstFrameEnd;
    }

    private async Task WriteStringKeyedFramesAsync()
    {
        using var wal = new Boutquin.Storage.Infrastructure.WriteAheadLog.WriteAheadLog<
            SerializableWrapper<string>, SerializableWrapper<string>>(_walDirectory);
        await wal.AppendAsync(new SerializableWrapper<string>("k1"), new SerializableWrapper<string>("a"), 1UL).ConfigureAwait(true);
        await wal.AppendAsync(new SerializableWrapper<string>("k2"), new SerializableWrapper<string>("b"), 2UL).ConfigureAwait(true);
    }

    /// <summary>
    /// A value that encodes any text but whose decoder raises the exception type its text names —
    /// the decode faults a key or value contract can raise on bytes its encoder wrote.
    /// </summary>
    public sealed class FaultingValue : ISerializable<FaultingValue>
    {
        public FaultingValue()
            : this("ok")
        {
        }

        public FaultingValue(string text) => Text = text;

        public string Text { get; }

        public void Serialize(Stream stream)
        {
            var utf8 = Encoding.UTF8.GetBytes(Text);
            stream.WriteByte((byte)utf8.Length);
            stream.Write(utf8);
        }

        public static FaultingValue Deserialize(Stream stream)
        {
            var length = stream.ReadByte();
            var utf8 = new byte[length];
            stream.ReadExactly(utf8);
            var text = Encoding.UTF8.GetString(utf8);
            return text switch
            {
                nameof(InvalidDataException) => throw new InvalidDataException("injected decode fault"),
                nameof(UnsupportedFormatVersionException) => throw new UnsupportedFormatVersionException(127, 1),
                nameof(NotSupportedException) => throw new NotSupportedException("injected decode fault"),
                nameof(OverflowException) => throw new OverflowException("injected decode fault"),
                nameof(InvalidOperationException) => throw new InvalidOperationException("injected decode fault"),
                nameof(KeyNotFoundException) => throw new KeyNotFoundException("injected decode fault"),
                _ => new FaultingValue(text),
            };
        }
    }

    /// <summary>
    /// A value whose decoder is stricter than its encoder: it writes empty text but refuses to
    /// read it back — the writer/reader asymmetry validation tightening produces.
    /// </summary>
    public sealed class StrictValue : ISerializable<StrictValue>
    {
        public StrictValue()
            : this("x")
        {
        }

        public StrictValue(string text) => Text = text;

        public string Text { get; }

        public void Serialize(Stream stream)
        {
            var utf8 = Encoding.UTF8.GetBytes(Text);
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(length, utf8.Length);
            stream.Write(length);
            stream.Write(utf8);
        }

        public static StrictValue Deserialize(Stream stream)
        {
            Span<byte> length = stackalloc byte[4];
            stream.ReadExactly(length);
            var utf8 = new byte[BinaryPrimitives.ReadInt32LittleEndian(length)];
            stream.ReadExactly(utf8);
            if (utf8.Length == 0)
            {
                throw new FormatException("StrictValue text must not be empty.");
            }

            return new StrictValue(Encoding.UTF8.GetString(utf8));
        }
    }
}
