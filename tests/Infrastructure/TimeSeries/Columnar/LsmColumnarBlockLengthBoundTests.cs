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
using Boutquin.Storage.Domain.ValueObjects.TimeSeries;
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar;

namespace Boutquin.Storage.Infrastructure.Tests.TimeSeries.Columnar;

/// <summary>
/// RX2 — LSMCOLUMNAR-BLOCKLEN-ALLOC. <c>LsmColumnarSegment.ReadBlockBytesAsync</c> seeks to a
/// footer-supplied <c>BlockOffset</c> and allocates <c>new byte[block.BlockLength]</c> with no
/// bound against the file length. The footer CRC covers only the footer body and is forgeable, so
/// a tampered <c>BlockLength</c> drives an over-allocation before <c>ReadExactlyAsync</c> can fail.
/// The projection-pushdown sibling path already validates the same span via
/// <see cref="WindowReadStream"/>'s constructor (<c>offset + length &gt; baseStream.Length</c>); this
/// non-projected read path must reject the overrun with the same <see cref="TruncatedSegmentException"/>
/// the inner format's <c>ValidateColumnExtent</c> uses, BEFORE the allocation.
/// </summary>
public sealed class LsmColumnarBlockLengthBoundTests : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), $"colblocklen-{Guid.NewGuid():N}");

    public LsmColumnarBlockLengthBoundTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            try { Directory.Delete(_folder, recursive: true); } catch { /* best-effort */ }
        }
    }

    private static ColumnarSegmentFormat<SerializableWrapper<string>, MarketDataBar> Format()
        => new(new CompressedColumnarSegmentFormat<MarketDataBar>());

    private static MarketDataBar Bar(DateOnly date, decimal close) => new()
    {
        Date = date,
        Open = close - 1m,
        High = close + 1m,
        Low = close - 2m,
        Close = close,
        Volume = 1000,
        Vwap = close,
        TradeCount = 1,
        Dividend = 0m,
        SplitRatio = 1m,
        AdjustedClose = close,
        SourceFlags = 0,
    };

    private static TimeSeriesKey<SerializableWrapper<string>> Key(string ds, DateOnly d)
        => new(new SerializableWrapper<string>(ds), d);

    private static SegmentWriteEntry<TimeSeriesKey<SerializableWrapper<string>>, MarketDataBar> Entry(
        string ds, DateOnly d, decimal close)
        => SegmentWriteEntry<TimeSeriesKey<SerializableWrapper<string>>, MarketDataBar>.Inline(
            Key(ds, d), Bar(d, close), 0UL);

    [Fact]
    public async Task ReadBlockBytes_BlockLengthOverrunsFile_ThrowsBeforeAllocating()
    {
        // Arrange — a single-dataset segment on disk.
        var d1 = new DateOnly(2024, 1, 1);
        var path = Path.Combine(_folder, "blocklen.bsct");
        await Format().WriteAsync(_folder, "blocklen.bsct", new[] { Entry("AAPL", d1, 100m) });

        var bytes = await File.ReadAllBytesAsync(path);

        // Tamper: inflate the first block's BlockLength to a value that overruns the file, then
        // repair the footer CRC so the read reaches the allocation. long.MaxValue would overflow a
        // naive offset+length add; the guard must be written overflow-safe, so test the largest
        // overrun. The value is never actually allocated — the bound must fire first.
        TamperFirstBlockLength(bytes, newBlockLength: long.MaxValue);
        RepairFooterCrc(bytes);
        await File.WriteAllBytesAsync(path, bytes);

        var reader = Format().Open(_folder, "blocklen.bsct");

        // Act + Assert — the point lookup reaches ReadBlockBytesAsync; a BlockLength overrunning the
        // file must surface as a typed truncation exception raised BEFORE new byte[BlockLength].
        var act = async () => await reader.TryGetBlockEntryAsync(Key("AAPL", d1));

        await act.Should().ThrowAsync<TruncatedSegmentException>(
            "a block length that overruns the file must be rejected before allocating the block buffer");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Helpers — footer body layout (ParseFooter): datasetCount(u32) + maxSeqno(u64, R2-G), then
    //   per block: keyLen(u32) + key + BlockOffset(i64) + BlockLength(i64) + RowCount(i32) +
    //   MinDate(i32) + MaxDate(i32). Trailer: footerLength(u32) + magic(u32). CRC(u32) sits
    //   immediately before the trailer and covers the footer body only.
    // ═══════════════════════════════════════════════════════════════════════════

    private static void TamperFirstBlockLength(byte[] bytes, long newBlockLength)
    {
        var (payloadStart, _) = LocateFooterPayload(bytes);
        var payload = bytes.AsSpan(payloadStart);

        var cursor = 4 + 8; // datasetCount(u32) + segment-level maxSeqno(u64, R2-G)
        var keyLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(cursor, 4));
        // Advance past keyLen(4) + key + BlockOffset(8) to reach BlockLength(8).
        var blockLengthFieldOffset = cursor + 4 + keyLen + 8;
        BinaryPrimitives.WriteInt64LittleEndian(
            bytes.AsSpan(payloadStart + blockLengthFieldOffset, 8), newBlockLength);
    }

    /// <summary>
    /// Returns the start offset and length of the CRC-covered footer body (everything between
    /// the footer offset and the 4-byte CRC suffix).
    /// </summary>
    private static (int PayloadStart, int PayloadLength) LocateFooterPayload(byte[] bytes)
    {
        // Trailer: last 8 bytes = footerLength(u32) + magic(u32).
        var trailerStart = bytes.Length - 8;
        var footerLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(trailerStart, 4));
        // The footer (footerLength bytes) ends right before the trailer and includes the CRC suffix.
        var footerOffset = trailerStart - footerLength;
        var payloadLength = footerLength - 4; // strip the 4-byte CRC suffix
        return (footerOffset, payloadLength);
    }

    private static void RepairFooterCrc(byte[] bytes)
    {
        var (payloadStart, payloadLength) = LocateFooterPayload(bytes);
        var payload = bytes.AsSpan(payloadStart, payloadLength).ToArray();
        var crc = Crc32C.HashToUInt32(payload);
        var crcOffset = payloadStart + payloadLength;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(crcOffset, 4), crc);
    }
}
