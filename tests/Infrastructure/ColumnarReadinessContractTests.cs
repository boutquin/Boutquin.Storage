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
using System.Globalization;
using Boutquin.Storage.Domain.ValueObjects.TimeSeries;
using Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar;
using Boutquin.Storage.Infrastructure.Tests.TimeSeries.Columnar;
using Boutquin.Storage.Infrastructure.TimeSeries;
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar;
using CiKey = Boutquin.Storage.Infrastructure.Tests.DatasetKeyIdentityAndSegmentNameContractTests.CiKey;

namespace Boutquin.Storage.Infrastructure.Tests;

public sealed class ColumnarReadinessContractTests
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("tr-TR")]
    [InlineData("sv-SE")]
    public void StringComparison_PreservesUtf8ByteIdentityAndOrderAcrossCultures(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            string[] values = ["Å", "A\u030A", "a", "B", "\uE000", "\U00010000", "", "\0", "İ", "i"];
            foreach (var left in values)
            {
                foreach (var right in values)
                {
                    var expected = Encoding.UTF8.GetBytes(left).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(right));
                    Math.Sign(ComparisonHelper.SafeCompareTo(left, right)).Should().Be(Math.Sign(expected));
                }
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void SortSpecifications_UseOrderedColumnValuesForEqualityAndHashing()
    {
        var first = new ColumnSortSpec([("Close", SortDirection.Descending), ("Date", SortDirection.Ascending)]);
        var equal = new ColumnSortSpec([("Close", SortDirection.Descending), ("Date", SortDirection.Ascending)]);
        first.Should().Be(equal);
        (first == equal).Should().BeTrue();
        first.GetHashCode().Should().Be(equal.GetHashCode());
        new HashSet<ColumnSortSpec> { first, equal }.Should().ContainSingle();
        first.Should().NotBe(new ColumnSortSpec([("Date", SortDirection.Ascending), ("Close", SortDirection.Descending)]));
        first.Should().NotBe(new ColumnSortSpec([("Close", SortDirection.Ascending), ("Date", SortDirection.Ascending)]));
        first.Should().NotBe(new ColumnSortSpec([("close", SortDirection.Descending), ("Date", SortDirection.Ascending)]));
        first.Equals(null).Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteRange_IsVisibleToTheNextReadBeforeCompaction(bool columnar)
    {
        var folder = Path.Combine(Path.GetTempPath(), "columnar-delete-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var stack = await ColumnarTimeSeriesEngineBuilder.BuildAsync<CiKey, MarketDataBar>(folder);
            var memory = new InMemoryTimeSeriesStore<CiKey, MarketDataBar>();
            var store = columnar ? stack.Store : (Boutquin.Storage.Domain.Interfaces.TimeSeries.ITimeSeriesStore<CiKey, MarketDataBar>)memory;
            var start = new DateOnly(2024, 1, 1);
            var key = new CiKey("A");
            await store.AppendAsync(key, [.. Enumerable.Range(0, 5).Select(i => new MarketDataBar { Date = start.AddDays(i), Close = i + 1 })]);
            if (columnar)
            {
                await stack.InnerEngine.FlushAsync();
            }
            await store.AppendAsync(key, [new MarketDataBar { Date = start, Close = 42m }]);
            await store.DeleteRangeAsync(key, start.AddDays(2), start.AddDays(4));
            var rows = new List<MarketDataBar>();
            await foreach (var row in store.GetRangeAsync(key, start, start.AddDays(4)))
            {
                rows.Add(row);
            }
            rows.Select(r => r.Close).Should().Equal(42m, 2m);
            (await store.GetCoverageAsync(key))!.Value.To.Should().Be(start.AddDays(1));
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, true);
            }
        }
    }

    [Fact]
    public async Task NonDateSortedSplitBlocks_KeepByteDistinctDatasetsAndTombstonesInDateOrder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "columnar-split-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var start = new DateOnly(2024, 1, 1);
            var spec = new ColumnSortSpec([("Close", SortDirection.Descending)]);
            var format = new ColumnarSegmentFormat<CiKey, MarketDataBar>(new CompressedColumnarSegmentFormat<MarketDataBar>(), spec)
            { MaxBlockSerializedBytes = 500 };
            var entries = new List<SegmentWriteEntry<TimeSeriesKey<CiKey>, MarketDataBar>>();
            foreach (var spelling in new[] { "Alpha", "ALPHA" })
            {
                for (var day = 0; day < 12; day++)
                {
                    var key = new TimeSeriesKey<CiKey>(new CiKey(spelling), start.AddDays(day));
                    entries.Add(SegmentWriteEntry<TimeSeriesKey<CiKey>, MarketDataBar>.Inline(key,
                        new MarketDataBar { Date = key.Date, Close = (spelling == "Alpha" ? 100 : 200) + day % 3 }, (ulong)(day + 1)));
                }
            }
            entries.Sort((left, right) => left.Key.CompareTo(right.Key));
            var deleted = new TimeSeriesKey<CiKey>(new CiKey("Alpha"), start.AddDays(4));
            var tombstones = new Dictionary<TimeSeriesKey<CiKey>, ulong> { [deleted] = 99 };
            await format.WriteAsync(folder, "split.bsct", entries, tombstones);
            var bytes = await File.ReadAllBytesAsync(Path.Combine(folder, "split.bsct"));
            var footerLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(bytes.Length - 8));
            var footerStart = bytes.Length - 8 - footerLength;
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(footerStart)).Should().BeGreaterThan(2,
                "each dataset must cross a real block cut");
            var keyLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(footerStart + 12));
            var descriptor = footerStart + 16 + keyLength;
            var blockStart = (int)BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(descriptor));
            var blockLength = (int)BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(descriptor + 8));
            var physical = new List<MarketDataBar>();
            using (var block = new MemoryStream(bytes.AsSpan(blockStart, blockLength).ToArray(), false))
            {
                await foreach (var row in new CompressedColumnarSegmentFormat<MarketDataBar>().ReadAsync(
                    block, DateOnly.MinValue, DateOnly.MaxValue, ColumnProjection.All))
                {
                    physical.Add(row);
                }
            }
            physical.Should().HaveCountGreaterThan(1);
            physical.Select(row => row.Close).Should().BeInDescendingOrder();
            physical.Select(row => row.Date).Should().NotEqual(physical.Select(row => row.Date).Order());
            var reader = format.Open(folder, "split.bsct");
            var actual = (await reader.GetAllItemsAsync()).ToArray();
            var visible = entries.Where(item => item.Key != deleted).ToArray();
            actual.Select(item => item.Key).Should().Equal(visible.Select(item => item.Key));
            actual.Select(item => item.Value.Close).Should().Equal(visible.Select(item => item.InlineValue!.Close));
            reader.GetTombstoneSeqnos().Should().ContainKey(deleted).WhoseValue.Should().Be(99);
            (await reader.TryGetValueWithSeqnoAsync(deleted, ulong.MaxValue)).Found.Should().BeFalse();
            foreach (var entry in visible)
            {
                var point = await reader.TryGetValueWithSeqnoAsync(entry.Key, ulong.MaxValue);
                point.Found.Should().BeTrue();
                point.Value.Close.Should().Be(entry.InlineValue!.Close);
                point.SequenceNumber.Should().Be(99, "the envelope exposes the segment maximum, including tombstones");
            }
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
