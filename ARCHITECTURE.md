# Architecture

This document explains how the components in Boutquin.Storage fit together. For a feature overview, see [README.md](README.md). For implementation pitfalls, see [docs/PITFALLS.md](docs/PITFALLS.md). For the DDIA chapter mapping, see [algorithms-in-designing-data-intensive-applications.md](algorithms-in-designing-data-intensive-applications.md).

## Layers

The project follows dependency inversion: **Domain** defines contracts, **Infrastructure** provides implementations.

```
src/Domain/          Interfaces, value objects, exceptions, attributes (no dependencies)
src/Infrastructure/  Implementations (depends only on Domain)
src/SourceGenerator/ Roslyn IIncrementalGenerator (netstandard2.0, no project references)
tests/               xUnit tests (depends on Infrastructure)
tests/SourceGenerator/ Generator snapshot, diagnostic, integration, and caching tests
benchmarks/          BenchmarkDotNet performance tests
```

Domain has zero external dependencies. Infrastructure depends only on Domain. The SourceGenerator targets netstandard2.0 (Roslyn requirement) and discovers Domain attributes by fully-qualified metadata name — it has no project reference to Domain. Tests and benchmarks depend on both.

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

## LSM Engine Composition

`LsmStorageEngine` is the flagship component. It wires together five subsystems:

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
     │  (optional)  │  │   Tree    │   │ (AppendOnlyFile  │
     │              │  │ (MemTable)│   │  StorageEngine)  │
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
| Segments | `IStorageEngine` | `AppendOnlyFileStorageEngine` | Immutable on-disk key-value files |
| WAL | `IWriteAheadLog` | `WriteAheadLog` | Crash-recovery log (fsync per append) |
| Bloom filters | `IBloomFilter` | `BloomFilter` | Skip segments on read (false-positive-only) |
| Compaction | `ICompactionStrategy` | `Full`, `SizeTiered`, `Leveled` | When to merge segments |
| Serialization | `IEntrySerializer` | `BinaryEntrySerializer`, `CsvEntrySerializer` | Key-value to/from bytes |

## Data Flow

### Write Path

```
Client calls SetAsync(key, value)
  │
  ├─ 1. WAL.AppendAsync(key, value)        ← fsync to disk (durability guarantee)
  │
  ├─ 2. MemTable.SetAsync(key, value)       ← O(log M) balanced tree insert
  │
  └─ 3. If MemTable is full:
       ├─ Flush MemTable → new on-disk segment file (prefix_NNNNNN.dat)
       ├─ Build Bloom filter for new segment (if factory provided)
       ├─ WAL.TruncateAsync()               ← data is safe on disk, WAL no longer needed
       └─ If compaction threshold reached:
            └─ CompactAsync()               ← merge all segments into one
```

### Read Path

```
Client calls TryGetValueAsync(key)
  │
  ├─ 1. Check MemTable tombstones           ← if tombstoned, return not-found
  ├─ 2. Check MemTable                      ← most recent writes are here
  │
  └─ 3. Search on-disk segments (newest → oldest):
       For each segment:
         ├─ Check segment tombstones
         ├─ Check Bloom filter              ← if definite miss, skip this segment
         └─ Scan segment file               ← sequential read through entries
       First match wins (newest data takes precedence)
```

### Compaction

```
CompactAsync()
  │
  ├─ 1. Read all entries from all segments (oldest → newest)
  ├─ 2. Sorted merge with last-writer-wins deduplication
  ├─ 3. Strip tombstoned keys
  ├─ 4. Write merged result to new segment file
  ├─ 5. Build new Bloom filter
  └─ 6. Delete old segment files (crash-safe: new file written before old deleted)
```

### Startup Recovery

```
Constructor
  │
  ├─ 1. Scan segment folder for existing prefix_*.dat files
  ├─ 2. Load segments in sorted filename order (monotonic counter preserves creation order)
  └─ 3. If WAL provided:
       └─ Replay WAL entries into MemTable (recovers writes since last flush)
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
| Understand transactions | `IMvccStore`, `ISsiStore` | `MvccStore.cs` (snapshot isolation), `SsiStore.cs` (serializable) |
| Understand replication | `ISingleLeaderReplication`, `IQuorumReplication` | Single-leader vs Dynamo-style W+R>N quorum |
| Understand consensus | `IRaftNode`, `IRaftCluster` | Leader election + log replication |
| Understand CRDTs | `IGCounter`, `IORSet` | Grow-only → add-wins observed-remove progression |
| Add a source-generated key type | Annotate with `[Key]` | `StorageSourceGenerator.cs`, `CodeGenerator.cs` |
| Add a source-generated value type | Annotate with `[StorageSerializable]` | `StorageSourceGenerator.cs`, `CodeGenerator.cs` |
| Customize generated output | `[assembly: StorageDefaults(...)]` | `StorageDefaultsAttribute.cs` |
| Understand generator diagnostics | `DiagnosticDescriptors.cs` | BSSG001–BSSG006, `StorageDiagnosticSuppressor.cs` |
| Run benchmarks | `benchmarks/StorageEngine/Program.cs` | 12 benchmark classes covering all major components |

## Directory Structure

```
src/
  Domain/
    Interfaces/          59 interface files
    Helpers/             SerializableWrapper, Guard
    ValueObjects/        FileLocation, SsTableMetadata, VersionedValue
    Exceptions/          Domain-specific exception types
    Enums/               NodeColor, RaftState
  Infrastructure/
    LsmTree/             LsmStorageEngine + 3 compaction strategies
    AppendOnlyFileStorage/  AppendOnlyFileStorageEngine (+ WithIndex variant)
    LogSegmentFileStorage/  LogSegmentedStorageEngine
    KeyValueStore/       InMemoryKeyValueStore, StorageFile
    WriteAheadLog/       WriteAheadLog (CRC32, fsync, corruption-tolerant recovery)
    SortedStringTable/   SortedStringTable (atomic writes, sparse index, GZip)
    DataStructures/      BTree, BPlusTree, RedBlackTree, SkipListMemTable,
                         BloomFilter, CountingBloomFilter, MerkleTree
    Algorithms/          Fnv1aHash, Murmur3, XxHash32
    Serialization/       BinaryEntrySerializer, CsvEntrySerializer
    Partitioning/        ConsistentHashRing, RangePartitioner, HashPartitioner, RendezvousHash
    Transactions/        MvccStore, SsiStore
    Replication/         SingleLeaderReplication, QuorumReplication, ReplicationLog
    Consensus/           RaftNode, RaftCluster
    DistributedSystems/  GCounter, PNCounter, GSet, ORSet, VectorClock,
                         LamportTimestamp, GossipProtocol
    Indexing/            SecondaryIndex, InMemoryStorageIndex, InMemoryFileIndex
    StorageWithBloomFilter/  BulkKeyValueStoreWithBloomFilter
  SourceGenerator/       Roslyn IIncrementalGenerator (netstandard2.0)
    StorageSourceGenerator.cs   Pipeline: attribute discovery → type extraction → code emission
    CodeGenerator.cs            Emits ISerializable, IComparable, IEquatable, operators
    DiagnosticDescriptors.cs    BSSG001–BSSG006 diagnostic rules
    StorageDiagnosticSuppressor.cs  Suppresses CA1036/S1210 on [Key] types
    EquatableArray.cs           Value-equal array wrapper for pipeline caching
    TypeToGenerate.cs           Pipeline data model (no Roslyn types)
tests/
  Infrastructure/        660 xUnit tests
  SourceGenerator/       42 generator tests (snapshots, diagnostics, integration, caching)
benchmarks/
  StorageEngine/         12 benchmark classes (storage engines, data structures,
                         serializers, SSTable, Bloom filter, WAL, compaction)
  Hashing/               Hash algorithm benchmarks (FNV-1a, Murmur3, xxHash32)
docs/
  PITFALLS.md            14 bug categories with fix patterns and test evidence
```
