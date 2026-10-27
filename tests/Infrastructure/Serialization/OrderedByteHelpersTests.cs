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

// Unit tests for the Domain-level ordered-byte primitives used by the Phase 3 source-
// generator ordered-wire path for [Key] types. Byte-lex order of the encoded bytes MUST
// equal numeric order of the original values — that is the single invariant LSM SSTable
// binary search and range scan rely on.
public sealed class OrderedByteHelpersTests
{
    // ═════════════════════════════════════════════════════════════════════════════════════
    // Signed Tier 1 — BE + sign-bit flip.
    // ═════════════════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData((sbyte)0, 1)]
    [InlineData(sbyte.MinValue, 1)]
    [InlineData(sbyte.MaxValue, 1)]
    [InlineData((sbyte)-1, 1)]
    [InlineData((sbyte)1, 1)]
    public void WriteInt8_Round_Trips(sbyte value, int expectedBytes)
    {
        using var ms = new MemoryStream();
        OrderedByteHelpers.WriteInt8(ms, value);
        ms.Length.Should().Be(expectedBytes);
        ms.Position = 0;
        OrderedByteHelpers.ReadInt8(ms).Should().Be(value);
    }

    [Fact]
    public void WriteInt8_Byte_Lex_Matches_Numeric_Order()
    {
        // Pick an ordered sample: negative, zero, positive.
        AssertByteLexMatchesNumericOrder<sbyte>(
            [sbyte.MinValue, -50, -1, 0, 1, 50, sbyte.MaxValue],
            OrderedByteHelpers.WriteInt8);
    }

    [Fact]
    public void WriteInt16_Byte_Lex_Matches_Numeric_Order()
    {
        AssertByteLexMatchesNumericOrder<short>(
            [short.MinValue, -10_000, -1, 0, 1, 10_000, short.MaxValue],
            OrderedByteHelpers.WriteInt16);
    }

    [Fact]
    public void WriteInt32_Byte_Lex_Matches_Numeric_Order()
    {
        AssertByteLexMatchesNumericOrder<int>(
            [int.MinValue, -1_000_000, -1, 0, 1, 1_000_000, int.MaxValue],
            OrderedByteHelpers.WriteInt32);
    }

    [Fact]
    public void WriteInt64_Byte_Lex_Matches_Numeric_Order()
    {
        AssertByteLexMatchesNumericOrder<long>(
            [long.MinValue, -1_000_000_000L, -1, 0, 1, 1_000_000_000L, long.MaxValue],
            OrderedByteHelpers.WriteInt64);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(123456789)]
    public void WriteInt32_Round_Trips(int value)
    {
        using var ms = new MemoryStream();
        OrderedByteHelpers.WriteInt32(ms, value);
        ms.Position = 0;
        OrderedByteHelpers.ReadInt32(ms).Should().Be(value);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(long.MinValue)]
    [InlineData(long.MaxValue)]
    [InlineData(-1L)]
    [InlineData(1_000_000_000L)]
    public void WriteInt64_Round_Trips(long value)
    {
        using var ms = new MemoryStream();
        OrderedByteHelpers.WriteInt64(ms, value);
        ms.Position = 0;
        OrderedByteHelpers.ReadInt64(ms).Should().Be(value);
    }

    // ═════════════════════════════════════════════════════════════════════════════════════
    // Unsigned Tier 1 — BE (no sign-flip).
    // ═════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void WriteUInt32_Byte_Lex_Matches_Numeric_Order()
    {
        AssertByteLexMatchesNumericOrder<uint>(
            [0u, 1u, 100u, 1_000_000u, uint.MaxValue],
            OrderedByteHelpers.WriteUInt32);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(uint.MaxValue)]
    [InlineData(123456789u)]
    public void WriteUInt32_Round_Trips(uint value)
    {
        using var ms = new MemoryStream();
        OrderedByteHelpers.WriteUInt32(ms, value);
        ms.Position = 0;
        OrderedByteHelpers.ReadUInt32(ms).Should().Be(value);
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(ulong.MaxValue)]
    [InlineData(1_000_000_000_000UL)]
    public void WriteUInt64_Round_Trips(ulong value)
    {
        using var ms = new MemoryStream();
        OrderedByteHelpers.WriteUInt64(ms, value);
        ms.Position = 0;
        OrderedByteHelpers.ReadUInt64(ms).Should().Be(value);
    }

    // ═════════════════════════════════════════════════════════════════════════════════════
    // String with terminator-escape (0x00 → 0x00 0xFF, terminator 0x00 0x00).
    // ═════════════════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("a\0b")]                     // Embedded null — must escape.
    [InlineData("café ☕")]                   // Multibyte UTF-8.
    [InlineData("\0")]
    [InlineData("\0\0")]
    public void WriteOrderedString_Round_Trips(string value)
    {
        using var ms = new MemoryStream();
        OrderedByteHelpers.WriteOrderedString(ms, value);
        ms.Position = 0;
        OrderedByteHelpers.ReadOrderedString(ms).Should().Be(value);
    }

    [Fact]
    public void WriteOrderedString_Terminator_Is_Zero_Zero()
    {
        using var ms = new MemoryStream();
        OrderedByteHelpers.WriteOrderedString(ms, "ab");
        var bytes = ms.ToArray();
        // UTF-8 "ab" = 0x61 0x62, terminator 0x00 0x00, total 4 bytes.
        bytes.Should().Equal([0x61, 0x62, 0x00, 0x00]);
    }

    [Fact]
    public void WriteOrderedString_Escapes_Embedded_Null()
    {
        using var ms = new MemoryStream();
        OrderedByteHelpers.WriteOrderedString(ms, "\0");
        // Embedded 0x00 → 0x00 0xFF, then terminator 0x00 0x00 = 4 bytes total.
        ms.ToArray().Should().Equal([0x00, 0xFF, 0x00, 0x00]);
    }

    [Fact]
    public void WriteOrderedString_Byte_Lex_Matches_Ordinal_Order()
    {
        // Ordinal comparison and byte-lex of UTF-8 agree for ASCII; this locks that for
        // the wrapper we care about (the bytes we persist must sort the same way the
        // in-memory IComparable string comparison does — via Ordinal by default).
        AssertByteLexMatchesNumericOrder<string>(
            ["", "a", "aa", "ab", "abc", "b", "z"],
            OrderedByteHelpers.WriteOrderedString,
            StringComparer.Ordinal);
    }

    // ═════════════════════════════════════════════════════════════════════════════════════
    // Nullable discriminator — 0x00 (null, sorts first) / 0x01 (present).
    // ═════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Null_Sorts_Before_Present()
    {
        using var nullStream = new MemoryStream();
        OrderedByteHelpers.WriteNullDiscriminator(nullStream, hasValue: false);
        using var presentStream = new MemoryStream();
        OrderedByteHelpers.WriteNullDiscriminator(presentStream, hasValue: true);

        var nullBytes = nullStream.ToArray();
        var presentBytes = presentStream.ToArray();
        CompareBytes(nullBytes, presentBytes).Should().BeNegative(
            "null discriminator 0x00 must sort before present 0x01");
    }

    [Fact]
    public void Null_Discriminator_Round_Trips()
    {
        using var ms = new MemoryStream();
        OrderedByteHelpers.WriteNullDiscriminator(ms, hasValue: false);
        OrderedByteHelpers.WriteNullDiscriminator(ms, hasValue: true);
        ms.Position = 0;
        OrderedByteHelpers.ReadNullDiscriminator(ms).Should().BeFalse();
        OrderedByteHelpers.ReadNullDiscriminator(ms).Should().BeTrue();
    }

    // ═════════════════════════════════════════════════════════════════════════════════════
    // Guid — 16-byte big-endian.
    // ═════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void WriteGuid_Round_Trips()
    {
        var g = new Guid("12345678-1234-5678-1234-567812345678");
        using var ms = new MemoryStream();
        OrderedByteHelpers.WriteGuid(ms, g);
        ms.Length.Should().Be(16);
        ms.Position = 0;
        OrderedByteHelpers.ReadGuid(ms).Should().Be(g);
    }

    // ═════════════════════════════════════════════════════════════════════════════════════
    // IEEE 754 floats — Half / Single / Double — sign-flip positives, complement negatives.
    // NaN rejected at encode time (no total-order position in IEEE 754).
    // ═════════════════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(0.0f)]
    [InlineData(1.0f)]
    [InlineData(-1.0f)]
    [InlineData(float.MinValue)]
    [InlineData(float.MaxValue)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    [InlineData(float.Epsilon)]
    public void WriteSingle_Round_Trips(float value)
    {
        using var ms = new MemoryStream();
        OrderedByteHelpers.WriteSingle(ms, value);
        ms.Length.Should().Be(4);
        ms.Position = 0;
        // ±0 collapse to +0 under sign-bit flip (both encode identically); the IEEE equality
        // comparison treats them equal anyway.
        OrderedByteHelpers.ReadSingle(ms).Should().Be(value == 0f ? 0f : value);
    }

    [Fact]
    public void WriteSingle_Byte_Lex_Matches_Numeric_Order()
    {
        AssertByteLexMatchesNumericOrder<float>(
            [float.NegativeInfinity, float.MinValue, -1e10f, -1.0f, -float.Epsilon, 0f, float.Epsilon, 1.0f, 1e10f, float.MaxValue, float.PositiveInfinity],
            OrderedByteHelpers.WriteSingle);
    }

    [Fact]
    public void WriteSingle_Nan_Is_Rejected()
    {
        using var ms = new MemoryStream();
        var act = () => OrderedByteHelpers.WriteSingle(ms, float.NaN);
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(-1.0)]
    [InlineData(double.MinValue)]
    [InlineData(double.MaxValue)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(double.Epsilon)]
    public void WriteDouble_Round_Trips(double value)
    {
        using var ms = new MemoryStream();
        OrderedByteHelpers.WriteDouble(ms, value);
        ms.Length.Should().Be(8);
        ms.Position = 0;
        OrderedByteHelpers.ReadDouble(ms).Should().Be(value == 0.0 ? 0.0 : value);
    }

    [Fact]
    public void WriteDouble_Byte_Lex_Matches_Numeric_Order()
    {
        AssertByteLexMatchesNumericOrder<double>(
            [double.NegativeInfinity, double.MinValue, -1e200, -1.0, -double.Epsilon, 0.0, double.Epsilon, 1.0, 1e200, double.MaxValue, double.PositiveInfinity],
            OrderedByteHelpers.WriteDouble);
    }

    [Fact]
    public void WriteDouble_Nan_Is_Rejected()
    {
        using var ms = new MemoryStream();
        var act = () => OrderedByteHelpers.WriteDouble(ms, double.NaN);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void WriteHalf_Round_Trips_And_Byte_Lex_Matches_Numeric_Order()
    {
        var samples = new[]
        {
            Half.NegativeInfinity,
            Half.MinValue,
            (Half)(-1.0f),
            -Half.Epsilon,
            (Half)0.0f,
            Half.Epsilon,
            (Half)1.0f,
            Half.MaxValue,
            Half.PositiveInfinity,
        };

        foreach (var v in samples)
        {
            using var ms = new MemoryStream();
            OrderedByteHelpers.WriteHalf(ms, v);
            ms.Length.Should().Be(2);
            ms.Position = 0;
            // ±0 collapse under sign-flip — both encode to the positive-zero pattern.
            var expected = v == (Half)0.0f ? (Half)0.0f : v;
            OrderedByteHelpers.ReadHalf(ms).Should().Be(expected);
        }

        AssertByteLexMatchesNumericOrder<Half>(samples, OrderedByteHelpers.WriteHalf);
    }

    [Fact]
    public void WriteHalf_Nan_Is_Rejected()
    {
        using var ms = new MemoryStream();
        var act = () => OrderedByteHelpers.WriteHalf(ms, Half.NaN);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void WriteHalf_NegativeZero_EncodesEqualToPositiveZero_MatchingCompareTo()
    {
        // Same ±0 collapse the Single/Double encoders pin: Half.CompareTo treats -0 == +0, so
        // the encoder must emit one byte sequence for both — the 0x8000 → 0 normalization.
        static byte[] Encode(Half v)
        {
            using var ms = new MemoryStream();
            OrderedByteHelpers.WriteHalf(ms, v);
            return ms.ToArray();
        }

        Encode((Half)(-0.0f)).Should().Equal(Encode((Half)0.0f));
        ((Half)(-0.0f)).CompareTo((Half)0.0f).Should().Be(0, "sanity: Half.CompareTo treats -0 == +0");
    }

    // ═════════════════════════════════════════════════════════════════════════════════════
    // Int128 / UInt128 — 16 BE bytes with high-byte sign flip on signed.
    // ═════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void WriteInt128_Round_Trips_And_Byte_Lex_Matches_Numeric_Order()
    {
        var samples = new[]
        {
            Int128.MinValue,
            new Int128(0xFFFF_FFFF_FFFF_FFFFul, 0xFFFF_FFFF_FFFF_FF00ul),
            -(Int128)1,
            (Int128)0,
            (Int128)1,
            new Int128(0, 0x7FFF_FFFF_FFFF_FFFFul),
            Int128.MaxValue,
        };

        foreach (var v in samples)
        {
            using var ms = new MemoryStream();
            OrderedByteHelpers.WriteInt128(ms, v);
            ms.Length.Should().Be(16);
            ms.Position = 0;
            OrderedByteHelpers.ReadInt128(ms).Should().Be(v);
        }

        AssertByteLexMatchesNumericOrder<Int128>(samples, OrderedByteHelpers.WriteInt128);
    }

    [Fact]
    public void WriteUInt128_Round_Trips_And_Byte_Lex_Matches_Numeric_Order()
    {
        var samples = new[]
        {
            (UInt128)0,
            (UInt128)1,
            new UInt128(0, ulong.MaxValue),
            new UInt128(1, 0),
            UInt128.MaxValue,
        };

        foreach (var v in samples)
        {
            using var ms = new MemoryStream();
            OrderedByteHelpers.WriteUInt128(ms, v);
            ms.Length.Should().Be(16);
            ms.Position = 0;
            OrderedByteHelpers.ReadUInt128(ms).Should().Be(v);
        }

        AssertByteLexMatchesNumericOrder<UInt128>(samples, OrderedByteHelpers.WriteUInt128);
    }

    // ═════════════════════════════════════════════════════════════════════════════════════
    // DateTimeOffset — UTC-instant primary + offset tiebreaker. The sample is constructed
    // so the first seven entries all share a common UTC instant but differ in offset, and
    // the remaining entries span distinct UTC instants.
    // ═════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void WriteDateTimeOffset_Round_Trips()
    {
        var samples = new[]
        {
            DateTimeOffset.MinValue,
            new DateTimeOffset(2026, 4, 22, 12, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 4, 22, 7, 0, 0, TimeSpan.FromHours(-5)),   // same UTC instant
            new DateTimeOffset(2026, 4, 22, 14, 0, 0, TimeSpan.FromHours(2)),   // same UTC instant
            DateTimeOffset.MaxValue,
        };

        foreach (var v in samples)
        {
            using var ms = new MemoryStream();
            OrderedByteHelpers.WriteDateTimeOffset(ms, v);
            ms.Length.Should().Be(10);
            ms.Position = 0;
            var round = OrderedByteHelpers.ReadDateTimeOffset(ms);
            round.UtcTicks.Should().Be(v.UtcTicks);
            round.Offset.Should().Be(v.Offset);
        }
    }

    [Fact]
    public void WriteDateTimeOffset_Byte_Lex_Matches_Utc_Instant_Then_Offset()
    {
        // Construct tuples sharing a UTC instant at 2026-04-22T12:00:00Z, varying offset.
        var utcAnchor = new DateTimeOffset(2026, 4, 22, 12, 0, 0, TimeSpan.Zero);
        var sameInstantLowOffset = utcAnchor.ToOffset(TimeSpan.FromHours(-5));
        var sameInstantMidOffset = utcAnchor.ToOffset(TimeSpan.Zero);
        var sameInstantHighOffset = utcAnchor.ToOffset(TimeSpan.FromHours(5));

        // Same-instant trio sorts by offset; earlier UTC instants sort first.
        var ordered = new[]
        {
            new DateTimeOffset(2026, 4, 21, 0, 0, 0, TimeSpan.Zero),  // earlier UTC instant
            sameInstantLowOffset,
            sameInstantMidOffset,
            sameInstantHighOffset,
            new DateTimeOffset(2026, 4, 23, 0, 0, 0, TimeSpan.Zero),  // later UTC instant
        };

        AssertByteLexMatchesNumericOrder<DateTimeOffset>(
            ordered,
            OrderedByteHelpers.WriteDateTimeOffset,
            Comparer<DateTimeOffset>.Create((a, b) =>
            {
                var utc = a.UtcTicks.CompareTo(b.UtcTicks);
                return utc != 0 ? utc : a.Offset.CompareTo(b.Offset);
            }));

        // Byte-level: first 8 bytes of the same-instant trio must be identical (UTC-instant
        // primary). The remaining 2 bytes encode the sign-flipped offset.
        using var msA = new MemoryStream();
        using var msB = new MemoryStream();
        OrderedByteHelpers.WriteDateTimeOffset(msA, sameInstantLowOffset);
        OrderedByteHelpers.WriteDateTimeOffset(msB, sameInstantHighOffset);
        var a = msA.ToArray();
        var b = msB.ToArray();
        a.Take(8).Should().Equal(b.Take(8));
    }

    // ═════════════════════════════════════════════════════════════════════════════════════
    // decimal — 17-byte frame with sign discriminator + mantissa + inverted-scale + pad.
    // Canonicalisation: numerically-equal values produce byte-identical output.
    // ═════════════════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("-1")]
    [InlineData("3.14159")]
    [InlineData("-999999.9999")]
    [InlineData("79228162514264337593543950335")]  // decimal.MaxValue
    [InlineData("-79228162514264337593543950335")] // decimal.MinValue
    public void WriteDecimal_Round_Trips(string literal)
    {
        var value = decimal.Parse(literal, System.Globalization.CultureInfo.InvariantCulture);

        using var ms = new MemoryStream();
        // Pass the value's raw scale so rounding to the declared scale is a no-op.
        OrderedByteHelpers.WriteDecimal(ms, value, ScaleOf(value));
        ms.Length.Should().Be(17);
        ms.Position = 0;
        OrderedByteHelpers.ReadDecimal(ms).Should().Be(value);
    }

    // Extract the raw scale byte from decimal.GetBits — bits[3] bits 16..23 carry the scale.
    private static int ScaleOf(decimal value)
    {
        Span<int> bits = stackalloc int[4];
        decimal.GetBits(value, bits);
        return (bits[3] >> 16) & 0x7F;
    }

    [Fact]
    public void WriteDecimal_Canonicalises_Trailing_Zeros()
    {
        // 1.0m, 1.00m, 1.000m, 1m all represent 1 and MUST encode identically — otherwise
        // an SSTable binary search for one representation misses the byte-equal key stored
        // under a different scale. Phase A2 strengthens this to cross-scale under a caller-
        // declared canonical scale — all variants round to the same declared scale before
        // the byte-lex emission, producing identical output regardless of the raw scale.
        const int canonicalScale = 10;
        var variants = new[] { 1m, 1.0m, 1.00m, 1.000m, 1.0000000000m };
        var bytes = variants.Select(v =>
        {
            using var ms = new MemoryStream();
            OrderedByteHelpers.WriteDecimal(ms, v, canonicalScale);
            return ms.ToArray();
        }).ToList();

        for (var i = 1; i < bytes.Count; i++)
        {
            bytes[i].Should().Equal(bytes[0]);
        }
    }

    [Fact]
    public void WriteDecimal_Canonicalises_Across_Raw_Scales_Under_Declared_Scale()
    {
        // Phase A2 invariant — two values with different raw scales but the same numeric
        // magnitude produce byte-identical encodings once canonicalised to the declared scale.
        // Without canonicalisation, equal values written at different raw scales would sort and compare
        // as different keys; this test pins the byte-identical encoding that closes that hole.
        const int canonicalScale = 4;
        using var ms1 = new MemoryStream();
        using var ms2 = new MemoryStream();
        OrderedByteHelpers.WriteDecimal(ms1, 1.5m, canonicalScale);
        OrderedByteHelpers.WriteDecimal(ms2, 1.500000m, canonicalScale);
        ms1.ToArray().Should().Equal(ms2.ToArray());
    }

    [Fact]
    public void WriteDecimal_Throws_OnOutOfRangeScale()
    {
        using var ms = new MemoryStream();
        var act1 = () => OrderedByteHelpers.WriteDecimal(ms, 1m, -1);
        var act2 = () => OrderedByteHelpers.WriteDecimal(ms, 1m, 29);
        act1.Should().Throw<ArgumentOutOfRangeException>();
        act2.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void WriteDecimal_Same_Scale_Byte_Lex_Matches_Numeric_Order()
    {
        // Fixed-scale decimals (the canonical MarketData pattern — e.g., always 4 decimal
        // places for prices) sort byte-lex by numeric value. Cross-scale ordering is a
        // documented limitation; this test pins the fixed-scale contract consumers rely on.
        var samples = new[]
        {
            decimal.Parse("-9999.9999", System.Globalization.CultureInfo.InvariantCulture),
            decimal.Parse("-1.5000", System.Globalization.CultureInfo.InvariantCulture),
            decimal.Parse("-0.0001", System.Globalization.CultureInfo.InvariantCulture),
            decimal.Parse("0.0000", System.Globalization.CultureInfo.InvariantCulture),
            decimal.Parse("0.0001", System.Globalization.CultureInfo.InvariantCulture),
            decimal.Parse("1.5000", System.Globalization.CultureInfo.InvariantCulture),
            decimal.Parse("9999.9999", System.Globalization.CultureInfo.InvariantCulture),
        };

        // Under the A2 canonicalisation contract, all samples share the same declared scale (4).
        AssertByteLexMatchesNumericOrder<decimal>(samples, (stream, value) => OrderedByteHelpers.WriteDecimal(stream, value, 4));
    }

    // ═════════════════════════════════════════════════════════════════════════════════════
    // Helper: byte-lex-order-matches-numeric-order assertion.
    // ═════════════════════════════════════════════════════════════════════════════════════

    private static void AssertByteLexMatchesNumericOrder<T>(
        T[] sortedAscending,
        Action<Stream, T> writer,
        IComparer<T>? comparer = null)
    {
        comparer ??= Comparer<T>.Default;
        var encoded = sortedAscending.Select(v =>
        {
            using var ms = new MemoryStream();
            writer(ms, v);
            return (Value: v, Bytes: ms.ToArray());
        }).ToList();

        // Verify pairs are in byte-lex order.
        for (var i = 0; i + 1 < encoded.Count; i++)
        {
            var left = encoded[i];
            var right = encoded[i + 1];
            var lex = CompareBytes(left.Bytes, right.Bytes);
            var num = comparer.Compare(left.Value, right.Value);
            Math.Sign(lex).Should().Be(Math.Sign(num),
                $"byte-lex({left.Value}, {right.Value}) should match numeric compare");
        }
    }

    private static int CompareBytes(byte[] left, byte[] right)
    {
        var min = Math.Min(left.Length, right.Length);
        for (var i = 0; i < min; i++)
        {
            var diff = left[i].CompareTo(right[i]);
            if (diff != 0)
            {
                return diff;
            }
        }
        return left.Length.CompareTo(right.Length);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // UInt8 — single byte, no transformation.
    // ═══════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData((byte)0x00)]
    [InlineData((byte)0x7F)]
    [InlineData((byte)0xFF)]
    public void WriteUInt8_RoundTrips(byte value)
    {
        using var ms = new MemoryStream();
        OrderedByteHelpers.WriteUInt8(ms, value);
        ms.Position = 0;
        OrderedByteHelpers.ReadUInt8(ms).Should().Be(value);
    }

    [Theory]
    [InlineData((byte)0, (byte)1)]
    [InlineData((byte)127, (byte)128)]
    [InlineData((byte)254, (byte)255)]
    public void WriteUInt8_ByteLexMatchesNumericOrder(byte smaller, byte larger)
    {
        using var msSmall = new MemoryStream();
        using var msLarge = new MemoryStream();
        OrderedByteHelpers.WriteUInt8(msSmall, smaller);
        OrderedByteHelpers.WriteUInt8(msLarge, larger);

        var lex = CompareBytes(msSmall.ToArray(), msLarge.ToArray());
        lex.Should().BeLessThan(0, $"byte for {smaller} should sort before byte for {larger}");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // UInt16 — big-endian, no sign transformation.
    // ═══════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData((ushort)0)]
    [InlineData((ushort)32768)]
    [InlineData((ushort)65535)]
    public void WriteUInt16_RoundTrips(ushort value)
    {
        using var ms = new MemoryStream();
        OrderedByteHelpers.WriteUInt16(ms, value);
        ms.Position = 0;
        OrderedByteHelpers.ReadUInt16(ms).Should().Be(value);
    }

    [Fact]
    public void WriteUInt16_ByteLexMatchesNumericOrder()
    {
        AssertByteLexMatchesNumericOrder<ushort>(
            [0, 1, 256, 32768, 65534, 65535],
            OrderedByteHelpers.WriteUInt16);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Int16 — round-trip (byte-lex test already exists).
    // ═══════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(short.MinValue)]
    [InlineData((short)-1)]
    [InlineData((short)0)]
    [InlineData((short)1)]
    [InlineData(short.MaxValue)]
    public void WriteInt16_RoundTrips(short value)
    {
        using var ms = new MemoryStream();
        OrderedByteHelpers.WriteInt16(ms, value);
        ms.Position = 0;
        OrderedByteHelpers.ReadInt16(ms).Should().Be(value);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Char — aliases WriteUInt16/ReadUInt16.
    // ═══════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData('A')]
    [InlineData('\0')]
    [InlineData('￿')]
    public void WriteChar_RoundTrips(char value)
    {
        using var ms = new MemoryStream();
        OrderedByteHelpers.WriteChar(ms, value);
        ms.Position = 0;
        OrderedByteHelpers.ReadChar(ms).Should().Be(value);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // ReadUInt8 — EndOfStream guard.
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ReadUInt8_OnEmptyStream_ThrowsEndOfStreamException()
    {
        using var ms = new MemoryStream();

        var act = () => OrderedByteHelpers.ReadUInt8(ms);

        act.Should().Throw<EndOfStreamException>();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // EndOfStream guards on the remaining single-byte readers.
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ReadInt8_OnEmptyStream_ThrowsEndOfStreamException()
    {
        using var ms = new MemoryStream();
        var act = () => OrderedByteHelpers.ReadInt8(ms);
        act.Should().Throw<EndOfStreamException>();
    }

    [Fact]
    public void ReadBool_OnEmptyStream_ThrowsEndOfStreamException()
    {
        using var ms = new MemoryStream();
        var act = () => OrderedByteHelpers.ReadBool(ms);
        act.Should().Throw<EndOfStreamException>();
    }

    [Fact]
    public void ReadNullDiscriminator_OnEmptyStream_ThrowsEndOfStreamException()
    {
        using var ms = new MemoryStream();
        var act = () => OrderedByteHelpers.ReadNullDiscriminator(ms);
        act.Should().Throw<EndOfStreamException>();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Bool round-trip (true/false both write a single byte; reader returns b != 0).
    // ═══════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WriteBool_ReadBool_RoundTrips(bool value)
    {
        using var ms = new MemoryStream();
        OrderedByteHelpers.WriteBool(ms, value);
        ms.Position = 0;
        OrderedByteHelpers.ReadBool(ms).Should().Be(value);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Null discriminator — 0x00 sorts before 0x01 (null first).
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void WriteNullDiscriminator_NullSortsBeforePresent()
    {
        using var nullMs = new MemoryStream();
        using var presentMs = new MemoryStream();
        OrderedByteHelpers.WriteNullDiscriminator(nullMs, hasValue: false);
        OrderedByteHelpers.WriteNullDiscriminator(presentMs, hasValue: true);
        nullMs.ToArray().AsSpan().SequenceCompareTo(presentMs.ToArray()).Should().BeLessThan(0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NullDiscriminator_RoundTrips(bool hasValue)
    {
        using var ms = new MemoryStream();
        OrderedByteHelpers.WriteNullDiscriminator(ms, hasValue);
        ms.Position = 0;
        OrderedByteHelpers.ReadNullDiscriminator(ms).Should().Be(hasValue);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // OrderedString escape handling and EOF guards.
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void WriteOrderedString_ReadOrderedString_RoundTripsEmptyString()
    {
        using var ms = new MemoryStream();
        OrderedByteHelpers.WriteOrderedString(ms, string.Empty);
        ms.Position = 0;
        OrderedByteHelpers.ReadOrderedString(ms).Should().BeEmpty();
    }

    [Fact]
    public void WriteOrderedString_ReadOrderedString_RoundTripsStringWithNullByte()
    {
        // The escape mechanism must round-trip a literal 0x00 byte (encoded as 0x00 0xFF).
        var s = "a b";
        using var ms = new MemoryStream();
        OrderedByteHelpers.WriteOrderedString(ms, s);
        ms.Position = 0;
        OrderedByteHelpers.ReadOrderedString(ms).Should().Be(s);
    }

    [Fact]
    public void WriteOrderedString_ReadOrderedString_RoundTripsLargeString()
    {
        // Trips the rented-buffer path (>256 bytes).
        var s = new string('A', 1024);
        using var ms = new MemoryStream();
        OrderedByteHelpers.WriteOrderedString(ms, s);
        ms.Position = 0;
        OrderedByteHelpers.ReadOrderedString(ms).Should().Be(s);
    }

    [Fact]
    public void ReadOrderedString_OnEmptyStream_ThrowsEndOfStreamException()
    {
        using var ms = new MemoryStream();
        var act = () => OrderedByteHelpers.ReadOrderedString(ms);
        act.Should().Throw<EndOfStreamException>();
    }

    [Fact]
    public void ReadOrderedString_StreamEndsAfterFirstTerminatorByte_Throws()
    {
        // Byte 0x00 with no follow-up — reader expects either 0x00 (end-of-string)
        // or 0xFF (escape) but gets EOF.
        using var ms = new MemoryStream([0x00]);
        var act = () => OrderedByteHelpers.ReadOrderedString(ms);
        act.Should().Throw<EndOfStreamException>().WithMessage("*escape*");
    }

    [Fact]
    public void ReadOrderedString_InvalidEscapeByte_ThrowsInvalidData()
    {
        // 0x00 followed by anything other than 0x00 or 0xFF is malformed.
        using var ms = new MemoryStream([0x00, 0x42]);
        var act = () => OrderedByteHelpers.ReadOrderedString(ms);
        act.Should().Throw<InvalidDataException>().WithMessage("*Invalid escape*");
    }

    [Fact]
    public void WriteOrderedString_LoneSurrogate_ThrowsArgumentException()
    {
        // Unpaired surrogates are not encodable in UTF-8. Replacement substitution (U+FFFD)
        // would collide distinct keys ("\uD800" and "\uDFFF" encode identically) and break
        // round-trip exactness — reject at encode like NaN floats.
        using var ms = new MemoryStream();
        var act = () => OrderedByteHelpers.WriteOrderedString(ms, "\uD800");
        act.Should().Throw<ArgumentException>().WithMessage("*unpaired surrogate*");
    }

    [Fact]
    public void ReadOrderedString_InvalidUtf8Payload_ThrowsInvalidData()
    {
        // 0xC3 0x28 is an ill-formed UTF-8 sequence (truncated 2-byte lead). The writer can
        // never produce it, so it is corruption — the reader must fail loud rather than
        // silently substituting U+FFFD and returning a string no writer ever encoded.
        using var ms = new MemoryStream([0xC3, 0x28, 0x00, 0x00]);
        var act = () => OrderedByteHelpers.ReadOrderedString(ms);
        act.Should().Throw<InvalidDataException>().WithMessage("*UTF-8*");
    }

    [Fact]
    public void WriteOrderedString_NullValue_Throws()
    {
        using var ms = new MemoryStream();
        var act = () => OrderedByteHelpers.WriteOrderedString(ms, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void WriteOrderedString_NullStream_Throws()
    {
        var act = () => OrderedByteHelpers.WriteOrderedString(null!, "x");
        act.Should().Throw<ArgumentNullException>();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Decimal — defence-in-depth scale-validation throws (defended against missing
    // [DecimalScale] checks). Round-trip + canonicalisation already live in
    // DecimalCanonicalScaleTests.
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void WriteDecimal_ExcessFractionalPrecision_ThrowsArgumentException()
    {
        // 1.00005m at declared scale 4 would have to round to 1.0000m — byte-identical to a
        // genuinely-distinct key 1.0000m while the in-memory comparator (decimal.CompareTo,
        // full precision) says they differ. Silent rounding breaks sign(CompareTo) ==
        // sign(byte-lex) at the equality boundary AND makes Deserialize(Serialize(v)) != v.
        // The writer must reject excess precision like it rejects NaN floats.
        using var ms = new MemoryStream();
        var act = () => OrderedByteHelpers.WriteDecimal(ms, 1.00005m, scale: 4);
        act.Should().Throw<ArgumentException>().WithMessage("*declared scale*");
    }

    [Fact]
    public void WriteDecimal_MagnitudeTooLargeForDeclaredScale_ThrowsArgumentException()
    {
        // 1e25 × 10^4 exceeds the 96-bit mantissa, so scale-padding silently stops at scale 3.
        // The resulting encoding compares mantissa bytes against differently-scaled siblings —
        // byte-lex order then contradicts decimal.Compare (probe: 1e25 sorts below 2e24).
        // Values whose magnitude cannot be represented at the declared scale must be rejected.
        using var ms = new MemoryStream();
        var act = () => OrderedByteHelpers.WriteDecimal(ms, 10_000_000_000_000_000_000_000_000m, scale: 4);
        act.Should().Throw<ArgumentException>().WithMessage("*declared scale*");
    }

    [Fact]
    public void WriteDecimal_ScaleNegative_ThrowsArgumentOutOfRange()
    {
        using var ms = new MemoryStream();
        var act = () => OrderedByteHelpers.WriteDecimal(ms, 1m, scale: -1);
        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("scale");
    }

    [Fact]
    public void WriteDecimal_ScaleAbove28_ThrowsArgumentOutOfRange()
    {
        using var ms = new MemoryStream();
        var act = () => OrderedByteHelpers.WriteDecimal(ms, 1m, scale: 29);
        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("scale");
    }

    [Fact]
    public void WriteDecimal_NullStream_Throws()
    {
        var act = () => OrderedByteHelpers.WriteDecimal(null!, 1m, scale: 2);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void WriteDecimal_ZeroValue_RoundTrips()
    {
        using var ms = new MemoryStream();
        OrderedByteHelpers.WriteDecimal(ms, 0m, scale: 4);
        ms.Position = 0;
        OrderedByteHelpers.ReadDecimal(ms).Should().Be(0m);
    }

    [Fact]
    public void WriteDecimal_NegativeValue_RoundTrips()
    {
        using var ms = new MemoryStream();
        OrderedByteHelpers.WriteDecimal(ms, -1.25m, scale: 4);
        ms.Position = 0;
        OrderedByteHelpers.ReadDecimal(ms).Should().Be(-1.25m);
    }

    // ═════════════════════════════════════════════════════════════════════════════════════
    // Float edge cases (#12 / coverage gap) — -0.0, NaN, ±∞. The generated [Key] comparator is
    // float/double.CompareTo, which treats -0 == +0; the encoder must therefore collapse -0 to +0
    // so the same logical key encodes to one byte sequence (a memtable dedups ±0; the SSTable must
    // too). These pin that ±0 encode equal, that finite/∞ order is preserved, and that NaN — which
    // has no total-order byte position — is rejected at encode.
    // ═════════════════════════════════════════════════════════════════════════════════════

    private static byte[] EncodeSingle(float v)
    {
        using var ms = new MemoryStream();
        OrderedByteHelpers.WriteSingle(ms, v);
        return ms.ToArray();
    }

    private static byte[] EncodeDouble(double v)
    {
        using var ms = new MemoryStream();
        OrderedByteHelpers.WriteDouble(ms, v);
        return ms.ToArray();
    }

    private static int ByteLex(byte[] a, byte[] b)
    {
        var n = Math.Min(a.Length, b.Length);
        for (var i = 0; i < n; i++)
        {
            var d = a[i].CompareTo(b[i]);
            if (d != 0) { return d; }
        }
        return a.Length.CompareTo(b.Length);
    }

    [Fact]
    public void WriteSingle_NegativeZero_EncodesEqualToPositiveZero_MatchingCompareTo()
    {
        // float.CompareTo treats -0 == +0; the encoder must agree (collapse -0 to +0) so the same
        // logical key has one byte sequence. The un-fixed encoder emitted -0 -> 0x7FFF.. (< +0),
        // contradicting CompareTo.
        ByteLex(EncodeSingle(-0.0f), EncodeSingle(0.0f)).Should().Be(0);
        ((-0.0f).CompareTo(0.0f)).Should().Be(0, "sanity: .NET float.CompareTo treats -0 == +0");
    }

    [Fact]
    public void WriteDouble_NegativeZero_EncodesEqualToPositiveZero_MatchingCompareTo()
    {
        ByteLex(EncodeDouble(-0.0), EncodeDouble(0.0)).Should().Be(0);
        ((-0.0).CompareTo(0.0)).Should().Be(0);
    }

    [Fact]
    public void WriteSingle_ByteLex_AcrossNegativeZeroAndInfinities_MatchesNumericOrder()
    {
        // Strictly increasing by float.CompareTo (±0 collapse to one position): -inf < -1 < 0 < 1 < +inf.
        var ordered = new[] { float.NegativeInfinity, -1.0f, 0.0f, 1.0f, float.PositiveInfinity };
        for (var i = 0; i + 1 < ordered.Length; i++)
        {
            ByteLex(EncodeSingle(ordered[i]), EncodeSingle(ordered[i + 1]))
                .Should().BeLessThan(0, $"encoded {ordered[i]} must sort before {ordered[i + 1]}");
        }
    }

    [Fact]
    public void WriteDouble_ByteLex_AcrossNegativeZeroAndInfinities_MatchesNumericOrder()
    {
        var ordered = new[] { double.NegativeInfinity, -1.0, 0.0, 1.0, double.PositiveInfinity };
        for (var i = 0; i + 1 < ordered.Length; i++)
        {
            ByteLex(EncodeDouble(ordered[i]), EncodeDouble(ordered[i + 1]))
                .Should().BeLessThan(0, $"encoded {ordered[i]} must sort before {ordered[i + 1]}");
        }
    }

    [Fact]
    public void WriteSingle_NaN_Throws()
    {
        var act = () => { using var ms = new MemoryStream(); OrderedByteHelpers.WriteSingle(ms, float.NaN); };
        act.Should().Throw<ArgumentException>("NaN has no total-order position and cannot be an ordered key");
    }

    [Fact]
    public void WriteDouble_NaN_Throws()
    {
        var act = () => { using var ms = new MemoryStream(); OrderedByteHelpers.WriteDouble(ms, double.NaN); };
        act.Should().Throw<ArgumentException>();
    }

    // ═════════════════════════════════════════════════════════════════════════════════════
    // Fix 1: ReadDecimal corrupt-discriminator rejection (fail-closed).
    // ═════════════════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(0x03)]
    [InlineData(0xFF)]
    [InlineData(0x10)]
    public void ReadDecimal_CorruptDiscriminator_ThrowsInvalidDataException(byte badDiscriminator)
    {
        // Build a 17-byte buffer with the bad discriminator at byte 0 (the decimal discriminator
        // position); remaining bytes are zero-valued padding.
        var buf = new byte[17];
        buf[0] = badDiscriminator;
        using var ms = new MemoryStream(buf);
        var act = () => OrderedByteHelpers.ReadDecimal(ms);
        act.Should().Throw<InvalidDataException>()
            .WithMessage($"*0x{badDiscriminator:X2}*");
    }

    // Fix 6: null-stream guard on one representative Read method.
    [Fact]
    public void ReadInt32_NullStream_ThrowsArgumentNullException()
    {
        var act = () => OrderedByteHelpers.ReadInt32(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
