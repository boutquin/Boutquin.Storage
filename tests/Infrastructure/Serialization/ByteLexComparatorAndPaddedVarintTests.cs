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

// M-1 (storage review-remediation test gaps): the byte-lex comparators on OrderedByteHelpers
// (CompareGuidByteLex / CompareDateTimeOffsetByteLex / CompareStringByteLex incl. the non-BMP
// surrogate path through MapUtf16ToCodePointOrder) and WriteVarUInt64Padded carry the
// byte-lex/CompareTo consistency invariant that LSM SSTable binary search relies on. These pin:
//   - sign(CompareXByteLex(a,b)) == byteLex(encode(a), encode(b)) for Guid / DateTimeOffset / string;
//   - the non-BMP discriminator that distinguishes the comparator from naive string.CompareOrdinal;
//   - WriteVarUInt64Padded's width-bound, value-exceeds-width, and destination-too-short throws.
public sealed class ByteLexComparatorAndPaddedVarintTests
{
    // ── AC-1.4: CompareGuidByteLex matches byte-lex of the encoded form ───────────────────

    [Fact]
    public void CompareGuidByteLex_Sign_Matches_ByteLex_Of_Encoding()
    {
        var guids = new[]
        {
            Guid.Empty,
            new Guid("00000000-0000-0000-0000-000000000001"),
            new Guid("12345678-1234-5678-1234-567812345678"),
            new Guid("87654321-4321-8765-4321-876543218765"),
            new Guid("ffffffff-ffff-ffff-ffff-ffffffffffff"),
        };

        AssertComparatorTracksByteLex(guids, OrderedByteHelpers.CompareGuidByteLex, EncodeGuid);
    }

    // ── AC-1.4: CompareDateTimeOffsetByteLex matches byte-lex of the encoded form ──────────

    [Fact]
    public void CompareDateTimeOffsetByteLex_Sign_Matches_ByteLex_Of_Encoding()
    {
        var utcAnchor = new DateTimeOffset(2026, 4, 22, 12, 0, 0, TimeSpan.Zero);
        var values = new[]
        {
            DateTimeOffset.MinValue,
            new DateTimeOffset(2026, 4, 21, 0, 0, 0, TimeSpan.Zero),     // earlier instant
            utcAnchor.ToOffset(TimeSpan.FromHours(-5)),                  // same instant, low offset
            utcAnchor.ToOffset(TimeSpan.Zero),                          // same instant, mid offset
            utcAnchor.ToOffset(TimeSpan.FromHours(5)),                  // same instant, high offset
            new DateTimeOffset(2026, 4, 23, 0, 0, 0, TimeSpan.Zero),     // later instant
            DateTimeOffset.MaxValue,
        };

        AssertComparatorTracksByteLex(values, OrderedByteHelpers.CompareDateTimeOffsetByteLex, EncodeDateTimeOffset);
    }

    [Fact]
    public void CompareDateTimeOffsetByteLex_SameInstantDifferentOffset_OrdersByOffset()
    {
        // The BCL DateTimeOffset.CompareTo reports two representations of the same instant as
        // equal; the byte-lex comparator must NOT — the encodings differ in the offset bytes, and
        // a comparator that reports 0 where the bytes differ breaks the equality-boundary invariant.
        var utcAnchor = new DateTimeOffset(2026, 4, 22, 12, 0, 0, TimeSpan.Zero);
        var low = utcAnchor.ToOffset(TimeSpan.FromHours(-5));
        var high = utcAnchor.ToOffset(TimeSpan.FromHours(5));

        low.CompareTo(high).Should().Be(0, "sanity: the BCL comparator treats same-instant-different-offset as equal");
        OrderedByteHelpers.CompareDateTimeOffsetByteLex(low, high).Should().BeLessThan(0,
            "the byte-lex comparator must order by offset (lower first) at the same instant — the encodings differ");
    }

    // ── AC-1.4: CompareStringByteLex matches byte-lex incl. the non-BMP surrogate path ────

    [Fact]
    public void CompareStringByteLex_Sign_Matches_ByteLex_Of_Encoding_AsciiAndMultibyte()
    {
        var strings = new[] { "", "a", "aa", "ab", "abc", "b", "café", "z", "☕" };
        AssertComparatorTracksByteLex(strings!, OrderedByteHelpers.CompareStringByteLex, EncodeOrderedString);
    }

    [Fact]
    public void CompareStringByteLex_NonBmpSurrogatePair_Sorts_Above_Bmp_MatchingUtf8ByteLex()
    {
        // A supplementary-plane code point (U+1F600 GRINNING FACE, encoded UTF-16 as the surrogate
        // pair D83D DE00) must sort ABOVE a BMP code point in U+E000..U+FFFF (here U+E000), matching
        // UTF-8 byte order (lead bytes 0xF0 vs 0xEE). This is exactly the case where naive
        // string.CompareOrdinal DISAGREES — ordinal sorts the surrogate code units (0xD83D) below
        // 0xE000. The test is discriminating: it would FAIL against CompareOrdinal, so it genuinely
        // exercises MapUtf16ToCodePointOrder.
        const string emoji = "\U0001F600"; // surrogate pair D83D DE00
        const string bmpPrivateUse = "";

        // The comparator under test: emoji sorts AFTER the BMP char (matches UTF-8 byte-lex).
        OrderedByteHelpers.CompareStringByteLex(emoji, bmpPrivateUse).Should().BeGreaterThan(0,
            "supplementary-plane code points sort above U+E000..U+FFFF in UTF-8 byte order");

        // Discriminator: naive ordinal comparison gives the OPPOSITE sign. If MapUtf16ToCodePointOrder
        // were removed and the comparator fell back to ordinal, the assertion above would flip.
        string.CompareOrdinal(emoji, bmpPrivateUse).Should().BeLessThan(0,
            "sanity: ordinal (UTF-16 code-unit) order disagrees — this is the case the comparator must fix");

        // And the actual encoded bytes confirm the comparator tracks the wire: emoji encoding sorts after.
        ByteLex(EncodeOrderedString(emoji), EncodeOrderedString(bmpPrivateUse)).Should().BeGreaterThan(0);
    }

    [Fact]
    public void CompareStringByteLex_NullHandling_NullSortsFirst()
    {
        OrderedByteHelpers.CompareStringByteLex(null, "a").Should().BeLessThan(0);
        OrderedByteHelpers.CompareStringByteLex("a", null).Should().BeGreaterThan(0);
        OrderedByteHelpers.CompareStringByteLex(null, null).Should().Be(0);
    }

    // ── AC-1.5: WriteVarUInt64Padded width-bound / value-exceeds-width / destination-too-short ──

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    [InlineData(-1)]
    [InlineData(100)]
    public void WriteVarUInt64Padded_WidthOutsideOneToTen_ThrowsArgumentOutOfRange(int width)
    {
        Span<byte> dest = stackalloc byte[16];
        // Local function so the span can cross the lambda boundary issue — assert via try/catch.
        ArgumentOutOfRangeException? caught = null;
        try
        {
            VarintHelpers.WriteVarUInt64Padded(dest, 1UL, width);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            caught = ex;
        }

        caught.Should().NotBeNull("width outside [1, 10] must throw ArgumentOutOfRangeException");
        caught!.ParamName.Should().Be("width");
    }

    [Fact]
    public void WriteVarUInt64Padded_ValueExceedsWidthBitCapacity_ThrowsArgumentOutOfRange()
    {
        // A 1-byte padded varint carries at most 7 bits (value <= 127). 128 overflows it.
        Span<byte> dest = stackalloc byte[10];
        ArgumentOutOfRangeException? caught = null;
        try
        {
            VarintHelpers.WriteVarUInt64Padded(dest, 128UL, width: 1);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            caught = ex;
        }

        caught.Should().NotBeNull("a value exceeding the width's bit-capacity must throw");
        caught!.ParamName.Should().Be("value");
    }

    [Fact]
    public void WriteVarUInt64Padded_DestinationShorterThanWidth_ThrowsArgumentException()
    {
        // width 5 but only 3 bytes of destination available.
        Span<byte> dest = stackalloc byte[3];
        ArgumentException? caught = null;
        try
        {
            VarintHelpers.WriteVarUInt64Padded(dest, 1UL, width: 5);
        }
        catch (ArgumentException ex)
        {
            caught = ex;
        }

        caught.Should().NotBeNull("a destination shorter than the requested width must throw");
        caught!.ParamName.Should().Be("destination");
    }

    [Theory]
    [InlineData(0UL, 1)]
    [InlineData(127UL, 1)]
    [InlineData(0UL, 5)]
    [InlineData(300UL, 3)]
    [InlineData(ulong.MaxValue, 10)]
    public void WriteVarUInt64Padded_ValidWidth_DecodesBackToValue(ulong value, int width)
    {
        // The padded encoding is a valid (non-canonical) ULEB128 of exactly `width` bytes that
        // ReadVarUInt64 decodes back to the original value.
        Span<byte> dest = stackalloc byte[10];
        VarintHelpers.WriteVarUInt64Padded(dest[..width], value, width);

        // The first width-1 bytes must carry the continuation bit; the last must clear it.
        for (var i = 0; i < width - 1; i++)
        {
            (dest[i] & 0x80).Should().Be(0x80, $"byte {i} of a {width}-byte padded varint must set the continuation bit");
        }
        (dest[width - 1] & 0x80).Should().Be(0, $"the final byte of a {width}-byte padded varint must clear the continuation bit");

        using var ms = new MemoryStream(dest[..width].ToArray());
        VarintHelpers.ReadVarUInt64(ms).Should().Be(value);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────

    private static byte[] EncodeGuid(Guid g)
    {
        Span<byte> buf = stackalloc byte[16];
        g.TryWriteBytes(buf, bigEndian: true, out _);
        return buf.ToArray();
    }

    private static byte[] EncodeDateTimeOffset(DateTimeOffset v)
    {
        using var ms = new MemoryStream();
        OrderedByteHelpers.WriteDateTimeOffset(ms, v);
        return ms.ToArray();
    }

    private static byte[] EncodeOrderedString(string v)
    {
        using var ms = new MemoryStream();
        OrderedByteHelpers.WriteOrderedString(ms, v);
        return ms.ToArray();
    }

    // For every ordered pair, sign(comparator) must equal sign(byte-lex(encode(a), encode(b))).
    private static void AssertComparatorTracksByteLex<T>(
        T[] values,
        Func<T, T, int> comparator,
        Func<T, byte[]> encode)
    {
        for (var i = 0; i < values.Length; i++)
        {
            for (var j = 0; j < values.Length; j++)
            {
                var cmp = Math.Sign(comparator(values[i], values[j]));
                var lex = Math.Sign(ByteLex(encode(values[i]), encode(values[j])));
                cmp.Should().Be(lex,
                    $"sign(comparator({values[i]}, {values[j]})) must equal sign(byte-lex(encode(...)))");
            }
        }
    }

    private static int ByteLex(byte[] a, byte[] b)
    {
        var n = Math.Min(a.Length, b.Length);
        for (var i = 0; i < n; i++)
        {
            var d = a[i].CompareTo(b[i]);
            if (d != 0)
            {
                return d;
            }
        }
        return a.Length.CompareTo(b.Length);
    }
}
