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
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar.Codecs;

namespace Boutquin.Storage.Infrastructure.Tests.TimeSeries.Columnar.Codecs;

public sealed class ScaledIntegerBitPackedCodecTests
{
    [Fact]
    public void Empty_RoundTrips()
    {
        ScaledIntegerBitPackedCodec.EncodeDecimal([]).Should().BeEmpty();
        ScaledIntegerBitPackedCodec.DecodeDecimal([], 0).Should().BeEmpty();
    }

    [Fact]
    public void AllSame_ProducesZeroBitWidth()
    {
        // 100 copies of the same value → bit width 0 → payload = header only (10 bytes).
        var input = Enumerable.Repeat(123.45m, 100).ToArray();
        var encoded = ScaledIntegerBitPackedCodec.EncodeDecimal(input);

        encoded.Length.Should().Be(10); // 1 scale + 8 min + 1 bitWidth, no payload
        ScaledIntegerBitPackedCodec.DecodeDecimal(encoded, input.Length).Should().Equal(input);
    }

    [Fact]
    public void SingleValue_RoundTrips()
    {
        var input = new[] { 42.125m };
        var decoded = ScaledIntegerBitPackedCodec.DecodeDecimal(
            ScaledIntegerBitPackedCodec.EncodeDecimal(input), input.Length);
        decoded.Should().Equal(input);
    }

    [Fact]
    public void CommonScale_SmallRange_CompressesFavourably()
    {
        // 1000 values in [100.00, 101.00] — range = 100 cents → 7 bits per value.
        // Payload = ceil(1000 × 7 / 8) = 875 bytes + 10 B header = 885 bytes. Much less
        // than 16,000 bytes plain.
        var rng = new Random(42);
        var input = Enumerable.Range(0, 1000)
            .Select(_ => 100.00m + (rng.Next(101) / 100m))
            .ToArray();

        var encoded = ScaledIntegerBitPackedCodec.EncodeDecimal(input);

        encoded.Length.Should().BeLessThan(900);
        ScaledIntegerBitPackedCodec.DecodeDecimal(encoded, input.Length).Should().Equal(input);
    }

    [Fact]
    public void MixedScales_UsesMaxScale_RoundTrips()
    {
        var input = new[] { 1m, 1.5m, 1.25m, 1.125m };
        var decoded = ScaledIntegerBitPackedCodec.DecodeDecimal(
            ScaledIntegerBitPackedCodec.EncodeDecimal(input), input.Length);
        decoded.Should().Equal(input);
    }

    [Fact]
    public void CommonTrailingZeroScale_RoundTripsBitExact()
    {
        // All values share scale 2. The decoder must reconstruct at the column's common scale,
        // preserving trailing zeros (100.00m), not collapse to the minimal scale (100m) via
        // division. Value equality (100.00m == 100m) hid this — assert decimal.GetBits scale.
        var input = new[] { 100.00m, 101.00m, 102.00m };
        var decoded = ScaledIntegerBitPackedCodec.DecodeDecimal(
            ScaledIntegerBitPackedCodec.EncodeDecimal(input), input.Length);

        decoded.Should().Equal(input);
        foreach (var d in decoded)
        {
            var scale = (decimal.GetBits(d)[3] >> 16) & 0xFF;
            scale.Should().Be(2, "the column's common trailing-zero scale must be preserved bit-exactly");
        }
    }

    [Fact]
    public void NegativeValues_RoundTrip()
    {
        var input = new[] { -10.5m, -5.25m, 0m, 5.25m, 10.5m };
        var decoded = ScaledIntegerBitPackedCodec.DecodeDecimal(
            ScaledIntegerBitPackedCodec.EncodeDecimal(input), input.Length);
        decoded.Should().Equal(input);
    }

    [Fact]
    public void ScaleExceeding18_RoundTripsBitExact()
    {
        // decimal carries up to 28 fractional digits, but 10^19 already overflows Int64, so no
        // scaled-integer packing holds a value that genuinely needs 20 digits. The column is
        // written anyway (it was acknowledged), and comes back with its exact representation.
        var input = new[] { 0.1234567890_1234567890m, 1.5m };
        var encode = () => ScaledIntegerBitPackedCodec.EncodeDecimal(input);
        var encoded = encode.Should().NotThrow("an acknowledged decimal must be encodable at any scale").Subject;
        var decoded = ScaledIntegerBitPackedCodec.DecodeDecimal(encoded, input.Length);
        decoded.Should().Equal(input);
        decimal.GetBits(decoded[0]).Should().Equal(decimal.GetBits(input[0]));
    }

    [Fact]
    public void DecimalExtremes_RoundTripBitExact()
    {
        // decimal.MinValue / MaxValue do not fit Int64 at any scale.
        var input = new[] { decimal.MinValue, decimal.MaxValue, 0m, -0.0000000000000000000000000001m };
        var encode = () => ScaledIntegerBitPackedCodec.EncodeDecimal(input);
        var encoded = encode.Should().NotThrow("an acknowledged decimal must be encodable at any magnitude").Subject;
        ScaledIntegerBitPackedCodec.DecodeDecimal(encoded, input.Length).Should().Equal(input);
        ScaledIntegerBitPackedCodec.DecodeDecimalSlice(encoded, 1, 3).Should().Equal(input[1..]);
    }

    [Fact]
    public void LargeRowCount_RoundTrips()
    {
        var input = Enumerable.Range(0, 5000).Select(i => 100m + (i * 0.01m)).ToArray();
        var decoded = ScaledIntegerBitPackedCodec.DecodeDecimal(
            ScaledIntegerBitPackedCodec.EncodeDecimal(input), input.Length);
        decoded.Should().Equal(input);
    }

    [Fact]
    public void Decode_TruncatedHeader_Throws()
    {
        var act = () => ScaledIntegerBitPackedCodec.DecodeDecimal(new byte[5], 1);
        act.Should().Throw<InvalidDataException>()
            .WithMessage("*header size*");
    }

    [Fact]
    public void Decode_TruncatedPayload_Throws()
    {
        // 10-byte header says bit width = 8, count = 1000 requires 1000 bytes of payload.
        var buf = new byte[10 + 5];
        buf[9] = 8; // bit width
        var act = () => ScaledIntegerBitPackedCodec.DecodeDecimal(buf, 1000);
        act.Should().Throw<InvalidDataException>()
            .WithMessage("*truncated*");
    }
    [Fact]
    public void DecodeDecimalSlice_TruncatedPayload_Throws()
    {
        // Slice path must apply the same payload-bound validation as the full decoder —
        // a truncated payload previously surfaced as a raw IndexOutOfRangeException from
        // the bit reader instead of a typed InvalidDataException.
        var encoded = ScaledIntegerBitPackedCodec.EncodeDecimal([1.5m, 2.5m, 3.5m, 4.5m, 5.5m]);
        var truncated = encoded[..(encoded.Length - 2)];

        var act = () => ScaledIntegerBitPackedCodec.DecodeDecimalSlice(truncated, 2, 3);
        act.Should().Throw<InvalidDataException>()
            .WithMessage("*truncated*");
    }

    // ── B9: bitWidth==64 boundary ─────────────────────────────────────────────

    [Fact]
    public void EncodeDecimal_RangeWiderThanInt64_RoundTrips()
    {
        // Both ends fit Int64 at scale 0, but max - min (up to 2^64 - 1) does not fit a signed
        // 64-bit value. The range always fits an unsigned one, so the column packs at bit width 64.
        var values = new decimal[] { (decimal)long.MinValue, (decimal)long.MaxValue, -5_000_000_000_000_000_000m, 5_000_000_000_000_000_000m };
        var encode = () => ScaledIntegerBitPackedCodec.EncodeDecimal(values);
        var encoded = encode.Should().NotThrow("a range wider than Int64 still fits an unsigned 64-bit width").Subject;
        ScaledIntegerBitPackedCodec.DecodeDecimal(encoded, values.Length).Should().Equal(values);
        ScaledIntegerBitPackedCodec.DecodeDecimalSlice(encoded, 2, 2).Should().Equal(values[2..]);
    }

    [Fact]
    public void EncodeDecimal_MaxLongRange_RoundTrips()
    {
        // The maximum valid signed range is min=0, max=long.MaxValue → range=long.MaxValue.
        // LeadingZeroCount(long.MaxValue) = LeadingZeroCount(0x7FFF...FFFF) = 1 on the ulong
        // interpretation → bitWidth = 64 - 1 = 63.  To force bitWidth==64 we need range whose
        // most-significant bit is set: range = 2^63 = 0x8000_0000_0000_0000.  The guard
        // requires (max - min) ≥ 0 in signed space; range 2^63 requires min < 0 and max ≥ 0
        // with max - min = long.MinValue in signed (overflow). The guard catches that.
        //
        // In practice bitWidth==64 is therefore unreachable through the public API (the Int64
        // overflow guard fires first). The spec says "verify bitWidth==64 is handled or capped".
        // The encoder cannot produce it; the decoder protects against it via the > 64 check.
        // Test what we CAN test: the near-max case (bitWidth==63) round-trips correctly.
        // This confirms the high-bitwidth path (63 bits, all 8 bytes written) is exercised.
        var values = new decimal[] { 0m, (decimal)long.MaxValue };
        var encoded = ScaledIntegerBitPackedCodec.EncodeDecimal(values);
        var decoded = ScaledIntegerBitPackedCodec.DecodeDecimal(encoded, values.Length);
        decoded.Should().Equal(values, "near-maximum bit-width (63-bit) round-trip must be bit-exact");
    }

    [Fact]
    public void DecodeDecimalSlice_ConstantColumn_LateRowGroupWithinPerRowGroupCeiling_Decodes()
    {
        // A constant column's payload is the same 10-byte header whatever the row count, so a
        // segment with more rows than the per-row-group ceiling still has row groups starting past
        // that ceiling. The ceiling bounds the slice's allocation (`count`), not its absolute
        // extent, matching the run-length slice decoders.
        var encoded = ScaledIntegerBitPackedCodec.EncodeDecimal([7.25m, 7.25m]);
        encoded[9].Should().Be((byte)0, "all-equal values produce a zero bit-width constant column");
        const long perRowGroupCeiling = 1L << 26;

        var act = () => ScaledIntegerBitPackedCodec.DecodeDecimalSlice(
            encoded, firstRowIndex: (int)perRowGroupCeiling, count: 100, maxDecodableRows: perRowGroupCeiling);

        var slice = act.Should().NotThrow("a 100-row slice is within the per-row-group ceiling").Subject;
        slice.Should().HaveCount(100).And.OnlyContain(v => v == 7.25m);
    }

    [Fact]
    public void DecodeDecimalSlice_ConstantColumn_CountAboveCeiling_Throws()
    {
        var encoded = ScaledIntegerBitPackedCodec.EncodeDecimal([7.25m, 7.25m]);

        var act = () => ScaledIntegerBitPackedCodec.DecodeDecimalSlice(
            encoded, firstRowIndex: 0, count: 101, maxDecodableRows: 100);

        act.Should().Throw<InvalidDataException>().WithMessage("*101 rows*at most 100*");
    }
    // Ordinary decimal arithmetic leaves trailing zeros in the raw scale: 1.25m * 1.00000000000000000m
    // carries scale 19 although 1.25 needs only 2 fractional digits. The codec must size its common
    // scale from the exact value, not the raw representation, or an acknowledged append faults the
    // engine at flush.
    [Fact]
    public void TrailingZeroScaleAbove18_ExactValueFits_RoundTripsValue()
    {
        var px = 1.25m * 1.00000000000000000m;
        px.Scale.Should().Be(19, "the fixture must exercise a raw scale above the Int64 ceiling");
        var input = new[] { px, 3m, 100.5m };

        var encode = () => ScaledIntegerBitPackedCodec.EncodeDecimal(input);
        var encoded = encode.Should().NotThrow("every value is exactly representable at 2 fractional digits").Subject;
        var decoded = ScaledIntegerBitPackedCodec.DecodeDecimal(encoded, input.Length);

        decoded.Should().Equal(input, "decimal equality is value equality, which is the codec's contract");
    }
    // The same trailing-zero mechanism below the scale ceiling: 68250131.4000000000000000m carries
    // scale 16, and 68250131.4 x 10^16 overflows Int64 although the value fits at scale 1.
    [Fact]
    public void TrailingZeroScaleOverflowsInt64_ExactValueFits_RoundTripsValue()
    {
        var px = 68250131.4000000000000000m;
        px.Scale.Should().Be(16, "the fixture must carry trailing zeros that push the scaled value past Int64");
        var input = new[] { px, -3.25m };

        var encode = () => ScaledIntegerBitPackedCodec.EncodeDecimal(input);
        var encoded = encode.Should().NotThrow("every value is exactly representable at 2 fractional digits").Subject;
        var decoded = ScaledIntegerBitPackedCodec.DecodeDecimal(encoded, input.Length);

        decoded.Should().Equal(input, "decimal equality is value equality, which is the codec's contract");
    }
}
