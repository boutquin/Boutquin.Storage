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

public sealed class GorillaXorCodecTests
{
    // ───── Round-trip basics ──────────────────────────────────────────

    [Fact]
    public void Empty_RoundTrips()
    {
        GorillaXorCodec.Encode([]).Should().BeEmpty();
        GorillaXorCodec.Decode([], 0, long.MaxValue).Should().BeEmpty();
    }

    [Fact]
    public void SingleValue_EmitsEightBytesRaw()
    {
        var input = new[] { 3.14159265358979d };
        var encoded = GorillaXorCodec.Encode(input);

        encoded.Length.Should().Be(8);
        GorillaXorCodec.Decode(encoded, input.Length, long.MaxValue).Should().Equal(input);
    }

    [Fact]
    public void AllZeros_CompressesToNearTheoreticalMinimum()
    {
        // 1000 × 0.0 → first value 8 bytes + 999 single '0' bits = 8 + ceil(999/8) = 133 bytes.
        var input = new double[1000];
        var encoded = GorillaXorCodec.Encode(input);

        encoded.Length.Should().BeLessThan(140);
        GorillaXorCodec.Decode(encoded, input.Length, long.MaxValue).Should().Equal(input);
    }

    [Fact]
    public void AllSameNonZero_CompressesToNearTheoreticalMinimum()
    {
        var input = Enumerable.Repeat(42.5d, 1000).ToArray();
        var encoded = GorillaXorCodec.Encode(input);

        // First value 64 bits + 999 single '0' bits.
        encoded.Length.Should().BeLessThan(140);
        GorillaXorCodec.Decode(encoded, input.Length, long.MaxValue).Should().Equal(input);
    }

    // ───── IEEE-754 edge cases ────────────────────────────────────────

    [Fact]
    public void NaN_RoundTrips()
    {
        var input = new[] { double.NaN, 1.0, double.NaN };
        var decoded = GorillaXorCodec.Decode(GorillaXorCodec.Encode(input), input.Length, long.MaxValue);
        decoded.Should().HaveCount(3);
        decoded[0].Should().Be(double.NaN);
        decoded[1].Should().Be(1.0);
        decoded[2].Should().Be(double.NaN);
    }

    [Fact]
    public void NonCanonicalNaN_PayloadBitsPreserved()
    {
        // Custom NaN payload — not the canonical 0x7FF8_0000_0000_0000.
        var customNaN = BitConverter.UInt64BitsToDouble(0x7FF8_DEAD_BEEF_1234UL);
        var input = new[] { customNaN, customNaN };
        var decoded = GorillaXorCodec.Decode(GorillaXorCodec.Encode(input), input.Length, long.MaxValue);

        BitConverter.DoubleToUInt64Bits(decoded[0]).Should().Be(0x7FF8_DEAD_BEEF_1234UL);
        BitConverter.DoubleToUInt64Bits(decoded[1]).Should().Be(0x7FF8_DEAD_BEEF_1234UL);
    }

    [Fact]
    public void PositiveAndNegativeInfinity_RoundTrip()
    {
        var input = new[] { double.PositiveInfinity, 0.0, double.NegativeInfinity, double.PositiveInfinity };
        var decoded = GorillaXorCodec.Decode(GorillaXorCodec.Encode(input), input.Length, long.MaxValue);
        decoded.Should().Equal(input);
    }

    [Fact]
    public void Subnormal_RoundTrips()
    {
        var input = new[] { double.Epsilon, 0.0, double.Epsilon * 2, double.Epsilon };
        var decoded = GorillaXorCodec.Decode(GorillaXorCodec.Encode(input), input.Length, long.MaxValue);
        decoded.Should().Equal(input);
    }

    [Fact]
    public void NegativeZero_PreservedAsDistinctFromPositiveZero()
    {
        var input = new[] { 0.0, -0.0, 0.0, -0.0 };
        var encoded = GorillaXorCodec.Encode(input);
        var decoded = GorillaXorCodec.Decode(encoded, input.Length, long.MaxValue);

        // 0.0 == -0.0 under value equality, but their bit patterns differ.
        BitConverter.DoubleToUInt64Bits(decoded[0]).Should().Be(0UL);
        BitConverter.DoubleToUInt64Bits(decoded[1]).Should().Be(0x8000_0000_0000_0000UL);
        BitConverter.DoubleToUInt64Bits(decoded[2]).Should().Be(0UL);
        BitConverter.DoubleToUInt64Bits(decoded[3]).Should().Be(0x8000_0000_0000_0000UL);
    }

    [Fact]
    public void DoubleBoundaries_RoundTrip()
    {
        var input = new[] { double.MinValue, 0.0, double.MaxValue, double.Epsilon, -double.Epsilon };
        var decoded = GorillaXorCodec.Decode(GorillaXorCodec.Encode(input), input.Length, long.MaxValue);
        decoded.Should().Equal(input);
    }

    // ───── Realistic workload ─────────────────────────────────────────

    [Fact]
    public void SyntheticMultiplicativeWalk_RoundTrips()
    {
        // 10 years of daily observations on a slow-moving gauge. Multiplicative walks
        // compress modestly (~1.2–1.3×) because each multiplication perturbs most mantissa
        // bits — Gorilla's big win (1-bit encoding when XOR == 0) requires exact value
        // repetition. Higher-ratio compression is tested in the MonitoringLikeData test below.
        var rng = new Random(2026);
        var n = 252 * 10;
        var values = new double[n];
        values[0] = 1.0;
        for (var i = 1; i < n; i++)
        {
            var dailyDrift = (rng.NextDouble() - 0.5) * 0.002; // ±0.1%
            values[i] = values[i - 1] * (1 + dailyDrift);
        }

        var encoded = GorillaXorCodec.Encode(values);
        var decoded = GorillaXorCodec.Decode(encoded, values.Length, long.MaxValue);

        decoded.Should().Equal(values);

        // Loose bound: any compression at all. A regression to 0× would indicate a codec bug.
        var rawSize = n * 8;
        encoded.Length.Should().BeLessThan(rawSize);
    }

    [Fact]
    public void MonitoringLikeData_WithManyRepeats_CompressesDramatically()
    {
        // Pelkonen 2015's headline 1.37 B/value result was measured on Facebook's server
        // monitoring data — gauges that held steady most of the time with occasional
        // discrete changes. Simulate that shape: 85% of consecutive values identical (→ 1-bit
        // encoding of each XOR == 0), 15% transitions to a fresh level.
        var rng = new Random(9001);
        var n = 2000;
        var values = new double[n];
        values[0] = 0.42;
        for (var i = 1; i < n; i++)
        {
            values[i] = rng.NextDouble() < 0.85
                ? values[i - 1]
                : 0.3 + (rng.NextDouble() * 0.4);
        }

        var encoded = GorillaXorCodec.Encode(values);
        var decoded = GorillaXorCodec.Decode(encoded, values.Length, long.MaxValue);

        decoded.Should().Equal(values);

        // Expect > 4× compression on repeat-heavy monitoring-style data.
        var ratio = (double)(n * 8) / encoded.Length;
        ratio.Should().BeGreaterThan(4.0);
    }

    // ───── Worst case ─────────────────────────────────────────────────

    [Fact]
    public void HighEntropyHashedDoubles_StaysWithinDegradationBound()
    {
        // Random IEEE-754 bit patterns — worst case for Gorilla. Each XOR has no pattern,
        // so every value pays the new-block header cost: 2 control bits + 5 + 6 + up to 64
        // meaningful bits = up to 77 bits/value ≈ 9.625 B/value. Assert ≤ 10 B/value.
        var rng = new Random(1234);
        var n = 1000;
        var values = new double[n];
        for (var i = 0; i < n; i++)
        {
            var hi = (ulong)(uint)rng.Next();
            var lo = (ulong)(uint)rng.Next();
            values[i] = BitConverter.UInt64BitsToDouble((hi << 32) | lo);
        }

        var encoded = GorillaXorCodec.Encode(values);
        var decoded = GorillaXorCodec.Decode(encoded, values.Length, long.MaxValue);

        // Round-trip must still hold bit-perfectly (DoubleToUInt64Bits comparison handles NaN).
        for (var i = 0; i < n; i++)
        {
            BitConverter.DoubleToUInt64Bits(decoded[i])
                .Should().Be(BitConverter.DoubleToUInt64Bits(values[i]), $"at index {i}");
        }

        // Upper bound: 10 bytes per value + 8-byte first value + 8 bytes of framing slack.
        encoded.Length.Should().BeLessThan((10 * n) + 16);
    }

    // ───── Error paths ────────────────────────────────────────────────

    [Fact]
    public void Decode_PayloadBelowEightBytes_Throws()
    {
        var act = () => GorillaXorCodec.Decode(new byte[4], 1, long.MaxValue);
        act.Should().Throw<InvalidDataException>()
            .WithMessage("*at least 8 bytes*");
    }

    [Fact]
    public void Decode_ExhaustsBufferBeforeProducingAllRows_Throws()
    {
        // 8 bytes of first value + nothing left — but count asks for 10 more values. The
        // allocation-before-bound guard now rejects this before the BitReader is constructed:
        // an 8-byte payload can decode at most 1 value, so a declared count of 10 is provably
        // impossible. (Previously this surfaced as a late EndOfStreamException only after the
        // result array had already been allocated from the unbounded count.)
        var buf = new byte[8];
        var act = () => GorillaXorCodec.Decode(buf, 10, long.MaxValue);
        act.Should().Throw<InvalidDataException>()
            .WithMessage("*exceeds*");
    }

    [Fact]
    public void Decode_CountOneOnFirstValue_DoesNotReadBeyondEightBytes()
    {
        // Encode a single value (8 bytes) and decode count=1. Should succeed without
        // touching the padding bits at all.
        var input = new[] { 123.456d };
        var encoded = GorillaXorCodec.Encode(input);
        encoded.Length.Should().Be(8);
        GorillaXorCodec.Decode(encoded, 1, long.MaxValue).Should().Equal(input);
    }

    // ───── BitWriter/BitReader basics ─────────────────────────────────
    //
    // Sanity checks for the shared bit-stream helpers. A failure here suggests Gorilla
    // failures are helper bugs rather than codec bugs.

    [Fact]
    public void BitWriter_WriteBits_ReadBackIdentically()
    {
        var writer = new BitWriter();
        writer.WriteBits(0b1010_1100UL, 8);
        writer.WriteBits(0b111UL, 3);
        writer.WriteBits(0b1_0000_0000_0000_0001UL, 17);
        var bytes = writer.ToArray();

        var reader = new BitReader(bytes);
        reader.ReadBits(8).Should().Be(0b1010_1100UL);
        reader.ReadBits(3).Should().Be(0b111UL);
        reader.ReadBits(17).Should().Be(0b1_0000_0000_0000_0001UL);
    }

    [Fact]
    public void BitWriter_AcceptsFull64BitField()
    {
        var writer = new BitWriter();
        writer.WriteBits(0xDEAD_BEEF_CAFE_1234UL, 64);
        var bytes = writer.ToArray();

        bytes.Length.Should().Be(8);
        var reader = new BitReader(bytes);
        reader.ReadBits(64).Should().Be(0xDEAD_BEEF_CAFE_1234UL);
    }

    [Fact]
    public void BitReader_ExhaustedBuffer_Throws()
    {
        // ref struct BitReader can't be captured by a lambda; inline the call and catch.
        var buffer = new byte[1];
        var reader = new BitReader(buffer);
        _ = reader.ReadBits(8);

        var threw = false;
        try
        {
            _ = reader.ReadBit();
        }
        catch (EndOfStreamException)
        {
            threw = true;
        }

        threw.Should().BeTrue();
    }
    [Fact]
    public void ReuseBlock_ImmediatelyAfterLeadingZeroClamp_RoundTrips()
    {
        // v0→v1 XOR has 62 leading zeros — the encoder clamps stored leading to 31 (5-bit
        // field), inflating the meaningful span. v1→v2 XOR fits inside that inflated window,
        // forcing the reuse-previous-block branch right after a clamped new block. Pins the
        // clamp/reuse interaction the geometry tests don't isolate.
        var v0 = 1.0;
        var v1 = BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(v0) ^ 0x3L);
        var v2 = BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(v1) ^ 0x1L);
        var input = new[] { v0, v1, v2 };

        var decoded = GorillaXorCodec.Decode(GorillaXorCodec.Encode(input), input.Length, long.MaxValue);

        decoded.Select(BitConverter.DoubleToInt64Bits).Should()
            .Equal(input.Select(BitConverter.DoubleToInt64Bits));
    }

    [Fact]
    public void XorBlockWithAllSixtyFourMeaningfulBits_RoundTrips()
    {
        // XOR with bit 63 and bit 0 both set: leading = 0, trailing = 0, meaningful = 64 —
        // the encoder stores 63 in its 6-bit length field and the decoder must reconstruct 64.
        var v0 = 0.0;
        var v1 = BitConverter.Int64BitsToDouble(unchecked((long)0x8000_0000_0000_0001UL));
        var input = new[] { v0, v1 };

        var decoded = GorillaXorCodec.Decode(GorillaXorCodec.Encode(input), input.Length, long.MaxValue);

        decoded.Select(BitConverter.DoubleToInt64Bits).Should()
            .Equal(input.Select(BitConverter.DoubleToInt64Bits));
    }

}
