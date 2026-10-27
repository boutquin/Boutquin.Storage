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
using Boutquin.Storage.Domain.Interfaces.TimeSeries;
using Boutquin.Storage.Domain.Interfaces.TimeSeries.Columnar;
using Boutquin.Storage.Domain.ValueObjects.TimeSeries;
using Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar;
using Boutquin.Storage.Infrastructure.Tests.TimeSeries.Columnar;
using Boutquin.Storage.Infrastructure.TimeSeries;
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar;

namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// A columnar configuration or record the flush cannot honour must be refused before any write is
/// acknowledged: columnar options cannot be relabelled away from the columnar format they carry, a
/// hand-written field plan must pair every encoding with a property type it can encode, and a
/// dataset whose admitted records exceed one block's in-memory buffer is cut into bounded blocks so
/// it is still flushed and compacted.
/// </summary>
public sealed class ColumnarOptionsAndAdmissionBoundTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        $"ColumnarAdmissionBound_{Guid.NewGuid():N}");

    public ColumnarOptionsAndAdmissionBoundTests()
    {
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    // ── Columnar options keep the columnar label ─────────────────────────────

    [Fact]
    public void ColumnarOptions_WithExpressionRelabellingToRow_IsRefused()
    {
        var options = new ColumnarTimeSeriesStoreOptions<MarketDataBar>(new CompressedColumnarSegmentFormat<MarketDataBar>());

        var act = () => options with { SegmentFormat = TimeSeriesSegmentFormat.Row };

        act.Should().Throw<ArgumentException>(
            "a columnar options record carries a columnar format, so a Row label would leave that format unconsulted");
    }

    [Fact]
    public void ColumnarOptions_ObjectInitializerRelabellingToRow_IsRefused()
    {
        var act = () => new ColumnarTimeSeriesStoreOptions<MarketDataBar>(new CompressedColumnarSegmentFormat<MarketDataBar>())
        {
            SegmentFormat = TimeSeriesSegmentFormat.Row,
        };

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ColumnarOptions_RelabelledThroughBaseTypedReference_IsRefused()
    {
        TimeSeriesStoreOptions<MarketDataBar> options =
            new ColumnarTimeSeriesStoreOptions<MarketDataBar>(new CompressedColumnarSegmentFormat<MarketDataBar>());

        var act = () => options with { SegmentFormat = TimeSeriesSegmentFormat.Row };

        act.Should().Throw<ArgumentException>("the with-expression clones the derived record, whose label is fixed");
    }

    [Fact]
    public void ColumnarOptions_KeepingTheColumnarLabel_IsAccepted()
    {
        var format = new CompressedColumnarSegmentFormat<MarketDataBar>();
        var options = new ColumnarTimeSeriesStoreOptions<MarketDataBar>(format) with
        {
            SegmentFormat = TimeSeriesSegmentFormat.Columnar,
        };

        options.SegmentFormat.Should().Be(TimeSeriesSegmentFormat.Columnar);
        options.ColumnarFormat.Should().BeSameAs(format);
        new ColumnarTimeSeriesStoreOptions<MarketDataBar> { ColumnarFormat = format }
            .SegmentFormat.Should().Be(TimeSeriesSegmentFormat.Columnar);
    }

    // ── Field-plan encodings are checked at construction ─────────────────────

    [Fact]
    public void CompressedFormat_PlanEncodingThatCannotEncodeItsPropertyType_IsRefusedAtConstruction()
    {
        var act = () => new CompressedColumnarSegmentFormat<MismatchedPlanBar>();

        act.Should().Throw<NotSupportedException>()
            .WithMessage("*Px*DeltaVlq*Decimal*");
    }

    [Fact]
    public async Task Builder_PlanEncodingThatCannotEncodeItsPropertyType_RefusesBeforeAnyWriteIsAcknowledged()
    {
        var act = async () =>
        {
            await using var stack = await ColumnarTimeSeriesEngineBuilder.BuildAsync<SerializableWrapper<string>, MismatchedPlanBar>(
                segmentFolder: _folder, memTableCapacity: 1_000);
            await stack.Store.AppendAsync(new SerializableWrapper<string>("G"), [new MismatchedPlanBar { Date = new DateOnly(2024, 1, 1), Px = 1.5m }]);
        };

        await act.Should().ThrowAsync<NotSupportedException>();
    }

    // ── A dataset larger than one block buffer is still flushed ──────────────

    // The real ceiling is 256 MiB of serialized records per block; a small bound exercises the same
    // cut without gigabytes of data. MarketDataBar rows serialize to a few dozen bytes each.
    private const long SmallBlockBound = 600;

    private static ColumnarSegmentFormat<SerializableWrapper<string>, MarketDataBar> BoundedFormat()
        => new(new CompressedColumnarSegmentFormat<MarketDataBar>()) { MaxBlockSerializedBytes = SmallBlockBound };

    private static MarketDataBar Bar(DateOnly date, int i)
        => new() { Date = date, Close = 100m + i, Open = 99m + i, High = 101m + i, Low = 98m + i, Volume = i, SplitRatio = 1m };

    private static long SerializedBytes(MarketDataBar bar)
    {
        using var ms = new MemoryStream();
        bar.Serialize(ms);
        return ms.Length;
    }

    [Fact]
    public async Task WriteAsync_DatasetLargerThanOneBlockBound_IsCutIntoBoundedBlocksThatAllReadBack()
    {
        var d0 = new DateOnly(2024, 1, 1);
        var big = Enumerable.Range(0, 120).Select(i => Bar(d0.AddDays(i * 2), i)).ToList();
        var entries = new List<SegmentWriteEntry<TimeSeriesKey<SerializableWrapper<string>>, MarketDataBar>>();
        entries.Add(SegmentWriteEntry<TimeSeriesKey<SerializableWrapper<string>>, MarketDataBar>.Inline(Key("A", d0), Bar(d0, -1), 1UL));
        entries.AddRange(big.Select((b, i) =>
            SegmentWriteEntry<TimeSeriesKey<SerializableWrapper<string>>, MarketDataBar>.Inline(Key("BIG", b.Date), b, (ulong)(i + 2))));
        entries.Add(SegmentWriteEntry<TimeSeriesKey<SerializableWrapper<string>>, MarketDataBar>.Inline(Key("C", d0), Bar(d0, -2), 500UL));

        var reader = await BoundedFormat().WriteAsync(_folder, "seg.bsct", entries);

        var footer = ReadFooterBlocks(Path.Combine(_folder, "seg.bsct"));
        var bigBlocks = footer.Where(b => b.Dataset == "BIG").ToList();
        bigBlocks.Count.Should().BeGreaterThan(1, "the dataset's records exceed one block's bound");
        bigBlocks.Sum(b => b.RowCount).Should().Be(big.Count);
        var cursor = 0;
        foreach (var block in bigBlocks)
        {
            var bytes = big.Skip(cursor).Take(block.RowCount).Sum(SerializedBytes);
            (block.RowCount == 1 || bytes <= SmallBlockBound).Should().BeTrue(
                $"block of {block.RowCount} rows holds {bytes} serialized bytes, above the {SmallBlockBound}-byte bound");
            cursor += block.RowCount;
        }

        var all = (await reader.GetAllItemsAsync()).ToList();
        all.Select(kv => kv.Key).Should().Equal(entries.Select(e => e.Key), "a full scan returns every row in key order");
        all.Select(kv => kv.Value.Close).Should().Equal(entries.Select(e => e.InlineValue!.Close));

        foreach (var bar in big)
        {
            var (value, found) = await reader.TryGetValueAsync(Key("BIG", bar.Date));
            found.Should().BeTrue($"the row dated {bar.Date:O} is in one of the dataset's blocks");
            value.Close.Should().Be(bar.Close);
            (await reader.TryGetValueAsync(Key("BIG", bar.Date.AddDays(1)))).Found.Should().BeFalse("no row was written on odd days");
        }

        var projecting = (IProjectingSegmentReader<TimeSeriesKey<SerializableWrapper<string>>, MarketDataBar, ColumnProjection>)reader;
        var from = big[17].Date;
        var to = big[93].Date;
        var projected = new List<DateOnly>();
        await foreach (var kv in projecting.StreamProjectedRangeAsync(Key("BIG", from), Key("BIG", to), new ColumnProjection(["Date", "Close"])))
        {
            projected.Add(kv.Key.Date);
        }

        projected.Should().Equal(big.Skip(17).Take(93 - 17 + 1).Select(b => b.Date), "a range across block boundaries is exact and ascending");
    }

    [Fact]
    public async Task LsmEngine_FlushAndCompactionOfADatasetSpanningSeveralBlocks_KeepEveryAcknowledgedRecord()
    {
        var folder = Path.Combine(_folder, "lsm");
        Directory.CreateDirectory(folder);
        var d0 = new DateOnly(2020, 1, 1);
        var bars = Enumerable.Range(0, 90).Select(i => Bar(d0.AddDays(i), i)).ToList();

        await using (var engine = LsmEngineFactory.Build<TimeSeriesKey<SerializableWrapper<string>>, MarketDataBar>(
            memTableCapacity: 1_000, segmentFolder: folder, segmentPrefix: "ts", segmentFormat: BoundedFormat()))
        {
            foreach (var chunk in bars.Chunk(30))
            {
                foreach (var bar in chunk)
                {
                    await engine.SetAsync(Key("BIG", bar.Date), bar);
                }

                await engine.FlushAsync();
            }

            await engine.CompactAsync();
        }

        ReadFooterBlocks(Directory.GetFiles(folder, "*.bsct").Should().ContainSingle().Subject)
            .Count.Should().BeGreaterThan(1, "the compacted dataset exceeds one block's bound");

        await using var reopened = await LsmEngineFactory.BuildAsync<TimeSeriesKey<SerializableWrapper<string>>, MarketDataBar>(
            memTableCapacity: 1_000, segmentFolder: folder, segmentPrefix: "ts", segmentFormat: BoundedFormat());
        var scanned = new List<DateOnly>();
        await foreach (var (key, _) in reopened.GetRangeAsync(Key("BIG", DateOnly.MinValue), Key("BIG", DateOnly.MaxValue)))
        {
            scanned.Add(key.Date);
        }

        scanned.Should().Equal(bars.Select(b => b.Date));
        foreach (var bar in bars)
        {
            (await reopened.TryGetValueAsync(Key("BIG", bar.Date))).Value.Close.Should().Be(bar.Close);
        }
    }

    private static TimeSeriesKey<SerializableWrapper<string>> Key(string dataset, DateOnly date)
        => new(new SerializableWrapper<string>(dataset), date);

    private sealed record FooterBlock(string Dataset, int RowCount, int MinDayNumber, int MaxDayNumber);

    // Walks the envelope footer: datasetCount, max seqno, then per block key bytes, offset, length,
    // row count and day-number span.
    private static List<FooterBlock> ReadFooterBlocks(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var footerLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(bytes.Length - 8, 4));
        var p = bytes.Length - 8 - footerLength;
        var count = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(p, 4));
        p += 4 + 8;
        var blocks = new List<FooterBlock>();
        for (var i = 0; i < count; i++)
        {
            var keyLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(p, 4));
            p += 4;
            using var keyStream = new MemoryStream(bytes, p, keyLength, writable: false);
            var dataset = SerializableWrapper<string>.Deserialize(keyStream).Value;
            p += keyLength + 8 + 8;
            var rows = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(p, 4));
            var min = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(p + 4, 4));
            var max = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(p + 8, 4));
            p += 12;
            blocks.Add(new FooterBlock(dataset, rows, min, max));
        }

        return blocks;
    }
}

/// <summary>
/// Hand-written columnar record whose plan pairs a <see langword="decimal"/> property with
/// <see cref="ColumnEncoding.DeltaVlq"/>, an encoding that has no decimal codec.
/// </summary>
public sealed class MismatchedPlanBar : IHasDate, ISerializable<MismatchedPlanBar>, IColumnarRehydratable<MismatchedPlanBar>
{
    public DateOnly Date { get; set; }

    public decimal Px { get; set; }

    public static IReadOnlyList<ColumnarFieldDescriptor> ColumnarFieldPlan { get; } =
    [
        new ColumnarFieldDescriptor("Date", ColumnEncoding.DeltaOfDeltaVlq),
        new ColumnarFieldDescriptor("Px", ColumnEncoding.DeltaVlq),
    ];

    public static MismatchedPlanBar Rehydrate(IReadOnlyDictionary<string, Array> columns, int rowIndex)
        => new()
        {
            Date = columns.TryGetValue("Date", out var d) ? ((DateOnly[])d)[rowIndex] : default,
            Px = columns.TryGetValue("Px", out var p) ? ((decimal[])p)[rowIndex] : default,
        };

    public void Serialize(Stream stream)
    {
        Span<byte> buf = stackalloc byte[20];
        BinaryPrimitives.WriteInt32LittleEndian(buf[..4], Date.DayNumber);
        var bits = decimal.GetBits(Px);
        for (var i = 0; i < 4; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(buf.Slice(4 + (i * 4), 4), bits[i]);
        }

        stream.Write(buf);
    }

    public static MismatchedPlanBar Deserialize(Stream stream)
    {
        Span<byte> buf = stackalloc byte[20];
        stream.ReadExactly(buf);
        var bits = new int[4];
        for (var i = 0; i < 4; i++)
        {
            bits[i] = BinaryPrimitives.ReadInt32LittleEndian(buf.Slice(4 + (i * 4), 4));
        }

        return new MismatchedPlanBar { Date = DateOnly.FromDayNumber(BinaryPrimitives.ReadInt32LittleEndian(buf[..4])), Px = new decimal(bits) };
    }
}
