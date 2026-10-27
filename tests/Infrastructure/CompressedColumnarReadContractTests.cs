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
using Boutquin.Storage.Domain.Attributes;
using Boutquin.Storage.Domain.Interfaces.TimeSeries;
using Boutquin.Storage.Domain.Interfaces.TimeSeries.Columnar;
using Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar;
using Boutquin.Storage.Infrastructure.Tests.TimeSeries.Columnar;
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar;
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar.Codecs;
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar.Footer;

namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// Read-side contract of <see cref="CompressedColumnarSegmentFormat{TRecord}"/>: the footer, not the
/// reading record's current annotations, decides how column bytes are decoded; every read entry point
/// returns the rows whose dates fall in range regardless of which columns are projected; forged
/// footers surface typed corruption exceptions; and decode allocations scale with one row group,
/// never with the whole segment.
/// </summary>
public sealed class CompressedColumnarReadContractTests
{
    private static readonly DateOnly s_day = new(2026, 1, 2);

    // ───── Footer encoding tag drives decode ─────────────────────────

    public static TheoryData<string, ColumnProjection> Projections() => new()
    {
        { "subset", new ColumnProjection(["Date", "X"]) },
        { "all", ColumnProjection.All },
    };

    [Theory]
    [MemberData(nameof(Projections))]
    public async Task RawDoubleSegment_ReadByGorillaPlan_ReturnsWrittenValues(string label, ColumnProjection projection)
    {
        var rows = DoubleRows(16);
        var bytes = await WriteAsync(new CompressedColumnarSegmentFormat<ReadContractRawBar>(),
            rows.Select(r => new ReadContractRawBar { Date = r.Date, X = r.X, Y = r.Y }).ToList());

        var read = await ReadAsync(new CompressedColumnarSegmentFormat<ReadContractGorillaBar>(), bytes, projection);

        read.Select(r => r.X).Should().Equal(rows.Select(r => r.X),
            $"the footer records PlainRawBytes for X, so a GorillaXor-annotated reader must still decode the written values ({label})");
    }

    [Theory]
    [MemberData(nameof(Projections))]
    public async Task GorillaSegment_ReadByRawDoublePlan_ReturnsWrittenValues(string label, ColumnProjection projection)
    {
        var rows = DoubleRows(16);
        var bytes = await WriteAsync(new CompressedColumnarSegmentFormat<ReadContractGorillaBar>(), rows);

        var read = await ReadAsync(new CompressedColumnarSegmentFormat<ReadContractRawBar>(), bytes, projection);

        read.Select(r => r.X).Should().Equal(rows.Select(r => r.X),
            $"the footer records GorillaXor for X, so a RawDouble-annotated reader must still decode the written values ({label})");
    }

    [Fact]
    public async Task ScaledDecimalSegment_ReadByDictionaryPlan_ReturnsWrittenValuesOnEveryEntryPoint()
    {
        var rows = Enumerable.Range(0, 9)
            .Select(i => new ReadContractScaledBar { Date = s_day.AddDays(i), Px = 100.25m + (i * 1.5m) })
            .ToList();
        var bytes = await WriteAsync(new CompressedColumnarSegmentFormat<ReadContractScaledBar>(rowGroupMaxRows: 4), rows);
        var reader = new CompressedColumnarSegmentFormat<ReadContractDictBar>(rowGroupMaxRows: 4);

        var viaRead = await ReadAsync(reader, bytes, new ColumnProjection(["Date", "Px"]));
        var viaRowGroups = new List<decimal>();
        await foreach (var rg in reader.ReadRowGroupsAsync(new MemoryStream(bytes), s_day, s_day.AddDays(30)).ConfigureAwait(true))
        {
            rg.IsOk.Should().BeTrue("an intact segment must pass its per-row-group CRC");
            viaRowGroups.AddRange(rg.Rows!.Select(r => r.Px));
        }

        viaRead.Select(r => r.Px).Should().Equal(rows.Select(r => r.Px));
        viaRowGroups.Should().Equal(rows.Select(r => r.Px));
    }

    [Fact]
    public async Task ProjectedRead_FooterFieldTypeDiffersFromRecordProperty_ThrowsCorruptSegment()
    {
        // The footer declares X as Single; the reading record declares it double. Decoding float bit
        // patterns as doubles would hand back unrelated values, so the reader must refuse.
        var rows = Enumerable.Range(0, 8)
            .Select(i => new ReadContractSingleBar { Date = s_day.AddDays(i), X = 1.5f + i, Y = i })
            .ToList();
        var bytes = await WriteAsync(new CompressedColumnarSegmentFormat<ReadContractSingleBar>(), rows);

        var act = () => ReadAsync(new CompressedColumnarSegmentFormat<ReadContractGorillaBar>(), bytes, new ColumnProjection(["Date", "X"]));

        await act.Should().ThrowAsync<CorruptSegmentException>();
    }

    [Fact]
    public async Task ProjectedRead_FooterTagIncompatibleWithPropertyType_ThrowsColumnDecode()
    {
        var rows = DoubleRows(8);
        var bytes = await WriteAsync(new CompressedColumnarSegmentFormat<ReadContractGorillaBar>(), rows);
        var footer = FooterReader.Read(new MemoryStream(bytes));
        var forged = footer with
        {
            Columns = footer.Columns
                .Select(c => c.Name == "X" ? c with { EncodingTag = ColumnEncodingTag.BitmapRle, CodecVersion = BitmapRleCodec.Version } : c)
                .ToArray(),
        };

        var act = () => ReadAsync(new CompressedColumnarSegmentFormat<ReadContractGorillaBar>(),
            RewriteFooter(bytes, forged), new ColumnProjection(["Date", "X"]));

        await act.Should().ThrowAsync<ColumnDecodeException>();
    }

    // ───── Date-less projections ─────────────────────────────────────

    [Fact]
    public async Task ReadAsync_ProjectionWithoutDate_ReturnsEveryInRangeRow()
    {
        var rows = DoubleRows(6);
        var bytes = await WriteAsync(new CompressedColumnarSegmentFormat<ReadContractGorillaBar>(), rows);

        var read = await ReadAsync(new CompressedColumnarSegmentFormat<ReadContractGorillaBar>(), bytes, new ColumnProjection(["X"]),
            s_day.AddDays(1), s_day.AddDays(4));

        read.Select(r => (r.Date, r.X)).Should().Equal(rows.Skip(1).Take(4).Select(r => (r.Date, r.X)),
            "the date range filter must apply to the stored dates even when the caller did not project Date");
    }

    [Fact]
    public async Task StreamWhereAsync_ProjectionWithoutDate_ReturnsEveryMatchingRow()
    {
        var rows = DoubleRows(6);
        var bytes = await WriteAsync(new CompressedColumnarSegmentFormat<ReadContractGorillaBar>(), rows);
        var format = new CompressedColumnarSegmentFormat<ReadContractGorillaBar>();

        var read = new List<ReadContractGorillaBar>();
        await foreach (var r in format.StreamWhereAsync(new MemoryStream(bytes), s_day, s_day.AddDays(30), new ColumnProjection(["X"]),
            [new ColumnPredicate("X", ComparisonOperator.GreaterThan, 0.0)]).ConfigureAwait(true))
        {
            read.Add(r);
        }

        read.Select(r => r.X).Should().Equal(rows.Select(r => r.X));
    }

    // A record can satisfy IHasDate through an explicit implementation over a column with another
    // name. The date filter reads IHasDate.Date, so the column that backs it must be decoded under
    // any projection, not only a column literally named "Date".
    [Fact]
    public async Task ReadAsync_ExplicitDateRecord_ProjectionWithoutDateColumn_ReturnsEveryInRangeRow()
    {
        var rows = ExplicitDateRows(6);
        var bytes = await WriteAsync(new CompressedColumnarSegmentFormat<ExplicitDateBar>(), rows);

        var read = await ReadAsync(new CompressedColumnarSegmentFormat<ExplicitDateBar>(), bytes, new ColumnProjection(["Value"]),
            s_day.AddDays(1), s_day.AddDays(4));

        read.Select(r => (r.TradeDate, r.Value)).Should().Equal(rows.Skip(1).Take(4).Select(r => (r.TradeDate, r.Value)),
            "the full-projection read of the same bytes returns these rows; a projection must not change which rows are in range");
    }

    [Fact]
    public async Task StreamWhereAsync_ExplicitDateRecord_ProjectionWithoutDateColumn_ReturnsEveryMatchingRow()
    {
        var rows = ExplicitDateRows(6);
        var bytes = await WriteAsync(new CompressedColumnarSegmentFormat<ExplicitDateBar>(), rows);
        var format = new CompressedColumnarSegmentFormat<ExplicitDateBar>();

        var read = new List<ExplicitDateBar>();
        await foreach (var r in format.StreamWhereAsync(new MemoryStream(bytes), s_day, s_day.AddDays(30), new ColumnProjection(["Value"]),
            [new ColumnPredicate("Value", ComparisonOperator.GreaterThan, 0.0)]).ConfigureAwait(true))
        {
            read.Add(r);
        }

        read.Select(r => (r.TradeDate, r.Value)).Should().Equal(rows.Select(r => (r.TradeDate, r.Value)));
    }

    // ───── Predicate columns are validated like projected columns ────

    // A predicate on a column the segment does not declare cannot be evaluated. Under a strict
    // projection — ColumnProjection.All included — it must raise the same ColumnNotFoundException an
    // explicit projection raises, not answer with a silent empty result.
    [Theory]
    [MemberData(nameof(Projections))]
    public async Task StreamWhereAsync_PredicateOnUndeclaredColumn_StrictProjection_ThrowsColumnNotFound(string label, ColumnProjection projection)
    {
        var bytes = await WriteAsync(new CompressedColumnarSegmentFormat<ReadContractGorillaBar>(), DoubleRows(6));
        var format = new CompressedColumnarSegmentFormat<ReadContractGorillaBar>();

        var act = async () =>
        {
            await foreach (var _ in format.StreamWhereAsync(new MemoryStream(bytes), s_day, s_day.AddDays(30), projection,
                [new ColumnPredicate("Yy", ComparisonOperator.Equal, 5)]).ConfigureAwait(true))
            {
            }
        };

        (await act.Should().ThrowAsync<ColumnNotFoundException>($"projection {label} is strict")).Which.Message.Should().Contain("Yy");
    }

    [Fact]
    public async Task StreamWhereAsync_PredicateOnUndeclaredColumn_AllowMissing_ReturnsNoRows()
    {
        var bytes = await WriteAsync(new CompressedColumnarSegmentFormat<ReadContractGorillaBar>(), DoubleRows(6));
        var format = new CompressedColumnarSegmentFormat<ReadContractGorillaBar>();

        var read = new List<ReadContractGorillaBar>();
        await foreach (var r in format.StreamWhereAsync(new MemoryStream(bytes), s_day, s_day.AddDays(30),
            new ColumnProjection(["X"], AllowMissing: true),
            [new ColumnPredicate("Yy", ComparisonOperator.Equal, 5)]).ConfigureAwait(true))
        {
            read.Add(r);
        }

        read.Should().BeEmpty("an opted-in missing column has no value, so no row can satisfy a predicate on it");
    }

    // ───── Decode allocation scales with one row group ───────────────

    [Fact]
    public async Task ReadRowGroupsAsync_SingleSelectedRowGroup_DoesNotAllocateTheWholeColumn()
    {
        const int rowGroups = 4096;
        const int rowsPerGroup = 1 << 14;
        var bytes = ForgeConstantBoolSegment(rowGroups, rowsPerGroup);
        var format = new CompressedColumnarSegmentFormat<RunLengthBar>();

        var before = GC.GetAllocatedBytesForCurrentThread();
        var results = 0;
        await foreach (var _ in format.ReadRowGroupsAsync(new MemoryStream(bytes), s_day, s_day).ConfigureAwait(true))
        {
            results++;
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        results.Should().Be(1);
        allocated.Should().BeLessThan(16L << 20,
            "reading one selected row group must decode that row group only, not a bool[] spanning all 4096 row groups");
    }

    // ───── Forged footers surface typed exceptions ───────────────────

    [Fact]
    public async Task ProjectedReads_FooterListsColumnTwice_ThrowCorruptSegment()
    {
        var rows = Enumerable.Range(0, 8).Select(i => new ColumnarBar
        {
            Date = s_day.AddDays(i),
            Ticker = "A",
            Close = 1.5m + i,
            Volume = i,
            TradeCount = i,
            Vwap = i,
            IsAdjusted = i % 2 == 0,
            Residual = i,
        }).ToList();
        var format = new CompressedColumnarSegmentFormat<ColumnarBar>();
        var bytes = await WriteAsync(format, rows);
        var footer = FooterReader.Read(new MemoryStream(bytes));
        var forged = RewriteFooter(bytes, footer with
        {
            Columns = footer.Columns.Concat([footer.Columns.First(c => c.Name == "Close")]).ToArray(),
            ZoneMaps = null,
        });

        var read = () => ReadAsync(format, forged, new ColumnProjection(["Date", "Close"]));
        var streamWhere = async () =>
        {
            await foreach (var _ in format.StreamWhereAsync(new MemoryStream(forged), s_day, s_day.AddDays(30), new ColumnProjection(["Date", "Close"]),
                [new ColumnPredicate("Volume", ComparisonOperator.GreaterThan, 2L)]).ConfigureAwait(true))
            {
            }
        };

        await read.Should().ThrowAsync<CorruptSegmentException>();
        await streamWhere.Should().ThrowAsync<CorruptSegmentException>();
    }

    [Fact]
    public async Task StreamWhereAsync_ForgedBitmapShorterThanRowRange_ThrowsCorruptSegment()
    {
        var format = new CompressedColumnarSegmentFormat<IndexableBar>(rowGroupMaxRows: 4);
        var rows = Enumerable.Range(0, 12)
            .Select(i => new IndexableBar { Date = s_day.AddDays(i), Ticker = i % 2 == 0 ? "AAA" : "BBB" })
            .ToList();
        var bytes = await WriteAsync(format, rows);
        var footer = FooterReader.Read(new MemoryStream(bytes));
        var entry = footer.BitmapIndexes!.Single();
        var oneRow = BitmapRleCodec.Encode([true]);
        var forged = RewriteFooter(bytes, footer with
        {
            BitmapIndexes = [new BitmapIndexEntry(entry.ColumnName, entry.ValueToBitmap.Select(v => new BitmapIndexValue(v.Value, oneRow)).ToList())],
        });

        var act = async () =>
        {
            await foreach (var _ in format.StreamWhereAsync(new MemoryStream(forged), DateOnly.MinValue, DateOnly.MaxValue,
                ColumnProjection.All, [new ColumnPredicate("Ticker", ComparisonOperator.Equal, "AAA")]).ConfigureAwait(true))
            {
            }
        };

        await act.Should().ThrowAsync<CorruptSegmentException>();
    }

    // ───── Helpers ───────────────────────────────────────────────────

    private static List<ReadContractGorillaBar> DoubleRows(int count)
        => Enumerable.Range(0, count)
            .Select(i => new ReadContractGorillaBar { Date = s_day.AddDays(i), X = 100.25 + i + (1.0 / (i + 3)), Y = i * 7 })
            .ToList();

    private static List<ExplicitDateBar> ExplicitDateRows(int count)
        => Enumerable.Range(0, count)
            .Select(i => new ExplicitDateBar { TradeDate = s_day.AddDays(i), Value = 10.5 + i })
            .ToList();

    private static async Task<byte[]> WriteAsync<T>(CompressedColumnarSegmentFormat<T> format, IReadOnlyList<T> rows)
        where T : IHasDate, IColumnarRehydratable<T>, new()
    {
        using var ms = new MemoryStream();
        await format.WriteAsync(ms, rows).ConfigureAwait(true);
        return ms.ToArray();
    }

    private static async Task<List<T>> ReadAsync<T>(
        CompressedColumnarSegmentFormat<T> format, byte[] bytes, ColumnProjection projection, DateOnly? from = null, DateOnly? to = null)
        where T : IHasDate, IColumnarRehydratable<T>, new()
    {
        var result = new List<T>();
        await foreach (var r in format.ReadAsync(new MemoryStream(bytes), from ?? s_day, to ?? s_day.AddDays(365), projection).ConfigureAwait(true))
        {
            result.Add(r);
        }

        return result;
    }

    // Replaces the segment's footer with `footer`, re-emitted through the production writer so its CRC
    // is valid: the established forged-footer threat model.
    private static byte[] RewriteFooter(byte[] bytes, SegmentFooter footer)
    {
        var oldLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(bytes.Length - SegmentMagic.TrailerSize, 4));
        var dataEnd = bytes.Length - SegmentMagic.TrailerSize - SegmentMagic.FooterCrcSize - (int)oldLength;
        using var rebuilt = new MemoryStream();
        rebuilt.Write(bytes, 0, dataEnd);
        FooterWriter.Write(rebuilt, footer);
        return rebuilt.ToArray();
    }

    // A segment declaring only RunLengthBar's BitmapRle "Flag" column as one run over every row, split
    // into contiguous row groups dated s_day, s_day+1, ….
    private static byte[] ForgeConstantBoolSegment(int rowGroups, int rowsPerGroup)
    {
        using var ms = new MemoryStream();
        Span<byte> header = stackalloc byte[SegmentMagic.HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header[..4], SegmentMagic.AsUInt32);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], SegmentMagic.CurrentFormatVersion);
        ms.Write(header);
        var offset = ms.Position;
        ms.WriteByte(1);
        Vlq.WriteUInt64(ms, (ulong)rowGroups * (ulong)rowsPerGroup);
        var length = ms.Position - offset;
        var columns = new[]
        {
            new ColumnDescriptor("Flag", offset, length, ColumnEncodingTag.BitmapRle, BitmapRleCodec.Version, ColumnFieldTypeCode.Boolean),
        };
        var groups = new RowGroupDescriptor[rowGroups];
        for (var i = 0; i < rowGroups; i++)
        {
            groups[i] = new RowGroupDescriptor(s_day.AddDays(i), s_day.AddDays(i), rowsPerGroup, i * rowsPerGroup);
        }

        FooterWriter.Write(ms, new SegmentFooter(SegmentMagic.CurrentFormatVersion, columns, groups));
        return ms.ToArray();
    }
}

[ColumnarStorable]
public partial record ReadContractGorillaBar : IHasDate
{
    public DateOnly Date { get; init; }
    public double X { get; init; }
    public int Y { get; init; }
}

[ColumnarStorable]
public partial record ReadContractRawBar : IHasDate
{
    public DateOnly Date { get; init; }
    [ColumnEncoding(ColumnEncoding.RawDouble)]
    public double X { get; init; }
    public int Y { get; init; }
}

[ColumnarStorable]
public partial record ReadContractSingleBar : IHasDate
{
    public DateOnly Date { get; init; }
    public float X { get; init; }
    public int Y { get; init; }
}

[ColumnarStorable]
public partial record ReadContractDictBar : IHasDate
{
    public DateOnly Date { get; init; }
    public decimal Px { get; init; }
}

[ColumnarStorable]
public partial record ReadContractScaledBar : IHasDate
{
    public DateOnly Date { get; init; }
    [ColumnEncoding(ColumnEncoding.ScaledIntegerBitPacked)]
    public decimal Px { get; init; }
}
