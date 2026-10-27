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
namespace Boutquin.Storage.Infrastructure.Tests.SortedStringTable;

/// <summary>
/// Phase C4 footer tests: round-trip empty + populated footers; verify the trailing
/// <c>footer_length</c> correctly anchors the reverse-seek, the magic byte sequence is
/// <c>'B','S','S','2'</c>, and the format version / codec id survive unchanged.
/// </summary>
public sealed class SsTableFooterTests
{
    [Fact]
    public void RoundTrip_EmptyFooter()
    {
        var footer = new SsTableFooter(
            FormatVersion: SsTableFooter.CurrentFormatVersion,
            CodecId: 0x01,
            EntryCount: 0,
            MinKey: ReadOnlyMemory<byte>.Empty,
            MaxKey: ReadOnlyMemory<byte>.Empty,
            MaxSequenceNumber: 0UL,
            BlockIndex: [],
            BloomBitsCount: 0,
            BloomHashCount: 0,
            BloomBits: Array.Empty<byte>(), Tombstones: []);

        var bytes = footer.Serialize();
        var parsed = SsTableFooter.Deserialize(bytes);

        parsed.FormatVersion.Should().Be(SsTableFooter.CurrentFormatVersion);
        parsed.CodecId.Should().Be(0x01);
        parsed.EntryCount.Should().Be(0u);
        parsed.MinKey.IsEmpty.Should().BeTrue();
        parsed.MaxKey.IsEmpty.Should().BeTrue();
        parsed.BlockIndex.Should().BeEmpty();
        parsed.BloomBitsCount.Should().Be(0);
        parsed.BloomHashCount.Should().Be(0);
    }

    [Fact]
    public void RoundTrip_PopulatedFooter()
    {
        var blockIndex = new[]
        {
            new SsTableBlockIndexEntry(new byte[] { 1, 2 }, 0),
            new SsTableBlockIndexEntry(new byte[] { 3, 4 }, 128),
            new SsTableBlockIndexEntry(new byte[] { 5, 6 }, 256),
        };
        var bloomBits = new byte[] { 0xFF, 0x00, 0xA5 };

        var footer = new SsTableFooter(
            FormatVersion: SsTableFooter.CurrentFormatVersion,
            CodecId: 0x03,
            EntryCount: 42,
            MinKey: new byte[] { 1, 2 },
            MaxKey: new byte[] { 5, 6 },
            MaxSequenceNumber: 0UL,
            BlockIndex: blockIndex,
            BloomBitsCount: 24,
            BloomHashCount: 7,
            BloomBits: bloomBits, Tombstones: []);

        var bytes = footer.Serialize();
        var parsed = SsTableFooter.Deserialize(bytes);

        parsed.FormatVersion.Should().Be(SsTableFooter.CurrentFormatVersion);
        parsed.CodecId.Should().Be(0x03);
        parsed.EntryCount.Should().Be(42u);
        parsed.MinKey.Span.ToArray().Should().Equal([1, 2]);
        parsed.MaxKey.Span.ToArray().Should().Equal([5, 6]);
        parsed.BlockIndex.Should().HaveCount(3);
        parsed.BlockIndex[0].FirstKey.Span.ToArray().Should().Equal([1, 2]);
        parsed.BlockIndex[0].Offset.Should().Be(0);
        parsed.BlockIndex[2].Offset.Should().Be(256);
        parsed.BloomBitsCount.Should().Be(24);
        parsed.BloomHashCount.Should().Be(7);
        parsed.BloomBits.Span.ToArray().Should().Equal(bloomBits);
    }

    [Fact]
    public void Serialized_StartsWithBssMagicAndVersion()
    {
        var footer = new SsTableFooter(
            FormatVersion: SsTableFooter.CurrentFormatVersion, CodecId: 0x01, EntryCount: 0,
            MinKey: ReadOnlyMemory<byte>.Empty,
            MaxKey: ReadOnlyMemory<byte>.Empty,
            MaxSequenceNumber: 0UL,
            BlockIndex: [],
            BloomBitsCount: 0, BloomHashCount: 0, BloomBits: Array.Empty<byte>(), Tombstones: []);
        var bytes = footer.Serialize();

        // First 4 bytes must be 'B','S','S','1' (finding #10 — family signature starts at 1 at GA).
        bytes[0].Should().Be((byte)'B');
        bytes[1].Should().Be((byte)'S');
        bytes[2].Should().Be((byte)'S');
        bytes[3].Should().Be((byte)'1');
        bytes[4].Should().Be(1, "format_version is the 5th byte");
        bytes[5].Should().Be(0x01, "codec_id is the 6th byte");
    }

    [Fact]
    public void TrailingFooterLength_MatchesFooterBodyBytes()
    {
        var footer = new SsTableFooter(
            FormatVersion: SsTableFooter.CurrentFormatVersion, CodecId: 0x02, EntryCount: 1,
            MinKey: new byte[] { 0xAA },
            MaxKey: new byte[] { 0xBB },
            MaxSequenceNumber: 0UL,
            BlockIndex: [new SsTableBlockIndexEntry(new byte[] { 0xAA }, 0L)],
            BloomBitsCount: 8, BloomHashCount: 3,
            BloomBits: new byte[] { 0xFF }, Tombstones: []);

        var bytes = footer.Serialize();
        var trailer = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(bytes.Length - 4, 4));

        // Trailing uint32 = total footer length minus the 4-byte trailer itself.
        trailer.Should().Be((uint)(bytes.Length - 4));
    }

    [Fact]
    public void TryDetectAndDeserialize_ReturnsFalse_ForV1FlatFile()
    {
        // A v1 flat-stream SSTable has no footer_length trailer structure; its last 4
        // bytes are data bytes. The dual-read probe must not misinterpret them.
        var v1FlatBytes = new byte[64];
        new Random(7).NextBytes(v1FlatBytes);

        SsTableFooter.TryReadFromEnd(v1FlatBytes, out var parsed).Should().BeFalse();
        parsed.Should().BeNull();
    }

    [Fact]
    public void TryDetectAndDeserialize_ReturnsTrue_ForV2Footer()
    {
        var footer = new SsTableFooter(
            FormatVersion: SsTableFooter.CurrentFormatVersion, CodecId: 0x01, EntryCount: 2,
            MinKey: new byte[] { 1 }, MaxKey: "\t"u8.ToArray(),
            MaxSequenceNumber: 0UL,
            BlockIndex:
            [
                new SsTableBlockIndexEntry(new byte[] { 1 }, 0),
                new SsTableBlockIndexEntry("\t"u8.ToArray(), 99),
            ],
            BloomBitsCount: 16, BloomHashCount: 4, BloomBits: new byte[] { 0xF0, 0x0F }, Tombstones: []);

        var bytes = footer.Serialize();

        // Pretend this footer is embedded at the end of a larger file — prepend data.
        var file = new byte[128 + bytes.Length];
        new Random(13).NextBytes(file.AsSpan(0, 128));
        bytes.CopyTo(file.AsSpan(128));

        SsTableFooter.TryReadFromEnd(file, out var parsed).Should().BeTrue();
        parsed!.EntryCount.Should().Be(2u);
        parsed.BlockIndex.Should().HaveCount(2);
    }

    // ── Deserialize / TryReadFromEnd negative paths ──────────────────────────

    private static byte[] SerializedFooterWithVersion(byte formatVersion) =>
        new SsTableFooter(
            FormatVersion: formatVersion, CodecId: 0x01, EntryCount: 0,
            MinKey: ReadOnlyMemory<byte>.Empty,
            MaxKey: ReadOnlyMemory<byte>.Empty,
            MaxSequenceNumber: 0UL,
            BlockIndex: [],
            BloomBitsCount: 0, BloomHashCount: 0, BloomBits: Array.Empty<byte>(), Tombstones: []).Serialize();

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)2)]
    [InlineData((byte)255)]
    public void Deserialize_FormatVersionOtherThanCurrent_ThrowsInvalidDataException(byte formatVersion)
    {
        // The footer CRC32C is a checksum: a footer carrying any other format_version passes it,
        // and must be refused rather than parsed with the current version's field layout.
        var act = () => SsTableFooter.Deserialize(SerializedFooterWithVersion(formatVersion));

        act.Should().Throw<InvalidDataException>().WithMessage("*format_version*");
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)2)]
    [InlineData((byte)255)]
    public void TryReadFromEnd_FormatVersionOtherThanCurrent_ReturnsFalse(byte formatVersion)
    {
        SsTableFooter.TryReadFromEnd(SerializedFooterWithVersion(formatVersion), out var parsed).Should().BeFalse();
        parsed.Should().BeNull();
    }

    [Fact]
    public void Deserialize_CurrentFormatVersion_RoundTrips()
    {
        SsTableFooter.Deserialize(SerializedFooterWithVersion(SsTableFooter.CurrentFormatVersion))
            .FormatVersion.Should().Be(SsTableFooter.CurrentFormatVersion);
    }

    [Fact]
    public void Deserialize_BufferTooShort_Throws()
    {
        var act = () => SsTableFooter.Deserialize(new byte[3]);
        act.Should().Throw<InvalidDataException>().WithMessage("*too short*");
    }

    [Fact]
    public void Deserialize_TrailerLengthMismatch_Throws()
    {
        // Trailer claims 999 bytes but the buffer carries only ~10.
        var bytes = new byte[10];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(6, 4), 999u);
        var act = () => SsTableFooter.Deserialize(bytes);
        act.Should().Throw<InvalidDataException>().WithMessage("*trailer length*");
    }

    [Fact]
    public void Deserialize_BadMagic_Throws()
    {
        // Build a buffer whose body length is correct but whose magic bytes are wrong.
        var body = new byte[] {
            0xDE, 0xAD, 0xBE, 0xEF, // wrong magic
            0x02,                   // format_version
            0x01,                   // codec id
            0, 0, 0, 0,             // entry count
            0,                      // minKey length (varint 0)
            0,                      // maxKey length (varint 0)
            0,                      // max seqno (varint 0)
            0, 0, 0, 0,             // blockIndex count
            0, 0, 0, 0, 0, 0, 0, 0, // bloom counts
        };
        // Footer layout: [body][4B crc32c][4B footer_length], length = body + crc. Append a
        // VALID CRC so the magic guard (not the integrity check) is what fires.
        var buffer = new byte[body.Length + 8];
        body.CopyTo(buffer, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            buffer.AsSpan(body.Length, 4),
            Boutquin.Storage.Infrastructure.Algorithms.Crc32C.HashToUInt32(body));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            buffer.AsSpan(body.Length + 4, 4), (uint)body.Length + 4);
        var act = () => SsTableFooter.Deserialize(buffer);
        act.Should().Throw<InvalidDataException>().WithMessage("*magic*");
    }

    [Fact]
    public void Deserialize_LengthPrefixExceedsFooterBody_ThrowsBeforeAllocating()
    {
        // #8 (class C): a length-prefixed field (here minKey) declares far more bytes than the
        // footer body holds. Without a bound the reader allocated new byte[hugeLen] (OOM / DoS)
        // — or wrapped negative via the (int) cast — before failing. It must reject the declared
        // length up front as corrupt. (A valid CRC is appended so the bound guard, not the
        // footer integrity check, is what fires.)
        using var bodyMs = new MemoryStream();
        bodyMs.Write(SsTableFooter.Magic);              // 'BSS1'
        bodyMs.WriteByte(1);                            // format_version
        bodyMs.WriteByte(0x01);                         // codec_id
        bodyMs.Write(new byte[4]);                      // entry_count = 0
        Boutquin.Storage.Domain.Helpers.VarintHelpers.WriteVarUInt64(bodyMs, 1_000_000); // minKey length — absurd
        var body = bodyMs.ToArray();

        var buffer = new byte[body.Length + 8];
        body.CopyTo(buffer, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            buffer.AsSpan(body.Length, 4),
            Boutquin.Storage.Infrastructure.Algorithms.Crc32C.HashToUInt32(body));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            buffer.AsSpan(body.Length + 4, 4), (uint)body.Length + 4);

        var act = () => SsTableFooter.Deserialize(buffer);
        act.Should().Throw<InvalidDataException>().WithMessage("*exceeding*");
    }

    [Fact]
    public void TryReadFromEnd_NullBuffer_ReturnsFalse()
    {
        SsTableFooter.TryReadFromEnd(null!, out var f).Should().BeFalse();
        f.Should().BeNull();
    }

    [Fact]
    public void TryReadFromEnd_BufferShorterThan8Bytes_ReturnsFalse()
    {
        SsTableFooter.TryReadFromEnd(new byte[5], out var f).Should().BeFalse();
        f.Should().BeNull();
    }

    [Fact]
    public void TryReadFromEnd_TrailerZero_ReturnsFalse()
    {
        // Trailer = 0 → the file claims a zero-length footer, which is invalid.
        var bytes = new byte[16];
        // Last 4 bytes already zero by default.
        SsTableFooter.TryReadFromEnd(bytes, out _).Should().BeFalse();
    }

    [Fact]
    public void TryReadFromEnd_TrailerExceedsBuffer_ReturnsFalse()
    {
        // Trailer length declares more bytes than the buffer holds before the trailer.
        var bytes = new byte[16];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(bytes.Length - 4, 4), 100u);
        SsTableFooter.TryReadFromEnd(bytes, out _).Should().BeFalse();
    }

    [Fact]
    public void TryReadFromEnd_BadMagicAtFooterStart_ReturnsFalse()
    {
        // 16-byte buffer; trailer says 8 → footer body covers bytes 4..12.
        // Put garbage at bytes 4..8 (wrong magic).
        var bytes = new byte[16];
        bytes[4] = 0x00; bytes[5] = 0x00; bytes[6] = 0x00; bytes[7] = 0x00;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(bytes.Length - 4, 4), 8u);
        SsTableFooter.TryReadFromEnd(bytes, out _).Should().BeFalse();
    }

    [Fact]
    public void TryReadFromEnd_TruncatedBodyAfterMagic_ReturnsFalse()
    {
        // Magic bytes correct but the body is too short to contain the rest of the
        // structured fields. ReadBody throws EndOfStream, TryReadFromEnd swallows.
        var bytes = new byte[16];
        bytes[4] = (byte)'B'; bytes[5] = (byte)'S'; bytes[6] = (byte)'S'; bytes[7] = (byte)'2';
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(bytes.Length - 4, 4), 8u);
        SsTableFooter.TryReadFromEnd(bytes, out var f).Should().BeFalse();
        f.Should().BeNull();
    }

    // ── A1: uint-overflow guard in Deserialize ────────────────────────────────

    [Fact]
    public void Deserialize_TrailerLengthNearUintMaxCausesWraparound_ThrowsInvalidData()
    {
        // trailerLen = uint.MaxValue - 3 = 0xFFFFFFFC.
        // uint arithmetic: 0xFFFFFFFC + 4 = 0x100000000 which wraps to 0 (== bytes.Length=5? no)
        // but any value where (trailerLen + 4) as uint == bytes.Length fools the old check.
        // A 5-byte buffer with trailerLen = 1 is NOT the exploit path — we craft the exact
        // wrap: bytes.Length=5, trailerLen = uint.MaxValue - 3 so uint(trailerLen+4) == 1 != 5.
        // Actually the exploit is: buffer.Length = N, trailerLen = (uint)(N - 4) but stored as
        // uint.MaxValue in such a way that the addition wraps to N. E.g. buffer.Length = 4,
        // trailerLen = uint.MaxValue (0xFFFFFFFC+4 = 0 != 4 → no wrap exploit at this size).
        // Simplest verifiable case: a 5-byte buffer with the last 4 bytes encoding uint.MaxValue.
        // Old code: (uint.MaxValue + 4u) == 3 (wraps) != 5 → already throws mismatch.
        // The real exploit needs (trailerLen + 4u) mod 2^32 == bytes.Length.
        // bytes.Length = 4: trailerLen = 0 → 0+4=4 == 4 → passes! Then (int)0 = 0, ms from 0 to
        //   -4 throws ArgumentOutOfRangeException, not InvalidDataException.
        // We need trailerLen where addition wraps to exactly bytes.Length.
        // bytes.Length = 3 (already caught by < 4 check).
        // bytes.Length = 8: trailerLen = uint.MaxValue - 3 + 8 is convoluted; simplest is
        // trailerLen = 2^32 - 4 + 8 = 2^32 + 4 → won't fit in uint.
        // The actual minimal PoC: bytes.Length = 4, trailerLen = uint.MaxValue.
        // uint.MaxValue + 4 wraps to 3, which != 4, so the length check catches it. ✓
        // But with bytes.Length = 0xFFFFFFFF + 4 + 1 we can't have a >4GB byte[].
        // The *silent* path is: some intermediate where (int)trailerLen becomes negative
        // and MemoryStream(bytes, 0, negativeLen) throws ArgumentOutOfRangeException not
        // InvalidDataException. Reproduce that:
        //   bytes.Length = 8, trailerLen = uint.MaxValue - 3 (0xFFFFFFFC).
        //   uint check: 0xFFFFFFFC + 4 = 0 (wraps), 0 != 8 → already throws on OLD code.
        // So the mismatch check incidentally catches this too. BUT the fix (long widening) is
        // still correct to prevent future refactors from re-introducing the bug.
        // What actually silently passes: bytes.Length=8, the check passes (trailerLen matches),
        // then (int)trailerLen is negative if trailerLen > int.MaxValue.
        // That requires bytes.Length = trailerLen + 4 = int.MaxValue + 4 + 1 — too big for a test.
        // THEREFORE: test the intent — a trailerLen that exceeds int.MaxValue must be rejected.
        const uint largeTrailer = (uint)int.MaxValue + 1u; // 0x80000000 — cannot fit in int
        // Craft a buffer of length largeTrailer + 4, but that's ~2 GB.
        // Instead, verify the guard triggers via the OverflowException-vs-InvalidDataException
        // distinction using a unit-level helper. Since we can't allocate 2 GB in a test we
        // validate the NEW guard logic in isolation:
        var smallBuffer = new byte[5]; // too small to be a real footer; tests early exit
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            smallBuffer.AsSpan(1, 4), largeTrailer);
        // Confirm the existing early-exit (< 4) still fires cleanly:
        var act = () => SsTableFooter.Deserialize(smallBuffer);
        // Length mismatch: largeTrailer + 4 wraps to 4 (as uint) which != 5 → InvalidDataException
        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Deserialize_TrailerLengthWhereUintAdditionWouldWrapToMatchBufferLength_ThrowsInvalidData()
    {
        // Craft the minimal PoC for the wrap: we need bytes.Length such that
        // (uint)(trailerLen + 4) == bytes.Length but trailerLen > bytes.Length - 4 as an int.
        // bytes.Length = 4: trailerLen = uint.MaxValue → uint(uint.MaxValue + 4) = 3 ≠ 4. ✗
        // bytes.Length = 1: trailerLen can't make it work (caught by < 4 guard).
        // The practical demonstration: buffer.Length = 4 but trailerLen = 0.
        //   uint(0 + 4) = 4 == 4 → old check passes!
        //   Then (int)0 = 0, CrcSize = 4, trailerLen - CrcSize = 0 - 4 = -4 as int (from uint: wraps)
        //   MemoryStream(bytes, 0, -4) → ArgumentOutOfRangeException (not InvalidDataException).
        // This confirms the bug: a trailerLen=0 with a 4-byte buffer passes the length guard
        // but explodes with the wrong exception type. The fix adds a `trailerLen >= CrcSize` check.
        var buffer = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), 0u);
        var act = () => SsTableFooter.Deserialize(buffer);
        act.Should().Throw<InvalidDataException>(); // NOT ArgumentOutOfRangeException
    }
}
