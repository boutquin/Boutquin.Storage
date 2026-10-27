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
using Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar;
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar;

namespace Boutquin.Storage.Infrastructure.Tests.TimeSeries.Columnar;

/// <summary>
/// F-W3-regress-01 — the run-length codecs (<c>BitmapRleCodec</c>,
/// <c>DeltaVlqCodec</c>, <c>DeltaOfDeltaVlqCodec</c>) legitimately
/// encode a great many rows in a handful of compressed bytes. The round-5 allocation guard
/// bounded the declared row count against the <i>whole-segment byte length</i>, which is FALSE
/// for a run-length codec: a constant/low-cardinality column of hundreds of thousands of rows
/// compresses to ~a few bytes, so the guard evaluated <c>rowCount &gt; segmentBytes</c> and threw
/// <see cref="CorruptSegmentException"/> on perfectly valid, codec-written data (data loss — the
/// segment becomes permanently unreadable). <see cref="RunLengthBar"/> carries ONLY run-length /
/// delta columns (no fixed-width column to inflate the segment past the row count), so it
/// reproduces the false-rejection end-to-end through <see cref="CompressedColumnarSegmentFormat{TRecord}"/>.
///
/// The correct allocation ceiling is a sane PER-ROW-GROUP maximum, not the segment byte length:
/// a single row group's row count is capped by the writer's <c>rowGroupMaxRows</c>, far below
/// OOM territory, while a forged <c>int.MaxValue</c> row count still exceeds the cap and is
/// rejected. These tests pin both halves: a valid large compressible column round-trips, and a
/// forged-RowCount footer with a saturating payload still throws.
/// </summary>
public sealed class ColumnarLargeCompressibleColumnRoundTripTests
{
    // > 131_072 (the default rowGroupMaxRows = 1 << 17) so the column spans >= 2 row groups,
    // the exact regime in which the last row group's (firstRowIndex + count) equals the whole-
    // column total and the segment-byte-length ceiling false-rejects.
    private const int RowCount = 200_000;

    [Fact]
    public async Task RoundTrip_ConstantRunLengthColumns_OverTwoRowGroups_DoesNotFalseReject()
    {
        // Arrange — a constant column set: Flag (BitmapRle), IntValue (DeltaVlq int), LongValue
        // (DeltaVlq long), Date (DeltaOfDeltaVlq). Each compresses to a few bytes for the whole
        // 200k-row column and there is no fixed-width column, so the segment stays a few hundred
        // bytes total. The date is held constant so row-group planning splits purely by row count
        // (rowGroupMaxRows), not the date-range cap, yielding exactly 2 row groups (131072 + 68928).
        var date = new DateOnly(2020, 6, 15);
        var rows = new List<RunLengthBar>(RowCount);
        for (var i = 0; i < RowCount; i++)
        {
            rows.Add(new RunLengthBar
            {
                Date = date,
                Flag = false,
                IntValue = 42,
                LongValue = 5_000_000L,
            });
        }

        var format = new CompressedColumnarSegmentFormat<RunLengthBar>();
        using var stream = new MemoryStream();
        await format.WriteAsync(stream, rows).ConfigureAwait(true);
        stream.Position = 0;

        // Act — read the whole column back. Pre-fix this throws CorruptSegmentException on the
        // last row group because (firstRowIndex + count) = 200000 > input.Length (~a few hundred
        // bytes), even though the data is valid.
        var recovered = new List<RunLengthBar>(RowCount);
        await foreach (var r in format.ReadAsync(stream, date, date, ColumnProjection.All).ConfigureAwait(true))
        {
            recovered.Add(r);
        }

        // Assert — every row round-trips. A false rejection would have surfaced above as a throw.
        recovered.Should().HaveCount(RowCount);
        recovered.Should().OnlyContain(r =>
            !r.Flag && r.IntValue == 42 && r.LongValue == 5_000_000L && r.Date == date);
    }

    [Fact]
    public async Task RoundTrip_ConstantRunLengthColumns_StreamRowGroups_DoNotFalseReject()
    {
        // Same valid large compressible column, exercised through the ReadRowGroupsAsync path,
        // which decodes each row group's slice independently (DecodeColumnSlice) — the path the
        // finding identifies as carrying the (firstRowIndex + count) extent into the ceiling.
        var date = new DateOnly(2021, 3, 1);
        var rows = new List<RunLengthBar>(RowCount);
        for (var i = 0; i < RowCount; i++)
        {
            rows.Add(new RunLengthBar
            {
                Date = date,
                Flag = true,
                IntValue = 7,
                LongValue = 1_234_567L,
            });
        }

        var format = new CompressedColumnarSegmentFormat<RunLengthBar>();
        using var stream = new MemoryStream();
        await format.WriteAsync(stream, rows).ConfigureAwait(true);
        stream.Position = 0;

        var total = 0;
        await foreach (var rg in format.ReadRowGroupsAsync(stream, date, date).ConfigureAwait(true))
        {
            rg.IsOk.Should().BeTrue();
            total += rg.Rows!.Count;
        }

        total.Should().Be(RowCount);
    }

    [Fact]
    public async Task ForgedRowCount_SaturatingRunLengthPayload_StillThrowsCorruptSegment()
    {
        // DoS defense must NOT regress. Write a small valid run-length segment, then forge the
        // first row group's rowCount to int.MaxValue and repair the footer CRC. The run-length
        // payloads are in the saturated regime (their per-payload ceiling is vacuous), so only
        // the per-row-group ceiling can reject the forged count — and it must, BEFORE the multi-
        // gigabyte allocation. The corruption surfaces as a CorruptSegmentException; the
        // production read path wraps a per-column codec failure in ColumnDecodeException, so the
        // CorruptSegmentException appears as the inner exception of the chain.
        var date = new DateOnly(2020, 1, 1);
        var rows = Enumerable.Range(0, 8).Select(i => new RunLengthBar
        {
            Date = date.AddDays(i),
            Flag = false,
            IntValue = 1,
            LongValue = 1L,
        }).ToList();

        var format = new CompressedColumnarSegmentFormat<RunLengthBar>();
        using var stream = new MemoryStream();
        await format.WriteAsync(stream, rows).ConfigureAwait(true);
        var bytes = stream.ToArray();

        TamperFirstRowGroupRowCount(bytes, newRowCount: int.MaxValue);
        RepairFooterCrc(bytes);

        using var corrupted = new MemoryStream(bytes);
        Exception? caught = null;
        try
        {
            await foreach (var _ in new CompressedColumnarSegmentFormat<RunLengthBar>().ReadAsync(
                corrupted, new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31), ColumnProjection.All)
                .ConfigureAwait(true))
            {
            }
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        // A forged int.MaxValue rowCount on a saturating run-length payload must be rejected by
        // the per-row-group ceiling before the unbounded allocation, surfacing a typed
        // CorruptSegmentException (bare or wrapped), never an OutOfMemoryException.
        caught.Should().NotBeNull();
        var chain = Unwrap(caught!).ToList();
        chain.Should().Contain(e => e is CorruptSegmentException,
            "the forged count must be rejected by the per-row-group ceiling as a corruption, not OOM");
        chain.Should().NotContain(e => e is OutOfMemoryException);
    }

    private static IEnumerable<Exception> Unwrap(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            yield return e;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Footer-walk helpers (mirror ColumnarFooterRowCountBoundTests layout).
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
