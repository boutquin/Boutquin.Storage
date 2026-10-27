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
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar;

namespace Boutquin.Storage.Infrastructure.Tests.TimeSeries.Columnar;

/// <summary>
/// C05 — SEGFMT-UNBOUNDED-BYTELENGTH-ALLOC-001. A forged footer column descriptor whose
/// <c>byteLength</c> far exceeds the file must be rejected with a typed segment exception
/// BEFORE the column-bytes allocation. On the unfixed code the allocation
/// (<c>new byte[desc.ByteLength]</c>) sat outside the decode-error guard, so a colossal
/// declared length surfaced an uncaught <see cref="OverflowException"/> (or an unbounded
/// allocation) instead of the <see cref="TruncatedSegmentException"/> the truncation
/// contract promises. A negative length is bit-rot — <see cref="CorruptSegmentException"/>.
/// </summary>
public sealed class ColumnarFooterByteLengthBoundTests
{
    [Fact]
    public async Task ReadRowGroupsAsync_HugeColumnByteLength_ThrowsTruncated_NotOverflowOrOom()
    {
        // Arrange — write a valid NarrowBar segment (Date + Ticker columns).
        var format = new CompressedColumnarSegmentFormat<NarrowBar>();
        var rows = Enumerable.Range(0, 4).Select(i => new NarrowBar
        {
            Date = new DateOnly(2020, 1, 1).AddDays(i),
            Ticker = i < 2 ? "AAPL" : "MSFT",
        }).ToList();

        using var stream = new MemoryStream();
        await format.WriteAsync(stream, rows).ConfigureAwait(true);
        var bytes = stream.ToArray();

        // Tamper: overwrite the first column descriptor's byteLength int64 with a value far
        // larger than the file, then repair the footer CRC so the read reaches the allocation.
        TamperFirstColumnByteLength(bytes, newByteLength: long.MaxValue);
        RepairFooterCrc(bytes);

        using var corrupted = new MemoryStream(bytes);

        // Act + Assert — must be a typed corruption exception, NOT an uncaught OverflowException.
        var act = async () =>
        {
            await foreach (var _ in new CompressedColumnarSegmentFormat<NarrowBar>().ReadRowGroupsAsync(
                corrupted,
                new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31)))
            {
            }
        };

        await act.Should().ThrowAsync<TruncatedSegmentException>(
            "a column byteLength exceeding the file must be rejected before allocating, not surface OverflowException");
    }

    [Fact]
    public async Task ReadBatchesAsync_HugeColumnByteLength_ThrowsTruncated_NotOverflowOrOom()
    {
        var format = new CompressedColumnarSegmentFormat<NarrowBar>();
        var rows = Enumerable.Range(0, 4).Select(i => new NarrowBar
        {
            Date = new DateOnly(2020, 1, 1).AddDays(i),
            Ticker = i < 2 ? "AAPL" : "MSFT",
        }).ToList();

        using var stream = new MemoryStream();
        await format.WriteAsync(stream, rows).ConfigureAwait(true);
        var bytes = stream.ToArray();

        TamperFirstColumnByteLength(bytes, newByteLength: long.MaxValue);
        RepairFooterCrc(bytes);

        using var corrupted = new MemoryStream(bytes);

        var act = async () =>
        {
            await foreach (var _ in new CompressedColumnarSegmentFormat<NarrowBar>().ReadBatchesAsync(
                corrupted,
                new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31),
                ColumnProjection.All))
            {
            }
        };

        await act.Should().ThrowAsync<TruncatedSegmentException>(
            "a column byteLength exceeding the file must be rejected before allocating, not surface OverflowException");
    }

    [Fact]
    public async Task ReadRowGroupsAsync_NegativeColumnByteLength_ThrowsCorruptSegment()
    {
        var format = new CompressedColumnarSegmentFormat<NarrowBar>();
        var rows = Enumerable.Range(0, 4).Select(i => new NarrowBar
        {
            Date = new DateOnly(2020, 1, 1).AddDays(i),
            Ticker = i < 2 ? "AAPL" : "MSFT",
        }).ToList();

        using var stream = new MemoryStream();
        await format.WriteAsync(stream, rows).ConfigureAwait(true);
        var bytes = stream.ToArray();

        // A negative declared length is bit-rot, not a plausible mid-flush truncation.
        TamperFirstColumnByteLength(bytes, newByteLength: -1);
        RepairFooterCrc(bytes);

        using var corrupted = new MemoryStream(bytes);

        var act = async () =>
        {
            await foreach (var _ in new CompressedColumnarSegmentFormat<NarrowBar>().ReadRowGroupsAsync(
                corrupted,
                new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31)))
            {
            }
        };

        await act.Should().ThrowAsync<CorruptSegmentException>(
            "a negative column byteLength is corruption and must be rejected before allocating");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Helpers (mirrors ColumnarAllocationAndColumnMismatchTests footer-walk layout)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Overwrites the first column descriptor's <c>byteLength</c> int64 field in the footer
    /// payload. Per-column layout: nameLen(2) + name + tag(1) + codec(1) + type(1) +
    /// offset(8) + length(8).
    /// </summary>
    private static void TamperFirstColumnByteLength(byte[] bytes, long newByteLength)
    {
        var (payloadStart, _) = LocateFooterPayload(bytes);
        var payload = bytes.AsSpan(payloadStart);

        // magic(4) + version(4) + columnCount(2); then first column descriptor follows.
        var cursor = 4 + 4 + 2;
        var nameLen = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(cursor, 2));
        // Advance past nameLen(2) + name + tag(1) + codec(1) + type(1) + offset(8) to reach length(8).
        var lengthFieldOffset = cursor + 2 + nameLen + 3 + 8;
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(
            bytes.AsSpan(payloadStart + lengthFieldOffset, 8), newByteLength);
    }

    private static (int PayloadStart, int PayloadLength) LocateFooterPayload(byte[] bytes)
    {
        var trailerStart = bytes.Length - 8;
        var footerLength = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(trailerStart, 4));
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
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(crcOffset, 4), crc);
    }
}
