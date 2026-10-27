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

public sealed class DictionaryCodecTests
{
    // ───── String ─────────────────────────────────────────────────────

    [Fact]
    public void String_Empty_RoundTrips()
    {
        DictionaryCodec.EncodeString([]).Should().BeEmpty();
        DictionaryCodec.DecodeString([], 0).Should().BeEmpty();
    }

    [Fact]
    public void String_AllSame_CompressesToDictOfOne()
    {
        var input = Enumerable.Repeat("AAPL", 1000).ToArray();
        var encoded = DictionaryCodec.EncodeString(input);

        // 1 dict size VLQ + 1 name-len VLQ + 4 UTF-8 bytes + 1000 × 1-byte IDs = ~1006 bytes.
        // Much better than 1000 × ~6 bytes raw.
        encoded.Length.Should().BeLessThan(1050);
        DictionaryCodec.DecodeString(encoded, input.Length).Should().Equal(input);
    }

    [Fact]
    public void String_LowCardinality_RoundTrips()
    {
        var tickers = new[] { "AAPL", "MSFT", "GOOG" };
        var input = Enumerable.Range(0, 100).Select(i => tickers[i % tickers.Length]).ToArray();
        var decoded = DictionaryCodec.DecodeString(DictionaryCodec.EncodeString(input), input.Length);
        decoded.Should().Equal(input);
    }

    [Fact]
    public void String_AllUnique_StillRoundTrips()
    {
        var input = Enumerable.Range(0, 100).Select(i => $"value-{i:D3}").ToArray();
        var decoded = DictionaryCodec.DecodeString(DictionaryCodec.EncodeString(input), input.Length);
        decoded.Should().Equal(input);
    }

    // The surrogate is passed as a code unit and spliced in here: an ill-formed string literal in
    // InlineData does not survive test-case serialization intact.
    [Theory]
    [InlineData(0xD800, "A", "B")] // lone high surrogate
    [InlineData(0xDC00, "A", "B")] // lone low surrogate
    [InlineData(0xDBFF, "", "")]   // high surrogate alone
    public void String_UnpairedSurrogate_RejectedAtEncode(int surrogate, string before, string after)
    {
        var illFormed = before + (char)surrogate + after;
        // A lenient UTF-8 encoder rewrites an unpaired surrogate to U+FFFD, so the decoded value
        // differs from the written one and two distinct inputs ("A\uD800B", "A\uDC00B") share one
        // dictionary entry. Ill-formed UTF-16 cannot round-trip; reject it like the tagged- and
        // ordered-wire string writers do.
        var act = () => DictionaryCodec.EncodeString(["AAPL", illFormed]);

        act.Should().Throw<ArgumentException>("an unpaired surrogate cannot be encoded in UTF-8 exactly");
    }

    [Fact]
    public void String_WellFormedSurrogatePair_RoundTripsExactly()
    {
        var input = new[] { "A\uD83D\uDE00B", "\uD83D\uDE00", "plain" };
        var decoded = DictionaryCodec.DecodeString(DictionaryCodec.EncodeString(input), input.Length);
        decoded.Should().Equal(input);
    }

    [Fact]
    public void String_NullRoundTripsAsNull()
    {
        // v1 (null-aware) layout reserves dictionary id 0 for null. After the Phase-D hardening
        // fix, null values round-trip as null rather than being coerced to the empty string at
        // encode time (DDIA 2e Ch. 4 pp 134–138: null is a distinct value, not a sentinel for "").
        var input = new string?[] { "a", null, "b", null, "a" };
        var decoded = DictionaryCodec.DecodeString(DictionaryCodec.EncodeString(input), input.Length);
        decoded.Should().Equal("a", null, "b", null, "a");
    }

    [Fact]
    public void String_NullAndEmptyAreDistinct()
    {
        var input = new string?[] { "", null, "", null, "nonempty" };
        var decoded = DictionaryCodec.DecodeString(DictionaryCodec.EncodeString(input), input.Length);
        decoded.Should().Equal("", null, "", null, "nonempty");
    }

    [Fact]
    public void String_DecodeWithoutV1Magic_ThrowsInvalidData()
    {
        // H11 (greenfield consolidation): v2.0.0 ships a single string-dictionary format.
        // Inputs whose leading byte is not 0xFF (the StringDictMagic) must raise
        // InvalidDataException — there is no fallback decoder for pre-null-aware payloads
        // because no such payload ever landed on disk in a shipped release.
        var notV1 = new byte[] { 0x02, 0x01, (byte)'x', 0x01, (byte)'y', 0x00, 0x01 };
        var act = () => DictionaryCodec.DecodeString(notV1, count: 2);
        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void String_Unicode_RoundTrips()
    {
        var input = new[] { "αβγ", "日本語", "emoji-🚀", "αβγ" };
        var decoded = DictionaryCodec.DecodeString(DictionaryCodec.EncodeString(input), input.Length);
        decoded.Should().Equal(input);
    }

    // ───── Decimal ────────────────────────────────────────────────────

    [Fact]
    public void Decimal_Empty_RoundTrips()
    {
        DictionaryCodec.EncodeDecimal([]).Should().BeEmpty();
        DictionaryCodec.DecodeDecimal([], 0).Should().BeEmpty();
    }

    [Fact]
    public void Decimal_SingleValue_RoundTrips()
    {
        var input = new[] { 100.25m };
        var decoded = DictionaryCodec.DecodeDecimal(DictionaryCodec.EncodeDecimal(input), input.Length);
        decoded.Should().Equal(input);
    }

    [Fact]
    public void Decimal_LowCardinality_RoundTrips()
    {
        var buckets = new[] { 100.25m, 100.50m, 100.75m };
        var input = Enumerable.Range(0, 200).Select(i => buckets[i % buckets.Length]).ToArray();
        var decoded = DictionaryCodec.DecodeDecimal(DictionaryCodec.EncodeDecimal(input), input.Length);
        decoded.Should().Equal(input);
    }

    [Fact]
    public void Decimal_Boundaries_RoundTrip()
    {
        var input = new[] { decimal.MinValue, 0m, decimal.MaxValue, -1.25m };
        var decoded = DictionaryCodec.DecodeDecimal(DictionaryCodec.EncodeDecimal(input), input.Length);
        decoded.Should().Equal(input);
    }

    [Fact]
    public void Decimal_VariousScales_RoundTrip()
    {
        var input = new[] { 1m, 1.0m, 1.00m, 1.000m, 0.5m, 0.50m };
        var decoded = DictionaryCodec.DecodeDecimal(DictionaryCodec.EncodeDecimal(input), input.Length);
        decoded.Should().Equal(input);
    }
    // ───── Corrupt declared dictionary size: validate before allocating ─────
    // A well-formed payload can never declare more dictionary entries than it has bytes
    // (every string entry needs at least its length byte; every decimal entry is exactly
    // 16 bytes), so the declared size must be bounded by the remaining payload BEFORE the
    // dictionary array is allocated — a corrupt size near int.MaxValue otherwise forces a
    // multi-gigabyte allocation from a few corrupt bytes.

    [Fact]
    public void DecodeString_DeclaredDictSizeBeyondPayload_FailsFastWithoutAllocating()
    {
        using var ms = new MemoryStream();
        ms.WriteByte(0xFF); // StringDictMagic
        ms.WriteByte(0x01); // Version
        Vlq.WriteUInt64(ms, 4_000_000); // declared entries; zero payload bytes follow
        var payload = ms.ToArray();

        var before = GC.GetAllocatedBytesForCurrentThread();
        var act = () => DictionaryCodec.DecodeString(payload, 1);
        act.Should().Throw<InvalidDataException>()
            .WithMessage("*dictionary*");
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        allocated.Should().BeLessThan(1_000_000,
            "the declared size must be rejected before the dictionary array is allocated");
    }

    [Fact]
    public void DecodeDecimal_DeclaredDictSizeBeyondPayload_FailsFastWithoutAllocating()
    {
        using var ms = new MemoryStream();
        Vlq.WriteUInt64(ms, 4_000_000); // 4M entries × 16 bytes each, but zero bytes follow
        var payload = ms.ToArray();

        var before = GC.GetAllocatedBytesForCurrentThread();
        var act = () => DictionaryCodec.DecodeDecimal(payload, 1);
        act.Should().Throw<InvalidDataException>()
            .WithMessage("*dictionary*");
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        allocated.Should().BeLessThan(1_000_000,
            "the declared size must be rejected before the dictionary array is allocated");
    }

    [Fact]
    public void Decimal_AllDistinctValues_RoundTrip()
    {
        // All-distinct input: the dictionary is as large as the column itself (the worst case
        // for dictionary encoding) — the round-trip must still be byte-exact.
        var input = Enumerable.Range(0, 50).Select(i => 100m + (i * 0.013m)).ToArray();

        var decoded = DictionaryCodec.DecodeDecimal(DictionaryCodec.EncodeDecimal(input), input.Length);

        decoded.Should().Equal(input);
        for (var i = 0; i < input.Length; i++)
        {
            decimal.GetBits(decoded[i]).Should().Equal(decimal.GetBits(input[i]));
        }
    }

    [Fact]
    public void Encode_SameInput_IsByteDeterministic()
    {
        // Segment determinism (byte-identical output for identical input) requires the
        // dictionary id assignment to be order-deterministic, not hash-iteration-ordered.
        var strings = new[] { "b", "a", "c", "a", "b", null, "d" };
        DictionaryCodec.EncodeString(strings).Should().Equal(DictionaryCodec.EncodeString(strings));

        var decimals = new[] { 3m, 1m, 2m, 1m, 3m };
        DictionaryCodec.EncodeDecimal(decimals).Should().Equal(DictionaryCodec.EncodeDecimal(decimals));
    }

    // AC-8.8 (test gap): DictionaryCodec is the designated fallback for high-precision decimals
    // (scale > 18) that ScaledIntegerBitPackedCodec rejects because 10^(scale) overflows int64.
    // WriteDecimal stores all 16 raw bits of the decimal representation — no precision is lost,
    // and no artificial scale cap is imposed. This test documents that contract.
    [Fact]
    public void Decimal_ExcessPrecision_Scale19To28_RoundTripsExactly()
    {
        // These values have scale > 18; ScaledIntegerBitPackedCodec rejects them.
        // DictionaryCodec must accept them and preserve bit-exact representation.
        var input = new decimal[]
        {
            0.1234567890123456789m,   // scale 19
            1.23456789012345678901m,  // scale 20
            0.0000000001234567890123456789m, // scale 28
        };

        var encoded = DictionaryCodec.EncodeDecimal(input);
        var decoded = DictionaryCodec.DecodeDecimal(encoded, input.Length);

        decoded.Should().Equal(input, "DictionaryCodec must preserve full decimal precision for scale > 18");
        for (var i = 0; i < input.Length; i++)
        {
            decimal.GetBits(decoded[i]).Should().Equal(
                decimal.GetBits(input[i]),
                "the bit-exact decimal representation (including trailing zeros and scale) must survive encode+decode");
        }
    }

}
