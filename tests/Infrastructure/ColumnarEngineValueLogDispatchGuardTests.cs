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
using Boutquin.Storage.Domain.ValueObjects.LsmTree;
using Boutquin.Storage.Domain.ValueObjects.TimeSeries;
using Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar;
using Boutquin.Storage.Domain.ValueObjects.ValueLog;
using Boutquin.Storage.Infrastructure.Tests.TimeSeries.Columnar;
using Boutquin.Storage.Infrastructure.TimeSeries;
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar;

namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// A segment format that cannot store a value-log pointer must never be handed one. An LSM engine
/// over such a format keeps every value inline whatever value-log options it was given — a null
/// <c>ValueLog</c> included, which otherwise resolves to the 1 KiB default threshold — and refuses a
/// value too large to keep inline before it acknowledges the write. A row-format engine keeps its
/// WiscKey dispatch unchanged.
/// </summary>
public sealed class ColumnarEngineValueLogDispatchGuardTests : IDisposable
{
    private const int InlineCeilingBytes = 128 * 1024 * 1024;

    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        $"ColumnarValueLogGuard_{Guid.NewGuid():N}");

    public ColumnarEngineValueLogDispatchGuardTests()
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

    private static SerializableWrapper<string> Ds(string name) => new(name);

    [Theory]
    [InlineData(null)]
    [InlineData(64)]
    [InlineData(1024)]
    public async Task HandWiredColumnarStack_KeepsEveryAcknowledgedRecordAcrossFlushAndReopen(int? valueLogThreshold)
    {
        // null is the hand-wired `ValueLog: null`; the explicit thresholds are WiscKey settings a
        // caller might copy from a row-format stack. None of them may divert a columnar record.
        var valueLog = valueLogThreshold is null
            ? null
            : ValueLogOptions.Default with { ValueLogThresholdBytes = valueLogThreshold.Value };
        var ds = Ds("NOTES");
        var d0 = new DateOnly(2024, 1, 1);
        TickerNoteBar[] bars =
        [
            new() { Date = d0, Ticker = "A", Note = "short" },
            new() { Date = d0.AddDays(1), Ticker = "B", Note = new string('m', 2 * 1024) },
            new() { Date = d0.AddDays(2), Ticker = "C", Note = new string('l', 100 * 1024) },
        ];

        await using (var stack = await OpenHandWiredAsync(valueLog))
        {
            await stack.Store.AppendAsync(ds, bars);
            var flush = async () => await stack.Engine.FlushAsync();
            await flush.Should().NotThrowAsync("every record the stack acknowledged must be flushable");
        }

        await using (var reopened = await OpenHandWiredAsync(valueLog))
        {
            var rows = new List<TickerNoteBar>();
            await foreach (var r in reopened.Store.GetRangeAsync(ds, DateOnly.MinValue, DateOnly.MaxValue, ColumnProjection.All))
            {
                rows.Add(r);
            }

            rows.Should().Equal(bars, "an acknowledged write must survive a flush and a reopen");
        }

        ValueLogBytesOnDisk().Should().Be(0, "a columnar stack must never write value-log data");
    }

    [Fact]
    public async Task HandWiredColumnarStack_RecordAboveTheInlineCeiling_IsRefusedBeforeAcknowledgement()
    {
        // A value-log entry cap far above the inline ceiling leaves the refusal to the engine: the
        // record cannot stay inline and the format cannot store a pointer, so it must not be accepted.
        var valueLog = ValueLogOptions.Default with { MaxEntryBytes = 4L * InlineCeilingBytes };
        var ds = Ds("BIG");
        var d0 = new DateOnly(2024, 1, 1);
        var small = new TickerNoteBar { Date = d0, Ticker = "S", Note = "kept out with its batch" };

        await using (var stack = await OpenHandWiredAsync(valueLog))
        {
            var big = new TickerNoteBar { Date = d0.AddDays(1), Ticker = "X", Note = new string('x', InlineCeilingBytes + 1) };
            var append = async () => await stack.Store.AppendAsync(ds, [small, big]);
            await append.Should().ThrowAsync<ValueTooLargeException>(
                "a record a columnar segment cannot hold inline must be refused before it is acknowledged");

            var after = new TickerNoteBar { Date = d0.AddDays(2), Ticker = "T", Note = "after" };
            await stack.Store.AppendAsync(ds, [after]);
            var flush = async () => await stack.Engine.FlushAsync();
            await flush.Should().NotThrowAsync("the refusal must leave the engine healthy");
        }

        await using (var reopened = await OpenHandWiredAsync(valueLog))
        {
            var rows = new List<TickerNoteBar>();
            await foreach (var r in reopened.Store.GetRangeAsync(ds, DateOnly.MinValue, DateOnly.MaxValue, ColumnProjection.All))
            {
                rows.Add(r);
            }

            rows.Select(r => r.Ticker).Should().Equal(["T"], "the refused batch applies nothing and the later write survives");
        }

        ValueLogBytesOnDisk().Should().Be(0, "the refused record must not reach the value log");
    }

    [Fact]
    public async Task RowFormatEngine_WithDefaultValueLog_StillRoutesALargeValueThroughTheValueLog()
    {
        // Regression floor: the guard is scoped to pointer-incapable formats. An SSTable engine keeps
        // WiscKey key-value separation for a value above the default 1 KiB threshold.
        var options = new LsmStorageEngineOptions<SerializableWrapper<int>, SerializableWrapper<string>>(
            MemTableCapacity: 16,
            SegmentFolder: _folder,
            SegmentPrefix: "seg",
            SegmentFormat: new SstSegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>>(),
            ValueLog: null);
        var large = new string('w', 4 * 1024);

        await using (var engine = await LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>.OpenAsync(options))
        {
            await engine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("inline"));
            await engine.SetAsync(new SerializableWrapper<int>(2), new SerializableWrapper<string>(large));
            await engine.FlushAsync();
        }

        ValueLogBytesOnDisk().Should().BeGreaterThan(large.Length, "the large value lives in the value log");

        await using (var reopened = await LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>.OpenAsync(options))
        {
            (await reopened.TryGetValueAsync(new SerializableWrapper<int>(1))).Value.Value.Should().Be("inline");
            (await reopened.TryGetValueAsync(new SerializableWrapper<int>(2))).Value.Value.Should().Be(large);
        }
    }

    private long ValueLogBytesOnDisk()
    {
        var dir = Path.Combine(_folder, "valuelog");
        return Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "valuelog_*.log", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length)
            : 0;
    }

    private async Task<HandWiredStack> OpenHandWiredAsync(ValueLogOptions? valueLog)
    {
        // The guide's five-step manual wiring, with the ValueLog argument under test.
        var innerFormat = new CompressedColumnarSegmentFormat<TickerNoteBar>();
        var segmentFormat = new ColumnarSegmentFormat<SerializableWrapper<string>, TickerNoteBar>(innerFormat);
        var lsmOptions = new LsmStorageEngineOptions<TimeSeriesKey<SerializableWrapper<string>>, TickerNoteBar>(
            MemTableCapacity: 10_000,
            SegmentFolder: _folder,
            SegmentPrefix: "bars",
            SegmentFormat: segmentFormat,
            ValueLog: valueLog);
        var engine = await LsmStorageEngine<TimeSeriesKey<SerializableWrapper<string>>, TickerNoteBar>
            .OpenAsync(lsmOptions);
        var tsEngine = new LsmTimeSeriesStorageEngine<SerializableWrapper<string>, TickerNoteBar>(
            engine, new ColumnarTimeSeriesStoreOptions<TickerNoteBar>(innerFormat));
        var store = new LsmTimeSeriesStore<SerializableWrapper<string>, TickerNoteBar>(
            tsEngine,
            new InMemoryDatasetCoverageIndex<SerializableWrapper<string>>());
        return new HandWiredStack(engine, store);
    }

    private sealed class HandWiredStack(
        LsmStorageEngine<TimeSeriesKey<SerializableWrapper<string>>, TickerNoteBar> engine,
        LsmTimeSeriesStore<SerializableWrapper<string>, TickerNoteBar> store) : IAsyncDisposable
    {
        public LsmStorageEngine<TimeSeriesKey<SerializableWrapper<string>>, TickerNoteBar> Engine { get; } = engine;

        public LsmTimeSeriesStore<SerializableWrapper<string>, TickerNoteBar> Store { get; } = store;

        public ValueTask DisposeAsync() => Engine.DisposeAsync();
    }
}
