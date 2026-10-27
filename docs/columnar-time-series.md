# Columnar Time-Series Storage

> **Prerequisite reading:** [`ITimeSeriesStore<TDatasetKey, TRecord>`](../README.md#time-series-storage-ddia-2e-ch-3-4-7) and the row-oriented [`LsmStorageEngine`](../README.md#lsm-storage-engine). This guide assumes familiarity with the row format; the columnar path is an **opt-in** alternative segment layout for the same LSM machinery.

## When to use columnar vs row

| Workload | Choose |
|---|---|
| Mixed reads/writes, full-record projections ("load this bar by (ticker, date)") | **Row** (default) |
| Bulk-analytical reads projecting a few fields across many records — backtest `(Date, AdjClose, Volume)` over 50 tickers × 10 years | **Columnar** |
| Cold-start deserialisation time dominates query compute | **Columnar** |
| Working-set memory is the binding constraint | **Columnar** |
| Per-key upserts on small windows | **Row** — columnar writes one entire dataset block per flush; small-window upsert patterns produce many tiny columnar blocks that compact poorly |

The DDIA 2nd ed (Ch. 4, pp 136–144) argument for columnar is exactly this: analytical workloads that read ≪ N of the N columns in a record pay an N× deserialisation cost in row format. Columnar pays that cost only for the columns actually projected. Boutquin.Storage threads `ColumnProjection` from the public `ITimeSeriesStore.GetRangeAsync` surface through the LSM merge and into `IProjectingSegmentReader<TKey, TValue, ColumnProjection>`, so unprojected column byte ranges are skipped on disk as well as during decode. The shipped benefits are **per-dataset block locality, CRC-guarded skip indexes, public range projection, and compression when the record is `[ColumnarStorable]`-annotated**.

## File layout

Every `LsmStorageEngine` flush writes one segment file. With `ColumnarSegmentFormat`, that file is:

```
┌──────────────────────────────────────────────────────────────────┐
│ Header                                                           │
│   magic 'B','S','L','C' (4 bytes)                                │
│   format version (uint32 LE)                                     │
├──────────────────────────────────────────────────────────────────┤
│ Dataset block 1     ← bytes produced by IColumnarSegmentFormat   │
│   (its own header + per-column pages + its own footer)           │
├──────────────────────────────────────────────────────────────────┤
│ Dataset block 2                                                  │
│   ...                                                            │
├──────────────────────────────────────────────────────────────────┤
│ Dataset block N                                                  │
├──────────────────────────────────────────────────────────────────┤
│ Footer (CRC32C-guarded)                                          │
│   datasetCount (uint32)        ← number of block entries;      │
│                                  a dataset may have several      │
│   maxSequenceNumber (uint64)   ← segment-level; highest LSM     │
│                                  seqno among entries + tombstones │
│                                  at seal time. Mirrors the SST   │
│                                  footer's max_sequence_number;   │
│                                  recovery seeds the engine's     │
│                                  per-segment watermark from it.  │
│   for each block (sorted by dataset key, then date):             │
│     keyByteLength (uint32)                                       │
│     keyBytes                    ← TDatasetKey.Serialize output   │
│     blockByteOffset (int64)                                      │
│     blockByteLength (int64)                                      │
│     rowCount (int32)                                             │
│     minDateDayNumber (int32)                                     │
│     maxDateDayNumber (int32)                                     │
│   tombstoneCount (uint32 LE)                                    │
│     each: keyByteLength (uint32 LE), serialized TimeSeriesKey,    │
│           deletionSequenceNumber (uint64 LE)                     │
│   optional enabled Bloom descriptor (BLM1, kind 0 or 1)          │
│   footerCRC32C (Castagnoli 0x1EDC6F41)                           │
├──────────────────────────────────────────────────────────────────┤
│ Trailer (last 8 bytes)                                           │
│   footerLengthBytes (uint32)                                     │
│   magic 'C','L','S','B'         ← reversed 'B','S','L','C'       │
└──────────────────────────────────────────────────────────────────┘
```

Both native and custom persisted Bloom descriptors are described in [segment Bloom filters](segment-bloom-filters.md). They follow the tombstone section and are covered by the same footer CRC.

The trailer magic is validated **before** `footerLengthBytes` is trusted as a seek offset — a non-Boutquin file surfaces `InvalidSegmentException` without the reader ever seeking into garbage. Every error condition raises a typed exception: `TruncatedSegmentException`, `CorruptSegmentException`, `UnsupportedFormatVersionException`, `InvalidSegmentException`.

## Constructing a columnar time-series store

Assume you have a `[ColumnarStorable]`-annotated dated record:

```csharp
using Boutquin.Storage.Domain.Attributes;
using Boutquin.Storage.Domain.Interfaces.TimeSeries;

[ColumnarStorable]
[StorageSerializable]
public partial record struct Bar(
    [property: Tag(1)] DateOnly Date,
    [property: Tag(2)] string Ticker,
    [property: Tag(3)] decimal Close,
    [property: Tag(4)] long Volume,
    [property: Tag(5)] double Vwap,
    [property: Tag(6)] bool IsAdjusted) : IHasDate;
```

### Recommended path — `ColumnarTimeSeriesEngineBuilder.BuildAsync`

For almost every consumer, use the single-call async factory. It assembles the whole
stack — inner columnar format → outer envelope format → LSM engine → time-series
decorator → store — and enforces the instance-sync invariant that the manual wiring below
leaves open (the time-series options must carry the *same* `CompressedColumnarSegmentFormat`
instance the LSM engine writes through). It returns a `ColumnarTimeSeriesStack` whose
`.Store` is the public `ITimeSeriesStore` surface and whose `.InnerEngine` exposes flush
and compaction.

```csharp
using Boutquin.Storage.Domain.Helpers;
using Boutquin.Storage.Infrastructure.TimeSeries;

// TDatasetKey here is SerializableWrapper<string> (ticker). BuildAsync runs async
// startup (LSM OpenAsync: WAL replay, value-log open) before returning.
using var stack = await ColumnarTimeSeriesEngineBuilder.BuildAsync<SerializableWrapper<string>, Bar>(
    segmentFolder: "data/bars",
    segmentPrefix: "bars",
    memTableCapacity: 10_000);

ITimeSeriesStore<SerializableWrapper<string>, Bar> store = stack.Store;
// stack.InnerEngine.FlushAsync() / .CompactAsync() when you need to force a flush/compact.
// Dispose the stack when done.
```

### Manual wiring (when you need to inject a custom component)

Reach for manual wiring only when you need to supply a component the builder doesn't take
(e.g. a custom coverage index already constructed elsewhere, or a custom sort spec the
builder doesn't expose). Build the stack bottom-up: **inner columnar format → outer segment
format → LSM engine → time-series decorator → store**.

```csharp
using Boutquin.Storage.Domain.Enums;
using Boutquin.Storage.Domain.Helpers;
using Boutquin.Storage.Domain.ValueObjects.LsmTree;
using Boutquin.Storage.Domain.ValueObjects.TimeSeries;
using Boutquin.Storage.Domain.ValueObjects.ValueLog;
using Boutquin.Storage.Infrastructure.LsmTree;
using Boutquin.Storage.Infrastructure.TimeSeries;
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar;

// 1. Per-dataset columnar format. Production path: CompressedColumnarSegmentFormat<T>,
//    which dispatches each column to its codec via the source-generated plan.
var innerFormat = new CompressedColumnarSegmentFormat<Bar>();

// 2. LSM-segment-level envelope. Groups LSM entries by dataset, writes one inner block
//    per dataset, emits the outer dataset-index footer. Segments carry the .bsct extension.
var segmentFormat = new ColumnarSegmentFormat<SerializableWrapper<string>, Bar>(innerFormat);

// 3. Underlying LSM engine. Configuration is threaded through LsmStorageEngineOptions;
//    OpenAsync runs async startup (WAL replay, value-log open).
//    ColumnarSegmentFormat advertises no value-log pointer support, so the engine
//    keeps values inline regardless of ValueLog options and rejects serialized
//    values larger than 128 MiB before acknowledgment. The explicit settings below
//    also match the builder's inline-only configuration; the engine enforces the
//    capability rule independently of these settings.
var inlineOnlyValueLog = ValueLogOptions.Default with
{
    ValueLogThresholdBytes = 128 * 1024 * 1024,
    MaxEntryBytes = 1,
};
var lsmOptions = new LsmStorageEngineOptions<TimeSeriesKey<SerializableWrapper<string>>, Bar>(
    MemTableCapacity: 10_000,
    SegmentFolder: "data/bars",
    SegmentPrefix: "bars",
    SegmentFormat: segmentFormat,
    ValueLog: inlineOnlyValueLog);
using var engine = await LsmStorageEngine<TimeSeriesKey<SerializableWrapper<string>>, Bar>
    .OpenAsync(lsmOptions);

// 4. Time-series decorator. Carries the options so downstream consumers can inspect the
//    format — and MUST carry the same innerFormat instance the engine writes through.
//    The columnar path uses ColumnarTimeSeriesStoreOptions<TRecord> — a row-safe base
//    TimeSeriesStoreOptions<TRecord> carries no ColumnarFormat member; the derived
//    columnar options type adds it and requires TRecord : IColumnarRehydratable<TRecord>
//    (auto-emitted by [ColumnarStorable]). Its SegmentFormat is fixed to Columnar.
var options = new ColumnarTimeSeriesStoreOptions<Bar>(innerFormat);
var tsEngine = new LsmTimeSeriesStorageEngine<SerializableWrapper<string>, Bar>(engine, options);

// 5. Store facade with coverage index.
var store = new LsmTimeSeriesStore<SerializableWrapper<string>, Bar>(
    tsEngine,
    new InMemoryDatasetCoverageIndex<SerializableWrapper<string>>());
```

### Read/write usage is identical to the row path

```csharp
var aapl = new SerializableWrapper<string>("AAPL");
await store.AppendAsync(aapl, bars);

await foreach (var bar in store.GetRangeAsync(aapl, from, to, ColumnProjection.All))
{
    // Process bar.
}

var coverage = await store.GetCoverageAsync(aapl);
// coverage.Value.From / .To report the earliest / latest date on disk for this ticker.

// Recency-biased tail read: the newest 200 records, newest-first (date DESCENDING),
// as of a cutoff date. GetTailAsync walks back from the newest covered date in
// exponentially widening, disjoint windows, so date zone maps prune all but the tail's
// row groups — the right primitive for latest-close, SMA-200, 52-week-high, and as-of
// ("newest record ≤ d") queries where "N trading days" can't be a calendar offset.
await foreach (var bar in store.GetTailAsync(aapl, count: 200, onOrBefore: asOfDate))
{
    // bars arrive newest-first
}
```

### Column projection

`GetRangeAsync` carries an optional `ColumnProjection? projection = null` parameter.
Leaving it `null` (or passing `ColumnProjection.All`) reads every column the segment
declares. Narrow the projection when you need only a subset of each record's fields:

```csharp
var closeOnly = new ColumnProjection(new[] { "Close", "Volume" });
await foreach (var bar in store.GetRangeAsync(aapl, from, to, closeOnly))
{
    // bar.Close and bar.Volume are populated.
    // bar.Date, bar.Ticker, bar.Vwap and bar.IsAdjusted are default(TField).
}
```

**Columnar vs. row segments.** The projection reaches its maximum effect on
columnar (`.bsct`) segments: the per-column footer offset map lets the reader
seek past unprojected column byte ranges entirely — no IO, no decode. Row
(`.sst`) segments materialise the full record regardless, so passing a non-All
projection to a row-format engine is a no-op (the parameter is accepted for
call-site symmetry but ignored).

**Memtable-segment uniformity.** Per DDIA 2e Ch. 4 p. 141, the engine hides
the memtable-vs-segment distinction from the caller. A record that was just
written but not yet flushed exposes the same projection shape as a record
read back from a columnar segment — unprojected fields are `default(TField)`
in both. This uniformity relies on the record type implementing
`IProjectable<TSelf>`. The source generator emits `IProjectable<TSelf>` and its
`ProjectTo` implementation automatically for every `[StorageSerializable]` /
`[ColumnarStorable]`-annotated record, so the uniformity holds out of the box;
a record that hand-lists the interface in its base list supersedes the emission.
Records that implement neither flow through the memtable unprojected.

**Missing-column behaviour.** `ColumnProjection.AllowMissing = false` (the
default, strict) throws `ColumnNotFoundException` when a projected field name
is not declared on the record type or the segment's footer. Opt into
`AllowMissing = true` only when a caller knowingly spans schema generations
written before a field was added — the missing field is then left at
`default(TField)` rather than raising.

**The date column is always read.** Range reads filter rows by date, so
`CompressedColumnarSegmentFormat<TRecord>.ReadAsync` and `StreamWhereAsync` decode the `Date`
column even when the projection omits it, and the records they return carry their dates. The
engine's projected `GetRangeAsync` also decodes `Date` to filter, then applies the caller's
projection to every record, so `Date` is clipped to `default` like any other omitted column. The
same holds for `ITimeSeriesStore.GetRangeAsync` and `GetTailAsync` on a columnar `LsmTimeSeriesStore`
and on `InMemoryTimeSeriesStore` (a row-format engine ignores the projection and returns whole
records): include `Date` in the projection (for example `new ColumnProjection(["Close", "Date"])`)
to receive each record's date.

**Footer-driven decoding.** Each column's footer entry records the codec that wrote it. Readers
decode with that codec, not with the reading record's current `[ColumnEncoding]` annotation, so
changing an annotation (or a generator default) does not misread segments already on disk. The
record's property type must still match the footer's declared field type; a mismatch raises
`CorruptSegmentException`.

`AppendAsync` → `FlushAsync` writes a `.bsct` file. `CompactAsync` merges multiple `.bsct` files into one (still `.bsct`). Engine startup recovery enumerates the active format's extension and then requires the `CURRENT` manifest to describe the authoritative segment set. Row (`.sst`) and columnar (`.bsct`) files therefore belong in separate engine directories; changing formats for an existing directory is an explicit export/re-ingest operation, not an automatic migration or dual-read path.

## Tuning row-group sizing

Row groups **within** each dataset block are planned by the inner `CompressedColumnarSegmentFormat<TRecord>` from its two constructor parameters. `ColumnarTimeSeriesEngineBuilder.BuildAsync` exposes the same two parameters and passes them to the inner format it constructs; with manual wiring, pass them to the `CompressedColumnarSegmentFormat<TRecord>` constructor:

| Parameter | Default | When to change |
|---|---|---|
| `rowGroupMaxDateRange` | 365 days | Reduce to 91 days (quarterly) if sub-year queries dominate the workload. The default is skewed toward yearly-report granularity. |
| `rowGroupMaxRows` | 131,072 (2¹⁷) | Reduce for tick-data pipelines where per-group decode cost is memory-bound; the default targets L3-cache fit (10 fields × 8 bytes × 131,072 ≈ 10 MiB). Must not exceed 67,108,864 (2²⁶), the per-row-group ceiling every columnar read enforces; the constructor (and therefore the builder) throws `ArgumentOutOfRangeException` above it. |

Defaults are chosen so daily-bar and hourly-bar workloads never hit the row ceiling: 131,072 rows is roughly 520 years of daily bars, and 365 days bounds the calendar span per group for skip-index granularity. Both ceilings are hit whichever comes first.

A segment footer describes at most 65,535 row groups per dataset block. When the two knobs would split one block into more groups than that — a very small `rowGroupMaxRows`, or a sub-day `rowGroupMaxDateRange` (which means one date per group) over a long history — the writer widens that block's groups evenly instead, so each holds at least `ceil(rows / 65,535)` rows. The knobs therefore stay upper bounds only for blocks the footer can describe as configured; every acknowledged record is still flushed.

A dataset block is encoded in memory, and a whole-block read loads it into one byte array; both are capped near 2 GiB. Write admission bounds each record (128 MiB of serialized bytes) but not their sum, so `ColumnarSegmentFormat` cuts one dataset's records into blocks of consecutive dates holding at most 256 MiB of serialized records each (a single larger record gets a block of its own). A large dataset therefore occupies several adjacent footer entries with ascending, non-overlapping date spans, and flushes and compactions of it still succeed.

`CompressedColumnarSegmentFormat<TRecord>` checks its `ColumnarFieldPlan` when it is constructed: an entry whose encoding has no codec for the property's type (for example `DeltaVlq` on a `decimal`) throws `NotSupportedException` there, before any write is accepted. The `[ColumnarStorable]` generator applies the same pairing rules at compile time; the constructor check covers hand-written plans.

These constructor parameters (and the `sortSpec` parameter of `ColumnarSegmentFormat<TDatasetKey, TRecord>`) are the only place row-group sizing and sort order are configured. `TimeSeriesStoreOptions<TRecord>` and `ColumnarTimeSeriesStoreOptions<TRecord>` carry no row-group or sort-order members. The sort spec is resolved against `TRecord` when `ColumnarSegmentFormat` is constructed (and therefore when the builder runs): a column it cannot sort by (not a public property of the record, a `Nullable<T>`, or not `IComparable`) throws there, naming the column, before any write is accepted.

```csharp
// Quarterly row groups of at most 32,768 rows, with manual wiring.
var innerFormat = new CompressedColumnarSegmentFormat<Bar>(
    rowGroupMaxRows: 1 << 15,
    rowGroupMaxDateRange: TimeSpan.FromDays(91));
```

## Caller responsibilities

- Dataset-key serialization must preserve comparison identity across a complete deserialize and reproduce identical bytes when serialized again. Admission rejects a lossy encoding with `ArgumentException` before acknowledging a write, including encodings that leave unread bytes. Lookup and coverage paths use the same validation, so an invalid key cannot alias a valid neighbor. Comparer-equal keys with different, stable encodings remain distinct datasets; validation never canonicalizes them into one dataset.
- The LSM engine passes the `ColumnarSegmentFormat` sorted entries. `TimeSeriesKey<TDatasetKey>` orders dataset-first — by the dataset comparer, then by the dataset's serialized bytes, then by date — so records for the same dataset arrive contiguously, including when two datasets compare equal but serialize differently. The envelope relies on this for per-dataset block grouping: a block holds one dataset under that identity (comparer-equal and byte-identical, `TimeSeriesKey<TDatasetKey>.CompareDatasets`), and point lookups and range reads match blocks by the same relation, so every row is read back under its own dataset bytes. `ColumnarSegmentFormat.WriteAsync` enforces it: an entry whose key is not strictly greater than its predecessor (out of order, or a duplicate key) is rejected with `ArgumentException` before any file is created, as the SSTable writer does.
- `LsmTimeSeriesStorageEngine`'s options field carries the `ColumnarFormat`. When the inner engine is an `LsmStorageEngine`, the decorator refuses columnar options whose `ColumnarFormat` is not the format that engine's `ColumnarSegmentFormat` wraps. `ColumnarTimeSeriesStoreOptions<TRecord>.SegmentFormat` is always `Columnar`: setting it to `Row` through an object initializer or a `with` expression throws `ArgumentException`, so the options cannot describe a format the engine never consults. The type-safe construction pattern is the canonical one (see the example above).
- `ColumnarSegmentFormat` is safe under the engine's existing `SemaphoreSlim` — no extra locking required from callers.

## Recovery and evolution

- Startup recovery replays any `.bsct` files in the engine's segment folder. Files with mismatched magic are rejected before being used as seek offsets.
- Segment-format version is a monotonic `uint` in the header. Future format bumps are read-rejected by prior versions via `UnsupportedFormatVersionException`, forcing an explicit upgrade rather than silent data loss.
- Per-column codec versions live in the inner format's footer (per the shipped `IColumnarSegmentFormat<T>` contract). Unknown codec versions surface as `UnsupportedEncodingException` with enough context to identify which codec and which minimum reader version is required.

## Phase E additions — zone maps, bitmap indexes, and row-group CRCs

As of v2.0.0 the columnar footer carries three additive extension sections. All three are
emitted automatically by `CompressedColumnarSegmentFormat<TRecord>.WriteAsync` and exposed
through dedicated reader methods; no format-version bump is required — a reader that does
not recognise an extension kind skips it via the length prefix and continues.

### Zone maps (E1)

Per-`(row group, column)` `ColumnRowGroupStats(MinValue, MaxValue, NullCount, DistinctCount)`
lets a predicate layer skip row groups whose `[min, max]` does not overlap a range query
without touching any column bytes (DDIA 2e Ch. 4 pp 139–141). Min/max are encoded
canonically per the column's CLR type (little-endian fixed-width numerics, 16-byte
`decimal.GetBits` LE, 4-byte `DateOnly` day-number, UTF-8 truncated to 256 bytes for
`string`). Expose via:

```csharp
var format = new CompressedColumnarSegmentFormat<Bar>();
await format.WriteAsync(stream, bars);
stream.Position = 0;
var zoneMaps = format.ReadZoneMaps(stream); // IReadOnlyList<IReadOnlyList<ColumnRowGroupStats>>?
```

The outer list is indexed by row-group index, the inner list by column index in footer
order. `null` is returned for segments written without zone maps.

### Bitmap indexes (E2)

Annotate a low-cardinality property with `[BitmapIndex]` to opt it into footer-level
bitmap-index emission. Each distinct observed value is stored with a `BitmapRleCodec`-encoded
presence bitmap spanning the whole segment. DDIA Fig 4-9 bitmap-AND across multiple indexed
columns becomes one bitmap per predicate plus a boolean-AND:

```csharp
[ColumnarStorable]
public partial record IndexedBar : IHasDate
{
    public DateOnly Date { get; init; }

    [BitmapIndex]
    public string Ticker { get; init; } = "";

    [BitmapIndex]
    public bool IsAdjusted { get; init; }

    public double Vwap { get; init; }

    public IndexedBar() { }
}
```

Read back via `format.ReadBitmapIndexes(stream)` which returns
`IReadOnlyList<BitmapIndexEntry>?` — one entry per annotated column, each carrying a list
of `BitmapIndexValue(byte[] Value, byte[] Bitmap)` pairs. `null` is returned when no column
on the type carries `[BitmapIndex]`.

`[BitmapIndex]` is pure opt-in — apply only to columns where the per-distinct-value footer
cost is smaller than the query-time savings. High-cardinality columns (timestamps, free-text
identifiers, 64-bit IDs) are anti-patterns.

### Per-row-group CRC32C and quarantine-aware reads (E3)

Every segment carries a per-row-group CRC32C (Castagnoli) in the footer, computed at write
time over a canonical row-group byte representation — the concatenation, in column order, of
each row's value encoded per its `ColumnFieldTypeCode`. Read back with `ReadRowGroupsAsync`:

```csharp
await foreach (var result in format.ReadRowGroupsAsync(stream, from, to))
{
    if (result.IsOk)
    {
        // result.Rows is non-null and carries rows for row group result.RowGroupIndex
        foreach (var row in result.Rows!) { /* use row */ }
    }
    else
    {
        logger.LogWarning(
            "Row group {Index} quarantined: expected {Expected:X8}, actual {Actual:X8}",
            result.RowGroupIndex, result.ExpectedCrc, result.ActualCrc);
    }
}
```

`ReadRowGroupsAsync` materialises every column in one pass and then, for each row group in
turn, rebuilds the canonical bytes and revalidates the CRC before yielding rows. The
corruption surface covered is post-decode — pre-decode byte corruption in a codec-compressed
column is already caught by the codec's own framing invariants, and the footer-level CRC
catches metadata corruption; E3 fills the remaining gap. Surrounding intact row groups
continue to yield normally so a single corrupt group does not fail the whole query.

`ReadAsync`, `ReadBatchesAsync` and `StreamWhereAsync` fail closed instead: when a read decodes
every column of a row group — as point reads, full scans and compaction do — a CRC mismatch
throws `CorruptSegmentException` naming the row group, after the intact row groups before it
have been yielded. A corrupt segment therefore cannot be served, nor rewritten by compaction into
a freshly stamped segment. The CRC covers all of a row group's columns, so a read projected to a
strict column subset cannot evaluate it without decoding the excluded columns; such reads keep
the projection's IO skip and are not CRC-verified. Use `ReadRowGroupsAsync` where a corrupt row
group should be quarantined and the surrounding intact ones still returned.

## Known limitations and future work

- **Across-segment row-group merging.** Compaction merges at the segment level, not the row-group level. Compaction decodes and merges records, then rewrites output dataset blocks through the configured inner format, which replans their row groups. It does not splice existing compressed row groups directly.
- **Default flip.** The library default remains `SegmentFormat = Row` until the post-ship validation gates clear (cold-run deserialisation ≥ 10× cheaper on a realistic backtest; ≥ 5× reduction in peak working-set memory on a realistic recalibration).

### Benchmark numbers (k-way-merge IO-skip, 12-field record)

The `LsmTimeSeriesStorageEngine<,>` columnar path now k-way-merges memtable entries with per-segment `IProjectingSegmentReader.StreamProjectedRangeAsync` streams, so the per-column IO skip from `IColumnarSegmentFormat.ReadAsync` reaches the public `ITimeSeriesStore.GetRangeAsync` surface. On the 10 000-row `MarketDataBar` fixture (12 decimal-heavy fields) a `Close`-only projection lands roughly at half the wall time of the 12-column baseline on the reflection-based `PlainColumnarSegmentFormat`; the ratio tightens further on the codec-compressed `CompressedColumnarSegmentFormat` where the per-column footer offset map lets unprojected bytes be skipped on disk. Characterise via `benchmarks/StorageEngine/ColumnarProjectionBenchmark` (full BDN run) and regression-guarded by `ColumnarProjectionBenchmarkValidationTests` (cheap CI variant).

### `ProjectTo` is generator-emitted

Records annotated with `[StorageSerializable]` (row-format) or `[ColumnarStorable]` (columnar) automatically gain an `IProjectable<TSelf>.ProjectTo(ColumnProjection)` implementation emitted by the source generator — the memtable-vs-segment uniformity (DDIA 2e p. 141) is now held for every generator-annotated record without the consumer hand-writing the interface. A record that explicitly lists `IProjectable<TSelf>` in its base list keeps its own implementation; the generator silently defers.

## Benchmark harness

The benchmark suite includes `RowVsColumnarBenchmark` (under `benchmarks/StorageEngine/`), parameterised over ticker count (1 / 10 / 50) and calendar years (1 / 5 / 10). Run with:

```bash
dotnet run --project benchmarks/StorageEngine/Boutquin.Storage.BenchMark.csproj --configuration Release -- --filter "*RowVsColumnarBenchmark*"
```

The harness uses `PlainColumnarSegmentFormat` as the inner format to isolate envelope + block-layout overhead from Phase 3 codec compression. For end-to-end compressed-codec numbers, swap the inner to `CompressedColumnarSegmentFormat<T>` with a `[ColumnarStorable]` record.

## References

- Kleppmann, M. *Designing Data-Intensive Applications*, 2nd ed. O'Reilly 2026. Ch. 4 "Storage and Retrieval for Analytics" → "Column-Oriented Storage" (pp 136–144).
- Pelkonen et al. 2015. *Gorilla: A Fast, Scalable, In-Memory Time Series Database.* VLDB. §4.1 (delta-of-delta timestamp) and §4.1.2 (XOR-delta doubles).
- Apache Parquet file-format specification — "Data Pages" and "Row Group" sections.
