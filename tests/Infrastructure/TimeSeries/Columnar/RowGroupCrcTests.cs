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
using Boutquin.Storage.Domain.ValueObjects.TimeSeries;
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar;
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar.Footer;

namespace Boutquin.Storage.Infrastructure.Tests.TimeSeries.Columnar;

/// <summary>
/// Phase E3 — per-row-group CRC32C (Castagnoli) for quarantine-aware reads. Covers writer-side
/// CRC emission, reader-side <c>ReadRowGroupsAsync</c> that yields
/// <see cref="RowGroupReadResult{TRecord}"/>, and corruption-isolation behaviour when one row
/// group's CRC mismatches — the caller gets <c>Quarantined</c> for the bad group and
/// <c>Ok</c> for the intact neighbours.
/// </summary>
public sealed class RowGroupCrcTests
{
    private static ColumnarBar MakeBar(int y, int m, int d, int n) => new()
    {
        Date = new DateOnly(y, m, d),
        Ticker = "AAPL",
        Close = 100m + (n * 0.25m),
        Volume = 1_000_000L + (n * 1000L),
        TradeCount = 1000 + n,
        Vwap = 100.0 + (n * 0.25),
        IsAdjusted = (n % 2) == 0,
        Residual = n * 0.001,
    };

    [Fact]
    public async Task RowGroupReadResult_Ok_Factory_SurfacesRows()
    {
        var rows = new[] { MakeBar(2020, 1, 1, 0), MakeBar(2020, 1, 2, 1) };
        var result = RowGroupReadResult<ColumnarBar>.Ok(rowGroupIndex: 3, rows);

        result.IsOk.Should().BeTrue();
        result.RowGroupIndex.Should().Be(3);
        result.Rows.Should().Equal(rows);
        result.ExpectedCrc.Should().Be(0u);
        result.ActualCrc.Should().Be(0u);

        await Task.CompletedTask.ConfigureAwait(true);
    }

    [Fact]
    public void RowGroupReadResult_Quarantined_Factory_CarriesBothCrcs()
    {
        var result = RowGroupReadResult<ColumnarBar>.Quarantined(
            rowGroupIndex: 2, expectedCrc: 0xAABBCCDDu, actualCrc: 0x11223344u);

        result.IsOk.Should().BeFalse();
        result.RowGroupIndex.Should().Be(2);
        result.Rows.Should().BeNull();
        result.ExpectedCrc.Should().Be(0xAABBCCDDu);
        result.ActualCrc.Should().Be(0x11223344u);
    }

    [Fact]
    public async Task Writer_EmitsPerRowGroupCrc32C()
    {
        var format = new CompressedColumnarSegmentFormat<ColumnarBar>(rowGroupMaxRows: 4);
        var input = Enumerable.Range(0, 10).Select(i => MakeBar(2020, 1, 1 + i, i)).ToList();

        using var stream = new MemoryStream();
        await format.WriteAsync(stream, input).ConfigureAwait(true);
        stream.Position = 0;

        var footer = FooterReader.Read(stream);
        footer.RowGroupCrcs.Should().NotBeNull();
        footer.RowGroupCrcs!.Should().HaveCount(footer.RowGroups.Count);
    }

    [Fact]
    public async Task ReadRowGroupsAsync_OnIntactSegment_YieldsOkForEveryRowGroup()
    {
        var format = new CompressedColumnarSegmentFormat<ColumnarBar>(rowGroupMaxRows: 4);
        var input = Enumerable.Range(0, 10).Select(i => MakeBar(2020, 1, 1 + i, i)).ToList();

        using var stream = new MemoryStream();
        await format.WriteAsync(stream, input).ConfigureAwait(true);
        stream.Position = 0;

        var results = new List<RowGroupReadResult<ColumnarBar>>();
        await foreach (var r in format.ReadRowGroupsAsync(
            stream, new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31)).ConfigureAwait(true))
        {
            results.Add(r);
        }

        results.Should().HaveCount(3); // 10 rows / 4 per rg → 3 row groups (4, 4, 2)
        results.Should().OnlyContain(r => r.IsOk);
        results.SelectMany(r => r.Rows!).Should().Equal(input);
    }

    [Fact]
    public async Task ReadRowGroupsAsync_OnTamperedRowGroup_YieldsQuarantineForThatGroupOnly()
    {
        var format = new CompressedColumnarSegmentFormat<ColumnarBar>(rowGroupMaxRows: 4);
        var input = Enumerable.Range(0, 10).Select(i => MakeBar(2020, 1, 1 + i, i)).ToList();

        using var stream = new MemoryStream();
        await format.WriteAsync(stream, input).ConfigureAwait(true);

        // Tamper the footer's CRC for row group 1 to something unlikely to match.
        var bytes = stream.ToArray();
        var footerStream = new MemoryStream(bytes);
        var footer = FooterReader.Read(footerStream);
        var tamperedCrcs = footer.RowGroupCrcs!.ToArray();
        tamperedCrcs[1] ^= 0xFFFFFFFFu;
        var tamperedFooter = new SegmentFooter(
            footer.FormatVersion,
            footer.Columns,
            footer.RowGroups,
            footer.ZoneMaps,
            footer.BitmapIndexes,
            tamperedCrcs,
            footer.RowGroupByteOffsets);

        // Rebuild the stream: header + column payload + new footer.
        // Column-payload region = [HeaderSize .. start of old footer].
        var oldFooterLengthPos = bytes.Length - SegmentMagic.TrailerSize;
        var oldFooterLength = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(oldFooterLengthPos, 4));
        var columnDataEnd = bytes.Length - SegmentMagic.TrailerSize - SegmentMagic.FooterCrcSize - (int)oldFooterLength;

        using var rebuilt = new MemoryStream();
        rebuilt.Write(bytes, 0, columnDataEnd);
        FooterWriter.Write(rebuilt, tamperedFooter);
        rebuilt.Position = 0;

        var results = new List<RowGroupReadResult<ColumnarBar>>();
        await foreach (var r in format.ReadRowGroupsAsync(
            rebuilt, new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31)).ConfigureAwait(true))
        {
            results.Add(r);
        }

        results.Should().HaveCount(3);
        results[0].IsOk.Should().BeTrue();
        results[1].IsOk.Should().BeFalse();
        results[1].RowGroupIndex.Should().Be(1);
        results[1].ExpectedCrc.Should().Be(tamperedCrcs[1]);
        results[1].ActualCrc.Should().NotBe(tamperedCrcs[1]);
        results[2].IsOk.Should().BeTrue();
    }
    [Fact]
    public async Task ReadRowGroupsAsync_ScaledDecimalWithMixedScales_YieldsOkNotQuarantined()
    {
        // ScaledIntegerBitPacked re-scales every value to the column's common (max) scale on
        // decode: 100.5m (scale 1) comes back as 100.50m (scale 2) — value-equal, different
        // decimal.GetBits representation. The per-row-group CRC must be invariant to that
        // value-preserving re-scaling, or an intact segment false-quarantines.
        var format = new CompressedColumnarSegmentFormat<Phase10ScaledDecimalBar>(rowGroupMaxRows: 2);
        var input = new List<Phase10ScaledDecimalBar>
        {
            new() { Date = new DateOnly(2020, 1, 1), Price = 100.5m },
            new() { Date = new DateOnly(2020, 1, 2), Price = 100.25m },
            new() { Date = new DateOnly(2020, 1, 3), Price = 100m },
        };

        using var stream = new MemoryStream();
        await format.WriteAsync(stream, input).ConfigureAwait(true);
        stream.Position = 0;

        var results = new List<RowGroupReadResult<Phase10ScaledDecimalBar>>();
        await foreach (var r in format.ReadRowGroupsAsync(
            stream, new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31)).ConfigureAwait(true))
        {
            results.Add(r);
        }

        results.Should().OnlyContain(r => r.IsOk,
            "value-preserving codec re-scaling is not corruption");
        results.SelectMany(r => r.Rows!).Select(b => b.Price)
            .Should().Equal(100.5m, 100.25m, 100m); // decimal == is scale-insensitive
    }

    // ───── Inner byte-0 header validation ─────────────────────────────────────
    // The inner segment writes [magic:4][version:4] at byte 0. The reader must validate it
    // (defense-in-depth, symmetric with the outer envelope) — an unvalidated header is dead
    // weight that silently accepts a corrupted or foreign prefix.

    [Fact]
    public async Task FooterReader_CorruptInnerHeaderMagic_ThrowsInvalidSegmentException()
    {
        var format = new CompressedColumnarSegmentFormat<ColumnarBar>();
        var input = new List<ColumnarBar> { MakeBar(2020, 1, 1, 0) };
        using var stream = new MemoryStream();
        await format.WriteAsync(stream, input).ConfigureAwait(true);

        stream.Position = 0;
        stream.WriteByte(0x00); // clobber first magic byte

        stream.Position = 0;
        var act = () => FooterReader.Read(stream);
        act.Should().Throw<InvalidSegmentException>()
            .WithMessage("*header*");
    }

    [Fact]
    public async Task FooterReader_FutureInnerHeaderVersion_ThrowsUnsupportedFormatVersionException()
    {
        var format = new CompressedColumnarSegmentFormat<ColumnarBar>();
        var input = new List<ColumnarBar> { MakeBar(2020, 1, 1, 0) };
        using var stream = new MemoryStream();
        await format.WriteAsync(stream, input).ConfigureAwait(true);

        stream.Position = 4; // header version field
        stream.Write([0xFE, 0xFF, 0xFF, 0xFF]);

        stream.Position = 0;
        var act = () => FooterReader.Read(stream);
        act.Should().Throw<UnsupportedFormatVersionException>();
    }

}
