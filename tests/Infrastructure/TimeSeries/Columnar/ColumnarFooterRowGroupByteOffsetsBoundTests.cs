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
using System.Buffers.Binary;

using Boutquin.Storage.Infrastructure.TimeSeries.Columnar;
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar.Footer;

namespace Boutquin.Storage.Infrastructure.Tests.TimeSeries.Columnar;

/// <summary>
/// F-30 — forged <c>RowGroupByteOffsets</c> extension section. The per-column byte-run offset
/// list drives the per-row-group slice boundaries on the non-restartable decode path
/// (<c>CompressedColumnarSegmentFormat</c>: <c>start = offsets[rgIdx]</c>,
/// <c>end = offsets[rgIdx + 1]</c>). A forged-but-CRC-valid footer can carry a negative, a
/// non-monotonic, a wrong-count, or an overrun offset; without validation the reader either
/// over-reads the column blob or computes a negative slice length and leaks a raw
/// <see cref="ArgumentOutOfRangeException"/> out of the public query API. <see cref="FooterReader"/>
/// must reject each violation with the typed <see cref="CorruptSegmentException"/> at decode time,
/// matching the rowCount / firstRowIndex / day-number guards on the same footer.
/// </summary>
public sealed class ColumnarFooterRowGroupByteOffsetsBoundTests
{
    [Fact]
    public async Task ReadRowGroupsAsync_NegativeRowGroupByteOffset_ThrowsCorruptSegment()
        => await AssertTamperedOffsetsThrowCorrupt(offsets =>
        {
            // Forge a negative offset for the second row group — an absolute byte offset must be
            // non-negative; a negative start produces an out-of-range column-blob slice.
            offsets[1] = -1;
        }).ConfigureAwait(true);

    [Fact]
    public async Task ReadRowGroupsAsync_NonMonotonicRowGroupByteOffset_ThrowsCorruptSegment()
        => await AssertTamperedOffsetsThrowCorrupt(offsets =>
        {
            // Force a strictly-decreasing pair: offsets must be monotonically non-decreasing because
            // slice i runs [offsets[i], offsets[i+1]); a decrease yields end < start (negative length).
            offsets[1] = int.MaxValue;
            offsets[2] = 0;
        }).ConfigureAwait(true);

    [Fact]
    public async Task ReadRowGroupsAsync_OverrunRowGroupByteOffset_ThrowsCorruptSegment()
        => await AssertTamperedOffsetsThrowCorrupt(offsets =>
        {
            // An offset past the end of the column blob over-reads on AsSpan(start, end - start).
            offsets[1] = int.MaxValue;
        }).ConfigureAwait(true);

    private static async Task AssertTamperedOffsetsThrowCorrupt(Action<int[]> tamper)
    {
        // Arrange — a date-dominant segment with a SMALL rowGroupMaxRows so 16 rows split into
        // several row groups; the DeltaOfDeltaVlq date column then emits a multi-entry
        // RowGroupByteOffsets extension section.
        var d0 = new DateOnly(2020, 1, 1);
        var rows = Enumerable.Range(0, 16).Select(i => new NarrowBar
        {
            Date = d0.AddDays(i),
            Ticker = "X",
        }).ToList();

        var format = new CompressedColumnarSegmentFormat<NarrowBar>(rowGroupMaxRows: 4);
        using var stream = new MemoryStream();
        await format.WriteAsync(stream, rows).ConfigureAwait(true);
        var bytes = stream.ToArray();

        TamperFirstColumnRowGroupByteOffsets(bytes, tamper);
        RepairFooterCrc(bytes);

        using var corrupted = new MemoryStream(bytes);

        var act = async () =>
        {
            await foreach (var _ in new CompressedColumnarSegmentFormat<NarrowBar>(rowGroupMaxRows: 4)
                .ReadRowGroupsAsync(corrupted, new DateOnly(2019, 1, 1), new DateOnly(2021, 12, 31)))
            {
            }
        };

        await act.Should().ThrowAsync<CorruptSegmentException>(
            "a forged RowGroupByteOffsets list must be validated at footer-decode time");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Footer-walk helpers. Payload layout:
    //   magic(4) + version(4) + columnCount(2)
    //   + [per column: nameLen(2) + name + tag(1) + codec(1) + type(1) + offset(8) + length(8)]
    //   + rowGroupCount(2) + [per row group: minDayNum(4) + maxDayNum(4) + rowCount(4) + firstRowIndex(4)]
    //   + extensionCount(1) + [per extension: kind(1) + sectionLength(4) + section bytes]
    // The RowGroupByteOffsets section layout:
    //   columnCount(2) + [per column: nameLen(2) + name + rgCount(2) + rgCount × int32 offsets]
    // ═══════════════════════════════════════════════════════════════════════════

    private static void TamperFirstColumnRowGroupByteOffsets(byte[] bytes, Action<int[]> tamper)
    {
        var (payloadStart, _) = LocateFooterPayload(bytes);
        var payload = bytes.AsSpan(payloadStart);

        var cursor = 4 + 4; // magic + version
        var columnCount = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(cursor, 2));
        cursor += 2;
        for (var i = 0; i < columnCount; i++)
        {
            var nameLen = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(cursor, 2));
            cursor += 2 + nameLen + 3 + 8 + 8;
        }

        var rowGroupCount = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(cursor, 2));
        cursor += 2;
        cursor += rowGroupCount * 16;

        var extensionCount = payload[cursor++];
        for (var e = 0; e < extensionCount; e++)
        {
            var kind = (FooterExtensionKind)payload[cursor++];
            var sectionLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(cursor, 4));
            cursor += 4;
            var sectionStart = cursor;
            cursor += sectionLength;

            if (kind != FooterExtensionKind.RowGroupByteOffsets)
            {
                continue;
            }

            // Walk into the section to the first column's offset array.
            var sc = sectionStart;
            sc += 2; // columnCount within the section
            var firstNameLen = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(sc, 2));
            sc += 2 + firstNameLen;
            var rgCount = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(sc, 2));
            sc += 2;

            var offsets = new int[rgCount];
            for (var i = 0; i < rgCount; i++)
            {
                offsets[i] = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(sc + (i * 4), 4));
            }

            tamper(offsets);

            for (var i = 0; i < rgCount; i++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(
                    bytes.AsSpan(payloadStart + sc + (i * 4), 4), offsets[i]);
            }

            return;
        }

        throw new InvalidOperationException("No RowGroupByteOffsets extension section found in the footer.");
    }

    private static (int PayloadStart, int PayloadLength) LocateFooterPayload(byte[] bytes)
    {
        var trailerStart = bytes.Length - 8;
        var footerLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(trailerStart, 4));
        var footerPayloadEnd = trailerStart - 4; // CRC is 4 bytes before trailer
        var footerPayloadStart = footerPayloadEnd - footerLength;
        return (footerPayloadStart, footerLength);
    }

    private static void RepairFooterCrc(byte[] bytes)
    {
        var (payloadStart, payloadLength) = LocateFooterPayload(bytes);
        var payload = bytes.AsSpan(payloadStart, payloadLength).ToArray();
        var crc = Boutquin.Storage.Infrastructure.Algorithms.Crc32C.HashToUInt32(payload);
        var crcOffset = payloadStart + payloadLength;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(crcOffset, 4), crc);
    }
}
