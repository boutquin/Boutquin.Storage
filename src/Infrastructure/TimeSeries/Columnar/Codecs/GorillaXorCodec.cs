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
using System.Numerics;

namespace Boutquin.Storage.Infrastructure.TimeSeries.Columnar.Codecs;

/// <summary>
/// Gorilla XOR codec for <see cref="double"/> columns. Exploits the observation that
/// consecutive IEEE-754 doubles in a time-series (price walks, sensor readings, curve yields)
/// share most of their leading and trailing bits; the XOR of two adjacent values therefore
/// has a small block of "meaningful" bits around the middle, which Gorilla encodes compactly.
/// </summary>
/// <remarks>
/// <para><b>Authority:</b> Pelkonen, T., Franklin, S., et al. (2015). <i>Gorilla: A Fast,
/// Scalable, In-Memory Time Series Database.</i> VLDB. §4.1.2.</para>
/// <para><b>Algorithm:</b></para>
/// <list type="number">
/// <item>The first value is emitted as a 64-bit raw IEEE-754 word.</item>
/// <item>For each subsequent value, compute <c>xor = current ^ previous</c>.
///   <list type="bullet">
///   <item>If <c>xor == 0</c>: emit a single <c>'0'</c> control bit. The value equals its predecessor.</item>
///   <item>Else emit <c>'1'</c>, then a second control bit:
///     <list type="bullet">
///     <item><c>'0'</c>: the meaningful bits of the new XOR fall within the previous block
///       (leading zero count ≥ previous, trailing zero count ≥ previous). Re-encode the XOR
///       using the previous block's width — no new 5+6-bit header needed.</item>
///     <item><c>'1'</c>: a new block. Emit 5 bits of leading-zero count (clamped to 31), 6
///       bits of <c>meaningful − 1</c> (0..63 → 1..64 bits), then the meaningful bits themselves.</item>
///     </list>
///   </item>
///   </list>
/// </item>
/// </list>
/// <para><b>Worst-case bound:</b> a high-entropy column where every XOR has a fresh random
/// bit pattern costs up to 77 bits per value (2 control bits + 5 + 6 + 64) — about 9.625 B
/// per value. Routing to this codec should therefore be gated on monotonicity / smoothness
/// expectations; for pure-random columns, raw IEEE-754 is slightly smaller and the routing
/// layer should choose plain encoding instead.</para>
/// <para><b>IEEE-754 edge cases:</b> the codec operates on the raw 64-bit representation via
/// <see cref="BitConverter.DoubleToUInt64Bits"/> / <see cref="BitConverter.UInt64BitsToDouble"/>,
/// so it round-trips every double — NaN (including non-canonical payloads), ±Infinity,
/// subnormals, signed zero distinction — bit-perfectly. XOR only loses information if the
/// bit-level operations are wrong, not because of floating-point semantics.</para>
/// </remarks>
internal static class GorillaXorCodec
{
    public const ColumnEncodingTag Tag = ColumnEncodingTag.GorillaXor;
    public const byte Version = 0;

    // Max leading-zero count encodable in 5 bits. Values 32..63 are clamped to 31; the
    // codec then treats those XORs as if they had 31 leading zeros — at worst costs a few
    // wasted bits in the meaningful span.
    private const int MaxLeading = 31;

    public static byte[] Encode(ReadOnlySpan<double> values)
    {
        if (values.Length == 0)
        {
            return [];
        }

        var writer = new BitWriter();

        var previousBits = BitConverter.DoubleToUInt64Bits(values[0]);
        writer.WriteBits(previousBits, 64);

        var hasPrevBlock = false;
        var prevLeading = 0;
        var prevTrailing = 0;
        var prevMeaningful = 0;

        for (var i = 1; i < values.Length; i++)
        {
            var currentBits = BitConverter.DoubleToUInt64Bits(values[i]);
            var xor = currentBits ^ previousBits;

            if (xor == 0)
            {
                writer.WriteBit(false);
            }
            else
            {
                writer.WriteBit(true);
                var leading = BitOperations.LeadingZeroCount(xor);
                var trailing = BitOperations.TrailingZeroCount(xor);
                if (leading > MaxLeading)
                {
                    leading = MaxLeading;
                }

                var meaningful = 64 - leading - trailing;

                if (hasPrevBlock && leading >= prevLeading && trailing >= prevTrailing)
                {
                    // Reuse previous block; re-encode XOR shifted to the previous window.
                    writer.WriteBit(false);
                    var shifted = xor >> prevTrailing;
                    writer.WriteBits(shifted & MaskLow(prevMeaningful), prevMeaningful);
                }
                else
                {
                    writer.WriteBit(true);
                    writer.WriteBits((ulong)leading, 5);
                    writer.WriteBits((ulong)(meaningful - 1), 6);
                    var shifted = xor >> trailing;
                    writer.WriteBits(shifted & MaskLow(meaningful), meaningful);
                    prevLeading = leading;
                    prevTrailing = trailing;
                    prevMeaningful = meaningful;
                    hasPrevBlock = true;
                }
            }

            previousBits = currentBits;
        }

        return writer.ToArray();
    }

    public static double[] Decode(ReadOnlySpan<byte> input, int count, long maxDecodableRows)
    {
        if (count == 0)
        {
            return [];
        }

        // Per-row-group allocation ceiling — the same bound every sibling codec threads from
        // CompressedColumnarSegmentFormat.MaxDecodableRowsPerRowGroup. The ≥1-bit-per-value
        // self-bound below still amplifies a forged footer 64× (8 bits per payload byte vs the
        // 8 bytes per decoded double), so without this cap a ~268 MB garbage column blob passes
        // the self-bound for an int.MaxValue row count and drives a ~17 GB allocation.
        if (count > maxDecodableRows)
        {
            throw new CorruptSegmentException(
                $"GorillaXor declared row count {count} exceeds the per-row-group decode ceiling "
                + $"{maxDecodableRows}; segment is corrupt.",
                offset: 0);
        }

        if (input.Length < 8)
        {
            throw new InvalidDataException(
                $"GorillaXor payload is {input.Length} bytes; must be at least 8 bytes for the first raw value.");
        }

        // Bound the declared count against what the payload could possibly encode BEFORE allocating
        // — a forged footer RowCount must not drive a multi-gigabyte array from a few payload bytes.
        // After the 8-byte first value, every additional value costs at least one control bit, so at
        // most (remainingBytes × 8 + 1) values are decodable.
        var maxDecodable = 1L + ((long)(input.Length - 8) * 8L);
        if (count > maxDecodable)
        {
            throw new InvalidDataException(
                $"GorillaXor declared row count {count} exceeds the {maxDecodable} values the "
                + $"{input.Length}-byte payload could encode. Segment is corrupt.");
        }

        var result = new double[count];
        var reader = new BitReader(input);

        var previousBits = reader.ReadBits(64);
        result[0] = BitConverter.UInt64BitsToDouble(previousBits);

        // Decode tracks only the geometry the reuse-previous-block path consumes:
        // the trailing-zero count (to shift back) and the meaningful-bit width (to sign the
        // span). prevLeading is not needed on the read side — the encoder's leading-zero
        // comparison is asymmetric, and trailing is computed from (64 − leading − meaningful)
        // in the new-block branch.
        var prevTrailing = 0;
        var prevMeaningful = 0;

        for (var i = 1; i < count; i++)
        {
            if (!reader.ReadBit())
            {
                // '0': value identical to previous.
                result[i] = BitConverter.UInt64BitsToDouble(previousBits);
                continue;
            }

            ulong xor;
            if (!reader.ReadBit())
            {
                // '0': reuse previous block geometry.
                if (prevMeaningful == 0)
                {
                    throw new InvalidDataException(
                        "GorillaXor decoded a reuse-previous-block control word before any block had been declared.");
                }

                var shifted = reader.ReadBits(prevMeaningful);
                xor = shifted << prevTrailing;
            }
            else
            {
                // '1': new block header.
                var leading = (int)reader.ReadBits(5);
                var meaningful = (int)reader.ReadBits(6) + 1;
                var trailing = 64 - leading - meaningful;
                if (trailing < 0)
                {
                    throw new InvalidDataException(
                        $"GorillaXor decoded invalid block header (leading={leading}, meaningful={meaningful}, implied trailing={trailing}).");
                }

                var shifted = reader.ReadBits(meaningful);
                xor = shifted << trailing;
                prevTrailing = trailing;
                prevMeaningful = meaningful;
            }

            previousBits ^= xor;
            result[i] = BitConverter.UInt64BitsToDouble(previousBits);
        }

        return result;
    }

    /// <summary>Returns a mask with the low <paramref name="bitCount"/> bits set. Handles <c>bitCount == 64</c> correctly.</summary>
    private static ulong MaskLow(int bitCount)
        => bitCount == 64 ? ulong.MaxValue : (1UL << bitCount) - 1UL;
}
