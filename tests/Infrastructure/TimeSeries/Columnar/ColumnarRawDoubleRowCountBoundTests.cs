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
/// RX1 — SEGFMT-RAWDOUBLE-ALLOC. <c>DecodeRawDoubles</c> / <c>DecodeRawDoublesSlice</c> allocate
/// <c>new double[count]</c> from the footer-supplied summed rowCount BEFORE bounding that count
/// against the column payload. <see cref="ColumnEncoding.RawDouble"/> is the one codec in the
/// full-column dispatch that does not self-bound (every other codec — ScaledIntegerBitPacked,
/// DeltaVlq, Dictionary, BitmapRle — verifies its declared element count against the payload
/// before allocating). Each raw double occupies exactly 8 bytes, so a column declaring more than
/// <c>payload.Length / 8</c> values cannot have been produced by that payload and is corruption.
/// A forged footer (CRC is body-only and forgeable) can shrink the RawDouble column's declared
/// payload below <c>rowCount * 8</c>, driving an over-allocation and a read past the payload. This
/// pins the bound-before-alloc guard, mirroring <c>ValidateRowCountAgainstPayload</c> /
/// <c>ValidateColumnExtent</c> and using the same <see cref="TruncatedSegmentException"/>.
/// </summary>
public sealed class ColumnarRawDoubleRowCountBoundTests
{
    [Fact]
    public async Task DecodeRawDoubles_RowCountExceedsPayload_ThrowsBeforeAllocating()
    {
        // Arrange — a small ColumnarBar segment. Residual rides RawDouble (restartable), decoded
        // through the full-column DecodeColumn -> DecodeRawDoubles path, where `total` (= 4 rows
        // here) is handed straight to `new double[total]`.
        var format = new CompressedColumnarSegmentFormat<ColumnarBar>();
        var rows = Enumerable.Range(0, 4).Select(i => new ColumnarBar
        {
            Date = new DateOnly(2020, 1, 1).AddDays(i),
            Ticker = "AAA",
            Close = 1m + i,
            Volume = 100 + i,
            TradeCount = i,
            Vwap = i * 1.25,
            IsAdjusted = i % 2 == 0,
            Residual = i * 0.5,
        }).ToList();

        using var stream = new MemoryStream();
        await format.WriteAsync(stream, rows).ConfigureAwait(true);
        var bytes = stream.ToArray();

        // Tamper: shrink ONLY the Residual (RawDouble) column's declared byteLength from 32
        // (4 doubles) to 16 (2 doubles). rowCount stays 4, every other column is untouched and
        // decodes cleanly, so the read reaches DecodeRawDoubles with payload.Length == 16 and
        // total == 4. RawDouble's ceiling is 16 / 8 = 2, so 4 > 2: the unfixed code allocates
        // `new double[4]` and then reads payload bytes [16, 24) past the 16-byte slice. 16 is
        // still < segmentLength - offset, so the column-extent overrun guard does NOT fire first.
        TamperColumnByteLength(bytes, columnName: "Residual", newByteLength: 16);
        RepairFooterCrc(bytes);

        using var corrupted = new MemoryStream(bytes);

        // Act + Assert — must be a typed truncation exception raised BEFORE the over-allocation,
        // NOT a wrapped ColumnDecodeException (IndexOutOfRange) surfacing AFTER the alloc.
        var act = async () =>
        {
            await foreach (var _ in new CompressedColumnarSegmentFormat<ColumnarBar>().ReadRowGroupsAsync(
                corrupted,
                new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31)))
            {
            }
        };

        await act.Should().ThrowAsync<TruncatedSegmentException>(
            "a RawDouble column whose rowCount exceeds payload.Length / 8 must be rejected before allocating");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Helpers (mirrors ColumnarFooterByteLengthBoundTests footer-walk layout)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Overwrites a named column descriptor's <c>byteLength</c> int64 field in the footer payload.
    /// Per-column layout: nameLen(2) + name + tag(1) + codec(1) + type(1) + offset(8) + length(8).
    /// </summary>
    private static void TamperColumnByteLength(byte[] bytes, string columnName, long newByteLength)
    {
        var (payloadStart, _) = LocateFooterPayload(bytes);
        var payload = bytes.AsSpan(payloadStart);

        var cursor = 4 + 4; // magic + version
        var columnCount = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(cursor, 2));
        cursor += 2;

        for (var i = 0; i < columnCount; i++)
        {
            var nameLen = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(cursor, 2));
            var name = System.Text.Encoding.UTF8.GetString(payload.Slice(cursor + 2, nameLen));
            var lengthFieldOffset = cursor + 2 + nameLen + 3 + 8; // past name + tag/codec/type + offset
            if (name == columnName)
            {
                BinaryPrimitives.WriteInt64LittleEndian(
                    bytes.AsSpan(payloadStart + lengthFieldOffset, 8), newByteLength);
                return;
            }

            cursor += 2 + nameLen + 3 + 8 + 8; // advance to next column descriptor
        }

        throw new InvalidOperationException($"Column '{columnName}' not found in footer.");
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
