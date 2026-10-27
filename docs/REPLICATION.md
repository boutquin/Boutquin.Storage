# Replication, conflicts, and causal sessions

The nonpackable `Boutquin.Storage.Distributed` project contains executable in-process reference implementations. `MultiLeaderReplicaSet<TKey,TValue>` models independent writable replicas and explicit message delivery. `LeaderlessReplicaSet<TKey,TValue>` adds response thresholds, spare nodes, hints, and repair. The existing `QuorumReplication<TKey,TValue>` remains a smaller strict-quorum baseline; `SingleLeaderReplication` retains its separate leader/follower model.

All new replica state is volatile. There are no sockets, persistent recovery, automatic membership changes, deletion/tombstones, or production deployment guarantees. A lock serializes calls within each replica set. Keys, payloads, and merge callbacks are borrowed: keys and values must stay immutable, and callbacks must be pure and deterministic. Fixed replica identifiers are compared ordinally.

## Run the demonstrations

```sh
dotnet run --project src/ReplicationSample -- 20260917
dotnet run --project src/ReplicationSample -- 4294967295
dotnet test tests/Infrastructure --filter FullyQualifiedName~ReplicationExtensionsTests
dotnet run -c Release --project benchmarks/Replication -- --filter '*' --job Dry --inProcess
```

The sample executes actual replication APIs with seeded loss, duplicates, delays, reordering, a partition, and a paused replica through `SimulatedNodeRuntime`. Once connectivity heals, captured operations are retransmitted. It verifies all replicas hold all 20 writes, shows all three conflict policies, rejects lagging session operations, exposes a disjoint sloppy read set, delivers hints, and repairs an unread key through Merkle comparison. Failure raises an exception and exits unsuccessfully.

The benchmark has four end-to-end cases: concurrent resolution, reversed causal delivery, read repair, and cold-key anti-entropy. Setup and allocation are included in each operation. A Dry run verifies execution only; it is not statistical performance evidence. Use normal BenchmarkDotNet jobs on a quiet machine for comparative measurements.

## Multi-leader writes and conflict resolution

```csharp
var replicas = new MultiLeaderReplicaSet<string, int>(
    ["west", "east"], new VersionVectorResolver<int>());
replicas.Write("west", "balance", 10);
replicas.Write("east", "balance", 20);
foreach (var operation in replicas.Capture("west"))
    replicas.Deliver("east", operation);
// Two concurrent siblings: [20, 10], in ordinal origin order.
var read = replicas.Read("east", "balance");
replicas.Write("east", "balance", 30, read.Session);
```

Writes apply only at the chosen origin. Every write carries a unique `(origin, sequence)`, a Lamport timestamp, and an immutable version vector including its dependencies. Delivery is idempotent and may arrive in any order. A write waits in a pending buffer until all preceding origin sequence numbers and all cross-origin dependencies have arrived. A missing dependency blocks visibility of its dependent effects, including effects on different keys. `Capture` returns applied operations only; retry the same captured operations to recover dropped messages. Repeating `Write` creates a new write and is not request deduplication.

All policies first discard causally superseded versions:

| Policy | Concurrent versions | Tradeoff |
|---|---|---|
| `VersionVectorResolver<T>` | Returns every maximal sibling in origin/sequence order | Preserves conflict for the application |
| `LastWriteWinsResolver<T>` | Greatest Lamport timestamp; ordinal origin breaks ties | Deliberately loses other concurrent values; logical order is not wall-clock recency |
| `MergeFunctionResolver<T>` | Calls a pure application function over the canonical sibling list | Application owns semantic correctness; unsuitable merges can violate business invariants |

Merge output is a read result, not a fabricated replicated version. It is recomputed from original siblings, so repeated delivery does not repeatedly add a previously merged sum. An explicit subsequent write with the read's session token creates a version that causally supersedes those siblings. No resolver implies transactions, consensus, or linearizability.

## Four session guarantees

Retain the returned `SessionToken` and pass it to **every subsequent operation in one sequential session**, including operations on different keys. Omitting it explicitly begins an unconstrained session. Tokens are immutable snapshots scoped to one replica-set lifetime; a token from another set is rejected. `SessionToken.Empty` has no dependencies.

| Guarantee | Enforced condition |
|---|---|
| Read-your-writes | A read's replica has applied all earlier writes represented by the token |
| Monotonic reads | Later reads cannot move to a replica with a smaller observed causal frontier |
| Monotonic writes | A later write is accepted only after all previous session writes have been applied locally |
| Write-follows-reads | A write includes the frontier observed by previous reads, and every receiver applies those causes before exposing that write |

The entire observed frontier is carried, including dependencies on keys other than the one read. This conservative rule can reduce availability. An insufficient frontier raises `SessionConsistencyException`; a caller can retry after replication or route to a current replica. Token propagation does not make arbitrary concurrent sessions linearizable. A new concurrent sibling can change a resolved value without regressing the causal frontier. Absence is an empty values list, not an unavailable replica disguised as a missing key.

These guarantees apply to the new multi-leader and leaderless APIs. Tokens are not adapters over the separate single-leader baseline and are not persisted or serializable bearer credentials.

## Leaderless responses, sloppy quorum, and hints

A leaderless set takes an ordered home list and a disjoint ordered spare list. This educational model uses the same home list for every key; it does not pretend to perform partition placement. Per-operation `ReplicationConsistency.One`, `Quorum`, and `All` require respectively 1, `floor(N/2)+1`, and N responses, where N counts homes only. The first required available nodes in preference order respond. Only those nodes receive the write; the others can lag.

With `sloppy: false`, only homes count. With `sloppy: true`, spares fill missing response slots, and each selected spare retains a hint addressed to an unavailable home. `Handoff` transfers the hinted write and its dependencies only when holder and destination are available, removes acknowledged hints, and leaves unreachable hints pending. Handoff is caller-scheduled and idempotent; there are no background timers or hint expiry. Spares retain their applied histories after handing off, avoiding unsafe history deletion.

Response count and session-frontier checks run before mutation. A quorum miss raises `QuorumNotMetException`; sufficient responses with missing causal dependencies raise `SessionConsistencyException`. The synchronous in-process acknowledgment model rejects before mutation. It does **not** model partial network-write timeouts or claim the same rejection guarantee in a real network protocol.

With strict home quorums, R + W > N gives an overlapping response set for completed operations. It does not eliminate concurrent writes or create linearizability. Sloppy substitution invalidates the home-set overlap argument: a write to home C plus spare D can succeed while a later read from homes A and B returns absence. Passing the write's session token makes that read fail until dependencies arrive. The executable sample proves both outcomes.

## Read repair and Merkle anti-entropy

A read merges the complete applied histories of its selected responders, resolves the requested key, and synchronously repairs those responders before returning. This intentionally transfers more than the requested key so that repaired values never lack their causal predecessors. Nonresponders remain stale. `ReadReplica` inspects local state without repair and rejects unavailable endpoints.

`Repair(first, second)` compares both available replicas' applied histories. It aligns the union of operation identities, builds the existing `MerkleTree` over present/absent identity leaves, skips equal roots, and recursively compares mismatched ranges. Only missing operations are delivered across mismatched leaves. The return value counts transferred operations; a second repair of equal histories returns zero. This repairs cold keys without application reads and preserves concurrent siblings in both directions.

Hashes represent membership of trusted immutable operation identities. They are **not** content checksums for mutable values or Byzantine/disk-corruption detection. Operation envelopes cannot be constructed outside their owning set through the public API; the immutable-key/value contract is required. Trees are rebuilt per compared range, so this reference implementation costs O(h log h) hashing in the worst case rather than maintaining incremental range indexes. The aligned identity inventory is assembled centrally in-process; the demo is not a network bandwidth benchmark.

## Costs and limits

History retention is unbounded because no safe global acknowledgment/garbage-collection protocol exists here. Vector size grows with membership; dominance checking for v versions and n origins costs O(v²n). Reads currently scan retained history, and causal delivery repeatedly scans pending writes. Long histories therefore increase CPU and memory use. Fixed membership, immutable borrowed values, explicit repair scheduling, and a single in-process driver make the failure model inspectable; production systems additionally need durable identity, bounded repair streams, crash recovery, authentication, membership epochs, and operational resource limits.

`ReplicationExtensionsTests` includes independent seeded network schedules and seeded leaderless availability/retry schedules. These exercise the real public APIs rather than substitute storage algorithms. The session tests check all four guarantees, cross-key dependency buffering, foreign-token rejection, immutable snapshots, and deliberate stale reads without a token. Convergence is conditional on eventual delivery/repair after the partition heals, not on an assumption that dropped messages deliver themselves.
