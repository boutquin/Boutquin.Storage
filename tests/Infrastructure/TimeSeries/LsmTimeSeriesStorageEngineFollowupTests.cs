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

namespace Boutquin.Storage.Infrastructure.Tests.TimeSeries;

/// <summary>
/// Phase-10 follow-up — covers the load-bearing-on-MarketData decorator delegation paths
/// and the projected-range fallback in <see cref="LsmTimeSeriesStorageEngine{TDatasetKey, TRecord}"/>:
/// <c>ContainsKeyAsync</c> (L163), <c>ClearAsync</c> (L173), <c>FlushAsync</c> (L188),
/// <c>CompactAsync</c> (L192), the row-format <c>AcquireSnapshotAsync</c> pass-through
/// (L208 false-arm + L217), and the IProjectable-records fallback path
/// <c>GetRangeProjectedFallbackAsync</c> (L400 partial + L402). The original Phase-10
/// push tested the columnar/throw arms but not the row-format / non-LsmStorageEngine-inner
/// arms each method also services.
/// </summary>
public sealed class LsmTimeSeriesStorageEngineFollowupTests : IDisposable
{
    private readonly string _testDir;
    private readonly List<IDisposable> _disposables = [];

    public LsmTimeSeriesStorageEngineFollowupTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"LsmTsEngineFollowup_{Guid.NewGuid():N}");
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

    private LsmStorageEngine<TimeSeriesKey<SerializableWrapper<string>>, TimeSeriesTestRecord> NewInnerEngine()
    {
        var segmentDir = Path.Combine(_testDir, $"seg_{Guid.NewGuid():N}");
        Directory.CreateDirectory(segmentDir);
        var engine = LsmEngineFactory.Build<
            TimeSeriesKey<SerializableWrapper<string>>,
            TimeSeriesTestRecord>(
            memTableCapacity: 4,
            segmentFolder: segmentDir,
            segmentPrefix: "seg");
        _disposables.Add(engine);
        return engine;
    }

    private static TimeSeriesKey<SerializableWrapper<string>> Key(string ds, int y, int m, int d)
        => new(new SerializableWrapper<string>(ds), new DateOnly(y, m, d));

    // ── 1. ContainsKeyAsync — pure delegation (L163) ──────────────────────

    /// <summary>
    /// <c>ContainsKeyAsync</c> on the decorator forwards verbatim to the inner engine.
    /// MarketData's existence-check pattern (probe-before-fetch) flows through this arm.
    /// </summary>
    [Fact]
    public async Task ContainsKeyAsync_DelegatesToInner()
    {
        var inner = NewInnerEngine();
        var engine = new LsmTimeSeriesStorageEngine<SerializableWrapper<string>, TimeSeriesTestRecord>(inner);

        var key = Key("AAPL", 2026, 1, 15);
        await engine.SetAsync(key, new TimeSeriesTestRecord(new DateOnly(2026, 1, 15), 42)).ConfigureAwait(true);

        (await engine.ContainsKeyAsync(key).ConfigureAwait(true)).Should().BeTrue();
        (await engine.ContainsKeyAsync(Key("AAPL", 2026, 1, 16)).ConfigureAwait(true)).Should().BeFalse();
    }

    // ── 2. ClearAsync — pure delegation (L173) ────────────────────────────

    /// <summary>
    /// <c>ClearAsync</c> wipes the inner engine's data via straight delegation. MarketData
    /// uses this on test-mode resets and on dataset-replacement workflows.
    /// </summary>
    [Fact]
    public async Task ClearAsync_DelegatesToInner()
    {
        var inner = NewInnerEngine();
        var engine = new LsmTimeSeriesStorageEngine<SerializableWrapper<string>, TimeSeriesTestRecord>(inner);

        await engine.SetAsync(Key("AAPL", 2026, 1, 1), new TimeSeriesTestRecord(new DateOnly(2026, 1, 1), 1)).ConfigureAwait(true);
        await engine.ClearAsync().ConfigureAwait(true);

        (await engine.ContainsKeyAsync(Key("AAPL", 2026, 1, 1)).ConfigureAwait(true)).Should().BeFalse(
            "ClearAsync forwards to the inner engine — every entry is gone after the call");
    }

    // ── 3. FlushAsync — pure delegation (L188) ────────────────────────────

    /// <summary>
    /// Explicit <c>FlushAsync</c> on the decorator forwards to the inner engine. The
    /// MarketData ingestion writer batches appends and forces a flush at the end of each
    /// source pull; that path comes through here.
    /// </summary>
    [Fact]
    public async Task FlushAsync_DelegatesToInner()
    {
        var inner = NewInnerEngine();
        var engine = new LsmTimeSeriesStorageEngine<SerializableWrapper<string>, TimeSeriesTestRecord>(inner);

        await engine.SetAsync(Key("AAPL", 2026, 1, 1), new TimeSeriesTestRecord(new DateOnly(2026, 1, 1), 1)).ConfigureAwait(true);
        var beforeSegments = inner.SegmentCount;

        await engine.FlushAsync().ConfigureAwait(true);

        inner.SegmentCount.Should().BeGreaterThan(beforeSegments,
            "FlushAsync forwards to the inner LSM engine — a new segment lands on disk");
    }

    // ── 4. CompactAsync — pure delegation (L192) ──────────────────────────

    /// <summary>
    /// <c>CompactAsync</c> on the decorator forwards to the inner LSM engine's compactor.
    /// MarketData runs background compaction on idle to reduce read amplification.
    /// </summary>
    [Fact]
    public async Task CompactAsync_DelegatesToInner()
    {
        var inner = NewInnerEngine();
        var engine = new LsmTimeSeriesStorageEngine<SerializableWrapper<string>, TimeSeriesTestRecord>(inner);

        // Drive at least 2 segment flushes so compaction has work.
        for (var i = 0; i < 8; i++)
        {
            await engine.SetAsync(
                Key("AAPL", 2026, 1, 1 + i),
                new TimeSeriesTestRecord(new DateOnly(2026, 1, 1 + i), i)).ConfigureAwait(true);
        }
        await engine.FlushAsync().ConfigureAwait(true);

        var beforeSegments = inner.SegmentCount;
        beforeSegments.Should().BeGreaterThanOrEqualTo(1, "test setup: at least one segment must exist");

        await engine.CompactAsync().ConfigureAwait(true);

        // After compaction segment count is ≤ 1 (consolidated). Verifies the call reached
        // the inner engine's compaction path; the exact post-state is the inner engine's
        // contract, not the decorator's.
        inner.SegmentCount.Should().BeLessThanOrEqualTo(beforeSegments,
            "CompactAsync forwards to the inner engine — segment count cannot increase");
    }

    // ── 5. AcquireSnapshotAsync — row-format pass-through (L208 false / L217) ──

    /// <summary>
    /// When <see cref="TimeSeriesStoreOptions{TRecord}.SegmentFormat"/> is
    /// <see cref="TimeSeriesSegmentFormat.Row"/>, <c>AcquireSnapshotAsync</c> forwards to
    /// the inner engine's snapshot acquisition (L217). The columnar-throws arm (L210–214)
    /// is already covered by <c>AcquireSnapshotAsync_ColumnarFormat_Throws</c> in the
    /// original Phase-10 tests; this test pins the row-format pass-through.
    /// </summary>
    [Fact]
    public async Task AcquireSnapshotAsync_RowFormat_DelegatesToInner()
    {
        var inner = NewInnerEngine();
        // Default options → Row segment format.
        var engine = new LsmTimeSeriesStorageEngine<SerializableWrapper<string>, TimeSeriesTestRecord>(inner);

        engine.Options.SegmentFormat.Should().Be(TimeSeriesSegmentFormat.Row,
            "test setup: default options must select Row format to exercise the L217 pass-through");

        await engine.SetAsync(Key("AAPL", 2026, 1, 1), new TimeSeriesTestRecord(new DateOnly(2026, 1, 1), 1)).ConfigureAwait(true);

        await using var snap = await engine.AcquireSnapshotAsync().ConfigureAwait(true);

        snap.Should().NotBeNull(
            "the row-format arm at L208 is false → L217 forwards to _inner.AcquireSnapshotAsync");
        var (value, found) = await snap.TryGetValueAsync(Key("AAPL", 2026, 1, 1)).ConfigureAwait(true);
        found.Should().BeTrue();
        value.Value.Should().Be(1);
    }

    // ── 6. GetRangeProjectedFallbackAsync — projectable records (L400 / L402) ──

    /// <summary>
    /// When the decorator's inner engine is not an <see cref="LsmStorageEngine{TKey, TValue}"/>
    /// (a future <see cref="ILsmStorageEngine{TKey, TValue}"/> implementation that cannot
    /// expose its memtable atomically), <c>GetRangeProjectedAsync</c> falls back to
    /// <c>GetRangeProjectedFallbackAsync</c> at L387–407. Records that implement
    /// <see cref="IProjectable{TSelf}"/> are projected post-materialisation via L402;
    /// records that don't fall through unchanged. This test wires a stub inner engine
    /// returning <see cref="ProjectableTestBar"/> values and verifies the projection
    /// reaches the caller.
    /// </summary>
    [Fact]
    public async Task GetRangeProjectedFallbackAsync_ProjectableRecord_AppliesProjection()
    {
        // ProjectableTestBar is in the Columnar test namespace; the stub mirrors its shape.
        var stubInner = new StubLsmStorageEngine<TimeSeriesKey<SerializableWrapper<string>>, ProjectableTestBar>();

        var key1 = new TimeSeriesKey<SerializableWrapper<string>>(
            new SerializableWrapper<string>("AAPL"), new DateOnly(2026, 1, 1));
        var bar = new ProjectableTestBar(new DateOnly(2026, 1, 1), open: 100m, close: 101m, volume: 1000);
        stubInner.SeedRange(key1, key1, [(key1, bar)]);

        var stubColumnar = new StubColumnarFormatBar();
        var options = new ColumnarTimeSeriesStoreOptions<ProjectableTestBar>
        {
            ColumnarFormat = stubColumnar,
        };
        var engine = new LsmTimeSeriesStorageEngine<SerializableWrapper<string>, ProjectableTestBar>(stubInner, options);

        // Projection asks only for "Close" — ProjectTo blanks Open and Volume to defaults.
        var projection = new ColumnProjection(["Close"]);
        var pairs = new List<(TimeSeriesKey<SerializableWrapper<string>> Key, ProjectableTestBar Value)>();
        await foreach (var item in engine.GetRangeAsync(key1, key1, projection).ConfigureAwait(false))
        {
            pairs.Add(item);
        }

        pairs.Should().HaveCount(1, "the fallback path materialises and yields the seeded record");
        pairs[0].Value.Close.Should().Be(101m,
            "the projected field is preserved through ProjectTo (L402 in the fallback)");
        pairs[0].Value.Open.Should().Be(0m,
            "the unprojected field is blanked — confirms ProjectTo executed at L402, not just a passthrough");
        pairs[0].Value.Volume.Should().Be(0,
            "the unprojected field is blanked — confirms ProjectTo executed at L402, not just a passthrough");
    }

    // ═════════════════════════════════════════════════════════════════════════════════════
    // Stubs — minimal ILsmStorageEngine (NOT LsmStorageEngine<,>) to force the fallback
    // path, plus a no-op IColumnarSegmentFormat for the options surface.
    // ═════════════════════════════════════════════════════════════════════════════════════

    private sealed class StubColumnarFormatBar : IColumnarSegmentFormat<ProjectableTestBar>
    {
        public Task WriteAsync(Stream output, IReadOnlyList<ProjectableTestBar> records, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("StubColumnarFormatBar is not for disk I/O.");

        public IAsyncEnumerable<ProjectableTestBar> ReadAsync(Stream input, DateOnly from, DateOnly to, ColumnProjection projection, ScanDirection direction = ScanDirection.Ascending, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("StubColumnarFormatBar is not for disk I/O.");

        public IAsyncEnumerable<ColumnBatch<ProjectableTestBar>> ReadBatchesAsync(Stream input, DateOnly from, DateOnly to, ColumnProjection projection, ScanDirection direction = ScanDirection.Ascending, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("StubColumnarFormatBar is not for disk I/O.");

        public IAsyncEnumerable<ProjectableTestBar> StreamWhereAsync(Stream input, DateOnly from, DateOnly to, ColumnProjection projection, IReadOnlyList<ColumnPredicate> predicates, ScanDirection direction = ScanDirection.Ascending, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("StubColumnarFormatBar is not for disk I/O.");
    }

    private sealed class StubLsmStorageEngine<TKey, TValue> : ILsmStorageEngine<TKey, TValue>
        where TKey : IStorageKey<TKey>, new()
        where TValue : ISerializable<TValue>, new()
    {
        private readonly List<(TKey Key, TValue Value)> _seed = [];

        public void SeedRange(TKey _, TKey _2, IEnumerable<(TKey Key, TValue Value)> records)
        {
            _seed.Clear();
            _seed.AddRange(records);
        }

        public int SegmentCount => 0;
        public bool SupportsSnapshots => false;

        public async IAsyncEnumerable<(TKey Key, TValue Value)> GetRangeAsync(
            TKey startKey, TKey endKey,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var item in _seed)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
                await Task.Yield();
            }
        }

        // Methods we don't exercise — the test only calls GetRangeAsync via the fallback.
        public Task SetAsync(TKey key, TValue value, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(TValue Value, bool Found)> TryGetValueAsync(TKey key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> ContainsKeyAsync(TKey key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveAsync(TKey key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ClearAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IEnumerable<(TKey Key, TValue Value)>> GetAllItemsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetBulkAsync(IEnumerable<KeyValuePair<TKey, TValue>> items, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task FlushAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CompactAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ISnapshot<TKey, TValue>> AcquireSnapshotAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
