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
using Boutquin.Storage.Domain.ValueObjects.TimeSeries;
using Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar;
using Boutquin.Storage.Infrastructure.Tests.TimeSeries.Columnar;
using Boutquin.Storage.Infrastructure.TimeSeries;
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar;

namespace Boutquin.Storage.Infrastructure.Tests.TimeSeries;

public sealed class LsmTimeSeriesStorageEngineTests : IDisposable
{
    private readonly string _testDir;
    private readonly List<IDisposable> _disposables = [];

    public LsmTimeSeriesStorageEngineTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"LsmTsEngineTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        foreach (var d in _disposables)
        {
            try { d.Dispose(); } catch { /* best-effort */ }
        }

        if (Directory.Exists(_testDir))
        {
            try { Directory.Delete(_testDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    private LsmStorageEngine<TimeSeriesKey<SerializableWrapper<string>>, TimeSeriesTestRecord> NewInnerEngine(
        IColumnarSegmentFormat<TimeSeriesTestRecord>? columnarFormat = null)
    {
        var segmentDir = Path.Combine(_testDir, $"seg_{Guid.NewGuid():N}");
        Directory.CreateDirectory(segmentDir);
        var engine = LsmEngineFactory.Build<
            TimeSeriesKey<SerializableWrapper<string>>,
            TimeSeriesTestRecord>(
            memTableCapacity: 4,
            segmentFolder: segmentDir,
            segmentPrefix: "seg",
            segmentFormat: columnarFormat is null
                ? null
                : new ColumnarSegmentFormat<SerializableWrapper<string>, TimeSeriesTestRecord>(columnarFormat));
        _disposables.Add(engine);
        return engine;
    }

    // Options defaults: row format, no columnar format supplied.
    [Fact]
    public void Options_Default_SelectsRowFormat()
    {
        var options = new TimeSeriesStoreOptions<TimeSeriesTestRecord>();

        options.SegmentFormat.Should().Be(TimeSeriesSegmentFormat.Row);
        // The base (row-safe) options no longer carry a columnar format member (Option A):
        // the columnar format lives only on ColumnarTimeSeriesStoreOptions.
        options.IsColumnarFormatSupplied.Should().BeFalse();
    }

    // ColumnSortSpec — construction-time validation (tuple shape: (Name, Direction)).
    [Fact]
    public void ColumnSortSpec_NullColumns_ThrowsArgumentNullException()
    {
        var act = () => new ColumnSortSpec(null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("columns");
    }

    [Fact]
    public void ColumnSortSpec_EmptyColumns_ThrowsArgumentException()
    {
        var act = () => new ColumnSortSpec([]);
        act.Should().Throw<ArgumentException>().WithParameterName("columns");
    }

    [Fact]
    public void ColumnSortSpec_WhitespaceColumn_ThrowsArgumentException()
    {
        var act = () => new ColumnSortSpec([("Date", SortDirection.Ascending), ("   ", SortDirection.Ascending)]);
        act.Should().Throw<ArgumentException>().WithParameterName("columns");
    }

    [Fact]
    public void ColumnSortSpec_ValidColumns_StoresColumnsInOrder()
    {
        var spec = new ColumnSortSpec([("Date", SortDirection.Ascending), ("Ticker", SortDirection.Ascending)]);
        spec.Columns.Should().Equal([("Date", SortDirection.Ascending), ("Ticker", SortDirection.Ascending)]);
    }

    [Fact]
    public void ColumnSortSpec_DateAscending_HasSingleDateColumnAscending()
    {
        ColumnSortSpec.DateAscending.Columns.Should().Equal([("Date", SortDirection.Ascending)]);
    }

    [Fact]
    public void ColumnSortSpec_MutatingSourceList_DoesNotAffectSpec()
    {
        // Fix 10: Columns must be a defensive copy so post-construction mutations to the source
        // list cannot alter the sort spec — which is load-bearing for on-disk segment layout.
        var source = new List<(string Name, SortDirection Direction)>
        {
            ("Date", SortDirection.Ascending),
            ("Price", SortDirection.Descending),
        };
        var spec = new ColumnSortSpec(source);

        source[0] = ("Mutated", SortDirection.Descending);
        source.Add(("Extra", SortDirection.Ascending));

        spec.Columns.Should().HaveCount(2,
            because: "Columns is a defensive copy taken at construction time");
        spec.Columns[0].Name.Should().Be("Date",
            because: "the original first entry must not be replaced by the mutation");
    }

    // ───── ColumnarTimeSeriesStoreOptions construction-time validation (Option A) ────────

    [Fact]
    public void ColumnarTimeSeriesStoreOptions_NullFormat_ThrowsArgumentNullException()
    {
        // The validated columnar-options constructor must reject a null format at construction.
        var act = () => new ColumnarTimeSeriesStoreOptions<TimeSeriesTestRecord>(null!);
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("columnarFormat");
    }

    [Fact]
    public void ColumnarTimeSeriesStoreOptions_WithFormat_SelectsColumnarAndCarriesFormat()
    {
        var format = new StubColumnarFormat();
        var options = new ColumnarTimeSeriesStoreOptions<TimeSeriesTestRecord>(format);

        options.SegmentFormat.Should().Be(TimeSeriesSegmentFormat.Columnar);
        options.ColumnarFormat.Should().BeSameAs(format);
        options.IsColumnarFormatSupplied.Should().BeTrue();
    }

    [Fact]
    public void TimeSeriesStoreOptions_RowDefault_DoesNotThrow()
    {
        var act = () => new TimeSeriesStoreOptions<TimeSeriesTestRecord>
        {
            SegmentFormat = TimeSeriesSegmentFormat.Row,
        };
        act.Should().NotThrow();
    }

    // No options → defaults applied → Row path → wraps the underlying engine transparently.
    [Fact]
    public void Constructor_WithoutOptions_DefaultsToRowAndDoesNotThrow()
    {
        var inner = NewInnerEngine();

        var engine = new LsmTimeSeriesStorageEngine<SerializableWrapper<string>, TimeSeriesTestRecord>(inner);

        engine.Options.SegmentFormat.Should().Be(TimeSeriesSegmentFormat.Row);
        engine.SegmentCount.Should().Be(inner.SegmentCount);
    }

    // Explicit Row options work identically to defaults.
    [Fact]
    public void Constructor_WithRowOptions_DoesNotThrow()
    {
        var inner = NewInnerEngine();
        var options = new TimeSeriesStoreOptions<TimeSeriesTestRecord>
        {
            SegmentFormat = TimeSeriesSegmentFormat.Row,
        };

        var act = () => new LsmTimeSeriesStorageEngine<SerializableWrapper<string>, TimeSeriesTestRecord>(inner, options);

        act.Should().NotThrow();
    }

    // Columnar WITHOUT a supplied ColumnarFormat is a caller error — flagged distinctly from
    // the Phase-1 NotImplementedException so the user sees the most actionable message.
    [Fact]
    public void Constructor_WithColumnarOptions_NoColumnarFormatSupplied_ThrowsArgumentException()
    {
        var inner = NewInnerEngine();
        var options = new TimeSeriesStoreOptions<TimeSeriesTestRecord>
        {
            SegmentFormat = TimeSeriesSegmentFormat.Columnar,
            // ColumnarFormat deliberately left null
        };

        var act = () => new LsmTimeSeriesStorageEngine<SerializableWrapper<string>, TimeSeriesTestRecord>(inner, options);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*ColumnarFormat*");
    }

    // Columnar WITH a supplied format constructs cleanly. The wrapper forwards storage
    // operations to the inner engine; the inner engine is the one that must have been
    // constructed with ColumnarSegmentFormat to actually produce columnar bytes on disk.
    // This test only exercises the wrapper's construction-time validation, not the
    // end-to-end flush path (that is covered by LsmColumnarIntegrationTests).
    [Fact]
    public void Constructor_WithColumnarOptions_AndColumnarFormat_DoesNotThrow()
    {
        var format = new StubColumnarFormat();
        var inner = NewInnerEngine(format);
        var options = new ColumnarTimeSeriesStoreOptions<TimeSeriesTestRecord>
        {
            ColumnarFormat = format,
        };

        var act = () => new LsmTimeSeriesStorageEngine<SerializableWrapper<string>, TimeSeriesTestRecord>(inner, options);

        act.Should().NotThrow();
    }

    // ───── Columnar options must describe the inner engine's actual segment format ─────

    private LsmStorageEngine<TimeSeriesKey<SerializableWrapper<string>>, MarketDataBar> NewBarEngine(
        ISegmentFormat<TimeSeriesKey<SerializableWrapper<string>>, MarketDataBar>? segmentFormat)
    {
        var segmentDir = Path.Combine(_testDir, $"bar_{Guid.NewGuid():N}");
        Directory.CreateDirectory(segmentDir);
        var engine = segmentFormat is null
            ? LsmEngineFactory.Build<TimeSeriesKey<SerializableWrapper<string>>, MarketDataBar>(
                memTableCapacity: 4, segmentFolder: segmentDir, segmentPrefix: "bar")
            : LsmEngineFactory.Build<TimeSeriesKey<SerializableWrapper<string>>, MarketDataBar>(
                memTableCapacity: 4, segmentFolder: segmentDir, segmentPrefix: "bar", segmentFormat: segmentFormat);
        _disposables.Add(engine);
        return engine;
    }

    // Columnar options over an engine that writes row .sst segments would report Columnar while the
    // engine writes rows; the options' format would never be used.
    [Fact]
    public void Constructor_ColumnarOptions_InnerEngineWritesRowSegments_ThrowsArgumentException()
    {
        var inner = NewBarEngine(segmentFormat: null);
        var options = new ColumnarTimeSeriesStoreOptions<MarketDataBar>(new CompressedColumnarSegmentFormat<MarketDataBar>());

        var act = () => new LsmTimeSeriesStorageEngine<SerializableWrapper<string>, MarketDataBar>(inner, options);

        act.Should().Throw<ArgumentException>().WithParameterName("options");
    }

    [Fact]
    public void Constructor_ColumnarOptions_FormatNotTheOneTheInnerEngineWraps_ThrowsArgumentException()
    {
        var wrapped = new CompressedColumnarSegmentFormat<MarketDataBar>();
        var inner = NewBarEngine(new ColumnarSegmentFormat<SerializableWrapper<string>, MarketDataBar>(wrapped));
        var options = new ColumnarTimeSeriesStoreOptions<MarketDataBar>(new CompressedColumnarSegmentFormat<MarketDataBar>(rowGroupMaxRows: 16));

        var act = () => new LsmTimeSeriesStorageEngine<SerializableWrapper<string>, MarketDataBar>(inner, options);

        act.Should().Throw<ArgumentException>().WithParameterName("options");
    }

    [Fact]
    public void Constructor_ColumnarOptions_FormatTheInnerEngineWraps_Constructs()
    {
        var wrapped = new CompressedColumnarSegmentFormat<MarketDataBar>();
        var inner = NewBarEngine(new ColumnarSegmentFormat<SerializableWrapper<string>, MarketDataBar>(wrapped));
        var options = new ColumnarTimeSeriesStoreOptions<MarketDataBar>(wrapped);

        var engine = new LsmTimeSeriesStorageEngine<SerializableWrapper<string>, MarketDataBar>(inner, options);

        engine.Options.Should().BeSameAs(options);
    }

    // Null inner engine is rejected.
    [Fact]
    public void Constructor_NullInner_ThrowsArgumentNullException()
    {
        var act = () => new LsmTimeSeriesStorageEngine<SerializableWrapper<string>, TimeSeriesTestRecord>(inner: null!);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("inner");
    }

    // Pass-through for Row: a write through the wrapper is visible via the inner engine.
    [Fact]
    public async Task RowPath_ForwardsStorageOperationsToInnerEngine()
    {
        var inner = NewInnerEngine();
        var engine = new LsmTimeSeriesStorageEngine<SerializableWrapper<string>, TimeSeriesTestRecord>(inner);

        var key = new TimeSeriesKey<SerializableWrapper<string>>(
            new SerializableWrapper<string>("AAPL"), new DateOnly(2020, 1, 1));
        var value = new TimeSeriesTestRecord(new DateOnly(2020, 1, 1), 42);

        await engine.SetAsync(key, value);
        var (fetched, found) = await engine.TryGetValueAsync(key);

        found.Should().BeTrue();
        fetched.Should().Be(value);
    }

    // Columnar projection static initializer is safe and matches the All-columns contract.
    [Fact]
    public void ColumnProjection_All_IsEmptyFieldNamesAndStrictDefault()
    {
        ColumnProjection.All.FieldNames.Should().BeEmpty();
        ColumnProjection.All.AllowMissing.Should().BeFalse();
    }

    // AllowMissing default is strict.
    [Fact]
    public void ColumnProjection_ExplicitFieldNames_AllowMissingDefaultsToFalse()
    {
        var projection = new ColumnProjection(["Date", "AdjClose"]);

        projection.FieldNames.Should().Equal("Date", "AdjClose");
        projection.AllowMissing.Should().BeFalse();
    }

    // Engine-level snapshot capability reports isolation-via-pinning, which is unconditionally
    // available for the LSM engine regardless of segment format. The format-level
    // ISegmentFormat.SupportsSnapshots flag (seqno-filter support) is a SEPARATE, narrower
    // predicate — see ColumnarSnapshotIsolationTests for the round-trip proofs.
    [Fact]
    public void SupportsSnapshots_RowFormat_ReturnsTrue()
    {
        var inner = NewInnerEngine();
        var options = new TimeSeriesStoreOptions<TimeSeriesTestRecord>
        {
            SegmentFormat = TimeSeriesSegmentFormat.Row,
        };
        var engine = new LsmTimeSeriesStorageEngine<SerializableWrapper<string>, TimeSeriesTestRecord>(inner, options);

        engine.SupportsSnapshots.Should().BeTrue();
    }

    // R2a (new contract): the engine reports isolation-via-pinning, which is format-agnostic.
    // Columnar engines now report TRUE — isolation comes from segment pinning + an at-acquisition
    // memtable copy, not from per-entry seqno filtering. (Previously this asserted FALSE, rewritten
    // to reflect the new isolation contract.)
    [Fact]
    public void SupportsSnapshots_ColumnarFormat_ReturnsTrue()
    {
        var format = new StubColumnarFormat();
        var inner = NewInnerEngine(format);
        var options = new ColumnarTimeSeriesStoreOptions<TimeSeriesTestRecord>
        {
            ColumnarFormat = format,
        };
        var engine = new LsmTimeSeriesStorageEngine<SerializableWrapper<string>, TimeSeriesTestRecord>(inner, options);

        engine.SupportsSnapshots.Should().BeTrue(
            "isolation is enforced by engine-level segment pinning, which is format-agnostic");
    }

    // AC-2.1: a default columnar store permits AcquireSnapshotAsync (no throw). Replaces the
    // deleted ...ThrowsInvalidOperationException test. The over-conservative guard is gone; the
    // engine's pinning machinery serves columnar snapshots correctly (proven end-to-end in
    // ColumnarSnapshotIsolationTests).
    [Fact]
    public async Task AcquireSnapshotAsync_WhenColumnarFormat_DoesNotThrow()
    {
        var format = new StubColumnarFormat();
        var inner = NewInnerEngine(format);
        var options = new ColumnarTimeSeriesStoreOptions<TimeSeriesTestRecord>
        {
            ColumnarFormat = format,
        };
        var engine = new LsmTimeSeriesStorageEngine<SerializableWrapper<string>, TimeSeriesTestRecord>(inner, options);

        var snapshot = await engine.AcquireSnapshotAsync();
        await using var _ = snapshot;

        snapshot.Should().NotBeNull("columnar snapshots are permitted under R2a");
    }

    // ── GetRangeAsync projected-range coverage ────────────────────────────────

    // A columnar engine wired the way the options describe: the inner engine writes columnar
    // segments through an envelope over the same format the options carry.
    private (LsmTimeSeriesStorageEngine<SerializableWrapper<string>, MarketDataBar> Engine,
             LsmStorageEngine<TimeSeriesKey<SerializableWrapper<string>>, MarketDataBar> Inner) NewColumnarEngine()
    {
        var format = new CompressedColumnarSegmentFormat<MarketDataBar>();
        var inner = NewBarEngine(new ColumnarSegmentFormat<SerializableWrapper<string>, MarketDataBar>(format));
        var engine = new LsmTimeSeriesStorageEngine<SerializableWrapper<string>, MarketDataBar>(
            inner, new ColumnarTimeSeriesStoreOptions<MarketDataBar>(format));
        return (engine, inner);
    }

    private static MarketDataBar Bar(DateOnly date, decimal close)
        => new() { Date = date, Open = close, High = close, Low = close, Close = close, SplitRatio = 1m };

    [Fact]
    public async Task GetRangeAsync_ProjectionAll_ShortCircuitsToGenericRange()
    {
        // ColumnProjection.All (FieldNames.Count == 0) skips the projected merge and calls
        // _inner.GetRangeAsync directly, so every record in the engine is returned.
        var (engine, inner) = NewColumnarEngine();

        var d0 = new DateOnly(2024, 1, 1);
        var key = new TimeSeriesKey<SerializableWrapper<string>>(new SerializableWrapper<string>("AAPL"), d0);
        await engine.SetAsync(key, Bar(d0, 42m)).ConfigureAwait(true);
        await inner.FlushAsync().ConfigureAwait(true);

        var start = new TimeSeriesKey<SerializableWrapper<string>>(
            new SerializableWrapper<string>("AAPL"), d0.AddDays(-1));
        var end = new TimeSeriesKey<SerializableWrapper<string>>(
            new SerializableWrapper<string>("AAPL"), d0.AddDays(1));

        var results = new List<(TimeSeriesKey<SerializableWrapper<string>> Key, MarketDataBar Value)>();
        await foreach (var item in engine.GetRangeAsync(start, end, ColumnProjection.All).ConfigureAwait(false))
        {
            results.Add(item);
        }

        results.Should().HaveCount(1);
        results[0].Value.Close.Should().Be(42m);
    }

    [Fact]
    public async Task GetRangeAsync_WithProjection_ExcludesTombstonedKeys()
    {
        var (engine, inner) = NewColumnarEngine();

        var d0 = new DateOnly(2024, 1, 1);
        var key = new TimeSeriesKey<SerializableWrapper<string>>(new SerializableWrapper<string>("AAPL"), d0);
        await engine.SetAsync(key, Bar(d0, 99m)).ConfigureAwait(true);
        await inner.FlushAsync().ConfigureAwait(true);

        await engine.RemoveAsync(key).ConfigureAwait(true);
        await inner.FlushAsync().ConfigureAwait(true);

        var start = new TimeSeriesKey<SerializableWrapper<string>>(
            new SerializableWrapper<string>("AAPL"), d0.AddDays(-1));
        var end = new TimeSeriesKey<SerializableWrapper<string>>(
            new SerializableWrapper<string>("AAPL"), d0.AddDays(1));

        var results = new List<(TimeSeriesKey<SerializableWrapper<string>> Key, MarketDataBar Value)>();
        await foreach (var item in engine.GetRangeAsync(start, end, new ColumnProjection(["Date"])).ConfigureAwait(false))
        {
            results.Add(item);
        }

        results.Should().BeEmpty("tombstoned key must be excluded from projected range");
    }

    [Fact]
    public async Task GetRangeAsync_WithProjection_MemtableEntryOverridesSegmentEntry()
    {
        var (engine, inner) = NewColumnarEngine();

        var d0 = new DateOnly(2024, 1, 1);
        var key = new TimeSeriesKey<SerializableWrapper<string>>(new SerializableWrapper<string>("AAPL"), d0);
        await engine.SetAsync(key, Bar(d0, 1m)).ConfigureAwait(true);
        await inner.FlushAsync().ConfigureAwait(true);

        // Overwrite with V2 in memtable — no flush
        await engine.SetAsync(key, Bar(d0, 2m)).ConfigureAwait(true);

        var start = new TimeSeriesKey<SerializableWrapper<string>>(
            new SerializableWrapper<string>("AAPL"), d0.AddDays(-1));
        var end = new TimeSeriesKey<SerializableWrapper<string>>(
            new SerializableWrapper<string>("AAPL"), d0.AddDays(1));

        var results = new List<(TimeSeriesKey<SerializableWrapper<string>> Key, MarketDataBar Value)>();
        await foreach (var item in engine.GetRangeAsync(start, end, new ColumnProjection(["Date", "Close"])).ConfigureAwait(false))
        {
            results.Add(item);
        }

        results.Should().HaveCount(1);
        results[0].Value.Close.Should().Be(2m, "memtable entry must win over segment entry");
    }

    [Fact]
    public async Task GetRangeAsync_FallbackPath_WhenInnerIsNotLsmStorageEngine()
    {
        // When the inner engine is not LsmStorageEngine<,>, GetRangeProjectedAsync falls back
        // to calling _inner.GetRangeAsync and applying ProjectTo. The stub returns one entry.
        var d0 = new DateOnly(2024, 6, 1);
        var key = new TimeSeriesKey<SerializableWrapper<string>>(new SerializableWrapper<string>("AAPL"), d0);
        var value = new TimeSeriesTestRecord(d0, 77);

        var start = new TimeSeriesKey<SerializableWrapper<string>>(
            new SerializableWrapper<string>("AAPL"), d0.AddDays(-1));
        var end = new TimeSeriesKey<SerializableWrapper<string>>(
            new SerializableWrapper<string>("AAPL"), d0.AddDays(1));

        var stubInner = new Mock<ILsmStorageEngine<TimeSeriesKey<SerializableWrapper<string>>, TimeSeriesTestRecord>>();
        stubInner
            .Setup(e => e.GetRangeAsync(start, end, It.IsAny<CancellationToken>()))
            .Returns(SingleAsync((key, value)));

        var options = new ColumnarTimeSeriesStoreOptions<TimeSeriesTestRecord>
        {
            ColumnarFormat = new StubColumnarFormat(),
        };
        var engine = new LsmTimeSeriesStorageEngine<SerializableWrapper<string>, TimeSeriesTestRecord>(
            stubInner.Object, options);

        var results = new List<(TimeSeriesKey<SerializableWrapper<string>> Key, TimeSeriesTestRecord Value)>();
        await foreach (var item in engine.GetRangeAsync(start, end, new ColumnProjection(["Date"])).ConfigureAwait(false))
        {
            results.Add(item);
        }

        results.Should().HaveCount(1);
        results[0].Key.Should().Be(key);
    }

    // Helper — yields a single item as IAsyncEnumerable, for Moq .Returns(...) on
    // methods that now return IAsyncEnumerable instead of Task<IEnumerable<>>.
#pragma warning disable CS1998 // no explicit awaits needed — the method body is async-iterator sugar
    private static async IAsyncEnumerable<T> SingleAsync<T>(T item,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
#pragma warning restore CS1998
    {
        cancellationToken.ThrowIfCancellationRequested();
        yield return item;
    }

    // A no-op stand-in for the real source-generated columnar format. Existence is enough to
    // satisfy the "ColumnarFormat must be supplied" guard.
    private sealed class StubColumnarFormat : IColumnarSegmentFormat<TimeSeriesTestRecord>
    {
        public Task WriteAsync(Stream output, IReadOnlyList<TimeSeriesTestRecord> records, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

#pragma warning disable CS1998 // no awaits — yield-break placeholder
        public async IAsyncEnumerable<TimeSeriesTestRecord> ReadAsync(
            Stream input,
            DateOnly from,
            DateOnly to,
            ColumnProjection projection,
            ScanDirection direction = ScanDirection.Ascending,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
#pragma warning restore CS1998
        {
            yield break;
        }

#pragma warning disable CS1998 // no awaits — yield-break placeholder
        public async IAsyncEnumerable<ColumnBatch<TimeSeriesTestRecord>> ReadBatchesAsync(
            Stream input,
            DateOnly from,
            DateOnly to,
            ColumnProjection projection,
            ScanDirection direction = ScanDirection.Ascending,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
#pragma warning restore CS1998
        {
            yield break;
        }

        public IAsyncEnumerable<TimeSeriesTestRecord> StreamWhereAsync(
            Stream input, DateOnly from, DateOnly to, ColumnProjection projection,
            IReadOnlyList<ColumnPredicate> predicates,
            ScanDirection direction = ScanDirection.Ascending,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("StubColumnarFormat is not for disk I/O.");
    }
}
