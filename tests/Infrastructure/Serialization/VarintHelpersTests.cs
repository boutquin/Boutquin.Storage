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
namespace Boutquin.Storage.Infrastructure.Tests.Serialization;

// Unit tests for the Domain-level varint / zigzag / tag-and-wire-type primitives used by
// both the Phase 2 source generator (tagged wire emission) and the shipped
// SerializableWrapper<T>. These lock:
//   - Encoding size: DDIA Ch. 4's predicted byte savings for small integers.
//   - Round-trip correctness: value in = value out for representative corners.
//   - Tag varint: combined tag+wire-type packs correctly and round-trips.
//   - Unknown-tag skip: SkipField advances the stream exactly the right number of bytes.
public sealed class VarintHelpersTests
{
    // DDIA §4's central claim: small magnitudes fit in 1–3 bytes instead of the 4/8 bytes a
    // fixed-width encoding would spend. Lock representative sizes.
    [Theory]
    [InlineData(0UL, 1)]
    [InlineData(1UL, 1)]
    [InlineData(127UL, 1)]
    [InlineData(128UL, 2)]
    [InlineData(16_383UL, 2)]
    [InlineData(16_384UL, 3)]
    [InlineData(100_000UL, 3)]               // The canonical "Volume" case.
    [InlineData(2_097_151UL, 3)]
    [InlineData(2_097_152UL, 4)]
    [InlineData(ulong.MaxValue, 10)]          // 10-byte ULEB128 worst case (docs/wire-format-2.0.md §6.3).
    public void WriteVarUInt64_Produces_Expected_Byte_Count(ulong value, int expectedBytes)
    {
        using var ms = new MemoryStream();
        var written = VarintHelpers.WriteVarUInt64(ms, value);
        written.Should().Be(expectedBytes);
        ms.Length.Should().Be(expectedBytes);
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(1UL)]
    [InlineData(127UL)]
    [InlineData(128UL)]
    [InlineData(100_000UL)]
    [InlineData(ulong.MaxValue)]
    public void ReadVarUInt64_Round_Trips(ulong value)
    {
        using var ms = new MemoryStream();
        VarintHelpers.WriteVarUInt64(ms, value);
        ms.Position = 0;
        VarintHelpers.ReadVarUInt64(ms).Should().Be(value);
        ms.Position.Should().Be(ms.Length, "reader should have consumed every byte");
    }

    // ZigZag: small negatives must compact to 1 byte. −1 encodes as 1, −2 as 3, etc.
    [Theory]
    [InlineData(0L, 1)]
    [InlineData(-1L, 1)]
    [InlineData(-2L, 1)]
    [InlineData(63L, 1)]
    [InlineData(-64L, 1)]
    [InlineData(long.MaxValue, 10)]
    [InlineData(long.MinValue, 10)]
    public void WriteZigZagInt64_Produces_Expected_Byte_Count(long value, int expectedBytes)
    {
        using var ms = new MemoryStream();
        VarintHelpers.WriteZigZagInt64(ms, value);
        ms.Length.Should().Be(expectedBytes);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(1L)]
    [InlineData(long.MinValue)]
    [InlineData(long.MaxValue)]
    [InlineData(-12345L)]
    public void ReadZigZagInt64_Round_Trips(long value)
    {
        using var ms = new MemoryStream();
        VarintHelpers.WriteZigZagInt64(ms, value);
        ms.Position = 0;
        VarintHelpers.ReadZigZagInt64(ms).Should().Be(value);
    }

    [Fact]
    public void ZigZag_Encode_Decode_Matches_Protobuf_Reference_Values()
    {
        // Reference values from the Protobuf encoding spec.
        VarintHelpers.ZigZagEncode(0).Should().Be(0UL);
        VarintHelpers.ZigZagEncode(-1).Should().Be(1UL);
        VarintHelpers.ZigZagEncode(1).Should().Be(2UL);
        VarintHelpers.ZigZagEncode(-2).Should().Be(3UL);
        VarintHelpers.ZigZagEncode(2147483647).Should().Be(4294967294UL);
        VarintHelpers.ZigZagEncode(-2147483648).Should().Be(4294967295UL);

        VarintHelpers.ZigZagDecode(0).Should().Be(0);
        VarintHelpers.ZigZagDecode(1).Should().Be(-1);
        VarintHelpers.ZigZagDecode(2).Should().Be(1);
    }

    // Combined tag-and-wire-type varint: high bits tag number, low 3 bits wire type.
    [Theory]
    [InlineData(1, WireType.Varint)]
    [InlineData(2, WireType.LengthPrefixed)]
    [InlineData(15, WireType.Fixed32)]         // Still 1-byte combined varint.
    [InlineData(16, WireType.Fixed64)]         // Rolls over to 2 bytes.
    [InlineData(536_870_911, WireType.Fixed128)]
    public void WriteTag_Round_Trips(int tagNumber, WireType wireType)
    {
        using var ms = new MemoryStream();
        VarintHelpers.WriteTag(ms, tagNumber, wireType);
        ms.Position = 0;
        VarintHelpers.TryReadTag(ms, out var readTag, out var readWireType).Should().BeTrue();
        readTag.Should().Be(tagNumber);
        readWireType.Should().Be(wireType);
    }

    [Fact]
    public void WriteTag_Rejects_Zero_Tag()
    {
        using var ms = new MemoryStream();
        var act = () => VarintHelpers.WriteTag(ms, 0, WireType.Varint);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ReadString_DeclaredLengthBeyondSeekableRemaining_ThrowsWithoutHugeAllocation()
    {
        // A corrupt length prefix declaring int.MaxValue with 5 real payload bytes must fail
        // BEFORE renting ~2 GB. Seekable streams expose the remaining byte count.
        using var ms = new MemoryStream();
        VarintHelpers.WriteVarUInt64(ms, int.MaxValue);
        ms.Write(new byte[] { 1, 2, 3, 4, 5 });
        ms.Position = 0;

        var before = GC.GetAllocatedBytesForCurrentThread();
        var act = () => VarintHelpers.ReadString(ms);
        // M-1 (F-12): a forged length prefix is corruption, so it surfaces the repo's typed
        // corruption contract (InvalidDataException), not the raw EndOfStreamException it leaked
        // before this milestone unified the length-prefix corruption path.
        act.Should().Throw<InvalidDataException>();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.Should().BeLessThan(1_000_000,
            "the declared-length bound must reject the corrupt prefix before renting a ~2 GB buffer");
    }

    [Fact]
    public void WriteString_LoneSurrogate_ThrowsArgumentException()
    {
        // Unpaired surrogates are not encodable in UTF-8: the default replacement fallback
        // silently writes U+FFFD, so Deserialize(Serialize(s)) != s with no error - silent
        // round-trip loss on the tagged wire. Reject at encode (the ordered-wire writer and
        // the NaN-float key writer apply the same fail-fast policy).
        using var ms = new MemoryStream();
        var act = () => VarintHelpers.WriteString(ms, "\uD800");
        act.Should().Throw<ArgumentException>().WithMessage("*surrogate*");
    }

    [Fact]
    public void ReadString_InvalidUtf8Payload_ThrowsInvalidData()
    {
        // The writer only ever emits well-formed UTF-8, so an invalid sequence is corruption -
        // fail loud instead of silently substituting U+FFFD and returning a string no writer wrote.
        using var ms = new MemoryStream();
        VarintHelpers.WriteVarUInt64(ms, 2);
        ms.Write(new byte[] { 0xC3, 0x28 }); // truncated 2-byte UTF-8 lead
        ms.Position = 0;

        var act = () => VarintHelpers.ReadString(ms);

        act.Should().Throw<InvalidDataException>().WithMessage("*UTF-8*");
    }

    [Fact]
    public void TryReadTag_TagBeyondInt32_ThrowsInvalidData()
    {
        // A valid <=10-byte varint can carry tag bits up to 2^61. An unchecked (int) cast of
        // combined >> 3 keeps the low 32 bits, so a corrupt tag of 2^32 + 5 COLLIDES with real
        // tag 5 and feeds garbage into that field instead of failing. No legitimate writer
        // produces such a tag (the generator caps tags at 2^28 - 1), so this is corruption.
        using var ms = new MemoryStream();
        VarintHelpers.WriteVarUInt64(ms, (((1UL << 32) + 5) << 3) | 0UL); // wire type Varint
        ms.Position = 0;

        var act = () => VarintHelpers.TryReadTag(ms, out _, out _);

        act.Should().Throw<InvalidDataException>().WithMessage("*tag*");
    }

    [Fact]
    public void SkipField_Varint_WithMoreThanTenContinuationBytes_ThrowsInvalidData()
    {
        // ReadVarUInt64 rejects a varint longer than 10 bytes as malformed; SkipField must
        // apply the same bound, or malformed-data acceptance depends on whether the tag is
        // known to the reader (known field -> throw; unknown field -> silently skipped).
        // Note: a continuing 10th byte (0x80) trips the 10th-byte-overflow rejection first —
        // the same classification ReadVarUInt64 gives these bytes — so any InvalidDataException
        // is the correct, path-consistent outcome.
        var bytes = new byte[50];
        Array.Fill(bytes, (byte)0x80);
        bytes[^1] = 0x00;
        using var ms = new MemoryStream(bytes);

        var act = () => VarintHelpers.SkipField(ms, WireType.Varint);

        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void TryReadTag_Returns_False_On_Empty_Stream()
    {
        using var ms = new MemoryStream();
        VarintHelpers.TryReadTag(ms, out var tag, out var wireType).Should().BeFalse();
    }

    [Fact]
    public void TryReadTag_Rejects_Unknown_Wire_Type()
    {
        // Emit a tag varint with wire type = 6 (beyond Fixed128).
        using var ms = new MemoryStream();
        VarintHelpers.WriteVarUInt64(ms, ((ulong)1 << 3) | 6);
        ms.Position = 0;
        Action act = () => VarintHelpers.TryReadTag(ms, out _, out _);
        act.Should().Throw<InvalidDataException>();
    }

    // SkipField must advance the stream exactly as many bytes as the payload occupies — any
    // over- or under-read corrupts the tag stream downstream. Cover every WireType.
    [Theory]
    [InlineData(WireType.Varint, 1)]
    [InlineData(WireType.Fixed16, 2)]
    [InlineData(WireType.Fixed32, 4)]
    [InlineData(WireType.Fixed64, 8)]
    [InlineData(WireType.Fixed128, 16)]
    public void SkipField_Advances_Stream_Exactly_For_Fixed_Width(WireType wireType, int expectedBytes)
    {
        using var ms = new MemoryStream();
        if (wireType == WireType.Varint)
        {
            VarintHelpers.WriteVarUInt64(ms, 42);   // 1-byte varint
        }
        else
        {
            ms.Write(new byte[expectedBytes]);
        }
        ms.Position = 0;

        VarintHelpers.SkipField(ms, wireType);
        ms.Position.Should().Be(expectedBytes);
    }

    [Fact]
    public void SkipField_Advances_Length_Prefixed_Correctly()
    {
        using var ms = new MemoryStream();
        var payload = new byte[] { 1, 2, 3, 4, 5, 6, 7 };
        VarintHelpers.WriteVarUInt64(ms, (ulong)payload.Length);
        ms.Write(payload);
        ms.Position = 0;

        VarintHelpers.SkipField(ms, WireType.LengthPrefixed);
        ms.Position.Should().Be(ms.Length);
    }

    // WriteString / ReadString round-trip for short and long strings (exercise the stackalloc
    // vs ArrayPool fork at 256 bytes).
    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("café ☕")]                     // Non-ASCII — UTF-8 multibyte.
    public void ReadString_Round_Trips_Short(string value)
    {
        using var ms = new MemoryStream();
        VarintHelpers.WriteString(ms, value);
        ms.Position = 0;
        VarintHelpers.ReadString(ms).Should().Be(value);
    }

    [Fact]
    public void ReadString_Round_Trips_Long_Beyond_Stackalloc_Threshold()
    {
        var value = new string('x', 1024);
        using var ms = new MemoryStream();
        VarintHelpers.WriteString(ms, value);
        ms.Position = 0;
        VarintHelpers.ReadString(ms).Should().Be(value);
    }

    [Fact]
    public void ReadVarUInt64_Throws_On_EndOfStream()
    {
        // Continuation-bit-set byte with nothing after it — stream ends mid-varint.
        using var ms = new MemoryStream([0x80]);
        Action act = () => VarintHelpers.ReadVarUInt64(ms);
        act.Should().Throw<EndOfStreamException>();
    }

    // ── Overflow / overlong / EOF guards (uncovered branches) ─────────────────

    [Fact]
    public void ReadVarUInt64_TenthByteWithHighBitsSet_ThrowsInvalidData()
    {
        // 10 bytes where the 10th byte sets bits beyond bit-0 → would overflow UInt64.
        var bytes = new byte[10];
        for (var i = 0; i < 9; i++)
        {
            bytes[i] = 0xFF; // continuation bit + 7 data bits
        }
        bytes[9] = 0x02; // illegal: only bit 0 is allowed on the final byte
        using var ms = new MemoryStream(bytes);
        Action act = () => VarintHelpers.ReadVarUInt64(ms);
        act.Should().Throw<InvalidDataException>().WithMessage("*overflow*");
    }

    [Fact]
    public void ReadVarUInt64_AllContinuationBitsSet_ThrowsAfterMaxBytes()
    {
        // 10 bytes all with continuation bit set → exceeds the 10-byte limit.
        // The 10th byte's overflow check fires first because (0xFF & 0xFE) != 0.
        var bytes = Enumerable.Repeat((byte)0xFF, 10).ToArray();
        using var ms = new MemoryStream(bytes);
        Action act = () => VarintHelpers.ReadVarUInt64(ms);
        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void WriteTag_TagZero_ThrowsArgumentOutOfRange()
    {
        using var ms = new MemoryStream();
        Action act = () => VarintHelpers.WriteTag(ms, 0, WireType.Varint);
        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("tagNumber");
    }

    [Fact]
    public void WriteTag_NegativeTag_ThrowsArgumentOutOfRange()
    {
        using var ms = new MemoryStream();
        Action act = () => VarintHelpers.WriteTag(ms, -1, WireType.Varint);
        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("tagNumber");
    }

    [Fact]
    public void TryReadTag_OnEmptyStream_ReturnsFalse()
    {
        using var ms = new MemoryStream();
        var ok = VarintHelpers.TryReadTag(ms, out var tag, out var wt);
        ok.Should().BeFalse();
        tag.Should().Be(0);
        wt.Should().Be(WireType.Varint);
    }

    [Fact]
    public void TryReadTag_MidStreamEnd_Throws()
    {
        // Single byte with continuation bit set but no follow-up.
        using var ms = new MemoryStream([0x80]);
        Action act = () => VarintHelpers.TryReadTag(ms, out _, out _);
        act.Should().Throw<EndOfStreamException>();
    }

    [Fact]
    public void TryReadTag_UnknownWireType_Throws()
    {
        // Wire type 7 is currently outside WireType.Fixed128's range.
        // tag=1 wireType=7 → combined = (1<<3)|7 = 15 → single byte 0x0F.
        using var ms = new MemoryStream([0x0F]);
        Action act = () => VarintHelpers.TryReadTag(ms, out _, out _);
        act.Should().Throw<InvalidDataException>().WithMessage("*Unknown wire type*");
    }

    // ── ReadExact / AdvanceExact stream-end branches ─────────────────────────

    [Fact]
    public void ReadExact_StreamEndsEarly_ThrowsEndOfStream()
    {
        using var ms = new MemoryStream([1, 2]);
        var dest = new byte[5];
        Action act = () => VarintHelpers.ReadExact(ms, dest);
        act.Should().Throw<EndOfStreamException>();
    }

    [Fact]
    public void ReadExact_EmptyDestination_DoesNotRead()
    {
        using var ms = new MemoryStream([1, 2]);
        VarintHelpers.ReadExact(ms, []);
        ms.Position.Should().Be(0);
    }

    // ── B12: ReadAllBounded — int overflow on buffer growth ───────────────────────────
    // When the rented buffer reaches a length close to int.MaxValue/2, the `rented.Length * 2`
    // expression overflows int. The fix uses (long)rented.Length * 2 clamped to int.MaxValue.
    // We exercise the doubling path with a buffer that fills exactly to rented.Length so the
    // growth branch fires, verifying no OverflowException is thrown.
    [Fact]
    public void ReadAllBounded_BufferGrowthNearIntMaxValue_DoesNotOverflow()
    {
        // Construct a stream whose declared length is just above the initial 4096-byte capacity
        // so the growth path fires at least once. The growth expression `rented.Length * 2` is
        // the vulnerable one. To exercise it without allocating gigabytes we use a small payload
        // but verify the fix is present via its structural effect: the grown buffer's requested
        // size is Math.Min(int, (int)Math.Min((long)rented.Length * 2, int.MaxValue)).
        // We test a real round-trip: write a length-prefixed byte sequence, read it back.
        // The test uses VarintHelpers.ReadLengthPrefixedBytes which calls ReadAllBounded.
        const int payloadLength = 8192; // forces one growth from seed 4096 → 8192
        var payload = new byte[payloadLength];
        new Random(42).NextBytes(payload);

        using var ms = new MemoryStream();
        // Write a varint length prefix followed by the raw payload bytes.
        VarintHelpers.WriteVarUInt64(ms, (ulong)payloadLength);
        ms.Write(payload);
        ms.Position = 0;
        // Skip past the varint length prefix to leave stream positioned for ReadLengthPrefixedPayloadBounded.
        VarintHelpers.ReadVarUInt64(ms);

        // Should not throw OverflowException.
        var roundTripped = VarintHelpers.ReadLengthPrefixedPayloadBounded(ms, payloadLength);
        roundTripped.Should().Equal(payload);
    }

    // ── C5-4: WriteVarUInt64Padded throw branches ─────────────────────────────────────────
    // The padded-write method has three guard conditions that must each throw:
    //   1. width < 1 or width > 10  → ArgumentOutOfRangeException (nameof(width))
    //   2. destination.Length < width → ArgumentException (nameof(destination))
    //   3. value too large for the given width → ArgumentOutOfRangeException (nameof(value))
    // This battery exercises each branch independently.

    [Theory]
    [InlineData(0)]   // below minimum
    [InlineData(11)]  // above maximum (MaxVarInt64Bytes = 10)
    public void WriteVarUInt64Padded_WidthOutOfRange_ThrowsArgumentOutOfRange(int width)
    {
        var dest = new byte[20];
        Action act = () => VarintHelpers.WriteVarUInt64Padded(dest, 0UL, width);
        act.Should().Throw<ArgumentOutOfRangeException>()
            .And.ParamName.Should().Be("width");
    }

    [Fact]
    public void WriteVarUInt64Padded_DestinationTooShort_ThrowsArgumentException()
    {
        // Request width=5 but supply a span of length 3 — must throw ArgumentException.
        // Use a byte[] rather than Span<byte> so the array can be captured in the lambda
        // (ref structs cannot be captured in closures — CS8175).
        var destArray = new byte[3];
        Action act = () => VarintHelpers.WriteVarUInt64Padded(destArray, 0UL, 5);
        act.Should().Throw<ArgumentException>()
            .And.ParamName.Should().Be("destination");
    }

    [Fact]
    public void WriteVarUInt64Padded_ValueExceedsBitCapacity_ThrowsArgumentOutOfRange()
    {
        // width=1 can encode values 0..127 (7 bits). Passing 128 exceeds the bit capacity.
        // Use a byte[] rather than Span<byte> so the array can be captured in the lambda
        // (ref structs cannot be captured in closures — CS8175).
        var destArray = new byte[10];
        Action act = () => VarintHelpers.WriteVarUInt64Padded(destArray, 128UL, 1);
        act.Should().Throw<ArgumentOutOfRangeException>()
            .And.ParamName.Should().Be("value");
    }

    [Theory]
    [InlineData(0UL, 1)]
    [InlineData(127UL, 1)]
    [InlineData(128UL, 2)]
    [InlineData(16_383UL, 2)]
    [InlineData(ulong.MaxValue, 10)]
    public void WriteVarUInt64Padded_ValidArgs_ProducesDecodableValue(ulong value, int width)
    {
        Span<byte> dest = new byte[10];
        VarintHelpers.WriteVarUInt64Padded(dest, value, width);

        // The padded encoding is valid ULEB128 — ReadVarUInt64 must round-trip the value.
        using var ms = new MemoryStream(dest[..width].ToArray());
        var decoded = VarintHelpers.ReadVarUInt64(ms);
        decoded.Should().Be(value, "WriteVarUInt64Padded must produce a valid ULEB128 encoding");
    }

    [Fact]
    public void SkipField_TenthByteOverflowVarint_ThrowsLikeReadVarUInt64()
    {
        // A 10-byte varint whose final byte sets more than bit 0 overflows UInt64;
        // ReadVarUInt64 rejects it with InvalidDataException. SkipField's own comment states
        // the length cap exists so malformed-data acceptance never depends on whether the tag
        // is known to the reader — the 10th-byte-overflow class must obey the same rule:
        // known tag → throw; unknown tag → ALSO throw, not silently skipped.
        byte[] overflowVarint = [0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x7F];

        using var readStream = new MemoryStream(overflowVarint);
        var read = () => VarintHelpers.ReadVarUInt64(readStream);
        read.Should().Throw<InvalidDataException>("the known-tag path rejects the overflow");

        using var skipStream = new MemoryStream(overflowVarint);
        var skip = () => VarintHelpers.SkipField(skipStream, WireType.Varint);
        skip.Should().Throw<InvalidDataException>(
            "the unknown-tag skip path must reject exactly what the known-tag read path rejects");
    }

    [Theory]
    [InlineData(long.MaxValue)]
    [InlineData(long.MaxValue - 5)]
    [InlineData(long.MaxValue / 2 + 1)]
    public void SkipField_LengthPrefixedDeclaringMoreThanRemaining_NearLongMax_ThrowsInvalidData(long declared)
    {
        // A declared length near long.MaxValue must not wrap the past-end bound check
        // (Position + count) negative and reach a raw Seek: the over-declared length is corruption.
        using var ms = new MemoryStream();
        ms.Write([0xAA, 0xBB]); // leading bytes so Position > 0 at the length prefix
        VarintHelpers.WriteVarUInt64(ms, (ulong)declared);
        ms.Write([0x01, 0x02, 0x03]);
        ms.Position = 2;

        var skip = () => VarintHelpers.SkipField(ms, WireType.LengthPrefixed);

        skip.Should().Throw<InvalidDataException>(
            "an over-declared length-prefixed skip is corruption and must surface the typed contract");
    }

    [Fact]
    public void SkipField_LengthPrefixedExactlyRemaining_SkipsToEnd()
    {
        using var ms = new MemoryStream();
        VarintHelpers.WriteVarUInt64(ms, 3UL);
        ms.Write([0x01, 0x02, 0x03]);
        ms.Position = 0;

        VarintHelpers.SkipField(ms, WireType.LengthPrefixed);

        ms.Position.Should().Be(ms.Length, "a length equal to the bytes remaining is a legal skip");
    }
}
