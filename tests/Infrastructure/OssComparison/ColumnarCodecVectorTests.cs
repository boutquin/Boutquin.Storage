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

namespace Boutquin.Storage.Infrastructure.Tests.OssComparison;

// These are scalar, hand-computed repository-format vectors. Gorilla shares the XOR/window
// algorithm with Prometheus; delta, RLE and dictionary share Parquet's semantic primitives.
// Container headers, bit-width fields, delta runs and null IDs differ: no wire compatibility.
public sealed class ColumnarCodecVectorTests
{
    [Fact]
    public void DeltaRuns_MatchHandComputedZigZagAndRunVector()
    {
        long[] values = [10, 13, 16, 14, 12, 12];
        var bytes = Convert.FromHexString("14020602030100");
        DeltaVlqCodec.EncodeInt64(values).Should().Equal(bytes);
        DeltaVlqCodec.DecodeInt64(bytes, values.Length).Should().Equal(values);
        for (var start = 0; start < values.Length; start++)
        {
            DeltaVlqCodec.DecodeSliceInt64(bytes, start, values.Length - start).Should().Equal(values[start..]);
        }
        AssertAllTruncationsFail(bytes, b => DeltaVlqCodec.DecodeInt64(b, values.Length));
    }

    [Fact]
    public void DateDeltaOfDelta_MatchesHandComputedPositiveAndNegativeChanges()
    {
        DateOnly[] values = [.. new[] { 100, 101, 103, 106, 106 }.Select(DateOnly.FromDayNumber)];
        var bytes = Convert.FromHexString("C80102020205");
        DeltaOfDeltaVlqCodec.Encode(values).Should().Equal(bytes);
        DeltaOfDeltaVlqCodec.Decode(bytes, values.Length).Should().Equal(values);
        AssertAllTruncationsFail(bytes, b => DeltaOfDeltaVlqCodec.Decode(b, values.Length));
    }

    [Fact]
    public void BooleanRuns_MatchHandComputedAlternatingRunsAndEverySlice()
    {
        bool[] values = [true, true, false, false, false, true];
        var bytes = Convert.FromHexString("01020301");
        BitmapRleCodec.Encode(values).Should().Equal(bytes);
        BitmapRleCodec.Decode(bytes, values.Length).Should().Equal(values);
        for (var start = 0; start < values.Length; start++)
        {
            BitmapRleCodec.DecodeSlice(bytes, start, values.Length - start).Should().Equal(values[start..]);
        }
        AssertAllTruncationsFail(bytes, b => BitmapRleCodec.Decode(b, values.Length));
    }

    [Fact]
    public void StringDictionary_DistinguishesNullEmptyAndUtf8WithStableIds()
    {
        string?[] values = [null, "", "é", "", null];
        var bytes = Convert.FromHexString("FF01020002C3A90001020100");
        DictionaryCodec.EncodeString(values).Should().Equal(bytes);
        DictionaryCodec.DecodeString(bytes, values.Length).Should().Equal(values);
        for (var start = 0; start < values.Length; start++)
        {
            DictionaryCodec.DecodeStringSlice(bytes, start, values.Length - start).Should().Equal(values[start..]);
        }
        AssertAllTruncationsFail(bytes, b => DictionaryCodec.DecodeString(b, values.Length));
    }

    [Fact]
    public void ScaledDecimalPacking_MatchesLittleEndianMinimumAndLsbFirstOffsets()
    {
        decimal[] values = [-1.25m, -1.20m, 0.00m, 1.25m];
        var bytes = Convert.FromHexString("0283FFFFFFFFFFFFFF0800057DFA");
        ScaledIntegerBitPackedCodec.EncodeDecimal(values).Should().Equal(bytes);
        ScaledIntegerBitPackedCodec.DecodeDecimal(bytes, values.Length).Should().Equal(values);
        for (var start = 0; start < values.Length; start++)
        {
            ScaledIntegerBitPackedCodec.DecodeDecimalSlice(bytes, start, values.Length - start).Should().Equal(values[start..]);
        }
        AssertAllTruncationsFail(bytes, b => ScaledIntegerBitPackedCodec.DecodeDecimal(b, values.Length));
        // 2-bit offsets 0,1,2,3 pack least-significant-first into E4, not Gorilla's MSB order.
        var twoBit = Convert.FromHexString("00000000000000000002E4");
        ScaledIntegerBitPackedCodec.EncodeDecimal([0m, 1m, 2m, 3m]).Should().Equal(twoBit);
        ScaledIntegerBitPackedCodec.DecodeDecimal(twoBit, 4).Should().Equal(0m, 1m, 2m, 3m);
    }

    [Fact]
    public void GorillaXor_MatchesIndependentTextBitWindowOracleIncludingReuseAndIeeePayloads()
    {
        ulong[] words = [0, 0, 1, 3, 2, 0x8000000000000000, 0x7FF0000000000000,
            0xFFF0000000000000, 0x7FF8000000000042, 0x7FF8000000000043, 0];
        var values = words.Select(BitConverter.UInt64BitsToDouble).ToArray();
        var expected = GorillaTextBits(words);
        GorillaXorCodec.Encode(values).Should().Equal(expected);
        GorillaXorCodec.Decode(expected, words.Length, words.Length)
            .Select(BitConverter.DoubleToUInt64Bits).Should().Equal(words);
        AssertAllTruncationsFail(expected, b => GorillaXorCodec.Decode(b, words.Length, words.Length));
    }

    [Fact]
    public void MalformedCodecMetadata_IsRejectedByProductionDecoders()
    {
        Action[] malformed = [
            () => DeltaVlqCodec.DecodeInt64([2, 0, 2], 2),
            () => BitmapRleCodec.Decode([1, 0], 1),
            () => DictionaryCodec.DecodeString(Convert.FromHexString("FF01010002"), 1),
            () => ScaledIntegerBitPackedCodec.DecodeDecimal(Convert.FromHexString("1D000000000000000000"), 1),
            () => ScaledIntegerBitPackedCodec.DecodeDecimal(Convert.FromHexString("00000000000000000041"), 1),
            () => DeltaOfDeltaVlqCodec.Decode(Convert.FromHexString("FFFFFFFFFFFFFFFFFF01"), 1),
            // '10' requests a previous Gorilla window before any has been defined.
            () => GorillaXorCodec.Decode(Convert.FromHexString("000000000000000080"), 2, 2)
        ];
        Action futureVersion = () => DictionaryCodec.DecodeString(Convert.FromHexString("FF0200"), 1);
        futureVersion.Should().Throw<UnsupportedFormatVersionException>();
        foreach (var decode in malformed)
        {
            decode.Should().Throw<InvalidDataException>();
        }
    }

    private static void AssertAllTruncationsFail(byte[] bytes, Action<byte[]> decode)
    {
        for (var length = 0; length < bytes.Length; length++)
        {
            var truncated = bytes[..length];
            Action act = () => decode(truncated);
            act.Should().Throw<Exception>("a prefix of {0} bytes cannot contain the whole column", length)
                .Where(error => error.GetType() == typeof(InvalidDataException) || error.GetType() == typeof(EndOfStreamException));
        }
    }

    // Deliberately uses a textual bitstream and substring windows rather than production bit
    // readers/writers or leading/trailing-zero intrinsics, so common bit cursor bugs cannot agree.
    private static byte[] GorillaTextBits(ulong[] words)
    {
        static string Bits(ulong word) => Convert.ToString(unchecked((long)word), 2).PadLeft(64, '0');
        var output = new StringBuilder(Bits(words[0]));
        (int Start, int End)? window = null;
        for (var index = 1; index < words.Length; index++)
        {
            var xor = words[index] ^ words[index - 1];
            if (xor == 0)
            {
                output.Append('0');
                continue;
            }
            var bits = Bits(xor);
            var first = Math.Min(31, bits.IndexOf('1'));
            var last = bits.LastIndexOf('1');
            if (window is { } previous && first >= previous.Start && last <= previous.End)
            {
                output.Append("10").Append(bits.AsSpan(previous.Start, previous.End - previous.Start + 1));
            }
            else
            {
                output.Append("11")
                    .Append(Convert.ToString(first, 2).PadLeft(5, '0'))
                    .Append(Convert.ToString(last - first, 2).PadLeft(6, '0'))
                    .Append(bits.AsSpan(first, last - first + 1));
                window = (first, last);
            }
        }
        var text = output.ToString().PadRight((output.Length + 7) / 8 * 8, '0');
        return Enumerable.Range(0, text.Length / 8).Select(i => Convert.ToByte(text.Substring(i * 8, 8), 2)).ToArray();
    }
}
