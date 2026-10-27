# Column-batch analytics

Boutquin.Storage can process decoded columnar row groups without rehydrating each record. In this
API, **vectorized** means batch-at-a-time execution over typed arrays. It does not imply hardware
SIMD acceleration.

`FilterOperator<TRecord,TValue>` applies one typed predicate and carries the selected row indexes
across every column. `ProjectionOperator<TRecord>` selects named columns. `AggregateOperator`
reduces one typed column without converting decimal or integer values through floating point.
Empty aggregates return their configured seed, and nullable column elements reach the caller's
predicate or reducer unchanged.

```csharp
var strict = new FilterOperator<DailyBar, DateOnly>(
    "Date", date => date >= from && date <= to).Execute(batch);
var total = new AggregateOperator<DailyBar, decimal, decimal>(
    "Amount", 0m, (sum, amount) => sum + amount).Execute(strict);
```

The strict predicate matters because `ReadBatchesAsync` selects row groups by their date bounds;
the first and last returned groups can contain rows just outside the requested interval.

`ColumnarObjectStoreReader<TRecord>` composes `IObjectStore` with a real
`IColumnarSegmentFormat<TRecord>`. The object is opened as a stream and fed directly to
`ReadBatchesAsync`; a missing key raises `FileNotFoundException`.

`MaterializedAggregateView<TKey,TValue>` folds `KeyValuePair<TKey,TValue>` events into exact
per-key aggregates. `RefreshAsync` resumes from the durable checkpoint. `RebuildAsync` replays the
log from position zero and saves the rebuilt state and position together through
`ICheckpointStore`. State and offset must be saved atomically by the checkpoint store. If a save reports failure
after committing, the next refresh reloads whichever complete checkpoint the store retained;
otherwise it replays the unacknowledged suffix.

Published dictionaries are structurally immutable, including the initial state, rebuilt state,
and decoded checkpoints when no new events remain. Decoded entries are copied before use, so a
decoder retaining its mutable dictionary cannot later alter the view. The configured key comparer
applies after recovery as well as during aggregation. Later refreshes and rebuilds leave previously
returned snapshots unchanged. Keys and values remain borrowed immutable objects: the view does not
deep-clone payloads, and aggregate callbacks must return immutable values without modifying their inputs.

`DataCubeBuilder<TRecord,TMeasure>` emits all populated grouping sets, including the grand total.
An omitted dimension represents `ALL`; a present dimension whose value is `null` remains distinct.
Cube construction is exponential in the number of dimensions, so the implementation rejects more
than 20 dimensions and is intended for compact in-process analysis.

Run `dotnet run --project src/Samples -- analytics` to write a compressed columnar segment,
store it in an in-memory object store, read row-group batches, apply an exact inclusive date filter,
and aggregate a decimal measure. `VectorizedOperatorBenchmark` compares the row-object loop with the
column-batch filter and aggregate pipeline at 4,096 and 65,536 rows. Run only that comparison with
`dotnet run --project benchmarks/StorageEngine -c Release -- --analytics-only`. The checked-in
`VectorizedOperator.baseline.json` records an illustrative M4 run. It shows the materializing batch
filter is slower than an already-rehydrated row loop and allocates in proportion to selected data;
this is the concrete cost of producing a reusable filtered batch. Project columns first to reduce
that cost when later stages need only a subset.
