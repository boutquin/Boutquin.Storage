# Storage comparison corpus

This is a selected behavioral corpus, not a port of either upstream's complete suite.
The fixtures and test adapters are test-only; they add no runtime package dependencies.

## Pinned upstream sources

| Source | Commit | Selected cases | Adaptation |
|---|---|---|---|
| [LevelDB db/db_test.cc](https://github.com/google/leveldb/blob/bb74ef739973a70ca9f0d90788a1da89b788c0ad/db/db_test.cc) | `bb74ef739973a70ca9f0d90788a1da89b788c0ad` | PutDeleteGet, GetSnapshot, GetIdenticalSnapshots, IterateOverEmptySnapshot, GetLevel0Ordering, GetOrderedByLevels | C# public LSM operations; FlushAsync replaces TEST_CompactMemTable; full compaction replaces range compaction. Both short and 200-character snapshot keys retained. Storage strategies replace the upstream option matrix. File placement and LevelDB internal counters are not asserted. |
| [Badger value_test.go](https://github.com/dgraph-io/badger/blob/78b99b8b0f014b539455186404697a0ff58c55f2/value_test.go) | `78b99b8b0f014b539455186404697a0ff58c55f2` | TestValueGC, TestValueGC2 | Same 100-key delete/small-overwrite histories; deterministic 140-character values and smaller file rotation replace random 32KiB values. Public CompactValueLogAsync replaces private file rewrite. Exact surviving content and reopen are additional checks. No transactional batch parity or identical GC policy is claimed. |

LevelDB scenarios retain the upstream BSD license in [LevelDB-LICENSE.txt](LevelDB-LICENSE.txt).
Badger scenarios retain the upstream Apache license in [Badger-LICENSE.txt](Badger-LICENSE.txt)
and attribution in the adapted test file. These are adaptations by Pierre G. Boutquin (2026).

## Independent SQLite oracle

`scripts/generate-storage-oracle.py` executes 288 committed SQLite mutations across seeds
17, 73 and 2026. After each mutation, SQLite computes the complete ordered state using
`SELECT ... ORDER BY`; no Storage implementation is imported. The JSON fixture is embedded
in the test assembly. Reproduce it with `python3 scripts/generate-storage-oracle.py --check`.
Generation used SQLite 3.54.0; engine version is reported by the script rather than included
in deterministic fixture content.

The C# adapter replays all histories across full, size-tiered and leveled compaction, with
inline values and WiscKey value separation. It checks every point key, inclusive ranges,
snapshot isolation, explicit flush/compaction, value-log GC, and clean reopen boundaries.
Snapshots are disposed before reopen; SQLite transactions and Storage snapshots are not
claimed to share transaction semantics. Nonzero integer keys and non-null string values
deliberately use the common supported domain. Storage's default-value rejection is a
separate contract test, not a failure to match SQLite's wider domain.

## Additional comparison families

| Corpus | Evidence |
|---|---|
| `AlgorithmOracleTests`, `CollectionAndDataflowOracleTests`, `QueryOracleTests` | Executed upstream Murmur vectors; independent hash, dictionary, set, cache, merge, token-scan, vector-distance, relational and streaming-window models. [Source mapping and differences](QueryOracleSources.md). |
| `IoOssComparisonTests` and the process-crash probe | Concurrent LSM histories, snapshots and value GC; concurrent WAL acknowledgment recovery; value-log handle eviction; 17 killed-process histories. [Source mapping and boundaries](Io-provenance.md). |
| `ColumnarCodecVectorTests`, `ColumnarSqliteQueryTests` | Independent bit/byte vectors for six column codecs and 120 SQLite scalar-query answers at three row-group sizes. |
| `TaggedWireIndependentVectorTests`, `StandardCompressionAndJsonVectorTests` | Independent schema-evolution frames, executed Google.Protobuf signed-varint parity, standard compression vectors and framed JSON unknown-field retention. |
| Distributed and service oracle histories | Consensus, CRDT, partition and Merkle vectors; independent event/SQLite histories for services, replication and transactions. [Pins, licenses and differences](Distributed/provenance.md). Distributed remains a source-only reference assembly. |

The specialized codec comparisons draw on [Prometheus v3.5.0](https://github.com/prometheus/prometheus/tree/8be3a9560fbdd18a94dedec4b747c35178177202/tsdb/chunkenc),
[Parquet format 2.12.0](https://github.com/apache/parquet-format/blob/7e401bf1c970488457670f17cf103bd03acea9c1/Encodings.md),
and [Protobuf v36.2](https://github.com/protocolbuffers/protobuf/tree/2c74169b34066ceb8ddb6b882fcb3fb32d737a55).
Repository-specific column/tagged formats differ from those formats. The test vectors pin
Storage's own documented byte layout; they do not claim complete format interoperability.

## Evidence boundaries

These selected tests complement the existing contract suites; they are not exhaustive
proofs of every input, schedule or failure mode. Process termination is tested separately
from physical power loss, filesystem/controller failures and drive-cache behavior. Concurrent
disjoint-writer histories do not establish arbitrary-history linearizability. Approximate
search at small search budgets is not exact search. The process-local distributed models
do not provide production network transport or automatic membership reconfiguration.
No performance equivalence or blanket upstream engine compatibility is claimed.
