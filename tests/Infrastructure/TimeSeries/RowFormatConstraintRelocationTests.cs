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

namespace Boutquin.Storage.Infrastructure.Tests.TimeSeries;

/// <summary>
/// Change-1 acceptance tests: the columnar rehydration contract (<c>IColumnarRehydratable</c>)
/// has been relocated off the format-agnostic store/engine/interface/options-base surfaces and
/// lives only on the columnar surfaces. A <c>[StorageSerializable]</c>-only dated record now
/// instantiates the row store/engine; the columnar boundary still enforces the contract.
/// </summary>
public sealed class RowFormatConstraintRelocationTests : IDisposable
{
    private readonly string _testDir;
    private readonly List<IDisposable> _disposables = [];

    public RowFormatConstraintRelocationTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"RowFmtRelocation_{Guid.NewGuid():N}");
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

    // AC-1.1 — a [StorageSerializable]-only record (NO IColumnarRehydratable) type-checks as the
    // TRecord of LsmTimeSeriesStore / LsmTimeSeriesStorageEngine with row format and round-trips
    // append → GetRangeAsync. The mere compilation of RowOnlyTestRecord in these generic positions
    // is the compile-time proof of the relocation; the green round-trip proves the row path works.
    [Fact]
    public async Task RowOnlyRecord_TypeChecksAndRoundTripsThroughRowStore()
    {
        var segmentDir = Path.Combine(_testDir, $"seg_{Guid.NewGuid():N}");
        Directory.CreateDirectory(segmentDir);
        var manifestPath = Path.Combine(_testDir, $"coverage_{Guid.NewGuid():N}.json");

        var engine = LsmEngineFactory.Build<
            TimeSeriesKey<SerializableWrapper<string>>,
            RowOnlyTestRecord>(
            memTableCapacity: 4,
            segmentFolder: segmentDir,
            segmentPrefix: "seg");
        _disposables.Add(engine);

        // The decorator default options (row format) — base options no longer carry the
        // columnar rehydration constraint, so this closes over RowOnlyTestRecord.
        var tsEngine = new LsmTimeSeriesStorageEngine<SerializableWrapper<string>, RowOnlyTestRecord>(engine);
        var coverage = new FileDatasetCoverageIndex<SerializableWrapper<string>>(manifestPath);
        var store = new LsmTimeSeriesStore<SerializableWrapper<string>, RowOnlyTestRecord>(tsEngine, coverage);

        var ds = new SerializableWrapper<string>("OPTQ");
        var d0 = new DateOnly(2024, 1, 1);
        var records = Enumerable.Range(0, 6)
            .Select(i => new RowOnlyTestRecord(d0.AddDays(i), 100 + i))
            .ToList();

        await store.AppendAsync(ds, records).ConfigureAwait(true);
        await engine.FlushAsync().ConfigureAwait(true);

        var fetched = new List<RowOnlyTestRecord>();
        await foreach (var r in store.GetRangeAsync(ds, d0, d0.AddDays(5), ColumnProjection.All).ConfigureAwait(true))
        {
            fetched.Add(r);
        }

        fetched.Should().HaveCount(6);
        fetched.Select(r => r.Value).Should().Equal(100, 101, 102, 103, 104, 105);
    }

    // AC-1.1 (engine-level) — the projection-aware ITimeSeriesStorageEngine.GetRangeAsync surface
    // also accepts the row-only record and forwards through the row short-circuit.
    [Fact]
    public async Task RowOnlyRecord_TypeChecksOnEngineAndForwardsRange()
    {
        var segmentDir = Path.Combine(_testDir, $"seg_{Guid.NewGuid():N}");
        Directory.CreateDirectory(segmentDir);

        var engine = LsmEngineFactory.Build<
            TimeSeriesKey<SerializableWrapper<string>>,
            RowOnlyTestRecord>(
            memTableCapacity: 4,
            segmentFolder: segmentDir,
            segmentPrefix: "seg");
        _disposables.Add(engine);

        ITimeSeriesStorageEngine<SerializableWrapper<string>, RowOnlyTestRecord> tsEngine =
            new LsmTimeSeriesStorageEngine<SerializableWrapper<string>, RowOnlyTestRecord>(engine);

        var d0 = new DateOnly(2024, 3, 1);
        var key = new TimeSeriesKey<SerializableWrapper<string>>(new SerializableWrapper<string>("DIV"), d0);
        await tsEngine.SetAsync(key, new RowOnlyTestRecord(d0, 7)).ConfigureAwait(true);

        var start = new TimeSeriesKey<SerializableWrapper<string>>(new SerializableWrapper<string>("DIV"), d0.AddDays(-1));
        var end = new TimeSeriesKey<SerializableWrapper<string>>(new SerializableWrapper<string>("DIV"), d0.AddDays(1));

        var results = new List<RowOnlyTestRecord>();
        await foreach (var (_, v) in tsEngine.GetRangeAsync(start, end, ColumnProjection.All).ConfigureAwait(true))
        {
            results.Add(v);
        }

        results.Should().ContainSingle();
        results[0].Value.Should().Be(7);
    }

    // AC-1.2 — a [ColumnarStorable] record still instantiates the columnar stack via the builder
    // and round-trips. No columnar regression from the relocation. (ColumnarRecordStructBar carries
    // the generator-emitted IColumnarRehydratable, satisfying the columnar builder's constraint.)
    [Fact]
    public async Task ColumnarRecord_StillBuildsColumnarStackAndRoundTrips()
    {
        var segmentDir = Path.Combine(_testDir, $"col_{Guid.NewGuid():N}");
        Directory.CreateDirectory(segmentDir);

        await using var stack = await ColumnarTimeSeriesEngineBuilder
            .BuildAsync<SerializableWrapper<string>, ColumnarRecordStructBar>(
                segmentFolder: segmentDir,
                segmentPrefix: "colbar",
                memTableCapacity: 4)
            .ConfigureAwait(true);

        var ds = new SerializableWrapper<string>("AAPL");
        var d0 = new DateOnly(2024, 1, 2);
        var records = Enumerable.Range(0, 8)
            .Select(i => new ColumnarRecordStructBar(
                d0.AddDays(i), 99m + i, 102m + i, 98m + i, 100m + i, 100m + i, 1_000_000L + i))
            .ToList();

        await stack.Store.AppendAsync(ds, records).ConfigureAwait(true);
        await stack.InnerEngine.FlushAsync().ConfigureAwait(true);

        var fetched = new List<ColumnarRecordStructBar>();
        await foreach (var r in stack.Store.GetRangeAsync(ds, d0, d0.AddDays(7), ColumnProjection.All).ConfigureAwait(true))
        {
            fetched.Add(r);
        }

        fetched.Should().HaveCount(8);
        fetched.Select(r => r.Close).Should().Equal(records.Select(r => r.Close));
    }

    // AC-1.3 — the columnar boundary STILL enforces IColumnarRehydratable as a COMPILE-TIME
    // constraint. The block below MUST NOT COMPILE: ColumnarTimeSeriesEngineBuilder.BuildAsync
    // (and ColumnarTimeSeriesStoreOptions / ColumnarSegmentFormat) constrain
    // TRecord : ..., IColumnarRehydratable<TRecord>, and RowOnlyTestRecord does not implement it.
    // It is guarded by `#if false` so the suite compiles; uncommenting it is the negative-compile
    // proof. This is exactly why Option A was chosen over Option B (which would have deferred the
    // rejection to a runtime cast).
#if BUILD_ARENA_AC_1_3_MUST_NOT_COMPILE
    private static async Task Ac13_ColumnarBuilderRejectsRowOnlyRecord_DoesNotCompile()
    {
        // ERROR (expected): the type 'RowOnlyTestRecord' cannot be used as type parameter
        // 'TRecord' — there is no implicit conversion from 'RowOnlyTestRecord' to
        // 'IColumnarRehydratable<RowOnlyTestRecord>'.
        _ = await ColumnarTimeSeriesEngineBuilder
            .BuildAsync<SerializableWrapper<string>, RowOnlyTestRecord>(segmentFolder: "x");

        // ERROR (expected): same constraint failure on the columnar-options type.
        _ = new ColumnarTimeSeriesStoreOptions<RowOnlyTestRecord>();
    }
#endif
}
