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

namespace Boutquin.Storage.Infrastructure.TimeSeries.Columnar.Footer;

/// <summary>
/// Serialises a <see cref="SegmentFooter"/> to a <see cref="Stream"/> in the Boutquin
/// columnar on-disk layout, followed by its CRC32C (Castagnoli) and the 8-byte trailer.
/// </summary>
/// <remarks>
/// <para><b>Layout (written sequentially to the stream):</b></para>
/// <list type="number">
/// <item>Footer payload (length = <c>footerLength</c>):
///   <list type="bullet">
///   <item>4 B — footer magic (<see cref="SegmentMagic.AsUInt32"/>)</item>
///   <item>4 B — format version (uint32 LE)</item>
///   <item>2 B — column count (uint16 LE)</item>
///   <item>Per column, in order: name length (uint16 LE), UTF-8 name bytes, encoding tag (byte),
///     codec version (byte), field type code (byte), absolute data offset (int64 LE), data length (int64 LE)</item>
///   <item>2 B — row group count (uint16 LE)</item>
///   <item>Per row group, in order: min date day-number (int32 LE), max date day-number (int32 LE),
///     row count (int32 LE), first row index (int32 LE)</item>
///   <item>1 B — extension section count (uint8). Zero means no extensions.</item>
///   <item>Per extension: <c>[kind: 1B][length: uint32 LE][payload: length bytes]</c>. Unknown
///     kinds are skipped via the length prefix. Phase E defines:
///     <see cref="FooterExtensionKind.ZoneMaps"/>,
///     <see cref="FooterExtensionKind.BitmapIndexes"/>,
///     <see cref="FooterExtensionKind.RowGroupCrcs"/>.</item>
///   </list>
/// </item>
/// <item>4 B — CRC32C (Castagnoli <c>0x1EDC6F41</c>) over the footer payload (including extensions).</item>
/// <item>8 B — trailer: footer length (uint32 LE) + magic (<see cref="SegmentMagic.AsUInt32"/> LE).</item>
/// </list>
/// <para>All integers are little-endian. The CRC covers <b>only</b> the footer payload — not
/// the CRC itself and not the trailer. On read, the trailer's magic is validated before the
/// <c>footerLength</c> field is trusted.</para>
/// </remarks>
internal static class FooterWriter
{
    /// <summary>
    /// Largest bitmap-index key, in UTF-8 bytes, the footer can carry: the key-length field is a
    /// uint16. The columnar write admission refuses a longer <c>[BitmapIndex]</c> value up front.
    /// </summary>
    internal const int MaxBitmapKeyBytes = ushort.MaxValue;

    /// <summary>
    /// Most row groups one footer can describe: the row-group count fields are uint16. The writer's
    /// row-group planner widens its groups so no block it plans exceeds this.
    /// </summary>
    internal const int MaxRowGroups = ushort.MaxValue;

    public static void Write(Stream output, SegmentFooter footer)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(footer);

        using var payload = new MemoryStream();
        WritePayload(payload, footer);

        var payloadBytes = payload.ToArray();
        var payloadLength = payloadBytes.Length;

        // Footer payload
        output.Write(payloadBytes, 0, payloadLength);

        // CRC32C over the payload
        var crc = Crc32C.HashToUInt32(payloadBytes);
        Span<byte> crcBuf = stackalloc byte[SegmentMagic.FooterCrcSize];
        BinaryPrimitives.WriteUInt32LittleEndian(crcBuf, crc);
        output.Write(crcBuf);

        // 8-byte trailer: (footerLength, magicTrailer)
        Span<byte> trailerBuf = stackalloc byte[SegmentMagic.TrailerSize];
        BinaryPrimitives.WriteUInt32LittleEndian(trailerBuf[..4], checked((uint)payloadLength));
        BinaryPrimitives.WriteUInt32LittleEndian(trailerBuf[4..], SegmentMagic.AsUInt32);
        output.Write(trailerBuf);
    }

    private static void WritePayload(Stream payload, SegmentFooter footer)
    {
        Span<byte> buf8 = stackalloc byte[8];

        // Footer magic
        BinaryPrimitives.WriteUInt32LittleEndian(buf8[..4], SegmentMagic.AsUInt32);
        BinaryPrimitives.WriteUInt32LittleEndian(buf8[4..], footer.FormatVersion);
        payload.Write(buf8);

        // Column count
        Span<byte> buf2 = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(buf2, checked((ushort)footer.Columns.Count));
        payload.Write(buf2);

        foreach (var column in footer.Columns)
        {
            var nameBytes = Encoding.UTF8.GetBytes(column.Name);
            BinaryPrimitives.WriteUInt16LittleEndian(buf2, checked((ushort)nameBytes.Length));
            payload.Write(buf2);
            payload.Write(nameBytes, 0, nameBytes.Length);
            payload.WriteByte((byte)column.EncodingTag);
            payload.WriteByte(column.CodecVersion);
            payload.WriteByte((byte)column.FieldType);
            BinaryPrimitives.WriteInt64LittleEndian(buf8, column.ByteOffset);
            payload.Write(buf8);
            BinaryPrimitives.WriteInt64LittleEndian(buf8, column.ByteLength);
            payload.Write(buf8);
        }

        // Row group count
        BinaryPrimitives.WriteUInt16LittleEndian(buf2, checked((ushort)footer.RowGroups.Count));
        payload.Write(buf2);

        Span<byte> buf4 = stackalloc byte[4];
        foreach (var rg in footer.RowGroups)
        {
            BinaryPrimitives.WriteInt32LittleEndian(buf4, rg.MinDate.DayNumber);
            payload.Write(buf4);
            BinaryPrimitives.WriteInt32LittleEndian(buf4, rg.MaxDate.DayNumber);
            payload.Write(buf4);
            BinaryPrimitives.WriteInt32LittleEndian(buf4, rg.RowCount);
            payload.Write(buf4);
            BinaryPrimitives.WriteInt32LittleEndian(buf4, rg.FirstRowIndex);
            payload.Write(buf4);
        }

        // Extension sections (Phase E additive layout).
        WriteExtensions(payload, footer);
    }

    // ───── Extension sections ────────────────────────────────────────

    private static void WriteExtensions(Stream payload, SegmentFooter footer)
    {
        // Gather extension sections that will actually be emitted.
        var sections = new List<(FooterExtensionKind Kind, byte[] Payload)>(3);

        if (footer.ZoneMaps is { Count: > 0 })
        {
            sections.Add((FooterExtensionKind.ZoneMaps, EncodeZoneMaps(footer.ZoneMaps, footer.Columns.Count)));
        }

        if (footer.BitmapIndexes is { Count: > 0 })
        {
            sections.Add((FooterExtensionKind.BitmapIndexes, EncodeBitmapIndexes(footer.BitmapIndexes)));
        }

        if (footer.RowGroupCrcs is { Count: > 0 })
        {
            sections.Add((FooterExtensionKind.RowGroupCrcs, EncodeRowGroupCrcs(footer.RowGroupCrcs)));
        }

        if (footer.RowGroupByteOffsets is { Count: > 0 })
        {
            sections.Add((FooterExtensionKind.RowGroupByteOffsets, EncodeRowGroupByteOffsets(footer.RowGroupByteOffsets)));
        }

        // Extension count (1 byte).
        payload.WriteByte(checked((byte)sections.Count));

        Span<byte> buf4 = stackalloc byte[4];
        foreach (var (kind, data) in sections)
        {
            payload.WriteByte((byte)kind);
            BinaryPrimitives.WriteUInt32LittleEndian(buf4, checked((uint)data.Length));
            payload.Write(buf4);
            payload.Write(data, 0, data.Length);
        }
    }

    private static byte[] EncodeZoneMaps(
        IReadOnlyList<IReadOnlyList<ColumnRowGroupStats>> zoneMaps,
        int expectedColumnCount)
    {
        using var ms = new MemoryStream();
        Span<byte> buf2 = stackalloc byte[2];
        Span<byte> buf4 = stackalloc byte[4];

        BinaryPrimitives.WriteUInt16LittleEndian(buf2, checked((ushort)zoneMaps.Count));
        ms.Write(buf2);
        BinaryPrimitives.WriteUInt16LittleEndian(buf2, checked((ushort)expectedColumnCount));
        ms.Write(buf2);

        foreach (var perColumn in zoneMaps)
        {
            if (perColumn.Count != expectedColumnCount)
            {
                throw new InvalidOperationException(
                    $"ZoneMaps row-group entry has {perColumn.Count} columns; footer declares {expectedColumnCount}.");
            }

            foreach (var stats in perColumn)
            {
                ArgumentNullException.ThrowIfNull(stats);
                BinaryPrimitives.WriteUInt16LittleEndian(buf2, checked((ushort)stats.MinValue.Length));
                ms.Write(buf2);
                ms.Write(stats.MinValue, 0, stats.MinValue.Length);
                BinaryPrimitives.WriteUInt16LittleEndian(buf2, checked((ushort)stats.MaxValue.Length));
                ms.Write(buf2);
                ms.Write(stats.MaxValue, 0, stats.MaxValue.Length);
                BinaryPrimitives.WriteInt32LittleEndian(buf4, stats.NullCount);
                ms.Write(buf4);
                BinaryPrimitives.WriteInt32LittleEndian(buf4, stats.DistinctCount);
                ms.Write(buf4);
            }
        }

        return ms.ToArray();
    }

    private static byte[] EncodeBitmapIndexes(IReadOnlyList<BitmapIndexEntry> entries)
    {
        using var ms = new MemoryStream();
        Span<byte> buf2 = stackalloc byte[2];
        Span<byte> buf4 = stackalloc byte[4];

        BinaryPrimitives.WriteUInt16LittleEndian(buf2, checked((ushort)entries.Count));
        ms.Write(buf2);

        foreach (var entry in entries)
        {
            var nameBytes = Encoding.UTF8.GetBytes(entry.ColumnName);
            BinaryPrimitives.WriteUInt16LittleEndian(buf2, checked((ushort)nameBytes.Length));
            ms.Write(buf2);
            ms.Write(nameBytes, 0, nameBytes.Length);

            BinaryPrimitives.WriteUInt32LittleEndian(buf4, checked((uint)entry.ValueToBitmap.Count));
            ms.Write(buf4);

            foreach (var value in entry.ValueToBitmap)
            {
                // Bitmap keys are equality keys and are deliberately not truncated (zone-map
                // string bounds are — they only need to be conservative). The wire's key-length
                // field is a ushort, so an over-wide key is a caller-data limit that must fail
                // with a clear typed message, not a raw OverflowException from checked((ushort)…).
                if (value.Value.Length > MaxBitmapKeyBytes)
                {
                    throw new InvalidOperationException(
                        $"Bitmap index key for column '{entry.ColumnName}' is {value.Value.Length} bytes; "
                        + "the footer's key-length field caps keys at 65,535 UTF-8 bytes. Remove the "
                        + "[BitmapIndex] annotation from this column or shorten its values.");
                }

                BinaryPrimitives.WriteUInt16LittleEndian(buf2, (ushort)value.Value.Length);
                ms.Write(buf2);
                ms.Write(value.Value, 0, value.Value.Length);
                BinaryPrimitives.WriteUInt32LittleEndian(buf4, checked((uint)value.Bitmap.Length));
                ms.Write(buf4);
                ms.Write(value.Bitmap, 0, value.Bitmap.Length);
            }
        }

        return ms.ToArray();
    }

    private static byte[] EncodeRowGroupCrcs(IReadOnlyList<uint> crcs)
    {
        var output = new byte[2 + (4 * crcs.Count)];
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(0, 2), checked((ushort)crcs.Count));
        for (var i = 0; i < crcs.Count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(2 + (i * 4), 4), crcs[i]);
        }
        return output;
    }

    /// <summary>
    /// Layout: [columnCount: uint16 LE] then per column:
    /// [nameLen: uint16 LE][name: UTF-8][rgCount: uint16 LE][offset0: int32 LE]...
    /// Offsets are byte positions within the column blob; the slice for row group i runs from
    /// offset[i] to offset[i+1] (or blob end for the last group).
    /// </summary>
    private static byte[] EncodeRowGroupByteOffsets(IReadOnlyDictionary<string, IReadOnlyList<int>> byteOffsets)
    {
        using var ms = new MemoryStream();
        Span<byte> buf2 = stackalloc byte[2];
        Span<byte> buf4 = stackalloc byte[4];

        BinaryPrimitives.WriteUInt16LittleEndian(buf2, checked((ushort)byteOffsets.Count));
        ms.Write(buf2);

        foreach (var (name, offsets) in byteOffsets)
        {
            var nameBytes = Encoding.UTF8.GetBytes(name);
            BinaryPrimitives.WriteUInt16LittleEndian(buf2, checked((ushort)nameBytes.Length));
            ms.Write(buf2);
            ms.Write(nameBytes, 0, nameBytes.Length);

            BinaryPrimitives.WriteUInt16LittleEndian(buf2, checked((ushort)offsets.Count));
            ms.Write(buf2);

            foreach (var offset in offsets)
            {
                BinaryPrimitives.WriteInt32LittleEndian(buf4, offset);
                ms.Write(buf4);
            }
        }

        return ms.ToArray();
    }
}
