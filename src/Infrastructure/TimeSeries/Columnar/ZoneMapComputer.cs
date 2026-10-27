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
using Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar;

namespace Boutquin.Storage.Infrastructure.TimeSeries.Columnar;

/// <summary>
/// Computes per-row-group per-column zone-map statistics (Phase E1) from a fully-materialised
/// typed column array. Emits min, max, null-count, and distinct-count in the canonical on-disk
/// byte representation dictated by the column's <see cref="ColumnFieldTypeCode"/>.
/// </summary>
/// <remarks>
/// <para><b>Theory:</b> DDIA 2e Ch. 4 — zone-map skipping requires min/max be
/// comparable using the same ordering the reader will apply at predicate time. For fixed-width
/// numeric types the on-disk byte representation preserves natural numeric order under
/// little-endian unsigned byte comparison only for non-negative values; the reader therefore
/// uses <em>typed</em> comparison on the decoded min/max, not raw-byte compare.</para>
/// <para><b>String truncation:</b> string min/max values are truncated to the first 256 UTF-8
/// bytes of their canonical encoding. Truncation is safe for range-predicate
/// skipping: if the full value is a prefix of a longer value, truncating only loses
/// distinguishing information at tie-break time, which at worst produces a false positive
/// (row group not skipped, full decode still returns the correct rows).</para>
/// </remarks>
internal static class ZoneMapComputer
{
    /// <summary>
    /// Computes zone-map statistics for a single (row-group, column) pair. The row-group slice
    /// covers <paramref name="columnValues"/>[<paramref name="firstRowIndex"/>..
    /// <paramref name="firstRowIndex"/> + <paramref name="rowCount"/>].
    /// </summary>
    public static ColumnRowGroupStats Compute(
        Array columnValues,
        ColumnFieldTypeCode fieldType,
        int firstRowIndex,
        int rowCount)
    {
        ArgumentNullException.ThrowIfNull(columnValues);
        if (rowCount == 0)
        {
            return new ColumnRowGroupStats([], [], 0, 0);
        }

        return fieldType switch
        {
            ColumnFieldTypeCode.DateOnly => ComputeDateOnly((DateOnly[])columnValues, firstRowIndex, rowCount),
            ColumnFieldTypeCode.Int32 => ComputeInt32((int[])columnValues, firstRowIndex, rowCount),
            ColumnFieldTypeCode.Int64 => ComputeInt64((long[])columnValues, firstRowIndex, rowCount),
            ColumnFieldTypeCode.Decimal => ComputeDecimal((decimal[])columnValues, firstRowIndex, rowCount),
            ColumnFieldTypeCode.Double => ComputeDouble((double[])columnValues, firstRowIndex, rowCount),
            ColumnFieldTypeCode.Single => ComputeSingle((float[])columnValues, firstRowIndex, rowCount),
            ColumnFieldTypeCode.Boolean => ComputeBoolean((bool[])columnValues, firstRowIndex, rowCount),
            ColumnFieldTypeCode.String => ComputeString((string?[])columnValues, firstRowIndex, rowCount),
            ColumnFieldTypeCode.Enum => ComputeEnum(columnValues, firstRowIndex, rowCount),
            _ => throw new NotSupportedException(
                $"Zone-map computation has no implementation for field type {fieldType}."),
        };
    }

    // Enum bounds are the underlying value widened to int64 (bit transport for ulong
    // underlyings) — the same widening the encode path and the query side use, so the skip
    // math is internally consistent. Ordering follows the widened signed value; see
    // ColumnFieldTypeCode.Enum remarks for the >long.MaxValue ulong-member caveat.
    private static ColumnRowGroupStats ComputeEnum(Array values, int first, int count)
    {
        var underlyingIsUInt64 = Enum.GetUnderlyingType(values.GetType().GetElementType()!) == typeof(ulong);
        long Widen(object boxed) => underlyingIsUInt64
            ? unchecked((long)Convert.ToUInt64(boxed, System.Globalization.CultureInfo.InvariantCulture))
            : Convert.ToInt64(boxed, System.Globalization.CultureInfo.InvariantCulture);

        var min = Widen(values.GetValue(first)!);
        var max = min;
        var distinct = new HashSet<long>();
        for (var i = 0; i < count; i++)
        {
            var v = Widen(values.GetValue(first + i)!);
            if (v < min)
            {
                min = v;
            }

            if (v > max)
            {
                max = v;
            }

            distinct.Add(v);
        }

        return new ColumnRowGroupStats(
            MinValue: LittleEndianColumnBytes.From(min),
            MaxValue: LittleEndianColumnBytes.From(max),
            NullCount: 0,
            DistinctCount: distinct.Count);
    }

    private static ColumnRowGroupStats ComputeDateOnly(DateOnly[] values, int first, int count)
    {
        var min = values[first];
        var max = values[first];
        var distinct = new HashSet<DateOnly>();
        for (var i = 0; i < count; i++)
        {
            var v = values[first + i];
            if (v < min)
            {
                min = v;
            }

            if (v > max)
            {
                max = v;
            }

            distinct.Add(v);
        }
        return new ColumnRowGroupStats(
            MinValue: LittleEndianColumnBytes.From(min.DayNumber),
            MaxValue: LittleEndianColumnBytes.From(max.DayNumber),
            NullCount: 0,
            DistinctCount: distinct.Count);
    }

    private static ColumnRowGroupStats ComputeInt32(int[] values, int first, int count)
    {
        var min = values[first];
        var max = values[first];
        var distinct = new HashSet<int>();
        for (var i = 0; i < count; i++)
        {
            var v = values[first + i];
            if (v < min)
            {
                min = v;
            }

            if (v > max)
            {
                max = v;
            }

            distinct.Add(v);
        }
        return new ColumnRowGroupStats(LittleEndianColumnBytes.From(min), LittleEndianColumnBytes.From(max), 0, distinct.Count);
    }

    private static ColumnRowGroupStats ComputeInt64(long[] values, int first, int count)
    {
        var min = values[first];
        var max = values[first];
        var distinct = new HashSet<long>();
        for (var i = 0; i < count; i++)
        {
            var v = values[first + i];
            if (v < min)
            {
                min = v;
            }

            if (v > max)
            {
                max = v;
            }

            distinct.Add(v);
        }
        return new ColumnRowGroupStats(LittleEndianColumnBytes.From(min), LittleEndianColumnBytes.From(max), 0, distinct.Count);
    }

    private static ColumnRowGroupStats ComputeDouble(double[] values, int first, int count)
    {
        // NaN is unordered: v < x and v > x are both false for it, so a NaN never matches a range or
        // equality predicate and must be excluded from min/max. If NaN seeded the bound, the bound
        // would collapse to NaN (Comparer.Default ranks NaN below everything), making a downstream
        // zone-map skip falsely prove a group disjoint and drop matching finite rows. Compute the
        // bound over finite (and ±Infinity, which ARE ordered) values only; ±Inf participate normally.
        var distinct = new HashSet<double>();
        var hasBound = false;
        double min = 0, max = 0;
        for (var i = 0; i < count; i++)
        {
            var v = values[first + i];
            distinct.Add(v);
            if (double.IsNaN(v))
            {
                continue;
            }

            if (!hasBound)
            {
                min = max = v;
                hasBound = true;
            }
            else
            {
                if (v < min)
                {
                    min = v;
                }

                if (v > max)
                {
                    max = v;
                }
            }
        }

        // All-NaN (or empty) row group: no ordered bound exists. Emit empty stats so the predicate
        // pushdown treats the group as unskippable — conservative and correct, since NaN cells never
        // match a range/equality predicate anyway.
        return hasBound
            ? new ColumnRowGroupStats(LittleEndianColumnBytes.From(min), LittleEndianColumnBytes.From(max), 0, distinct.Count)
            : new ColumnRowGroupStats([], [], 0, distinct.Count);
    }

    private static ColumnRowGroupStats ComputeSingle(float[] values, int first, int count)
    {
        // See ComputeDouble: NaN is unordered and must not seed or pollute the min/max bound.
        var distinct = new HashSet<float>();
        var hasBound = false;
        float min = 0, max = 0;
        for (var i = 0; i < count; i++)
        {
            var v = values[first + i];
            distinct.Add(v);
            if (float.IsNaN(v))
            {
                continue;
            }

            if (!hasBound)
            {
                min = max = v;
                hasBound = true;
            }
            else
            {
                if (v < min)
                {
                    min = v;
                }

                if (v > max)
                {
                    max = v;
                }
            }
        }

        return hasBound
            ? new ColumnRowGroupStats(LittleEndianColumnBytes.From(min), LittleEndianColumnBytes.From(max), 0, distinct.Count)
            : new ColumnRowGroupStats([], [], 0, distinct.Count);
    }

    private static ColumnRowGroupStats ComputeDecimal(decimal[] values, int first, int count)
    {
        var min = values[first];
        var max = values[first];
        var distinct = new HashSet<decimal>();
        for (var i = 0; i < count; i++)
        {
            var v = values[first + i];
            if (v < min)
            {
                min = v;
            }

            if (v > max)
            {
                max = v;
            }

            distinct.Add(v);
        }
        return new ColumnRowGroupStats(EncodeDecimal(min), EncodeDecimal(max), 0, distinct.Count);
    }

    private static byte[] EncodeDecimal(decimal value)
    {
        // Canonical 16-byte little-endian of the four int32 components of decimal.GetBits.
        var bits = decimal.GetBits(value);
        var output = new byte[16];
        for (var i = 0; i < 4; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(i * 4, 4), bits[i]);
        }
        return output;
    }

    private static ColumnRowGroupStats ComputeBoolean(bool[] values, int first, int count)
    {
        var sawFalse = false;
        var sawTrue = false;
        for (var i = 0; i < count; i++)
        {
            if (values[first + i])
            {
                sawTrue = true;
            }
            else
            {
                sawFalse = true;
            }
        }
        var min = !sawFalse;
        var max = sawTrue;
        var distinct = (sawFalse && sawTrue) ? 2 : 1;
        return new ColumnRowGroupStats(
            [min ? (byte)1 : (byte)0],
            [max ? (byte)1 : (byte)0],
            0,
            distinct);
    }

    private static ColumnRowGroupStats ComputeString(string?[] values, int first, int count)
    {
        string? min = null;
        string? max = null;
        var nulls = 0;
        var distinct = new HashSet<string?>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            var v = values[first + i];
            distinct.Add(v);
            if (v is null)
            {
                nulls++;
                continue;
            }
            if (min is null || string.CompareOrdinal(v, min) < 0)
            {
                min = v;
            }

            if (max is null || string.CompareOrdinal(v, max) > 0)
            {
                max = v;
            }
        }
        var minBytes = min is null ? [] : TruncateUtf8(min, 256);
        var maxBytes = max is null ? [] : TruncateUtf8(max, 256);
        return new ColumnRowGroupStats(minBytes, maxBytes, nulls, distinct.Count);
    }

    private static byte[] TruncateUtf8(string value, int maxBytes)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length <= maxBytes)
        {
            return bytes;
        }

        // Walk back to a UTF-8 code-point boundary to avoid splitting a multi-byte sequence.
        var cutoff = maxBytes;
        while (cutoff > 0 && (bytes[cutoff] & 0xC0) == 0x80)
        {
            cutoff--;
        }
        var truncated = new byte[cutoff];
        Array.Copy(bytes, truncated, cutoff);
        return truncated;
    }
}
