# Boutquin.Storage

[![NuGet](https://img.shields.io/nuget/v/Boutquin.Storage.Domain.svg)](https://www.nuget.org/packages/Boutquin.Storage.Domain)
[![License](https://img.shields.io/github/license/boutquin/Boutquin.Storage)](https://github.com/boutquin/Boutquin.Storage/blob/main/LICENSE.txt)
[![Build](https://github.com/boutquin/Boutquin.Storage/actions/workflows/pr-verify.yml/badge.svg)](https://github.com/boutquin/Boutquin.Storage/actions/workflows/pr-verify.yml)

A collection of data storage algorithms and engines implemented in C#, inspired by Martin Kleppmann and Chris Riccomini's [Designing Data-Intensive Applications](https://dataintensive.net/). Built with clean architecture, .NET 10, and strict code quality standards.

> **Version:** this documentation describes Storage 2.0.1. The package badge reflects the latest published package; the installation commands below explicitly target 2.0.1. Check NuGet for package availability.

The [concept coverage map](docs/DDIA-COVERAGE-MAP.md), [trade-off guide](docs/TRADEOFFS.md),
[runnable examples](docs/SAMPLES.md), and [delivery status](docs/ROADMAP.md) connect the components
to their demonstrated behavior and limits.

Unqualified DDIA chapter references below refer to the first edition; references marked **2e** refer to the second edition, coauthored by Chris Riccomini.

## Solution Structure

| Project | NuGet Package | Description |
|---------|---------------|-------------|
| **Boutquin.Storage.Domain** | `Boutquin.Storage.Domain` | Production-strength contracts: interfaces, value objects, exceptions, and abstractions for storage engines |
| **Boutquin.Storage.Infrastructure** | `Boutquin.Storage.Infrastructure` | Production-strength implementations: algorithms, data structures, LSM + SSTable + WAL + columnar time-series engines |
| **Boutquin.Storage.Distributed** | — (in-repo, not on NuGet) | **In-process reference** implementations of Raft, coordination, replication and causal sessions, sharding, durable transaction patterns, local-first sync, simulation, MVCC / Snapshot Isolation, CRDTs, clocks, gossip, and Merkle repair. `<IsPackable>false</IsPackable>` — consumed via `<ProjectReference>` only. See [`src/Distributed/README.md`](src/Distributed/README.md) |
| **Boutquin.Storage.Infrastructure.Tests** | — | Unit tests (xUnit, FluentAssertions, Moq) covering Infrastructure + Distributed |
| **Boutquin.Storage.SourceGenerator** | `Boutquin.Storage.SourceGenerator` | Packable Roslyn `IIncrementalGenerator` for `[Key]`, `[StorageSerializable]`, and `[ColumnarStorable]` types. Its NuGet package installs the generator and bundled `Boutquin.Storage.SourceGenerator.CodeFixes` assembly from `analyzers/dotnet/cs`; the code-fix project is not published separately |
| **Boutquin.Storage.SourceGenerator.Tests** | — | Generator tests (snapshots, diagnostics, integration, caching, tagged/ordered wire runtime tests, byte-level snapshot fixtures, schema-evolution matrix, 10k-tuple property-based byte-lex sort invariant, generic-outer constraint emission) |
| **Boutquin.Storage.ArchitectureTests** | — | Architecture tests (naming, design rules, dependency enforcement, columnar-namespace isolation, Distributed-assembly one-way dependency) |
| **Boutquin.Storage.Samples** | — | Usage examples and demonstrations |
| **Boutquin.Storage.BenchMark** | — | Performance benchmarks for storage engines (BenchmarkDotNet) |
| **Boutquin.Storage.BenchMark.Hashing** | — | Performance benchmarks for hash algorithms |

## Features

> **Scope legend.** Features below are tagged **(b)** production-strength — live in
> `Boutquin.Storage.Domain` and `Boutquin.Storage.Infrastructure`, shipped to NuGet under
> `Boutquin.Storage.*` — or **(a)** in-process reference, doc-corrected — live in
> `Boutquin.Storage.Distributed` (`<IsPackable>false</IsPackable>`, consumed via
> `<ProjectReference>` only). The reference tier is faithful to the DDIA chapter but
> applies explicit per-module limits. Several references are volatile, while the transaction
> journal and local-first replica are file-backed; durability does not add network transport,
> replicated failover, authentication, or dynamic membership. See
> [`src/Distributed/README.md`](src/Distributed/README.md) for the exact boundaries and
> pointers to production alternatives.

### Stream processing primitives

- Event-time tumbling, sliding and session windows support watermarks and explicit late-event revisions. CDC tables enrich joins; event logs give derived output stable replay positions. At-least-once and idempotent-sink processors checkpoint state and offset together. The [streaming guide](docs/STREAMING-AND-DERIVED-DATA.md) explains finite replay, retained state, and each delivery boundary.

### Analytics and change capture (b)

- Typed column-batch operators, materialized aggregate refresh/rebuild and data cubes compose with compressed object-store data. [Analytics](docs/analytics.md) records the measured materialization cost. [CDC pipelines](docs/CDC-PIPELINES.md) connect journal-first LSM and leader changes to replay-safe read models, text indexes and analytics through snapshot/tail handoff.

### Text and vector search (b)

- Inverted text indexes with configurable analysis and TF-IDF ranking; exact flat, IVF and HNSW vector indexes with cosine, Euclidean and dot-product distance. The [search guide](docs/search-and-vector-indexes.md) includes measured recall/latency trade-offs and a runnable example.

### Replication and causal sessions (a)

- Multi-leader conflict policies and leaderless response thresholds expose replication, session, hint and repair trade-offs. The [replication guide](docs/REPLICATION.md) includes real simulated faults and explains volatile state, sloppy response sets and causal availability limits.

### Local-first sync (a)

- Durable per-device journals and peer checkpoints exchange offline deltas through deterministic conflict resolvers. Duplicate and overlapping delivery is idempotent; future checkpoint gaps fail before mutation. Optional G-counter and observed-remove-set adapters reuse the existing CRDT semantics. The [local-first sync guide](docs/local-first-sync.md) explains restart, retention and uncertain-save boundaries.

### Sharding operations (a)

- Versioned external maps, persisted assignments, minimal-churn rebalance plans, salted hot keys and local/global index routing make placement trade-offs executable. The [sharding guide](docs/sharding-operations.md) distinguishes metadata cutover from data movement.

### Durable transactions and messaging (a)

- File-backed two-phase commit, recovered prepared locks, and one-record outbox/inbox/idempotency make failure boundaries explicit. Read-committed and strict-locking stores demonstrate isolation differences. The [transaction guide](docs/TRANSACTIONS.md) covers recovery, cross-shard visibility and runnable failure examples.

### Coordination (a)

- Committed Raft barriers support registers, revision-based CAS, watches, IDs, fencing tokens and ephemeral leases. The [coordination guide](docs/COORDINATION.md) covers quorum failures, retries, shared-clock assumptions and the shard-ownership demo.

### Protobuf evolution (b)

- Registry-versioned frames carry standard Protobuf payloads with bounded decoding and unknown-field preservation. The [schema evolution guide](docs/SCHEMA-EVOLUTION.md) demonstrates field addition, renaming, deprecation and reservation across three generated schemas.

### Deterministic simulation (a)

- Virtual time, seeded network faults, reusable invariants, real Raft execution and a bounded history checker. The [distributed testing guide](docs/TESTING-DISTRIBUTED-SYSTEMS.md) explains the model and executable demonstration.

### Event projections (b)

- **Checkpointed projection runner** — deterministic folds over an event log, atomic state/offset checkpoints, separate rebuilds, and idempotent external-handler contracts. The [projection guide](docs/event-projections.md) explains recovery boundaries and includes runnable counter and searchable read-model examples.

### Batch and dataflow (b)

- External merge sorting bounds run size and merge fan-in; file shuffle, grouping/joins and validated task graphs build derived views. The [batch guide](docs/BATCH-DATAFLOW.md) includes the executable aggregate example, measured disk-sort cost and memory/I/O boundaries.

### Storage Engines (b)
- **LSM Storage Engine** — Full Log-Structured Merge-tree with MemTable, WriteAheadLog integration, startup recovery, tombstone deletes, range queries, per-segment Bloom filters, pluggable compaction strategies (`ICompactionStrategy`), and pluggable on-disk segment formats (`ISegmentFormat`) — row-oriented by default, column-oriented for analytical time-series workloads (DDIA Ch. 3)
- **WiscKey-style key-value separation** — `SetAsync` routes values beyond `ValueLogOptions.ValueLogThresholdBytes` to an append-only, CRC-protected value log; SSTable block entries carry `ValueKind.ValueLogPointer` through flush and compaction without re-inlining; reads transparently dereference via `ValueLogReader`; `CompactValueLogAsync()` runs whole-file garbage collection honouring live-snapshot pins. Avoids rewriting large values during SSTable compaction; value-log garbage collection still rewrites live values. The separation follows the [WiscKey paper](https://www.usenix.org/system/files/conference/fast16/fast16-papers-lu.pdf). See [`docs/wisckey-value-log.md`](docs/wisckey-value-log.md).
- **Append-only file storage** — Durable, sequential-write key-value store with compaction
- **Append-only file storage with index** — Adds in-memory indexing for O(1) lookups
- **Log-segmented storage** — Segments the append-only log for independent compaction and garbage collection
- **In-memory key-value store** — `SortedDictionary`-backed store with LSM-tree MemTable semantics

### Data Structures (b)
- **B-tree** — Balanced search tree with configurable branching factor for disk-friendly indexing (DDIA Ch. 3)
- **Red-Black tree** — Self-balancing BST for ordered key-value storage (MemTable backing)
- **Sorted String Table (SSTable)** — Immutable, sorted on-disk key-value segments with a persisted block index and Bloom filter in the footer, atomic writes (write-to-temp-then-rename), and configurable block compression (DDIA Ch. 3)
- **WriteAheadLog** — Crash-recovery log with CRC32C-protected frames, fsync durability, segment seals, and fail-loud recovery that repairs only an interrupted final write (DDIA Ch. 3)
- **Merkle tree (a)** — Hash tree for efficient data integrity verification and anti-entropy repair (DDIA Ch. 5). *Lives in `Boutquin.Storage.Distributed.Merkle`.*
- **Bloom filter** — Probabilistic membership testing with configurable false-positive rate (DDIA Ch. 3). Row and columnar segments support capacity/false-positive targets, disabling filters, and custom persisted codecs; see [segment Bloom filters](docs/segment-bloom-filters.md).

### Algorithms
- **FNV-1a** — Fast 32-bit hash (baseline, hash-table lookups)
- **Murmur3** — High-quality 32-bit hash (Bloom filter primary hash)
- **xxHash32** — Non-cryptographic 32-bit hash (Bloom filter secondary hash), validated against `System.IO.Hashing.XxHash32`

### Data Structures (continued)
- **B+ tree** — Leaf-linked B-tree variant for efficient range queries via leaf chain traversal (DDIA Ch. 3)
- **Skip list** — Probabilistic balanced structure alternative to Red-Black trees for MemTable backing (DDIA Ch. 3)
- **Counting Bloom filter** — Bloom filter variant supporting element removal via integer counters (DDIA Ch. 3)
- **Secondary index** — Document-partitioned local index with `SemaphoreSlim`-guarded thread safety (DDIA Ch. 6)

### Partitioning (DDIA Ch. 6) (a)
- **Consistent hash ring** — Virtual-node-based ring for minimal key redistribution on node changes (Dynamo, Cassandra)
- **Range partitioner** — Binary search on sorted boundaries for range-query-friendly partitioning
- **Hash partitioner** — Modular hash-based partitioning for uniform key distribution
- **Rendezvous hash** — Highest-random-weight hashing; simpler than consistent hashing with no virtual nodes

### Transactions (DDIA Ch. 7) (a)
- **MVCC store** (`MvccStore`) — Multi-version concurrency control with version chains, snapshot isolation, and invisible writes
- **Snapshot-isolation store** (`SnapshotIsolationStore`) — plain Snapshot Isolation: readers see a consistent snapshot; writers detect concurrent point-key writes at commit time and abort one. It does *not* implement Serializable Snapshot Isolation (no predicate-range tracking, so write skew is not prevented)

### Replication (DDIA Ch. 5) (a)
- **Single-leader replication** — Leader writes to store + replication log; followers maintain high-water marks for catch-up
- **Quorum replication** — Dynamo-style W + R > N quorum with read repair for stale replicas
- **Replication log** — Ordered log for follower catch-up via sequence numbers

### CRDTs (DDIA Ch. 5) (a)
- **GCounter** — Grow-only counter with per-node state; merge via element-wise max
- **PNCounter** — Increment/decrement counter composed of two GCounters (positive minus negative)
- **GSet** — Grow-only set; merge is set union
- **ORSet** — Observed-remove set with unique tags per add; add-wins semantics on concurrent operations

### Ordering & Consensus (DDIA Ch. 8–9) (a)
- **Vector clock** — Detect concurrent events across nodes; merge via element-wise max (Dynamo-style)
- **Lamport timestamp** — Lock-free total ordering with `Interlocked.CompareExchange`; tie-break by node ID
- **Gossip protocol** — Eventually consistent state dissemination with version-based merge
- **Raft consensus** — Leader election and log replication with election safety and log matching invariants (Ongaro & Ousterhout, 2014)

### Source Generator (2.0.0)
- **Roslyn IIncrementalGenerator** — Automatic code generation for `[Key]` and `[StorageSerializable]` record structs. Emits `ISerializable<T>`, `IComparable<T>`, `IEquatable<T>`, comparison operators, and binary serialization/deserialization — no hand-written boilerplate needed.
- **Two wire formats, selected by annotation.** `[StorageSerializable]` values use a tagged-wire format in the Protobuf family (DDIA 2e Ch. 5): per-field tag + wire-type varint, ULEB128 integers, zigzag signed, length-prefixed strings, unknown-tag skip for schema evolution when tags and field types remain compatible. `[Key]` types use a byte-lex-comparable ordered wire format: BE + sign-bit-flipped integers, UTF-8 terminator-escaped strings, canonical BE `Guid`, 1-byte null discriminator. The invariant `byte-lex(encode(a), encode(b)) == CompareTo(a, b)` lets LSM SSTable binary search operate on raw byte spans.
- **22 supported primitive types** (Tier 1 numerics `sbyte`..`Int128`, `Half`..`double`; Tier 2 `bool`/`char`/`string`; Tier 3 BCL `DateOnly`/`TimeOnly`/`TimeSpan`/`DateTimeOffset`/`Guid`/`decimal`) plus **enums** (dispatched to underlying), **nullable value types**, `IEnumerable<T>` of any supported primitive, and nested `[StorageSerializable]` records.
- **Every property declares `[Tag(N)]`.** Tag 0 is reserved by the generator for the per-record schema-format version marker; a 2.0 reader throws `UnsupportedFormatVersionException` on data whose first field is not the marker. A 1.x-written payload usually fails earlier and with a different type — `EndOfStreamException` or `InvalidDataException` from the length-prefix and wire-type checks that run first — so the signal is any of the three (see the migration guide). Detection is best-effort: some 1.x byte patterns deserialize without any exception, so use an out-of-band marker (a directory name or version file) as the reliable 1.x signal. `[ReservedTag(N)]` on a type prevents accidental reuse of retired tag slots.
- **Packed collection encoding by default.** `IEnumerable<T>` properties where `T` is a Tier-1 numeric or `bool` serialise as one `LengthPrefixed` block with concatenated element bodies (no per-element tag, no count). `[Unpacked]` opts back into one tag-and-wire-type varint per element (Protobuf 2 `repeated` semantics); `[Packed]` is an explicit-intent no-op on default-packed element kinds. Readers accept both encodings on the same property.
- **`[DecimalScale(N)]` required on `[Key]` decimal properties** — canonicalises mantissa scale so byte-lex ordering matches `decimal.Compare` across cross-scale inputs (BSSG017).
- **Diagnostic rules BSSG001–BSSG026 (BSSG009 reserved-unshipped)** covering unsupported types, explicit exclusions (`DateTime`/`nint`/`nuint`), tag validation (missing/duplicate/out-of-range/reserved-collision), `[Key]`-shape constraints (no nested records, no collections, no nested-nullable), decimal scale canonicalisation, `[Packed]`/`[Unpacked]` mutual exclusion, `string?`-on-`[Key]` rejection, `[ColumnarStorable]` encoding and record-shape checks, unbounded allocations from decoded lengths, and annotated-type shapes the generated code cannot honour (generic annotated types, inherited public properties, types with no matching constructor, and non-`partial` containing types).
- **Wire format is publicly specified.** [`docs/wire-format-2.0.md`](docs/wire-format-2.0.md) is the byte-level reference for third-party decoders — framing, tag-and-wire-type varint, ULEB128 / zigzag, every Tier-1/2/3 well-known-type encoding, and the three collection wire shapes with worked examples.
- **Breaking change from 1.x.** Byte compatibility with 1.x persisted data is not preserved. See [`docs/migration-1x-to-2x.md`](docs/migration-1x-to-2x.md) for the migration playbook and [`docs/source-generator-type-reference.md`](docs/source-generator-type-reference.md) for the full type + diagnostic reference.

### Time-Series Storage (DDIA 2e Ch. 3, 4, 7)
- **`ITimeSeriesStore<TDatasetKey, TRecord>`** — time-series-shaped facade over a sorted, range-scannable storage engine. Dataset-first composite keys (`TimeSeriesKey<TDatasetKey>`) group each dataset contiguously; a heavily written dataset can still be a hotspot. Coverage and gap queries consult an advisory per-dataset manifest, with full-scan fallback.
- **Recency-biased tail reads** — alongside ascending `GetRangeAsync(dataset, from, to)`, `GetTailAsync(dataset, count, onOrBefore)` returns the newest `count` records in date-DESCENDING order (newest first), optionally as of a date (`onOrBefore`). It walks back from the newest covered date in exponentially widening, disjoint windows so only the tail's row groups are read — the right primitive for latest-close, trailing-N (SMA-200, 52-week high), and as-of lookups where "N trading days" is uncomputable as a calendar offset.
- **`InMemoryTimeSeriesStore` / `LsmTimeSeriesStore`** — reference (dictionary-backed) and durable (LSM-backed) implementations of the time-series facade.
- **Pluggable segment format (`ISegmentFormat<TKey, TValue>`)** — LSM-segment layout is a strategy the engine composes over. `SstSegmentFormat` (default, `.sst` extension) writes the v1 block-structured SSTable with per-entry sequence numbers, a block-index footer, and a bloom filter; `ColumnarSegmentFormat` (`.bsct` extension) writes column-oriented segments with per-dataset block indexing, CRC32C-guarded footer, and magic-guarded trailer. New formats slot in without touching memtable, WAL, or compaction machinery.
- **Column-oriented time-series segments** (DDIA 2e Ch. 4) — `IColumnarSegmentFormat<TRecord>` + `CompressedColumnarSegmentFormat<TRecord>` + `ColumnarSegmentFormat<TDatasetKey, TRecord>` form the columnar stack: per-column compression (Gorilla XOR for doubles, delta-of-delta for dates, dictionary for strings/low-cardinality decimals, bitmap-RLE for bools), date-range skip index per row group, and a per-dataset block envelope that lets one LSM segment carry many datasets while each dataset's records stay contiguous and column-oriented. See [docs/columnar-time-series.md](docs/columnar-time-series.md) for the worked example.
- **`ColumnarTimeSeriesEngineBuilder.BuildAsync<TDatasetKey, TRecord>`** — single-call async factory for the full columnar time-series stack. Assembles `CompressedColumnarSegmentFormat` → `ColumnarSegmentFormat` → `LsmStorageEngine` → `LsmTimeSeriesStorageEngine` → `LsmTimeSeriesStore`, enforcing the instance-sync invariant that manual five-step wiring leaves open. Returns a `Task<ColumnarTimeSeriesStack<TDatasetKey, TRecord>>` whose `.Store` property is the public `ITimeSeriesStore` surface and whose `.InnerEngine` exposes flush and compaction. This is the recommended wiring path for all columnar time-series workloads.
- **Row-format stores accept `[StorageSerializable]`-only records (new in 2.0.0)** — a dated record that is *not* `[ColumnarStorable]` (and so does not implement the generated `IColumnarRehydratable` rehydration contract) now instantiates `LsmTimeSeriesStore` / `LsmTimeSeriesStorageEngine` with the default row format and round-trips. The rehydration requirement lives only on the columnar construction path (`ColumnarTimeSeriesStoreOptions<TRecord>` / `ColumnarTimeSeriesEngineBuilder`), where it is enforced at compile time. Row-only records — option quotes, settlements, dividends — no longer need a columnar annotation they will never use.
- **Snapshot isolation on columnar stores (new in 2.0.0)** — `AcquireSnapshotAsync` is accepted on both the row and columnar formats. Isolation is enforced by engine-level segment pinning plus an at-acquisition memtable copy — a format-agnostic mechanism — not by per-entry sequence-number filtering, so a pinned columnar segment (immutable pre-acquisition state) keeps post-acquisition writes invisible to a live snapshot even across a compaction that supersedes it (the segment is parked, not deleted, until the snapshot disposes). Engine-level `SupportsSnapshots` is `true` for both formats; the format-level `ISegmentFormat.SupportsSnapshots` retains its narrow "carries a per-entry seqno slot" meaning (`false` for columnar) and is no longer the isolation gate. The columnar segment footer persists a per-segment max sequence number so a segment recovered from disk carries its real flush watermark. See [docs/snapshot-isolation.md](docs/snapshot-isolation.md).

### Serialization
- **Binary serializer** — Compact binary format with synchronous and asynchronous APIs
- **CSV serializer** — RFC 4180-compliant text format with quote-doubling

## Quick Start

### Installation

A 2.0 application that uses generated serializers installs all three matching-version packages.
For a source build, reference the repository projects or locally packed packages. The generator
requires a Roslyn 5.9 or newer compiler host. Use the SDK selected by `global.json`; other hosts must meet that compiler requirement.

```sh
dotnet add package Boutquin.Storage.Domain --version 2.0.1
dotnet add package Boutquin.Storage.Infrastructure --version 2.0.1
dotnet add package Boutquin.Storage.SourceGenerator --version 2.0.1
```

### LSM Storage Engine

```csharp
using Boutquin.Storage.Domain.Helpers;
using Boutquin.Storage.Domain.ValueObjects.LsmTree;
using Boutquin.Storage.Infrastructure.LsmTree;
using Boutquin.Storage.Infrastructure.WriteAheadLog;

// Create a full LSM engine with WriteAheadLog, per-segment Bloom filters, and
// auto-compaction. Tombstones are always on. The default segment
// format is SstSegmentFormat<TKey, TValue> — the block-structured SSTable with LZ4
// compression; its footer carries a durable per-segment Bloom filter, so no
// bloom-filter factory is supplied to the engine. The WAL takes a *directory*.
using var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>("data/wal");

// Configuration is threaded through one named LsmStorageEngineOptions record.
var options = new LsmStorageEngineOptions<SerializableWrapper<int>, SerializableWrapper<string>>(
    MemTableCapacity: 1000,
    SegmentFolder: "data",
    SegmentPrefix: "seg",
    SegmentFormat: new SstSegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>>(),
    WriteAheadLog: wal,
    CompactionStrategy: new FullCompactionStrategy<SerializableWrapper<int>>(threshold: 4));

// OpenAsync runs async startup (WAL replay, value-log open); the constructor does
// only cheap synchronous setup.
using var engine = await LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>
    .OpenAsync(options);

await engine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("hello"));
await engine.SetAsync(new SerializableWrapper<int>(2), new SerializableWrapper<string>("world"));

var (value, found) = await engine.TryGetValueAsync(new SerializableWrapper<int>(1));
// found == true, value.Value == "hello"

await engine.FlushAsync(); // Force MemTable flush to SSTable on disk

// Range query: stream all keys in [1, 2] inclusive (GetRangeAsync is IAsyncEnumerable).
await foreach (var (key, val) in engine.GetRangeAsync(
    new SerializableWrapper<int>(1), new SerializableWrapper<int>(2)))
{
    // key.Value, val.Value
}

// Delete a key (tombstone)
await engine.RemoveAsync(new SerializableWrapper<int>(1));

// Compact: merges segments, strips tombstones, rebuilds Bloom filters
await engine.CompactAsync();
```

### Write-Ahead Log

```csharp
using Boutquin.Storage.Infrastructure.WriteAheadLog;

// WriteAheadLog provides crash-recovery durability for MemTable writes.
// The constructor takes a *directory*; the log rolls across segment files inside it.
using var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>("data/wal");

// Each append carries the LSM-assigned monotonic sequence number alongside the entry.
await wal.AppendAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("value1"), sequenceNumber: 1);
await wal.AppendAsync(new SerializableWrapper<int>(2), new SerializableWrapper<string>("value2"), sequenceNumber: 2);

// After a crash, recover all valid entries (a torn tail is truncated). Each
// WalEntry carries its key, value, sequence number, and entry kind.
var entries = await wal.RecoverAsync();

// Once every appended entry is durable elsewhere, discard the whole log. The LSM engine does
// not do this after a flush: it reclaims by watermark with TruncateCompletedSegmentsAsync and
// calls TruncateAsync only from ClearAsync.
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
using Boutquin.Storage.Domain.ValueObjects.SortedStringTable;
using Boutquin.Storage.Infrastructure.SortedStringTable;

using var sst = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
    "data", "segment_001.sst");

// Write takes discriminated SegmentWriteEntry items, each carrying a per-entry
// sequence number. Use the Inline factory for in-table values (Pointer is for the
// WiscKey value-log path). Input MUST be sorted — unsorted input throws ArgumentException.
var items = new List<SegmentWriteEntry<SerializableWrapper<int>, SerializableWrapper<string>>>
{
    SegmentWriteEntry<SerializableWrapper<int>, SerializableWrapper<string>>.Inline(
        new SerializableWrapper<int>(1), new SerializableWrapper<string>("alpha"), sequenceNumber: 1),
    SegmentWriteEntry<SerializableWrapper<int>, SerializableWrapper<string>>.Inline(
        new SerializableWrapper<int>(2), new SerializableWrapper<string>("bravo"), sequenceNumber: 2),
    SegmentWriteEntry<SerializableWrapper<int>, SerializableWrapper<string>>.Inline(
        new SerializableWrapper<int>(3), new SerializableWrapper<string>("charlie"), sequenceNumber: 3),
};
sst.Write(items);

bool found = sst.TryGetValue(new SerializableWrapper<int>(2), out var value);
// found == true, value.Value == "bravo"
```

### Merkle Tree

```csharp
using Boutquin.Storage.Distributed.Merkle;

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

// Generate and verify an inclusion proof for a single block. The leaf hash must
// use the RFC 6962 leaf-domain separation (SHA-256(0x00 ‖ data)) — call
// MerkleTree.HashLeaf rather than hashing the raw block, or the proof won't match.
var proof = tree.GetProof(leafIndex: 1);
bool proofValid = MerkleTree.VerifyProof(
    leafHash: MerkleTree.HashLeaf("block2"u8.ToArray()),
    proof: proof,
    rootHash: rootHash,
    leafIndex: 1);
```

### Bloom Filter with Double Hashing

```csharp
using System.Text;
using Boutquin.Storage.Infrastructure.DataStructures;

// The filter needs to know how to turn an item into bytes to hash — pass an
// itemToBytes delegate (or a BloomFilterStreamSerializer<T> overload).
var filter = new BloomFilter<string>(
    expectedElements: 1000,
    falsePositiveProbability: 0.01,
    itemToBytes: s => Encoding.UTF8.GetBytes(s));
filter.Add("key1");
filter.Add("key2");

bool maybeExists = filter.Contains("key1"); // true (definitely added)
bool maybeKey3 = filter.Contains("key3"); // false means absent; true may be a false positive
```

### Source Generator

```csharp
using Boutquin.Storage.Domain.Attributes;

// Mark a key type — the generator produces ISerializable<T>, IComparable<T>,
// IEquatable<T>, equality operators, and comparison operators automatically.
// [Key] → byte-lex-comparable ordered wire. Every property declares a stable
// [Tag(N)]; tag 0 is reserved by the generator.
[Key]
public partial record struct CityKey(
    [property: Tag(1)] string Country,
    [property: Tag(2)] long Id);

// Mark a value type — the generator produces ISerializable<T> only.
// [StorageSerializable] → tagged wire with unknown-tag skip, enabling
// additive schema evolution when existing tags and field types stay compatible.
[StorageSerializable]
public partial record struct CityValue(
    [property: Tag(1)] string Name,
    [property: Tag(2)] int Population);

// With an LsmStorageEngine<CityKey, CityValue> named cityEngine,
// no SerializableWrapper is needed:
await cityEngine.SetAsync(new CityKey("US", 1), new CityValue("New York", 8_336_817));
```

Schema evolution on a `[StorageSerializable]`:

```csharp
// Add a new field — old readers skip the unknown tag, new writers emit it.
// Old data read by a new reader sees the missing tag and populates the default
// (the empty string for a non-nullable string, null for a string?). Either way the
// migrated value writes back unchanged: a null string? writes no tag, like a null T?.
[StorageSerializable]
public partial record struct CityValue(
    [property: Tag(1)] string Name,
    [property: Tag(2)] int Population,
    [property: Tag(3)] string Timezone);

// Retire a field — mark the tag reserved so nothing reuses it by accident.
[StorageSerializable]
[ReservedTag(2)]
public partial record struct CityValue(
    [property: Tag(1)] string Name,
    [property: Tag(3)] string Timezone);
```

To customize generated output at the assembly level:

```csharp
[assembly: StorageDefaults(GenerateComparisonOperators = false)]
```

For the full supported-type table, wire-format specifics, and diagnostic
reference, see [`docs/source-generator-type-reference.md`](docs/source-generator-type-reference.md). For migrating a 1.x
deployment, see [`docs/migration-1x-to-2x.md`](docs/migration-1x-to-2x.md).

## Architecture

```
┌────────────────────────────────────────────────────────────────────┐
│                          Domain Layer (b)                          │
│  Storage: ILsmStorageEngine, IBTree, IBPlusTree, ISortedStringTable│
│  Structures: IRedBlackTree, ISkipListMemTable, IBloomFilter,       │
│              ICountingBloomFilter, IWriteAheadLog                  │
│  Time-Series: ITimeSeriesStore, ITimeSeriesStorageEngine           │
│  Key constraint: IStorageKey<TSelf>                                │
│  Value Objects: FileLocation, SsTableMetadata, VersionedValue      │
└───────────────────────────┬────────────────────────────────────────┘
                            │ implemented by
┌───────────────────────────▼────────────────────────────────────────┐
│                     Infrastructure Layer (b)                       │
│  Storage Engines: LsmStorageEngine, AppendOnly, LogSegmented       │
│  Data Structures: BTree, BPlusTree, RedBlackTree, SkipListMemTable │
│  Probabilistic: BloomFilter, CountingBloomFilter                   │
│  Time-Series: LsmTimeSeriesStore, columnar segment formats         │
│  Algorithms: FNV-1a, Murmur3, xxHash32                             │
│  Serialization: Binary, CSV                                        │
└────────────────────────────────────────────────────────────────────┘
┌────────────────────────────────────────────────────────────────────┐
│         Distributed (a) — in-process reference, not on NuGet        │
│  Partitioning: IConsistentHashRing, IRangePartitioner / impls      │
│  Replication: ISingleLeaderReplication, IQuorumReplication / impls  │
│  Consensus: IRaftNode, IRaftCluster / RaftNode, RaftCluster        │
│  CRDTs: IGCounter, IPNCounter, IGSet, IORSet / impls, VectorClock  │
│  Transactions: IMvccStore, ISnapshotIsolationStore /               │
│                MvccStore, SnapshotIsolationStore                   │
│  Merkle: IMerkleTree / MerkleTree                                  │
└────────────────────────────────────────────────────────────────────┘
┌────────────────────────────────────────────────────────────────────┐
│                     Source Generator (Compile-time)                │
│  StorageSourceGenerator (IIncrementalGenerator)                    │
│  Attributes: [Key], [StorageSerializable], [Tag], [ReservedTag],   │
│              [StorageDefaults]                                     │
│  Emits: ISerializable<T>, IComparable<T>, IEquatable<T>, operators │
│  Wire formats: tagged (values, DDIA 2e Ch.5), ordered (keys, byte-lex)│
│  Diagnostics: BSSG001–BSSG026 (BSSG009 reserved-unshipped)         │
└────────────────────────────────────────────────────────────────────┘
```

The architecture separates contracts from implementations: the Domain layer defines contracts, and Infrastructure provides implementations that can be swapped independently.

For detailed architecture including the interface hierarchy, LSM engine composition, data flow diagrams, and component navigation, see [ARCHITECTURE.md](ARCHITECTURE.md).

## Design Decisions

| Decision | Choice | Reasoning |
|---|---|---|
| LSM segment layout | Pluggable via `ISegmentFormat<TKey, TValue>` | A single LSM engine drives row or column-oriented segments via strategy injection; the engine owns memtable, WAL, lock, tombstones, and compaction — nothing format-specific. Columnar slots in without duplicating any of that machinery. |
| Default segment format | Row (`.sst`) | The block-structured `SstSegmentFormat` is the default. Columnar (`.bsct`) remains an explicit choice for analytical time-series workloads. |
| Columnar per-dataset block boundary | `TimeSeriesKey<TDatasetKey>` lex ordering | Flushed entries are already grouped contiguously by dataset, so the envelope can emit one column-oriented block per dataset at zero extra sort cost. |
| Columnar footer magic validation | Trailer magic checked **before** footer-length field | A non-Boutquin file (or truncated file) is rejected before its bytes are used as a seek offset, so invalid segments can never trigger out-of-bounds reads. |
| Per-column codec defaults | Gorilla XOR (`double`), delta-of-delta (`DateOnly`), delta+VLQ (integers), dictionary (`string`/low-cardinality `decimal`), scaled-int bit-packing (`decimal` over dict threshold), bitmap-RLE (`bool`/low-cardinality) | The defaults use techniques described in [Gorilla](https://www.vldb.org/pvldb/vol8/p1816-teller.pdf) and [Parquet encodings](https://parquet.apache.org/docs/file-format/data-pages/encodings/). Overridable per-field via `[ColumnEncoding]` for columns whose statistics don't match the default's assumptions. |

### Recovery threat model of the durable logs

The write-ahead log, the value log and its manifest, the append-only file engine, the event log and the Raft log share one recovery contract. In durable mode, each append is acknowledged only after its flush-to-disk request, so whatever follows the last whole frame is an unacknowledged, interrupted write under the failure model below. Every frame header carries its own CRC32C, so a flipped length byte is detected rather than inferred from whether later bytes happen to parse.

The process-termination tests verify recovery after killing an undisposed writer. They do not establish physical power-loss guarantees: those depend on the filesystem and storage device honoring the requested barriers. macOS's stronger `F_FULLFSYNC` barrier is not implemented, and Windows replacement does not request a separate write-through rename barrier. POSIX replacement synchronizes the containing directory. An atomic replacement or a passing killed-process test must not be read as proof that controller caches survive a power cut.

- **Crash tears are repaired.** The interrupted final write is truncated, and every earlier frame is recovered, only when it is consistent with a single interrupted write: a strict prefix of what the writer was laying down, followed only by zeros. A partial header and a valid header with a short payload qualify in every log. Every log repairs a last-frame payload checksum failure only when the damaged frame's own last byte did not land, and refuses a final frame whose last byte landed, because that frame was written completely and fsync'd before its append was acknowledged. A WAL frame and a value-log frame end with their payload checksum, whose genuine high byte is zero in about one frame in 256, so the last byte must be zero and the checksum bytes that landed must equal the checksum of the landed payload; zeros may follow the frame to end-of-file (the rest of an interrupted group-commit batch in the WAL). A Raft-log frame and an append-only-engine frame end with a fixed non-zero end marker, so the marker must be zero and the command or payload before it must be complete (its checksum passes) or end in a zero byte: a landed non-zero last byte means the whole command or payload landed. The append-only engine also accepts zeros after the frame (the rest of an interrupted bulk batch). The event log ends each frame with its payload, so the payload's last byte must be zero. The event log and the Raft log write and fsync exactly one frame per append, so zeros after a frame's end cannot come from that frame's interrupted write, and they repair the shape only when the damaged frame ends exactly at end-of-file. Only the active tail is ever repaired: the last WAL segment, the highest value-log segment, or the single file of the other logs.
- **Damage to acknowledged bytes fails loud.** A final WAL, value-log, append-only-engine, event-log or Raft-log frame whose last byte landed yet fails its payload checksum, a checksum failure with non-zero bytes after it, a complete header that fails its checksum, or a torn tail on any segment but the last throws a typed exception (`WriteAheadLogCorruptException`, `CorruptValueLogException`, `InvalidDataException`) and leaves the file untouched, so a later append or compaction cannot erase the frames behind the damage.
- **Sealed segments are verified.** Rotation seals the outgoing WAL segment with a `SegmentSeal` frame and the outgoing value-log segment with a seal record in the manifest (final length, frame count and checksum). A sealed segment that was emptied, cut (even at a frame boundary), extended, reordered, renamed or swapped with another throws.
- **Lost segments and metadata fail loud.** A missing middle WAL segment, a deleted last WAL segment, an emptied, header-cut or zero-filled last WAL segment numbered above 1 (only `wal_000001.log` may be cut to nothing, because it is the one segment created in place), a manifest-listed value-log segment that is missing on disk, and a lost or emptied value-log manifest beside segments that hold frames all refuse to open. None is read as an empty log, and the value-log orphan sweep never deletes acknowledged segments.
- **Outside the threat model.** Truncating the active tail (the last WAL segment, the highest value-log segment) or a single-file log (append-only engine, event log, Raft log) exactly at a frame boundary leaves only valid frames and nothing records how many followed, so it is indistinguishable from those writes never having happened. Deleting every WAL segment is likewise indistinguishable from an empty log, and so is deleting the oldest WAL segment or segments: recovery requires only that the surviving segment indices be contiguous, and reclamation of flushed segments legitimately removes a contiguous oldest-first prefix, so a lost head looks like reclamation. The writes in a deleted head segment are gone, and an LSM engine over that WAL reopens without them. Recovery returns what remains. In the event log, whose frames end with their payload, damage to a final payload whose genuine last byte is zero would leave bytes that a payload prefix followed by zeros could produce, but its JSON payloads never end in a zero byte. Caller-encoded bytes may end in a zero byte (a small little-endian integer), so the Raft log and the append-only engine close the gap with a fixed non-zero end marker, a byte the writer always makes non-zero; only damage that zeroes that marker (and, when the record's genuine last byte is zero, damages the record too) is recovered as a torn append. Detecting the tail cases would need a durable high-water mark written on every append, at the cost of a second fsync per append; detecting a lost head would need a durable reclamation low-water mark that recovery checks, which the WAL does not keep.

The guarantees are pinned by executing them against the real logs, not only by text: see [docs/PITFALLS.md](docs/PITFALLS.md) section 4 and the [WiscKey guide](docs/wisckey-value-log.md) for the value log's byte-level rules.

## Operation Costs

Operation cost depends on the selected engine, segment layout, key and value sizes, and durability settings. LSM reads may consult multiple segments; compaction reads and rewrites the selected data. Columnar projections decode the selected columns in matching row groups. Flush-to-disk latency is additional to in-memory algorithm costs.

See the interface XML documentation for individual operation contracts and the [trade-off guide](docs/TRADEOFFS.md) for measured workloads and their limits. These are implementation-specific costs, not universal guarantees derived from DDIA.

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
