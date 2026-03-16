# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

#### Source Generator
- **Roslyn IIncrementalGenerator** — Compile-time code generation for `[Key]` and `[StorageSerializable]` record structs. Emits `ISerializable<T>` (binary serialize/deserialize), `IComparable<T>` (lexicographic chained comparison), `IEquatable<T>` (value equality), and comparison operators (`<`, `>`, `<=`, `>=`). Supports nested annotated types, `IEnumerable<T>` collections (primitive and annotated), all 8 primitive types, nested parent types, and assembly-level defaults via `[assembly: StorageDefaults]`. (660 → 702 tests, +42)
- **Six diagnostic rules** — BSSG001 (unsupported property type), BSSG002 (must be partial), BSSG003 (should be record struct), BSSG004 (duplicate assembly defaults), BSSG005 (mutually exclusive attributes), BSSG006 (interface already implemented). All with actionable messages and correct severity levels.
- **DiagnosticSuppressor** — Suppresses false-positive CA1036 and S1210 on `[Key]` types where the generator produces the required operators.
- **Pipeline caching** — `EquatableArray<T>`, `TypeToGenerate`, `PropertyInfo`, `ParentTypeInfo` are value-equal plain types (no Roslyn types) enabling Roslyn's incremental caching. Verified via caching tests.
- **Snapshot testing** — 10 Verify.SourceGenerators snapshot tests covering all code paths (key, serializable, composite, nested, collections, primitives, global namespace, internal accessibility).
- **Attribute rename** — `SerializableAttribute` → `StorageSerializableAttribute` to avoid collision with `System.SerializableAttribute`. Uses `[Conditional("BOUTQUIN_STORAGE_GENERATOR")]` to vanish from compiled output.

#### Partitioning (DDIA Ch. 6)
- **Consistent hash ring** — Virtual-node-based ring (default 150 per physical node) with `SortedDictionary` for O(log n) key lookup and wrap-around. Minimal key redistribution on node addition/removal. (474 → 496 tests, +22)
- **Range partitioner** — Binary search on sorted immutable boundaries; N boundaries = N+1 partitions. Thread-safe after construction. (DDIA Ch. 6)
- **Hash partitioner** — Modular hash-based partitioning with pluggable `IHashAlgorithm` (default Murmur3). (DDIA Ch. 6)
- **Rendezvous hash** — Highest-random-weight hashing; deterministic key-to-node mapping without virtual nodes. (DDIA Ch. 6)

#### Transactions (DDIA Ch. 7)
- **MVCC store** — Multi-version concurrency control with per-key version chains, snapshot isolation, and visibility rules. `SemaphoreSlim` synchronization. (496 → 514 tests, +18)
- **SSI store** — Serializable Snapshot Isolation wrapping MVCC; detects read-write and write-read dependencies at commit time (Cahill et al., 2008). (DDIA Ch. 7)

#### Replication (DDIA Ch. 5)
- **Single-leader replication** — Leader writes to store + replication log with monotonic sequence numbers. Followers maintain high-water marks for catch-up sync. `SemaphoreSlim` serialization. (514 → 535 tests, +21)
- **Quorum replication** — Dynamo-style W + R > N quorum. Writes go to exactly W replicas (not all). Read repair updates stale replicas with latest version. (DDIA Ch. 5)
- **Replication log** — In-memory ordered log for follower catch-up via sequence numbers. (DDIA Ch. 5)

#### CRDTs (DDIA Ch. 5)
- **GCounter** — Grow-only counter with per-node state; merge via element-wise max. (DDIA Ch. 5)
- **PNCounter** — Positive-negative counter composed of two GCounters; safe pattern-matching merge. (DDIA Ch. 5)
- **GSet** — Grow-only set; merge is set union (commutative, associative, idempotent). (DDIA Ch. 5)
- **ORSet** — Observed-remove set with unique tags per add; add-wins on concurrent add/remove; atomic tag counter. (DDIA Ch. 5)

#### Ordering & Consensus (DDIA Ch. 8–9)
- **Vector clock** — Detect concurrent events; merge via element-wise max. Before/After/Concurrent/Equal comparison. (535 → 556 tests, +21)
- **Lamport timestamp** — Lock-free total ordering via `Interlocked.CompareExchange`. Tie-break by node ID. (DDIA Ch. 8)
- **Gossip protocol** — Eventually consistent state dissemination with version-based ConcurrentDictionary merge. (DDIA Ch. 8)
- **Raft consensus** — Leader election and log replication with `RaftNode` and `RaftCluster`. Election safety, log matching, and leader completeness invariants. (DDIA Ch. 9)

#### Additional Data Structures
- **B+ tree** — Leaf-linked B-tree variant for efficient range queries via leaf chain traversal. (DDIA Ch. 3)
- **Skip list MemTable** — Probabilistic balanced structure with configurable max level; alternative to Red-Black tree. (DDIA Ch. 3)
- **Counting Bloom filter** — Bloom filter variant supporting element removal via integer counters. (DDIA Ch. 3)
- **Secondary index** — Document-partitioned local index; `SemaphoreSlim`-guarded, O(1) lookups via `Dictionary` + `HashSet`. (DDIA Ch. 6)
- **Leveled compaction strategy** — Simplified leveled compaction with configurable level-0 threshold and size multiplier. (DDIA Ch. 3)

### Changed

#### Polish & Hardening (Tier 3)
- **ReplicationLog** — Added monotonicity validation (rejects out-of-order sequence numbers) and binary search for O(log n) follower catch-up. (565 → 575 tests, +10)
- **StorageFile** — Added 30-second timeout to all semaphore waits (prevents silent deadlocks) and static semaphore cleanup in Dispose (prevents unbounded dictionary growth).
- **RendezvousHash** — Replaced `List.Contains` with `HashSet` for O(1) duplicate node detection.
- **Configuration guidance** — Added recommended values for `memTableCapacity`, `sparseIndexInterval`, `virtualNodeCount`, `level0Threshold`, `levelSizeMultiplier`, `baseLevelSizeMB` in XML docs.
- **LeveledCompactionStrategy** — Enhanced parameter documentation with RocksDB-calibrated recommendations.

#### Tests (386 → 575, +189 new tests across Phases 1–4, correctness audit, and polish)

#### LSM Engine Features (Items 1–6, 11)
- **WriteAheadLog integration** — Every write is persisted to an optional `IWriteAheadLog` before MemTable mutation (write-ahead guarantee). WriteAheadLog is truncated after successful flush. (DDIA Ch. 3)
- **Startup recovery** — On construction, engine scans for existing `{prefix}_*.dat` segment files and loads them in sorted order. If a WriteAheadLog is provided, its entries are replayed into the MemTable. Handles both segments + WriteAheadLog replay together. (DDIA Ch. 3)
- **Tombstone/delete support** — Optional `enableTombstones` flag enables `RemoveAsync` via in-memory tombstone sets. Tombstones survive flushes and are stripped during compaction to reclaim space. (DDIA Ch. 3)
- **Range queries** — `GetRangeAsync(startKey, endKey)` returns all key-value pairs within an inclusive range, merging MemTable and all segments with last-writer-wins deduplication. Added to `ILsmStorageEngine` interface. (DDIA Ch. 3)
- **Bloom filter integration** — Optional `bloomFilterFactory` creates per-segment bloom filters during flush. Reads skip segments where the bloom filter reports a definite miss, reducing read amplification. (DDIA Ch. 3)
- **Compaction strategy pattern** — `ICompactionStrategy` interface with `ShouldCompact` and `SelectSegments` methods. Two implementations: `FullCompactionStrategy` (threshold-based, merges all segments) and `SizeTieredCompactionStrategy` (min-segments-based). Strategy takes precedence over legacy `compactionThreshold` integer when provided. (DDIA Ch. 3)

#### SSTable Features (Items 3, 8, 9)
- **Atomic write** — `SortedStringTable.Write()` now serializes to a temporary file then uses `File.Move` with overwrite for crash-safe atomic writes. (DDIA Ch. 3)
- **Sparse index persistence** — `.idx` companion file stores sparse index entries via `BinaryPrimitives` serialization. Loaded automatically on construction. (DDIA Ch. 3)
- **GZip compression** — Optional `enableCompression` parameter wraps output in `GZipStream`. Compressed files are fully decompressed to `MemoryStream` for seekable reads. (DDIA Ch. 3)

#### Thread Safety & Dispose (Item 10)
- **`BulkKeyValueStoreWithBloomFilter` sealed + thread-safe** — Changed to `sealed class` implementing `IDisposable`. All 8 public methods wrapped with `SemaphoreSlim` + `ObjectDisposedException.ThrowIf`. Multiple dispose calls are safe.

#### LSM Compaction (original)
- **LSM Storage Engine compaction** — Size-tiered compaction that merges all on-disk segments into a single segment using sorted merge with last-writer-wins deduplication. Reduces read amplification from O(k) to O(1). Configurable auto-compaction threshold triggers compaction after flush when segment count reaches the threshold. Old segment files are deleted only after the compacted segment is fully written (crash-safe). Explicit `CompactAsync` also available for manual control. (DDIA Ch. 3)

#### New Data Structures & Storage Components
- **Sorted String Table (SSTable)** — Immutable, sorted on-disk key-value segments with sparse index, strict sorted-input validation, and merge with last-writer-wins semantics (DDIA Ch. 3).
- **WriteAheadLog** — Crash-recovery log with length-prefixed entries, CRC32 checksums via `System.IO.Hashing.Crc32`, `fsync` durability, persistent `FileStream`, corruption-tolerant recovery that skips corrupt entries and recovers subsequent valid ones, and thread-safe dispose via `Interlocked.Exchange` (DDIA Ch. 3).
- **B-tree** — Balanced search tree with configurable minimum degree, proactive node splitting, cancellation token support on all async methods, and null key validation via `Guard.AgainstNullOrDefault` (DDIA Ch. 3).
- **Merkle tree** — SHA-256 hash tree with `Build`, `Verify`, `GetProof`, and `VerifyProof` operations. Uses `BitOperations.RoundUpToPowerOf2`, `stackalloc` for hash concatenation, defensive copy on `RootHash`, and negative index validation (DDIA Ch. 5).
- **LSM Storage Engine** — Full Log-Structured Merge-tree orchestrator with RedBlackTree MemTable, WriteAheadLog, and on-disk segments. Features `ObjectDisposedException.ThrowIf` on all public methods, extracted `TryGetValueInternalAsync` to prevent lock re-entrance, atomic `SetBulkAsync` with single lock acquisition, and compaction documented as future work (DDIA Ch. 3).
- **Synchronous `WriteEntry` method** on `IEntrySerializer` — eliminates sync-over-async in `SortedStringTable.Write()`. Implemented in both `BinaryEntrySerializer` and `CsvEntrySerializer`.

#### Documentation
- O(n) complexity annotations on all 8 domain interfaces (`IBTree`, `IMerkleTree`, `IWriteAheadLog`, `ILsmStorageEngine`, `ISortedStringTable`, `IRedBlackTree`, `IBloomFilter`, `IHashAlgorithm`).
- Thread-safety remarks on all 8 implementation classes (`BTree`, `MerkleTree`, `WriteAheadLog`, `LsmStorageEngine`, `SortedStringTable`, `RedBlackTree`, `BloomFilter`, `XxHash32`).
- DDIA (Kleppmann) chapter citations on all interfaces.
- WriteAheadLog on-disk record format documentation (length-prefix + payload + CRC32).
- SSTable on-disk format documentation (sequential entries, sparse in-memory index, non-atomic write caveat).
- LSM compaction documented as future work with rationale.
- `docs/PITFALLS.md` — Catalog of 14 bug categories with traps, fixes, and prevention checklist.

#### Tests (232 → 386, +154 new tests)
- xxHash32 reference validation against `System.IO.Hashing.XxHash32` (8 tests covering empty, 1-byte, 5-byte, 17-byte, and string inputs).
- WriteAheadLog: append, recover, truncate, round-trip, corrupt entry skip, checksum corruption detection, file-not-exist recovery, null path rejection, dispose-then-use (3 `ObjectDisposedException` tests), skip-corrupt-recover-subsequent.
- SSTable: write + entry count, TryGetValue found/not-found/empty/first/last, merge sorted + dedup, creation/modification time, many entries with sparse index, unsorted input rejection, duplicate key rejection.
- B-tree: null key validation (4 tests), cancellation token (7 tests).
- LSM engine: `ObjectDisposedException` after dispose (8 tests covering all public methods).
- Merkle tree: negative leaf index rejection, defensive copy on `RootHash`, null block rejection.
- Red-Black tree: null key validation.

#### CI/CD & Tooling
- GitHub Actions CI workflows (`pr-verify.yml`, `publish.yml`) for PR verification and NuGet publishing.
- SourceLink and deterministic build support via `Directory.Build.props`.
- NuGet package metadata (description, tags, readme, icon).
- `global.json` and project-local `CLAUDE.md`.

### Changed
- **xxHash32 correctness fix** — Single-byte remainder changed from XOR (`^=`) to ADD (`+=`) per the xxHash specification. Validated against `System.IO.Hashing.XxHash32`.
- Fixed 14 HIGH + 26 MEDIUM code review findings across all components (TDD methodology).
- Upgraded to .NET 10 / C# 14 with `TreatWarningsAsErrors` enabled.
- Modernized `README.md` with badges, solution structure table, architecture diagram, usage examples for all components, complexity reference table, and pitfalls link.
- Fixed 31 earlier code review findings: nullable reference types, `ConfigureAwait(false)` on all library awaits, static method promotions, char overloads, locale-safe formatting.

### Removed
- Deprecated patterns: non-static methods that don't use instance state, string overloads where char suffices.
- Hand-rolled CRC32 implementation — replaced with `System.IO.Hashing.Crc32`.
- Redundant `FindSegmentStart` binary search in SSTable (replaced with single `FindSegmentIndex` call).
- Sync-over-async `.GetAwaiter().GetResult()` calls in SSTable (replaced with synchronous `WriteEntry`).
