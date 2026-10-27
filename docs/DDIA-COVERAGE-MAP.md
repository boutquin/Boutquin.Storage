# DDIA concept coverage

This maps the implementation to the **second edition** of *Designing Data-Intensive
Applications* by Martin Kleppmann and Chris Riccomini. Chapter numbers follow the
[publisher’s contents](https://www.oreilly.com/library/view/designing-data-intensive-applications/9781098119058/),
checked on 2026-09-17. Older source comments may use first-edition numbering; topic names
are the reliable cross-edition anchor. The component mapping below is this project's assessment,
not an endorsement or completeness claim from the authors.

| Second-edition chapters | Implemented concepts | Start here |
|---|---|---|
| 1–2: architecture choices and requirements | Explicit durability/consistency boundaries, measured costs, fault tests | [Trade-offs](TRADEOFFS.md) |
| 3: data models | Event sourcing, CQRS read models, typed data frames | [Projections](event-projections.md), [batch](BATCH-DATAFLOW.md) |
| 4: storage | LSM/WAL/SSTable, B-tree/B+ tree, Bloom filters, WiscKey | [Architecture](../ARCHITECTURE.md), [value log](wisckey-value-log.md) |
| 4: analytics and retrieval | Columnar compression, batch operators, materialized aggregates, cubes, inverted/flat/IVF/HNSW indexes | [Columnar storage](columnar-time-series.md), [analytics](analytics.md), [search](search-and-vector-indexes.md) |
| 5: encoding | Tagged values, ordered keys, schema registry, Protobuf evolution | [Wire format](wire-format-2.0.md), [schema evolution](SCHEMA-EVOLUTION.md) |
| 6: replication | Leader/follower logs, quorum and causal replication, repair, session guarantees and offline sync | [Replication](REPLICATION.md), [sync](local-first-sync.md) |
| 7: sharding | Hash/range placement, versioned routing, rebalance plans, hot keys, secondary indexes | [Sharding](sharding-operations.md) |
| 8: transactions | MVCC, snapshot isolation, read committed, strict point-key locking, durable 2PC and outbox/inbox | [Transactions](TRANSACTIONS.md) |
| 9: partial failures | Seeded delay/loss/duplicates/partitions/pauses, histories, invariants and bounded linearizability checking | [Simulation](TESTING-DISTRIBUTED-SYSTEMS.md) |
| 10: consensus | Raft, committed registers/CAS/watches, IDs, fencing and leases | [Coordination](COORDINATION.md) |
| 11: batch | Object storage, external sort, shuffle, grouping/joining and task graphs | [Batch](BATCH-DATAFLOW.md) |
| 12–13: streams and composition | Change capture, snapshots/tails, checkpointed projections, event time and derived state | [CDC](CDC-PIPELINES.md), [streaming](STREAMING-AND-DERIVED-DATA.md) |
| 14: responsible systems | Application-level privacy, consent and societal decisions remain the consumer's responsibility | No algorithm claims to solve these policy decisions |

The library does not provide SQL, a cloud warehouse, a general query optimizer, a complete
streaming platform, GraphQL or a production distributed database. Snapshot isolation is not SSI;
point-key strict locking does not imply predicate locking. Distributed components are reference
implementations accessed by project reference. See their [assembly contract](../src/Distributed/README.md).

Run the [samples](SAMPLES.md) to see each composition and its failure boundary. Code availability
and NuGet publication are separate states; consult the [delivery status](ROADMAP.md).
