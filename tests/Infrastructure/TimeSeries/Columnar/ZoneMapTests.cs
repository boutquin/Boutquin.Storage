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
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar.Footer;

namespace Boutquin.Storage.Infrastructure.Tests.TimeSeries.Columnar;

/// <summary>
/// Phase E1 — per-row-group per-column zone maps. Tests the
/// <see cref="ColumnRowGroupStats"/> value type, footer roundtrip of the
/// zone-map extension section, and writer-side stats computation.
/// </summary>
public sealed class ZoneMapTests
{
    // ───── ColumnRowGroupStats value semantics ────────────────────────

    [Fact]
    public void ColumnRowGroupStats_Equality_ComparesByteContentNotReference()
    {
        var a = new ColumnRowGroupStats(
            MinValue: [0x01, 0x02],
            MaxValue: [0x03, 0x04],
            NullCount: 1,
            DistinctCount: 2);
        var b = new ColumnRowGroupStats(
            MinValue: [0x01, 0x02],
            MaxValue: [0x03, 0x04],
            NullCount: 1,
            DistinctCount: 2);

        a.Should().Be(b);
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void ColumnRowGroupStats_Equality_DifferentMinValue_NotEqual()
    {
        var a = new ColumnRowGroupStats(
            MinValue: [0x01], MaxValue: [0x02],
            NullCount: 0, DistinctCount: 1);
        var b = new ColumnRowGroupStats(
            MinValue: [0x03], MaxValue: [0x02],
            NullCount: 0, DistinctCount: 1);

        a.Should().NotBe(b);
    }

    [Fact]
    public void ColumnRowGroupStats_EmptyMinMax_Allowed_RepresentsNullOnlyGroup()
    {
        var stats = new ColumnRowGroupStats(
            MinValue: [], MaxValue: [],
            NullCount: 10, DistinctCount: 0);

        stats.MinValue.Should().BeEmpty();
        stats.MaxValue.Should().BeEmpty();
        stats.NullCount.Should().Be(10);
        stats.DistinctCount.Should().Be(0);
    }

    [Fact]
    public void ColumnRowGroupStats_NegativeNullCount_ThrowsArgumentOutOfRange()
    {
        var act = () => new ColumnRowGroupStats(
            MinValue: [], MaxValue: [],
            NullCount: -1, DistinctCount: 0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ColumnRowGroupStats_NegativeDistinctCount_ThrowsArgumentOutOfRange()
    {
        var act = () => new ColumnRowGroupStats(
            MinValue: [], MaxValue: [],
            NullCount: 0, DistinctCount: -1);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ColumnRowGroupStats_NullMinOrMax_ThrowsArgumentNull()
    {
        var actMin = () => new ColumnRowGroupStats(
            MinValue: null!, MaxValue: [],
            NullCount: 0, DistinctCount: 0);
        var actMax = () => new ColumnRowGroupStats(
            MinValue: [], MaxValue: null!,
            NullCount: 0, DistinctCount: 0);

        actMin.Should().Throw<ArgumentNullException>();
        actMax.Should().Throw<ArgumentNullException>();
    }

    // ───── Footer roundtrip: ZoneMaps extension section ───────────────

    [Fact]
    public void Footer_WithZoneMaps_RoundTripsLossless()
    {
        var footer = new SegmentFooter(
            FormatVersion: SegmentMagic.CurrentFormatVersion,
            Columns:
            [
                new ColumnDescriptor("Price", 8, 40, ColumnEncodingTag.PlainRawBytes, 0, ColumnFieldTypeCode.Double),
                new ColumnDescriptor("Ticker", 48, 16, ColumnEncodingTag.Dictionary, 0, ColumnFieldTypeCode.String),
            ],
            RowGroups:
            [
                new RowGroupDescriptor(new DateOnly(2020, 1, 1), new DateOnly(2020, 1, 5), 5, 0),
                new RowGroupDescriptor(new DateOnly(2020, 1, 6), new DateOnly(2020, 1, 10), 5, 5),
            ],
            ZoneMaps:
            [
                // Row group 0
                [
                    new ColumnRowGroupStats(
                        MinValue: BitConverter.GetBytes(1.0), MaxValue: BitConverter.GetBytes(5.0),
                        NullCount: 0, DistinctCount: 5),
                    new ColumnRowGroupStats(
                        MinValue: System.Text.Encoding.UTF8.GetBytes("AAPL"),
                        MaxValue: System.Text.Encoding.UTF8.GetBytes("MSFT"),
                        NullCount: 0, DistinctCount: 2),
                ],
                // Row group 1
                [
                    new ColumnRowGroupStats(
                        MinValue: BitConverter.GetBytes(6.0), MaxValue: BitConverter.GetBytes(10.0),
                        NullCount: 1, DistinctCount: 4),
                    new ColumnRowGroupStats(
                        MinValue: System.Text.Encoding.UTF8.GetBytes("GOOG"),
                        MaxValue: System.Text.Encoding.UTF8.GetBytes("NVDA"),
                        NullCount: 0, DistinctCount: 3),
                ],
            ]);

        using var stream = new MemoryStream();
        WriteSegmentHeader(stream);
        FooterWriter.Write(stream, footer);

        stream.Position = 0;
        var recovered = FooterReader.Read(stream);

        recovered.FormatVersion.Should().Be(footer.FormatVersion);
        recovered.Columns.Should().Equal(footer.Columns);
        recovered.RowGroups.Should().Equal(footer.RowGroups);
        recovered.ZoneMaps.Should().NotBeNull();
        recovered.ZoneMaps!.Should().HaveCount(2);
        recovered.ZoneMaps[0].Should().Equal(footer.ZoneMaps![0]);
        recovered.ZoneMaps[1].Should().Equal(footer.ZoneMaps[1]);
    }

    [Fact]
    public void Footer_WithoutZoneMaps_RoundTrips_ZoneMapsIsNull()
    {
        // Writer that omits the zone-map extension section — simulating a pre-Phase-E
        // writer or a writer configured with zone-maps disabled. Readers MUST surface this
        // as "no zone-map information available" (null) rather than throwing.
        var footer = new SegmentFooter(
            FormatVersion: SegmentMagic.CurrentFormatVersion,
            Columns: [new ColumnDescriptor("X", 8, 8, ColumnEncodingTag.PlainRawBytes, 0, ColumnFieldTypeCode.Double)],
            RowGroups: [new RowGroupDescriptor(new DateOnly(2020, 1, 1), new DateOnly(2020, 1, 1), 1, 0)]);

        using var stream = new MemoryStream();
        WriteSegmentHeader(stream);
        FooterWriter.Write(stream, footer);
        stream.Position = 0;
        var recovered = FooterReader.Read(stream);

        recovered.ZoneMaps.Should().BeNull();
    }

    /// <summary>Writes the 8-byte segment header stub required by the footer reader's offset math.</summary>
    private static void WriteSegmentHeader(Stream output)
    {
        Span<byte> header = stackalloc byte[SegmentMagic.HeaderSize];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header[..4], SegmentMagic.AsUInt32);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header[4..], SegmentMagic.CurrentFormatVersion);
        output.Write(header);
    }

    // ───── Writer integration: stats computed for every column & row group ─

    private static ColumnarBar MakeBar(int y, int m, int d, int n, string ticker = "AAPL") => new()
    {
        Date = new DateOnly(y, m, d),
        Ticker = ticker,
        Close = 100m + (n * 0.25m),
        Volume = 1_000_000L + (n * 1000L),
        TradeCount = 1000 + n,
        Vwap = 100.0 + (n * 0.25),
        IsAdjusted = (n % 2) == 0,
        Residual = n * 0.001,
    };

    [Fact]
    public async Task CompressedFormat_WritesZoneMapsForEveryColumnAndRowGroup()
    {
        var format = new CompressedColumnarSegmentFormat<ColumnarBar>(rowGroupMaxRows: 5);
        var input = Enumerable.Range(0, 12)
            .Select(i => MakeBar(2020, 1, 1 + i, i, ticker: i < 5 ? "AAPL" : (i < 10 ? "MSFT" : "GOOG")))
            .ToList();

        using var stream = new MemoryStream();
        await format.WriteAsync(stream, input).ConfigureAwait(true);
        stream.Position = 0;

        var zoneMaps = format.ReadZoneMaps(stream);

        zoneMaps.Should().NotBeNull();
        zoneMaps!.Should().HaveCount(3); // 12 rows / 5 per rg → 3 row groups (5, 5, 2)

        // Every row group has stats for every column.
        foreach (var perColumn in zoneMaps)
        {
            perColumn.Should().HaveCount(8); // ColumnarBar has 8 columns
        }

        // Spot-check: row group 0 covers rows 0–4, Vwap is 100.0, 100.25, 100.5, 100.75, 101.0.
        stream.Position = 0;
        var footer = FooterReader.Read(stream);
        var vwapIndex = footer.Columns
            .Select((c, i) => (c.Name, i))
            .First(t => t.Name == "Vwap").i;

        var vwapRg0 = zoneMaps[0][vwapIndex];
        vwapRg0.NullCount.Should().Be(0);
        vwapRg0.DistinctCount.Should().Be(5);
        BitConverter.ToDouble(vwapRg0.MinValue).Should().Be(100.0);
        BitConverter.ToDouble(vwapRg0.MaxValue).Should().Be(101.0);
    }
}
