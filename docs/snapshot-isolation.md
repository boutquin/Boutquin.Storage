# Snapshot Isolation on `ILsmStorageEngine`

`ILsmStorageEngine<TKey, TValue>` exposes a point-in-time consistent read view via
`AcquireSnapshotAsync`. Consumers who need multi-key reads to observe a coherent slice of
engine state — rather than two independent `TryGetValueAsync` calls that may straddle a
concurrent write — use `ISnapshot<TKey, TValue>` instead of the live engine's read methods.

Reference: Kleppmann, *Designing Data-Intensive Applications* (2nd ed., 2026), Ch. 4 p. 131
(SSTable immutability as the substrate for snapshot isolation) and Ch. 8 (the MVCC
visibility rule).

## When to use which read API

| API | Guarantee | Cost | Use when |
|-----|-----------|------|----------|
| `engine.TryGetValueAsync(key)` / `engine.GetRangeAsync(…)` | Single-call atomicity (serialized by the engine's write lock) | None | One key, or when the reader tolerates reading mid-mutation for multi-key reads |
| `ISnapshot.TryGetValueAsync(key)` / `ISnapshot.GetRangeAsync(…)` | Point-in-time consistent across every call through the token | Each live snapshot pins a segment set — retired files remain until their last snapshot pin is released | Multi-key reads must agree on a single instant; consistent range scans; read-only replicas that need a stable view |
| `IMvccStore<TKey, TValue>` | MVCC transactions with explicit registries | In-memory version chains | Transactional workloads with reads and writes composed under one `BeginTransaction` |
| `ISnapshotIsolationStore<TKey, TValue>` | Plain Snapshot Isolation (DDIA 2e p. 293–299) — readers see a consistent snapshot; writers detect concurrent point-key writes at commit time and abort one | First-committer-wins bookkeeping on commit | Transactional workloads that need consistent snapshots and lost-update prevention — but see the serializability caveat below |

Plain `ISnapshot` is the cheapest consistent-read primitive and composes with any future
transaction layer that wants to delegate its "snapshot" to the LSM engine.

> **Serializability caveat.** `ISnapshotIsolationStore` implements *plain* Snapshot
> Isolation, **not** Serializable Snapshot Isolation. It has no predicate-range tracking,
> so it does not prevent the write-skew anomaly (DDIA 2e p. 317–321). When you need true
> serializability, use a transaction system that tracks predicate conflicts or enforce the
> relevant invariant through compare-and-set or application-level locking.

## The visibility rule

A snapshot is identified by `SequenceNumber` — the engine's monotonic write counter at
acquisition time. Reads through the snapshot walk MemTable → segments newest-to-oldest and
stop on the first entry for the key whose per-entry seqno is `≤ SequenceNumber`. If that
entry is a value-write, its value is returned. If it is a tombstone, the key is reported as
absent. An entry whose seqno is strictly greater than `SequenceNumber` — including
tombstones written after acquisition — is skipped and traversal continues to older
versions.

This implements the two-sided MVCC predicate:

```
visible(entry) ≡ (entry.creator_seqno ≤ snapshot.seqno)
               AND (entry.deleter_seqno IS NULL OR entry.deleter_seqno > snapshot.seqno)
```

### Worked example — pre-write snapshot sees the pre-write value

```csharp
await engine.SetAsync(k, preValue);         // seqno = 1
await using var snap = await engine.AcquireSnapshotAsync();  // snap.seqno = 1

await engine.SetAsync(k, postValue);        // seqno = 2

var (value, found) = await snap.TryGetValueAsync(k);
// value == preValue, found == true

var (live, _) = await engine.TryGetValueAsync(k);
// live == postValue
```

### Worked example — post-write snapshot sees the post-write value

```csharp
await engine.SetAsync(k, preValue);         // seqno = 1
await engine.SetAsync(k, postValue);        // seqno = 2
await using var snap = await engine.AcquireSnapshotAsync();  // snap.seqno = 2

var (value, _) = await snap.TryGetValueAsync(k);
// value == postValue
```

### Worked example — tombstone written after acquisition is invisible

```csharp
await engine.SetAsync(k, v);                // seqno = 1
await using var snap = await engine.AcquireSnapshotAsync();  // snap.seqno = 1

await engine.RemoveAsync(k);                // seqno = 2

var (value, found) = await snap.TryGetValueAsync(k);
// value == v, found == true  (the delete's seqno = 2 is invisible to snap.seqno = 1)

var (_, liveFound) = await engine.TryGetValueAsync(k);
// liveFound == false
```

### Worked example — tombstone visible to the snapshot

```csharp
await engine.SetAsync(k, v);                // seqno = 1
await engine.RemoveAsync(k);                // seqno = 2
await using var snap = await engine.AcquireSnapshotAsync();  // snap.seqno = 2

var (_, found) = await snap.TryGetValueAsync(k);
// found == false (snapshot sees both versions; tombstone's seqno = 2 wins)
```

## Lifecycle and retention cost

Every snapshot pins its acquisition-time segment set on disk until it is disposed — even
after compaction has logically superseded those segments. Pinning is how the snapshot
continues to serve correct reads for older versions of keys that have since been overwritten
or deleted. Additional segment-file retention is the sum of the sizes of the
**distinct retired files still pinned by live snapshots**. Each is counted once even
when several snapshots pin it. A small write followed by compaction can retire a large
pre-existing segment, so lifetime × write rate is not an upper bound. Snapshots also
retain acquisition-time memtable copies and may keep referenced value-log files alive.
Dispose promptly and monitor retired files and disk usage. The lifecycle pattern is:


```csharp
await using var snap = await engine.AcquireSnapshotAsync();
// Snapshot reads inside the block stay consistent.
var a = await snap.TryGetValueAsync(keyA);
var b = await snap.TryGetValueAsync(keyB);
// `snap.DisposeAsync()` runs at the end of the `await using` scope; the segment pins
// are released and compaction can physically reclaim any retired files.
```

A snapshot that is dropped without disposal is **never** reclaimed by the garbage collector:
nothing unregisters it, so its segment pins and its sequence-number floor (which also stops
tombstone garbage collection from discarding anything the snapshot could still see) stay in
force until the engine is disposed. A leaked snapshot can therefore retain files for
the engine's entire lifetime. Always use `await using`.

## Columnar time-series stores support snapshots

Snapshot isolation is available on **both** segment formats — the row/SST format and the
columnar time-series format. Isolation is enforced by the engine's **segment pinning + the
at-acquisition memtable copy**, which is format-agnostic: it does not depend on the segment
format carrying a per-entry sequence-number slot.

A columnar segment carries no per-row seqno on disk (the synthesized read seqno is `0`), but a
pinned columnar segment holds **only pre-acquisition data** — it was flushed before acquisition
and an immutable on-disk segment is never appended to. Post-acquisition writes land in the live
memtable or in **new** segments the snapshot never captured (including segments a concurrent
compaction merges into), so they stay invisible to the live snapshot. Visibility is enforced by
*which segments and which memtable copy the snapshot holds*, not by per-entry seqno filtering on
the columnar bytes. A columnar flush also collapses every record for one `(dataset-key, date)`
to a single last-writer-wins slot, so a pinned columnar segment never holds two versions of the
same key for the (absent) seqno filter to disambiguate — the disambiguation the row format does
with seqno is, for columnar, already done at flush time.

Two `SupportsSnapshots` predicates exist, and they answer different questions:

| Surface | Question it answers | Columnar value |
|---------|---------------------|----------------|
| `ILsmStorageEngine.SupportsSnapshots` / engine-level | "can this engine serve snapshot isolation?" — yes, via pinning | `true` |
| `ISegmentFormat.SupportsSnapshots` / format-level | "does this format carry a per-entry seqno slot for filtered reads?" | `false` |

The format-level flag is **not** the isolation gate; `true` there would falsely imply the
columnar bytes carry per-entry seqnos. It is retained as the honest seqno-slot predicate for a
hypothetical future reader that needs seqno-filter availability specifically.

### Deferred reclamation under a held-open columnar snapshot

Because columnar stores can now be snapshotted, columnar segments are reachable by the
**park-not-delete** path: while a snapshot is live, a compaction that supersedes a pinned segment
**parks** that segment (keeps the file on disk and the reader alive so the snapshot's reads stay
valid) instead of physically deleting it. Parked files are reclaimed on the next compaction or on
the snapshot's dispose. Retention duration follows the last pin's lifetime; retained
bytes depend on the sizes of the pinned files.

This deferred-reclamation backlog is **operator-visible**: `LsmStorageEngine.RetiredPinnedFileCount`
reports the number of compaction-superseded segments currently parked because a live snapshot pins
them. It is non-zero while such a snapshot is held and returns to zero once the last pinning
snapshot disposes and the sweep runs — so an operator can see why disk is not being reclaimed under
a held-open snapshot rather than discovering it empirically.

## What plain snapshot isolation does not prevent

Plain SI stops a multi-key read from observing a mid-mutation straddle, but it does not
make a reader immune to every concurrency hazard:

- **Write skew** — a consumer who reads two keys under a snapshot, decides, and then writes
  a third key *outside the snapshot* has no protection against another transaction mutating
  the read set between acquisition and write. Neither `ISnapshot` nor
  `ISnapshotIsolationStore` prevents this (plain SI has no predicate-range tracking); use
  compare-and-set or application-level locking for serializable semantics.
- **Predicate-based write conflicts** — a range read under a snapshot sees a stable set of keys, so it does not exhibit phantom reads. A
  post-snapshot write to a new key is invisible to the snapshot. If the consumer makes a
  decision based on "no such key exists" and then writes the key outside the snapshot, a
  concurrent inserter of the same key cannot be detected by the snapshot itself.
- **Lost update** — `ISnapshot` is read-only by construction, so lost update manifests only
  when the consumer composes a snapshot read with an out-of-snapshot write. Use
  compare-and-set, application-level locking, or the commit-time first-committer-wins check
  of `ISnapshotIsolationStore` for that composition. A bare `IMvccStore` performs no
  commit-time conflict check: two concurrent read-modify-write transactions both commit, and
  one update is lost.

See DDIA 2e Ch. 8 for the full taxonomy.

## Durability

Snapshots are process-local and non-durable. A process crash drops every live snapshot;
post-crash recovery starts with an empty live-snapshot registry. Post-crash, compaction's
tombstone-strip rule degenerates to the bottommost-segment check until the next snapshot is
acquired. Any pre-crash compaction that had written a new segment and flipped the manifest
but had not yet deleted the picked segments leaves orphan files on disk that the engine's
startup orphan-recovery path cleans up.

## API reference

See the XML doc-comments on
`Boutquin.Storage.Domain.Interfaces.ISnapshot<TKey, TValue>`,
`Boutquin.Storage.Domain.Interfaces.ILsmStorageEngine<TKey, TValue>.AcquireSnapshotAsync`,
and `Boutquin.Storage.Infrastructure.LsmTree.LsmStorageEngine<TKey, TValue>` for the
contract and implementation-level detail.
