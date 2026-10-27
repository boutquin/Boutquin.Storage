# Boutquin.Storage.Distributed

**Reference implementations. Not for production deployment. Not published to NuGet.**

This assembly hosts the educational, single-node-process reference implementations of the
distributed-systems primitives from Kleppmann's *Designing Data-Intensive Applications* —
Raft consensus and coordination, single-leader / quorum / multi-leader / leaderless replication,
causal sessions, MVCC / Snapshot Isolation and durable transaction patterns, consistent hashing /
range / rendezvous / jump partitioning, versioned shard routing and rebalancing, local and global
secondary indexes, local-first sync, deterministic simulation, CRDTs, vector clocks, Lamport
timestamps, gossip, and Merkle repair.

The implementations are correct *as scoped*: they faithfully encode the algorithm, invariant,
or data structure presented in the DDIA chapter. They are **not** production-grade. A
production rewrite would add — among other things — network transports, authentication,
automatic membership changes, replicated failover, bounded retention and operational resource
controls. Raft would additionally need pre-vote, leadership transfer, automated log compaction and
snapshot installation, joint consensus, and a leadership-start no-op entry (Raft §8's
commit-liveness device; without it a new leader whose log ends in prior-term entries advances its
commit index only at the next client proposal).
Snapshot Isolation would need predicate-range tracking to become SSI; vector-clock and gossip
references omit dotted-version-vector compaction and phi-accrual failure detection.

Persistence is module-specific rather than a blanket assembly property. Replication histories
and hints are volatile. `SessionToken` values are immutable frontier snapshots scoped to one
replica-set lifetime; they are not persisted or serializable credentials. `FileRaftLog` and
`FileRaftPersistentState` persist local Raft state; durable two-phase-commit participants,
coordinator decisions, outbox/inbox/idempotency records, and `JsonFileSyncReplica` replay
file-backed journals. Those durable local components remain single-owner references and do not
claim replicated disaster recovery, remote consensus, or multi-process coordination.

Raft membership is fixed before execution. The parameterless `RaftCluster` and ordinary
`RaftSimulation` constructors accept fresh followers only: follower role alone does not
erase prior term, vote, or log history. To restore a cluster, use the constructor taking
`restoredMemberIds`, supplying the unchanged original member set from a caller-owned
manifest, then register every member before elections or message delivery. The local
persistence seams do not record membership and cannot verify the historical accuracy of
that supplied set. Supplying another set is unsupported reconfiguration. Commands carried
by the in-memory log and message queues must be immutable; file-backed log reads decode
private copies and provide a stronger ownership guarantee.

## Why carved into a separate assembly

The main `Boutquin.Storage.Domain` and `Boutquin.Storage.Infrastructure` assemblies ship as
(b) *production-strength* libraries under `Boutquin.Storage.*` on NuGet: LSM-tree, SSTable,
WAL, columnar time-series format, CDC, events, schemas, checkpointing, and object storage.
The source generator ships as its own analyzer package. Carving the in-process reference code
into its own assembly — and deliberately
*not* publishing it to NuGet (`<IsPackable>false</IsPackable>`) — draws a structural line
between the two, rather than relying on package-description copy to communicate "educational
reference only".

Consumers who want to experiment with the reference primitives depend on
`Boutquin.Storage.Distributed` via `<ProjectReference>` only (e.g. samples, tests). No one
can `dotnet add package Boutquin.Storage.Distributed` by accident.

## Snapshot isolation boundary

The assembly name describes the dominant content: replication, consensus, partitioning,
CRDTs, gossip, Merkle anti-entropy — these are all distributed-systems primitives.

Two carved types — `MvccStore` and `SnapshotIsolationStore` — are *single-node* transaction
primitives. They are included alongside the distributed reference implementations because
they share the same scope (educational, in-process, would themselves change substantially
under a production rewrite — commit-timestamp snapshots, predicate-range tracking to prevent
phantoms, read-only-transaction optimisation).

`SnapshotIsolationStore` implements plain Snapshot Isolation per DDIA 2e p. 293–299 — readers
see a consistent snapshot; writers detect concurrent point-key writes at commit time and
abort one. It does **not** implement Serializable Snapshot Isolation (SSI; DDIA p. 317–321),
which adds predicate-range tracking to prevent the write-skew anomaly. Applications that need
serializable execution must use a transaction system that tracks predicate conflicts; this
reference store does not provide that guarantee.

## Assembly layout

| Area | Namespace |
|------|-----------|
| Contracts | `Boutquin.Storage.Distributed.Interfaces.{Consensus,Coordination,DistributedSystems,Merkle,Partitioning,Replication,Sharding,Simulation,Sync,Transactions}` |
| Implementations | `Boutquin.Storage.Distributed.{Cdc,Consensus,Coordination,DistributedSystems,Merkle,Partitioning,Replication,Sharding,Simulation,Sync,Transactions}` |

Contracts live alongside their implementations in this assembly, not in
`Boutquin.Storage.Domain`, because a production rewrite of roughly three-quarters of these
interfaces would reshape the surface area (new endpoints, new injected dependencies, new
return shapes). Locking them into `Boutquin.Storage.Domain`'s `PublicAPI.Shipped.txt` would
create a "stable contract, unstable implementation" pin that is worse than the current
split.

## For production use, prefer

- **Raft** — [dotnext-net Raft](https://github.com/dotnet/dotNext), orleans-consensus,
  embedded-raft integrations.
- **Replication / partitioning** — Akka.NET cluster, Orleans silos, MassTransit sagas.
- **CRDTs** — AntidoteDB, automerge-net.
- **Transactions** — FoundationDB, CockroachDB, SQL Server 2022 In-Memory OLTP.

## Relationship to the rest of the repo

- `Boutquin.Storage.Domain` — stable contracts for the production-strength storage layer.
- `Boutquin.Storage.Infrastructure` — production-strength implementations of the storage
  layer (LSM, SSTable, WAL, columnar, CDC, events, schemas, and object storage).
- **`Boutquin.Storage.Distributed`** *(this assembly)* — in-process reference
  implementations of distributed primitives. Project-reference only.
- `Boutquin.Storage.SourceGenerator` — a separate packable analyzer package. Its NuGet package
  bundles `Boutquin.Storage.SourceGenerator.CodeFixes.dll`; the code-fix project is not published
  independently.

Architecture tests in `tests/ArchitectureTests/DistributedAssemblyTests.cs` enforce the
one-way dependency: nothing in `Boutquin.Storage.Domain` or
`Boutquin.Storage.Infrastructure` references `Boutquin.Storage.Distributed`.

Public guides describe the executable boundaries in more detail:

- [replication, conflict resolution, causal sessions, sloppy quorum, hints and repair](../../docs/REPLICATION.md);
- [durable transactions, messaging and isolation comparisons](../../docs/TRANSACTIONS.md);
- [Raft-backed coordination, watches, leases and fencing](../../docs/COORDINATION.md);
- [operational sharding and its metadata-only rebalance boundary](../../docs/sharding-operations.md);
- [local-first sync persistence, replay and retention](../../docs/local-first-sync.md);
- [deterministic simulation and invariant testing](../../docs/TESTING-DISTRIBUTED-SYSTEMS.md).
