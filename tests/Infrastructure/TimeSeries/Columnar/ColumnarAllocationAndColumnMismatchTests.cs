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
/// Fix-3: BitmapIndex extension section with a huge declared <c>valueCount</c> but short
/// remaining bytes must throw a typed decode exception rather than pre-allocating a
/// multi-GB array.
///
/// Fix-5: <c>ReadRowGroupsAsync</c> and <c>ReadBatchesAsync</c> must throw
/// <see cref="ColumnNotFoundException"/> when the segment footer declares a column
/// that the format instance's column plan does not recognise, consistent with
/// the fail-fast contract established in the sibling column-mismatch handling.
/// </summary>
public sealed class ColumnarAllocationAndColumnMismatchTests
{
    // ═══════════════════════════════════════════════════════════════════════════
    // Fix 3: BitmapIndex huge valueCount — no large pre-allocation
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task BitmapIndex_HugeValueCount_ShortPayload_ThrowsDecodeException_NotOOM()
    {
        // Arrange — write a valid segment with a bitmap index.
        var format = new CompressedColumnarSegmentFormat<IndexableBar>();
        var rows = Enumerable.Range(0, 4).Select(i => new IndexableBar
        {
            Date = new DateOnly(2020, 1, 1).AddDays(i),
            Ticker = i < 2 ? "AAPL" : "MSFT",
        }).ToList();

        using var stream = new MemoryStream();
        await format.WriteAsync(stream, rows).ConfigureAwait(true);
        var bytes = stream.ToArray();

        // Tamper: locate the BitmapIndex extension section's valueCount field for the
        // first entry and replace it with 0x00FFFFFF (≈16 M), then repair the CRC.
        // The actual payload only has a few bytes remaining — EnsureRemaining will throw
        // a CorruptSegmentException before any allocation of that size.
        TamperBitmapIndexValueCount(bytes, newValueCount: 0x00FF_FFFFu);
        RepairFooterCrc(bytes);

        using var corrupted = new MemoryStream(bytes);

        // Act + Assert — should throw a decode error, NOT OutOfMemoryException.
        var act = () => format.ReadBitmapIndexes(corrupted);
        act.Should().Throw<Exception>()
            .And.Should().Match(ex =>
                ex is CorruptSegmentException || ex is OverflowException,
                "a huge declared count with short payload must fail with a decode error");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Fix 5: ReadRowGroupsAsync — column mismatch throws ColumnNotFoundException
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ReadRowGroupsAsync_ColumnMismatch_ThrowsColumnNotFoundException()
    {
        // Arrange — write a ColumnarBar segment (many columns).
        var writeFormat = new CompressedColumnarSegmentFormat<ColumnarBar>();
        var rows = Enumerable.Range(0, 5).Select(MakeColumnarBar).ToList();

        using var stream = new MemoryStream();
        await writeFormat.WriteAsync(stream, rows).ConfigureAwait(true);
        stream.Position = 0;

        // Read with NarrowBar format — its column plan only knows Date + Ticker.
        // The footer will declare Close, Volume, etc., which NarrowBar doesn't know.
        var readFormat = new CompressedColumnarSegmentFormat<ColumnarBar>();
        // To trigger the mismatch we need a reader whose _columns list doesn't include
        // a column the footer declares. The simplest way: write ColumnarBar but attempt
        // to read rows of NarrowBar (different column plan).
        var narrowFormat = new CompressedColumnarSegmentFormat<NarrowBar>();

        stream.Position = 0;
        var act = async () =>
        {
            await foreach (var _ in narrowFormat.ReadRowGroupsAsync(
                stream,
                new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31)))
            {
            }
        };

        await act.Should().ThrowAsync<ColumnNotFoundException>()
            .WithMessage("*not present*", "the exception should name the missing column");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Fix 5: ReadBatchesAsync — column mismatch throws ColumnNotFoundException
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ReadBatchesAsync_ColumnMismatch_ThrowsColumnNotFoundException()
    {
        // Arrange — write a ColumnarBar segment, read with NarrowBar format.
        var writeFormat = new CompressedColumnarSegmentFormat<ColumnarBar>();
        var rows = Enumerable.Range(0, 5).Select(MakeColumnarBar).ToList();

        using var stream = new MemoryStream();
        await writeFormat.WriteAsync(stream, rows).ConfigureAwait(true);
        stream.Position = 0;

        var narrowFormat = new CompressedColumnarSegmentFormat<NarrowBar>();

        stream.Position = 0;
        var act = async () =>
        {
            await foreach (var _ in narrowFormat.ReadBatchesAsync(
                stream,
                new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31),
                ColumnProjection.All))
            {
            }
        };

        await act.Should().ThrowAsync<ColumnNotFoundException>()
            .WithMessage("*not present*");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════════════

    private static ColumnarBar MakeColumnarBar(int n) => new()
    {
        Date = new DateOnly(2020, 1, 1).AddDays(n),
        Ticker = "AAPL",
        Close = 100m + (n * 0.25m),
        Volume = 1_000_000L + (n * 1000L),
        TradeCount = 1000 + n,
        Vwap = 100.0 + (n * 0.25),
        IsAdjusted = (n % 2) == 0,
        Residual = n * 0.001,
    };

    /// <summary>
    /// Locates the first BitmapIndex extension section in the footer payload and overwrites
    /// the first entry's <c>valueCount</c> uint32 field with <paramref name="newValueCount"/>.
    /// </summary>
    private static void TamperBitmapIndexValueCount(byte[] bytes, uint newValueCount)
    {
        var (payloadStart, _) = LocateFooterPayload(bytes);
        var payload = bytes.AsSpan(payloadStart);

        // Parse enough of the payload to reach the BitmapIndex extension section.
        // Layout: magic(4) + version(4) + columnCount(2)
        var cursor = 4 + 4;
        var columnCount = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(cursor, 2));
        cursor += 2;
        // Skip all column descriptors: per-col = 2(nameLen) + nameLen + 3(tag+codec+type) + 8(offset) + 8(len)
        for (var c = 0; c < columnCount; c++)
        {
            var nameLen = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(cursor, 2));
            cursor += 2 + nameLen + 3 + 8 + 8;
        }
        // Skip row groups: rowGroupCount(2) then per-rg = 4*4 = 16 bytes
        var rgCount = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(cursor, 2));
        cursor += 2 + (rgCount * 16);
        // extensionCount(1)
        var extCount = payload[cursor++];
        // Walk extensions until BitmapIndex kind (0x02 per FooterExtensionKind)
        for (var ext = 0; ext < extCount; ext++)
        {
            var kind = payload[cursor++];
            var sectionLen = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(cursor, 4));
            cursor += 4;
            if (kind == 0x02 /* BitmapIndexes */)
            {
                // Section layout: entryCount(2), then per-entry: nameLen(2) + name + valueCount(4) + ...
                var entryCount = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(cursor, 2));
                if (entryCount > 0)
                {
                    var sectionCursor = cursor + 2;
                    var firstNameLen = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(sectionCursor, 2));
                    sectionCursor += 2 + firstNameLen;
                    // Overwrite valueCount at payloadStart + sectionCursor.
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
                        bytes.AsSpan(payloadStart + sectionCursor, 4), newValueCount);
                }
                return;
            }
            cursor += sectionLen;
        }
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
