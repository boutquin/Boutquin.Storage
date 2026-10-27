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

public sealed class BitmapRleCodecTests
{
    [Fact]
    public void Empty_EncodesToZeroBytesAndDecodesToEmpty()
    {
        var encoded = BitmapRleCodec.Encode([]);
        encoded.Should().BeEmpty();

        var decoded = BitmapRleCodec.Decode(encoded, 0);
        decoded.Should().BeEmpty();
    }

    [Fact]
    public void SingleFalse_RoundTrips()
    {
        var input = new[] { false };
        var decoded = BitmapRleCodec.Decode(BitmapRleCodec.Encode(input), input.Length);
        decoded.Should().Equal(input);
    }

    [Fact]
    public void SingleTrue_RoundTrips()
    {
        var input = new[] { true };
        var decoded = BitmapRleCodec.Decode(BitmapRleCodec.Encode(input), input.Length);
        decoded.Should().Equal(input);
    }

    [Fact]
    public void AllSameValue_LongRun_RoundTrips()
    {
        var input = Enumerable.Repeat(true, 10_000).ToArray();
        var decoded = BitmapRleCodec.Decode(BitmapRleCodec.Encode(input), input.Length);
        decoded.Should().Equal(input);
    }

    [Fact]
    public void AlternatingValues_WorstCase_RoundTrips()
    {
        var input = Enumerable.Range(0, 100).Select(i => i % 2 == 0).ToArray();
        var decoded = BitmapRleCodec.Decode(BitmapRleCodec.Encode(input), input.Length);
        decoded.Should().Equal(input);
    }

    [Fact]
    public void MixedRuns_RoundTrips()
    {
        var input = new[]
        {
            true, true, true,
            false, false,
            true,
            false, false, false, false, false,
            true, true,
        };
        var decoded = BitmapRleCodec.Decode(BitmapRleCodec.Encode(input), input.Length);
        decoded.Should().Equal(input);
    }

    [Fact]
    public void LongRuns_CompressionRatioIsFavourable()
    {
        // 1000 trues + 1000 falses + 1000 trues should fit in ~6 bytes (1 start byte + 3 VLQs).
        var input = new bool[3000];
        for (var i = 0; i < 1000; i++)
        {
            input[i] = true;
        }

        for (var i = 2000; i < 3000; i++)
        {
            input[i] = true;
        }

        var encoded = BitmapRleCodec.Encode(input);

        encoded.Length.Should().BeLessThan(10);
        BitmapRleCodec.Decode(encoded, input.Length).Should().Equal(input);
    }

    [Fact]
    public void Decode_RowCountBelowPayloadImplies_Throws()
    {
        var input = Enumerable.Repeat(true, 10).ToArray();
        var encoded = BitmapRleCodec.Encode(input);

        var act = () => BitmapRleCodec.Decode(encoded, 5);
        act.Should().Throw<InvalidDataException>()
            .WithMessage("*overflows declared row count*");
    }

    [Fact]
    public void Decode_EmptyPayloadWithNonzeroCount_Throws()
    {
        var act = () => BitmapRleCodec.Decode([], 10);
        act.Should().Throw<InvalidDataException>()
            .WithMessage("*empty*");
    }
    [Fact]
    public void Decode_ZeroLengthRun_Throws()
    {
        // A corrupt/forged zero-length run must be rejected, not silently toggle parity. Here the
        // zero run flips the running value from true→false, so the following real run of 3 decodes
        // as all-false instead of all-true — wrong-but-silent without a guard. The sibling
        // DeltaVlqCodec throws InvalidDataException on the analogous (run <= 0) corruption.
        using var ms = new MemoryStream();
        ms.WriteByte(1);          // start value = true
        Vlq.WriteUInt64(ms, 0);   // forged zero-length run
        Vlq.WriteUInt64(ms, 3);   // real run of 3
        var payload = ms.ToArray();

        var act = () => BitmapRleCodec.Decode(payload, 3);
        act.Should().Throw<InvalidDataException>()
            .WithMessage("*run length*");
    }

    [Fact]
    public void DecodeSlice_TruncatedPayload_Throws()
    {
        // first=true + one run of 3 → only 3 values present; ask for 5. The slice decoder
        // must throw like the full decoder — not return a silently zero-filled (false) tail
        // on the production ReadAsync path, which has no per-row-group CRC backstop.
        using var ms = new MemoryStream();
        ms.WriteByte(1);
        Vlq.WriteUInt64(ms, 3);
        var payload = ms.ToArray();

        var act = () => BitmapRleCodec.DecodeSlice(payload, 0, 5);
        act.Should().Throw<InvalidDataException>()
            .WithMessage("*exhausted*");
    }

    [Fact]
    public void DecodeSlice_ZeroLengthRun_Throws()
    {
        // Symmetric to Decode_ZeroLengthRun_Throws — the slice path has the identical missing guard.
        // The zero run flips parity, so the following real run of 3 decodes all-false instead of
        // all-true: wrong-but-silent without a guard.
        using var ms = new MemoryStream();
        ms.WriteByte(1);          // start value = true
        Vlq.WriteUInt64(ms, 0);   // forged zero-length run
        Vlq.WriteUInt64(ms, 3);   // real run of 3
        var payload = ms.ToArray();

        var act = () => BitmapRleCodec.DecodeSlice(payload, 0, 3);
        act.Should().Throw<InvalidDataException>()
            .WithMessage("*run length*");
    }
}
