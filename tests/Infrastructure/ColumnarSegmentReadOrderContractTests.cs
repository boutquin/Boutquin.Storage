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
using System.Xml.Linq;
using Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar;
using Boutquin.Storage.Infrastructure.Tests.TimeSeries.Columnar;
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar;

namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// Pins the iteration order the columnar segment format actually delivers and documents: rows are
/// yielded in the order they were stored (row group by row group, row by row), which is
/// date-ascending only when the writer was handed date-ascending records. The format does not sort
/// on read; callers that need a date-ordered stream (the envelope does, per block) sort themselves.
/// </summary>
public sealed class ColumnarSegmentReadOrderContractTests
{
    private static ColumnarBar Bar(DateOnly date, int n) => new()
    {
        Date = date,
        Ticker = "AAPL",
        Close = 100m + n,
        Volume = 1_000L + n,
        TradeCount = n,
        Vwap = 100.0 + n,
        IsAdjusted = n % 2 == 0,
        Residual = n * 0.5,
    };

    private static List<ColumnarBar> Records(IEnumerable<int> dayOffsets) =>
        dayOffsets.Select((d, i) => Bar(new DateOnly(2026, 3, 1).AddDays(d), i)).ToList();

    private static async Task<byte[]> WriteAsync(CompressedColumnarSegmentFormat<ColumnarBar> format, IReadOnlyList<ColumnarBar> rows)
    {
        using var ms = new MemoryStream();
        await format.WriteAsync(ms, rows).ConfigureAwait(true);
        return ms.ToArray();
    }

    // ───── Executed behaviour: stored order, not date order ──────────

    public static TheoryData<string, int[]> WriteOrders() => new()
    {
        { "newest-first", [11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1, 0] },
        { "interleaved", [5, 0, 9, 3, 11, 1, 8, 2, 10, 4, 7, 6] },
        { "ascending", [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11] },
    };

    [Theory]
    [MemberData(nameof(WriteOrders))]
    public async Task ReadAsync_YieldsRowsInStoredOrder(string label, int[] offsets)
    {
        var format = new CompressedColumnarSegmentFormat<ColumnarBar>(rowGroupMaxRows: 4);
        var written = Records(offsets);
        var bytes = await WriteAsync(format, written);
        var from = new DateOnly(2026, 3, 1).AddDays(2);
        var to = new DateOnly(2026, 3, 1).AddDays(9);

        var read = new List<DateOnly>();
        using var input = new MemoryStream(bytes);
        await foreach (var r in format.ReadAsync(input, from, to, ColumnProjection.All).ConfigureAwait(true))
        {
            read.Add(r.Date);
        }

        read.Should().Equal(written.Select(r => r.Date).Where(d => d >= from && d <= to),
            $"rows must come back in the order they were stored, restricted to the window ({label})");
    }

    [Theory]
    [MemberData(nameof(WriteOrders))]
    public async Task StreamWhereAsync_YieldsRowsInStoredOrder(string label, int[] offsets)
    {
        var format = new CompressedColumnarSegmentFormat<ColumnarBar>(rowGroupMaxRows: 4);
        var written = Records(offsets);
        var bytes = await WriteAsync(format, written);
        var from = new DateOnly(2026, 3, 1).AddDays(1);
        var to = new DateOnly(2026, 3, 1).AddDays(10);

        var read = new List<DateOnly>();
        using var input = new MemoryStream(bytes);
        await foreach (var r in format.StreamWhereAsync(input, from, to, ColumnProjection.All,
            [new ColumnPredicate("TradeCount", ComparisonOperator.GreaterThanOrEqual, 0)]).ConfigureAwait(true))
        {
            read.Add(r.Date);
        }

        read.Should().Equal(written.Select(r => r.Date).Where(d => d >= from && d <= to),
            $"predicate reads keep the stored order too ({label})");
    }

    // ───── Documented contract: the XML docs state stored order ──────

    private static string SummaryOf(string xmlFileName, string memberPrefix)
    {
        var path = Path.Combine(AppContext.BaseDirectory, xmlFileName);
        var member = XDocument.Load(path).Descendants("member")
            .Single(m => ((string?)m.Attribute("name"))!.StartsWith(memberPrefix, StringComparison.Ordinal));
        return string.Concat(member.Elements().Where(e => e.Name != "inheritdoc").Select(e => e.Value));
    }

    private const string InterfaceMember = "Boutquin.Storage.Domain.Interfaces.TimeSeries.Columnar.IColumnarSegmentFormat`1";
    private const string ImplMember = "Boutquin.Storage.Infrastructure.TimeSeries.Columnar.CompressedColumnarSegmentFormat`1";

    public static TheoryData<string, string> DocumentedMembers() => new()
    {
        { "Boutquin.Storage.Domain.xml", "T:" + InterfaceMember },
        { "Boutquin.Storage.Domain.xml", "M:" + InterfaceMember + ".ReadAsync(" },
        { "Boutquin.Storage.Domain.xml", "M:" + InterfaceMember + ".StreamWhereAsync(" },
        { "Boutquin.Storage.Infrastructure.xml", "M:" + ImplMember + ".ReadAsync(" },
        { "Boutquin.Storage.Infrastructure.xml", "M:" + ImplMember + ".StreamWhereAsync(" },
    };

    [Theory]
    [MemberData(nameof(DocumentedMembers))]
    public void ReadDocs_StateStoredOrder_AndDoNotPromiseDateAscending(string xmlFile, string memberPrefix)
    {
        var text = string.Join(' ', SummaryOf(xmlFile, memberPrefix).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        text.Should().Contain("stored order", "the read path yields rows as stored and the docs must say so");
        text.Should().NotContain("in date-ascending order", "the format never sorts on read, so it cannot promise date order unconditionally");
    }
}
