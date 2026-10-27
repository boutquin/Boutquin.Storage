# Sharding operations and routing

`Boutquin.Storage.Distributed` contains an in-process reference implementation of the
operational layer around the existing partitioning algorithms. It keeps logical shard identity
separate from node placement:

1. An `IPartitioner<TKey>` maps a key to a fixed logical partition.
2. `ShardRouter<TKey>` converts that partition to a shard identifier.
3. An external `IShardMap` resolves the shard against the current versioned assignment snapshot.

Keeping the partition count fixed avoids hash-modulo-node-count reshuffles. Node changes update
only assignment metadata, so a router observes the latest map without being reconstructed.

## Assignment snapshots and rebalance plans

`ShardMapSnapshot` is a complete assignment set with a monotonically increasing version.
`JsonFileShardAssignmentStore` persists snapshots with optimistic concurrency and atomically
replaces the JSON file after flushing its contents. It coordinates writers in one process, keyed on
the physical file, so spellings that differ only by symbolic-link ancestors or letter case share one
optimistic-concurrency gate. Content that parses but violates a snapshot invariant (negative version,
null or blank entries, a shard assigned twice) is reported as `InvalidDataException` from both
`LoadAsync` and `TrySaveAsync`, and the file is left unchanged. The same invariants are enforced when a
`ShardMapSnapshot` is constructed (`ArgumentException`), so the store never persists a snapshot that
its own loader would reject. A
production implementation would use a consensus-backed metadata service or transactional database
to coordinate writers across processes and would make the containing-directory rename durable.

Every `RebalancePlan` names the exact snapshot version from which it was calculated. Applying a
stale plan fails without changing the live map. Successful application increments the version and
publishes the new snapshot only after persistence succeeds.

Applying a plan changes **assignment metadata only**. It does not migrate shard data. A real
operator must separately copy a consistent shard snapshot to the destination, stream the write
tail, verify catch-up, cut routing over with the versioned plan, and remove the source copy only
after the rollback window closes. Treating the metadata update as data movement would create a
route to an empty shard.

`ManualRebalancer` validates explicit operator moves and removes no-op moves.
`AutomaticRebalancer` balances a fixed shard set across the target nodes while retaining the
largest possible number of current assignments. It moves all shards from removed nodes and only
the excess shards from surviving nodes. Output ordering is deterministic for review and replay.

## Hot keys

`SaltedHotKeyStrategy<TKey>` encodes one logical key into a configurable set of salted routing
keys. A write selects one salt with Jump Consistent Hash; a read must gather every salt. More salts
spread writes further but increase read fan-out and result-combination cost. The caller supplies a
stable request hash so retries choose the same salt.

## Secondary indexes

| Strategy | Write path | Read path | Main trade-off |
|---|---|---|---|
| Local index | Update the index beside the primary record | Scatter-gather every relevant data shard | Simple local writes and co-location; fan-out reads |
| Global index | Update the index shard that owns the term | Query that one index shard, then fetch from recorded source shards | Fast single-term lookup; remote writes, and a hot index shard if the terms skew |

`LocalSecondaryIndexRouter` queries distinct shards concurrently and merges results in stable
order. `GlobalSecondaryIndexRouter` is itself sharded by term: a term partitioner (a term hash by
default, or a `RangePartitioner<string>` for contiguous term ranges) maps every index update and
lookup to the one index shard that owns that term, while each entry retains its source shard.
A single configured index shard is the one-partition case and concentrates all index traffic on
one node. `InMemoryShardedSecondaryIndex` supplies the reference backing store; production systems
need durable index updates coordinated with primary record writes.

A record whose indexed value changes must be withdrawn from its old term, or the old entry keeps
listing it. Both routers expose `RemoveAsync` and `ReplaceAsync(previousTerm, newTerm, entry)` for
that: replace writes the new posting first, then removes the old one, so a concurrent reader never
sees the record under neither term. For the global router the two terms can live on different
index shards, and the two writes are not atomic across shards.

## Partitioning algorithm comparison

| Algorithm | Lookup | Metadata | Topology constraint |
|---|---:|---:|---|
| Jump Consistent Hash | Expected O(log n) | O(1) | Buckets are sequential; removal is from the end |
| Consistent hash ring | O(log V), V virtual nodes | O(V) | Arbitrary nodes; ring rebuild after topology change |
| Rendezvous hashing | O(n) | O(n) node set | Arbitrary nodes; scores every node per lookup |

`JumpConsistentHash` implements the integer algorithm from Lamping and Veach,
[A Fast, Minimal Memory, Consistent Hash Algorithm](https://arxiv.org/abs/1406.2294).
The fixed compatibility vectors in the tests also match Google Guava's independent implementation.
See Guava's
[hashing implementation](https://github.com/google/guava/blob/master/guava/src/com/google/common/hash/Hashing.java)
and its published compatibility vectors for a separately maintained cross-check.
Its sequential-bucket constraint is why this repository uses it for logical bucket selection and
keeps arbitrary node placement in `IShardMap`.

Run the repeatable BenchmarkDotNet comparison with:

```bash
dotnet run --project benchmarks/Sharding -c Release
```

The benchmark measures pre-hashed Jump lookup separately from string-key ring and rendezvous
lookup. That distinction is intentional: Jump consumes a 64-bit hash, while the existing reference
implementations include their own string hashing. Use the results to compare complete call costs,
not as a claim that their hash-input work is identical.

## Executable demonstration

```bash
dotnet run --project src/ShardRoutingDemo
```

The demo persists a snapshot, routes through that external map, applies a versioned automatic
plan, and contrasts local scatter-gather with a global secondary-index lookup.
