# I/O comparison provenance

These additions exercise selected behaviors using Storage's public APIs. They do not execute
upstream engines or claim format, performance or transaction interoperability.

| Pinned source | Selected behavior | Storage adaptation |
|---|---|---|
| [LevelDB log_test.cc](https://github.com/google/leveldb/blob/bb74ef739973a70ca9f0d90788a1da89b788c0ad/db/log_test.cc), commit `bb74ef739973a70ca9f0d90788a1da89b788c0ad`, `OpenForAppend`, `Fragmentation` | Preserve records across reopening and varied sizes | `LevelDb_LogReopenForAppend_VariedRecordLengths` uses plain and indexed append engines, three generations, varied payloads and compaction. It checks exact current values after every fresh instance. Storage frames do not use LevelDB's fragmented-log wire format. |
| [LevelDB db_test.cc](https://github.com/google/leveldb/blob/bb74ef739973a70ca9f0d90788a1da89b788c0ad/db/db_test.cc), same commit, snapshot and multithreaded database families | Reads and snapshots remain consistent as writes proceed | `LevelDb_ConcurrentDisjointWriters_SnapshotAndCompactionKeepEveryVersion` is a new deterministic scenario with four disjoint writers and a pinned baseline snapshot; full, size-tiered and leveled strategies each run with inline and separated values. It checks every acknowledged current version and the original snapshot through compaction and value GC. This is selected concurrent evidence, not exhaustive linearizability checking. |
| [Badger value_test.go](https://github.com/dgraph-io/badger/blob/78b99b8b0f014b539455186404697a0ff58c55f2/value_test.go), commit `78b99b8b0f014b539455186404697a0ff58c55f2`, `TestValueBasic` | Value pointers resolve exact bytes | `Badger_ValueRead_ConcurrentEvictionRetainsExactPayloads` adds six readers, 24 rotated files and a one-handle cache to test Storage's handle pin/evict implementation. The concurrency/cache stress is a Storage extension, not a literal port of Badger's locking or transaction model. |

Retained attribution and full licenses: [LevelDB BSD license](LevelDB-LICENSE.txt) and
[Badger Apache license](Badger-LICENSE.txt). No upstream implementation source is vendored.
Adaptations and original extensions are by Pierre G. Boutquin (2026).

`Wal_ConcurrentGroupCommit_RecoversEveryAcknowledgedFrameOnce` is an original Storage contract
scenario. It verifies all 96 concurrently submitted frames once after reopen; it does not infer
physical frame ordering from task completion order.

The separate `tests/IoCrashProbe` host and `scripts/verify-io-process-crash.py` controller run
original process-death checks. The controller waits for completed write acknowledgments or a
production manifest-commit seam, kills the undisposed host, and verifies recovery in another
process. All three compaction strategies run with and without value separation at both boundaries.
Standalone WAL, value log, plain/indexed append engines are checked too. The segmented append
engine's documented non-persistent catalog is exercised as an explicit difference: files remain,
but a fresh engine does not recover its live segment list.

These tests do not simulate a power cut, a drive lying about flushes, network filesystems or
controller caches. macOS's stronger F_FULLFSYNC barrier is absent. Windows replacement
power-loss durability remains a platform qualification question; an atomic replacement or a
passing process-kill history does not establish that guarantee.
