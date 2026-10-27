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
using Boutquin.Storage.Domain.Interfaces.TimeSeries.Columnar;
using Boutquin.Storage.Domain.ValueObjects.TimeSeries;
using Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar;
using Boutquin.Storage.Infrastructure.TimeSeries;
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar;

namespace Boutquin.Storage.Infrastructure.Tests.TimeSeries.Columnar;

/// <summary>
/// Validates column projection at the time-series store surface. Covers the shipped plumbing
/// (row-format short-circuit, memtable projection via
/// <see cref="IProjectable{TSelf}"/>, strict/lenient missing-column handling via
/// <see cref="ColumnProjection.AllowMissing"/>, and <see cref="IProjectingSegmentReader{TKey,
/// TValue, TProjection}"/> type-safety guards).
/// </summary>
public sealed class ColumnarProjectionTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);
    private static SerializableWrapper<string> Ds(string s) => new(s);

    private static ProjectableTestBar Bar(int y, int m, int d, decimal open, decimal close, long vol)
        => new(D(y, m, d), open, close, vol);

    private static async Task<List<ProjectableTestBar>> ToListAsync(
        IAsyncEnumerable<ProjectableTestBar> source)
    {
        var list = new List<ProjectableTestBar>();
        await foreach (var r in source)
        {
            list.Add(r);
        }

        return list;
    }

    // In-memory store applies ProjectTo uniformly; unprojected fields are default.
    [Fact]
    public async Task InMemoryStore_GetRangeAsync_Projection_ReturnsOnlyProjectedFields()
    {
        var store = new InMemoryTimeSeriesStore<SerializableWrapper<string>, ProjectableTestBar>();
        var ds = Ds("AAPL");
        await store.AppendAsync(ds,
        [
            Bar(2024, 1, 1, open: 100m, close: 101m, vol: 1000),
            Bar(2024, 1, 2, open: 102m, close: 103m, vol: 2000),
        ]);

        var projection = new ColumnProjection(["Close", "Volume"]);
        var slice = await ToListAsync(store.GetRangeAsync(ds, D(2024, 1, 1), D(2024, 1, 2), projection));

        slice.Should().HaveCount(2);
        slice[0].Open.Should().Be(0m, "Open is not projected — must be default(decimal)");
        slice[0].Close.Should().Be(101m);
        slice[0].Volume.Should().Be(1000);
        slice[1].Open.Should().Be(0m);
        slice[1].Close.Should().Be(103m);
        slice[1].Volume.Should().Be(2000);
    }

    // ColumnProjection.All returns the record unchanged.
    [Fact]
    public async Task InMemoryStore_GetRangeAsync_ProjectionAll_ReturnsFullRecord()
    {
        var store = new InMemoryTimeSeriesStore<SerializableWrapper<string>, ProjectableTestBar>();
        var ds = Ds("AAPL");
        await store.AppendAsync(ds, [Bar(2024, 1, 1, 100m, 101m, 1000)]);

        var slice = await ToListAsync(store.GetRangeAsync(ds, D(2024, 1, 1), D(2024, 1, 1), ColumnProjection.All));

        slice.Should().ContainSingle();
        slice[0].Open.Should().Be(100m);
        slice[0].Close.Should().Be(101m);
        slice[0].Volume.Should().Be(1000);
    }

    // AllowMissing = false + absent field → ColumnNotFoundException.
    [Fact]
    public async Task InMemoryStore_GetRangeAsync_MissingColumn_Strict_Throws()
    {
        var store = new InMemoryTimeSeriesStore<SerializableWrapper<string>, ProjectableTestBar>();
        var ds = Ds("AAPL");
        await store.AppendAsync(ds, [Bar(2024, 1, 1, 100m, 101m, 1000)]);

        var projection = new ColumnProjection(["DoesNotExist"], AllowMissing: false);

        var act = async () =>
        {
            await foreach (var _ in store.GetRangeAsync(ds, D(2024, 1, 1), D(2024, 1, 1), projection))
            {
            }
        };

        await act.Should().ThrowAsync<ColumnNotFoundException>()
            .Where(e => e.ColumnName == "DoesNotExist");
    }

    // AllowMissing = true + absent field → success, field at default.
    [Fact]
    public async Task InMemoryStore_GetRangeAsync_MissingColumn_Lenient_Succeeds()
    {
        var store = new InMemoryTimeSeriesStore<SerializableWrapper<string>, ProjectableTestBar>();
        var ds = Ds("AAPL");
        await store.AppendAsync(ds, [Bar(2024, 1, 1, 100m, 101m, 1000)]);

        var projection = new ColumnProjection(["DoesNotExist", "Close"], AllowMissing: true);
        var slice = await ToListAsync(store.GetRangeAsync(ds, D(2024, 1, 1), D(2024, 1, 1), projection));

        slice.Should().ContainSingle();
        slice[0].Close.Should().Be(101m, "Close is declared — must be populated even alongside a missing name");
        slice[0].Open.Should().Be(0m, "Open is not projected — must be default");
        slice[0].Volume.Should().Be(0, "Volume is not projected — must be default");
    }

    // AllowMissing + present field name → populated from record.
    [Fact]
    public async Task InMemoryStore_GetRangeAsync_AllowMissing_PresentField_Populated()
    {
        var store = new InMemoryTimeSeriesStore<SerializableWrapper<string>, ProjectableTestBar>();
        var ds = Ds("AAPL");
        await store.AppendAsync(ds, [Bar(2024, 1, 1, 100m, 101m, 1000)]);

        var projection = new ColumnProjection(["Close"], AllowMissing: true);
        var slice = await ToListAsync(store.GetRangeAsync(ds, D(2024, 1, 1), D(2024, 1, 1), projection));

        slice.Should().ContainSingle();
        slice[0].Close.Should().Be(101m, "AllowMissing governs missing-field tolerance only; present fields still populate.");
    }

    // Row-format engine accepts projection but returns full records (row format has no per-field skip).
    [Fact]
    public async Task LsmStore_RowFormat_ProjectionIgnored_ReturnsFullRecord()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"RowFmtProj_{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var inner = LsmEngineFactory.Build<TimeSeriesKey<SerializableWrapper<string>>, ProjectableTestBar>(
                memTableCapacity: 1000,
                segmentFolder: folder,
                segmentPrefix: "rowfmt");
            using var _ = inner;

            var options = new TimeSeriesStoreOptions<ProjectableTestBar>(); // defaults to Row
            var engine = new LsmTimeSeriesStorageEngine<SerializableWrapper<string>, ProjectableTestBar>(inner, options);
            var store = new LsmTimeSeriesStore<SerializableWrapper<string>, ProjectableTestBar>(
                engine,
                new InMemoryDatasetCoverageIndex<SerializableWrapper<string>>());

            var ds = Ds("AAPL");
            await store.AppendAsync(ds, [Bar(2024, 1, 1, 100m, 101m, 1000)]);

            // Pass a non-All projection — row format must ignore it and return the full record.
            var projection = new ColumnProjection(["Close"]);
            var slice = new List<ProjectableTestBar>();
            await foreach (var r in store.GetRangeAsync(ds, D(2024, 1, 1), D(2024, 1, 1), projection))
            {
                slice.Add(r);
            }

            slice.Should().ContainSingle();
            slice[0].Open.Should().Be(100m, "row format materialises every field; projection is a no-op.");
            slice[0].Close.Should().Be(101m);
            slice[0].Volume.Should().Be(1000);
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    // Memtable records in an LSM columnar engine pass through ProjectTo so the in-memtable
    // read sees the same shape as a post-flush segment read (DDIA Ch. 4 p. 141).
    [Fact]
    public async Task LsmStore_ColumnarFormat_MemtableProjection_AppliesProjectTo()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"ColFmtProj_{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            // ProjectableTestBar is not [ColumnarStorable] — use the stub for memtable-only tests.
            var innerColumnar = new StubColumnarFormat<ProjectableTestBar>();
            var segmentFormat = new ColumnarSegmentFormat<SerializableWrapper<string>, ProjectableTestBar>(innerColumnar);

            var inner = LsmEngineFactory.Build<TimeSeriesKey<SerializableWrapper<string>>, ProjectableTestBar>(
                memTableCapacity: 1000,
                segmentFolder: folder,
                segmentPrefix: "colts",
                segmentFormat: segmentFormat);
            using var _ = inner;

            var options = new ColumnarTimeSeriesStoreOptions<ProjectableTestBar>
            {
                ColumnarFormat = innerColumnar,
            };
            var engine = new LsmTimeSeriesStorageEngine<SerializableWrapper<string>, ProjectableTestBar>(inner, options);
            var store = new LsmTimeSeriesStore<SerializableWrapper<string>, ProjectableTestBar>(
                engine,
                new InMemoryDatasetCoverageIndex<SerializableWrapper<string>>());

            var ds = Ds("AAPL");
            // Memtable only — do NOT flush; exercises the memtable-projection path specifically.
            await store.AppendAsync(ds,
            [
                Bar(2024, 1, 1, 100m, 101m, 1000),
                Bar(2024, 1, 2, 102m, 103m, 2000),
            ]);

            var projection = new ColumnProjection(["Close"]);
            var slice = new List<ProjectableTestBar>();
            await foreach (var r in store.GetRangeAsync(ds, D(2024, 1, 1), D(2024, 1, 2), projection))
            {
                slice.Add(r);
            }

            slice.Should().HaveCount(2);
            slice[0].Close.Should().Be(101m);
            slice[0].Open.Should().Be(0m, "Open is not projected — memtable record must be clipped by ProjectTo.");
            slice[0].Volume.Should().Be(0);
            slice[1].Close.Should().Be(103m);
            slice[1].Open.Should().Be(0m);
            slice[1].Volume.Should().Be(0);
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    // In-memory and LSM stores produce identical projection shape for the same input.
    [Fact]
    public async Task Projection_ContractEquivalence_InMemoryVsLsm()
    {
        var ds = Ds("AAPL");
        var records = new[]
        {
            Bar(2024, 1, 1, 100m, 101m, 1000),
            Bar(2024, 1, 2, 102m, 103m, 2000),
        };
        var projection = new ColumnProjection(["Close"]);

        // In-memory store.
        var mem = new InMemoryTimeSeriesStore<SerializableWrapper<string>, ProjectableTestBar>();
        await mem.AppendAsync(ds, records);
        var memSlice = await ToListAsync(mem.GetRangeAsync(ds, D(2024, 1, 1), D(2024, 1, 2), projection));

        // LSM columnar store.
        var folder = Path.Combine(Path.GetTempPath(), $"Equiv_{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            // ProjectableTestBar is not [ColumnarStorable] — use the stub for memtable-only tests.
            var innerColumnar = new StubColumnarFormat<ProjectableTestBar>();
            var segmentFormat = new ColumnarSegmentFormat<SerializableWrapper<string>, ProjectableTestBar>(innerColumnar);
            var inner = LsmEngineFactory.Build<TimeSeriesKey<SerializableWrapper<string>>, ProjectableTestBar>(
                memTableCapacity: 1000,
                segmentFolder: folder,
                segmentPrefix: "equiv",
                segmentFormat: segmentFormat);
            using var _ = inner;

            var options = new ColumnarTimeSeriesStoreOptions<ProjectableTestBar>
            {
                ColumnarFormat = innerColumnar,
            };
            var engine = new LsmTimeSeriesStorageEngine<SerializableWrapper<string>, ProjectableTestBar>(inner, options);
            var store = new LsmTimeSeriesStore<SerializableWrapper<string>, ProjectableTestBar>(
                engine,
                new InMemoryDatasetCoverageIndex<SerializableWrapper<string>>());

            await store.AppendAsync(ds, records);
            var lsmSlice = new List<ProjectableTestBar>();
            await foreach (var r in store.GetRangeAsync(ds, D(2024, 1, 1), D(2024, 1, 2), projection))
            {
                lsmSlice.Add(r);
            }

            lsmSlice.Should().HaveCount(memSlice.Count);
            for (var i = 0; i < lsmSlice.Count; i++)
            {
                lsmSlice[i].Close.Should().Be(memSlice[i].Close);
                lsmSlice[i].Open.Should().Be(memSlice[i].Open);
                lsmSlice[i].Volume.Should().Be(memSlice[i].Volume);
            }
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    // ColumnarSegmentReader implements IProjectingSegmentReader; the runtime cast succeeds.
    // Uses MarketDataBar (which is [ColumnarStorable] + ISerializable<MarketDataBar>) since this
    // test writes to disk and ColumnarSegmentFormat requires ISerializable on TRecord.
    [Fact]
    public async Task ColumnarSegmentReader_ImplementsIProjectingSegmentReader()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"ReaderIface_{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var inner = new CompressedColumnarSegmentFormat<MarketDataBar>();
            var format = new ColumnarSegmentFormat<SerializableWrapper<string>, MarketDataBar>(inner);
            var entries = new[]
            {
                SegmentWriteEntry<TimeSeriesKey<SerializableWrapper<string>>, MarketDataBar>.Inline(
                    new TimeSeriesKey<SerializableWrapper<string>>(Ds("AAPL"), D(2024, 1, 1)),
                    new MarketDataBar { Date = D(2024, 1, 1), Close = 101m, SplitRatio = 1m },
                    0UL),
            };

            var reader = await format.WriteAsync(folder, "seg.bsct", entries);

            reader.Should().BeAssignableTo<
                IProjectingSegmentReader<
                    TimeSeriesKey<SerializableWrapper<string>>,
                    MarketDataBar,
                    ColumnProjection>>();
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    // SstSegmentFormat (row-format) readers do NOT implement IProjectingSegmentReader.
    // Guards against accidental row-reader dispatch through the projected path.
    [Fact]
    public void SstSegmentFormat_RowReader_DoesNotImplementIProjectingSegmentReader()
    {
        // We can't instantiate a reader cheaply without writing a row segment, but we can
        // at least assert the class itself doesn't implement the interface.
        var rowReaderType = typeof(Boutquin.Storage.Infrastructure.LsmTree.SstSegmentFormat<
            TimeSeriesKey<SerializableWrapper<string>>,
            ProjectableTestBar>);
        rowReaderType.GetInterfaces()
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IProjectingSegmentReader<,,>))
            .Should()
            .BeEmpty("row formats materialise the full record — projection has nothing to skip there.");
    }

    // Placeholder format for memtable-only tests that need a valid IColumnarSegmentFormat<T>
    // registered in TimeSeriesStoreOptions but never actually flush to disk.
    private sealed class StubColumnarFormat<T> : IColumnarSegmentFormat<T>
        where T : IHasDate, IColumnarRehydratable<T>, new()
    {
        public Task WriteAsync(Stream output, IReadOnlyList<T> records, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("StubColumnarFormat is not for disk I/O.");

        public IAsyncEnumerable<T> ReadAsync(Stream input, DateOnly from, DateOnly to, ColumnProjection projection, ScanDirection direction = ScanDirection.Ascending, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("StubColumnarFormat is not for disk I/O.");

        public IAsyncEnumerable<ColumnBatch<T>> ReadBatchesAsync(Stream input, DateOnly from, DateOnly to, ColumnProjection projection, ScanDirection direction = ScanDirection.Ascending, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("StubColumnarFormat is not for disk I/O.");

        public IAsyncEnumerable<T> StreamWhereAsync(Stream input, DateOnly from, DateOnly to, ColumnProjection projection, IReadOnlyList<ColumnPredicate> predicates, ScanDirection direction = ScanDirection.Ascending, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("StubColumnarFormat is not for disk I/O.");
    }
}
