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
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar.Footer;

namespace Boutquin.Storage.Infrastructure.TimeSeries.Columnar;

/// <summary>
/// Builds the canonical byte representation of a row group's values for Phase E3 per-row-group
/// CRC32C computation. The representation is deterministic, column-encoding-independent, and
/// content-addressed: two row groups with identical typed values produce identical bytes
/// regardless of the codec that compressed the column on disk.
/// </summary>
/// <remarks>
/// <para><b>Why a content-addressed form.</b> The v2.0.0 columnar writer lays out data column-
/// by-column; a row group does not occupy a contiguous byte range in the on-disk segment.
/// Computing a CRC over "the row group's payload bytes" (DDIA p. 122) would
/// require a row-group-major layout that the v2.0.0 writer doesn't produce. Instead, Phase E3
/// takes a content-addressed approach: for each row in the row group, in column order, emit
/// the canonical bytes of the value per <see cref="ColumnFieldTypeCode"/>. A mismatch post-
/// decode signals corruption somewhere between encode and the reader's typed output array.</para>
/// <para><b>Corruption surface covered.</b> Pre-decode byte corruption in a codec-compressed
/// column is caught by the codec's own invariants (<c>BitmapRleCodec</c>, etc., throw
/// <see cref="InvalidDataException"/> on impossible framings); the footer-level CRC catches
/// metadata corruption. Phase E3's per-row-group CRC catches post-decode corruption that
/// slipped past both — the smallest remaining surface area, but the one that matters for
/// quarantine-aware reads (DDIA 2e pp. 121–122: "checksums in the log and discarding
/// corrupted ... entries").</para>
/// </remarks>
internal static class RowGroupCanonicalBytes
{
    public static byte[] Build(
        IReadOnlyList<ColumnDescriptor> descriptors,
        Array[] typedColumnArrays,
        RowGroupDescriptor rowGroup)
    {
        using var ms = new MemoryStream();
        for (var c = 0; c < descriptors.Count; c++)
        {
            AppendColumn(ms, descriptors[c].FieldType, typedColumnArrays[c], rowGroup.FirstRowIndex, rowGroup.RowCount);
        }
        return ms.ToArray();
    }

    private static void AppendColumn(Stream output, ColumnFieldTypeCode fieldType, Array values, int first, int count)
    {
        switch (fieldType)
        {
            case ColumnFieldTypeCode.DateOnly:
                {
                    var arr = (DateOnly[])values;
                    Span<byte> buf = stackalloc byte[4];
                    for (var i = 0; i < count; i++)
                    {
                        BinaryPrimitives.WriteInt32LittleEndian(buf, arr[first + i].DayNumber);
                        output.Write(buf);
                    }
                    break;
                }
            case ColumnFieldTypeCode.Int32:
                {
                    var arr = (int[])values;
                    Span<byte> buf = stackalloc byte[4];
                    for (var i = 0; i < count; i++)
                    {
                        BinaryPrimitives.WriteInt32LittleEndian(buf, arr[first + i]);
                        output.Write(buf);
                    }
                    break;
                }
            case ColumnFieldTypeCode.Int64:
                {
                    var arr = (long[])values;
                    Span<byte> buf = stackalloc byte[8];
                    for (var i = 0; i < count; i++)
                    {
                        BinaryPrimitives.WriteInt64LittleEndian(buf, arr[first + i]);
                        output.Write(buf);
                    }
                    break;
                }
            case ColumnFieldTypeCode.Double:
                {
                    var arr = (double[])values;
                    Span<byte> buf = stackalloc byte[8];
                    for (var i = 0; i < count; i++)
                    {
                        BinaryPrimitives.WriteDoubleLittleEndian(buf, arr[first + i]);
                        output.Write(buf);
                    }
                    break;
                }
            case ColumnFieldTypeCode.Single:
                {
                    var arr = (float[])values;
                    Span<byte> buf = stackalloc byte[4];
                    for (var i = 0; i < count; i++)
                    {
                        BinaryPrimitives.WriteSingleLittleEndian(buf, arr[first + i]);
                        output.Write(buf);
                    }
                    break;
                }
            case ColumnFieldTypeCode.Boolean:
                {
                    var arr = (bool[])values;
                    for (var i = 0; i < count; i++)
                    {
                        output.WriteByte(arr[first + i] ? (byte)1 : (byte)0);
                    }
                    break;
                }
            case ColumnFieldTypeCode.Decimal:
                {
                    // Scale-normalize before hashing: ScaledIntegerBitPacked re-scales every
                    // value to the column's common (max) scale on decode, so 100.5m written at
                    // scale 1 decodes as the value-equal 100.50m at scale 2. decimal.GetBits is
                    // scale-sensitive — hashing raw bits would CRC-mismatch (false quarantine)
                    // on intact data. Normalizing on BOTH the write and read builds makes the
                    // CRC invariant to value-preserving re-scaling while still catching any
                    // value-changing corruption.
                    var arr = (decimal[])values;
                    Span<byte> buf = stackalloc byte[16];
                    for (var i = 0; i < count; i++)
                    {
                        var bits = decimal.GetBits(BitmapIndexComputer.NormalizeDecimalScale(arr[first + i]));
                        for (var b = 0; b < 4; b++)
                        {
                            BinaryPrimitives.WriteInt32LittleEndian(buf.Slice(b * 4, 4), bits[b]);
                        }
                        output.Write(buf);
                    }
                    break;
                }
            case ColumnFieldTypeCode.Enum:
                {
                    // Underlying value widened to int64, 8 bytes LE — matches the encode-side
                    // widening (bit transport for ulong underlyings).
                    var underlyingIsUInt64 = Enum.GetUnderlyingType(values.GetType().GetElementType()!) == typeof(ulong);
                    Span<byte> buf = stackalloc byte[8];
                    for (var i = 0; i < count; i++)
                    {
                        var boxed = values.GetValue(first + i)!;
                        var widened = underlyingIsUInt64
                            ? unchecked((long)Convert.ToUInt64(boxed, System.Globalization.CultureInfo.InvariantCulture))
                            : Convert.ToInt64(boxed, System.Globalization.CultureInfo.InvariantCulture);
                        BinaryPrimitives.WriteInt64LittleEndian(buf, widened);
                        output.Write(buf);
                    }
                    break;
                }
            case ColumnFieldTypeCode.String:
                {
                    var arr = (string?[])values;
                    Span<byte> lenBuf = stackalloc byte[4];
                    for (var i = 0; i < count; i++)
                    {
                        var v = arr[first + i];
                        if (v is null)
                        {
                            BinaryPrimitives.WriteInt32LittleEndian(lenBuf, -1);
                            output.Write(lenBuf);
                        }
                        else
                        {
                            var bytes = Encoding.UTF8.GetBytes(v);
                            BinaryPrimitives.WriteInt32LittleEndian(lenBuf, bytes.Length);
                            output.Write(lenBuf);
                            output.Write(bytes, 0, bytes.Length);
                        }
                    }
                    break;
                }
            default:
                throw new NotSupportedException(
                    $"Row-group canonical-bytes builder has no implementation for field type {fieldType}.");
        }
    }
}
