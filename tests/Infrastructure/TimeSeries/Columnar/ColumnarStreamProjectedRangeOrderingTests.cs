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
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar;

namespace Boutquin.Storage.Infrastructure.Tests.TimeSeries.Columnar;

/// <summary>
/// Pins the ascending-by-date ordering contract of the columnar reader's STREAMING projected
/// range path (<see cref="IProjectingSegmentReader{TKey,TValue,TProjection}.StreamProjectedRangeAsync"/>)
/// under a non-date <see cref="ColumnSortSpec"/>.
/// </summary>
/// <remarks>
/// The full-materialisation read path (<c>StreamBlockEntriesAsync</c> /
/// <c>GetAllItemsInternalAsync</c>) buffers each block and sorts ascending-by-date before
/// yielding, so the emitted key sequence is ascending regardless of the on-disk column sort.
/// The sibling streaming projected path must honour the same contract: the
/// <see cref="IProjectingSegmentReader{TKey,TValue,TProjection}"/> doc promises
/// "key/value pairs in the requested direction" (ascending), and an ascending-expecting
/// consumer (k-way merge / range early-break) silently drops every in-range row after the first
/// out-of-order key. This test reads directly off the reader's projected path (bypassing any
/// engine-side <c>SortedDictionary</c> that would mask the defect) and asserts both ascending
/// order AND set-equality with a full scan.
/// </remarks>
public sealed class ColumnarStreamProjectedRangeOrderingTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        $"StreamProjOrder_{Guid.NewGuid():N}");

    public ColumnarStreamProjectedRangeOrderingTests()
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

    private static TimeSeriesKey<SerializableWrapper<string>> Key(string dataset, DateOnly date)
        => new(new SerializableWrapper<string>(dataset), date);

    [Fact]
    public async Task StreamProjectedRange_NonDateSort_YieldsAscendingByDate_AndFullInRangeSet()
    {
        // A non-date ColumnSortSpec makes the on-disk row order DESCEND by date (TradeCount
        // descends with date, sorted ascending). The inner format yields records in on-disk
        // order and only filters by [from,to] — it never re-sorts by date. So without a
        // per-block date sort, the streaming projected path emits dates in DESCENDING order,
        // violating the ascending contract and silently dropping in-range rows for any
        // ascending-expecting consumer.
        var inner = new CompressedColumnarSegmentFormat<MarketDataBar>();
        var format = new ColumnarSegmentFormat<SerializableWrapper<string>, MarketDataBar>(
            inner,
            new ColumnSortSpec([("TradeCount", SortDirection.Ascending)]));

        var d0 = new DateOnly(2020, 1, 1);
        var bars = Enumerable.Range(0, 9)
            .Select(i => new MarketDataBar
            {
                Date = d0.AddDays(i * 10),
                Close = 100m + i,
                SplitRatio = 1m,
                TradeCount = 1000 - i, // descends as Date ascends
            })
            .ToList();

        var entries = bars
            .Select(b => (Key("SORT", b.Date), b, 0UL))
            .ToList();

        var reader = await format.WriteAsync(_folder, $"projorder{format.FileExtension}", entries);

        var projecting = reader as IProjectingSegmentReader<
            TimeSeriesKey<SerializableWrapper<string>>, MarketDataBar, ColumnProjection>;
        projecting.Should().NotBeNull(
            "the columnar reader must expose the projected-range path under test");

        // Project a field subset (NOT .All) so the read routes through StreamProjectedRangeAsync.
        var projection = new ColumnProjection(["Close", "Date"]);

        // Sub-range covering the first three dates: 2020-01-01, 2020-01-11, 2020-01-21.
        var subFrom = d0;                  // 2020-01-01
        var subTo = d0.AddDays(20);        // 2020-01-21
        var startKey = Key("SORT", subFrom);
        var endKey = Key("SORT", subTo);

        var yielded = new List<DateOnly>();
        await foreach (var kvp in projecting!.StreamProjectedRangeAsync(startKey, endKey, projection))
        {
            yielded.Add(kvp.Value.Date);
        }

        var expectedSet = bars
            .Where(b => b.Date >= subFrom && b.Date <= subTo)
            .Select(b => b.Date)
            .OrderBy(d => d)
            .ToList();
        expectedSet.Should().HaveCount(3);

        // Set-equality: the streaming projected path must return exactly the in-range rows.
        yielded.OrderBy(d => d).Should().Equal(expectedSet,
            "the projected range read must return exactly the in-range rows a full scan returns");

        // Ordering: the emitted dates must be ascending — this is the contract the sibling
        // full-scan path already honours and the assertion that goes RED on the pre-fix code
        // (dates emitted DESCENDING: 01-21, 01-11, 01-01).
        yielded.Should().BeInAscendingOrder(
            "IProjectingSegmentReader.StreamProjectedRangeAsync promises ascending-by-key order, "
            + "so an ascending-expecting consumer does not silently drop in-range rows");
    }
}
