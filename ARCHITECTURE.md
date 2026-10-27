# Architecture

This document explains how the components in Boutquin.Storage fit together. For a feature overview, see [README.md](README.md). For implementation pitfalls, see [docs/PITFALLS.md](docs/PITFALLS.md). For the DDIA chapter mapping, see [algorithms-in-designing-data-intensive-applications.md](algorithms-in-designing-data-intensive-applications.md).

## Layers

The project follows dependency inversion: **Domain** defines contracts, **Infrastructure** provides implementations. A third in-repo assembly **Distributed** hosts the educational, in-process reference implementations of the distributed-systems primitives (Raft, replication, MVCC / snapshot isolation, partitioning, CRDTs, gossip, Merkle) — project-reference only, never published to NuGet.

```
src/Domain/          Production contracts: interfaces, value objects, exceptions, attributes
src/Infrastructure/  Production implementations: LSM, SSTable, WAL, columnar, source-generator runtime,
                     event log, CDC, schemas, checkpointing, object store, value log, DataStructures
                     (BTree, BPlusTree, BloomFilter, CountingBloomFilter, RedBlackTree, SkipListMemTable)
src/Distributed/     In-process reference implementations (a), NOT shipped to NuGet:
                     Consensus (Raft), Replication, Transactions (MVCC/snapshot isolation), Partitioning,
                     DistributedSystems (CRDTs, VectorClock, LamportTimestamp, Gossip), Merkle
src/SourceGenerator/ Roslyn IIncrementalGenerator (netstandard2.0, no project references)
tests/               xUnit tests (depends on Infrastructure + Distributed)
tests/SourceGenerator/ Generator snapshot, diagnostic, integration, and caching tests
tests/ArchitectureTests/ NetArchTest rules, including the Distributed one-way-dependency guard
benchmarks/          BenchmarkDotNet performance tests
```

- **Domain** depends on the public `Boutquin.Domain` 1.0.0 package.
- **Infrastructure** depends on Domain plus K4os.Compression.LZ4, Google.Protobuf,
  System.IO.Hashing and TestableIO.System.IO.Abstractions. It ships to NuGet under
  `Boutquin.Storage.Infrastructure` within the [documented limits](docs/TRADEOFFS.md).
- **Distributed** depends on Domain and on Infrastructure (for the `Murmur3` /
  `XxHash32` default `IHashAlgorithm` instances that the carved partitioners fall
  back to when no algorithm is injected). In-process reference — `<IsPackable>false</IsPackable>`,
  consumed via `<ProjectReference>` only. The reverse dependency is forbidden
  (`tests/ArchitectureTests/DistributedAssemblyTests.cs` guards the invariant).
- **SourceGenerator** targets netstandard2.0 (Roslyn requirement) and discovers
  Domain attributes by fully-qualified metadata name — it has no project reference
  to Domain.
- Tests and benchmarks depend on Infrastructure and Distributed.

**Scope tiers.** The project distinguishes **(b) production-strength** —
`Boutquin.Storage.Domain` and `Boutquin.Storage.Infrastructure`, shipped to NuGet with
a locked PublicAPI baseline, intended for production use — from **(a) in-process
reference, doc-corrected** — `Boutquin.Storage.Distributed`, in-repo only, each public
type declares its educational scope and points to production alternatives in its
`<remarks>` header. See [`src/Distributed/README.md`](src/Distributed/README.md) for
the full scope statement.

## Interface Hierarchy

The storage interfaces split along two axes: **serialization requirements** and **bulk operation support**.

```
IReadOnlyKeyValueStore<TKey, TValue>         TKey : IComparable
  └─ IKeyValueStore<TKey, TValue>            + Set, Remove, Clear
       │
       ├─ IStorageEngine<TKey, TValue>        TKey : ISerializable (no new methods — marker)
       │
       └─ IBulkKeyValueStore<TKey, TValue>    + GetAllItems, SetBulk (TKey : IComparable only)
            │
            ├─ IBTree, IBPlusTree, IMemTable   In-memory data structures
            │
            └─ IBulkStorageEngine              Combines IStorageEngine + IBulkKeyValueStore
                 │
                 ├─ ICompactableBulkStorageEngine   + EntrySerializer property
                 │
                 └─ ILsmStorageEngine               + Flush, Compact, GetRange, SegmentCount
```

**Why two branches?** `IBulkKeyValueStore` uses loose `IComparable` constraints so in-memory data structures (BTree, BPlusTree, SkipListMemTable) can implement it without requiring serialization. File-backed engines need `ISerializable` for disk persistence, so they go through `IBulkStorageEngine`, which reunites the two branches. `ILsmStorageEngine` extends `IBulkStorageEngine` so it is usable anywhere an `IStorageEngine` is expected.

**The key constraint — `IStorageKey<TSelf>`.** File-backed engines and segment formats constrain their key type with `TKey : IStorageKey<TSelf>, new()`. `IStorageKey<TSelf>` is the curiously-recurring self-typed contract that bundles the two things a persisted key must provide: `IComparable<TSelf>` (sort order for the SSTable / B-tree layout) and `ISerializable<TSelf>` (byte-lex-comparable ordered-wire encode/decode). The source generator emits an `IStorageKey<TSelf>` implementation for every `[Key]`-annotated record; `SerializableWrapper<T>` implements it by hand for test fixtures. Value types only need `ISerializable<TValue>, new()`.

## LSM Engine Composition

`LsmStorageEngine` coordinates the following subsystems:

```
                    ┌───────────────────┐
    Set/Get ──────> │  LsmStorageEngine │
                    └────────┬──────────┘
                             │ coordinates
              ┌──────────────┼──────────────────┐
              │              │                  │
              ▼              ▼                  ▼
     ┌──────────────┐  ┌───────────┐   ┌──────────────────┐
     │ WriteAheadLog│  │ RedBlack  │   │ On-Disk Segments │
     │  (optional)  │  │   Tree    │   │ (SST or columnar│
     │              │  │ (MemTable)│   │  segment format) │
     └──────────────┘  └───────────┘   └────────┬─────────┘
                                                │
                                    ┌───────────┼───────────┐
                                    ▼           ▼           ▼
                              ┌──────────┐ ┌────────┐ ┌───────────┐
                              │ Bloom    │ │ Entry  │ │ Compaction│
                              │ Filter   │ │Serial- │ │ Strategy  │
                              │(per-seg) │ │ izer   │ │ (pluggable│
                              └──────────┘ └────────┘ └───────────┘
```

| Subsystem | Interface | Implementations | Role |
|-----------|-----------|-----------------|------|
| MemTable | `IKeyValueStore` | `RedBlackTree`, `SkipListMemTable` | In-memory sorted buffer for writes |
| Segments | `ISegmentFormat` | `SstSegmentFormat` (default, `.sst`), `ColumnarSegmentFormat` (`.bsct`) | Immutable on-disk segment layout |
| WAL | `IWriteAheadLog` | `WriteAheadLog` | Crash-recovery log (bounded group commit by default, per-entry seqno) |
| Bloom filters | `SegmentBloomFilterOptions`, `IBloomFilterStateCodec` | Native byte-keyed filters or custom persisted filters | Skip definite misses on point reads; [configuration and lifecycle](docs/segment-bloom-filters.md) |
| Compaction | `ICompactionStrategy` | `Full`, `SizeTiered`, `Leveled` | When to merge segments |
| Serialization | `IEntrySerializer` | `BinaryEntrySerializer`, `CsvEntrySerializer` | Key-value to/from bytes (used by `AppendOnlyFileStorageEngine` standalone primitive) |

## Data Flow

### Write Path

```
Client calls SetAsync(key, value)
  │
  ├─ 1. WAL.AppendAsync(key, value)        ← if a WAL is supplied, await its flush barrier
  │
  ├─ 2. EnsureActiveHasCapacityAsync()     ← rotation gate (DDIA Ch. 3 p. 117–119)
  │      └─ If active.Tree.IsFull:
  │           ├─ Release _lock; await _ingestGate.WaitAsync(ct)
  │           │    (the ONLY ingest-blocking signal; queue saturated → block)
  │           ├─ Reacquire _lock
  │           └─ Seal old active → _immutables; allocate fresh active;
  │              signal flusher via Channel<byte>
  │
  └─ 3. _active.Tree.SetAsync(key, value)   ← O(log M) balanced tree insert
                                              (writes target the FRESH active)

Background flusher loop (idle until signalled):
  ├─ Pick head of _immutables               ← FIFO seal order (invariant L2)
  ├─ Snapshot data + tombstones under _lock
  ├─ Release _lock; write SSTable to disk    ← WITHOUT _lock; ingest proceeds
  ├─ Reacquire _lock; commit:
  │    ├─ Append segment + manifest
  │    ├─ Remove head of _immutables
  │    ├─ Release _ingestGate slot          ← unblocks blocked ingest
  │    ├─ WAL.TruncateCompletedSegmentsAsync(max seqno CONTAINED in the flushed slot)
  │    │     deletes only WAL segments wholly at or below that watermark; a segment that
  │    │     also holds frames of writes applied to the fresh active memtable stays. It
  │    │     is not conditioned on the queue draining. (The whole-log
  │    │     TruncateAsync runs only on ClearAsync and clear-pending recovery.)
  │    └─ If compaction strategy fires: CompactInternalAsync()
  └─ Complete the slot's Persisted TCS       ← FlushAsync awaiters unblock here
```

### Read Path

```
Client calls TryGetValueAsync(key)
  │
  ├─ 1. Probe _active                       ← most recent writes are here
  │      (data hit, tombstone miss, or shadowed → walk stops)
  │
  ├─ 2. Probe _immutables newest-first      ← any seal still in-flight is visible here
  │      (rotation invariant L3: a sealed slot stays read-visible until its
  │      segment is durable AND the manifest reflects it)
  │
  └─ 3. Search on-disk segments (newest → oldest):
       For each segment:
         ├─ Check segment tombstones
         ├─ Check Bloom filter              ← if definite miss, skip this segment
         └─ Format-specific lookup          ← SST block index or columnar dataset index
       First match wins (newest data takes precedence)
```

### Compaction

```
CompactAsync()
  │
  ├─ 1. Strategy selects segments; capture readers and snapshot watermark
  ├─ 2. K-way merge selected streams with newest-value precedence
  ├─ 3. Drop tombstones only when coverage and snapshot rules allow it
  ├─ 4. Write replacement segment, including its Bloom state
  ├─ 5. Commit the new authoritative manifest
  └─ 6. Retire superseded files; retain snapshot-pinned readers/files
```

The merge accumulates surviving entries before writing; peak memory is proportional to
compacted output. Inline values are decoded and re-serialized, while value-log pointers
pass through unchanged. See [schema rewrite limits](docs/wire-format-2.0.md).

### Snapshot Isolation

`LsmStorageEngine.AcquireSnapshotAsync` captures a point-in-time consistent read view as a
`(sequence_number_at_start, segment_set_at_start)` pair, atomically under the engine's write
lock. `ISnapshot<TKey, TValue>` serves `TryGetValueAsync` / `GetRangeAsync` reads against that
frozen view: every entry with per-entry seqno `≤ SequenceNumber` is visible; every entry
written after acquisition — values and tombstones alike — is invisible. Per-key resolution
follows the DDIA 2e Ch. 7 MVCC rule, walking MemTable → segments newest-to-oldest and
stopping at the first visible entry.

```
AcquireSnapshotAsync()
  │ (under engine._lock)
  ├─ capture LastAssignedSequenceNumber      (snapshot.SequenceNumber)
  ├─ capture ISegmentReader list + filenames (pinned — compaction may not delete)
  ├─ capture per-segment tombstone seqno maps (immutable copies)
  └─ capture MemTable data + tombstones, each with its per-key seqno
```

While a snapshot is live, compaction consults the `SnapshotRegistry` before physically
deleting superseded segments — pinned files stay on disk and their readers remain alive
until the last pinning snapshot disposes. Tombstone GC additionally defers stripping while
`min(live-snapshot seqnos) ≤ tombstone.seqno`; the bottommost-segment predicate is a
necessary but not sufficient condition.

Isolation is **format-agnostic**: `AcquireSnapshotAsync` succeeds on **both** the row/SST and
the columnar time-series segment formats. The seqno-filtered per-entry visibility above is the
row path; for columnar segments — which carry no per-entry seqno slot — isolation is enforced
purely by the same engine-level segment pinning plus the at-acquisition memtable copy. A pinned
columnar segment holds only pre-acquisition data (an immutable on-disk segment is never appended
to), so post-acquisition writes — including those a concurrent compaction merges into a new
segment — stay invisible to the live snapshot; the superseded segment is parked (kept readable),
not deleted, until the snapshot disposes. Engine-level `SupportsSnapshots` is therefore `true`
for both formats, while the narrower format-level `ISegmentFormat.SupportsSnapshots` (`false` for
columnar) keeps its "carries a per-entry seqno slot" meaning and is no longer the isolation gate.
`LsmStorageEngine.RetiredPinnedFileCount` surfaces the compaction-superseded segments a held-open
snapshot keeps parked on disk.

An `ISnapshot` gives stable point and range reads, including protection from phantom reads
within that view. It does not validate writes made outside the snapshot. The
single-node transaction primitives in `Boutquin.Storage.Distributed.Transactions` —
`MvccStore` and `SnapshotIsolationStore` — also implement only snapshot isolation, not
serializable snapshot isolation (no predicate-range tracking). For full serializability,
use a transaction protocol or application locking that validates the complete read/write
set and relevant predicates; a single-key compare-and-set alone is insufficient. See
[docs/snapshot-isolation.md](docs/snapshot-isolation.md) for the full consumer guide, the
worked visibility examples, the retention cost model, and the `await using` lifecycle
pattern.

### WiscKey Value Log

`Boutquin.Storage.Infrastructure.ValueLog` is an append-only, CRC-protected value-log
subsystem that stores large values in dedicated segment files and lets the SSTable block
entry carry only a fixed-width pointer. `LsmStorageEngine<TKey, TValue>.SetAsync` routes
through it when the serialised payload exceeds `ValueLogOptions.ValueLogThresholdBytes`;
small values stay inline. Reads dereference pointers transparently via `ValueLogReader`.
SSTable flush and compaction carry `ValueKind.ValueLogPointer` end-to-end without
re-inlining (so large values are no longer rewritten through every compaction level),
and `LsmStorageEngine.CompactValueLogAsync()` runs whole-file garbage collection over
`ValueLogGarbageCollector`, honouring live-snapshot pointer pins before unlinking closed
segment files. See [`docs/wisckey-value-log.md`](docs/wisckey-value-log.md) for the full
subsystem reference — file format, configuration tuning, and operator runbook.

```
valuelog/
  valuelog.manifest              — append-only add/delete/seal records (29 B each)
  valuelog_NNNNNN.log            — entry frames [length:u32|header_crc32c:u32|kind:u8|seqno:u64|payload|payload_crc32c:u32]

ValueLogWriter.AppendAsync(payload, seqno)
  │
  ├─ 1. Check |payload| ≤ MaxEntryBytes else throw ValueTooLargeException
  ├─ 2. If the frame would not fit within SegmentMaxBytes → manifest.Seal (length, frame
  │     count, CRC32C of the segment) + open next + manifest.Add
  ├─ 3. Write frame (17 B header incl. header CRC32C + payload + payload CRC32C), fsync segment
  └─ 4. Return ValueLogPointer(fileIndex, offset)

ValueLogReader.Read(pointer)
  │
  ├─ 1. Resolve SafeFileHandle via LRU-bounded pool (ReadHandleCacheSize)
  ├─ 2. RandomAccess.Read header (17 B); verify header CRC32C (length, kind, seqno)
  ├─ 3. RandomAccess.Read payload (length B) and payload CRC trailer (4 B)
  └─ 4. Verify payload CRC32C; either mismatch → CorruptValueLogPointerException
        (both checksums are skipped when ValidateCrcOnRead = Never)
```

Segment files are named `valuelog_NNNNNN.log` (monotonic 6-digit counter, matching the
WAL pattern). Magic bytes `'B','S','V','L'`, format version 1. The 12-byte pointer sits
in the value-bytes slot of an SSTable block entry whose `ValueKind = ValueLogPointer`
(the slot was widened from an earlier 8-byte offset design to a 12-byte
`(file_index : u32, packed_offset_and_sequence_check : u64)` pair so the garbage collector can reclaim closed
segment files rather than only rewriting in place). See
[docs/wisckey-value-log.md](docs/wisckey-value-log.md) for the full byte diagram,
tuning guide, and operator runbook.

#### Engine dispatch

```
LsmStorageEngine.SetAsync(key, value)
  │
  ├─ 1. Serialise value → N bytes (payloadStream)
  ├─ 2. Assign monotonic seqno
  ├─ 3. If N > ValueLogOptions.ValueLogThresholdBytes:
  │       ├─ EnsureValueLogOpenAsync (lazy)
  │       ├─ ValueLogWriter.AppendAsync(bytes, seqno) + fsync → ValueLogPointer p
  │       ├─ wal?.AppendPointerAsync(key, p, seqno) + fsync
  │       └─ memTable.SetAsync(key, MemTableValue.ValueLogPointer(p))
  │     Else:
  │       ├─ wal?.AppendAsync(key, value, seqno) + fsync
  │       └─ memTable.SetAsync(key, MemTableValue.Inline(value))
  └─ 4. memTableSeqnos[key] = seqno; clear tombstone

LsmStorageEngine.TryGetValueAsync(key)
  │
  ├─ 1. Check memTableTombstones → if hit, return (default, false)
  ├─ 2. memTable.TryGetValueAsync(key) → MemTableValue slot
  │     ├─ Inline → return slot.InlineValue
  │     └─ ValueLogPointer → ValueLogReader.Read(slot.Pointer)
  │                          → bytes → TValue.Deserialize → return
  └─ 3. Fall through to segment scan (newest-first); each segment block entry
        dispatches on its ValueKind — inline bytes deserialise directly, pointer
        entries dereference through ValueLogReader (ResolveBlockEntryValue)
```

WAL frames carry two orthogonal bytes — `EntryKind` (`Set`/`Delete`) and `ValueKind`
(`Inline`/`ValueLogPointer`). Recovery dispatches on `(EntryKind, ValueKind)`: pointer
frames re-apply the pointer to the MemTable after checking the referenced value-log
file's durable EOF covers it (dangling pointers surface as
`DanglingValueLogPointerException`). SSTable-side `ValueKind` propagation is end-to-end:
flush carries a pointer-valued MemTable slot through as `SegmentWriteEntry.Pointer(...)`
with its 12-byte body intact (no value-log read, no `TValue.Deserialize` at flush time),
and compaction copies the pointer bytes verbatim through the k-way merge. The pointer is
dereferenced only at the engine read layer. Whole-file garbage collection runs via
`LsmStorageEngine.CompactValueLogAsync()`.

### Time-Series Column Projection

The time-series store surface threads a `ColumnProjection` through the full read
stack so callers reading only a subset of record fields (e.g. close + volume
from a 12-field bar) unlock the read-amplification win the columnar segment
format was designed to deliver.

**Projected read path.** The call chain is:
`ITimeSeriesStore.GetRangeAsync(dataset, from, to, projection, ct)` →
`ITimeSeriesStorageEngine.GetRangeAsync(startKey, endKey, projection, ct)` →
(format-aware dispatch on `LsmTimeSeriesStorageEngine`) → for Columnar engines,
a k-way merge consumes one memtable slice (range-filtered, pointer-dereferenced,
`ProjectTo`-clipped) and one projected stream per segment obtained by casting
each `ISegmentReader` to
`IProjectingSegmentReader<TKey, TValue, ColumnProjection>` and invoking
`StreamProjectedRangeAsync`. Columnar segment readers fan the projection down
into `IColumnarSegmentFormat.ReadAsync` so unprojected column byte ranges are
skipped on disk (per the footer's per-column offset map). Segment readers that
fail the cast (row-format segments in a mixed-format engine) fall back to
`StreamBlockEntriesAsync` with post-hoc `ProjectTo`, preserving the uniformity
guarantee at full-materialisation cost.

**When to use.** The projection earns its cost only when the engine is
Columnar-configured AND the record type has many fields AND the caller's
hot path touches a narrow subset. Row-format engines accept the parameter for
call-site symmetry but short-circuit — row readers have no per-field skip to
offer. In-memory stores (`InMemoryTimeSeriesStore`) have no on-disk bytes to
skip either; their projection application is purely shape-uniformity (see
below).

**Memtable-vs-segment uniformity.** Per DDIA 2e Ch. 4 p. 141, the engine
hides the memtable-vs-segment distinction from the caller. A just-written
record that has not yet flushed exposes the same projection shape as a
post-flush segment row — unprojected fields are `default(TField)` in both.
This uniformity relies on the record type implementing `IProjectable<TSelf>`.
The source generator emits `IProjectable<TSelf>` and `ProjectTo` automatically
for every `[StorageSerializable]` and `[ColumnarStorable]`-annotated record;
consumers that hand-list the interface in their base list supersede the
emission (escape hatch for custom projection logic).

### Startup Recovery

```
LsmStorageEngine.OpenAsync(options)
  │
  ├─ 1. Constructor enumerates prefix_*{SegmentFormat.FileExtension}
  ├─ 2. Read CURRENT manifest (authoritative segment set)
  │    ├─ Delete unlisted segment files left by interrupted compaction
  │    ├─ Validate every listed file's presence, size, and CRC32C
  │    └─ Open validated segments in manifest order
  ├─ 3. Open the value-log subsystem when its directory exists
  └─ 4. Replay WAL entries asynchronously into the MemTable
```

## Component Navigation

**"I want to..."** → Start here:

| Goal | Start at | Key files |
|------|----------|-----------|
| Understand the full LSM engine | `ILsmStorageEngine` | `LsmStorageEngine.cs` |
| Add a new storage engine | Implement `IStorageEngine` | See `AppendOnlyFileStorageEngine` as template |
| Add a new compaction strategy | Implement `ICompactionStrategy` | See `FullCompactionStrategy` (simplest) |
| Understand on-disk format | `IEntrySerializer` | `BinaryEntrySerializer.cs`, WAL entry format in `WriteAheadLog.cs` |
| Add a new data structure | Implement `IBulkKeyValueStore` | See `BTree` (tree) or `SkipListMemTable` (probabilistic) |
| Understand partitioning | `IConsistentHashRing`, `IRangePartitioner` | `ConsistentHashRing.cs`, `RangePartitioner.cs` |
| Understand transactions | `IMvccStore`, `ISnapshotIsolationStore` | `MvccStore.cs` (MVCC), `SnapshotIsolationStore.cs` (plain snapshot isolation) |
| Understand replication | `ISingleLeaderReplication`, `IQuorumReplication` | Single-leader vs Dynamo-style W+R>N quorum |
| Understand consensus | `IRaftNode`, `IRaftCluster` | Leader election + log replication |
| Understand CRDTs | `IGCounter`, `IORSet` | Grow-only → add-wins observed-remove progression |
| Add a source-generated key type | Annotate with `[Key]` | `StorageSourceGenerator.cs`, `CodeGenerator.cs` |
| Add a source-generated value type | Annotate with `[StorageSerializable]` | `StorageSourceGenerator.cs`, `CodeGenerator.cs` |
| Customize generated output | `[assembly: StorageDefaults(...)]` | `StorageDefaultsAttribute.cs` |
| Understand generator diagnostics | `DiagnosticDescriptors.cs` | BSSG001–BSSG026 (BSSG009 reserved-unshipped), `StorageDiagnosticSuppressor.cs` |
| Understand object storage | `IObjectStore` | `FileSystemObjectStore.cs`, `InMemoryObjectStore.cs` |
| Understand schema evolution | `ISchemaRegistry` | `InMemorySchemaRegistry.cs`, `AvroFieldLevelCompatibilityChecker.cs`, `TaggedWireCompatibilityChecker.cs`, `ProtobufTagCompatibilityChecker.cs` |
| Understand event sourcing | `IEventLog` | `AppendOnlyEventLog.cs`, `InMemoryEventLog.cs` |
| Understand CDC | `IChangeDataCaptureSource` | `EventLogCdcSource.cs`, `ObjectStoreCdcSource.cs` |
| Understand checkpointing | `ICheckpointStore` | `FileCheckpointStore.cs`, `InMemoryCheckpointStore.cs` |
| Run storage benchmarks | `benchmarks/StorageEngine/Program.cs` | 16 registered benchmark types covering storage engines, structures, serializers, WAL, row/columnar comparison, analytics, vector search, and streaming windows |

## Directory Structure

```
src/
  Domain/
    Interfaces/          Storage and service contracts
                         (includes ObjectStore/IObjectStore,
                          Streaming/IStream, ICheckpointStore,
                          Schemas/ISchema, ISchemaRegistry,
                            ISchemaCompatibilityChecker,
                            IAvroStyleVersionedSerializer, IProtobufSerializer,
                          Events/IEventLog,
                          Cdc/IChangeDataCaptureSource,
                          Batch/IExternalSorter, Analytics/IVectorizedOperator,
                          Search/IInvertedIndex, IVectorIndex)
    Helpers/             SerializableWrapper, Guard
    ValueObjects/        FileLocation, SsTableMetadata, VersionedValue,
                         SchemaVersion, SchemaField, SchemaEnvelope,
                         EventEnvelope, ChangeRecord
    Exceptions/          Domain-specific exception types
    Enums/               NodeColor, RaftState, CompatibilityMode,
                         SchemaFieldType, ChangeOperation
  Infrastructure/
    LsmTree/             LsmStorageEngine + 3 compaction strategies
    AppendOnlyFileStorage/  AppendOnlyFileStorageEngine (+ WithIndex variant)
    LogSegmentFileStorage/  LogSegmentedStorageEngine
    KeyValueStore/       InMemoryKeyValueStore, ConcurrentKeyValueStore, StorageFile
    WriteAheadLog/       WriteAheadLog (CRC32C, bounded group commit,
                         bounded torn-tail repair and fail-closed corruption checks)
    SortedStringTable/   SortedStringTable (atomic writes, block index, LZ4 default)
    DataStructures/      BTree, BPlusTree, RedBlackTree, SkipListMemTable,
                         BloomFilter, CountingBloomFilter
    Algorithms/          Fnv1aHash, Murmur3, XxHash32
    Serialization/       BinaryEntrySerializer, CsvEntrySerializer
    Indexing/            SecondaryIndex, InMemoryStorageIndex, InMemoryFileIndex
    ObjectStore/         FileSystemObjectStore, InMemoryObjectStore
    Streaming/           EventLogStream, processors, windows, joins, state, sinks,
                         FileCheckpointStore, InMemoryCheckpointStore
    Analytics/           Vectorized filter/projection/aggregate, data cubes,
                         materialized aggregate views
    Batch/               ExternalMergeSorter, HashShufflePartitioner,
                         BatchJobGraph/Runner, MiniDataFrame
    Search/              InvertedIndex, FlatVectorIndex, HnswVectorIndex, IvfVectorIndex
    TimeSeries/          Row and columnar LSM segment formats, projection-aware reads
    Compression/         DefaultCompressionCodecRegistry and codec implementations
    Schemas/             InMemorySchemaRegistry, AvroFieldLevelCompatibilityChecker,
                         TaggedWireCompatibilityChecker, ProtobufTagCompatibilityChecker,
                         JsonVersionedSerializer, TaggedWireVersionedSerializer, ProtoSerializer
    Events/              AppendOnlyEventLog, InMemoryEventLog
    Cdc/                 EventLogCdcSource, ObjectStoreCdcSource, LsmCdcAdapter,
                         projection sinks, checkpointed log-compacted topic
    StorageWithBloomFilter/  BulkKeyValueStoreWithBloomFilter
  Distributed/           In-process reference, <IsPackable>false</IsPackable>, not on NuGet
    Consensus/           RaftNode, RaftCluster
    Replication/         SingleLeaderReplication, MultiLeaderReplicaSet,
                         LeaderlessReplicaSet, QuorumReplication, ReplicationLog
    Replication/Exceptions/  FollowerTooFarBehindException
    Transactions/        MvccStore, SnapshotIsolationStore, TwoPhaseCommitCoordinator,
                         DurableTransactionalResource, transactional message store
    Partitioning/        ConsistentHashRing, RangePartitioner, HashPartitioner, RendezvousHash
    DistributedSystems/  GCounter, PNCounter, GSet, ORSet, VectorClock,
                         LamportTimestamp, GossipProtocol
    Merkle/              MerkleTree
    Coordination/, Sharding/, Simulation/, Sync/
                         Additional in-process reference capability groups
    Interfaces/{Consensus,Coordination,DistributedSystems,Merkle,Partitioning,
                Replication,Sharding,Simulation,Sync,Transactions}/
                         Carved contracts (IRaftNode, IReplicationLog, ICrdt,
                         IConsistentHashRing, IMvccStore, ISnapshotIsolationStore, IMerkleTree, ...)
    README.md            Scope statement + production-library pointers
  SourceGenerator/       Roslyn IIncrementalGenerator (netstandard2.0)
    StorageSourceGenerator.cs   Pipeline: attribute discovery → type extraction → code emission
    CodeGenerator.cs            Emits ISerializable, IComparable, IEquatable, operators
    DiagnosticDescriptors.cs    BSSG001–BSSG026 diagnostic rules (BSSG009 reserved-unshipped)
    StorageDiagnosticSuppressor.cs  Suppresses CA1036/S1210 on [Key] types
    EquatableArray.cs           Value-equal array wrapper for pipeline caching
    TypeToGenerate.cs           Pipeline data model (no Roslyn types)
tests/
  Infrastructure/        xUnit tests covering Infrastructure + Distributed
  SourceGenerator/       Generator tests (snapshots, diagnostics, integration, caching)
  ArchitectureTests/     Architecture tests (naming, design, dependency enforcement,
                         columnar-namespace isolation, Distributed one-way dependency)
benchmarks/
  StorageEngine/         16 registered benchmark types (storage engines, data structures,
                         serializers, SSTable, Bloom filter, WAL, compaction,
                         row/columnar comparison, analytics, search, streaming)
  Batch/, Cdc/, Coordination/, Replication/, Sharding/, Simulation/,
  Sync/, Transactions/   Focused workload hosts for composed capabilities
  Hashing/               Hash algorithm benchmarks (FNV-1a, Murmur3, xxHash32)
docs/
  PITFALLS.md            15 bug categories with fix patterns and test evidence
```
