# Runnable examples

Run these commands from the repository root with the SDK selected by `global.json`.
Restore dependencies from the configured public NuGet feeds as described in
[contributing](../CONTRIBUTING.md). These examples use temporary local files or in-memory
stores; they do not contact a production database. Recovery and fault examples assert their stated invariants.

| Example | Command | Observable result |
|---|---|---|
| Event projections | `dotnet run --project src/Samples -- projections` | Counter resumes at 9/offset 2; replay agrees and document state is searchable |
| Keyword and vector retrieval | `dotnet run --project src/Samples -- search` | Keyword matches and approximate nearest neighbors from HNSW |
| Protobuf evolution | `dotnet run --project src/Samples -- protobuf` | Three writer schemas replay and unknown fields survive the older reader |
| Columnar analytics | `dotnet run --project src/Samples -- analytics` | Exact date filter excludes neighboring rows; decimal aggregate is 20.20 |
| CDC composition | `dotnet run --project src/CdcSample` | Snapshot cursor 2, tail cursor 5, two live documents, retained deletion and analytics total 4 |
| Shard routing | `dotnet run --project src/ShardRoutingDemo` | Versioned metadata rebalance; local and global indexes each return two matches |
| Durable transactions | `dotnet run --project src/TransactionSample -- 20260917` | Interrupted 2PC delivery recovers; duplicate message does not repeat a local effect |
| Raft coordination | `dotnet run --project src/CoordinationSample -- 20260917` | Quorum-gated operations and fencing reject stale ownership |
| Simulation | `dotnet run --project src/SimulationSample` | Raft commits 42; stale follower history is rejected, then follower catches up to 7 |
| Batch aggregation | `dotnet run --project src/BatchSample` | External sort and group sums 6,633 / 6,700 / 6,567 produce total 19,900 |
| Replication | `dotnet run --project src/ReplicationSample -- 20260917` | Causal delivery and leaderless repair under explicit faults |
| Streaming | `dotnet run --project src/Samples -- streaming` | CDC enrichment yields window total 20; checkpointed event-log replay resumes at offset 2 with total 25 |
| Offline sync | `dotnet run --project src/LocalFirstSyncDemo` | Durable offline edits exchange deltas and converge after reopen |

The transaction and coordination examples accept an unsigned seed. Use a new seed to vary
fault scheduling, and retain the failing seed when investigating a counterexample. Simulation
models messages and clocks explicitly; passing a sample is not evidence of production network
or operating-system crash tolerance.

## Benchmarks

For a quick workload execution check, use a dedicated BenchmarkDotNet project:

```sh
dotnet run -c Release --project benchmarks/Cdc -- --filter '*' --job Dry
dotnet run -c Release --project benchmarks/Sharding -- --filter '*' --job Dry
dotnet run -c Release --project benchmarks/Batch -- --filter '*' --job Dry
```

Use the standard jobs, multiple repetitions and an otherwise idle machine for measurements.
Verify the BenchmarkDotNet summary reports the expected **executed** case count; a generated
build timeout can execute zero cases even when the launcher exits normally. Allocation totals
are not peak resident memory. The recorded [batch](BATCH-DATAFLOW.md),
[analytics](analytics.md) and [search](search-and-vector-indexes.md) measurements describe
their concrete inputs and limitations.

For the combined storage benchmark host, `--analytics-only` selects the typed operator
comparison, `--search-only` selects vector retrieval, and `--streaming-only` selects window processing:

```sh
dotnet run -c Release --project benchmarks/StorageEngine -- --analytics-only
dotnet run -c Release --project benchmarks/StorageEngine -- --search-only
dotnet run -c Release --project benchmarks/StorageEngine -- --streaming-only
```
