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
using Boutquin.Storage.Domain.ValueObjects.TimeSeries;
using Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar;
using Boutquin.Storage.Infrastructure.TimeSeries;
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar;

namespace Boutquin.Storage.Infrastructure.Tests.TimeSeries.Columnar;

/// <summary>
/// Validates <c>ColumnarTimeSeriesEngineBuilder.BuildAsync&lt;TDatasetKey,TRecord&gt;</c> —
/// the single-call factory that assembles the full columnar time-series stack. Covers
/// correctness of the default wiring, custom parameters, disposal delegation, argument
/// validation, and coverage-index plumbing.
/// </summary>
public sealed class ColumnarTimeSeriesEngineBuilderTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        $"BuilderTests_{Guid.NewGuid():N}");

    public ColumnarTimeSeriesEngineBuilderTests()
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

    private static SerializableWrapper<string> Ds(string ticker) => new(ticker);

    private static MarketDataBar MakeBar(DateOnly date, decimal close, decimal splitRatio = 1m)
        => new() { Date = date, Close = close, SplitRatio = splitRatio };

    // ──────────────────────────────────────────────────────────────────────────
    // Correctness — default wiring
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Build_DefaultsProduceWorkingStore()
    {
        await using var stack = await ColumnarTimeSeriesEngineBuilder.BuildAsync<SerializableWrapper<string>, MarketDataBar>(
            segmentFolder: _folder);

        var ds = Ds("AAPL");
        var d0 = new DateOnly(2024, 1, 1);
        var bars = Enumerable.Range(0, 10)
            .Select(i => MakeBar(d0.AddDays(i), 100m + i))
            .ToList();

        await stack.Store.AppendAsync(ds, bars);

        var fetched = new List<MarketDataBar>();
        await foreach (var r in stack.Store.GetRangeAsync(ds, d0, d0.AddDays(10), ColumnProjection.All))
        {
            fetched.Add(r);
        }

        fetched.Should().HaveCount(10);
        fetched.Select(r => r.Close).Should().Equal(bars.Select(r => r.Close));
    }

    [Fact]
    public async Task Build_ForceFlush_ProducesColumnarSegments()
    {
        await using var stack = await ColumnarTimeSeriesEngineBuilder.BuildAsync<SerializableWrapper<string>, MarketDataBar>(
            segmentFolder: _folder,
            segmentPrefix: "colbuild",
            memTableCapacity: 4);

        var ds = Ds("MSFT");
        var d0 = new DateOnly(2024, 1, 1);
        var bars = Enumerable.Range(0, 10)
            .Select(i => MakeBar(d0.AddDays(i), 200m + i))
            .ToList();

        await stack.Store.AppendAsync(ds, bars);   // triggers auto-flushes at capacity 4
        await stack.InnerEngine.FlushAsync();       // flush any residual memtable

        stack.InnerEngine.SegmentCount.Should().BeGreaterThan(
            0, "flushing past memtable capacity must produce at least one segment");
        Directory.GetFiles(_folder, "*.bsct").Should().NotBeEmpty(
            "columnar builder must produce .bsct segments");
        Directory.GetFiles(_folder, "*.dat").Should().BeEmpty(
            "columnar builder must not produce .dat row-format segments");

        var fetched = new List<MarketDataBar>();
        await foreach (var r in stack.Store.GetRangeAsync(ds, d0, d0.AddDays(10), ColumnProjection.All))
        {
            fetched.Add(r);
        }
        fetched.Should().HaveCount(10);
        fetched.Select(r => r.Close).Should().Equal(bars.Select(r => r.Close));
    }

    [Fact]
    public async Task Build_PartialProjection_ReturnsOnlyProjectedColumns()
    {
        await using var stack = await ColumnarTimeSeriesEngineBuilder.BuildAsync<SerializableWrapper<string>, MarketDataBar>(
            segmentFolder: _folder,
            memTableCapacity: 4);

        var ds = Ds("GOOG");
        var d0 = new DateOnly(2024, 3, 1);
        var bars = Enumerable.Range(0, 6)
            .Select(i => MakeBar(d0.AddDays(i), 500m + i) with { Open = 490m + i })
            .ToList();

        await stack.Store.AppendAsync(ds, bars);
        await stack.InnerEngine.FlushAsync();

        var projection = new ColumnProjection(["Date", "Close"]);
        var fetched = new List<MarketDataBar>();
        await foreach (var r in stack.Store.GetRangeAsync(ds, d0, d0.AddDays(6), projection))
        {
            fetched.Add(r);
        }

        fetched.Should().HaveCount(6);
        fetched.Select(r => r.Close).Should().Equal(bars.Select(r => r.Close));
        fetched.Select(r => r.Open).Should().AllSatisfy(
            open => open.Should().Be(0m, "Open is not projected — must be default(decimal)"));
    }

    [Fact]
    public async Task Build_DateRangeFilter_ExcludesOutOfRangeRows()
    {
        await using var stack = await ColumnarTimeSeriesEngineBuilder.BuildAsync<SerializableWrapper<string>, MarketDataBar>(
            segmentFolder: _folder,
            memTableCapacity: 4);

        var ds = Ds("TSLA");
        var d0 = new DateOnly(2024, 1, 1);
        var allBars = Enumerable.Range(0, 20)
            .Select(i => MakeBar(d0.AddDays(i), 300m + i))
            .ToList();

        await stack.Store.AppendAsync(ds, allBars);
        await stack.InnerEngine.FlushAsync();

        var from = d0.AddDays(5);
        var to = d0.AddDays(9);
        var fetched = new List<MarketDataBar>();
        await foreach (var r in stack.Store.GetRangeAsync(ds, from, to, ColumnProjection.All))
        {
            fetched.Add(r);
        }

        fetched.Should().HaveCount(5, "only rows with dates in [d0+5, d0+9] inclusive must be returned");
        fetched.First().Date.Should().Be(from);
        fetched.Last().Date.Should().Be(to);
        fetched.Should().AllSatisfy(r =>
        {
            r.Date.Should().BeOnOrAfter(from);
            r.Date.Should().BeOnOrBefore(to);
        });
    }

    [Fact]
    public async Task Build_CustomRowGroupSize_RespectedInFooter()
    {
        const int rowGroupMaxRows = 4;
        const int totalRows = 8; // 2 full row groups

        await using var stack = await ColumnarTimeSeriesEngineBuilder.BuildAsync<SerializableWrapper<string>, MarketDataBar>(
            segmentFolder: _folder,
            segmentPrefix: "rg",
            memTableCapacity: totalRows + 1,   // no auto-flush
            rowGroupMaxRows: rowGroupMaxRows);

        var ds = Ds("AMZN");
        var d0 = new DateOnly(2024, 1, 1);
        var bars = Enumerable.Range(0, totalRows)
            .Select(i => MakeBar(d0.AddDays(i), 150m + i))
            .ToList();

        await stack.Store.AppendAsync(ds, bars);
        await stack.InnerEngine.FlushAsync();

        var bsctPath = Directory.GetFiles(_folder, "rg*.bsct").Single();
        var blockBytes = ExtractFirstDatasetBlock(bsctPath);

        using var blockStream = new MemoryStream(blockBytes);
        var innerFormat = new CompressedColumnarSegmentFormat<MarketDataBar>(rowGroupMaxRows);
        var zoneMaps = innerFormat.ReadZoneMaps(blockStream);

        zoneMaps.Should().NotBeNull("zone maps are always written by CompressedColumnarSegmentFormat");
        zoneMaps!.Count.Should().Be(2,
            because: $"{totalRows} rows with rowGroupMaxRows={rowGroupMaxRows} must produce 2 row groups");
    }

    [Fact]
    public async Task Build_CustomCoverageIndex_IsUsed()
    {
        var mockIndex = new Mock<IDatasetCoverageIndex<SerializableWrapper<string>>>();
        mockIndex
            .Setup(x => x.GetAsync(It.IsAny<SerializableWrapper<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateRange?)null);
        mockIndex
            .Setup(x => x.RegisterAsync(It.IsAny<SerializableWrapper<string>>(), It.IsAny<DateOnly>(),
                It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mockIndex
            .Setup(x => x.SetAsync(It.IsAny<SerializableWrapper<string>>(), It.IsAny<DateRange?>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mockIndex
            .Setup(x => x.RebuildAsync(It.IsAny<IAsyncEnumerable<(SerializableWrapper<string>, DateOnly)>>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await using var stack = await ColumnarTimeSeriesEngineBuilder.BuildAsync<SerializableWrapper<string>, MarketDataBar>(
            segmentFolder: _folder,
            coverageIndex: mockIndex.Object);

        var ds = Ds("META");
        await stack.Store.AppendAsync(ds, [MakeBar(new DateOnly(2024, 1, 1), 400m)]);

        // GetCoverageAsync delegates the first lookup to the index.
        await stack.Store.GetCoverageAsync(ds);

        mockIndex.Verify(
            x => x.GetAsync(It.Is<SerializableWrapper<string>>(k => k.Value == "META"),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce(),
            "GetCoverageAsync must delegate to the supplied coverage index");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Disposal
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Build_Dispose_DisposesInnerEngine()
    {
        var stack = await ColumnarTimeSeriesEngineBuilder.BuildAsync<SerializableWrapper<string>, MarketDataBar>(
            segmentFolder: _folder);

        stack.Dispose();

        var act = async () => await stack.InnerEngine.FlushAsync();
        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Argument validation
    // ──────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Build_NullOrEmptySegmentFolder_Throws(string? folder)
    {
        var act = async () => await ColumnarTimeSeriesEngineBuilder.BuildAsync<SerializableWrapper<string>, MarketDataBar>(
            segmentFolder: folder!);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Build_NonPositiveMemTableCapacity_Throws(int capacity)
    {
        var act = async () => await ColumnarTimeSeriesEngineBuilder.BuildAsync<SerializableWrapper<string>, MarketDataBar>(
            segmentFolder: _folder,
            memTableCapacity: capacity);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Build_RowGroupMaxRowsAboveReadCeiling_Throws()
    {
        var act = async () => await ColumnarTimeSeriesEngineBuilder.BuildAsync<SerializableWrapper<string>, MarketDataBar>(
            segmentFolder: _folder,
            rowGroupMaxRows: (1 << 26) + 1);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>()
            .WithParameterName("rowGroupMaxRows");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Build_NonPositiveRowGroupMaxRows_Throws(int maxRows)
    {
        var act = async () => await ColumnarTimeSeriesEngineBuilder.BuildAsync<SerializableWrapper<string>, MarketDataBar>(
            segmentFolder: _folder,
            rowGroupMaxRows: maxRows);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Build_ZeroOrNegativeRowGroupMaxDateRange_Throws(int days)
    {
        var act = async () => await ColumnarTimeSeriesEngineBuilder.BuildAsync<SerializableWrapper<string>, MarketDataBar>(
            segmentFolder: _folder,
            rowGroupMaxDateRange: TimeSpan.FromDays(days));

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Non-date sort spec must not corrupt the per-row-group date span used by the
    // read-path date filter (F-WA-C04-1 regression guard).
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Build_NonDateSortSpec_DateRangeQuery_ReturnsSameRowsAsFullScan()
    {
        // A non-date ColumnSortSpec reorders records before they reach PlanRowGroups. If the
        // per-row-group MinDate/MaxDate are taken positionally (records[start]/records[end-1])
        // they become wrong — frequently inverted — and the read-path date filter
        // (rg.MaxDate < from || rg.MinDate > to) silently skips groups that contain matching
        // dates. The contract: a date-range read must return exactly the rows a full scan would,
        // independent of the on-disk sort order.
        const int rowGroupMaxRows = 3;

        await using var stack = await ColumnarTimeSeriesEngineBuilder.BuildAsync<SerializableWrapper<string>, MarketDataBar>(
            segmentFolder: _folder,
            segmentPrefix: "nds",
            memTableCapacity: 64,                // no auto-flush mid-append
            rowGroupMaxRows: rowGroupMaxRows,
            sortSpec: new ColumnSortSpec([("TradeCount", SortDirection.Ascending)]));

        var ds = Ds("SORT");
        var d0 = new DateOnly(2020, 1, 1);

        // Dates ascend, but TradeCount DESCENDS with date, so sorting by TradeCount ascending
        // fully reverses date order across row-group boundaries — the positional date span of
        // every row group is inverted.
        var bars = Enumerable.Range(0, 9)
            .Select(i => MakeBar(d0.AddDays(i * 10), 100m + i) with { TradeCount = 1000 - i })
            .ToList();

        await stack.Store.AppendAsync(ds, bars);
        await stack.InnerEngine.FlushAsync();

        // Full scan over the whole range — the date filter never fires, so this is the ground truth.
        var fullRangeFrom = d0;
        var fullRangeTo = d0.AddDays(100);
        var fullScan = new List<MarketDataBar>();
        await foreach (var r in stack.Store.GetRangeAsync(ds, fullRangeFrom, fullRangeTo, ColumnProjection.All))
        {
            fullScan.Add(r);
        }

        fullScan.Should().HaveCount(9, "every appended bar lies within the full range");

        // Now query a sub-range that must return a specific set of rows. With the positional-date
        // bug, the row groups holding these dates record inverted spans and are silently skipped.
        var subFrom = d0;                 // 2020-01-01
        var subTo = d0.AddDays(20);       // 2020-01-21 — first three dates inclusive
        var expectedSubset = fullScan
            .Where(r => r.Date >= subFrom && r.Date <= subTo)
            .OrderBy(r => r.Date)
            .ToList();

        expectedSubset.Should().HaveCount(3, "dates 01-01, 01-11, 01-21 fall in [subFrom, subTo]");

        var subScan = new List<MarketDataBar>();
        await foreach (var r in stack.Store.GetRangeAsync(ds, subFrom, subTo, ColumnProjection.All))
        {
            subScan.Add(r);
        }

        subScan.Select(r => r.Date).OrderBy(d => d)
            .Should().Equal(
                expectedSubset.Select(r => r.Date),
                "a date-range read must return exactly the in-range rows, regardless of on-disk sort order");
    }

    [Fact]
    public async Task Build_DateSortSpec_RowGroupPruning_StillPrunesAndReturnsCorrectRows()
    {
        // No-regression guard: with a date-ordered sort spec, row-group date pruning must still
        // produce correct results (the fix must not disable pruning for the date-ordered case).
        const int rowGroupMaxRows = 3;

        await using var stack = await ColumnarTimeSeriesEngineBuilder.BuildAsync<SerializableWrapper<string>, MarketDataBar>(
            segmentFolder: _folder,
            segmentPrefix: "dsp",
            memTableCapacity: 64,
            rowGroupMaxRows: rowGroupMaxRows,
            sortSpec: new ColumnSortSpec([("Date", SortDirection.Ascending)]));

        var ds = Ds("DATE");
        var d0 = new DateOnly(2020, 1, 1);
        var bars = Enumerable.Range(0, 9)
            .Select(i => MakeBar(d0.AddDays(i * 10), 100m + i))
            .ToList();

        await stack.Store.AppendAsync(ds, bars);
        await stack.InnerEngine.FlushAsync();

        // Query a sub-range that spans the middle three dates.
        var subFrom = d0.AddDays(30);   // 2020-01-31
        var subTo = d0.AddDays(50);     // 2020-02-20
        var expected = bars.Where(b => b.Date >= subFrom && b.Date <= subTo).ToList();
        expected.Should().HaveCount(3);

        var fetched = new List<MarketDataBar>();
        await foreach (var r in stack.Store.GetRangeAsync(ds, subFrom, subTo, ColumnProjection.All))
        {
            fetched.Add(r);
        }

        fetched.Select(r => r.Date).OrderBy(d => d)
            .Should().Equal(expected.Select(r => r.Date));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Private helpers
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Parses a <c>ColumnarSegmentFormat</c> <c>.bsct</c> file and extracts the raw bytes of
    /// the first dataset block. The returned bytes form a self-contained
    /// <c>CompressedColumnarSegmentFormat</c> stream that can be passed directly to
    /// <c>ReadZoneMaps</c> or <c>ReadRowGroupsAsync</c>.
    /// </summary>
    private static byte[] ExtractFirstDatasetBlock(string bsctPath)
    {
        using var fs = new FileStream(bsctPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var fileLength = fs.Length;

        // Trailer layout (last 8 bytes): uint32 footerLengthBytes + uint32 trailerMagic
        fs.Seek(-8, SeekOrigin.End);
        var trailer = new byte[8];
        fs.ReadExactly(trailer);
        var footerLengthBytes = BinaryPrimitives.ReadUInt32LittleEndian(trailer);

        // Footer starts at: fileLength - 8 (trailer) - footerLengthBytes
        fs.Seek(fileLength - 8 - footerLengthBytes, SeekOrigin.Begin);

        // Read datasetCount (uint32)
        var buf4 = new byte[4];
        fs.ReadExactly(buf4);

        // Skip the segment-level maxSequenceNumber (uint64, R2-G) written immediately after
        // datasetCount, before the per-dataset block index.
        fs.Seek(8, SeekOrigin.Current);

        // Read first dataset entry: keyByteLength (u32) + keyBytes + blockByteOffset (i64) + blockByteLength (i64)
        fs.ReadExactly(buf4);
        var keyByteLength = BinaryPrimitives.ReadUInt32LittleEndian(buf4);
        fs.Seek(keyByteLength, SeekOrigin.Current); // skip key bytes

        var buf8 = new byte[8];
        fs.ReadExactly(buf8);
        var blockByteOffset = BinaryPrimitives.ReadInt64LittleEndian(buf8);
        fs.ReadExactly(buf8);
        var blockByteLength = BinaryPrimitives.ReadInt64LittleEndian(buf8);

        // Extract the raw CompressedColumnarSegmentFormat block
        fs.Seek(blockByteOffset, SeekOrigin.Begin);
        var blockBytes = new byte[blockByteLength];
        fs.ReadExactly(blockBytes);
        return blockBytes;
    }
    [Fact]
    public async Task GetRange_ProjectedSubset_RecordWithoutPublicDateProperty_FailsFastNotSilentlyEmpty()
    {
        // The projected-range fast path drives the columnar segment's internal date filter by
        // augmenting the projection with the literal column name "Date". ExplicitDateBar
        // satisfies IHasDate via an explicit interface implementation — its date column is
        // "TradeDate", and no "Date" column exists. The phantom-column augmentation previously
        // surfaced as a ColumnNotFoundException("Date") from deep inside the segment read —
        // blaming a column the caller never projected, with no hint that the record's shape is
        // the problem. The engine must fail fast at its boundary with an actionable error.
        await using var stack = await ColumnarTimeSeriesEngineBuilder.BuildAsync<SerializableWrapper<string>, ExplicitDateBar>(
            segmentFolder: _folder,
            segmentPrefix: "expl",
            memTableCapacity: 2,
            sortSpec: new ColumnSortSpec([("TradeDate", SortDirection.Ascending)]));

        var ds = Ds("XDAT");
        var d0 = new DateOnly(2024, 1, 1);
        var bars = Enumerable.Range(0, 3)
            .Select(i => new ExplicitDateBar { TradeDate = d0.AddDays(i), Value = i })
            .ToList();
        await stack.Store.AppendAsync(ds, bars);
        await stack.InnerEngine.FlushAsync();

        // The unprojected path works — IHasDate is consumed through the interface.
        var all = new List<ExplicitDateBar>();
        await foreach (var r in stack.Store.GetRangeAsync(ds, d0, d0.AddDays(10), ColumnProjection.All))
        {
            all.Add(r);
        }
        all.Should().HaveCount(3);

        // The projected-subset path must fail fast, not silently return nothing.
        var act = async () =>
        {
            var projected = new List<ExplicitDateBar>();
            await foreach (var r in stack.Store.GetRangeAsync(
                ds, d0, d0.AddDays(10), new ColumnProjection(["Value"])))
            {
                projected.Add(r);
            }
            return projected;
        };

        await act.Should().ThrowAsync<InvalidOperationException>(
                "the contract violation must be attributed to the record shape, not a phantom column")
            .WithMessage("*Date*");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Value-log dispatch — a columnar segment cannot hold a value-log pointer
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Build_RecordAboveDefaultValueLogThreshold_FlushesAndSurvivesReopen()
    {
        // 4,000 chars serialise to well over the engine's 1 KiB default value-log threshold. The
        // columnar envelope stores only inline records, so the record must stay inline: an
        // acknowledged append that becomes a value-log pointer faults every later flush and is
        // lost on reopen.
        var ds = Ds("NOTE");
        var d0 = new DateOnly(2024, 1, 1);
        var note = new string('n', 4_000);

        await using (var stack = await ColumnarTimeSeriesEngineBuilder.BuildAsync<SerializableWrapper<string>, TickerNoteBar>(
            segmentFolder: _folder, memTableCapacity: 1_000))
        {
            await stack.Store.AppendAsync(ds, [new TickerNoteBar { Date = d0, Ticker = "AAPL", Note = note }]);

            var flush = async () => await stack.InnerEngine.FlushAsync();
            await flush.Should().NotThrowAsync("an acknowledged columnar append must be flushable");
        }

        await using var reopened = await ColumnarTimeSeriesEngineBuilder.BuildAsync<SerializableWrapper<string>, TickerNoteBar>(
            segmentFolder: _folder, memTableCapacity: 1_000);
        var fetched = new List<TickerNoteBar>();
        await foreach (var r in reopened.Store.GetRangeAsync(ds, d0, d0, ColumnProjection.All))
        {
            fetched.Add(r);
        }

        fetched.Should().ContainSingle().Which.Note.Should().Be(note);
    }
}

/// <summary>
/// Columnar fixture with a free-text column (whose size drives the engine's value-log threshold) and
/// a <c>[BitmapIndex]</c> string column (whose UTF-8 length is capped by the footer's key-length
/// field). Serialisation is hand-rolled because the storage generator targets record structs only.
/// </summary>
[ColumnarStorable]
public partial record TickerNoteBar : IHasDate, ISerializable<TickerNoteBar>
{
    public DateOnly Date { get; init; }

    [BitmapIndex]
    public string Ticker { get; init; } = "";

    public string Note { get; init; } = "";

    public TickerNoteBar() { }

    public void Serialize(Stream stream)
    {
        // Lenient on purpose: a null coerces to empty and an unpaired surrogate to U+FFFD, so the
        // columnar format's own limits are what a write admission has to enforce.
        var ticker = Encoding.UTF8.GetBytes(Ticker ?? "");
        var note = Encoding.UTF8.GetBytes(Note ?? "");
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(header[..4], Date.DayNumber);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..8], ticker.Length);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], note.Length);
        stream.Write(header);
        stream.Write(ticker);
        stream.Write(note);
    }

    public static TickerNoteBar Deserialize(Stream stream)
    {
        Span<byte> header = stackalloc byte[12];
        stream.ReadExactly(header);
        var ticker = new byte[BinaryPrimitives.ReadInt32LittleEndian(header[4..8])];
        var note = new byte[BinaryPrimitives.ReadInt32LittleEndian(header[8..])];
        stream.ReadExactly(ticker);
        stream.ReadExactly(note);
        return new TickerNoteBar
        {
            Date = DateOnly.FromDayNumber(BinaryPrimitives.ReadInt32LittleEndian(header[..4])),
            Ticker = Encoding.UTF8.GetString(ticker),
            Note = Encoding.UTF8.GetString(note),
        };
    }
}
