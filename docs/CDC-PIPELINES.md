# Change capture and derived data

CDC connects ordered storage changes to materialized read models, full-text indexes and downstream analytics. The source position belongs to one source lifetime: never combine unrelated logs into a sink that deduplicates only by position.

```sh
dotnet run --project src/CdcSample/Boutquin.Storage.CdcSample.csproj
dotnet run -c Release --project benchmarks/Cdc/Boutquin.Storage.Cdc.Benchmarks.csproj -- --filter '*' --job Dry
```

The example captures two documents, saves a consistent snapshot/cursor, then updates one document, deletes another and inserts a third. Snapshot plus tail produces two current documents, one full-text match for `storage`, and cursor 5. Replaying from zero keeps the deletion intact. A file-checkpointed compacted topic retains three records, including the deletion watermark. The same uncompacted CDC journal feeds `MaterializedAggregateView` with replacement word counts (zero on deletion); checkpoint refresh and full rebuild both produce four current words. `AggregateOperator` independently computes the same result from a column batch of the current read model. This avoids counting overwritten text twice.

## LSM capture and recovery

`LsmCdcAdapter<TKey,TValue>` exclusively owns writes to a dedicated LSM engine and `IEventLog<ChangeRecord<TKey,TValue>>`. Call `RecoverAsync` before use. `SetAsync` and `RemoveAsync` append an ordered change, then materialize it in the engine. The event-log append is the commit point. `AppendOnlyEventLog` provides the durable journal implementation; an in-memory log gives volatile capture.

An engine failure after append can leave a committed change whose call reported failure. New adapter operations remain blocked until recovery replays the journal. Once append returns successfully, materialization ignores caller cancellation so cancellation does not intentionally split the two steps. Process interruption still requires recovery. A retry is another change with another position; this adapter does not deduplicate business request IDs.

Recovery validates the journal prefix, rejects unexpected engine keys before writing, and reapplies the latest value or deletion per key. It does not clear an unrelated engine. Start with an empty dedicated pair or reopen the same pair. Bypassing the adapter, sharing the journal with another writer, or mutating borrowed keys/values violates the ownership contract. The caller disposes resources after all operations finish. Recovery is O(history plus retained keys); the journal and deletion metadata are not automatically pruned.

## Replication capture

`SingleLeaderCdcSource` observes every direct `SingleLeaderReplication.WriteAsync`, including writes predating adapter attachment. Capture happens under the same gate as the leader mutation and replication-log append. Snapshot capture returns latest keyed records and their paired next cursor. CDC retains its own history, so follower-driven replication-log truncation cannot erase consumer changes.

The single-leader reference implementation is volatile, has no delete method, and retains this history for its lifetime. It is not a durable database replication connector. Finish operations before disposing the leader.

## Snapshot-to-tail handoff

`ISnapshotCdcSource.CaptureSnapshotAsync` returns `CdcSnapshot.Records` with the original per-key positions and `NextPosition`, the first change after the snapshot. Apply those records to a fresh or explicitly reset sink, then read from `NextPosition`. Writers may run between those steps: their positions are at or above the cursor. Retained delete records prevent an old replay from resurrecting a key.

`CdcProjectionPump.BootstrapAsync` performs that handoff; `CatchUpAsync` applies currently available changes and returns a next cursor only after the sink accepts them. Sources use finite capture boundaries; call catch-up again to poll newer changes. They do not block forever waiting for another append. A failed catch-up may already have applied earlier records. Replay from the last persisted cursor using idempotent sinks. Resume cursors are not persisted by the pump itself.

Snapshots do not implicitly remove unrelated target rows. Bootstrap a fresh target, and restart a failed initial bootstrap from a new snapshot. Values remain borrowed and must be treated as immutable.

## Compacted topics and sinks

`ILogCompactedTopic` retains the latest record per key, including deletions. `CheckpointedLogCompactedTopic` saves that record set and the exclusive next offset together through `ICheckpointStore`. Its persistence follows the chosen checkpoint store. One owner must use each identifier; there is no compare-and-swap across topic instances. Call `RestoreAsync` on open and after any failed save, including a save that may have committed before returning an error. Offsets are not reused after recovery.

Compaction intentionally creates gaps. Reads reconstruct latest state, not every historical event, so a compacted topic is unsuitable as the history for counting every update. Each append serializes the complete retained state: O(retained keys) time/space. Tombstones remain until an external retention design can prove all readers are past them. JSON options and key equality must form a stable round-trip contract across reopen.

`ReadModelProjectionSink` delegates value and per-key watermark updates to `IReadModelStore`. `InvertedIndexProjectionSink` maintains a volatile index and per-key positions, replacing old terms and preserving deletion watermarks. Recreate the text sink and index together and bootstrap after restart. Separate sinks are not one atomic transaction; replay-safe per-sink positions allow catch-up after a partial fan-out failure.

The sample analytics adapter requires contiguous un-compacted positions because `ProjectionRunner` verifies them. Feed the full journal to that view, rather than a compacted topic with intentionally missing positions. See [analytics operators](analytics.md) for their typed batch and view contracts.

See [event projections](event-projections.md) for state/offset checkpoints and deterministic rebuilds, and [search indexes](search-and-vector-indexes.md) for text analysis and retrieval behavior.

## Verification and costs

Tests exercise journal reopen into a new LSM engine, post-commit failures, rejected foreign data, cancellation, follower truncation, delete replay, and twenty seeded snapshot/tail schedules totaling 2,000 changes. The benchmark workloads exercise compacted checkpoint copying and repeated read-model projection at two input sizes. Dry jobs are executable workload checks, not stable latency measurements or peak-memory claims.
