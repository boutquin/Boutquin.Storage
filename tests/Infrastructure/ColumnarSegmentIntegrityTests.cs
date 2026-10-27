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
using Boutquin.Storage.Infrastructure.Tests.TimeSeries.Columnar;
using Boutquin.Storage.Infrastructure.TimeSeries;
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar;
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar.Footer;

namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// Fail-closed integrity of the columnar segment read paths. Every columnar segment carries a
/// per-row-group CRC32C over the row group's decoded values; a read that materialises every
/// column of a row group must verify it and throw <see cref="CorruptSegmentException"/> on a
/// mismatch rather than serve the corrupted values — and compaction, which re-reads segments
/// through that path, must therefore refuse to rewrite corrupt input into a freshly stamped
/// segment. A forged footer whose row groups are not contiguous must likewise be rejected with
/// the typed corruption exception rather than leaking a raw index exception.
/// </summary>
public sealed class ColumnarSegmentIntegrityTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), $"ColumnarIntegrity_{Guid.NewGuid():N}");

    public ColumnarSegmentIntegrityTests()
    {
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            try { Directory.Delete(_folder, recursive: true); } catch { /* best-effort */ }
        }
    }

    private static List<ColumnarBar> TwelveBars() => Enumerable.Range(0, 12).Select(i => new ColumnarBar
    {
        Date = new DateOnly(2026, 1, 1).AddDays(i),
        Ticker = "AAPL",
        Close = 100m + i,
        Volume = 1_000_000L + i,
        TradeCount = 100 + i,
        Vwap = 100.0 + i,
        IsAdjusted = (i % 2) == 0,
        Residual = i * 0.001,
    }).ToList();

    // Writes 12 rows in 3 row groups of 4 and flips one byte of row group 1's RawDouble Residual
    // span. RawDouble has no framing, so the flip decodes to a different double without any codec
    // error — only the per-row-group CRC can detect it.
    private static async Task<byte[]> WriteSegmentWithCorruptRowGroupOneAsync(
        CompressedColumnarSegmentFormat<ColumnarBar> format)
    {
        using var stream = new MemoryStream();
        await format.WriteAsync(stream, TwelveBars()).ConfigureAwait(true);
        var bytes = stream.ToArray();

        var footer = FooterReader.Read(new MemoryStream(bytes));
        var residual = footer.Columns.Single(c => c.Name == nameof(ColumnarBar.Residual));
        bytes[(int)residual.ByteOffset + 4 * 8] ^= 0x55;
        return bytes;
    }

    [Fact]
    public async Task ReadAsync_AllColumns_CorruptRowGroup_ThrowsCorruptSegmentException()
    {
        var format = new CompressedColumnarSegmentFormat<ColumnarBar>(rowGroupMaxRows: 4);
        var bytes = await WriteSegmentWithCorruptRowGroupOneAsync(format).ConfigureAwait(true);

        var act = async () =>
        {
            await foreach (var _ in format.ReadAsync(
                new MemoryStream(bytes), new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31),
                ColumnProjection.All).ConfigureAwait(true))
            {
            }
        };

        (await act.Should().ThrowAsync<CorruptSegmentException>().ConfigureAwait(true))
            .Which.Message.Should().Contain("row group 1");
    }

    [Fact]
    public async Task ReadAsync_AllColumns_IntactRowGroupsBeforeCorruptOne_AreYieldedThenReadFails()
    {
        // Row group 0 is intact and is served; the read fails closed at row group 1 instead of
        // serving its tampered Residual.
        var format = new CompressedColumnarSegmentFormat<ColumnarBar>(rowGroupMaxRows: 4);
        var bytes = await WriteSegmentWithCorruptRowGroupOneAsync(format).ConfigureAwait(true);
        var expected = TwelveBars();

        var served = new List<ColumnarBar>();
        var act = async () =>
        {
            await foreach (var r in format.ReadAsync(
                new MemoryStream(bytes), new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31),
                ColumnProjection.All).ConfigureAwait(true))
            {
                served.Add(r);
            }
        };

        await act.Should().ThrowAsync<CorruptSegmentException>().ConfigureAwait(true);
        served.Should().Equal(expected.Take(4));
    }

    [Fact]
    public async Task ReadAsync_SubsetProjection_IsNotCrcVerifiedAndKeepsItsColumnSkip()
    {
        // The CRC covers every column, so a strict subset projection cannot evaluate it without
        // decoding the excluded columns. Such a read keeps the projection's column skip: it serves
        // the projected (intact) Close values of every row group, corrupt Residual notwithstanding.
        var format = new CompressedColumnarSegmentFormat<ColumnarBar>(rowGroupMaxRows: 4);
        var bytes = await WriteSegmentWithCorruptRowGroupOneAsync(format).ConfigureAwait(true);

        var closes = new List<decimal>();
        await foreach (var r in format.ReadAsync(
            new MemoryStream(bytes), new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31),
            new ColumnProjection([nameof(ColumnarBar.Date), nameof(ColumnarBar.Close)])).ConfigureAwait(true))
        {
            closes.Add(r.Close);
        }

        closes.Should().Equal(TwelveBars().Select(b => b.Close));
    }

    [Fact]
    public async Task ReadAsync_AllColumns_IntactSegment_RoundTrips()
    {
        var format = new CompressedColumnarSegmentFormat<ColumnarBar>(rowGroupMaxRows: 4);
        using var stream = new MemoryStream();
        await format.WriteAsync(stream, TwelveBars()).ConfigureAwait(true);
        stream.Position = 0;

        var rows = new List<ColumnarBar>();
        await foreach (var r in format.ReadAsync(
            stream, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), ColumnProjection.All)
            .ConfigureAwait(true))
        {
            rows.Add(r);
        }

        rows.Should().Equal(TwelveBars());
    }

    [Fact]
    public async Task Engine_CorruptionAfterOpen_GetRangeAndCompactionFailClosed()
    {
        await using var stack = await ColumnarTimeSeriesEngineBuilder.BuildAsync<SerializableWrapper<string>, MarketDataBar>(
            segmentFolder: _folder, segmentPrefix: "ts");

        var ds = new SerializableWrapper<string>("AAPL");
        var d0 = new DateOnly(2024, 1, 1);
        // Close carries one value no other row or column shares, so its 16-byte dictionary
        // entry is locatable in the segment file.
        const decimal markerClose = 98765.4321m;
        var bars = Enumerable.Range(0, 10)
            .Select(i => new MarketDataBar { Date = d0.AddDays(i), Close = i == 0 ? markerClose : 100m + i, SplitRatio = 1m })
            .ToList();
        await stack.Store.AppendAsync(ds, bars).ConfigureAwait(true);
        await stack.InnerEngine.FlushAsync().ConfigureAwait(true);

        // Corrupt the segment on disk while the stack is open: the engine's whole-file check ran at
        // open only, so the per-row-group CRC is the only remaining defence.
        var segmentPath = Directory.GetFiles(_folder, "*.bsct").Single();
        var fileBytes = await File.ReadAllBytesAsync(segmentPath).ConfigureAwait(true);
        var marker = DecimalBytes(markerClose);
        var at = fileBytes.AsSpan().IndexOf(marker);
        at.Should().BeGreaterThan(0, "the marker Close value's dictionary entry must be present");
        fileBytes[at] ^= 0x01;
        await File.WriteAllBytesAsync(segmentPath, fileBytes).ConfigureAwait(true);

        var read = async () =>
        {
            await foreach (var _ in stack.Store.GetRangeAsync(ds, d0, d0.AddDays(20), ColumnProjection.All)
                .ConfigureAwait(true))
            {
            }
        };
        await read.Should().ThrowAsync<CorruptSegmentException>(
            "a read after open must not serve a row group whose CRC no longer matches").ConfigureAwait(true);

        // A second segment makes compaction re-read the corrupt one; it must refuse rather than
        // rewrite the corrupted value into a freshly CRC-stamped segment.
        await stack.Store.AppendAsync(ds, [new MarketDataBar { Date = d0.AddDays(30), Close = 999m, SplitRatio = 1m }])
            .ConfigureAwait(true);
        await stack.InnerEngine.FlushAsync().ConfigureAwait(true);

        var compact = async () => await stack.InnerEngine.CompactAsync().ConfigureAwait(true);
        await compact.Should().ThrowAsync<CorruptSegmentException>(
            "compaction must not launder corrupt input into a new segment").ConfigureAwait(true);
    }

    [Fact]
    public async Task ReadRowGroupsAsync_ForgedNonContiguousFirstRowIndex_ThrowsCorruptSegmentException()
    {
        var format = new CompressedColumnarSegmentFormat<ColumnarBar>(rowGroupMaxRows: 4);
        using var stream = new MemoryStream();
        await format.WriteAsync(stream, TwelveBars().Take(8).ToList()).ConfigureAwait(true);
        var bytes = stream.ToArray();
        var footer = FooterReader.Read(new MemoryStream(bytes));
        footer.RowGroups.Should().HaveCount(2);

        // Re-emit a CRC-valid footer whose second row group starts at row 6 instead of row 4.
        var forgedRowGroups = footer.RowGroups.ToArray();
        forgedRowGroups[1] = forgedRowGroups[1] with { FirstRowIndex = 6 };
        var forged = RebuildWithFooter(bytes, footer with { RowGroups = forgedRowGroups });

        var act = async () =>
        {
            await foreach (var _ in format.ReadRowGroupsAsync(
                new MemoryStream(forged), new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31))
                .ConfigureAwait(true))
            {
            }
        };

        await act.Should().ThrowAsync<CorruptSegmentException>().ConfigureAwait(true);
    }

    [Fact]
    public async Task ReadAsync_AllColumns_ForgedColumnFieldType_ThrowsCorruptSegmentException()
    {
        // The CRC check hashes decoded values by the footer's declared field type. A CRC-valid
        // forged footer that misdeclares a column's type must surface the typed corruption
        // exception, not a raw cast failure from the hashing step.
        var format = new CompressedColumnarSegmentFormat<ColumnarBar>(rowGroupMaxRows: 4);
        using var stream = new MemoryStream();
        await format.WriteAsync(stream, TwelveBars()).ConfigureAwait(true);
        var bytes = stream.ToArray();
        var footer = FooterReader.Read(new MemoryStream(bytes));
        var columns = footer.Columns.ToArray();
        var tradeCount = Array.FindIndex(columns, c => c.Name == nameof(ColumnarBar.TradeCount));
        columns[tradeCount] = columns[tradeCount] with { FieldType = ColumnFieldTypeCode.Int64 };
        var forged = RebuildWithFooter(bytes, footer with { Columns = columns });

        var read = async () =>
        {
            await foreach (var _ in format.ReadAsync(
                new MemoryStream(forged), new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31),
                ColumnProjection.All).ConfigureAwait(true))
            {
            }
        };
        var readRowGroups = async () =>
        {
            await foreach (var _ in format.ReadRowGroupsAsync(
                new MemoryStream(forged), new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31)).ConfigureAwait(true))
            {
            }
        };

        await read.Should().ThrowAsync<CorruptSegmentException>().ConfigureAwait(true);
        await readRowGroups.Should().ThrowAsync<CorruptSegmentException>().ConfigureAwait(true);
    }

    internal static byte[] RebuildWithFooter(byte[] segment, SegmentFooter footer)
    {
        var oldFooterLength = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
            segment.AsSpan(segment.Length - SegmentMagic.TrailerSize, 4));
        var columnDataEnd = segment.Length - SegmentMagic.TrailerSize - SegmentMagic.FooterCrcSize - (int)oldFooterLength;
        using var rebuilt = new MemoryStream();
        rebuilt.Write(segment, 0, columnDataEnd);
        FooterWriter.Write(rebuilt, footer);
        return rebuilt.ToArray();
    }

    private static byte[] DecimalBytes(decimal value)
    {
        var bits = decimal.GetBits(value);
        var buffer = new byte[16];
        for (var i = 0; i < 4; i++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(i * 4, 4), bits[i]);
        }

        return buffer;
    }
}
