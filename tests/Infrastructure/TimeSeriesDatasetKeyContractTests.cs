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
using Boutquin.Storage.Domain.Interfaces.TimeSeries;
using Boutquin.Storage.Domain.ValueObjects.TimeSeries;
using Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar;
using Boutquin.Storage.Infrastructure.Tests.TimeSeries.Columnar;
using Boutquin.Storage.Infrastructure.TimeSeries;
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar;

namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// Pins the time-series family's dataset-key contract: a dataset is identified by its key's
/// serialized bytes (the identity <see cref="FileDatasetCoverageIndex{TDatasetKey}"/> and every
/// on-disk structure already use), never by instance identity, and the in-memory store's append
/// and projected-read behaviour match their LSM sibling and the columnar guide.
/// </summary>
public sealed class TimeSeriesDatasetKeyContractTests : IDisposable
{
    private static readonly DateOnly s_d0 = new(2024, 1, 1);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ts-key-contract-" + Guid.NewGuid().ToString("N"));

    public TimeSeriesDatasetKeyContractTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // best-effort cleanup of a temp directory
        }
    }

    // A dataset key meeting every declared TDatasetKey constraint (ISerializable, IComparable,
    // new()) that keeps reference equality: two instances naming the same dataset compare equal
    // and serialize identically but are not Equals.
    public sealed class RefKey : ISerializable<RefKey>, IComparable<RefKey>
    {
        public RefKey()
        {
        }

        public RefKey(string name) => Name = name;

        public string Name { get; set; } = string.Empty;

        public int CompareTo(RefKey? other) => string.CompareOrdinal(Name, other?.Name);

        public void Serialize(Stream stream)
        {
            var bytes = Encoding.UTF8.GetBytes(Name);
            stream.WriteByte((byte)bytes.Length);
            stream.Write(bytes);
        }

        public static RefKey Deserialize(Stream stream)
        {
            var length = stream.ReadByte();
            var bytes = new byte[length];
            stream.ReadExactly(bytes);
            return new RefKey(Encoding.UTF8.GetString(bytes));
        }
    }

    private static MarketDataBar Bar(DateOnly date, decimal close) => new() { Date = date, Close = close };

    private static async Task<List<T>> ToListAsync<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source)
        {
            list.Add(item);
        }

        return list;
    }

    // ---- ColumnarSegmentFormat: tombstone shadow-drop and footer tombstone map ----

    [Fact]
    public async Task ColumnarWrite_TombstoneKeyedByEqualKeyInstance_DropsTheOlderData()
    {
        var format = new ColumnarSegmentFormat<RefKey, MarketDataBar>(new CompressedColumnarSegmentFormat<MarketDataBar>());
        var entries = new List<SegmentWriteEntry<TimeSeriesKey<RefKey>, MarketDataBar>>
        {
            SegmentWriteEntry<TimeSeriesKey<RefKey>, MarketDataBar>.Inline(
                new TimeSeriesKey<RefKey>(new RefKey("X"), s_d0), Bar(s_d0, 7m), 5UL),
        };
        var tombstones = new Dictionary<TimeSeriesKey<RefKey>, ulong>
        {
            [new TimeSeriesKey<RefKey>(new RefKey("X"), s_d0)] = 9UL,
        };

        var reader = await format.WriteAsync(_dir, "shadow.bsct", entries, tombstones);
        var (_, found) = await reader.TryGetValueAsync(new TimeSeriesKey<RefKey>(new RefKey("X"), s_d0));

        found.Should().BeFalse(
            "the seq-5 data sits beside a seq-9 tombstone for the same dataset key; keeping it would stamp it with the segment max seqno 9 and resurrect a deleted row");
    }

    [Fact]
    public async Task ColumnarFooter_TombstoneMap_ResolvesAnEqualKeyInstance()
    {
        var format = new ColumnarSegmentFormat<RefKey, MarketDataBar>(new CompressedColumnarSegmentFormat<MarketDataBar>());
        var d1 = s_d0.AddDays(1);
        var entries = new List<SegmentWriteEntry<TimeSeriesKey<RefKey>, MarketDataBar>>
        {
            SegmentWriteEntry<TimeSeriesKey<RefKey>, MarketDataBar>.Inline(
                new TimeSeriesKey<RefKey>(new RefKey("X"), d1), Bar(d1, 1m), 10UL),
        };
        var tombstones = new Dictionary<TimeSeriesKey<RefKey>, ulong>
        {
            [new TimeSeriesKey<RefKey>(new RefKey("X"), s_d0)] = 9UL,
        };

        var reader = await format.WriteAsync(_dir, "footer.bsct", entries, tombstones);
        var parsed = reader.GetTombstoneSeqnos();

        parsed.TryGetValue(new TimeSeriesKey<RefKey>(new RefKey("X"), s_d0), out var seqno).Should().BeTrue(
            "the footer's tombstone map is keyed by deserialized key instances; a caller's equal key must resolve it");
        seqno.Should().Be(9UL);
    }

    // ---- InMemoryTimeSeriesStore: dataset identity ----

    [Fact]
    public async Task InMemoryStore_EqualKeyInstance_ReadsTheAppendedRowsAndCoverage()
    {
        var store = new InMemoryTimeSeriesStore<RefKey, MarketDataBar>();
        await store.AppendAsync(new RefKey("X"), [Bar(s_d0, 1m)]);

        var rows = await ToListAsync(store.GetRangeAsync(new RefKey("X"), s_d0, s_d0));
        var coverage = await store.GetCoverageAsync(new RefKey("X"));

        rows.Should().HaveCount(1, "the LSM sibling identifies datasets by key value, so an equal key instance reads the row");
        coverage.Should().Be(new DateRange(s_d0, s_d0));
    }

    // ---- InMemoryDatasetCoverageIndex: dataset identity ----

    [Fact]
    public async Task InMemoryCoverageIndex_EqualKeyInstances_ShareOneEntry()
    {
        var index = new InMemoryDatasetCoverageIndex<RefKey>();
        await index.RegisterAsync(new RefKey("X"), s_d0, s_d0);
        await index.RegisterAsync(new RefKey("X"), s_d0.AddDays(10), s_d0.AddDays(10));

        var coverage = await index.GetAsync(new RefKey("X"));

        coverage.Should().Be(new DateRange(s_d0, s_d0.AddDays(10)),
            "FileDatasetCoverageIndex keys datasets by serialized bytes, so equal key instances share one entry");
    }

    [Fact]
    public async Task InMemoryCoverageIndex_Rebuild_IsResolvableByACallerHeldKey()
    {
        var index = new InMemoryDatasetCoverageIndex<RefKey>();

        await index.RebuildAsync(Scan());
        var coverage = await index.GetAsync(new RefKey("X"));

        coverage.Should().Be(new DateRange(s_d0, s_d0.AddDays(5)),
            "a rebuild scan yields fresh deserialized key instances; they name one dataset whose coverage spans both rows");

        static async IAsyncEnumerable<(RefKey Dataset, DateOnly Date)> Scan()
        {
            await Task.CompletedTask;
            yield return (new RefKey("X"), s_d0);
            yield return (new RefKey("X"), s_d0.AddDays(5));
        }
    }

    [Fact]
    public async Task LsmStoreWithDefaultCoverageIndex_CoverageFollowsEqualKeyInstances()
    {
        await using var stack = await ColumnarTimeSeriesEngineBuilder.BuildAsync<RefKey, MarketDataBar>(
            Path.Combine(_dir, "lsm"), memTableCapacity: 100);
        var writer = new RefKey("X");
        var reader = new RefKey("X");
        var later = new DateOnly(2024, 6, 1);

        await stack.Store.AppendAsync(writer, [Bar(s_d0, 1m)]);
        await stack.Store.GetCoverageAsync(reader);
        await stack.Store.AppendAsync(writer, [Bar(later, 2m)]);

        (await stack.Store.GetCoverageAsync(reader)).Should().Be(new DateRange(s_d0, later),
            "both rows are readable through the reader key, so its coverage must include the later append");
        (await stack.Store.GetGapsAsync(reader, new DateRange(s_d0, later))).Should().BeEmpty();

        await stack.Store.DeleteRangeAsync(reader, DateOnly.MinValue, DateOnly.MaxValue);

        (await stack.Store.GetCoverageAsync(writer)).Should().BeNull(
            "every record of the dataset was deleted, so no key instance may still report coverage");
    }

    [Fact]
    public async Task ProjectedRange_ReinsertNewerThanAnOlderSegmentTombstone_IsReturned()
    {
        await using var stack = await ColumnarTimeSeriesEngineBuilder.BuildAsync<RefKey, MarketDataBar>(
            Path.Combine(_dir, "proj"), memTableCapacity: 100);

        await stack.Store.AppendAsync(new RefKey("X"), [Bar(s_d0, 1m)]);
        await stack.InnerEngine.FlushAsync();
        await stack.Store.DeleteRangeAsync(new RefKey("X"), s_d0, s_d0);
        await stack.InnerEngine.FlushAsync();
        await stack.Store.AppendAsync(new RefKey("X"), [Bar(s_d0, 3m)]);
        await stack.InnerEngine.FlushAsync();

        var rows = await ToListAsync(stack.Store.GetRangeAsync(
            new RefKey("X"), s_d0, s_d0, new ColumnProjection(["Close"])));

        rows.Select(r => r.Close).Should().Equal([3m],
            "the re-insert is newer than the tombstone; the projected merge must match winning data to tombstones by key value");
    }

    // ---- InMemoryTimeSeriesStore.AppendAsync: a rejected batch applies nothing ----

    [Fact]
    public async Task InMemoryAppend_BatchWithNullElement_AppliesNothing()
    {
        var store = new InMemoryTimeSeriesStore<SerializableWrapper<string>, MarketDataBar>();
        var dataset = new SerializableWrapper<string>("X");
        MarketDataBar[] batch = [Bar(s_d0, 1m), null!, Bar(s_d0.AddDays(1), 2m)];

        var act = () => store.AppendAsync(dataset, batch);

        await act.Should().ThrowAsync<NullReferenceException>("the LSM sibling rejects the same batch with the same exception");
        (await ToListAsync(store.GetRangeAsync(dataset, DateOnly.MinValue, DateOnly.MaxValue))).Should().BeEmpty(
            "the LSM sibling validates the whole batch before applying any of it");
        (await store.GetCoverageAsync(dataset)).Should().BeNull();
    }

    [Fact]
    public async Task InMemoryAppend_CancelledWhileTheBatchIsEnumerated_AppliesNothing()
    {
        var store = new InMemoryTimeSeriesStore<SerializableWrapper<string>, MarketDataBar>();
        var dataset = new SerializableWrapper<string>("X");
        using var cts = new CancellationTokenSource();

        var act = () => store.AppendAsync(dataset, CancelAfterFirst(cts), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        (await ToListAsync(store.GetRangeAsync(dataset, DateOnly.MinValue, DateOnly.MaxValue))).Should().BeEmpty(
            "a cancelled append is a rejected batch: none of it may be applied");

        static IEnumerable<MarketDataBar> CancelAfterFirst(CancellationTokenSource source)
        {
            yield return Bar(s_d0, 1m);
            source.Cancel();
            yield return Bar(s_d0.AddDays(1), 2m);
        }
    }

    // ---- Columnar guide: projected reads and the Date column ----

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task ColumnarGuide_DateColumnParagraph_DescribesProjectedReadDates(string lineEnding)
    {
        var guide = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "columnar-time-series.md"));
        // Git may check Markdown out with LF or CRLF. Exercise both representations before
        // parsing blank-line paragraph boundaries, whose meaning is independent of checkout.
        guide = guide.ReplaceLineEndings(lineEnding).ReplaceLineEndings("\n");
        var paragraph = guide[guide.IndexOf("**The date column is always read.**", StringComparison.Ordinal)..];
        paragraph = paragraph[..paragraph.IndexOf("\n\n", StringComparison.Ordinal)];

        // Behaviour the paragraph must describe, observed on both the LSM store and its in-memory sibling.
        await using var stack = await ColumnarTimeSeriesEngineBuilder.BuildAsync<SerializableWrapper<string>, MarketDataBar>(
            Path.Combine(_dir, "guide"), memTableCapacity: 100);
        var memory = new InMemoryTimeSeriesStore<SerializableWrapper<string>, MarketDataBar>();
        var dataset = new SerializableWrapper<string>("X");
        foreach (var store in new ITimeSeriesStore<SerializableWrapper<string>, MarketDataBar>[] { stack.Store, memory })
        {
            await store.AppendAsync(dataset, [Bar(s_d0, 1m)]);
            var withoutDate = await ToListAsync(store.GetRangeAsync(dataset, s_d0, s_d0, new ColumnProjection(["Close"])));
            var withDate = await ToListAsync(store.GetRangeAsync(dataset, s_d0, s_d0, new ColumnProjection(["Close", "Date"])));
            withoutDate.Single().Date.Should().Be(default, "a projection that omits Date clips it, like every other omitted column");
            withDate.Single().Date.Should().Be(s_d0);
        }

        paragraph.Should().NotContain("the returned records carry their dates",
            "projected store and engine reads return Date = default when the projection omits Date");
        paragraph.Should().Contain("include `Date` in the projection",
            "the guide must tell a caller how to receive dates from a projected read");
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Boutquin.Storage.slnx")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the test runs inside the repository checkout");
        return directory!.FullName;
    }
}
