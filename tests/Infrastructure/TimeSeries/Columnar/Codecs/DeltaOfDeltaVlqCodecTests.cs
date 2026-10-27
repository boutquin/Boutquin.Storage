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

public sealed class DeltaOfDeltaVlqCodecTests
{
    [Fact]
    public void Empty_RoundTrips()
    {
        DeltaOfDeltaVlqCodec.Encode([]).Should().BeEmpty();
        DeltaOfDeltaVlqCodec.Decode([], 0).Should().BeEmpty();
    }

    [Fact]
    public void SingleDate_RoundTrips()
    {
        var input = new[] { new DateOnly(2020, 6, 15) };
        var decoded = DeltaOfDeltaVlqCodec.Decode(DeltaOfDeltaVlqCodec.Encode(input), input.Length);
        decoded.Should().Equal(input);
    }

    [Fact]
    public void TwoConsecutiveDates_RoundTrip()
    {
        var input = new[] { new DateOnly(2020, 1, 1), new DateOnly(2020, 1, 2) };
        var decoded = DeltaOfDeltaVlqCodec.Decode(DeltaOfDeltaVlqCodec.Encode(input), input.Length);
        decoded.Should().Equal(input);
    }

    [Fact]
    public void EvenlySpacedDaily_EncodesCompactly()
    {
        // 365 daily bars → delta = 1 everywhere → delta-of-delta = 0 everywhere after first.
        // Expected output: ~3 VLQ bytes for start day number + 1 byte for first delta + 363 × 1 byte
        // of zero delta-of-delta = ~367 bytes. Still an order of magnitude better than 4 × 365 plain.
        var input = Enumerable.Range(0, 365).Select(i => new DateOnly(2020, 1, 1).AddDays(i)).ToArray();
        var encoded = DeltaOfDeltaVlqCodec.Encode(input);

        encoded.Length.Should().BeLessThan(400);
        DeltaOfDeltaVlqCodec.Decode(encoded, input.Length).Should().Equal(input);
    }

    [Fact]
    public void WeeklyCadence_RoundTrips()
    {
        var input = Enumerable.Range(0, 52).Select(i => new DateOnly(2020, 1, 1).AddDays(i * 7)).ToArray();
        var decoded = DeltaOfDeltaVlqCodec.Decode(DeltaOfDeltaVlqCodec.Encode(input), input.Length);
        decoded.Should().Equal(input);
    }

    [Fact]
    public void IrregularCadence_RoundTrips()
    {
        var input = new[]
        {
            new DateOnly(2020, 1, 1),
            new DateOnly(2020, 1, 3),
            new DateOnly(2020, 1, 10),
            new DateOnly(2020, 2, 14),
            new DateOnly(2020, 6, 1),
            new DateOnly(2020, 12, 31),
        };
        var decoded = DeltaOfDeltaVlqCodec.Decode(DeltaOfDeltaVlqCodec.Encode(input), input.Length);
        decoded.Should().Equal(input);
    }

    [Fact]
    public void DateOnlyBoundaries_RoundTrip()
    {
        var input = new[] { DateOnly.MinValue, new DateOnly(2020, 1, 1), DateOnly.MaxValue };
        var decoded = DeltaOfDeltaVlqCodec.Decode(DeltaOfDeltaVlqCodec.Encode(input), input.Length);
        decoded.Should().Equal(input);
    }

    [Fact]
    public void NonMonotoneInput_StillRoundTrips()
    {
        // Codec doesn't require monotonicity for correctness — only for compression. Verify
        // a non-monotone sequence decodes identically.
        var input = new[]
        {
            new DateOnly(2020, 6, 1),
            new DateOnly(2020, 1, 1),
            new DateOnly(2020, 12, 1),
        };
        var decoded = DeltaOfDeltaVlqCodec.Decode(DeltaOfDeltaVlqCodec.Encode(input), input.Length);
        decoded.Should().Equal(input);
    }
    [Fact]
    public void Decode_TruncatedPayload_Throws()
    {
        var dates = Enumerable.Range(0, 5).Select(i => new DateOnly(2024, 1, 1).AddDays(i * 7)).ToArray();
        var encoded = DeltaOfDeltaVlqCodec.Encode(dates);
        var truncated = encoded[..(encoded.Length / 2)];

        // Fail-closed either way: truncation at a value boundary trips the codec's own
        // payload-exhaustion guard (InvalidDataException); truncation mid-VLQ trips the
        // varint reader's terminator guard (EndOfStreamException). Both are typed throws —
        // never a silently short or zero-filled result.
        var act = () => DeltaOfDeltaVlqCodec.Decode(truncated, dates.Length);
        act.Should().Throw<Exception>()
            .Which.Should().Match(e => e is InvalidDataException || e is EndOfStreamException);
    }

    [Fact]
    public void Decode_TruncatedPayloadCountTwo_ThrowsInvalidDataException()
    {
        // Encode two dates where the first date's day-number fits in one VLQ byte (day 0 =
        // DateOnly.MinValue zigzag-encodes to 0x00, a single byte). This guarantees the first
        // VLQ read completes, the cursor advances past it, and then the bounds check fires
        // BEFORE the second Vlq.ReadInt64 call — producing InvalidDataException, not the raw
        // EndOfStreamException that Vlq.ReadInt64 throws when it runs off the buffer.
        var first = DateOnly.MinValue;          // day-number 0  → zigzag 0x00 = 1 byte
        var second = first.AddDays(1);
        var encoded = DeltaOfDeltaVlqCodec.Encode([first, second]);

        // encoded[0] is the complete first-value VLQ (one byte); drop everything after.
        var truncated = encoded[..1];

        var act = () => DeltaOfDeltaVlqCodec.Decode(truncated, count: 2);

        act.Should().Throw<InvalidDataException>(
            "a payload truncated before the second value must throw InvalidDataException, not EndOfStreamException");
    }

}
