# Batch and dataflow primitives

The batch module demonstrates external sorting, shuffle partitioning, grouping/joins and dependency-ordered tasks in a single process. It composes with the event-log and read-model interfaces.

## Measured example

A local BenchmarkDotNet short run on 2026-09-17 sorted 5,000 integers, using 128-item runs, four-way merging and JSON record encoding for the external sorter. Both workloads completed three measured iterations after three warmups.

| Workload | Mean | Standard deviation | Total allocated per operation |
|---|---:|---:|---:|
| In-memory sort | 52.58 microseconds | 0.56 microseconds | 19.61 KB |
| External merge sort | 60.69 milliseconds | 12.55 milliseconds | 2,415.43 KB |

The external measurement was noisy on a machine running other validation: its reported confidence-interval error was 228.90 milliseconds. These numbers illustrate the disk-pass and serialization cost for this small input, and are not a stable throughput comparison. Allocated bytes are total managed allocations, not peak live memory. The bounded run size and merge fan-in are enforced by the implementation and exercised by tests; this allocation column does not establish a process memory ceiling.

```sh
dotnet run --project src/BatchSample/Boutquin.Storage.BatchSample.csproj
dotnet run -c Release --project benchmarks/Batch/Boutquin.Storage.Batch.Benchmarks.csproj -- --filter '*' --job short
```

The example sorts 200 descending integers with eight-item runs and three-way merges, shuffles canonical keys across four files, groups each partition, and publishes the aggregates to an event log and a materialized read model. The graph validates every dependency before the first effect. The expected aggregate total is 19,900. Output log and read model are volatile in this example; durable publication requires an atomic sink or replay with idempotent positions.

## Sorting beyond the run budget

`IExternalSorter<T>` / `ExternalMergeSorter<T>` consume a finite async stream once. A chunk holds at most `maxItemsInMemory` items; sorted runs spill to a private directory. Multi-pass merging opens no more than `mergeFanIn` readers and keeps one decoded head per input. Duplicates remain, but equal-key ordering is not stable.

Memory depends on the caller's object graphs, encoded record limit, file buffers and run metadata. The item limit is not a claimed total byte limit: an arbitrary `T` can contain arbitrary data. `maxRecordBytes` bounds serialized payloads and checked decoding allocations. File metadata is proportional to the number of runs. Intermediate records use explicit little-endian int32 lengths and caller-supplied codecs that must preserve values and ordering keys.

A private enumeration directory is removed on completion, exception, cancellation or early enumerator disposal. Unrelated parent files remain. A process crash can leave an orphan directory; scratch files have no durable recovery guarantee. The parent directory must be writable and have room for intermediate runs. Every merge pass trades another full read/write for bounded file descriptors. The benchmark contrasts this cost with a fully in-memory sort, rather than claiming disk sorting is faster.

## Shuffle, grouping and joins

`IShufflePartitioner<T>` returns a valid fixed partition number. `HashShufflePartitioner<T>` applies FNV-1a to canonical key bytes, so equal keys reach the same file. Key encodings and partition count must agree across join inputs. This is an ordinary hash partitioner, not a security hash or an automatic skew remedy.

`ShuffleFileSet<T>` owns the private partition files. Creation consumes the input once and opens one output handle at a time; the per-record append-open cost favors descriptor bounds over speed. Readers stream a partition in input order. Dispose readers before disposing their file set. Failures remove owned partial files.

`MiniDataFrame<TRow>` is a typed, in-memory frame with projection, filtering, grouping and inner equijoins. Its row sequence is copied, while row objects remain borrowed. Grouping holds all rows in a partition, and duplicate join keys produce the full Cartesian matches. Use it for bounded partitions/results; a skewed key can exceed that budget. For large partitions, feed the partition reader through `ExternalMergeSorter` and process adjacent equal keys incrementally.

## Task graph semantics

`IBatchTask`, `BatchTask`, `BatchJobGraph` and `BatchJobRunner` provide a stable topological order. Missing dependencies, duplicate task names and cycles fail before actions run. The runner makes one attempt per task and stops immediately on failure/cancellation. Successful earlier effects remain; there is no rollback, hidden retry or distributed scheduler. Sinks must define atomic publication and retry/idempotency behavior themselves.

This follows the external-sort, shuffle and derived-data patterns described in DDIA's batch-processing chapters. See the [event projection guide](event-projections.md) for rebuilding and maintaining derived views from ordered logs.
