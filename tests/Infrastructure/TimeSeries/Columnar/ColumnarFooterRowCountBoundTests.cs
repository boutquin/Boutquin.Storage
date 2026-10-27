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
/// F-V5B-02 — SEGFMT-SUMMED-ROWCOUNT-ALLOC. <c>ReadRowGroupsAsync</c> sums every footer
/// row-group's <c>rowCount</c> and pre-allocates one flat array of that size
/// (<c>Array.CreateInstance(type, total)</c>) BEFORE the per-row-group decode loop bounds
/// each slice. <see cref="Boutquin.Storage.Infrastructure.TimeSeries.Columnar.Footer.FooterReader"/>
/// only validates <c>rowCount &gt; 0</c>, never against the segment's byte length, and the CRC
/// is forgeable. A forged footer can thus drive a multi-GB allocation from a tiny payload. Because
/// this non-restartable path serves only GorillaXor / DeltaOfDeltaVlq (≥ 1 bit per value), a sound
/// payload-derived ceiling exists: <c>total</c> rows require at least <c>total / 8</c> payload
/// bytes, so a <c>total</c> exceeding <c>payloadBytes * 8</c> is corruption and must be rejected
/// before the allocation. Mirrors the <c>ValidateColumnExtent</c> alloc-before-bound guard.
/// </summary>
public sealed class ColumnarFooterRowCountBoundTests
{
    [Fact]
    public async Task ReadRowGroupsAsync_SummedRowCountExceedsPayload_ThrowsBeforeAllocating()
    {
        // Arrange — a small FloatBar segment. Residual rides GorillaXor (non-restartable), so the
        // footer carries RowGroupByteOffsets and the read takes the summed-rowCount alloc path.
        var format = new CompressedColumnarSegmentFormat<FloatBar>();
        var rows = Enumerable.Range(0, 4).Select(i => new FloatBar
        {
            Date = new DateOnly(2020, 1, 1).AddDays(i),
            Residual = i * 0.5f,
        }).ToList();

        using var stream = new MemoryStream();
        await format.WriteAsync(stream, rows).ConfigureAwait(true);
        var bytes = stream.ToArray();

        // Tamper: inflate the first row group's rowCount far beyond what the few-byte payload
        // could ever encode (a million floats over a handful of payload bytes), then repair the
        // footer CRC so the read reaches the allocation. 1,000,000 is large enough to prove the
        // bound fires yet small enough that the test is fast and never OOMs.
        TamperFirstRowGroupRowCount(bytes, newRowCount: 1_000_000);
        RepairFooterCrc(bytes);

        using var corrupted = new MemoryStream(bytes);

        // Act + Assert — must be a typed corruption/truncation exception, raised BEFORE the
        // unbounded Array.CreateInstance, NOT a downstream decode error or an OOM.
        var act = async () =>
        {
            await foreach (var _ in new CompressedColumnarSegmentFormat<FloatBar>().ReadRowGroupsAsync(
                corrupted,
                new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31)))
            {
            }
        };

        await act.Should().ThrowAsync<TruncatedSegmentException>(
            "a summed rowCount that exceeds what the payload could possibly encode must be rejected before allocating");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Helpers (mirrors ColumnarFooterByteLengthBoundTests footer-walk layout)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Overwrites the first row-group descriptor's <c>rowCount</c> int32 field in the footer
    /// payload. Payload layout: magic(4) + version(4) + columnCount(2) + [per column:
    /// nameLen(2) + name + tag(1) + codec(1) + type(1) + offset(8) + length(8)] +
    /// rowGroupCount(2) + [per row group: minDayNum(4) + maxDayNum(4) + rowCount(4) +
    /// firstRowIndex(4)].
    /// </summary>
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
