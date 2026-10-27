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
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Boutquin.Storage.Infrastructure.TimeSeries.Columnar.Codecs;

/// <summary>
/// Scaled-integer + bit-packing codec for <see cref="decimal"/> columns. Selected per-column
/// by an explicit <c>[ColumnEncoding(ColumnEncoding.ScaledIntegerBitPacked)]</c> attribute on
/// the property; the unannotated default for decimals routes through
/// <c>DictionaryCodec.EncodeDecimal</c>.
/// </summary>
/// <remarks>
/// <para><b>Authority:</b> Apache Parquet file format "Data Pages" § (bit-packed integer
/// encodings). DDIA 2e Ch. 4 motivates column compression generally but does not describe
/// bit-packing; the encoding itself is Parquet's.</para>
/// <para><b>Algorithm:</b></para>
/// <list type="number">
/// <item>Find the maximum decimal scale (number of fractional digits) across the column —
///   call it <c>S</c>. When that raw scale exceeds 18 only because of trailing zeros, <c>S</c> is
///   the largest number of fractional digits any value needs exactly. Scale every value to a common base: <c>scaled[i] = values[i] × 10^S</c>
///   → long.</item>
/// <item>Compute <c>min = min(scaled)</c>, <c>max = max(scaled)</c>, <c>range = max − min</c>
///   as an unsigned 64-bit value (the span between two Int64 values always fits one).</item>
/// <item>Bit width <c>W = ceil(log2(range + 1))</c> — the minimum bits needed to hold the
///   shifted range <c>[0, range]</c>, at most 64.</item>
/// <item>Emit: <c>S</c> (byte), <c>min</c> (int64 LE), <c>W</c> (byte), then <c>N × W</c> bits
///   of packed payload (rounded up to whole bytes).</item>
/// </list>
/// <para><b>Totality:</b> encoding never refuses a decimal. The column is written before the
/// flush that carries it can succeed, and every value in it was already acknowledged, so a
/// column that no common scale of at most 18 fractional digits packs into Int64 (a value beyond
/// ±9.2 × 10^18, more than 18 fractional digits, or values whose common scale pushes one of them
/// past Int64) is written raw instead: the scale byte holds the marker <c>0xFF</c>, followed by
/// each value's four <see cref="decimal.GetBits(decimal)"/> words (int32 LE, 16 bytes per value),
/// which decode to the exact written representation.</para>
/// <para><b>Layout example:</b> 4 values <c>[100.25m, 100.50m, 100.75m, 101.00m]</c> all scale
/// to 10,025/10,050/10,075/10,100 at <c>S = 2</c>. min = 10025, range = 75, bit width = 7.
/// Output: 1 B scale + 8 B min + 1 B width + ceil(4×7/8) = 4 B payload = 14 bytes for 4
/// decimals (vs 64 bytes plain).</para>
/// </remarks>
internal static class ScaledIntegerBitPackedCodec
{
    public const ColumnEncodingTag Tag = ColumnEncodingTag.ScaledIntegerBitPacked;
    public const byte Version = 0;

    private const int HeaderSize = 1 /*scale*/ + 8 /*min*/ + 1 /*bitWidth*/;

    // Scale-byte marker for a column written as raw decimals because no common scale packs it.
    // Valid packed scales are 0..18, so the marker cannot collide with one.
    private const byte RawDecimalMarker = 0xFF;
    private const int RawDecimalBytes = 16;

    public static byte[] EncodeDecimal(ReadOnlySpan<decimal> values)
    {
        if (values.Length == 0)
        {
            return [];
        }

        // 1. Common scale = max individual scale.
        byte scale = 0;
        for (var i = 0; i < values.Length; i++)
        {
            var s = GetScale(values[i]);
            if (s > scale)
            {
                scale = s;
            }
        }

        // The raw scale counts trailing zeros (1.25m * 1.00000000000000000m carries scale 19), and
        // trailing zeros can also push a scaled value past Int64 (68250131.4000000000000000m at
        // scale 16). The column's raw common scale is kept whenever it fits, because decode
        // reconstructs values at that scale; otherwise fall back to the fewest fractional digits
        // that represent every value exactly — decimal equality is value equality.
        long[]? scaled;
        if (scale > 18 || !TryScaleColumn(values, scale, out scaled))
        {
            byte exactScale = 0;
            for (var i = 0; i < values.Length; i++)
            {
                var s = GetExactScale(values[i]);
                if (s > exactScale)
                {
                    exactScale = s;
                }
            }

            scale = Math.Min(scale, exactScale);

            // 2. No common scale packs every value into Int64 (10^19 already overflows it, and a
            // value can exceed Int64 at any scale): write the column raw rather than refuse values
            // the engine has already acknowledged.
            if (scale > 18 || !TryScaleColumn(values, scale, out scaled))
            {
                return EncodeRawDecimals(values);
            }
        }

        // 3. min / range.
        var min = scaled[0];
        var max = scaled[0];
        for (var i = 1; i < scaled.Length; i++)
        {
            if (scaled[i] < min)
            {
                min = scaled[i];
            }

            if (scaled[i] > max)
            {
                max = scaled[i];
            }
        }

        // The span between two Int64 values is at most 2^64 - 1, which a signed subtraction cannot
        // hold (long.MinValue..long.MaxValue) but an unsigned one always can: subtracting the
        // two's-complement bit patterns modulo 2^64 yields the exact non-negative span.
        var range = unchecked((ulong)max - (ulong)min);

        // 4. bit width for (range + 1) distinct shifted values.
        var bitWidth = range == 0UL ? (byte)0 : (byte)(64 - BitOperations.LeadingZeroCount(range));

        // 5. Pack payload. The bit count for a large whole-column flush exceeds 2^31 well
        // before the BYTE count reaches the array limit (~34 M rows at width 63), so the byte
        // math and the write cursor are 64-bit — the decode side already reads through a long
        // cursor for exactly this magnitude. Only a payload whose BYTES would exceed the CLR
        // array limit is genuinely unencodable, and that fails with a typed guard.
        var payloadBitCount = (long)values.Length * bitWidth;
        var payloadByteCountLong = (payloadBitCount + 7) / 8;
        if (payloadByteCountLong > Array.MaxLength - HeaderSize)
        {
            throw new InvalidOperationException(
                $"ScaledIntegerBitPacked column of {values.Length} rows at bit width {bitWidth} needs "
                + $"{payloadByteCountLong} payload bytes, which exceeds the CLR array limit. Reduce the "
                + "flush size or route this column to a different encoding.");
        }

        var payloadByteCount = (int)payloadByteCountLong;
        var output = new byte[HeaderSize + payloadByteCount];
        output[0] = scale;
        BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(1, 8), min);
        output[9] = bitWidth;

        if (bitWidth > 0)
        {
            var buffer = output.AsSpan(HeaderSize, payloadByteCount);
            var bitCursor = 0L;
            for (var i = 0; i < scaled.Length; i++)
            {
                var shifted = unchecked((ulong)scaled[i] - (ulong)min);
                WriteBits(buffer, bitCursor, bitWidth, shifted);
                bitCursor += bitWidth;
            }
        }

        return output;
    }

    /// <summary>
    /// Decodes <paramref name="count"/> values starting at <paramref name="firstRowIndex"/> from
    /// the whole-column payload. Bit arithmetic makes this O(count) regardless of firstRowIndex.
    /// <paramref name="maxDecodableRows"/> is the caller's ceiling on the number of rows this slice
    /// may decode (<paramref name="count"/>, not the absolute extent) — the only bound for a
    /// constant (bitWidth==0) column, whose ~0-byte payload yields no per-row ceiling. Defaults to
    /// <see cref="long.MaxValue"/> (no ceiling) for trusted callers.
    /// </summary>
    public static decimal[] DecodeDecimalSlice(ReadOnlySpan<byte> input, int firstRowIndex, int count, long maxDecodableRows = long.MaxValue)
    {
        if (count == 0)
        {
            return [];
        }

        if (input.Length > 0 && input[0] == RawDecimalMarker)
        {
            return DecodeRawDecimals(input, firstRowIndex, count);
        }

        if (input.Length < HeaderSize)
        {
            throw new InvalidDataException(
                $"ScaledIntegerBitPackedCodec payload too short: {input.Length} bytes < header size {HeaderSize}.");
        }

        var scale = input[0];
        if (scale > 18)
        {
            throw new InvalidDataException($"ScaledIntegerBitPackedCodec decoded invalid scale {scale} (> 18).");
        }

        var min = BinaryPrimitives.ReadInt64LittleEndian(input.Slice(1, 8));
        var bitWidth = input[9];
        if (bitWidth > 64)
        {
            // Mirrors the guard in DecodeDecimal: a corrupt header could otherwise drive ReadBits
            // with bitWidth > 64 (only reachable via projection-pushdown on a corrupt segment).
            throw new InvalidDataException($"ScaledIntegerBitPackedCodec decoded invalid bit width {bitWidth} (> 64).");
        }

        if (bitWidth == 0)
        {
            // A zero-bit-width (constant) column legitimately encodes any number of identical values
            // in a ~0-byte body, so the payload carries NO ceiling on `count` — the per-row
            // requiredBytes check below (which bounds the bitWidth>0 path) is vacuous here. The
            // footer-supplied count is attacker-controllable (CRC is forgeable), so a forged count
            // near int.MaxValue would otherwise drive a ~32 GB `new decimal[count]` (OOM DoS) on the
            // production read path. Bound the allocation — `count` — against the caller-supplied
            // ceiling (the per-row-group cap on the production read path). The absolute extent
            // `firstRowIndex + count` is the wrong quantity: a constant payload is the same header at
            // any row count, so a legitimate late row group starts past the per-row-group cap and
            // would be false-rejected. Matches the run-length slice decoders. This is the absolute
            // ceiling BSSG021's descriptor requires a constant-column path to carry in lieu of a
            // payload-derived bound.
            BoundConstantColumnRows(count, maxDecodableRows);

            var single = ScaledLongToDecimal(min, scale);
            var constResult = new decimal[count];
            for (var i = 0; i < count; i++)
            {
                constResult[i] = single;
            }

            return constResult;
        }

        // Bound the slice's bit range by the bytes actually present — mirrors the full
        // decoder's expectedPayloadBytes check. This MUST run before the new decimal[count]
        // allocation below: a forged count (e.g. int.MaxValue) over a tiny payload would
        // otherwise OOM on the allocation before this guard could fire. Without it a truncated
        // payload also surfaced as a raw IndexOutOfRangeException from the bit reader instead of
        // a typed exception.
        var requiredBits = ((long)firstRowIndex + count) * bitWidth;
        var requiredBytes = (requiredBits + 7) / 8;
        if (input.Length - HeaderSize < requiredBytes)
        {
            throw new InvalidDataException(
                $"ScaledIntegerBitPackedCodec payload truncated: need {requiredBytes} bytes after header "
                + $"for rows [{firstRowIndex}, {firstRowIndex + count}), have {input.Length - HeaderSize}.");
        }

        var result = new decimal[count];
        var payload = input[HeaderSize..];
        var bitCursor = (long)firstRowIndex * bitWidth;
        for (var i = 0; i < count; i++)
        {
            var shifted = ReadBits(payload, bitCursor, bitWidth);
            bitCursor += bitWidth;
            long scaled;
            unchecked
            {
                scaled = min + (long)shifted;
            }

            result[i] = ScaledLongToDecimal(scaled, scale);
        }

        return result;
    }

    public static decimal[] DecodeDecimal(ReadOnlySpan<byte> input, int count, long maxDecodableRows = long.MaxValue)
    {
        if (count == 0)
        {
            return [];
        }

        if (input.Length > 0 && input[0] == RawDecimalMarker)
        {
            return DecodeRawDecimals(input, 0, count);
        }

        if (input.Length < HeaderSize)
        {
            throw new InvalidDataException(
                $"ScaledIntegerBitPackedCodec payload too short: {input.Length} bytes < header size {HeaderSize}.");
        }

        var scale = input[0];
        if (scale > 18)
        {
            throw new InvalidDataException($"ScaledIntegerBitPackedCodec decoded invalid scale {scale} (> 18).");
        }

        var min = BinaryPrimitives.ReadInt64LittleEndian(input.Slice(1, 8));
        var bitWidth = input[9];
        if (bitWidth > 64)
        {
            throw new InvalidDataException($"ScaledIntegerBitPackedCodec decoded invalid bit width {bitWidth} (> 64).");
        }

        // The per-row payload check below bounds `count` for bitWidth>0, but is vacuous when
        // bitWidth==0 (expectedPayloadBytes = count*0 = 0, so the relational guard is never true).
        // A constant column legitimately carries no per-row payload, so it needs the independent
        // segment-level ceiling instead — bound BEFORE the `new decimal[count]` below, otherwise a
        // forged footer RowCount drives a multi-GB allocation (OOM DoS) on the constant-column path.
        if (bitWidth == 0)
        {
            BoundConstantColumnRows(count, maxDecodableRows);
        }

        // Keep the byte requirement in long arithmetic: count * bitWidth / 8 exceeds int.MaxValue for
        // a forged count (2^28 rows at 64 bits is 2^31 bytes), and narrowing it first wraps negative,
        // passes the comparison and lets new decimal[count] run before anything rejects the count.
        var expectedPayloadBits = (long)count * bitWidth;
        var expectedPayloadBytes = (expectedPayloadBits + 7) / 8;
        if (input.Length - HeaderSize < expectedPayloadBytes)
        {
            throw new InvalidDataException(
                $"ScaledIntegerBitPackedCodec payload truncated: need {expectedPayloadBytes} bytes after header, have {input.Length - HeaderSize}.");
        }

        var result = new decimal[count];
        if (bitWidth == 0)
        {
            // All values are identical and equal to min / 10^scale.
            var single = ScaledLongToDecimal(min, scale);
            for (var i = 0; i < count; i++)
            {
                result[i] = single;
            }

            return result;
        }

        // The check above bounds expectedPayloadBytes by the span length, so it fits an int; the bit
        // cursor does not (a payload over 256 MiB holds more than int.MaxValue bits).
        var payload = input.Slice(HeaderSize, (int)expectedPayloadBytes);
        var bitCursor = 0L;
        for (var i = 0; i < count; i++)
        {
            var shifted = ReadBits(payload, bitCursor, bitWidth);
            bitCursor += bitWidth;
            long scaled;
            unchecked
            {
                scaled = min + (long)shifted;
            }

            result[i] = ScaledLongToDecimal(scaled, scale);
        }

        return result;
    }

    // ───── Helpers ────────────────────────────────────────────────────

    // A constant (bitWidth==0) column encodes every row in a ~0-byte body, so the payload yields
    // no ceiling on the requested row count. The caller threads an independent allocation ceiling
    // (on the production read path, the per-row-group cap scaled by the number of row groups the
    // decode spans). A `requestedRows` exceeding that ceiling cannot have come from a real segment
    // and is a forged-footer DoS attempt — reject it with the same typed corruption exception the
    // truncation guards use, BEFORE any allocation.
    private static void BoundConstantColumnRows(long requestedRows, long maxDecodableRows)
    {
        if (requestedRows > maxDecodableRows)
        {
            throw new InvalidDataException(
                $"ScaledIntegerBitPackedCodec constant-column decode declares {requestedRows} rows, "
                + $"but the segment ceiling permits at most {maxDecodableRows}; segment is corrupt.");
        }
    }

    private static byte GetScale(decimal value)
    {
        Span<int> bits = stackalloc int[4];
        _ = decimal.GetBits(value, bits);
        // High word layout: bits 16..23 hold the scale (0..28).
        return (byte)((bits[3] >> 16) & 0xFF);
    }

    // Scales every value to `scale` fractional digits, returning false (instead of throwing) when a
    // scaled value does not fit Int64. The column's max - min span always fits the unsigned width.
    private static bool TryScaleColumn(ReadOnlySpan<decimal> values, byte scale, [NotNullWhen(true)] out long[]? scaled)
    {
        scaled = new long[values.Length];
        var multiplier = Pow10(scale);
        try
        {
            for (var i = 0; i < values.Length; i++)
            {
                var rescaled = decimal.Truncate(values[i] * multiplier);
                if (rescaled < long.MinValue || rescaled > long.MaxValue)
                {
                    scaled = null;
                    return false;
                }

                scaled[i] = (long)rescaled;
            }

            return true;
        }
        catch (OverflowException)
        {
            scaled = null;
            return false;
        }
    }

    // Fractional digits the value needs once trailing zeros are dropped.
    private static byte GetExactScale(decimal value)
    {
        Span<int> bits = stackalloc int[4];
        _ = decimal.GetBits(value, bits);
        var scale = (byte)((bits[3] >> 16) & 0xFF);
        var mantissa = ((UInt128)(uint)bits[2] << 64) | ((UInt128)(uint)bits[1] << 32) | (uint)bits[0];
        while (scale > 0 && mantissa % 10 == 0)
        {
            mantissa /= 10;
            scale--;
        }

        return scale;
    }

    private static long Pow10(byte scale)
    {
        long result = 1;
        for (var i = 0; i < scale; i++)
        {
            result *= 10;
        }

        return result;
    }

    // Raw layout: the marker byte, then each value's four decimal.GetBits words as int32 LE.
    private static byte[] EncodeRawDecimals(ReadOnlySpan<decimal> values)
    {
        var payloadBytes = (long)values.Length * RawDecimalBytes;
        if (payloadBytes > Array.MaxLength - 1)
        {
            throw new InvalidOperationException(
                $"ScaledIntegerBitPacked column of {values.Length} rows needs {payloadBytes} raw payload bytes, "
                + "which exceeds the CLR array limit. Reduce the flush size.");
        }

        var output = new byte[1 + payloadBytes];
        output[0] = RawDecimalMarker;
        Span<int> bits = stackalloc int[4];
        for (var i = 0; i < values.Length; i++)
        {
            _ = decimal.GetBits(values[i], bits);
            var slot = output.AsSpan(1 + (i * RawDecimalBytes), RawDecimalBytes);
            for (var w = 0; w < 4; w++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(slot.Slice(w * 4, 4), bits[w]);
            }
        }

        return output;
    }

    // Decodes rows [firstRowIndex, firstRowIndex + count) of a raw column. The payload bounds the
    // row count (16 bytes per row), so the check runs before the result is allocated.
    private static decimal[] DecodeRawDecimals(ReadOnlySpan<byte> input, int firstRowIndex, int count)
    {
        var requiredBytes = ((long)firstRowIndex + count) * RawDecimalBytes;
        if (input.Length - 1 < requiredBytes)
        {
            throw new InvalidDataException(
                $"ScaledIntegerBitPackedCodec raw payload truncated: need {requiredBytes} bytes after the marker "
                + $"for rows [{firstRowIndex}, {firstRowIndex + count}), have {input.Length - 1}.");
        }

        var result = new decimal[count];
        Span<int> bits = stackalloc int[4];
        for (var i = 0; i < count; i++)
        {
            var slot = input.Slice(1 + ((firstRowIndex + i) * RawDecimalBytes), RawDecimalBytes);
            for (var w = 0; w < 4; w++)
            {
                bits[w] = BinaryPrimitives.ReadInt32LittleEndian(slot.Slice(w * 4, 4));
            }

            try
            {
                result[i] = new decimal(bits);
            }
            catch (ArgumentException ex)
            {
                throw new InvalidDataException(
                    $"ScaledIntegerBitPackedCodec raw row {firstRowIndex + i} is not a valid decimal encoding.", ex);
            }
        }

        return result;
    }

    private static decimal ScaledLongToDecimal(long scaled, byte scale)
    {
        if (scale == 0)
        {
            return new decimal(scaled);
        }

        // Reconstruct the decimal at the column's common scale via the scaled mantissa, NOT by
        // dividing by 10^scale. Division collapses trailing zeros (10000 / 100 -> 100m, scale 0),
        // losing the fidelity of a fixed-scale column (100.00m would decode as 100m). The explicit
        // (lo, mid, hi, isNegative, scale) constructor preserves the scale bit-exactly.
        var negative = scaled < 0;
        var magnitude = negative ? unchecked((ulong)-scaled) : (ulong)scaled;
        var lo = (int)(magnitude & 0xFFFFFFFF);
        var mid = (int)(magnitude >> 32);
        return new decimal(lo, mid, 0, negative, scale);
    }

    // long bitOffset: a packed payload near the 2 GiB array limit holds > 2^31 bits — the
    // same reasoning as the read-side cursor. The derived byte index fits int because the
    // encode path bounds payload BYTES against Array.MaxLength before any write.
    private static void WriteBits(Span<byte> buffer, long bitOffset, byte bitWidth, ulong value)
    {
        var byteIndex = (int)(bitOffset >> 3);
        var bitInByte = (int)(bitOffset & 7);
        var remaining = bitWidth;
        while (remaining > 0)
        {
            var bitsThisStep = Math.Min(8 - bitInByte, remaining);
            // bitsThisStep is always 1–8 because bitInByte ∈ [0,7] and remaining ≥ 1, so
            // (8 − bitInByte) ∈ [1,8] and bitsThisStep = min(that, remaining). The 32-bit
            // 1u shift is therefore safe — it never reaches the UB shift-by-32 case.
            Debug.Assert(bitsThisStep is >= 1 and <= 8, $"bitsThisStep={bitsThisStep} out of [1,8]");
            var mask = (ulong)((1u << bitsThisStep) - 1);
            var chunk = (byte)(value & mask);
            buffer[byteIndex] |= (byte)(chunk << bitInByte);
            value >>= bitsThisStep;
            remaining -= (byte)bitsThisStep;
            byteIndex++;
            bitInByte = 0;
        }
    }

    // bitOffset is long: a packed payload near the 2 GiB array limit holds > 2^31 bits, so an
    // int bit cursor (firstRowIndex * bitWidth) could silently truncate to a wrong — possibly
    // negative — offset on the slice path.
    private static ulong ReadBits(ReadOnlySpan<byte> buffer, long bitOffset, byte bitWidth)
    {
        ulong result = 0;
        var byteIndex = (int)(bitOffset >> 3);
        var bitInByte = (int)(bitOffset & 7);
        var produced = 0;
        var remaining = bitWidth;
        while (remaining > 0)
        {
            var bitsThisStep = Math.Min(8 - bitInByte, remaining);
            var mask = (ulong)((1u << bitsThisStep) - 1);
            var chunk = ((ulong)buffer[byteIndex] >> bitInByte) & mask;
            result |= chunk << produced;
            produced += bitsThisStep;
            remaining -= (byte)bitsThisStep;
            byteIndex++;
            bitInByte = 0;
        }

        return result;
    }
}
