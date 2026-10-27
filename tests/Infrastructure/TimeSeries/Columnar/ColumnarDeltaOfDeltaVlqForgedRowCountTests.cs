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

namespace Boutquin.Storage.Infrastructure.Tests.TimeSeries.Columnar;

/// <summary>
/// F-V2-alloc-ddvlq (production-path wiring) — updated for the F-W3-regress-01 ceiling fix. The
/// DeltaOfDeltaVlq date column is decoded through the non-restartable per-row-group path in
/// <c>CompressedColumnarSegmentFormat</c>, which (a) bounds the SUMMED row-group rowCount against
/// the column payload (<c>ValidateRowCountAgainstPayload</c>: <c>total ≤ payloadBytes × 8</c>,
/// sound because DeltaOfDeltaVlq spends ≥ 1 bit per row) BEFORE the flat-array allocation, and
/// (b) threads a per-row-group allocation ceiling into
/// <see cref="Boutquin.Storage.Infrastructure.TimeSeries.Columnar.Codecs.DeltaOfDeltaVlqCodec.Decode"/>
/// for each row-group slice. A forged footer RowCount near <see cref="int.MaxValue"/> must be
/// rejected with a typed corruption exception BEFORE any unbounded <c>new DateOnly[count]</c> —
/// never an <see cref="OutOfMemoryException"/> and never a silent over-read.
///
/// <para><b>Why this is no longer the segment-byte ceiling.</b> The round-5 fix wrongly bounded the
/// declared count against the whole-segment byte length, which false-rejected a valid run-length-
/// compressed column (a codec decodes many rows from few bytes). The corrected ceiling is a sane
/// per-row-group maximum; for the DeltaOfDeltaVlq date column the load-bearing defense at practical
/// fixture sizes is the tighter payload-derived summed-rowcount guard
/// (<c>ValidateRowCountAgainstPayload</c>), which fires first on a large forge. This test asserts
/// the production read rejects the forged count with a typed corruption exception, proving the
/// allocation is bounded.</para>
/// </summary>
public sealed class ColumnarDeltaOfDeltaVlqForgedRowCountTests
{
    [Fact]
    public async Task ReadRowGroupsAsync_ForgedDeltaOfDeltaVlqRowCount_RejectedBeforeAllocating()
    {
        // Arrange — a date-dominant NarrowBar segment. The DateOnly column rides DeltaOfDeltaVlq
        // (non-restartable), decoded through the per-row-group DecodeColumnSlice path; the short
        // constant ticker rides a tiny dictionary column.
        var format = new CompressedColumnarSegmentFormat<NarrowBar>();
        var d0 = new DateOnly(2020, 1, 1);
        var rows = Enumerable.Range(0, 64).Select(i => new NarrowBar
        {
            Date = d0.AddDays(i),
            Ticker = "X",
        }).ToList();

        using var stream = new MemoryStream();
        await format.WriteAsync(stream, rows).ConfigureAwait(true);
        var bytes = stream.ToArray();

        // Forge a single-row-group rowCount of int.MaxValue (~2.1 billion). The date column payload
        // is ~64 bytes, so the summed-rowcount guard (total ≤ payloadBytes × 8 ≈ 512) rejects this
        // before the flat-array allocation. A typed corruption exception, never an OOM, never a
        // ~16 GB DateOnly[].
        TamperFirstRowGroupRowCount(bytes, int.MaxValue);
        RepairFooterCrc(bytes);

        using var corrupted = new MemoryStream(bytes);

        // Act + Assert — the forged RowCount must be rejected with a typed corruption exception
        // BEFORE any unbounded allocation. The summed-rowcount guard surfaces a TruncatedSegmentException.
        var act = async () =>
        {
            await foreach (var _ in new CompressedColumnarSegmentFormat<NarrowBar>().ReadRowGroupsAsync(
                corrupted,
                new DateOnly(2019, 1, 1), new DateOnly(2021, 12, 31)))
            {
            }
        };

        await act.Should().ThrowAsync<TruncatedSegmentException>(
            "a forged DeltaOfDeltaVlq RowCount must be rejected by the payload-derived summed-rowcount "
            + "guard before the unbounded DateOnly[] allocation");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Footer-walk helpers — payload layout mirrors ColumnarFooterRowCountBoundTests:
    // magic(4) + version(4) + columnCount(2) + [per column: nameLen(2) + name +
    // tag(1) + codec(1) + type(1) + offset(8) + length(8)] + rowGroupCount(2) +
    // [per row group: minDayNum(4) + maxDayNum(4) + rowCount(4) + firstRowIndex(4)].
    // ═══════════════════════════════════════════════════════════════════════════

    private static void TamperFirstRowGroupRowCount(byte[] bytes, int newRowCount)
    {
        var (payloadStart, _) = LocateFooterPayload(bytes);
        var payload = bytes.AsSpan(payloadStart);

        var cursor = 4 + 4; // magic + version
        var columnCount = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(cursor, 2));
        cursor += 2;

        for (var i = 0; i < columnCount; i++)
        {
            var nameLen = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(cursor, 2));
            cursor += 2 + nameLen + 3 + 8 + 8; // name + tag/codec/type + offset + length
        }

        cursor += 2; // rowGroupCount

        // Row group 0: rowCount sits after minDayNum(4) + maxDayNum(4).
        var rowCountFieldOffset = cursor + 4 + 4;
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(payloadStart + rowCountFieldOffset, 4), newRowCount);
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
