# Capability delivery status

Updated 2026-09-17. Status describes repository implementation, tests, examples and documentation.
It does not announce a NuGet release or a production distributed service. Version 2.0 remains
under development until its release tag and package publication.

| Delivery group | Capabilities | Status |
|---|---|---|
| Foundation | Object storage, event logs, schema registry, checkpoints, columnar formats | Implemented |
| Retrieval and reusable verification | Full-text search, flat/IVF/HNSW indexes, projections, deterministic simulation and bounded history checking | Implemented |
| Distribution and coordination | External shard maps, routing, local/global indexes, rebalance plans, durable transaction records, outbox/inbox, Raft coordination and fencing | Implemented |
| Replication and offline behavior | Causal multi-leader, leaderless repair, session guarantees and durable sync | Implemented |
| Analytical composition | Typed column-batch operators, materialized aggregates, data cubes, CDC snapshot/tail and batch utilities | Implemented |
| Streaming | Event-time windows, late events, joins and checkpointed state | Implemented |

The dependency chain starts with logs and atomic state/offset checkpoints. Projections build read
models; CDC connects mutable records to those derived consumers. Simulation exposes fault schedules
for coordination, replication and transactions. Columnar operators, external sort and streaming
then reuse these foundations rather than embedding another database or broker.

The [coverage map](DDIA-COVERAGE-MAP.md) relates this work to the book's concepts, the
[trade-off guide](TRADEOFFS.md) states costs and boundaries, and [sample commands](SAMPLES.md)
make the compositions executable. Domain and Infrastructure remain the storage library family;
Distributed stays an in-process reference assembly consumed through project references.

Future product work such as distributed network transport, automatic membership changes,
full SQL or a managed warehouse is outside this library's coverage objective. The implemented
reference algorithms do not claim those operational capabilities. Raft accepts file-backed log and
term/vote stores, while the coordination and simulation examples use its in-memory defaults.
