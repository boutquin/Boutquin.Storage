# Boutquin.Storage

[![NuGet](https://img.shields.io/nuget/v/Boutquin.Storage.Domain.svg)](https://www.nuget.org/packages/Boutquin.Storage.Domain)
[![License](https://img.shields.io/github/license/boutquin/Boutquin.Storage)](https://github.com/boutquin/Boutquin.Storage/blob/main/LICENSE.txt)
[![Build](https://github.com/boutquin/Boutquin.Storage/actions/workflows/pr-verify.yml/badge.svg)](https://github.com/boutquin/Boutquin.Storage/actions/workflows/pr-verify.yml)

A collection of data storage algorithms and engines implemented in C#, inspired by Martin Kleppmann's [Designing Data-Intensive Applications](https://dataintensive.net/). Built with clean architecture, .NET 10, and strict code quality standards.

## Solution Structure

| Project | NuGet Package | Description |
|---------|---------------|-------------|
| **Boutquin.Storage.Domain** | `Boutquin.Storage.Domain` | Interfaces, value objects, exceptions, and abstractions for storage engines |
| **Boutquin.Storage.Infrastructure** | `Boutquin.Storage.Infrastructure` | Concrete implementations of algorithms, data structures, and storage engines |
| **Boutquin.Storage.Infrastructure.Tests** | — | 660 unit tests (xUnit, FluentAssertions, Moq) |
| **Boutquin.Storage.SourceGenerator** | — | Roslyn IIncrementalGenerator for `[Key]` and `[StorageSerializable]` types |
| **Boutquin.Storage.SourceGenerator.Tests** | — | 42 generator tests (snapshots, diagnostics, integration, caching) |
| **Boutquin.Storage.Samples** | — | Usage examples and demonstrations |
| **Boutquin.Storage.BenchMark** | — | Performance benchmarks for storage engines (BenchmarkDotNet) |
| **Boutquin.Storage.BenchMark.Hashing** | — | Performance benchmarks for hash algorithms |

## Features

### Storage Engines
- **LSM Storage Engine** — Full Log-Structured Merge-tree with MemTable, WriteAheadLog integration, startup recovery, tombstone deletes, range queries, per-segment Bloom filters, pluggable compaction strategies (`ICompactionStrategy`), and on-disk segments (DDIA Ch. 3)
- **Append-only file storage** — Durable, sequential-write key-value store with compaction
- **Append-only file storage with index** — Adds in-memory indexing for O(1) lookups
- **Log-segmented storage** — Segments the append-only log for independent compaction and garbage collection
- **In-memory key-value store** — `SortedDictionary`-backed store with LSM-tree MemTable semantics

### Data Structures
- **B-tree** — Balanced search tree with configurable branching factor for disk-friendly indexing (DDIA Ch. 3)
- **Red-Black tree** — Self-balancing BST for ordered key-value storage (MemTable backing)
- **Sorted String Table (SSTable)** — Immutable, sorted on-disk key-value segments with sparse index, atomic writes (write-to-temp-then-rename), sparse index persistence (.idx files), and optional GZip compression (DDIA Ch. 3)
- **WriteAheadLog** — Crash-recovery log with CRC32 checksums, fsync durability, and corruption-tolerant recovery (DDIA Ch. 3)
- **Merkle tree** — Hash tree for efficient data integrity verification and anti-entropy repair (DDIA Ch. 5)
- **Bloom filter** — Probabilistic membership testing with configurable false-positive rate (DDIA Ch. 3)

### Algorithms
- **FNV-1a** — Fast 32-bit hash (baseline, hash-table lookups)
- **Murmur3** — High-quality 32-bit hash (Bloom filter primary hash)
- **xxHash32** — Near-RAM-speed 32-bit hash (Bloom filter secondary hash), validated against `System.IO.Hashing.XxHash32`

### Data Structures (continued)
- **B+ tree** — Leaf-linked B-tree variant for efficient range queries via leaf chain traversal (DDIA Ch. 3)
- **Skip list** — Probabilistic balanced structure alternative to Red-Black trees for MemTable backing (DDIA Ch. 3)
- **Counting Bloom filter** — Bloom filter variant supporting element removal via integer counters (DDIA Ch. 3)
- **Secondary index** — Document-partitioned local index with `SemaphoreSlim`-guarded thread safety (DDIA Ch. 6)

### Partitioning (DDIA Ch. 6)
- **Consistent hash ring** — Virtual-node-based ring for minimal key redistribution on node changes (Dynamo, Cassandra)
- **Range partitioner** — Binary search on sorted boundaries for range-query-friendly partitioning (Bigtable, HBase)
- **Hash partitioner** — Modular hash-based partitioning for uniform key distribution
- **Rendezvous hash** — Highest-random-weight hashing; simpler than consistent hashing with no virtual nodes

### Transactions (DDIA Ch. 7)
- **MVCC store** — Multi-version concurrency control with version chains, snapshot isolation, and invisible writes
- **SSI store** — Serializable Snapshot Isolation detecting read-write and write-read dependencies at commit time

### Replication (DDIA Ch. 5)
- **Single-leader replication** — Leader writes to store + replication log; followers maintain high-water marks for catch-up
- **Quorum replication** — Dynamo-style W + R > N quorum with read repair for stale replicas
- **Replication log** — Ordered log for follower catch-up via sequence numbers

### CRDTs (DDIA Ch. 5)
- **GCounter** — Grow-only counter with per-node state; merge via element-wise max
- **PNCounter** — Increment/decrement counter composed of two GCounters (positive minus negative)
- **GSet** — Grow-only set; merge is set union
- **ORSet** — Observed-remove set with unique tags per add; add-wins semantics on concurrent operations

### Ordering & Consensus (DDIA Ch. 8–9)
- **Vector clock** — Detect concurrent events across nodes; merge via element-wise max (Dynamo-style)
- **Lamport timestamp** — Lock-free total ordering with `Interlocked.CompareExchange`; tie-break by node ID
- **Gossip protocol** — Eventually consistent state dissemination with version-based merge
- **Raft consensus** — Leader election and log replication with election safety and log matching invariants (Ongaro & Ousterhout, 2014)

### Source Generator
- **Roslyn IIncrementalGenerator** — Automatic code generation for `[Key]` and `[StorageSerializable]` record structs. Emits `ISerializable<T>`, `IComparable<T>`, `IEquatable<T>`, comparison operators, and binary serialization/deserialization — no hand-written boilerplate needed. Supports nested types, collections, all 8 primitive types, and assembly-level defaults via `[StorageDefaults]`. Six diagnostic rules (BSSG001–BSSG006) with actionable messages.

### Serialization
- **Binary serializer** — Compact binary format with synchronous and asynchronous APIs
- **CSV serializer** — RFC 4180-compliant text format with quote-doubling

## Quick Start

### Installation

```sh
dotnet add package Boutquin.Storage.Domain
dotnet add package Boutquin.Storage.Infrastructure
```

### LSM Storage Engine

```csharp
using Boutquin.Storage.Domain.Helpers;
using Boutquin.Storage.Infrastructure.DataStructures;
using Boutquin.Storage.Infrastructure.LsmTree;
using Boutquin.Storage.Infrastructure.WriteAheadLog;

// Create a full LSM engine with WriteAheadLog, Bloom filters, tombstones, and auto-compaction
var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
using var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>("data/wal.log");
var strategy = new FullCompactionStrategy(threshold: 4);

using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
    memTableCapacity: 1000,
    segmentFolder: "data",
    segmentPrefix: "seg",
    entrySerializer: serializer,
    writeAheadLog: wal,
    enableTombstones: true,
    bloomFilterFactory: () => new BloomFilter<SerializableWrapper<int>>(1000, 0.01),
    compactionStrategy: strategy);

await engine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("hello"));
await engine.SetAsync(new SerializableWrapper<int>(2), new SerializableWrapper<string>("world"));

var (value, found) = await engine.TryGetValueAsync(new SerializableWrapper<int>(1));
// found == true, value.Value == "hello"

await engine.FlushAsync(); // Force MemTable flush to SSTable on disk

// Range query: get all keys in [1, 2] inclusive
var range = await engine.GetRangeAsync(new SerializableWrapper<int>(1), new SerializableWrapper<int>(2));

// Delete a key (tombstone)
await engine.RemoveAsync(new SerializableWrapper<int>(1));

// Compact: merges segments, strips tombstones, rebuilds Bloom filters
await engine.CompactAsync();
```

### Write-Ahead Log

```csharp
using Boutquin.Storage.Infrastructure.WriteAheadLog;

// WriteAheadLog provides crash-recovery durability for MemTable writes
using var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>("data/wal.log");

await wal.AppendAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("value1"));
await wal.AppendAsync(new SerializableWrapper<int>(2), new SerializableWrapper<string>("value2"));

// After a crash, recover all valid entries (corrupt entries are skipped)
var entries = await wal.RecoverAsync();

// After successful flush to SSTable, truncate the WriteAheadLog
await wal.TruncateAsync();
```

### B-tree

```csharp
using Boutquin.Storage.Infrastructure.DataStructures;

var btree = new BTree<int, string>(minimumDegree: 3);

await btree.SetAsync(10, "ten");
await btree.SetAsync(20, "twenty");
await btree.SetAsync(5, "five");

var (value, found) = await btree.TryGetValueAsync(10);
// found == true, value == "ten"
// Height and Order properties available for inspection
```

### Sorted String Table (SSTable)

```csharp
using Boutquin.Storage.Infrastructure.SortedStringTable;

var sst = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
    "data", "segment_001.dat");

// Input MUST be sorted — unsorted input throws ArgumentException
var items = new List<KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>>
{
    new(new SerializableWrapper<int>(1), new SerializableWrapper<string>("alpha")),
    new(new SerializableWrapper<int>(2), new SerializableWrapper<string>("bravo")),
    new(new SerializableWrapper<int>(3), new SerializableWrapper<string>("charlie")),
};
sst.Write(items);

bool found = sst.TryGetValue(new SerializableWrapper<int>(2), out var value);
// found == true, value.Value == "bravo"
```

### Merkle Tree

```csharp
using Boutquin.Storage.Infrastructure.DataStructures;

var tree = new MerkleTree();

var blocks = new List<byte[]>
{
    "block1"u8.ToArray(),
    "block2"u8.ToArray(),
    "block3"u8.ToArray(),
    "block4"u8.ToArray(),
};

tree.Build(blocks);
byte[] rootHash = tree.RootHash; // Defensive copy returned

// Verify data integrity
bool isValid = tree.Verify(blocks); // true

// Generate and verify inclusion proof for a single block
var proof = tree.GetProof(leafIndex: 1);
bool proofValid = MerkleTree.VerifyProof(
    leafHash: SHA256.HashData("block2"u8),
    proof: proof,
    rootHash: rootHash,
    leafIndex: 1);
```

### Bloom Filter with Double Hashing

```csharp
using Boutquin.Storage.Infrastructure.DataStructures;

var filter = new BloomFilter<string>(expectedElements: 1000, falsePositiveProbability: 0.01);
filter.Add("key1");
filter.Add("key2");

bool maybeExists = filter.Contains("key1"); // true (definitely added)
bool probablyNot = filter.Contains("key3"); // false (definitely not added, or rare false positive)
```

### Source Generator

```csharp
using Boutquin.Storage.Domain.Attributes;

// Mark a key type — the generator produces ISerializable<T>, IComparable<T>,
// IEquatable<T>, equality operators, and comparison operators automatically.
[Key]
public partial record struct CityKey(string Country, long Id);

// Mark a value type — the generator produces ISerializable<T> only.
[StorageSerializable]
public partial record struct CityValue(string Name, int Population);

// Use with any storage engine — no SerializableWrapper needed:
await engine.SetAsync(new CityKey("US", 1), new CityValue("New York", 8_336_817));
```

To customize generated output at the assembly level:

```csharp
[assembly: StorageDefaults(GenerateComparisonOperators = false)]
```

## Architecture

```
┌────────────────────────────────────────────────────────────────────┐
│                          Domain Layer                              │
│  Storage: ILsmStorageEngine, IBTree, IBPlusTree, ISortedStringTable│
│  Structures: IRedBlackTree, ISkipListMemTable, IBloomFilter,       │
│              ICountingBloomFilter, IMerkleTree, IWriteAheadLog     │
│  Distributed: IConsistentHashRing, IRangePartitioner, IVectorClock │
│  Replication: ISingleLeaderReplication, IQuorumReplication         │
│  Consensus: IRaftNode, IRaftCluster                                │
│  CRDTs: IGCounter, IPNCounter, IGSet, IORSet                       │
│  Transactions: IMvccStore, ISsiStore                               │
│  Value Objects: FileLocation, SsTableMetadata, VersionedValue      │
└───────────────────────────┬────────────────────────────────────────┘
                            │ depends on
┌───────────────────────────▼────────────────────────────────────────┐
│                     Infrastructure Layer                           │
│  Storage Engines: LsmStorageEngine, AppendOnly, LogSegmented       │
│  Data Structures: BTree, BPlusTree, RedBlackTree, SkipListMemTable │
│  Probabilistic: BloomFilter, CountingBloomFilter, MerkleTree       │
│  Partitioning: ConsistentHashRing, RangePartitioner, HashPartioner │
│  Replication: SingleLeaderReplication, QuorumReplication           │
│  Consensus: RaftNode, RaftCluster                                  │
│  CRDTs: GCounter, PNCounter, GSet, ORSet, VectorClock              │
│  Transactions: MvccStore, SsiStore                                 │
│  Algorithms: FNV-1a, Murmur3, xxHash32                             │
│  Serialization: Binary, CSV                                        │
└────────────────────────────────────────────────────────────────────┘
┌────────────────────────────────────────────────────────────────────┐
│                     Source Generator (Compile-time)                 │
│  StorageSourceGenerator (IIncrementalGenerator)                     │
│  Attributes: [Key], [StorageSerializable], [StorageDefaults]        │
│  Emits: ISerializable<T>, IComparable<T>, IEquatable<T>, operators  │
│  Diagnostics: BSSG001–BSSG006                                      │
└────────────────────────────────────────────────────────────────────┘
```

The architecture follows the dependency inversion principle from [Designing Data-Intensive Applications](algorithms-in-designing-data-intensive-applications.md) — the Domain layer defines contracts, and Infrastructure provides implementations that can be swapped independently.

For detailed architecture including the interface hierarchy, LSM engine composition, data flow diagrams, and component navigation, see [ARCHITECTURE.md](ARCHITECTURE.md).

## Complexity Reference

Every interface documents Big-O complexity. Summary:

| Component | Write | Read | Space | Reference |
|-----------|-------|------|-------|-----------|
| **LSM Engine** | O(log M) | O(log M + k log S) | O(n) to O(kn) | DDIA Ch. 3 |
| **LSM Compaction** | O(N) compact | — | O(N) | DDIA Ch. 3 |
| **B-tree** | O(log_t n) | O(log_t n) | O(n) | DDIA Ch. 3 |
| **B+ tree** | O(log_t n) | O(log_t n + k) range | O(n) | DDIA Ch. 3 |
| **Skip list** | O(log n) expected | O(log n) expected | O(n) | DDIA Ch. 3 |
| **SSTable** | O(S) | O(log S) | O(S) | DDIA Ch. 3 |
| **WriteAheadLog** | O(S) append | O(F) recover | O(F) | DDIA Ch. 3 |
| **Merkle tree** | O(n) build | O(log n) proof | O(n) | DDIA Ch. 5 |
| **Red-Black tree** | O(log n) | O(log n) | O(n) | DDIA Ch. 3 |
| **Bloom filter** | O(k) add | O(k) contains | O(m) | DDIA Ch. 3 |
| **Hash (xxHash32)** | — | O(n) | O(1) | xxHash spec |
| **Consistent hash ring** | O(v log n) add | O(log n) lookup | O(vn) | DDIA Ch. 6 |
| **Vector clock** | O(1) increment | O(n) compare | O(n) | DDIA Ch. 8 |
| **Raft consensus** | O(n) propose | O(1) read log | O(n) | DDIA Ch. 9 |

## Implementation Pitfalls

See [docs/PITFALLS.md](docs/PITFALLS.md) for a catalog of 15 bug categories discovered during code review, including:
- Hash algorithm spec fidelity (XOR vs ADD)
- File I/O durability (`Flush` vs `fsync`)
- Endianness portability
- Corruption recovery semantics
- Thread safety and dispose patterns
- StreamReader buffering and UTF-8 BOM pollution in serialization

Each pitfall includes the trap, why it's wrong, the fix pattern, and test references.

## Contributing

Contributions are welcome! Please read the [contributing guidelines](CONTRIBUTING.md) and [code of conduct](CODE_OF_CONDUCT.md) first.

### Reporting Bugs

If you find a bug, please report it by opening an issue on the [Issues](https://github.com/boutquin/Boutquin.Storage/issues) page with:

- A clear and descriptive title
- Steps to reproduce the issue
- Expected and actual behavior
- Screenshots or code snippets, if applicable

### Contributing Code

1. Fork the repository and clone locally
2. Create a feature branch: `git checkout -b feature-name`
3. Make your changes following the [style guides](CONTRIBUTING.md)
4. Commit with clear messages: `git commit -m "Add feature X"`
5. Push and open a pull request

## License

This project is licensed under the Apache 2.0 License — see the [LICENSE](LICENSE.txt) file for details.

## Contact

For inquiries, please open an issue or reach out via [GitHub Discussions](https://github.com/boutquin/Boutquin.Storage/discussions).

## Acknowledgments

- [Martin Kleppmann](https://martin.kleppmann.com/) for [Designing Data-Intensive Applications](https://dataintensive.net/)
- [Algorithms reference](algorithms-in-designing-data-intensive-applications.md) — chapter-by-chapter breakdown of DDIA algorithms implemented in this project
