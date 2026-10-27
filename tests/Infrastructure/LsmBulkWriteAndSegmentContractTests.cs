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
using System.Collections;
using Boutquin.Storage.Domain.ValueObjects.LsmTree;
using Boutquin.Storage.Domain.ValueObjects.TimeSeries;
using Boutquin.Storage.Infrastructure.Caching;
using Boutquin.Storage.Infrastructure.Cdc;
using Boutquin.Storage.Infrastructure.Events;
using Boutquin.Storage.Infrastructure.Tests.TimeSeries.Columnar;
using Boutquin.Storage.Infrastructure.TimeSeries;
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar;

namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// Pins the LSM write-path contracts that must hold whatever shape the caller hands the engine:
/// a segment format reached through a pass-through decorator keeps every acknowledged write; a bulk
/// write over a live list applies exactly the batch it validated; the CDC adapter's pre-journal
/// admission check reaches the engine behind the time-series decorator; a table sharing a block
/// cache never reads another file's block; an SSTable write validates and writes one view of its
/// input; and a block cache configured on the engine options is either read through or refused.
/// </summary>
public sealed class LsmBulkWriteAndSegmentContractTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"LsmBulkWriteSegmentContract_{Guid.NewGuid():N}");

    public LsmBulkWriteAndSegmentContractTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private string NewFolder(string tag)
    {
        var folder = Path.Combine(_root, tag + "_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static SerializableWrapper<int> K(int key) => new(key);

    private static SerializableWrapper<string> V(string value) => new(value);

    private static long ValueLogBytesOnDisk(string folder)
    {
        var dir = Path.Combine(folder, "valuelog");
        return Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "valuelog_*.log", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length)
            : 0;
    }

    // ---------------------------------------------------------------------------------------
    // A format that cannot store a value-log pointer never receives one, decorated or not.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task PassThroughDecoratedColumnarFormat_KeepsEveryAcknowledgedWriteAcrossFlushAndReopen()
    {
        var folder = NewFolder("decorated-columnar");
        var ds = new SerializableWrapper<string>("NOTES");
        var d0 = new DateOnly(2024, 1, 1);
        var small = new TickerNoteBar { Date = d0, Ticker = "S", Note = "short" };
        var big = new TickerNoteBar { Date = d0.AddDays(1), Ticker = "B", Note = new string('b', 4 * 1024) };

        // ValueLog: null resolves to the 1 KiB default threshold, so the 4 KiB note is the value a
        // value-log-capable format would receive as a pointer.
        await using (var engine = await LsmStorageEngine<TimeSeriesKey<SerializableWrapper<string>>, TickerNoteBar>
            .OpenAsync(DecoratedColumnarOptions(folder)).ConfigureAwait(true))
        {
            await engine.SetAsync(new TimeSeriesKey<SerializableWrapper<string>>(ds, small.Date), small).ConfigureAwait(true);
            await engine.SetAsync(new TimeSeriesKey<SerializableWrapper<string>>(ds, big.Date), big).ConfigureAwait(true);
            var flushError = await Record.ExceptionAsync(() => engine.FlushAsync()).ConfigureAwait(true);
            flushError.Should().BeNull("every write the engine acknowledged must be flushable by its segment format");
        }

        await using (var reopened = await LsmStorageEngine<TimeSeriesKey<SerializableWrapper<string>>, TickerNoteBar>
            .OpenAsync(DecoratedColumnarOptions(folder)).ConfigureAwait(true))
        {
            var (smallRead, smallFound) = await reopened
                .TryGetValueAsync(new TimeSeriesKey<SerializableWrapper<string>>(ds, small.Date)).ConfigureAwait(true);
            var (bigRead, bigFound) = await reopened
                .TryGetValueAsync(new TimeSeriesKey<SerializableWrapper<string>>(ds, big.Date)).ConfigureAwait(true);
            smallFound.Should().BeTrue("an acknowledged write survives a flush and a reopen");
            smallRead.Should().Be(small);
            bigFound.Should().BeTrue("an acknowledged write survives a flush and a reopen");
            bigRead.Should().Be(big);
        }

        ValueLogBytesOnDisk(folder).Should().Be(0, "a format that cannot store a pointer is never handed one");
    }

    private static LsmStorageEngineOptions<TimeSeriesKey<SerializableWrapper<string>>, TickerNoteBar> DecoratedColumnarOptions(
        string folder)
        => new(
            MemTableCapacity: 10_000,
            SegmentFolder: folder,
            SegmentPrefix: "bars",
            SegmentFormat: new PassThroughSegmentFormat<TimeSeriesKey<SerializableWrapper<string>>, TickerNoteBar>(
                new ColumnarSegmentFormat<SerializableWrapper<string>, TickerNoteBar>(
                    new CompressedColumnarSegmentFormat<TickerNoteBar>())),
            ValueLog: null);

    /// <summary>
    /// A decorator written against the segment-format contract's original members only: it forwards
    /// what it must implement and nothing else, as a logging or metrics wrapper would.
    /// </summary>
    private sealed class PassThroughSegmentFormat<TKey, TValue>(ISegmentFormat<TKey, TValue> inner)
        : ISegmentFormat<TKey, TValue>
        where TKey : IStorageKey<TKey>, new()
        where TValue : ISerializable<TValue>, new()
    {
        public string FileExtension => inner.FileExtension;

        public bool SupportsSnapshots => inner.SupportsSnapshots;

        public Task<ISegmentReader<TKey, TValue>> WriteAsync(
            string segmentFolder,
            string segmentFileName,
            IReadOnlyList<SegmentWriteEntry<TKey, TValue>> sortedEntries,
            IReadOnlyDictionary<TKey, ulong>? tombstones = null,
            CancellationToken cancellationToken = default)
            => inner.WriteAsync(segmentFolder, segmentFileName, sortedEntries, tombstones, cancellationToken);

        public ISegmentReader<TKey, TValue> Open(string segmentFolder, string segmentFileName)
            => inner.Open(segmentFolder, segmentFileName);
    }

    // ---------------------------------------------------------------------------------------
    // A bulk write over a live list applies exactly the batch it validated.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task SetBulkAsync_LiveListGainingAnEntryAfterValidation_AppliesExactlyTheValidatedBatch()
    {
        // The late arrival carries a null value the engine refuses; it appears only from the second
        // enumeration on, i.e. after the validate pass.
        var live = new LiveList(
            [new(K(1), V("a")), new(K(2), V("b"))],
            new KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>(K(3), null!));
        using var engine = LsmEngineFactory.Build<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 16, segmentFolder: NewFolder("bulk-live"), segmentPrefix: "seg");

        var bulkError = await Record.ExceptionAsync(() => engine.SetBulkAsync(live)).ConfigureAwait(true);

        bulkError.Should().BeNull("the batch the engine validated holds no entry it refuses");
        var all = (await engine.GetAllItemsAsync().ConfigureAwait(true)).Select(i => (i.Key.Value, i.Value.Value)).ToList();
        all.Should().Equal((1, "a"), (2, "b"));
        live.Enumerations.Should().Be(1, "the engine snapshots a caller-owned list exactly once");
    }

    /// <summary>
    /// Models a list another thread keeps appending to: every enumeration after the first takes a
    /// fresh moment-in-time copy that additionally contains a late arrival.
    /// </summary>
    private sealed class LiveList(
        List<KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>> initial,
        KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>> lateArrival)
        : IReadOnlyList<KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>>
    {
        private int _enumerations;

        public int Enumerations => Volatile.Read(ref _enumerations);

        public int Count => initial.Count;

        public KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>> this[int index] => initial[index];

        public IEnumerator<KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>> GetEnumerator()
        {
            var snapshot = new List<KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>>(initial);
            if (Interlocked.Increment(ref _enumerations) > 1)
            {
                snapshot.Add(lateArrival);
            }

            return snapshot.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    // ---------------------------------------------------------------------------------------
    // The CDC adapter refuses an engine-refused entry before the journal, behind the decorator.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task LsmCdcAdapter_OverTimeSeriesDecorator_RefusesAnEngineRefusedEntryBeforeTheJournal()
    {
        var inner = new CompressedColumnarSegmentFormat<MarketDataBar>();
        var format = new ColumnarSegmentFormat<SerializableWrapper<string>, MarketDataBar>(inner);
        await using var lsm = LsmEngineFactory.Build<TimeSeriesKey<SerializableWrapper<string>>, MarketDataBar>(
            memTableCapacity: 16, segmentFolder: NewFolder("cdc-decorated"), segmentPrefix: "ts", segmentFormat: format);
        var engine = new LsmTimeSeriesStorageEngine<SerializableWrapper<string>, MarketDataBar>(
            lsm, new ColumnarTimeSeriesStoreOptions<MarketDataBar>(inner));
        var journal = new InMemoryEventLog<ChangeRecord<TimeSeriesKey<SerializableWrapper<string>>, MarketDataBar>>();
        var adapter = new LsmCdcAdapter<TimeSeriesKey<SerializableWrapper<string>>, MarketDataBar>(engine, journal);
        await adapter.RecoverAsync().ConfigureAwait(true);

        var day = new DateOnly(2024, 1, 1);
        var bar = new MarketDataBar { Date = day, Close = 10m, Open = 10m, High = 10m, Low = 10m, SplitRatio = 1m };
        var mismatchedKey = new TimeSeriesKey<SerializableWrapper<string>>(new SerializableWrapper<string>("AAPL"), day.AddDays(4));

        var refused = await Record.ExceptionAsync(() => adapter.SetAsync(mismatchedKey, bar).AsTask()).ConfigureAwait(true);
        refused.Should().BeOfType<ArgumentException>();

        var journaled = 0;
        await foreach (var _ in journal.ReadFromAsync(0).ConfigureAwait(true))
        {
            journaled++;
        }

        journaled.Should().Be(0, "an entry the engine refuses must never reach the authoritative journal");

        var goodKey = new TimeSeriesKey<SerializableWrapper<string>>(new SerializableWrapper<string>("AAPL"), day);
        var position = await adapter.SetAsync(goodKey, bar).ConfigureAwait(true);
        position.Should().Be(0);
        await adapter.RecoverAsync().ConfigureAwait(true);
        var (stored, found) = await engine.TryGetValueAsync(goodKey).ConfigureAwait(true);
        found.Should().BeTrue();
        stored.Should().Be(bar);
    }

    // ---------------------------------------------------------------------------------------
    // A table sharing a block cache never reads another file's block.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task SortedStringTable_SharingACacheWithAnEngine_ReadsOnlyItsOwnBlocks()
    {
        var cache = new RecordingBlockCache(new LruBlockCache(maxBytes: 1024 * 1024));
        using var engine = LsmEngineFactory.Build<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 100, segmentFolder: NewFolder("engine"), segmentPrefix: "seg",
            segmentFormat: new SstSegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>>(blockCache: cache));
        await engine.SetAsync(K(1), V("ENGINE-VALUE")).ConfigureAwait(true);
        await engine.FlushAsync().ConfigureAwait(true);
        (await engine.TryGetValueAsync(K(1)).ConfigureAwait(true)).Value.Value.Should().Be("ENGINE-VALUE");
        var engineIdentities = cache.PutSegmentIds.ToHashSet();
        engineIdentities.Should().NotBeEmpty("the engine's read populated the shared cache");

        var tables = new List<SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>>();
        try
        {
            foreach (var value in new[] { "DIRECT-A", "DIRECT-B" })
            {
                var table = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
                    NewFolder("direct"), "direct.sst", blockCache: cache);
                tables.Add(table);
                table.Write([SegmentWriteEntry<SerializableWrapper<int>, SerializableWrapper<string>>.Inline(K(1), V(value), 1UL)]);
            }

            foreach (var (table, expected) in tables.Zip(new[] { "DIRECT-A", "DIRECT-B" }))
            {
                table.TryGetValue(K(1), out var read).Should().BeTrue();
                read.Value.Should().Be(expected, "a table must never be answered from another file's cached block");
                table.TryGetValue(K(1), out var reread).Should().BeTrue();
                reread.Value.Should().Be(expected);
            }
        }
        finally
        {
            foreach (var table in tables)
            {
                table.Dispose();
            }
        }

        cache.PutSegmentIds.Distinct().Count().Should().Be(3, "each table caches its blocks under its own identity");
    }

    /// <summary>A pass-through cache that records the segment identity of every block it is handed.</summary>
    private sealed class RecordingBlockCache(IBlockCache inner) : IBlockCache
    {
        private readonly List<long> _putSegmentIds = [];

        public IReadOnlyList<long> PutSegmentIds
        {
            get
            {
                lock (_putSegmentIds)
                {
                    return [.. _putSegmentIds];
                }
            }
        }

        public bool TryGet(BlockCacheKey key, out byte[] payload) => inner.TryGet(key, out payload);

        public void Put(BlockCacheKey key, byte[] payload, long costBytes)
        {
            lock (_putSegmentIds)
            {
                _putSegmentIds.Add(key.SegmentId);
            }

            inner.Put(key, payload, costBytes);
        }

        public void Clear() => inner.Clear();
    }

    // ---------------------------------------------------------------------------------------
    // An SSTable write validates and writes one view of its input.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void SortedStringTable_Write_ListWhoseIndexerAndEnumeratorDisagree_NeverStoresAnUnreachableKey()
    {
        static SegmentWriteEntry<SerializableWrapper<int>, SerializableWrapper<string>> E(int key)
            => SegmentWriteEntry<SerializableWrapper<int>, SerializableWrapper<string>>.Inline(K(key), V("v" + key), (ulong)key);

        // The indexer shows the sorted order a strict-order check accepts; the enumerator (and so
        // every copy made through it) shows the order another writer left the list in.
        var entries = new SplitViewList([E(1), E(2), E(3)], [E(3), E(1), E(2)]);
        using var table = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            NewFolder("split-view"), "split.sst");

        var writeError = Record.Exception(() => table.Write(entries));

        if (writeError is not null)
        {
            writeError.Should().BeOfType<ArgumentException>("an unsorted view must be refused, not written");
            return;
        }

        var stored = table.EnumerateInline().Select(e => e.Key.Value).ToList();
        stored.Should().NotBeEmpty();
        stored.Where(key => !table.TryGetValue(K(key), out _)).Should().BeEmpty(
            "every key a write stores must be reachable by point lookup");
    }

    /// <summary>An <see cref="IList{T}"/> whose indexer and enumerator present different orders.</summary>
    private sealed class SplitViewList(
        IReadOnlyList<SegmentWriteEntry<SerializableWrapper<int>, SerializableWrapper<string>>> indexerView,
        IReadOnlyList<SegmentWriteEntry<SerializableWrapper<int>, SerializableWrapper<string>>> enumeratorView)
        : IList<SegmentWriteEntry<SerializableWrapper<int>, SerializableWrapper<string>>>
    {
        public SegmentWriteEntry<SerializableWrapper<int>, SerializableWrapper<string>> this[int index]
        {
            get => indexerView[index];
            set => throw new NotSupportedException();
        }

        public int Count => indexerView.Count;

        public bool IsReadOnly => true;

        public IEnumerator<SegmentWriteEntry<SerializableWrapper<int>, SerializableWrapper<string>>> GetEnumerator()
            => enumeratorView.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public void CopyTo(SegmentWriteEntry<SerializableWrapper<int>, SerializableWrapper<string>>[] array, int arrayIndex)
        {
            foreach (var entry in enumeratorView)
            {
                array[arrayIndex++] = entry;
            }
        }

        public bool Contains(SegmentWriteEntry<SerializableWrapper<int>, SerializableWrapper<string>> item)
            => enumeratorView.Contains(item);

        public int IndexOf(SegmentWriteEntry<SerializableWrapper<int>, SerializableWrapper<string>> item)
            => throw new NotSupportedException();

        public void Add(SegmentWriteEntry<SerializableWrapper<int>, SerializableWrapper<string>> item) => throw new NotSupportedException();

        public void Clear() => throw new NotSupportedException();

        public void Insert(int index, SegmentWriteEntry<SerializableWrapper<int>, SerializableWrapper<string>> item)
            => throw new NotSupportedException();

        public bool Remove(SegmentWriteEntry<SerializableWrapper<int>, SerializableWrapper<string>> item) => throw new NotSupportedException();

        public void RemoveAt(int index) => throw new NotSupportedException();
    }

    // ---------------------------------------------------------------------------------------
    // A block cache on the engine options is read through, or refused.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void EngineOptions_BlockCacheTheSegmentFormatDoesNotReadThrough_IsRefusedAtConstruction()
    {
        var options = new LsmStorageEngineOptions<SerializableWrapper<int>, SerializableWrapper<string>>(
            MemTableCapacity: 16,
            SegmentFolder: NewFolder("options-cache"),
            SegmentPrefix: "seg",
            SegmentFormat: new SstSegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>>(),
            BlockCache: new LruBlockCache(maxBytes: 1024 * 1024));

        LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>? engine = null;
        var error = Record.Exception(() => engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(options));
        engine?.Dispose();

        error.Should().BeOfType<ArgumentException>(
            "a cache the segment format never consults would report caching the reads never get");
    }

    [Fact]
    public async Task EngineOptions_BlockCacheTheSegmentFormatReadsThrough_ServesPointLookups()
    {
        var cache = new RecordingBlockCache(new LruBlockCache(maxBytes: 1024 * 1024));
        var options = new LsmStorageEngineOptions<SerializableWrapper<int>, SerializableWrapper<string>>(
            MemTableCapacity: 16,
            SegmentFolder: NewFolder("options-cache-wired"),
            SegmentPrefix: "seg",
            SegmentFormat: new SstSegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>>(blockCache: cache),
            BlockCache: cache);

        await using var engine = await LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>
            .OpenAsync(options).ConfigureAwait(true);
        await engine.SetAsync(K(1), V("one")).ConfigureAwait(true);
        await engine.FlushAsync().ConfigureAwait(true);
        (await engine.TryGetValueAsync(K(1)).ConfigureAwait(true)).Value.Value.Should().Be("one");

        engine.BlockCache.Should().BeSameAs(cache);
        cache.PutSegmentIds.Should().NotBeEmpty("the configured cache is the one the reads go through");
    }
}
