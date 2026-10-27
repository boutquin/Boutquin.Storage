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
using System.Text.RegularExpressions;
using Boutquin.Storage.Domain.Attributes;
using Boutquin.Storage.Domain.Interfaces.TimeSeries;
using Boutquin.Storage.Domain.ValueObjects.LsmTree;
using Boutquin.Storage.Domain.ValueObjects.TimeSeries;
using Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar;
using Boutquin.Storage.Domain.ValueObjects.ValueLog;
using Boutquin.Storage.Infrastructure.Tests.TimeSeries.Columnar;
using Boutquin.Storage.Infrastructure.TimeSeries;
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar;

namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// The columnar write contract: every record a columnar stack acknowledges must be flushable and
/// must read back after a reopen. A record or configuration the segment writer could never encode
/// or describe is refused before anything is acknowledged — never accepted and then lost when the
/// flush that carries it faults the engine.
/// </summary>
public sealed class ColumnarWriteAdmissionContractTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        $"ColumnarWriteAdmission_{Guid.NewGuid():N}");

    public ColumnarWriteAdmissionContractTests()
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

    private static SerializableWrapper<string> Ds(string name) => new(name);

    private Task<ColumnarTimeSeriesStack<SerializableWrapper<string>, ScaledPriceBar>> BuildAsync(
        int rowGroupMaxRows = 1 << 17,
        TimeSpan? rowGroupMaxDateRange = null,
        ColumnSortSpec? sortSpec = null,
        int memTableCapacity = 10_000)
        => ColumnarTimeSeriesEngineBuilder.BuildAsync<SerializableWrapper<string>, ScaledPriceBar>(
            segmentFolder: _folder,
            memTableCapacity: memTableCapacity,
            rowGroupMaxRows: rowGroupMaxRows,
            rowGroupMaxDateRange: rowGroupMaxDateRange,
            sortSpec: sortSpec);

    private static async Task<List<ScaledPriceBar>> ReadAllAsync(
        ColumnarTimeSeriesStack<SerializableWrapper<string>, ScaledPriceBar> stack,
        SerializableWrapper<string> ds)
    {
        var rows = new List<ScaledPriceBar>();
        await foreach (var r in stack.Store.GetRangeAsync(ds, DateOnly.MinValue, DateOnly.MaxValue, ColumnProjection.All))
        {
            rows.Add(r);
        }

        return rows;
    }

    // ───── ScaledIntegerBitPacked decimals ────────────────────────────────

    [Fact]
    public async Task ScaledDecimal_OutsideInt64AtEveryScale_IsFlushedAndReadBackAfterReopen()
    {
        // 1e20 does not fit Int64 at any scale, so no scaled-integer packing can hold it. The value
        // was acknowledged, so the flush must still write it.
        var ds = Ds("WIDE");
        var d0 = new DateOnly(2024, 1, 1);
        ScaledPriceBar[] bars =
        [
            new() { Date = d0, Px = 100_000_000_000_000_000_000m },
            new() { Date = d0.AddDays(1), Px = -100_000_000_000_000_000_000m },
        ];

        await using (var stack = await BuildAsync())
        {
            await stack.Store.AppendAsync(ds, bars);
            var flush = async () => await stack.InnerEngine.FlushAsync();
            await flush.Should().NotThrowAsync("an acknowledged ScaledIntegerBitPacked value must be flushable");
        }

        await using var reopened = await BuildAsync();
        (await ReadAllAsync(reopened, ds)).Select(b => b.Px).Should().Equal(bars.Select(b => b.Px));
    }

    [Fact]
    public async Task ScaledDecimals_WhoseCommonScaleOverflowsInt64_AreFlushedAndReadBackAfterReopen()
    {
        // Each value alone packs into Int64; together the common scale (10^9) pushes 1e10 to 1e19,
        // past Int64. No per-record admission can see this pairing, so the flush must handle it.
        var ds = Ds("PAIR");
        var d0 = new DateOnly(2024, 1, 1);
        ScaledPriceBar[] bars =
        [
            new() { Date = d0, Px = 10_000_000_000m },
            new() { Date = d0.AddDays(1), Px = 0.000000001m },
        ];

        await using (var stack = await BuildAsync())
        {
            await stack.Store.AppendAsync(ds, bars);
            var flush = async () => await stack.InnerEngine.FlushAsync();
            await flush.Should().NotThrowAsync("two individually encodable values must flush together");
        }

        await using var reopened = await BuildAsync();
        (await ReadAllAsync(reopened, ds)).Select(b => b.Px).Should().Equal(bars.Select(b => b.Px));
    }

    // ───── Row-group count ────────────────────────────────────────────────

    [Fact]
    public async Task DatasetBlock_PlanningMoreRowGroupsThanTheFooterHolds_IsFlushedAndReadBackAfterReopen()
    {
        // rowGroupMaxRows = 1 asks for one row group per row; the footer's row-group count field is
        // 16 bits wide, so 65,536 rows of one dataset exceed what a plan honouring the knob could
        // describe. Every row was acknowledged, so the block must still be written.
        const int Rows = ushort.MaxValue + 1;
        var ds = Ds("DENSE");
        var d0 = new DateOnly(1900, 1, 1);
        var bars = new ScaledPriceBar[Rows];
        for (var i = 0; i < Rows; i++)
        {
            bars[i] = new ScaledPriceBar { Date = d0.AddDays(i), Px = i * 0.01m };
        }

        await using (var stack = await BuildAsync(rowGroupMaxRows: 1, memTableCapacity: Rows + 16))
        {
            await stack.Store.AppendAsync(ds, bars);
            var flush = async () => await stack.InnerEngine.FlushAsync();
            await flush.Should().NotThrowAsync("an acknowledged block must be describable by the segment footer");
        }

        await using var reopened = await BuildAsync(rowGroupMaxRows: 1, memTableCapacity: Rows + 16);
        var rows = await ReadAllAsync(reopened, ds);
        rows.Should().HaveCount(Rows);
        rows[^1].Should().Be(bars[^1]);
    }

    // ───── Sort spec ──────────────────────────────────────────────────────

    [Fact]
    public async Task SortSpec_NamingAColumnTheRecordDoesNotHave_IsRefusedBeforeAnyWrite()
    {
        // The sort spec is applied to every block at flush; a column name that does not resolve
        // against the record would fault every flush after the writes were acknowledged.
        var build = async () => await BuildAsync(sortSpec: new ColumnSortSpec([("Pxx", SortDirection.Ascending)]));

        await build.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*Pxx*");
    }

    // ───── Manual-wiring recipe in the columnar guide ───────────────────

    [Fact]
    public void Docs_NoColumnarWiringExamplePassesANullValueLog()
    {
        // A null ValueLog resolves to ValueLogOptions.Default (1 KiB threshold), which diverts
        // ordinary records to the value log; the columnar envelope refuses those pointers at flush,
        // after the write was acknowledged. No public guide may show that wiring.
        var root = RepoRoot();
        var surfaces = Directory.GetFiles(Path.Combine(root, "docs"), "*.md", SearchOption.AllDirectories)
            .Append(Path.Combine(root, "README.md"));
        var hits = new List<string>();
        foreach (var path in surfaces)
        {
            var lines = File.ReadAllLines(path);
            for (var i = 0; i < lines.Length; i++)
            {
                if (Regex.IsMatch(lines[i], @"ValueLog:\s*null\b"))
                {
                    hits.Add($"{Path.GetRelativePath(root, path)}:{i + 1}");
                }
            }
        }

        hits.Should().BeEmpty("a columnar stack wired with ValueLog: null loses every record over 1 KiB");
    }

    [Fact]
    public async Task Docs_ManualWiringRecipe_KeepsARecordAboveTheDefaultValueLogThreshold()
    {
        // Executes the guide's manual wiring with the value-log setting read from the guide itself:
        // a 2 KiB record must be acknowledged, flushed and read back after a reopen.
        var valueLog = DocumentedManualWiringValueLog();
        var ds = Ds("NOTE");
        var d0 = new DateOnly(2024, 1, 1);
        var note = new string('n', 2_048);

        await using (var store = await OpenManualWiringAsync(valueLog))
        {
            await store.Store.AppendAsync(ds, [new TickerNoteBar { Date = d0, Ticker = "AAPL", Note = note }]);
            var flush = async () => await store.Engine.FlushAsync();
            await flush.Should().NotThrowAsync("the documented recipe must keep an acknowledged record flushable");
        }

        await using var reopened = await OpenManualWiringAsync(valueLog);
        var fetched = new List<TickerNoteBar>();
        await foreach (var r in reopened.Store.GetRangeAsync(ds, d0, d0, ColumnProjection.All))
        {
            fetched.Add(r);
        }

        fetched.Should().ContainSingle().Which.Note.Should().Be(note);
    }

    // The ValueLog argument of step 3 of the guide's manual wiring: null, or the inline-only
    // `ValueLogOptions.Default with { ValueLogThresholdBytes = …, MaxEntryBytes = … }` it names.
    private static ValueLogOptions? DocumentedManualWiringValueLog()
    {
        var guide = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "columnar-time-series.md"));
        var start = guide.IndexOf("### Manual wiring", StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, "the guide carries a manual-wiring section");
        var end = guide.IndexOf("\n### ", start + 1, StringComparison.Ordinal);
        var section = guide[start..(end < 0 ? guide.Length : end)];

        var argument = Regex.Match(section, @"ValueLog:\s*(\w+)");
        argument.Success.Should().BeTrue("the manual wiring passes a ValueLog argument to LsmStorageEngineOptions");
        if (argument.Groups[1].Value == "null")
        {
            return null;
        }

        return ValueLogOptions.Default with
        {
            ValueLogThresholdBytes = (int)DocumentedProduct(section, "ValueLogThresholdBytes"),
            MaxEntryBytes = DocumentedProduct(section, "MaxEntryBytes"),
        };
    }

    private static long DocumentedProduct(string section, string member)
    {
        var match = Regex.Match(section, member + @"\s*=\s*([0-9_ *]+),?");
        match.Success.Should().BeTrue($"the manual wiring sets {member}");
        return match.Groups[1].Value
            .Split('*', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(f => long.Parse(f.Replace("_", "", StringComparison.Ordinal), System.Globalization.CultureInfo.InvariantCulture))
            .Aggregate(1L, (a, b) => a * b);
    }

    private async Task<ManualStack> OpenManualWiringAsync(ValueLogOptions? valueLog)
    {
        // Steps 1–5 of the guide's manual wiring, verbatim apart from the folder.
        var innerFormat = new CompressedColumnarSegmentFormat<TickerNoteBar>();
        var segmentFormat = new ColumnarSegmentFormat<SerializableWrapper<string>, TickerNoteBar>(innerFormat);
        var lsmOptions = new LsmStorageEngineOptions<TimeSeriesKey<SerializableWrapper<string>>, TickerNoteBar>(
            MemTableCapacity: 10_000,
            SegmentFolder: _folder,
            SegmentPrefix: "bars",
            SegmentFormat: segmentFormat,
            ValueLog: valueLog);
        var engine = await LsmStorageEngine<TimeSeriesKey<SerializableWrapper<string>>, TickerNoteBar>
            .OpenAsync(lsmOptions);
        var options = new ColumnarTimeSeriesStoreOptions<TickerNoteBar>(innerFormat);
        var tsEngine = new LsmTimeSeriesStorageEngine<SerializableWrapper<string>, TickerNoteBar>(engine, options);
        var store = new LsmTimeSeriesStore<SerializableWrapper<string>, TickerNoteBar>(
            tsEngine,
            new InMemoryDatasetCoverageIndex<SerializableWrapper<string>>());
        return new ManualStack(engine, store);
    }

    private sealed class ManualStack(
        LsmStorageEngine<TimeSeriesKey<SerializableWrapper<string>>, TickerNoteBar> engine,
        LsmTimeSeriesStore<SerializableWrapper<string>, TickerNoteBar> store) : IAsyncDisposable
    {
        public LsmStorageEngine<TimeSeriesKey<SerializableWrapper<string>>, TickerNoteBar> Engine { get; } = engine;

        public LsmTimeSeriesStore<SerializableWrapper<string>, TickerNoteBar> Store { get; } = store;

        public ValueTask DisposeAsync() => Engine.DisposeAsync();
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Boutquin.Storage.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException(
            "Could not locate the repository root from " + AppContext.BaseDirectory);
    }
}

/// <summary>
/// Columnar fixture whose single value column uses the scaled-integer bit-packed decimal codec.
/// Serialisation is hand-rolled because the storage generator targets record structs only.
/// </summary>
[ColumnarStorable]
public partial record ScaledPriceBar : IHasDate, ISerializable<ScaledPriceBar>
{
    public DateOnly Date { get; init; }

    [ColumnEncoding(ColumnEncoding.ScaledIntegerBitPacked)]
    public decimal Px { get; init; }

    public ScaledPriceBar() { }

    public void Serialize(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[20];
        BinaryPrimitives.WriteInt32LittleEndian(buffer[..4], Date.DayNumber);
        Span<int> bits = stackalloc int[4];
        _ = decimal.GetBits(Px, bits);
        for (var i = 0; i < 4; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(4 + (i * 4), 4), bits[i]);
        }

        stream.Write(buffer);
    }

    public static ScaledPriceBar Deserialize(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[20];
        stream.ReadExactly(buffer);
        Span<int> bits = stackalloc int[4];
        for (var i = 0; i < 4; i++)
        {
            bits[i] = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(4 + (i * 4), 4));
        }

        return new ScaledPriceBar
        {
            Date = DateOnly.FromDayNumber(BinaryPrimitives.ReadInt32LittleEndian(buffer[..4])),
            Px = new decimal(bits),
        };
    }
}
