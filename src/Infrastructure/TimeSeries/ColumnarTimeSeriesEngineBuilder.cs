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
using Boutquin.Storage.Domain.ValueObjects.LsmTree;
using Boutquin.Storage.Domain.ValueObjects.TimeSeries;
using Boutquin.Storage.Domain.ValueObjects.ValueLog;
using Boutquin.Storage.Infrastructure.LsmTree;
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar;

namespace Boutquin.Storage.Infrastructure.TimeSeries;

/// <summary>
/// Assembles a columnar time-series stack — LSM engine + time-series engine + store —
/// from a segment folder and the columnar tuning knobs, preventing the instance-sync
/// mistakes that the manual five-step wiring pattern permits.
/// </summary>
public static class ColumnarTimeSeriesEngineBuilder
{
    // A columnar segment stores records inline only: the envelope refuses a value-log pointer at
    // flush, after the write was acknowledged, which faults the engine and loses the write. A null
    // ValueLog does not switch dispatch off — the engine resolves it to ValueLogOptions.Default,
    // whose 1 KiB threshold diverts ordinary records — so the stack pins dispatch explicitly. The
    // threshold sits at the largest value ValueLogOptions accepts (128 MiB), keeping every record up
    // to it inline; the one-byte entry cap makes the value log refuse anything larger with
    // ValueTooLargeException before the append is acknowledged, so no pointer is ever produced.
    private static readonly ValueLogOptions s_inlineOnlyValueLog = ValueLogOptions.Default with
    {
        ValueLogThresholdBytes = 128 * 1024 * 1024,
        MaxEntryBytes = 1,
    };

    /// <summary>
    /// Builds a fully-wired columnar time-series store for <typeparamref name="TRecord"/>.
    /// </summary>
    /// <typeparam name="TDatasetKey">
    /// The dataset partition key. Must be serializable and comparable (required by the
    /// underlying LSM key type <c>TimeSeriesKey&lt;TDatasetKey&gt;</c>).
    /// </typeparam>
    /// <typeparam name="TRecord">
    /// The dated record type. Must be annotated with <c>[ColumnarStorable]</c> — the source
    /// generator emits the <see cref="IColumnarRehydratable{TSelf}"/> implementation required
    /// by this method's constraint.
    /// </typeparam>
    /// <param name="segmentFolder">Directory where segment files are written.</param>
    /// <param name="segmentPrefix">
    /// Filename prefix for segment files (e.g. <c>"ts"</c> → <c>ts_000000.bsct</c>).
    /// </param>
    /// <param name="memTableCapacity">
    /// Number of records the in-memory MemTable holds before an automatic flush.
    /// Higher values reduce flush frequency at the cost of memory.
    /// </param>
    /// <param name="rowGroupMaxRows">
    /// Maximum rows per columnar row group. Caps peak decode-time memory; 2¹⁷ rows at 10
    /// fields × 8 bytes ≈ 10 MiB fits L3 cache on any modern CPU. Must not exceed 2²⁶
    /// (67 108 864), the per-row-group ceiling every columnar read enforces.
    /// </param>
    /// <param name="rowGroupMaxDateRange">
    /// Maximum calendar span per columnar row group. Controls skip-index granularity.
    /// Default 365 days groups daily-bar data into ~252-row groups.
    /// </param>
    /// <param name="sortSpec">
    /// Column sort order inside each dataset block. Defaults to
    /// <see cref="ColumnSortSpec.DateAscending"/> — matches the flush order imposed by
    /// <c>TimeSeriesKey</c>, so no re-sort is needed for daily-bar workloads.
    /// </param>
    /// <param name="coverageIndex">
    /// Coverage index for O(1) gap/coverage queries. Defaults to
    /// <see cref="InMemoryDatasetCoverageIndex{TDatasetKey}"/> (in-process, non-persistent).
    /// Pass a <c>FileDatasetCoverageIndex</c> for durability across restarts.
    /// </param>
    /// <param name="compactionStrategy">
    /// Pluggable compaction strategy. Defaults to <see langword="null"/>; the engine then
    /// uses <c>SizeTieredCompactionStrategy</c> (Cassandra-style; DDIA Ch. 4 p. 124).
    /// </param>
    /// <param name="writeAheadLog">
    /// Optional WAL for crash recovery. When supplied, every write is persisted before
    /// the MemTable is updated; the MemTable is reconstructed from WAL replay on restart.
    /// </param>
    /// <returns>
    /// A <see cref="ColumnarTimeSeriesStack{TDatasetKey,TRecord}"/> whose <c>Store</c>
    /// property is the public API and whose <c>InnerEngine</c> exposes flush/compact.
    /// The caller is responsible for disposing the stack.
    /// </returns>
    public static async Task<ColumnarTimeSeriesStack<TDatasetKey, TRecord>> BuildAsync<TDatasetKey, TRecord>(
        string segmentFolder,
        string segmentPrefix = "ts",
        int memTableCapacity = 10_000,
        int rowGroupMaxRows = 1 << 17,
        TimeSpan? rowGroupMaxDateRange = null,
        ColumnSortSpec? sortSpec = null,
        IDatasetCoverageIndex<TDatasetKey>? coverageIndex = null,
        ICompactionStrategy<TimeSeriesKey<TDatasetKey>>? compactionStrategy = null,
        IWriteAheadLog<TimeSeriesKey<TDatasetKey>, TRecord>? writeAheadLog = null,
        CancellationToken cancellationToken = default)
        where TDatasetKey : ISerializable<TDatasetKey>, IComparable<TDatasetKey>, new()
        where TRecord : ISerializable<TRecord>, IHasDate, IColumnarRehydratable<TRecord>, new()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(segmentFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(segmentPrefix);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(memTableCapacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rowGroupMaxRows);

        var dateRange = rowGroupMaxDateRange ?? TimeSpan.FromDays(365);
        if (dateRange <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(rowGroupMaxDateRange),
                "Row-group date range must be positive.");
        }

        // — format chain (instance-sync enforced internally) ——————————————
        var innerFormat = new CompressedColumnarSegmentFormat<TRecord>(
            rowGroupMaxRows, dateRange);
        // The format chain is the single owner of row-group sizing and sort order: the engine
        // writes with these formats, and the time-series options deliberately carry no copy of
        // these knobs (a copy would advertise an effect the engine never applies).
        var resolvedSortSpec = sortSpec ?? ColumnSortSpec.DateAscending;
        var envelopeFormat = new ColumnarSegmentFormat<TDatasetKey, TRecord>(innerFormat, resolvedSortSpec);

        // — LSM engine ——————————————————————————————————————————————————————
        var lsmOptions = new LsmStorageEngineOptions<TimeSeriesKey<TDatasetKey>, TRecord>(
            memTableCapacity,
            segmentFolder,
            segmentPrefix,
            envelopeFormat,
            writeAheadLog,
            compactionStrategy,
            ValueLog: s_inlineOnlyValueLog);
        var innerEngine = await LsmStorageEngine<TimeSeriesKey<TDatasetKey>, TRecord>
            .OpenAsync(lsmOptions, cancellationToken).ConfigureAwait(false);

        // — time-series engine (options carry innerFormat, not envelopeFormat) ——
        var tsOptions = new ColumnarTimeSeriesStoreOptions<TRecord>
        {
            ColumnarFormat = innerFormat,
        };
        var tsEngine = new LsmTimeSeriesStorageEngine<TDatasetKey, TRecord>(
            innerEngine, tsOptions);

        // — store —————————————————————————————————————————————————————————
        var store = new LsmTimeSeriesStore<TDatasetKey, TRecord>(
            tsEngine,
            coverageIndex ?? new InMemoryDatasetCoverageIndex<TDatasetKey>());

        return new ColumnarTimeSeriesStack<TDatasetKey, TRecord>(store, innerEngine);
    }
}
