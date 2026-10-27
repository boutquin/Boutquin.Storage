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

namespace Boutquin.Storage.Infrastructure.TimeSeries.Columnar.Footer;

/// <summary>
/// Reads and validates a <see cref="SegmentFooter"/> from a seekable <see cref="Stream"/>.
/// Validates trailer magic first, then format version, then CRC. Partial reads are refused.
/// </summary>
internal static class FooterReader
{
    /// <summary>
    /// Locates, validates, and deserialises the footer of a columnar segment.
    /// </summary>
    /// <param name="input">A readable, seekable stream. Stream position is not restored on exit.</param>
    /// <returns>The decoded <see cref="SegmentFooter"/>.</returns>
    /// <exception cref="ArgumentException">Stream is not seekable or not readable.</exception>
    /// <exception cref="TruncatedSegmentException">Stream is shorter than the minimum segment overhead.</exception>
    /// <exception cref="InvalidSegmentException">Trailer magic does not match Boutquin's.</exception>
    /// <exception cref="UnsupportedFormatVersionException">Declared format version exceeds this reader's support.</exception>
    /// <exception cref="CorruptSegmentException">Footer CRC32C mismatch.</exception>
    public static SegmentFooter Read(Stream input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.CanSeek || !input.CanRead)
        {
            throw new ArgumentException("Stream must be readable and seekable.", nameof(input));
        }

        var length = input.Length;
        if (length < SegmentMagic.HeaderSize + SegmentMagic.FooterCrcSize + SegmentMagic.TrailerSize)
        {
            throw new TruncatedSegmentException(
                $"Segment is {length} bytes — shorter than the minimum Boutquin columnar overhead " +
                $"(header {SegmentMagic.HeaderSize} + CRC {SegmentMagic.FooterCrcSize} + trailer {SegmentMagic.TrailerSize}).",
                length,
                SegmentMagic.HeaderSize + SegmentMagic.FooterCrcSize + SegmentMagic.TrailerSize);
        }

        // ── Trailer: read last 8 bytes as (uint footerLength, uint magicTrailer) ─
        // Magic is validated FIRST — on a non-Boutquin file the trailing bytes may contain
        // arbitrary garbage that could otherwise be treated as a seek offset.
        Span<byte> trailerBuf = stackalloc byte[SegmentMagic.TrailerSize];
        input.Seek(length - SegmentMagic.TrailerSize, SeekOrigin.Begin);
        ReadExactly(input, trailerBuf);
        var declaredFooterLength = BinaryPrimitives.ReadUInt32LittleEndian(trailerBuf[..4]);
        var trailerMagic = BinaryPrimitives.ReadUInt32LittleEndian(trailerBuf[4..]);
        if (trailerMagic != SegmentMagic.AsUInt32)
        {
            throw new InvalidSegmentException(
                $"Trailer magic 0x{trailerMagic:X8} does not match Boutquin columnar segment magic 0x{SegmentMagic.AsUInt32:X8}. " +
                "The file is either not a Boutquin columnar segment or is corrupted at its final 4 bytes.");
        }

        // Validate the byte-0 header (magic + version) — defense-in-depth, symmetric with the
        // outer envelope's ParseFooter. The footer payload carries the authoritative copy, but
        // an unvalidated header would silently accept a corrupted or foreign 8-byte prefix.
        Span<byte> headerBuf = stackalloc byte[SegmentMagic.HeaderSize];
        input.Seek(0, SeekOrigin.Begin);
        ReadExactly(input, headerBuf);
        var headerMagic = BinaryPrimitives.ReadUInt32LittleEndian(headerBuf[..4]);
        if (headerMagic != SegmentMagic.AsUInt32)
        {
            throw new InvalidSegmentException(
                $"Segment header magic 0x{headerMagic:X8} does not match Boutquin columnar segment magic 0x{SegmentMagic.AsUInt32:X8}. " +
                "The segment is corrupted at its first 4 bytes or is not a Boutquin columnar segment.");
        }

        var headerVersion = BinaryPrimitives.ReadUInt32LittleEndian(headerBuf[4..]);
        if (headerVersion > SegmentMagic.CurrentFormatVersion)
        {
            throw new UnsupportedFormatVersionException(headerVersion, SegmentMagic.CurrentFormatVersion);
        }

        // Now we can trust declaredFooterLength — but still range-check it.
        var footerPayloadEnd = length - SegmentMagic.TrailerSize - SegmentMagic.FooterCrcSize;
        var footerPayloadStart = footerPayloadEnd - declaredFooterLength;
        if (footerPayloadStart < SegmentMagic.HeaderSize)
        {
            throw new TruncatedSegmentException(
                $"Trailer declares footer length {declaredFooterLength}, which would place the footer start at byte {footerPayloadStart} — before the segment header (byte {SegmentMagic.HeaderSize}).",
                length,
                footerPayloadStart);
        }

        // ── Footer payload + CRC ─
        var payload = new byte[declaredFooterLength];
        input.Seek(footerPayloadStart, SeekOrigin.Begin);
        ReadExactly(input, payload);

        Span<byte> crcBuf = stackalloc byte[SegmentMagic.FooterCrcSize];
        ReadExactly(input, crcBuf);
        var declaredCrc = BinaryPrimitives.ReadUInt32LittleEndian(crcBuf);
        var computedCrc = Crc32C.HashToUInt32(payload);
        if (declaredCrc != computedCrc)
        {
            throw new CorruptSegmentException(
                $"Footer CRC32C mismatch: computed 0x{computedCrc:X8}, declared 0x{declaredCrc:X8}. Footer payload is corrupted.",
                footerPayloadEnd);
        }

        // ── Parse the validated payload ─
        return ParsePayload(payload);
    }

    private static SegmentFooter ParsePayload(ReadOnlySpan<byte> payload)
    {
        var cursor = 0;

        EnsureRemaining(payload, cursor, 4);
        var footerMagic = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(cursor, 4));
        cursor += 4;
        if (footerMagic != SegmentMagic.AsUInt32)
        {
            // This is different from the trailer magic mismatch — this one indicates the
            // payload bytes themselves are not a Boutquin footer, which is a corruption
            // signal rather than a wrong-file signal (trailer already validated).
            throw new CorruptSegmentException(
                $"Footer payload does not start with Boutquin magic — expected 0x{SegmentMagic.AsUInt32:X8}, got 0x{footerMagic:X8}.",
                0);
        }

        EnsureRemaining(payload, cursor, 4);
        var formatVersion = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(cursor, 4));
        cursor += 4;
        if (formatVersion > SegmentMagic.CurrentFormatVersion)
        {
            throw new UnsupportedFormatVersionException(formatVersion, SegmentMagic.CurrentFormatVersion);
        }

        EnsureRemaining(payload, cursor, 2);
        var columnCount = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(cursor, 2));
        cursor += 2;

        var columns = new ColumnDescriptor[columnCount];
        for (var i = 0; i < columnCount; i++)
        {
            EnsureRemaining(payload, cursor, 2);
            var nameLen = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(cursor, 2));
            cursor += 2;
            EnsureRemaining(payload, cursor, nameLen);
            var name = Encoding.UTF8.GetString(payload.Slice(cursor, nameLen));
            cursor += nameLen;
            // 3 bytes (encodingTag, codecVersion, fieldType) + 8 bytes offset + 8 bytes length.
            EnsureRemaining(payload, cursor, 3 + 8 + 8);
            var encodingTag = (ColumnEncodingTag)payload[cursor++];
            var codecVersion = payload[cursor++];
            var fieldType = (ColumnFieldTypeCode)payload[cursor++];
            var byteOffset = BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(cursor, 8));
            cursor += 8;
            var byteLength = BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(cursor, 8));
            cursor += 8;
            columns[i] = new ColumnDescriptor(name, byteOffset, byteLength, encodingTag, codecVersion, fieldType);
        }

        EnsureRemaining(payload, cursor, 2);
        var rowGroupCount = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(cursor, 2));
        cursor += 2;

        var rowGroups = new RowGroupDescriptor[rowGroupCount];
        // long so the running row total cannot wrap before it is compared.
        long expectedFirstRowIndex = 0;
        for (var i = 0; i < rowGroupCount; i++)
        {
            // 4 int32 fields per row group: minDayNum, maxDayNum, rowCount, firstRowIndex.
            EnsureRemaining(payload, cursor, 16);
            var minDayNum = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(cursor, 4));
            cursor += 4;
            var maxDayNum = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(cursor, 4));
            cursor += 4;
            var rowCount = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(cursor, 4));
            cursor += 4;
            var firstRowIndex = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(cursor, 4));
            cursor += 4;

            // A row group spans >= 1 row and starts at a non-negative absolute offset. A forged
            // descriptor with a zero/negative rowCount or a negative firstRowIndex would otherwise
            // reach the slice decoders and produce silently wrong data (or out-of-range reads).
            if (rowCount <= 0)
            {
                throw new CorruptSegmentException(
                    $"Footer row-group {i} declares rowCount {rowCount}; a row group must contain at least one row.",
                    cursor);
            }

            if (firstRowIndex < 0)
            {
                throw new CorruptSegmentException(
                    $"Footer row-group {i} declares firstRowIndex {firstRowIndex}; the absolute row offset must be non-negative.",
                    cursor);
            }

            // The writer lays row groups out back to back from row 0, and every reader indexes the
            // decoded column arrays (sized to the summed row count) at firstRowIndex + i. A gap or
            // overlap would index past those arrays or serve one row under two row groups, so a
            // descriptor must start exactly where its predecessor ended.
            if (firstRowIndex != expectedFirstRowIndex)
            {
                throw new CorruptSegmentException(
                    $"Footer row-group {i} declares firstRowIndex {firstRowIndex}; row groups are contiguous, "
                    + $"so it must start at row {expectedFirstRowIndex}.",
                    cursor);
            }

            expectedFirstRowIndex += rowCount;

            // A forged-but-CRC-valid footer can carry a minDayNum/maxDayNum outside the DateOnly
            // day-number range; FromDayNumber would otherwise leak a raw ArgumentOutOfRangeException
            // out of every public columnar read. Guard with the typed corruption exception, matching
            // the rowCount/firstRowIndex guards above and the DeltaOfDeltaVlqCodec.ValidateDayNumber
            // convention.
            if (!CorruptionGuards.IsValidDayNumber(minDayNum) || !CorruptionGuards.IsValidDayNumber(maxDayNum))
            {
                throw new CorruptSegmentException(
                    $"Footer row-group {i} declares day-number span [{minDayNum}, {maxDayNum}]; "
                    + $"each must fall within the DateOnly range [{DateOnly.MinValue.DayNumber}, {DateOnly.MaxValue.DayNumber}].",
                    cursor);
            }

            rowGroups[i] = new RowGroupDescriptor(
                DateOnly.FromDayNumber(minDayNum),
                DateOnly.FromDayNumber(maxDayNum),
                rowCount,
                firstRowIndex);
        }

        // Extension sections — every footer carries the extensionCount byte (≥ 0),
        // followed by that many (kind, length, payload) sections. The greenfield premise
        // means there are no pre-extension footers on disk; the byte is always present.
        IReadOnlyList<IReadOnlyList<ColumnRowGroupStats>>? zoneMaps = null;
        IReadOnlyList<BitmapIndexEntry>? bitmapIndexes = null;
        IReadOnlyList<uint>? rowGroupCrcs = null;
        IReadOnlyDictionary<string, IReadOnlyList<int>>? rowGroupByteOffsets = null;

        EnsureRemaining(payload, cursor, 1);
        var extensionCount = payload[cursor++];

        for (var i = 0; i < extensionCount; i++)
        {
            EnsureRemaining(payload, cursor, 1 + 4);
            var kind = (FooterExtensionKind)payload[cursor++];
            var sectionLength = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(cursor, 4));
            cursor += 4;
            // Bound the footer-supplied uint against the remaining payload BEFORE narrowing it: a
            // forged length >= 2^31 must surface CorruptSegmentException, not OverflowException.
            EnsureRemaining(payload, cursor, sectionLength);
            var sectionSpan = payload.Slice(cursor, (int)sectionLength);
            cursor += (int)sectionLength;

            switch (kind)
            {
                case FooterExtensionKind.ZoneMaps:
                    zoneMaps = DecodeZoneMaps(sectionSpan, columns.Length);
                    break;
                case FooterExtensionKind.BitmapIndexes:
                    bitmapIndexes = DecodeBitmapIndexes(sectionSpan);
                    break;
                case FooterExtensionKind.RowGroupCrcs:
                    rowGroupCrcs = DecodeRowGroupCrcs(sectionSpan);
                    break;
                case FooterExtensionKind.RowGroupByteOffsets:
                    rowGroupByteOffsets = DecodeRowGroupByteOffsets(sectionSpan, rowGroups, columns);
                    break;
                default:
                    // Unknown kind — skipped via the length prefix above. Forward-compat:
                    // future writers can add new extension kinds on the same v1 substrate.
                    break;
            }
        }

        return new SegmentFooter(
            formatVersion,
            columns,
            rowGroups,
            zoneMaps,
            bitmapIndexes,
            rowGroupCrcs,
            rowGroupByteOffsets);
    }

    // ───── Extension decoders ────────────────────────────────────────

    private static IReadOnlyList<IReadOnlyList<ColumnRowGroupStats>> DecodeZoneMaps(
        ReadOnlySpan<byte> payload,
        int expectedColumnCount)
    {
        var cursor = 0;
        EnsureRemaining(payload, cursor, 4);
        var rowGroupCount = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(cursor, 2));
        cursor += 2;
        var declaredColumnCount = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(cursor, 2));
        cursor += 2;

        if (declaredColumnCount != expectedColumnCount)
        {
            throw new CorruptSegmentException(
                $"ZoneMaps extension declares {declaredColumnCount} columns; footer declares {expectedColumnCount}.",
                cursor);
        }

        var result = new IReadOnlyList<ColumnRowGroupStats>[rowGroupCount];
        for (var rg = 0; rg < rowGroupCount; rg++)
        {
            var perColumn = new ColumnRowGroupStats[declaredColumnCount];
            for (var c = 0; c < declaredColumnCount; c++)
            {
                EnsureRemaining(payload, cursor, 2);
                var minLen = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(cursor, 2));
                cursor += 2;
                EnsureRemaining(payload, cursor, minLen);
                var minBytes = payload.Slice(cursor, minLen).ToArray();
                cursor += minLen;

                EnsureRemaining(payload, cursor, 2);
                var maxLen = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(cursor, 2));
                cursor += 2;
                EnsureRemaining(payload, cursor, maxLen);
                var maxBytes = payload.Slice(cursor, maxLen).ToArray();
                cursor += maxLen;

                EnsureRemaining(payload, cursor, 8);
                var nullCount = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(cursor, 4));
                cursor += 4;
                var distinctCount = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(cursor, 4));
                cursor += 4;

                // ColumnRowGroupStats rejects negative counts with ArgumentOutOfRangeException; a
                // CRC-valid forged footer must instead raise the typed corruption signal.
                if (nullCount < 0 || distinctCount < 0)
                {
                    throw new CorruptSegmentException(
                        $"ZoneMaps entry for row group {rg}, column {c} declares a negative count "
                        + $"(nullCount={nullCount}, distinctCount={distinctCount}).",
                        cursor - 8);
                }

                perColumn[c] = new ColumnRowGroupStats(minBytes, maxBytes, nullCount, distinctCount);
            }
            result[rg] = perColumn;
        }

        return result;
    }

    private static IReadOnlyList<BitmapIndexEntry> DecodeBitmapIndexes(ReadOnlySpan<byte> payload)
    {
        var cursor = 0;
        EnsureRemaining(payload, cursor, 2);
        var entryCount = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(cursor, 2));
        cursor += 2;

        var result = new BitmapIndexEntry[entryCount];
        for (var i = 0; i < entryCount; i++)
        {
            EnsureRemaining(payload, cursor, 2);
            var nameLen = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(cursor, 2));
            cursor += 2;
            EnsureRemaining(payload, cursor, nameLen);
            var name = Encoding.UTF8.GetString(payload.Slice(cursor, nameLen));
            cursor += nameLen;

            EnsureRemaining(payload, cursor, 4);
            var valueCount = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(cursor, 4));
            cursor += 4;

            // Clamp the pre-size by the remaining payload length — mirrors the pattern used
            // by ParseFooter's block/tombstone counts. A forged-CRC payload with a huge
            // declared count would otherwise pre-allocate a multi-GB array before the
            // per-entry EnsureRemaining reads bound the actual count. Defensive only —
            // this section is already CRC-gated; the per-entry reads will throw
            // CorruptSegmentException on the first entry that exceeds the payload.
            var values = new List<BitmapIndexValue>((int)Math.Min(valueCount, (uint)payload.Length));
            for (var v = 0; v < valueCount; v++)
            {
                EnsureRemaining(payload, cursor, 2);
                var valLen = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(cursor, 2));
                cursor += 2;
                EnsureRemaining(payload, cursor, valLen);
                var valBytes = payload.Slice(cursor, valLen).ToArray();
                cursor += valLen;

                EnsureRemaining(payload, cursor, 4);
                var bitmapLen = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(cursor, 4));
                cursor += 4;
                EnsureRemaining(payload, cursor, bitmapLen);
                var bitmapLenInt = (int)bitmapLen;
                var bitmapBytes = payload.Slice(cursor, bitmapLenInt).ToArray();
                cursor += bitmapLenInt;

                values.Add(new BitmapIndexValue(valBytes, bitmapBytes));
            }

            result[i] = new BitmapIndexEntry(name, values);
        }

        return result;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<int>> DecodeRowGroupByteOffsets(
        ReadOnlySpan<byte> payload,
        IReadOnlyList<RowGroupDescriptor> rowGroups,
        IReadOnlyList<ColumnDescriptor> columns)
    {
        var cursor = 0;
        EnsureRemaining(payload, cursor, 2);
        var columnCount = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(cursor, 2));
        cursor += 2;

        var result = new Dictionary<string, IReadOnlyList<int>>(columnCount, StringComparer.Ordinal);
        for (var c = 0; c < columnCount; c++)
        {
            EnsureRemaining(payload, cursor, 2);
            var nameLen = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(cursor, 2));
            cursor += 2;
            EnsureRemaining(payload, cursor, nameLen);
            var name = Encoding.UTF8.GetString(payload.Slice(cursor, nameLen));
            cursor += nameLen;

            EnsureRemaining(payload, cursor, 2);
            var rgCount = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(cursor, 2));
            cursor += 2;

            EnsureRemaining(payload, cursor, rgCount * 4);
            var offsets = new int[rgCount];
            for (var i = 0; i < rgCount; i++)
            {
                offsets[i] = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(cursor, 4));
                cursor += 4;
            }

            // A forged-but-CRC-valid footer can carry a byte-offset list that drives the
            // non-restartable decode path's per-row-group slice boundaries (start = offsets[i],
            // end = offsets[i+1]). Validate the decoded list against the structural invariants the
            // writer always honours, BEFORE it reaches the slice decoders: an unvalidated negative,
            // non-monotonic, wrong-count, or overrun offset would otherwise produce a negative slice
            // length or over-read the column blob and leak a raw ArgumentOutOfRangeException out of
            // the public query API. Throw the typed CorruptSegmentException, matching the rowCount /
            // firstRowIndex / day-number guards on the same footer.
            ValidateRowGroupByteOffsets(name, offsets, rowGroups.Count, ColumnBlobLength(name, columns));

            result[name] = offsets;
        }

        return result;
    }

    /// <summary>
    /// Returns the on-disk blob length for the column named <paramref name="name"/>, or
    /// <see langword="null"/> when the offset section names a column absent from the descriptor
    /// list (itself a corruption signal validated by the caller).
    /// </summary>
    private static long? ColumnBlobLength(string name, IReadOnlyList<ColumnDescriptor> columns)
    {
        foreach (var column in columns)
        {
            if (string.Equals(column.Name, name, StringComparison.Ordinal))
            {
                return column.ByteLength;
            }
        }

        return null;
    }

    /// <summary>
    /// Validates a decoded per-column byte-offset list: it must name a known column, carry exactly
    /// one offset per row group, and present non-negative, monotonically non-decreasing offsets each
    /// within the column's blob length. Any violation is forged-footer corruption and throws
    /// <see cref="CorruptSegmentException"/>.
    /// </summary>
    private static void ValidateRowGroupByteOffsets(string name, int[] offsets, int rowGroupCount, long? blobLength)
    {
        if (blobLength is null)
        {
            throw new CorruptSegmentException(
                $"Footer RowGroupByteOffsets names column '{name}', which is absent from the column descriptors.",
                offset: 0);
        }

        if (offsets.Length != rowGroupCount)
        {
            throw new CorruptSegmentException(
                $"Footer RowGroupByteOffsets for column '{name}' declares {offsets.Length} offsets, "
                + $"but the segment has {rowGroupCount} row group(s); the counts must match.",
                offset: 0);
        }

        var previous = 0;
        for (var i = 0; i < offsets.Length; i++)
        {
            var offset = offsets[i];
            if (offset < 0)
            {
                throw new CorruptSegmentException(
                    $"Footer RowGroupByteOffsets for column '{name}' declares a negative offset {offset} "
                    + $"at row group {i}; byte offsets must be non-negative.",
                    offset);
            }

            if (offset < previous)
            {
                throw new CorruptSegmentException(
                    $"Footer RowGroupByteOffsets for column '{name}' is non-monotonic: offset {offset} "
                    + $"at row group {i} is below the prior offset {previous}; offsets must be non-decreasing.",
                    offset);
            }

            if (offset > blobLength.Value)
            {
                throw new CorruptSegmentException(
                    $"Footer RowGroupByteOffsets for column '{name}' declares offset {offset} at row group {i}, "
                    + $"which overruns the {blobLength.Value}-byte column blob.",
                    offset);
            }

            previous = offset;
        }
    }

    private static IReadOnlyList<uint> DecodeRowGroupCrcs(ReadOnlySpan<byte> payload)
    {
        var cursor = 0;
        EnsureRemaining(payload, cursor, 2);
        var count = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(cursor, 2));
        cursor += 2;

        var result = new uint[count];
        for (var i = 0; i < count; i++)
        {
            EnsureRemaining(payload, cursor, 4);
            result[i] = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(cursor, 4));
            cursor += 4;
        }

        return result;
    }

    /// <summary>
    /// Verifies that <paramref name="payload"/> has at least <paramref name="count"/> bytes
    /// remaining from <paramref name="cursor"/>. Throws <see cref="CorruptSegmentException"/>
    /// rather than letting <see cref="ReadOnlySpan{T}.Slice(int, int)"/> throw
    /// <see cref="ArgumentOutOfRangeException"/>, so a crafted payload with a matching CRC
    /// produces the same exception type as other footer corruption.
    /// </summary>
    /// <remarks>
    /// <paramref name="count"/> is <see cref="long"/> so a footer-supplied <see cref="uint"/> length
    /// is bounded before any narrowing, and the bound subtracts (<c>count &gt; Length - cursor</c>)
    /// rather than adds: <c>cursor + count</c> in <see cref="int"/> wraps negative for a count near
    /// <see cref="int.MaxValue"/> and would let the forged length through to the slice.
    /// </remarks>
    private static void EnsureRemaining(ReadOnlySpan<byte> payload, int cursor, long count)
    {
        if ((uint)cursor > (uint)payload.Length || count < 0 || count > payload.Length - cursor)
        {
            throw new CorruptSegmentException(
                $"Footer payload is truncated or malformed: needed {count} more bytes at cursor {cursor} of {payload.Length}.",
                cursor);
        }
    }

    private static void ReadExactly(Stream input, Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = input.Read(buffer[total..]);
            if (read == 0)
            {
                throw new TruncatedSegmentException(
                    $"Stream ended after {total} of {buffer.Length} expected bytes.",
                    input.Length,
                    input.Position + (buffer.Length - total));
            }

            total += read;
        }
    }
}
