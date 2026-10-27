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
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar.Codecs;
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar.Footer;

namespace Boutquin.Storage.Infrastructure.TimeSeries.Columnar;

/// <summary>
/// Builds a cross-row-group <see cref="BitmapIndexEntry"/> for a single column marked with
/// <see cref="Boutquin.Storage.Domain.Attributes.BitmapIndexAttribute"/>. Produces
/// one <see cref="Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar.BitmapIndexValue"/>
/// per distinct observed value whose bitmap spans every row in the segment.
/// </summary>
/// <remarks>
/// <para><b>Distinct-value canonical bytes</b> match <see cref="ZoneMapComputer"/>'s min/max
/// encoding per <see cref="ColumnFieldTypeCode"/> for every fixed-width and string type — so a
/// query layer can reuse one byte representation for zone-map skip and bitmap-index lookup.
/// <b>Decimal is the one exception:</b> the bitmap path scale-normalizes each value via
/// <see cref="NormalizeDecimalScale"/> before <see cref="decimal.GetBits(decimal)"/> (so value-equal
/// decimals like <c>100.00m</c> and <c>100m</c> share one bucket and a differently-scaled
/// equality literal still finds its row groups), whereas <see cref="ZoneMapComputer"/> writes the
/// raw scale-preserving <see cref="decimal.GetBits(decimal)"/>. The two are therefore value-equal but
/// <i>not</i> byte-identical for decimals: the query-encode side normalizes to match this build
/// side, and the zone-map skip path decodes and compares by value (scale-agnostic) rather than by
/// byte-matching, so each path stays internally consistent.</para>
/// <para><b>Bitmap encoding.</b> Each per-value bitmap is encoded via
/// <see cref="BitmapRleCodec"/> — the same RLE substrate the columnar format uses for
/// bool columns. A bitmap has length equal to the whole-segment row count; bit <c>i</c> is
/// <see langword="true"/> iff the column's row-<c>i</c> value equals the distinct value.</para>
/// </remarks>
internal static class BitmapIndexComputer
{
    public static BitmapIndexEntry Compute(ColumnDescriptor descriptor, Array values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return descriptor.FieldType switch
        {
            ColumnFieldTypeCode.Int32 => Build<int>(descriptor.Name, (int[])values, LittleEndianColumnBytes.From),
            ColumnFieldTypeCode.Int64 => Build<long>(descriptor.Name, (long[])values, LittleEndianColumnBytes.From),
            ColumnFieldTypeCode.Double => BuildDouble(descriptor.Name, (double[])values),
            ColumnFieldTypeCode.Single => BuildSingle(descriptor.Name, (float[])values),
            ColumnFieldTypeCode.Boolean => Build<bool>(descriptor.Name, (bool[])values, b => [b ? (byte)1 : (byte)0]),
            ColumnFieldTypeCode.DateOnly => Build<DateOnly>(descriptor.Name, (DateOnly[])values, d => LittleEndianColumnBytes.From(d.DayNumber)),
            ColumnFieldTypeCode.Decimal => Build<decimal>(descriptor.Name, (decimal[])values, EncodeDecimal),
            ColumnFieldTypeCode.String => BuildString(descriptor.Name, (string?[])values),
            ColumnFieldTypeCode.Enum => BuildEnum(descriptor.Name, values),
            _ => throw new NotSupportedException(
                $"Bitmap-index computation has no implementation for field type {descriptor.FieldType}."),
        };
    }

    // Enum buckets are keyed by the underlying value widened to int64 (8 bytes LE) — the
    // same widening the zone-map bounds and the query side (ColumnarPredicatePushdown
    // EncodeValue) use, so an equality lookup byte-matches its bucket.
    private static BitmapIndexEntry BuildEnum(string columnName, Array values)
    {
        var underlyingIsUInt64 = Enum.GetUnderlyingType(values.GetType().GetElementType()!) == typeof(ulong);
        var total = values.Length;
        var widened = new long[total];
        for (var i = 0; i < total; i++)
        {
            var boxed = values.GetValue(i)!;
            widened[i] = underlyingIsUInt64
                ? unchecked((long)Convert.ToUInt64(boxed, System.Globalization.CultureInfo.InvariantCulture))
                : Convert.ToInt64(boxed, System.Globalization.CultureInfo.InvariantCulture);
        }

        return Build<long>(columnName, widened, LittleEndianColumnBytes.From);
    }

    private static BitmapIndexEntry Build<T>(string columnName, T[] values, Func<T, byte[]> encoder)
        where T : notnull
    {
        var total = values.Length;
        var distinct = new Dictionary<T, List<int>>();
        for (var i = 0; i < total; i++)
        {
            var v = values[i];
            if (!distinct.TryGetValue(v, out var bitmap))
            {
                bitmap = [];
                distinct[v] = bitmap;
            }
            bitmap.Add(i);
        }

        var entries = new List<BitmapIndexValue>(distinct.Count);
        foreach (var (value, bitmap) in distinct)
        {
            entries.Add(new BitmapIndexValue(encoder(value), EncodeRowPositions(bitmap, total)));
        }

        // Order by value bytes for deterministic footer output.
        entries.Sort((a, b) => a.Value.AsSpan().SequenceCompareTo(b.Value));
        return new BitmapIndexEntry(columnName, entries);
    }

    // Floating-point columns must bucket by raw bit pattern, not by value equality (finding #13):
    // default equality makes NaN != NaN, so each NaN row would spawn its own single-bit bitmap
    // (index bloat). Every NaN is canonicalised to one bit pattern — and ±0.0 are ALSO collapsed
    // to +0.0: the row-level filter compares via Comparer<T>.Default, for which -0.0 == +0.0, so
    // keeping the zeros as distinct buckets lets the equality skip declare "value absent" for a
    // segment whose every row matches — a silent false drop. The pushdown layer's query-literal
    // encoder applies the same canonicalisation, so bucket keys and query keys agree.
    private static BitmapIndexEntry BuildDouble(string columnName, double[] values)
    {
        var total = values.Length;
        var distinct = new Dictionary<long, List<int>>();
        for (var i = 0; i < total; i++)
        {
            var bits = double.IsNaN(values[i])
                ? BitConverter.DoubleToInt64Bits(double.NaN)
                : BitConverter.DoubleToInt64Bits(values[i] == 0.0 ? 0.0 : values[i]);
            if (!distinct.TryGetValue(bits, out var bitmap))
            {
                bitmap = [];
                distinct[bits] = bitmap;
            }
            bitmap.Add(i);
        }

        var entries = new List<BitmapIndexValue>(distinct.Count);
        foreach (var (bits, bitmap) in distinct)
        {
            var bytes = LittleEndianColumnBytes.From(BitConverter.Int64BitsToDouble(bits));
            entries.Add(new BitmapIndexValue(bytes, EncodeRowPositions(bitmap, total)));
        }
        entries.Sort((a, b) => a.Value.AsSpan().SequenceCompareTo(b.Value));
        return new BitmapIndexEntry(columnName, entries);
    }

    private static BitmapIndexEntry BuildSingle(string columnName, float[] values)
    {
        var total = values.Length;
        var distinct = new Dictionary<int, List<int>>();
        for (var i = 0; i < total; i++)
        {
            var bits = float.IsNaN(values[i])
                ? BitConverter.SingleToInt32Bits(float.NaN)
                : BitConverter.SingleToInt32Bits(values[i] == 0.0f ? 0.0f : values[i]);
            if (!distinct.TryGetValue(bits, out var bitmap))
            {
                bitmap = [];
                distinct[bits] = bitmap;
            }
            bitmap.Add(i);
        }

        var entries = new List<BitmapIndexValue>(distinct.Count);
        foreach (var (bits, bitmap) in distinct)
        {
            var bytes = LittleEndianColumnBytes.From(BitConverter.Int32BitsToSingle(bits));
            entries.Add(new BitmapIndexValue(bytes, EncodeRowPositions(bitmap, total)));
        }
        entries.Sort((a, b) => a.Value.AsSpan().SequenceCompareTo(b.Value));
        return new BitmapIndexEntry(columnName, entries);
    }

    private static BitmapIndexEntry BuildString(string columnName, string?[] values)
    {
        var total = values.Length;
        var distinct = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i < total; i++)
        {
            var v = values[i];
            if (v is null)
            {
                // Null strings never reach here: the columnar writer rejects them at write time
                // (CompressedColumnarSegmentFormat.BuildTypedValues sets rejectNulls = true for
                // reference-typed columns), and [Key] string columns are rejected at compile time
                // (BSSG019). A prior implementation encoded null as an empty byte array, but that
                // sentinel was byte-identical to the encoding of a non-null empty string ("" →
                // Encoding.UTF8.GetBytes = []), so the two distinct logical buckets collided on one
                // key — a silent-wrong-skip waiting on any future change that let a null through.
                // Under the writer's no-null string contract the branch is dead, so we fail fast
                // here instead of materialising the colliding sentinel: if a null ever reaches this
                // builder it is a contract violation that must surface, not encode ambiguously.
                throw new InvalidOperationException(
                    $"Bitmap index for string column '{columnName}' received a null value at row {i}. "
                    + "Columnar string columns are guaranteed non-null by the writer's null-rejection "
                    + "guard; a null here indicates that contract was bypassed. Coerce nulls to a "
                    + "non-null sentinel at the writer boundary before indexing.");
            }
            if (!distinct.TryGetValue(v, out var bitmap))
            {
                bitmap = [];
                distinct[v] = bitmap;
            }
            bitmap.Add(i);
        }

        var entries = new List<BitmapIndexValue>(distinct.Count);
        foreach (var (value, bitmap) in distinct)
        {
            entries.Add(new BitmapIndexValue(Encoding.UTF8.GetBytes(value), EncodeRowPositions(bitmap, total)));
        }

        entries.Sort((a, b) => a.Value.AsSpan().SequenceCompareTo(b.Value));
        return new BitmapIndexEntry(columnName, entries);
    }

    // Each distinct value's row positions are collected in ascending order and RLE-encoded
    // directly, producing the same bytes as BitmapRleCodec.Encode over that value's whole-segment
    // bool column — but without materialising one bool[segmentRows] per distinct value, which made
    // transient build memory distinct × rows (1 GB for 5,000 tags over 200k rows). The position
    // lists together hold exactly one int per row, so build memory is O(rows).
    private static byte[] EncodeRowPositions(List<int> positions, int total)
    {
        using var output = new MemoryStream();
        output.WriteByte(positions[0] == 0 ? (byte)1 : (byte)0);

        var end = 0; // exclusive end of the last run written
        var i = 0;
        while (i < positions.Count)
        {
            var runStart = positions[i];
            if (runStart > end)
            {
                Vlq.WriteUInt64(output, (ulong)(runStart - end));
            }

            var j = i + 1;
            while (j < positions.Count && positions[j] == positions[j - 1] + 1)
            {
                j++;
            }

            end = positions[j - 1] + 1;
            Vlq.WriteUInt64(output, (ulong)(end - runStart));
            i = j;
        }

        if (end < total)
        {
            Vlq.WriteUInt64(output, (ulong)(total - end));
        }

        return output.ToArray();
    }

    private static byte[] EncodeDecimal(decimal value)
    {
        var bits = decimal.GetBits(NormalizeDecimalScale(value));
        var output = new byte[16];
        for (var i = 0; i < 4; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(i * 4, 4), bits[i]);
        }
        return output;
    }

    /// <summary>
    /// Strips trailing-zero scale so value-equal decimals (100.00m and 100m) share one canonical
    /// representation. <see cref="decimal.GetBits(decimal)"/> encodes the scale, so a bitmap bucket keyed on
    /// the raw bits would distinguish 100.00m from 100m — and an equality query whose literal has a
    /// different scale than the stored value would find no matching bucket and skip a row group that
    /// actually matches. Data skipping must be a conservative superset (DDIA 2e pp. 136–143 —
    /// blocks are skipped only when they provably cannot overlap the query). Used on
    /// both the index-build side here and the query-encode side (ColumnarPredicatePushdown) so the
    /// two agree. Zero is also stripped of its sign bit: <c>decimal.Negate(0m)</c>, <c>0m / -3m</c> and
    /// <c>Math.Round(-0.4m)</c> all yield a negative zero that <see cref="decimal.GetBits(decimal)"/>
    /// distinguishes but the row filter's <c>Comparer&lt;decimal&gt;.Default</c> treats as equal to
    /// <c>0m</c> — the same ±0 identity the double/float bitmap paths canonicalise.
    /// </summary>
    internal static decimal NormalizeDecimalScale(decimal value)
    {
        if (value == 0m)
        {
            return 0m;
        }

        var scale = (decimal.GetBits(value)[3] >> 16) & 0xFF;
        while (scale > 0 && value == Math.Round(value, scale - 1))
        {
            scale--;
        }

        return Math.Round(value, scale);
    }
}
