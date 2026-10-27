# Choosing and composing the primitives

A component's persistence and consistency come from its actual collaborators. An in-memory
checkpoint store does not make a projection durable; a file-backed participant does not make its
coordinator replicated. The guides below identify those boundaries explicitly.

| Choice | Benefit | Cost or boundary |
|---|---|---|
| LSM with WAL and immutable segments | Sequential writes, replay, ordered scans, background compaction | Write/read amplification, memory budgets and compaction I/O; durable mode needs fsync |
| B-tree / B+ tree | Ordered lookup and range traversal | Rebalancing and mutation work; persistence follows the chosen implementation |
| Bloom filter | Avoid many absent-key reads | False positives; it never establishes that a key exists |
| WiscKey value separation | Compaction moves pointers instead of large values | Separate value-log reads and garbage collection constrained by snapshot pins |
| Compressed columnar segments | Skip row groups and decode selected typed columns | Encoding/decoding cost; a row-group date match still needs a strict row predicate |
| Materializing column-batch filter | Reusable typed selection for later operators | Copies selected values; measured runs can be slower than an already-decoded row loop; no SIMD claim |
| Data cube | Precomputed grouping combinations | Exponential dimension count, bounded to compact analysis |
| Flat / IVF / HNSW vector search | Exact baseline versus tunable approximate retrieval | Recall and work depend on corpus, probes/graph exploration and mutation pattern; HNSW keeps removed or replaced vectors as unreturned routing points until they outnumber live ones, then rebuilds the graph from the live vectors |
| Full-text inverted index | Term and combined-term lookup with TF-IDF ranking | Volatile postings and index memory; text analysis defines matching behavior |
| Protobuf frame / generated tagged values | Explicit field identity and unknown-field tolerance | Different wire formats; version registration and length limits remain required |
| Event log / projection checkpoint | Rebuild derived state; atomically resume state and offset | Retained history and replay cost; external effects require idempotency or another transaction boundary |
| CDC journal / compacted topic | Ordered capture versus latest state per key | A compacted topic loses intermediate events and retains deletion watermarks; it cannot replace a contiguous historical log |
| External sort / in-memory frame | Bounded sorted runs versus convenient typed operators | External sort adds file I/O; MiniDataFrame grouping/joining retains in-memory data and Cartesian join output can grow |
| Event-time windows and joins | Late-event revisions, CDC enrichment and replayable derived streams | Retained window state and buffered finite joins cost memory; materialize join provenance into a fresh event log before checkpointed delivery |
| Local / global secondary index | Local writes versus a single lookup destination | Read fan-out versus remote writes and concentrated index load |
| Rebalance plan | Versioned, reviewable minimum-change placement | Applying metadata is not copying shard records; snapshot/tail/cutover needs separate coordination |
| Multi-leader / leaderless reference | Availability and conflict/session behavior are observable | Fixed membership and volatile state; quorum arithmetic alone is not linearizability |
| Offline sync journal | Durable delta exchange and deterministic CRDT/conflict resolution | Peer replay is idempotent; uncertain local append can repeat an operation; retained history grows and file replacement has no directory-fsync guarantee |
| Durable 2PC | An irrevocable recorded decision can be retried after reopen | Prepared work can block; shards may observe the decision at different times |
| Outbox/inbox shared record | Local business state, dedup identity and outgoing messages commit together | Delivery retries remain necessary; arbitrary remote side effects are not exactly-once |
| Raft coordination | Committed barriers, CAS, fencing and ordered ownership | Coordination examples use volatile Raft state; loss of quorum stops progress and leases use an explicit logical clock |
| Deterministic simulation | Repeatable counterexamples and controlled fault boundaries | Modeled behavior only; a bounded checker can report inconclusive and does not prove all real deployments correct |

Durable-mode acknowledgment waits for the implementation's flush request. The executable
process-termination corpus checks recovery after writer death; it does not simulate physical
power loss or establish filesystem/controller-cache guarantees. macOS `F_FULLFSYNC` and a
Windows write-through replacement barrier are not implemented. See the
[recovery threat model](../README.md#recovery-threat-model-of-the-durable-logs) for the precise boundary.

Windows atomic replacement requires filesystem support for extended rename with
replace-if-exists and POSIX semantics. Existing delete-sharing readers retain their old
file handles while subsequent opens see the replacement. Unsupported rename operations
fail explicitly rather than falling back to a non-atomic copy or delete.

For measured retrieval behavior see [search](search-and-vector-indexes.md). For row/batch
materialization cost see [analytics](analytics.md); for disk-sort measurements and allocation
versus peak-memory distinctions see [batch](BATCH-DATAFLOW.md). Benchmark Dry runs establish
that workloads execute, not stable throughput or latency. Reproduce measurements on your data
and hardware before choosing a configuration.

[Sample commands](SAMPLES.md) expose these choices in runnable form. The
[coverage map](DDIA-COVERAGE-MAP.md) connects them to the book's topics.
