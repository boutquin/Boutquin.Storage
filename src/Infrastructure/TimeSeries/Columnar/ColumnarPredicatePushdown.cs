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
using Boutquin.Storage.Domain.Helpers;
using Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar;

namespace Boutquin.Storage.Infrastructure.TimeSeries.Columnar;

/// <summary>
/// Zone-map and bitmap-index evaluation for value-predicate pushdown (DDIA 2e Ch. 4 — data
/// skipping). Decodes the canonical on-disk min/max bytes a <see cref="ZoneMapComputer"/> wrote and
/// decides whether a row group is provably empty of matches for a given predicate, and encodes a
/// predicate value to the canonical bytes a bitmap-index key uses. All decisions are conservative:
/// the method returns <c>true</c> (skip) only when the row group cannot contain a match, so a
/// row-level filter over the surviving rows still yields exact results.
/// </summary>
internal static class ColumnarPredicatePushdown
{
    /// <summary>
    /// Decides whether a row group can be skipped for <paramref name="op"/>/<paramref name="value"/>
    /// using only the row group's zone-map statistics. String columns are never skipped here
    /// (their min/max may be UTF-8-truncated, which would make a range skip unsafe); equality on a
    /// string column is handled by the bitmap path instead.
    /// </summary>
    public static bool CanSkipByZoneMap(
        ColumnRowGroupStats stats,
        ComparisonOperator op,
        object value,
        ColumnFieldTypeCode fieldType)
    {
        if (fieldType == ColumnFieldTypeCode.String)
        {
            return false; // truncated min/max — defer to the bitmap path and the row-level filter.
        }

        var min = DecodeBound(stats.MinValue, fieldType);
        var max = DecodeBound(stats.MaxValue, fieldType);
        if (min is null || max is null)
        {
            return false; // empty stats (all-null / no information) — cannot skip.
        }

        // Enum bounds decode as widened int64; widen the predicate's enum value the same way
        // so the comparison below runs long-vs-long. (Same widening as the encode side —
        // bit transport for ulong underlyings.)
        if (fieldType == ColumnFieldTypeCode.Enum && value is Enum)
        {
            // ulong underlyings are range-unskippable: a member above long.MaxValue widens to
            // a NEGATIVE int64, so the signed order of the widened bounds diverges from the
            // unsigned Enum.CompareTo order the row-level filter applies — a range skip across
            // that divergence silently drops matching rows. Equality stays skippable (the
            // widened byte key is sign-agnostic); range operators defer to the row filter.
            if (op is not (ComparisonOperator.Equal or ComparisonOperator.NotEqual)
                && Enum.GetUnderlyingType(value.GetType()) == typeof(ulong))
            {
                return false;
            }

            value = WidenEnum(value);
        }

        // Fail closed on a predicate value whose CLR type does not match the column's decoded
        // bound type (e.g. a long literal against an Int32 column): the boxed comparison below
        // would throw ArgumentException mid-read. "Cannot prove disjoint" ⇒ no skip — the same
        // conservative contract the bitmap path applies when EncodeValue returns null.
        if (value.GetType() != min.GetType())
        {
            return false;
        }

        // cmpMin = sign of (min - value); cmpMax = sign of (max - value).
        var cmpMin = Comparer.Default.Compare(min, value);
        var cmpMax = Comparer.Default.Compare(max, value);

        return op switch
        {
            // value outside [min, max] ⇒ no cell can equal it.
            ComparisonOperator.Equal => cmpMin > 0 || cmpMax < 0,
            // matches need a cell != value; only a constant, null-free row group equal to value
            // has none — a null cell satisfies NotEqual (ColumnPredicate.Matches: null vs
            // non-null is a match), so any null in the group forbids the skip.
            ComparisonOperator.NotEqual => cmpMin == 0 && cmpMax == 0 && stats.NullCount == 0,
            // cell < value has a match iff min < value.
            ComparisonOperator.LessThan => cmpMin >= 0,
            // cell <= value has a match iff min <= value.
            ComparisonOperator.LessThanOrEqual => cmpMin > 0,
            // cell > value has a match iff max > value.
            ComparisonOperator.GreaterThan => cmpMax <= 0,
            // cell >= value has a match iff max >= value.
            ComparisonOperator.GreaterThanOrEqual => cmpMax < 0,
            _ => false,
        };
    }

    /// <summary>
    /// Decodes a canonical on-disk min/max bound to its CLR value, or <see langword="null"/> when
    /// the bound is empty (no observed non-null values). Mirrors <see cref="ZoneMapComputer"/>'s
    /// encoding.
    /// </summary>
    public static object? DecodeBound(byte[] bytes, ColumnFieldTypeCode fieldType)
    {
        if (bytes.Length == 0)
        {
            return null;
        }

        // A non-empty bound shorter than the field type's fixed width is forged-footer corruption:
        // FooterReader.DecodeZoneMaps bounds the bound's byte presence with EnsureRemaining but does
        // NOT enforce the field-type width, so a recomputed-valid-CRC footer (the established threat
        // model) can carry e.g. a 2-byte Int32 MinValue. DecodeBound is reached from
        // CanSkipByZoneMap on the predicate-pushdown read path, which runs OUTSIDE the per-column
        // decode try/catch — a raw BinaryPrimitives ArgumentOutOfRangeException would escape the
        // public query API uncaught. Fail with the typed corruption exception instead, upholding the
        // same crafted-input-produces-typed-exception contract FooterReader.EnsureRemaining states.
        EnsureBoundWidth(bytes.Length, fieldType);

        return fieldType switch
        {
            ColumnFieldTypeCode.DateOnly => DecodeDateOnlyBound(bytes),
            ColumnFieldTypeCode.Int32 => BinaryPrimitives.ReadInt32LittleEndian(bytes),
            ColumnFieldTypeCode.Int64 => BinaryPrimitives.ReadInt64LittleEndian(bytes),
            ColumnFieldTypeCode.Double => BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(bytes)),
            ColumnFieldTypeCode.Single => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes)),
            ColumnFieldTypeCode.Decimal => DecodeDecimal(bytes),
            ColumnFieldTypeCode.Boolean => bytes[0] != 0,
            ColumnFieldTypeCode.String => Encoding.UTF8.GetString(bytes),
            ColumnFieldTypeCode.Enum => BinaryPrimitives.ReadInt64LittleEndian(bytes),
            _ => null,
        };
    }

    /// <summary>
    /// Encodes a predicate value to the canonical on-disk bytes a bitmap-index key uses (the same
    /// representation <see cref="ZoneMapComputer"/> writes for min/max). Returns
    /// <see langword="null"/> for an unsupported value/type pairing.
    /// </summary>
    public static byte[]? EncodeValue(object value, ColumnFieldTypeCode fieldType)
    {
        try
        {
            return fieldType switch
            {
                ColumnFieldTypeCode.DateOnly when value is DateOnly d => LittleEndianColumnBytes.From(d.DayNumber),
                ColumnFieldTypeCode.Int32 when value is int i => LittleEndianColumnBytes.From(i),
                ColumnFieldTypeCode.Int64 when value is long l => LittleEndianColumnBytes.From(l),
                // ±0.0 canonicalised to +0.0, mirroring BitmapIndexComputer's bucketing: the row
                // filter's Comparer<T>.Default treats the zeros as equal, so query key and bucket
                // key must land on one representation or an equality skip false-drops zero rows.
                ColumnFieldTypeCode.Double when value is double dd => LittleEndianColumnBytes.From(dd == 0.0 ? 0.0 : dd),
                ColumnFieldTypeCode.Single when value is float f => LittleEndianColumnBytes.From(f == 0.0f ? 0.0f : f),
                ColumnFieldTypeCode.Decimal when value is decimal m => EncodeDecimal(m),
                ColumnFieldTypeCode.Boolean when value is bool b => [b ? (byte)1 : (byte)0],
                ColumnFieldTypeCode.String when value is string s => Encoding.UTF8.GetBytes(s),
                ColumnFieldTypeCode.Enum when value is Enum => LittleEndianColumnBytes.From(WidenEnum(value)),
                _ => null,
            };
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    /// <summary>
    /// Decodes a width-validated DateOnly zone-map bound, RANGE-guarding the decoded day number
    /// before <see cref="DateOnly.FromDayNumber(int)"/>. <see cref="EnsureBoundWidth"/> guarantees
    /// the 4-byte width (F-U2-01) but not the day-number range — a forged 4-byte <c>0x7FFFFFFF</c>
    /// would otherwise leak a raw <see cref="ArgumentOutOfRangeException"/> out of the predicate-
    /// pushdown read path. Throw the typed <see cref="CorruptSegmentException"/> instead.
    /// </summary>
    private static DateOnly DecodeDateOnlyBound(byte[] bytes)
    {
        var dayNumber = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        if (!CorruptionGuards.IsValidDayNumber(dayNumber))
        {
            throw new CorruptSegmentException(
                $"Zone-map DateOnly bound carries day-number {dayNumber} outside the DateOnly range "
                + $"[{DateOnly.MinValue.DayNumber}, {DateOnly.MaxValue.DayNumber}].",
                offset: 0);
        }

        return DateOnly.FromDayNumber(dayNumber);
    }

    /// <summary>
    /// Validates that a non-empty zone-map bound carries at least the fixed number of bytes the
    /// fixed-width decode for <paramref name="fieldType"/> reads, throwing
    /// <see cref="CorruptSegmentException"/> on a too-short (forged-footer) bound rather than letting
    /// the fixed-width reader throw an untyped <see cref="ArgumentOutOfRangeException"/>. Variable-
    /// width arms (Boolean reads one byte — already guaranteed non-empty by the caller; String
    /// consumes the whole array) impose no further width requirement.
    /// </summary>
    private static void EnsureBoundWidth(int length, ColumnFieldTypeCode fieldType)
    {
        var required = fieldType switch
        {
            ColumnFieldTypeCode.DateOnly => 4,
            ColumnFieldTypeCode.Int32 => 4,
            ColumnFieldTypeCode.Int64 => 8,
            ColumnFieldTypeCode.Double => 8,
            ColumnFieldTypeCode.Single => 4,
            ColumnFieldTypeCode.Decimal => 16,
            ColumnFieldTypeCode.Enum => 8,
            _ => 0,
        };

        if (length < required)
        {
            throw new CorruptSegmentException(
                $"Zone-map bound for column field type {fieldType} is truncated: needed {required} "
                + $"bytes, found {length}.",
                offset: 0);
        }
    }

    private static long WidenEnum(object boxed) =>
        Enum.GetUnderlyingType(boxed.GetType()) == typeof(ulong)
            ? unchecked((long)Convert.ToUInt64(boxed, System.Globalization.CultureInfo.InvariantCulture))
            : Convert.ToInt64(boxed, System.Globalization.CultureInfo.InvariantCulture);

    private static decimal DecodeDecimal(byte[] bytes)
    {
        Span<int> bits = stackalloc int[4];
        for (var i = 0; i < 4; i++)
        {
            bits[i] = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(i * 4, 4));
        }

        // EnsureBoundWidth guarantees the 16-byte width but not the flags-word validity: a forged-
        // but-CRC-valid footer carrying a reserved bit or scale > 28 would otherwise leak a raw
        // ArgumentException out of the pushdown read path. Range-guard the flags word and throw the
        // typed CorruptSegmentException instead, matching DecodeDateOnlyBound's convention.
        if (!CorruptionGuards.IsValidDecimalBits(bits[3]))
        {
            throw new CorruptSegmentException(
                $"Zone-map decimal bound carries flags word 0x{bits[3]:X8} that sets a reserved bit "
                + "or a scale > 28.",
                offset: 0);
        }

        return new decimal(bits);
    }

    private static byte[] EncodeDecimal(decimal value)
    {
        // Normalize scale so a query literal (100m) buckets identically to a stored value of a
        // different scale (100.00m) — symmetric with BitmapIndexComputer's index-build encoding.
        // Otherwise the bitmap skip drops a matching row group (DDIA 2e pp. 136–143 — data
        // skipping must be conservative).
        var bits = decimal.GetBits(BitmapIndexComputer.NormalizeDecimalScale(value));
        var output = new byte[16];
        for (var i = 0; i < 4; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(i * 4, 4), bits[i]);
        }
        return output;
    }
}
