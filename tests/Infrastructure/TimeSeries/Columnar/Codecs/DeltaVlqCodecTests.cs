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

public sealed class DeltaVlqCodecTests
{
    // ───── Int64 ─────────────────────────────────────────────────────

    [Fact]
    public void Int64_Empty_RoundTrips()
    {
        DeltaVlqCodec.EncodeInt64([]).Should().BeEmpty();
        DeltaVlqCodec.DecodeInt64([], 0).Should().BeEmpty();
    }

    [Fact]
    public void Int64_SingleValue_RoundTrips()
    {
        var input = new long[] { 12345 };
        var decoded = DeltaVlqCodec.DecodeInt64(DeltaVlqCodec.EncodeInt64(input), input.Length);
        decoded.Should().Equal(input);
    }

    [Fact]
    public void Int64_MonotoneIncreasing_RoundTrips()
    {
        var input = Enumerable.Range(0, 1000).Select(i => (long)(i * 1000)).ToArray();
        var decoded = DeltaVlqCodec.DecodeInt64(DeltaVlqCodec.EncodeInt64(input), input.Length);
        decoded.Should().Equal(input);
    }

    [Fact]
    public void Int64_ConstantDeltaRun_EncodesCompactly()
    {
        // 100 identical deltas should collapse to ~4 bytes (start + runLen + delta ≈ 3–4 VLQ bytes).
        var input = Enumerable.Range(0, 100).Select(i => (long)(i * 7)).ToArray();
        var encoded = DeltaVlqCodec.EncodeInt64(input);

        encoded.Length.Should().BeLessThan(10);
        DeltaVlqCodec.DecodeInt64(encoded, input.Length).Should().Equal(input);
    }

    [Fact]
    public void Int64_AlternatingDeltas_RoundTrips()
    {
        var input = new long[] { 100, 110, 105, 115, 110, 120, 115 };
        var decoded = DeltaVlqCodec.DecodeInt64(DeltaVlqCodec.EncodeInt64(input), input.Length);
        decoded.Should().Equal(input);
    }

    [Fact]
    public void Int64_NegativeDeltas_RoundTrips()
    {
        var input = new long[] { 1000, 900, 800, 700, 600 };
        var decoded = DeltaVlqCodec.DecodeInt64(DeltaVlqCodec.EncodeInt64(input), input.Length);
        decoded.Should().Equal(input);
    }

    [Fact]
    public void Int64_MixedSigns_RoundTrips()
    {
        var input = new long[] { -100, -50, 0, 50, 100, 50, 0, -50, -100 };
        var decoded = DeltaVlqCodec.DecodeInt64(DeltaVlqCodec.EncodeInt64(input), input.Length);
        decoded.Should().Equal(input);
    }

    [Fact]
    public void Int64_Boundaries_RoundTrip()
    {
        var input = new[] { long.MinValue, 0L, long.MaxValue };
        var decoded = DeltaVlqCodec.DecodeInt64(DeltaVlqCodec.EncodeInt64(input), input.Length);
        decoded.Should().Equal(input);
    }

    // ───── Int32 ─────────────────────────────────────────────────────

    [Fact]
    public void Int32_MonotoneIncreasing_RoundTrips()
    {
        var input = Enumerable.Range(0, 500).ToArray();
        var decoded = DeltaVlqCodec.DecodeInt32(DeltaVlqCodec.EncodeInt32(input), input.Length);
        decoded.Should().Equal(input);
    }

    [Fact]
    public void Int32_Boundaries_RoundTrip()
    {
        var input = new[] { int.MinValue, 0, int.MaxValue };
        var decoded = DeltaVlqCodec.DecodeInt32(DeltaVlqCodec.EncodeInt32(input), input.Length);
        decoded.Should().Equal(input);
    }

    [Fact]
    public void Int32_DecodeInt64OverflowingValueAsInt32_Throws()
    {
        var input = new[] { long.MaxValue };
        var encoded = DeltaVlqCodec.EncodeInt64(input);

        var act = () => DeltaVlqCodec.DecodeInt32(encoded, 1);
        act.Should().Throw<InvalidDataException>()
            .WithMessage("*overflows Int32*");
    }
    // ───── Slice fail-closed contract ─────────────────────────────────
    // The slice decoder must throw on a payload exhausted before `count` values are
    // produced, exactly like the full-column decoder — a silent zero-filled tail on the
    // production ReadAsync path (which has no per-row-group CRC backstop) is wrong data.

    [Fact]
    public void DecodeSliceInt64_TruncatedPayload_Throws()
    {
        // first=10 + one run of 2 (delta 10) → only 3 values present; ask for 5.
        using var ms = new MemoryStream();
        Vlq.WriteInt64(ms, 10);
        Vlq.WriteUInt64(ms, 2);
        Vlq.WriteInt64(ms, 10);
        var payload = ms.ToArray();

        var act = () => DeltaVlqCodec.DecodeSliceInt64(payload, 0, 5);
        act.Should().Throw<InvalidDataException>()
            .WithMessage("*exhausted*");
    }

    [Fact]
    public void DecodeSliceInt64_ZeroRunLength_Throws()
    {
        // first=10 + a run with length 0 — malformed (the encoder only emits runs >= 1).
        using var ms = new MemoryStream();
        Vlq.WriteInt64(ms, 10);
        Vlq.WriteUInt64(ms, 0);
        Vlq.WriteInt64(ms, 5);
        var payload = ms.ToArray();

        var act = () => DeltaVlqCodec.DecodeSliceInt64(payload, 0, 2);
        act.Should().Throw<InvalidDataException>()
            .WithMessage("*run length*");
    }

    [Fact]
    public void DecodeSliceInt32_TruncatedPayload_Throws()
    {
        using var ms = new MemoryStream();
        Vlq.WriteInt64(ms, 10);
        Vlq.WriteUInt64(ms, 2);
        Vlq.WriteInt64(ms, 10);
        var payload = ms.ToArray();

        var act = () => DeltaVlqCodec.DecodeSliceInt32(payload, 0, 5);
        act.Should().Throw<InvalidDataException>()
            .WithMessage("*exhausted*");
    }

}
