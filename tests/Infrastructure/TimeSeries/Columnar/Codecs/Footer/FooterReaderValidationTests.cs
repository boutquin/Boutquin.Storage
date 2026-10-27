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

namespace Boutquin.Storage.Infrastructure.Tests.TimeSeries.Columnar.Codecs.Footer;

/// <summary>
/// Footer row-group-descriptor validation. A row group's <c>FirstRowIndex</c> is a non-negative
/// absolute row offset and its <c>RowCount</c> is strictly positive; a negative offset or a
/// zero/negative count reaches the slice decoders and yields silent wrong data (or out-of-range
/// reads). The reader must reject such descriptors with the same corruption exception other footer
/// corruption raises.
/// </summary>
public sealed class FooterReaderValidationTests
{
    private static SegmentFooter MakeFooter(int rowCount, int firstRowIndex)
    {
        var d = new DateOnly(2020, 1, 1);
        var columns = new[]
        {
            new ColumnDescriptor("Close", 0, 8, ColumnEncodingTag.GorillaXor, 0, ColumnFieldTypeCode.Double),
        };
        var rowGroups = new[]
        {
            new RowGroupDescriptor(d, d, rowCount, firstRowIndex),
        };
        return new SegmentFooter(SegmentMagic.CurrentFormatVersion, columns, rowGroups);
    }

    private static byte[] Serialise(SegmentFooter footer)
    {
        using var ms = new MemoryStream();
        // The reader validates a byte-0 header (magic + version) before trusting the trailer.
        Span<byte> header = stackalloc byte[SegmentMagic.HeaderSize];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header[..4], SegmentMagic.AsUInt32);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header[4..], SegmentMagic.CurrentFormatVersion);
        ms.Write(header);
        FooterWriter.Write(ms, footer);
        return ms.ToArray();
    }

    [Fact]
    public void Read_NegativeFirstRowIndex_Throws()
    {
        var bytes = Serialise(MakeFooter(rowCount: 4, firstRowIndex: -1));

        var act = () => FooterReader.Read(new MemoryStream(bytes));
        act.Should().Throw<CorruptSegmentException>();
    }

    [Fact]
    public void Read_ZeroRowCount_Throws()
    {
        var bytes = Serialise(MakeFooter(rowCount: 0, firstRowIndex: 0));

        var act = () => FooterReader.Read(new MemoryStream(bytes));
        act.Should().Throw<CorruptSegmentException>();
    }

    private static SegmentFooter MakeTwoRowGroupFooter(int secondFirstRowIndex)
    {
        var d = new DateOnly(2020, 1, 1);
        var columns = new[]
        {
            new ColumnDescriptor("Close", 0, 8, ColumnEncodingTag.GorillaXor, 0, ColumnFieldTypeCode.Double),
        };
        var rowGroups = new[]
        {
            new RowGroupDescriptor(d, d, 4, 0),
            new RowGroupDescriptor(d, d, 4, secondFirstRowIndex),
        };
        return new SegmentFooter(SegmentMagic.CurrentFormatVersion, columns, rowGroups);
    }

    [Theory]
    [InlineData(6)] // gap: rows 4..5 belong to no row group
    [InlineData(2)] // overlap: rows 2..3 belong to both row groups
    [InlineData(0)] // duplicate start
    public void Read_NonContiguousRowGroups_Throws(int secondFirstRowIndex)
    {
        var bytes = Serialise(MakeTwoRowGroupFooter(secondFirstRowIndex));

        var act = () => FooterReader.Read(new MemoryStream(bytes));
        act.Should().Throw<CorruptSegmentException>()
            .WithMessage("*row-group 1*firstRowIndex*");
    }

    [Fact]
    public void Read_ContiguousRowGroups_RoundTrips()
    {
        var bytes = Serialise(MakeTwoRowGroupFooter(secondFirstRowIndex: 4));

        var footer = FooterReader.Read(new MemoryStream(bytes));
        footer.RowGroups.Select(rg => rg.FirstRowIndex).Should().Equal(0, 4);
    }

    [Fact]
    public void Read_ValidDescriptor_RoundTrips()
    {
        var bytes = Serialise(MakeFooter(rowCount: 4, firstRowIndex: 0));

        var footer = FooterReader.Read(new MemoryStream(bytes));
        footer.RowGroups.Should().ContainSingle();
        footer.RowGroups[0].RowCount.Should().Be(4);
        footer.RowGroups[0].FirstRowIndex.Should().Be(0);
    }
    // Zone-map null/distinct counts are footer fields like any other: a CRC-valid forged negative
    // count must surface as the typed corruption signal, never as a raw ArgumentOutOfRangeException
    // from the statistics constructor.
    [Theory]
    [InlineData(0)] // nullCount
    [InlineData(4)] // distinctCount
    public void Read_NegativeZoneMapCount_ThrowsCorruptSegment(int fieldOffset)
    {
        var bytes = SerialiseWithNegativeZoneMapCount(fieldOffset);

        var act = () => FooterReader.Read(new MemoryStream(bytes));

        act.Should().Throw<CorruptSegmentException>().WithMessage("*negative*");
    }

    [Fact]
    public void Read_ZoneMapCounts_RoundTrip()
    {
        var bytes = Serialise(MakeZoneMapFooter());

        var footer = FooterReader.Read(new MemoryStream(bytes));

        footer.ZoneMaps![0][0].NullCount.Should().Be(SentinelNullCount);
        footer.ZoneMaps![0][0].DistinctCount.Should().Be(SentinelDistinctCount);
    }

    private const int SentinelNullCount = 0x1A2B3C4D;
    private const int SentinelDistinctCount = 0x0E1F2A3B;

    private static SegmentFooter MakeZoneMapFooter()
    {
        var stats = new ColumnRowGroupStats([1, 2], [3, 4], SentinelNullCount, SentinelDistinctCount);
        return MakeFooter(rowCount: 4, firstRowIndex: 0) with { ZoneMaps = [[stats]] };
    }

    // Writes a valid zone-map footer, overwrites the nullCount (offset 0) or distinctCount (offset 4)
    // field with -1 and recomputes the footer CRC — the forgeable-CRC threat model.
    private static byte[] SerialiseWithNegativeZoneMapCount(int fieldOffset)
    {
        var bytes = Serialise(MakeZoneMapFooter());
        Span<byte> pattern = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(pattern[..4], SentinelNullCount);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(pattern[4..], SentinelDistinctCount);
        var at = bytes.AsSpan().IndexOf(pattern);
        at.Should().BePositive("the sentinel counts must be present in the serialised zone map");
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at + fieldOffset, 4), -1);

        var payloadStart = SegmentMagic.HeaderSize;
        var crcAt = bytes.Length - SegmentMagic.TrailerSize - SegmentMagic.FooterCrcSize;
        var crc = Boutquin.Storage.Infrastructure.Algorithms.Crc32C.HashToUInt32(bytes.AsSpan(payloadStart, crcAt - payloadStart));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(crcAt, 4), crc);
        return bytes;
    }
}
