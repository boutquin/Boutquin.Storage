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

namespace Boutquin.Storage.Infrastructure.Tests.Serialization;

/// <summary>
/// Phase A2 — property-based cover of the canonicalisation invariant:
/// <c>sign(byte-lex(encode(a, N), encode(b, N))) == sign(decimal.Compare(a, b))</c>
/// for every pair of decimals within the declared scale N. Without A2, this invariant
/// held only when two decimals shared the same raw scale; cross-scale cases (e.g.
/// <c>1.5m</c> vs <c>1.50m</c>) could byte-lex in the wrong direction. The
/// canonicalising <c>WriteDecimal(stream, value, scale)</c> restores the invariant
/// across all raw-scale combinations when the values are rounded to the declared scale.
/// </summary>
public sealed class DecimalCanonicalScaleTests
{
    /// <summary>
    /// Seeded deterministic generator across the mantissa × scale grid. 10k pairs
    /// (the "Phase A2" target). Inputs are quantised to the declared scale at the
    /// ingestion boundary (the documented caller contract — the writer now REJECTS excess
    /// precision instead of silently rounding, because silent rounding made two unequal keys
    /// byte-identical while the full-precision in-memory comparator kept them distinct).
    /// Each pair asserts byte-lex-of-encoded agrees with decimal.Compare on the canonical
    /// values across the full mantissa × raw-scale grid.
    /// </summary>
    [Fact]
    public void ByteLex_Matches_DecimalCompare_Across_MantissaScaleGrid()
    {
        const int scale = 4;
        const int samples = 10_000;
        var rng = new Random(424242);

        for (var i = 0; i < samples; i++)
        {
            // Quantise at the ingestion boundary, mirroring what a conforming caller does.
            // The raw-scale randomness in GenerateDecimal still exercises the cross-raw-scale
            // canonicalisation grid (scale 0..4 survives quantisation with varied raw scales).
            var a = decimal.Round(GenerateDecimal(rng), scale, MidpointRounding.ToEven);
            var b = decimal.Round(GenerateDecimal(rng), scale, MidpointRounding.ToEven);

            var lex = ByteLexEncodedCompare(a, b, scale);
            var num = a.CompareTo(b);

            Math.Sign(lex).Should().Be(
                Math.Sign(num),
                $"byte-lex({a}, {b}) at scale={scale} should match decimal.Compare after canonicalisation.");
        }
    }

    [Fact]
    public void Equivalent_Values_At_Different_Raw_Scales_Produce_Identical_Bytes()
    {
        const int scale = 6;
        var cases = new (decimal A, decimal B)[]
        {
            (1m, 1.000000m),
            (2.5m, 2.500000m),
            (-0.1m, -0.100000m),
            (100m, 100.000000m),
            (decimal.Zero, 0.000000m),
        };

        foreach (var (a, b) in cases)
        {
            var ba = EncodeDecimal(a, scale);
            var bb = EncodeDecimal(b, scale);
            ba.Should().Equal(bb, $"({a}, {b}) at scale={scale} should canonicalise to identical bytes");
        }
    }

    private static decimal GenerateDecimal(Random rng)
    {
        // Positive 64-bit magnitude in the low/mid words + random sign + scale in [0, 10].
        // decimal's (lo, mid, hi, isNegative, scale) constructor takes a UNSIGNED 96-bit
        // magnitude; passing signed two's-complement bits here would create values outside
        // the intended range and confuse the canonicalisation test.
        var absMantissa = ((ulong)(uint)rng.Next() << 32) | (uint)rng.Next();
        var isNeg = rng.Next(2) == 0;
        var scale = (byte)rng.Next(0, 11);
        return new decimal(
            lo: (int)(absMantissa & 0xFFFFFFFFul),
            mid: (int)((absMantissa >> 32) & 0xFFFFFFFFul),
            hi: 0,
            isNegative: isNeg,
            scale: scale);
    }

    private static int ByteLexEncodedCompare(decimal a, decimal b, int scale)
    {
        var ba = EncodeDecimal(a, scale);
        var bb = EncodeDecimal(b, scale);
        return ba.AsSpan().SequenceCompareTo(bb);
    }

    private static byte[] EncodeDecimal(decimal value, int scale)
    {
        using var ms = new MemoryStream();
        OrderedByteHelpers.WriteDecimal(ms, value, scale);
        return ms.ToArray();
    }

    /// <summary>
    /// Fail-closed corruption typing: a corrupt SCALE byte (decoding to a scale &gt; 28) must
    /// surface as <see cref="InvalidDataException"/> — the documented corruption signal — not
    /// as a raw <see cref="ArgumentOutOfRangeException"/> escaping the
    /// <c>decimal(int,int,int,bool,byte)</c> constructor. The discriminator guard already
    /// fails closed; the scale byte is the same untrusted on-disk surface.
    /// </summary>
    [Theory]
    [InlineData(0x00)] // scale decodes to 255
    [InlineData(0x10)] // scale decodes to 239
    [InlineData(0xE2)] // scale decodes to 29 — first invalid value past the cap
    public void ReadDecimal_CorruptScaleByte_FailsClosedWithInvalidDataException(byte corruptScaleByte)
    {
        var encoded = EncodeDecimal(1.5m, scale: 1);
        encoded[13] = corruptScaleByte;
        using var ms = new MemoryStream(encoded);

        var act = () => OrderedByteHelpers.ReadDecimal(ms);

        act.Should().Throw<InvalidDataException>()
            .WithMessage("*scale*", because: "a corrupt scale byte is on-disk corruption, not a caller error");
    }

    /// <summary>
    /// Negative-path twin: the scale byte is complemented before decoding on negative values,
    /// so the corrupt raw byte 0xFF decodes to scale 255 after the complement.
    /// </summary>
    [Fact]
    public void ReadDecimal_CorruptScaleByte_NegativePath_FailsClosed()
    {
        var encoded = EncodeDecimal(-1.5m, scale: 1);
        encoded[13] = 0xFF; // complemented to 0x00 → scale 255
        using var ms = new MemoryStream(encoded);

        var act = () => OrderedByteHelpers.ReadDecimal(ms);

        act.Should().Throw<InvalidDataException>().WithMessage("*scale*");
    }

    /// <summary>
    /// Characterization pin for the existing discriminator guard (previously untested).
    /// </summary>
    [Theory]
    [InlineData(0x03)]
    [InlineData(0x7F)]
    [InlineData(0xFF)]
    public void ReadDecimal_CorruptDiscriminator_FailsClosedWithInvalidDataException(byte discriminator)
    {
        var encoded = EncodeDecimal(1.5m, scale: 1);
        encoded[0] = discriminator;
        using var ms = new MemoryStream(encoded);

        var act = () => OrderedByteHelpers.ReadDecimal(ms);

        act.Should().Throw<InvalidDataException>().WithMessage("*discriminator*");
    }
}
