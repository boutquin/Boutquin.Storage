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
using Boutquin.Storage.Domain.Interfaces.TimeSeries.Columnar;
using Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar;
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar;

namespace Boutquin.Storage.Infrastructure.Tests.TimeSeries.Columnar;

public sealed class CompressedColumnarSegmentFormatTests
{
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

    private static async Task<List<ColumnarBar>> ReadAllAsync(
        CompressedColumnarSegmentFormat<ColumnarBar> format,
        Stream input,
        DateOnly from,
        DateOnly to,
        ColumnProjection? projection = null)
    {
        var list = new List<ColumnarBar>();
        await foreach (var r in format.ReadAsync(input, from, to, projection ?? ColumnProjection.All).ConfigureAwait(true))
        {
            list.Add(r);
        }

        return list;
    }

    // ───── Construction ───────────────────────────────────────────────

    [Fact]
    public void Constructor_DiscoversColumnsFromGeneratedPlan()
    {
        var act = () => new CompressedColumnarSegmentFormat<ColumnarBar>();
        act.Should().NotThrow();
    }

    // Every read path refuses a row group declaring more rows than the decode ceiling, so a
    // writer configured above it would emit segments its own reader rejects as corrupt. The
    // constructor must refuse that configuration; the ceiling itself stays writable.
    [Fact]
    public void Constructor_RowGroupMaxRowsAboveReadCeiling_ThrowsArgumentOutOfRange()
    {
        const int aboveCeiling = (1 << 26) + 1;
        var act = () => new CompressedColumnarSegmentFormat<ColumnarBar>(rowGroupMaxRows: aboveCeiling);
        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("rowGroupMaxRows");
    }

    [Fact]
    public void Constructor_RowGroupMaxRowsAtReadCeiling_IsAccepted()
    {
        var act = () => new CompressedColumnarSegmentFormat<ColumnarBar>(rowGroupMaxRows: 1 << 26);
        act.Should().NotThrow();
    }

    [Fact]
    public void Constructor_OnTypeWithoutColumnarStorable_ThrowsInvalidOperation()
    {
        Action act = () => new CompressedColumnarSegmentFormat<RecordWithoutPlan>();
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*ColumnarFieldPlan*");
    }

    // ───── Round-trip (all codecs exercised via ColumnarBar) ──────────

    [Fact]
    public async Task RoundTrip_SingleRowGroup_AllCodecs_RecoverBitwise()
    {
        var format = new CompressedColumnarSegmentFormat<ColumnarBar>();
        var input = Enumerable.Range(0, 10).Select(i => MakeBar(2020, 1, 1 + i, i)).ToList();

        using var stream = new MemoryStream();
        await format.WriteAsync(stream, input).ConfigureAwait(true);
        stream.Position = 0;

        var recovered = await ReadAllAsync(format, stream,
            new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31)).ConfigureAwait(true);

        recovered.Should().Equal(input);
    }

    [Fact]
    public async Task RoundTrip_MultipleRowGroups_RecoversAllRecords()
    {
        var format = new CompressedColumnarSegmentFormat<ColumnarBar>(rowGroupMaxRows: 4);
        var input = Enumerable.Range(0, 15).Select(i => MakeBar(2020, 1, 1 + i, i)).ToList();

        using var stream = new MemoryStream();
        await format.WriteAsync(stream, input).ConfigureAwait(true);
        stream.Position = 0;

        var recovered = await ReadAllAsync(format, stream,
            new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31)).ConfigureAwait(true);

        recovered.Should().Equal(input);
    }

    [Fact]
    public async Task RoundTrip_MorePlannedRowGroupsThanTheFooterHolds_WritesADescribableSegment()
    {
        // rowGroupMaxRows = 1 plans one row group per row, but the footer's row-group count field
        // is 16 bits wide. A 65,536-row block must still be written, read back whole, and carry no
        // more row groups than the footer can describe. (ScaledPriceBar's two columns slice-decode
        // in time proportional to the slice, so reading tens of thousands of row groups stays fast.)
        const int Rows = ushort.MaxValue + 1;
        var format = new CompressedColumnarSegmentFormat<ScaledPriceBar>(rowGroupMaxRows: 1);
        var start = new DateOnly(1900, 1, 1);
        var input = Enumerable.Range(0, Rows)
            .Select(i => new ScaledPriceBar { Date = start.AddDays(i), Px = i * 0.25m })
            .ToList();

        using var stream = new MemoryStream();
        var write = async () => await format.WriteAsync(stream, input).ConfigureAwait(true);
        await write.Should().NotThrowAsync("the footer must describe every block the writer plans").ConfigureAwait(true);

        var recovered = new List<ScaledPriceBar>();
        var rowGroups = 0;
        stream.Position = 0;
        await foreach (var rg in format.ReadRowGroupsAsync(stream, start, start.AddDays(Rows)).ConfigureAwait(true))
        {
            rg.IsOk.Should().BeTrue();
            recovered.AddRange(rg.Rows!);
            rowGroups++;
        }

        rowGroups.Should().BeLessThanOrEqualTo(ushort.MaxValue);
        recovered.Should().Equal(input);
    }

    [Fact]
    public async Task RangeRead_NonDateOrderedInput_ReturnsAllInRangeRows()
    {
        // A non-date ColumnSortSpec hands PlanRowGroups records that are NOT date-ordered.
        // If the per-row-group MinDate/MaxDate are recorded positionally (records[start].Date /
        // records[end-1].Date) they become wrong — frequently inverted — and the read-path
        // date filter (rg.MaxDate < from || rg.MinDate > to) silently skips groups that contain
        // matching dates. WriteAsync does not sort; feeding deliberately date-unordered records
        // reproduces exactly the array PlanRowGroups sees after the envelope's ApplySortSpec.
        var format = new CompressedColumnarSegmentFormat<ColumnarBar>(rowGroupMaxRows: 3);

        // Dates 2020-01-01 .. 2020-01-09, but presented in fully REVERSED order — every row
        // group's positional [first.Date, last.Date] span is inverted (MinDate > MaxDate).
        var input = Enumerable.Range(0, 9)
            .Select(i => MakeBar(2020, 1, 1 + i, i))
            .Reverse()
            .ToList();

        using var stream = new MemoryStream();
        await format.WriteAsync(stream, input).ConfigureAwait(true);
        stream.Position = 0;

        // Query the first three calendar dates. With the positional-date bug these live in a
        // row group whose recorded span is inverted, so the filter skips it and returns nothing.
        var from = new DateOnly(2020, 1, 1);
        var to = new DateOnly(2020, 1, 3);
        var recovered = await ReadAllAsync(format, stream, from, to).ConfigureAwait(true);

        recovered.Select(r => r.Date).OrderBy(d => d).Should().Equal(
            new DateOnly(2020, 1, 1), new DateOnly(2020, 1, 2), new DateOnly(2020, 1, 3));
    }

    [Fact]
    public async Task RoundTrip_EmptyInput_ProducesParseableSegment()
    {
        var format = new CompressedColumnarSegmentFormat<ColumnarBar>();
        using var stream = new MemoryStream();
        await format.WriteAsync(stream, []).ConfigureAwait(true);
        stream.Position = 0;

        var recovered = await ReadAllAsync(format, stream,
            new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31)).ConfigureAwait(true);

        recovered.Should().BeEmpty();
    }

    [Fact]
    public async Task CompressedFormat_ProducesCompactOutput_OnRealisticBarData()
    {
        var format = new CompressedColumnarSegmentFormat<ColumnarBar>();

        var bars = Enumerable.Range(0, 252)
            .Select(i => MakeBar(2020, 1, 1, i).WithDateOffset(i))
            .ToList();

        using var stream = new MemoryStream();
        await format.WriteAsync(stream, bars).ConfigureAwait(true);

        // Raw estimate: 252 rows × ~57 bytes/row (4 date + 8 string + 16 decimal + 8 long
        // + 4 int + 8 double + 1 bool + 8 double) ≈ 14 364 bytes uncompressed.
        // The codec pipeline (delta-of-delta + VLQ on dates, Gorilla XOR on doubles,
        // bitmap RLE on bools) must beat raw by at least 1.6×.
        stream.Length.Should().BeLessThan(9_000,
            "codec-based compression must compress 252 daily-bar rows to at least 1.6× below raw");
    }

    // ───── Projection ─────────────────────────────────────────────────

    [Fact]
    public async Task Projection_SubsetOfColumns_LeavesUnprojectedFieldsAtDefault()
    {
        var format = new CompressedColumnarSegmentFormat<ColumnarBar>();
        var input = new[] { MakeBar(2020, 1, 1, 42, ticker: "MSFT") };

        using var stream = new MemoryStream();
        await format.WriteAsync(stream, input).ConfigureAwait(true);
        stream.Position = 0;

        var recovered = await ReadAllAsync(format, stream,
            new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31),
            new ColumnProjection(["Date", "Close"])).ConfigureAwait(true);

        recovered.Should().ContainSingle();
        recovered[0].Date.Should().Be(input[0].Date);
        recovered[0].Close.Should().Be(input[0].Close);
        // Unprojected fields stay at default(TField).
        // Ticker's default is "" (property initializer on ColumnarBar), not null.
        recovered[0].Ticker.Should().BeEmpty();
        recovered[0].Volume.Should().Be(0L);
        recovered[0].Vwap.Should().Be(0.0);
    }

    [Fact]
    public async Task Projection_StrictMissingColumn_ThrowsColumnNotFound()
    {
        var format = new CompressedColumnarSegmentFormat<ColumnarBar>();
        var input = new[] { MakeBar(2020, 1, 1, 42) };

        using var stream = new MemoryStream();
        await format.WriteAsync(stream, input).ConfigureAwait(true);
        stream.Position = 0;

        var enumerator = format.ReadAsync(stream,
            new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31),
            new ColumnProjection(["Date", "NonexistentColumn"])).GetAsyncEnumerator();

        var act = async () => await enumerator.MoveNextAsync().ConfigureAwait(true);
        (await act.Should().ThrowAsync<Boutquin.Storage.Domain.Exceptions.ColumnNotFoundException>().ConfigureAwait(true))
            .Which.ColumnName.Should().Be("NonexistentColumn");
        await enumerator.DisposeAsync().ConfigureAwait(true);
    }

    // ───── Helpers ────────────────────────────────────────────────────

    // Hand-implements IColumnarRehydratable<T> but deliberately omits [ColumnarStorable] so the
    // static ColumnarFieldPlan property is absent — constructor must still throw.
    public sealed record RecordWithoutPlan
        : Boutquin.Storage.Domain.Interfaces.TimeSeries.IHasDate,
          IColumnarRehydratable<RecordWithoutPlan>
    {
        public DateOnly Date { get; init; }
        public RecordWithoutPlan() { }

        public static RecordWithoutPlan Rehydrate(
            IReadOnlyDictionary<string, Array> columns, int rowIndex)
            => new();
    }

    // ───── ReadRowGroupsAsync argument validation ─────────────────────

    [Fact]
    public async Task ReadRowGroupsAsync_DescendingScanDirection_Throws()
    {
        var format = new CompressedColumnarSegmentFormat<ColumnarBar>();
        var input = Enumerable.Range(0, 5).Select(i => MakeBar(2020, 1, 1 + i, i)).ToList();

        using var stream = new MemoryStream();
        await format.WriteAsync(stream, input).ConfigureAwait(true);
        stream.Position = 0;

        Func<Task> act = async () =>
        {
            await foreach (var _ in format.ReadRowGroupsAsync(
                stream, new DateOnly(2020, 1, 1), new DateOnly(2020, 1, 31),
                direction: (ScanDirection)99).ConfigureAwait(true))
            {
                break;
            }
        };
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task ReadRowGroupsAsync_FromAfterTo_YieldsNothing()
    {
        var format = new CompressedColumnarSegmentFormat<ColumnarBar>();
        var input = Enumerable.Range(0, 5).Select(i => MakeBar(2020, 1, 1 + i, i)).ToList();

        using var stream = new MemoryStream();
        await format.WriteAsync(stream, input).ConfigureAwait(true);
        stream.Position = 0;

        var count = 0;
        await foreach (var _ in format.ReadRowGroupsAsync(
            stream, from: new DateOnly(2020, 12, 31), to: new DateOnly(2020, 1, 1)).ConfigureAwait(true))
        {
            count++;
        }
        count.Should().Be(0);
    }

    [Fact]
    public async Task ReadRowGroupsAsync_NullStream_Throws()
    {
        var format = new CompressedColumnarSegmentFormat<ColumnarBar>();
        Func<Task> act = async () =>
        {
            await foreach (var _ in format.ReadRowGroupsAsync(
                null!, new DateOnly(2020, 1, 1), new DateOnly(2020, 1, 31)).ConfigureAwait(true))
            {
                break;
            }
        };
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task ReadRowGroupsAsync_FilteredByDate_SkipsOutOfRangeRowGroups()
    {
        // Multiple row groups; request a date range that falls outside all of them.
        var format = new CompressedColumnarSegmentFormat<ColumnarBar>(rowGroupMaxRows: 4);
        var input = Enumerable.Range(0, 12)
            .Select(i => MakeBar(2020, 1, 1 + i, i))
            .ToList();

        using var stream = new MemoryStream();
        await format.WriteAsync(stream, input).ConfigureAwait(true);
        stream.Position = 0;

        var count = 0;
        await foreach (var rg in format.ReadRowGroupsAsync(
            stream, from: new DateOnly(2025, 1, 1), to: new DateOnly(2025, 12, 31)).ConfigureAwait(true))
        {
            count++;
        }
        count.Should().Be(0, "every row group's date range falls before the requested window");
    }

    // ───── WriteAsync edge cases ───────────────────────────────────────

    [Fact]
    public async Task WriteAsync_NullStream_Throws()
    {
        var format = new CompressedColumnarSegmentFormat<ColumnarBar>();
        var input = new[] { MakeBar(2020, 1, 1, 0) };
        Func<Task> act = async () => await format.WriteAsync(null!, input).ConfigureAwait(true);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task WriteAsync_NullRecords_Throws()
    {
        var format = new CompressedColumnarSegmentFormat<ColumnarBar>();
        using var stream = new MemoryStream();
        Func<Task> act = async () => await format.WriteAsync(stream, null!).ConfigureAwait(true);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task ReadAsync_EmptySegment_YieldsNothing()
    {
        var format = new CompressedColumnarSegmentFormat<ColumnarBar>();
        using var stream = new MemoryStream();
        await format.WriteAsync(stream, []).ConfigureAwait(true);
        stream.Position = 0;

        var count = 0;
        await foreach (var _ in format.ReadAsync(
            stream, new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31), ColumnProjection.All).ConfigureAwait(true))
        {
            count++;
        }
        count.Should().Be(0);
    }

    [Fact]
    public async Task ReadRowGroupsAsync_RowGroupCrcValid_SurfacesOk()
    {
        // The rowGroupCrcs path is wired when the writer emits CRC entries (always in v2.0.0).
        // The Ok branch of ValidateRowGroup is exercised by every successful round-trip.
        var format = new CompressedColumnarSegmentFormat<ColumnarBar>(rowGroupMaxRows: 3);
        var input = Enumerable.Range(0, 9).Select(i => MakeBar(2020, 1, 1 + i, i)).ToList();

        using var stream = new MemoryStream();
        await format.WriteAsync(stream, input).ConfigureAwait(true);
        stream.Position = 0;

        var rgCount = 0;
        await foreach (var rg in format.ReadRowGroupsAsync(
            stream, from: new DateOnly(2020, 1, 1), to: new DateOnly(2020, 12, 31)).ConfigureAwait(true))
        {
            rg.Should().NotBeNull();
            rgCount++;
        }
        rgCount.Should().Be(3, "9 records / rowGroupMaxRows 3 = 3 row groups");
    }

    [Fact]
    public void TuningGuide_RowGroupKnobs_AreParametersOfTheFormatThatPlansRowGroups()
    {
        // Row groups are planned by this format from its constructor arguments alone, so every
        // knob the guide's row-group tuning table names must be one of those parameters — a knob
        // named on any other surface would be a setting with no runtime effect.
        var guide = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "columnar-time-series.md"));
        var start = guide.IndexOf("## Tuning row-group sizing", StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, "the guide carries a row-group tuning section");
        var end = guide.IndexOf("\n## ", start + 1, StringComparison.Ordinal);
        var section = guide[start..(end < 0 ? guide.Length : end)];

        var tableKnobs = section.Split('\n')
            .Where(line => line.StartsWith("| `", StringComparison.Ordinal))
            .Select(line => line.Split('`')[1])
            .ToList();
        var parameters = typeof(CompressedColumnarSegmentFormat<ColumnarBar>).GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);

        tableKnobs.Should().NotBeEmpty();
        tableKnobs.Should().OnlyContain(knob => parameters.Contains(knob),
            "each documented knob must be a CompressedColumnarSegmentFormat constructor parameter");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && dir.GetFiles("*.slnx").Length == 0)
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException(
            "Could not locate the repository root from " + AppContext.BaseDirectory);
    }
}

internal static class ColumnarBarTestExtensions
{
    public static ColumnarBar WithDateOffset(this ColumnarBar bar, int days) =>
        bar with { Date = bar.Date.AddDays(days) };
}
