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

using Key = Boutquin.Storage.Domain.ValueObjects.TimeSeries.TimeSeriesKey<Boutquin.Storage.Domain.Helpers.SerializableWrapper<string>>;

namespace Boutquin.Storage.Infrastructure.Tests.TimeSeries.Columnar;

/// <summary>
/// F-V1-01 (outer-envelope block + tombstone-key sites) — a forged-but-CRC-valid <c>.bsct</c>
/// footer whose block-index <c>minDn</c>/<c>maxDn</c> (or tombstone-key day number) fall outside
/// the <see cref="DateOnly"/> range must surface a TYPED <see cref="CorruptSegmentException"/> out
/// of the public read API (<c>GetAllItemsAsync</c> → <c>LoadIndex</c> → <c>ParseFooter</c>), not a
/// raw <see cref="ArgumentOutOfRangeException"/> from <see cref="DateOnly.FromDayNumber(int)"/>.
/// </summary>
public sealed class ColumnarSegmentFormatDayNumberRangeTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        $"ColumnarSegmentFormatDayNumberRangeTests_{Guid.NewGuid():N}");

    public ColumnarSegmentFormatDayNumberRangeTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private static ColumnarSegmentFormat<SerializableWrapper<string>, MarketDataBar> NewFormat()
    {
        var inner = new CompressedColumnarSegmentFormat<MarketDataBar>();
        return new ColumnarSegmentFormat<SerializableWrapper<string>, MarketDataBar>(inner);
    }

    private static (TimeSeriesKey<SerializableWrapper<string>> Key, MarketDataBar Value, ulong SequenceNumber) Kv(
        string dataset, DateOnly date, decimal close)
        => (
            new TimeSeriesKey<SerializableWrapper<string>>(new SerializableWrapper<string>(dataset), date),
            new MarketDataBar { Date = date, Close = close, Open = close, High = close, Low = close, SplitRatio = 1m },
            0UL);

    [Fact]
    public async Task Open_ForgedBlockMinDayNumber_ThrowsCorruptSegment()
    {
        var format = NewFormat();
        var d0 = new DateOnly(2024, 1, 1);
        var entries = new List<(TimeSeriesKey<SerializableWrapper<string>> Key, MarketDataBar Value, ulong SequenceNumber)>
        {
            Kv("AAPL", d0, 1m),
        };

        _ = await format.WriteAsync(_folder, "seg_dnrange.bsct", entries);
        var path = Path.Combine(_folder, "seg_dnrange.bsct");

        // Forge the single block's minDn to int.MaxValue (well past the DateOnly ceiling), then
        // repair the outer footer CRC so the read reaches the unguarded FromDayNumber.
        ForgeFirstBlockMinDayNumber(path, int.MaxValue);

        var reader = format.Open(_folder, "seg_dnrange.bsct");

        var act = () => reader.GetAllItemsAsync().GetAwaiter().GetResult();
        act.Should().Throw<CorruptSegmentException>(
            "a forged block day-number outside the DateOnly range must surface a typed corruption exception");
    }

    [Fact]
    public async Task Open_ValidSegment_StillReadsUnchanged()
    {
        // Regression guard — the day-number guard must not reject legitimate in-range blocks.
        var format = NewFormat();
        var d0 = new DateOnly(2024, 1, 1);
        var entries = new List<(TimeSeriesKey<SerializableWrapper<string>> Key, MarketDataBar Value, ulong SequenceNumber)>
        {
            Kv("AAPL", d0, 1m),
            Kv("MSFT", d0, 10m),
        };

        _ = await format.WriteAsync(_folder, "seg_dnvalid.bsct", entries);

        var reopened = format.Open(_folder, "seg_dnvalid.bsct");
        var all = (await reopened.GetAllItemsAsync()).ToList();
        all.Should().HaveCount(2, "a valid segment must still read after the day-number guard is added");
    }

    // ─── Forged dataset / tombstone keys and block-index order ──────────────────
    // A key decoded from a forged-but-CRC-valid footer fails inside the key deserialiser, whose own
    // corruption type is InvalidDataException / EndOfStreamException; the envelope reader must surface
    // its typed CorruptSegmentException like the block day-number guard beside it.

    [Fact]
    public async Task Open_ForgedTombstoneKeyDayNumber_ThrowsCorruptSegment()
    {
        var path = await WriteTwoDatasetSegmentWithTombstoneAsync("seg_tombdn.bsct");
        ForgeFooter(path, (bytes, layout) =>
            BinaryPrimitives.WriteInt32LittleEndian(
                bytes.AsSpan(layout.TombstoneKeys[0].Start + layout.TombstoneKeys[0].Length - 4, 4), int.MaxValue));

        var reader = NewFormat().Open(_folder, "seg_tombdn.bsct");

        var act = () => reader.GetTombstoneSeqnos();
        act.Should().Throw<CorruptSegmentException>("a forged tombstone key is footer corruption");
    }

    [Fact]
    public async Task Open_ForgedTombstoneDatasetKeyLength_ThrowsCorruptSegment()
    {
        var path = await WriteTwoDatasetSegmentWithTombstoneAsync("seg_tomblen.bsct");
        ForgeFooter(path, (bytes, layout) => bytes[layout.TombstoneKeys[0].Start] = 0x7F);

        var reader = NewFormat().Open(_folder, "seg_tomblen.bsct");

        var act = () => reader.GetTombstoneSeqnos();
        act.Should().Throw<CorruptSegmentException>("a forged tombstone key is footer corruption");
    }

    [Fact]
    public async Task Open_ForgedBlockDatasetKeyLength_ThrowsCorruptSegment()
    {
        var path = await WriteTwoDatasetSegmentWithTombstoneAsync("seg_blocklen.bsct");
        ForgeFooter(path, (bytes, layout) => bytes[layout.BlockKeys[0].Start] = 0x7F);

        var reader = NewFormat().Open(_folder, "seg_blocklen.bsct");

        var act = () => reader.GetAllItemsAsync().GetAwaiter().GetResult();
        act.Should().Throw<CorruptSegmentException>("a forged block dataset key is footer corruption");
    }

    // The writer emits dataset blocks in strictly ascending key order, and every scan relies on it
    // (the LSM k-way merge and the range-scan early break need an ascending source). A footer whose
    // block index is out of order, or names one dataset twice, can only be corruption.

    [Fact]
    public async Task Open_SwappedBlockIndexEntries_ThrowsCorruptSegment()
    {
        var path = await WriteTwoDatasetSegmentWithTombstoneAsync("seg_swapped.bsct");
        ForgeFooter(path, (bytes, layout) =>
        {
            var first = layout.BlockEntries[0];
            var second = layout.BlockEntries[1];
            var a = bytes.AsSpan(first.Start, first.Length).ToArray();
            var b = bytes.AsSpan(second.Start, second.Length).ToArray();
            b.CopyTo(bytes, first.Start);
            a.CopyTo(bytes, first.Start + b.Length);
        });

        var reader = NewFormat().Open(_folder, "seg_swapped.bsct");

        var act = () => reader.GetAllItemsAsync().GetAwaiter().GetResult();
        act.Should().Throw<CorruptSegmentException>("a descending block index breaks the reader's ascending contract");
    }

    [Fact]
    public async Task Open_DuplicatedBlockDatasetKey_ThrowsCorruptSegment()
    {
        var path = await WriteTwoDatasetSegmentWithTombstoneAsync("seg_dupkey.bsct");
        ForgeFooter(path, (bytes, layout) =>
            bytes.AsSpan(layout.BlockKeys[0].Start, layout.BlockKeys[0].Length)
                .CopyTo(bytes.AsSpan(layout.BlockKeys[1].Start, layout.BlockKeys[1].Length)));

        var reader = NewFormat().Open(_folder, "seg_dupkey.bsct");

        var act = () => reader.GetAllItemsAsync().GetAwaiter().GetResult();
        act.Should().Throw<CorruptSegmentException>("one dataset named by two blocks is footer corruption");
    }

    [Fact]
    public async Task Open_PristineTwoDatasetSegmentWithTombstone_ReadsInAscendingOrder()
    {
        // Regression guard for the key and order checks: an unforged segment still reads, ascending.
        await WriteTwoDatasetSegmentWithTombstoneAsync("seg_pristine.bsct");

        var reader = NewFormat().Open(_folder, "seg_pristine.bsct");

        reader.GetTombstoneSeqnos().Should().ContainSingle().Which.Value.Should().Be(7UL);
        var keys = (await reader.GetAllItemsAsync()).Select(kv => kv.Key).ToList();
        keys.Should().HaveCount(3).And.BeInAscendingOrder();
    }

    private async Task<string> WriteTwoDatasetSegmentWithTombstoneAsync(string fileName)
    {
        // Equal-length dataset keys ("a", "b") so two block-index entries can be swapped in place.
        var d0 = new DateOnly(2024, 1, 2);
        Key Ts(string dataset, DateOnly date) => new(new SerializableWrapper<string>(dataset), date);
        SegmentWriteEntry<Key, MarketDataBar> Row(string dataset, DateOnly date, ulong seqno)
            => SegmentWriteEntry<Key, MarketDataBar>.Inline(
                Ts(dataset, date),
                new MarketDataBar { Date = date, Close = 1m, Open = 1m, High = 1m, Low = 1m, SplitRatio = 1m },
                seqno);

        var entries = new[] { Row("a", d0, 1UL), Row("a", d0.AddDays(1), 2UL), Row("b", d0, 3UL) };
        var tombstones = new Dictionary<Key, ulong> { [Ts("c", d0.AddDays(1))] = 7UL };
        await NewFormat().WriteAsync(_folder, fileName, entries, tombstones);
        return Path.Combine(_folder, fileName);
    }

    private sealed record FooterSpan(int Start, int Length);

    private sealed record FooterLayout(
        IReadOnlyList<FooterSpan> BlockEntries,
        IReadOnlyList<FooterSpan> BlockKeys,
        IReadOnlyList<FooterSpan> TombstoneKeys);

    // Walks the outer footer (layout in the helper banner below), applies the forgery, and repairs
    // the footer CRC so the read gets past the integrity check to the field under test.
    private static void ForgeFooter(string path, Action<byte[], FooterLayout> forge)
    {
        var bytes = File.ReadAllBytes(path);
        var trailerStart = bytes.Length - 8;
        var footerLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(trailerStart, 4));
        var payloadStart = trailerStart - footerLength;
        var payloadLength = footerLength - 4;

        var blockEntries = new List<FooterSpan>();
        var blockKeys = new List<FooterSpan>();
        var tombstoneKeys = new List<FooterSpan>();
        var p = payloadStart;
        var datasetCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(p, 4));
        p += 4 + 8; // datasetCount + segment max seqno
        for (var i = 0; i < datasetCount; i++)
        {
            var entryStart = p;
            var keyLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(p, 4));
            blockKeys.Add(new FooterSpan(p + 4, keyLen));
            p += 4 + keyLen + 8 + 8 + 4 + 4 + 4; // key + offset + length + rowCount + minDn + maxDn
            blockEntries.Add(new FooterSpan(entryStart, p - entryStart));
        }

        var tombstoneCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(p, 4));
        p += 4;
        for (var i = 0; i < tombstoneCount; i++)
        {
            var keyLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(p, 4));
            tombstoneKeys.Add(new FooterSpan(p + 4, keyLen));
            p += 4 + keyLen + 8; // key + seqno
        }

        forge(bytes, new FooterLayout(blockEntries, blockKeys, tombstoneKeys));

        var crc = Boutquin.Storage.Infrastructure.Algorithms.Crc32C.HashToUInt32(bytes.AsSpan(payloadStart, payloadLength));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(payloadStart + payloadLength, 4), crc);
        File.WriteAllBytes(path, bytes);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Helpers — walk the outer footer payload to the first block's minDn field.
    // Payload layout: datasetCount(4) + maxSeqno(8) + [per block: keyLen(4) + key + blockOffset(8) +
    // blockLength(8) + rowCount(4) + minDn(4) + maxDn(4)] + tombstoneCount(4) + ...
    // File layout: ... [footer payload][crc(4)][trailer: footerLength(4) + magic(4)].
    // ═══════════════════════════════════════════════════════════════════════════

    private static void ForgeFirstBlockMinDayNumber(string path, int newMinDayNum)
    {
        var bytes = File.ReadAllBytes(path);

        // footerLength (in the trailer) counts the whole footerBytes block INCLUDING the trailing
        // 4-byte CRC. So: [footerBytes = payload || crc(4)][trailer(8)].
        var trailerStart = bytes.Length - 8;
        var footerLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(trailerStart, 4));
        var footerBytesStart = trailerStart - footerLength;
        var crcOffset = trailerStart - 4;          // CRC is the final 4 bytes of footerBytes
        var payloadStart = footerBytesStart;       // payload (sans CRC) begins here

        var p = payloadStart;
        p += 4; // datasetCount
        p += 8; // segment-level maxSeqno (R2-G), written immediately after datasetCount
        var keyLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(p, 4));
        p += 4 + keyLen; // keyLen + key bytes
        p += 8 + 8 + 4;  // blockOffset + blockLength + rowCount
        // p now points at minDn.
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(p, 4), newMinDayNum);

        // Recompute the footer CRC over the payload (footerBytes minus its 4-byte CRC suffix).
        var payload = bytes.AsSpan(payloadStart, footerLength - 4).ToArray();
        var crc = Boutquin.Storage.Infrastructure.Algorithms.Crc32C.HashToUInt32(payload);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(crcOffset, 4), crc);

        File.WriteAllBytes(path, bytes);
    }
}
