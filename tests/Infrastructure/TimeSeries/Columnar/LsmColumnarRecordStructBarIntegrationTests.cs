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
using Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar;
using Boutquin.Storage.Infrastructure.TimeSeries;
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar;
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar.Footer;

namespace Boutquin.Storage.Infrastructure.Tests.TimeSeries.Columnar;

/// <summary>
/// End-to-end integration tests for <see cref="ColumnarRecordStructBar"/> — a <c>record struct</c>
/// annotated with both <c>[StorageSerializable]</c> and <c>[ColumnarStorable]</c>. Verifies that
/// the generator-emitted <c>Rehydrate</c> method (using <c>with</c>-expressions) correctly
/// materialises records from decoded column arrays without the per-field boxing that
/// <c>PropertyInfo.SetValue</c> incurs on value types.
/// </summary>
public sealed class LsmColumnarRecordStructBarIntegrationTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        $"LsmColumnarRecordStruct_{Guid.NewGuid():N}");

    public LsmColumnarRecordStructBarIntegrationTests()
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

    private static ColumnarRecordStructBar MakeBar(DateOnly date, decimal close, decimal open = 100m,
        decimal high = 110m, decimal low = 90m, decimal adjClose = 0m, long volume = 1_000_000L)
        => new(date, open, high, low, close, adjClose == 0m ? close : adjClose, volume);

    private (LsmTimeSeriesStore<SerializableWrapper<string>, ColumnarRecordStructBar> Store,
             LsmStorageEngine<TimeSeriesKey<SerializableWrapper<string>>, ColumnarRecordStructBar> Inner)
        BuildStore(int memTableCapacity = 16)
    {
        var innerColumnar = new CompressedColumnarSegmentFormat<ColumnarRecordStructBar>();
        var segmentFormat = new ColumnarSegmentFormat<SerializableWrapper<string>, ColumnarRecordStructBar>(innerColumnar);

        var engine = LsmEngineFactory.Build<TimeSeriesKey<SerializableWrapper<string>>, ColumnarRecordStructBar>(
            memTableCapacity: memTableCapacity,
            segmentFolder: _folder,
            segmentPrefix: "rsbar",
            segmentFormat: segmentFormat);

        var options = new ColumnarTimeSeriesStoreOptions<ColumnarRecordStructBar>
        {
            ColumnarFormat = innerColumnar,
        };

        var tsEngine = new LsmTimeSeriesStorageEngine<SerializableWrapper<string>, ColumnarRecordStructBar>(engine, options);
        var store = new LsmTimeSeriesStore<SerializableWrapper<string>, ColumnarRecordStructBar>(
            tsEngine,
            new InMemoryDatasetCoverageIndex<SerializableWrapper<string>>());
        return (store, engine);
    }

    [Fact]
    public async Task WriteAndReadBack_SingleDataset_ReturnsAllRows()
    {
        var (store, inner) = BuildStore(memTableCapacity: 4);
        var ds = new SerializableWrapper<string>("AAPL");
        var d0 = new DateOnly(2024, 1, 2);
        var records = Enumerable.Range(0, 10)
            .Select(i => MakeBar(d0.AddDays(i), close: 100m + i, open: 99m + i,
                high: 102m + i, low: 98m + i, adjClose: 100m + i, volume: 1_000_000L + (i * 1000L)))
            .ToList();

        await store.AppendAsync(ds, records).ConfigureAwait(true);
        await inner.FlushAsync().ConfigureAwait(true);

        var fetched = new List<ColumnarRecordStructBar>();
        await foreach (var r in store.GetRangeAsync(ds, d0, d0.AddDays(9), ColumnProjection.All).ConfigureAwait(true))
        {
            fetched.Add(r);
        }

        fetched.Should().HaveCount(10);
        for (var i = 0; i < 10; i++)
        {
            fetched[i].Date.Should().Be(records[i].Date, $"row {i} Date");
            fetched[i].Open.Should().Be(records[i].Open, $"row {i} Open");
            fetched[i].High.Should().Be(records[i].High, $"row {i} High");
            fetched[i].Low.Should().Be(records[i].Low, $"row {i} Low");
            fetched[i].Close.Should().Be(records[i].Close, $"row {i} Close");
            fetched[i].AdjustedClose.Should().Be(records[i].AdjustedClose, $"row {i} AdjustedClose");
            fetched[i].Volume.Should().Be(records[i].Volume, $"row {i} Volume");
        }
    }

    [Fact]
    public async Task WriteAndReadBack_PartialProjection_ReturnsOnlyProjectedFields()
    {
        var (store, inner) = BuildStore(memTableCapacity: 4);
        var ds = new SerializableWrapper<string>("MSFT");
        var d0 = new DateOnly(2024, 2, 1);
        var records = Enumerable.Range(0, 5)
            .Select(i => MakeBar(d0.AddDays(i), close: 200m + i, open: 199m + i,
                high: 202m + i, low: 198m + i, adjClose: 200m + i, volume: 2_000_000L + i))
            .ToList();

        await store.AppendAsync(ds, records).ConfigureAwait(true);
        await inner.FlushAsync().ConfigureAwait(true);

        // Project only Date + Close — all other fields should be default-valued.
        var projection = new ColumnProjection(["Date", "Close"]);

        var fetched = new List<ColumnarRecordStructBar>();
        await foreach (var r in store.GetRangeAsync(ds, d0, d0.AddDays(4), projection).ConfigureAwait(true))
        {
            fetched.Add(r);
        }

        fetched.Should().HaveCount(5);
        for (var i = 0; i < 5; i++)
        {
            fetched[i].Date.Should().Be(records[i].Date, $"row {i} Date should be projected");
            fetched[i].Close.Should().Be(records[i].Close, $"row {i} Close should be projected");
            // Unprojected fields should be default (0 for decimal/long)
            fetched[i].Open.Should().Be(0m, $"row {i} Open should be default (unprojected)");
            fetched[i].High.Should().Be(0m, $"row {i} High should be default (unprojected)");
            fetched[i].Low.Should().Be(0m, $"row {i} Low should be default (unprojected)");
            fetched[i].AdjustedClose.Should().Be(0m, $"row {i} AdjustedClose should be default (unprojected)");
            fetched[i].Volume.Should().Be(0L, $"row {i} Volume should be default (unprojected)");
        }
    }

    [Fact]
    public async Task WriteAndReadBack_DateRange_FiltersCorrectly()
    {
        var (store, inner) = BuildStore(memTableCapacity: 4);
        var ds = new SerializableWrapper<string>("GOOG");
        var d0 = new DateOnly(2024, 3, 1);
        var records = Enumerable.Range(0, 10)
            .Select(i => MakeBar(d0.AddDays(i), close: 300m + i))
            .ToList();

        await store.AppendAsync(ds, records).ConfigureAwait(true);
        await inner.FlushAsync().ConfigureAwait(true);

        // Read only rows 3–6 (inclusive)
        var from = d0.AddDays(3);
        var to = d0.AddDays(6);

        var fetched = new List<ColumnarRecordStructBar>();
        await foreach (var r in store.GetRangeAsync(ds, from, to, ColumnProjection.All).ConfigureAwait(true))
        {
            fetched.Add(r);
        }

        fetched.Should().HaveCount(4);
        fetched.Should().OnlyContain(r => r.Date >= from && r.Date <= to);
        fetched.Select(r => r.Close).Should().Equal(303m, 304m, 305m, 306m);
    }

    [Fact]
    public async Task WriteAndReadRowGroups_CrcValidation_OkAndQuarantine()
    {
        var format = new CompressedColumnarSegmentFormat<ColumnarRecordStructBar>(rowGroupMaxRows: 4);
        var d0 = new DateOnly(2024, 4, 1);
        var records = Enumerable.Range(0, 10)
            .Select(i => MakeBar(d0.AddDays(i), close: 400m + i))
            .ToList();

        using var stream = new MemoryStream();
        await format.WriteAsync(stream, records).ConfigureAwait(true);

        // Verify intact segment yields Ok for every row group.
        stream.Position = 0;
        var okResults = new List<RowGroupReadResult<ColumnarRecordStructBar>>();
        await foreach (var r in format.ReadRowGroupsAsync(
            stream, d0, d0.AddDays(9)).ConfigureAwait(true))
        {
            okResults.Add(r);
        }

        okResults.Should().NotBeEmpty();
        okResults.Should().OnlyContain(r => r.IsOk, "intact segment must yield Ok for all row groups");

        // Tamper one row group's CRC and verify Quarantined is returned for it.
        var bytes = stream.ToArray();
        var footerStream = new MemoryStream(bytes);
        var footer = FooterReader.Read(footerStream);
        var tamperedCrcs = footer.RowGroupCrcs!.ToArray();
        tamperedCrcs[0] ^= 0xFFFFFFFFu;
        var tamperedFooter = new SegmentFooter(
            footer.FormatVersion,
            footer.Columns,
            footer.RowGroups,
            footer.ZoneMaps,
            footer.BitmapIndexes,
            tamperedCrcs,
            footer.RowGroupByteOffsets);

        var oldFooterLengthPos = bytes.Length - SegmentMagic.TrailerSize;
        var oldFooterLength = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(oldFooterLengthPos, 4));
        var columnDataEnd = bytes.Length - SegmentMagic.TrailerSize - SegmentMagic.FooterCrcSize - (int)oldFooterLength;

        using var rebuilt = new MemoryStream();
        rebuilt.Write(bytes, 0, columnDataEnd);
        FooterWriter.Write(rebuilt, tamperedFooter);
        rebuilt.Position = 0;

        var tamperedResults = new List<RowGroupReadResult<ColumnarRecordStructBar>>();
        await foreach (var r in format.ReadRowGroupsAsync(
            rebuilt, d0, d0.AddDays(9)).ConfigureAwait(true))
        {
            tamperedResults.Add(r);
        }

        tamperedResults.Should().NotBeEmpty();
        tamperedResults[0].IsOk.Should().BeFalse("row group 0 has a tampered CRC");
        tamperedResults[0].RowGroupIndex.Should().Be(0);
        // Remaining row groups (if any) should be Ok
        tamperedResults.Skip(1).Should().OnlyContain(r => r.IsOk, "only row group 0 was tampered");
    }
}
