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
#nullable disable

using System.Reflection;
using Boutquin.Storage.Domain.ValueObjects.TimeSeries;
using Boutquin.Storage.Infrastructure.Tests.TimeSeries.Columnar;
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar;

namespace Boutquin.Storage.Infrastructure.Tests.TimeSeries;

// AC-2.8 (R2-G) — a columnar segment recovered from disk must carry its real flush watermark,
// not the data-derived 0 the reader used to report. Without it, the C-3 max-seqno stamp
// collapses to a no-op for recovered columnar segments and a re-insert that out-ranked an
// earlier tombstone before restart can be suppressed after a no-WAL reopen + compaction
// (a restart-only data-loss edge). The footer now persists a segment-level max-seqno; recovery
// reads it unconditionally. A row-format control runs the same sequence.
public sealed class ColumnarRecoverySeqnoParityTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"colrecseq-{Guid.NewGuid():N}");

    public ColumnarRecoverySeqnoParityTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
        }
    }

    private static MarketDataBar Bar(DateOnly date, decimal close) => new()
    {
        Date = date,
        Open = close - 1m,
        High = close + 1m,
        Low = close - 2m,
        Close = close,
        Volume = 1000,
        Vwap = close,
        TradeCount = 1,
        Dividend = 0m,
        SplitRatio = 1m,
        AdjustedClose = close,
        SourceFlags = 0,
    };

    private static TimeSeriesKey<SerializableWrapper<string>> Key(string ds, DateOnly d)
        => new(new SerializableWrapper<string>(ds), d);

    private static LsmStorageEngine<TimeSeriesKey<SerializableWrapper<string>>, MarketDataBar>
        BuildColumnarInner(string folder, int memTableCapacity = 8)
    {
        var innerColumnar = new CompressedColumnarSegmentFormat<MarketDataBar>();
        var segmentFormat = new ColumnarSegmentFormat<SerializableWrapper<string>, MarketDataBar>(innerColumnar);
        return LsmEngineFactory.Build<TimeSeriesKey<SerializableWrapper<string>>, MarketDataBar>(
            memTableCapacity: memTableCapacity,
            segmentFolder: folder,
            segmentPrefix: "col",
            segmentFormat: segmentFormat);
    }

    private static LsmStorageEngine<TimeSeriesKey<SerializableWrapper<string>>, MarketDataBar>
        BuildRowInner(string folder, int memTableCapacity = 8)
        => LsmEngineFactory.Build<TimeSeriesKey<SerializableWrapper<string>>, MarketDataBar>(
            memTableCapacity: memTableCapacity,
            segmentFolder: folder,
            segmentPrefix: "row");

    private static IReadOnlyList<ulong> SegmentMaxSeqnos(
        LsmStorageEngine<TimeSeriesKey<SerializableWrapper<string>>, MarketDataBar> engine)
    {
        var field = typeof(LsmStorageEngine<TimeSeriesKey<SerializableWrapper<string>>, MarketDataBar>)
            .GetField("_segmentMaxSeqnos", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return new List<ulong>((List<ulong>)field.GetValue(engine)!);
    }

    private static async Task<(MarketDataBar Value, bool Found)> TryGetAsync(
        LsmStorageEngine<TimeSeriesKey<SerializableWrapper<string>>, MarketDataBar> engine,
        TimeSeriesKey<SerializableWrapper<string>> key)
        => await engine.TryGetValueAsync(key);

    [Theory]
    [InlineData(true)]   // columnar
    [InlineData(false)]  // row control
    public async Task DeleteThenReinsert_RecoveredAcrossCompaction_ResolvesToReinsertedValue(bool columnar)
    {
        var folder = Path.Combine(_root, $"{(columnar ? "col" : "row")}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);

        var d = new DateOnly(2024, 1, 1);

        // ── Pre-restart: insert -> flush -> delete -> flush -> reinsert(999) -> flush ──
        {
            var inner = columnar ? BuildColumnarInner(folder) : BuildRowInner(folder);
            await inner.SetAsync(Key("AAPL", d), Bar(d, 100m));
            await inner.FlushAsync();
            await inner.RemoveAsync(Key("AAPL", d));
            await inner.FlushAsync();
            await inner.SetAsync(Key("AAPL", d), Bar(d, 999m));
            await inner.FlushAsync();

            // Pre-restart compaction resolves to the re-inserted value.
            await inner.CompactAsync();
            var preRestart = await TryGetAsync(inner, Key("AAPL", d));
            preRestart.Found.Should().BeTrue("pre-restart the re-insert must survive the tombstone");
            preRestart.Value.Close.Should().Be(999m);
            await inner.DisposeAsync();
        }

        // ── No-WAL reopen over the same folder + post-recovery compaction ──
        var reopened = columnar ? BuildColumnarInner(folder) : BuildRowInner(folder);
        await using var _ = reopened;

        // The recovered segment(s) must carry the real flush watermark, not 0.
        var maxSeqnos = SegmentMaxSeqnos(reopened);
        maxSeqnos.Should().NotBeEmpty();
        maxSeqnos.Should().Contain(s => s > 0UL,
            "the recovered segment's _segmentMaxSeqnos must be the real flush watermark, not the reader's data-derived 0");

        await reopened.CompactAsync();

        var postRestart = await TryGetAsync(reopened, Key("AAPL", d));
        postRestart.Found.Should().BeTrue(
            "post-recovery compaction must resolve to the re-inserted value, identical to pre-restart");
        postRestart.Value.Close.Should().Be(999m);
    }
}
