// Copyright (c) 2024-2026 Pierre G. Boutquin. All rights reserved.
//
//  Licensed under the Apache License, Version 2.0 (the "License").
//  You may not use this file except in compliance with the License.
//  You may obtain a copy of the License at
//
//      http://www.apache.org/licenses/LICENSE-2.0
//
//  Unless required by applicable law or agreed to in writing, software
//  distributed under the License is distributed on an "AS IS" BASIS,
//  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//
//  See the License for the specific language governing permissions and
//  limitations under the License.
//
using System.Collections.Immutable;

namespace Boutquin.Storage.Distributed.Transactions;

/// <summary>
/// An in-memory MVCC key-value store that provides snapshot isolation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope:</b> In-process reference implementation in <c>Boutquin.Storage.Distributed</c>,
/// which is not published to NuGet (see <c>src/Distributed/README.md</c>). Not durable
/// across process restart, not intended for production deployment. Snapshot isolation per
/// DDIA 2e Ch. 8 (p. 293–299); SSI's predicate-range tracking (DDIA 2e Ch. 8, p. 317–321) is a future enhancement
/// and would be layered atop this type as a separate
/// <c>SerializableSnapshotIsolationStore</c>.
/// </para>
///
/// <para>
/// <b>How it works:</b> Each key maintains a version chain — an
/// <see cref="ImmutableList{T}"/> of <see cref="VersionedValue{TValue}"/> entries ordered
/// newest-first inside a <see cref="ConcurrentDictionary{TKey, TValue}"/>. Readers walk the
/// chain lock-free; writers append a new head via copy-on-write
/// (<see cref="ImmutableList{T}.Insert(int, T)"/>) under a small write gate so two concurrent
/// writers do not race on the same chain head.
/// </para>
///
/// <para>
/// <b>Why <see cref="ImmutableList{T}"/> not <see cref="ImmutableArray{T}"/> or a custom
/// linked list.</b> Per-key chains are short under typical MVCC workloads (most keys see a
/// handful of concurrent writers; the prune loop trims committed-and-superseded versions
/// periodically). <see cref="ImmutableList{T}"/> provides O(log n) walk and structural
/// sharing on append, which dominates <see cref="ImmutableArray{T}"/>'s O(n) copy-on-append
/// for chains longer than a few entries. A custom head-first linked list would shave constants
/// at the cost of debuggability and reliance on .NET's atomic-reference-write semantics —
/// rejected as premature optimization for a tier-(a) reference implementation. If profiling
/// post-GA shows the walk-cost dominating, the data structure is private to this type and
/// replaceable without API impact.
/// </para>
///
/// <para>
/// <b>Reader-blocks-reader is gone.</b> Previously reads waited on the same
/// <see cref="SemaphoreSlim"/> that serialised writes; the snapshot-isolation point of MVCC
/// (readers never block writers, writers never block readers) was defeated. The new chain
/// representation lets readers do <see cref="ConcurrentDictionary{TKey, TValue}.TryGetValue"/>
/// then walk the captured immutable list — entirely lock-free.
/// </para>
///
/// <para>
/// <b>Commit state lives on the version itself.</b> Each <see cref="VersionedValue{TValue}"/>
/// carries an <see cref="VersionedValue{TValue}.IsCommitted"/> flag flipped by
/// <see cref="CommitAsync"/>; the prune loop reads this directly rather than chasing a
/// separate transaction-state dictionary that would race with concurrent writes.
/// </para>
///
/// <para>
/// <b>Thread safety:</b> reads are lock-free; writes (including
/// <see cref="WriteAsync"/>, <see cref="CommitAsync"/>, <see cref="AbortTransaction"/>,
/// <see cref="PruneBelowAsync"/>) take a single <see cref="SemaphoreSlim"/> that protects
/// chain mutation, transaction-state mutation, and snapshot-set mutation against each other.
/// </para>
/// </remarks>
/// <para><b>Concurrent committed same-key writers (bare-store regime only):</b> the chain is
/// head-inserted in write order, so two CONCURRENT transactions that both commit writes to one
/// key resolve by physical write order, not transaction-id order. The SI wrapper's
/// first-committer-wins check makes that regime unreachable (one of the two aborts); callers
/// using the bare store with unsynchronised concurrent writers accept write-order resolution.</para>
/// <typeparam name="TKey">The key type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
public sealed class MvccStore<TKey, TValue> : IMvccStore<TKey, TValue>
    where TKey : IComparable<TKey>
{
    private enum TransactionState { Active, Committed, Aborted }

    // Lock-free read path: TryGetValue returns a stable reference to the immutable list at
    // the current head; readers walk the list without taking the gate.
    private readonly ConcurrentDictionary<TKey, ImmutableList<VersionedValue<TValue>>> _versionChains
        = new();

    // Per-transaction state and snapshot-set tables — read by ReadAsync and BeginTransaction
    // outside the gate (ConcurrentDictionary reads are atomic). Writes take the gate.
    private readonly ConcurrentDictionary<long, TransactionState> _transactionStates = new();
    private readonly ConcurrentDictionary<long, ImmutableHashSet<long>> _snapshots = new();

    // Floor captured at each transaction's begin-time. A reader uses this to determine whether a
    // pruned-committed version is visible: if version.TransactionId <= floor-at-begin, the version
    // committed before any still-relevant GC watermark and is unconditionally visible (it is the
    // "always-visible base" that prune folded into the floor). Per-transaction capture is required
    // so a transaction's view is stable — a subsequent PruneBelowAsync call that advances the
    // global _committedFloor must not change this transaction's floor mid-read.
    private readonly ConcurrentDictionary<long, long> _floorAtBegin = new();

    // Per-transaction list of versions written, used by CommitAsync to flip IsCommitted on
    // every version this transaction produced without scanning every chain.
    private readonly ConcurrentDictionary<long, List<VersionedValue<TValue>>> _writesByTransaction
        = new();

    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _nextTransactionId;

    // The highest committed transaction id that has been pruned by PruneBelowAsync. Any version
    // whose writer's id is <= this floor was committed before the watermark was established, so
    // it is permanently visible to ALL transactions regardless of whether the writer's id still
    // appears in _transactionStates or in a reader's snapshot. This prevents PruneBelowAsync
    // from making a committed version invisible to transactions begun after the prune.
    //
    // Captured per-transaction at BeginTransaction (into the snapshot structure below) so that
    // an active transaction's floor cannot advance under it while it is reading — the view is
    // stable from begin to commit/abort, matching the snapshot-isolation contract.
    private long _committedFloor;

    private int _disposed;

    /// <inheritdoc />
    public long BeginTransaction()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        _gate.Wait();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            var txnId = Interlocked.Increment(ref _nextTransactionId);
            _transactionStates[txnId] = TransactionState.Active;

            // Snapshot: record which transactions are committed right now. Atomic read on the
            // ConcurrentDictionary; the snapshot is frozen via ImmutableHashSet so readers can
            // capture it without re-checking under the gate.
            var committedAtStart = ImmutableHashSet.CreateBuilder<long>();
            foreach (var (id, state) in _transactionStates)
            {
                if (state == TransactionState.Committed)
                {
                    committedAtStart.Add(id);
                }
            }

            _snapshots[txnId] = committedAtStart.ToImmutable();

            // Capture the current committed floor so this transaction's visibility rule is stable
            // for its entire lifetime. A later PruneBelowAsync call may advance _committedFloor,
            // but this transaction reads versions against the floor that existed when it began —
            // exactly the snapshot-isolation guarantee applied to the prune watermark.
            _floorAtBegin[txnId] = _committedFloor;

            return txnId;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Reading through an <b>aborted</b> handle throws: an aborted transaction's writes
    /// logically never happened, yet the own-write visibility rule below would otherwise
    /// serve them back to the very transaction that aborted them. Reading through a
    /// <b>committed</b> handle stays allowed — its writes are real and its begin-time
    /// snapshot is still a consistent view. The guard is a lock-free
    /// <see cref="ConcurrentDictionary{TKey, TValue}"/> read, preserving the lock-free
    /// read path.
    /// <para>
    /// <b>Abort-racing-read contract (caller-quiescence).</b> An <see cref="AbortTransaction"/>
    /// racing an in-flight <c>ReadAsync</c> on the same handle may still surface that
    /// transaction's own writes — the abort state-flip is not observed until after the
    /// lock-free aborted-handle check above completes. This is within the caller-quiescence
    /// contract: callers must not abort a handle while a <c>ReadAsync</c> on that handle is
    /// concurrently in flight, mirroring the same contract documented on
    /// <see cref="Dispose"/>.
    /// </para>
    /// <para>
    /// <b>Prune-racing-read.</b> A committed handle at or below a
    /// <see cref="PruneBelowAsync"/> watermark is retired by that prune. A read through it that
    /// races the prune needs no caller quiescence: it returns the handle's snapshot value or
    /// throws <see cref="InvalidOperationException"/>, because the prune removes the handle's
    /// snapshot before rewriting any chain and the read re-checks the snapshot after its walk.
    /// </para>
    /// </remarks>
    public Task<(TValue Value, bool Found)> ReadAsync(TKey key, long transactionId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentNullException.ThrowIfNull(key);

        if (_transactionStates.TryGetValue(transactionId, out var state) &&
            state == TransactionState.Aborted)
        {
            throw new InvalidOperationException(
                $"Transaction {transactionId} was aborted. Its writes logically never " +
                "happened, so reads through this handle are invalid — begin a new transaction.");
        }

        if (!_snapshots.TryGetValue(transactionId, out var snapshot))
        {
            // Unknown transaction id — distinguishes "key absent under a real snapshot"
            // (returns false) from "the caller passed an id that was never begun, was already
            // pruned, or was aborted long enough ago to be cleaned up." Silently returning
            // not-found would let buggy callers proceed with a torn view.
            throw new InvalidOperationException(
                $"Transaction {transactionId} is not active in this MvccStore. The id was never " +
                "returned by BeginTransaction, was already pruned, or was aborted and cleaned up.");
        }

        // Floor captured at this transaction's begin time. A version whose writer's id is <=
        // this floor was committed before any still-relevant GC watermark — it is the "always-
        // visible base" folded into the floor by PruneBelowAsync — and is unconditionally visible
        // to this reader. This prevents a prune from making a committed version invisible to
        // transactions begun after the prune (the post-prune phantom-read bug).
        _floorAtBegin.TryGetValue(transactionId, out var floorAtBegin);

        // Lock-free read path. ConcurrentDictionary.TryGetValue + ImmutableList walk; no gate.
        var result = ((TValue)default!, false);
        if (_versionChains.TryGetValue(key, out var chain))
        {
            // Walk the version chain (newest first) to find the latest visible version.
            // Visibility: own write OR in begin-time snapshot OR writer committed at/below the
            // committed floor captured at begin time (pruned-committed base — always visible).
            // The chain reference is captured at the start of the walk and is immutable, so a
            // concurrent writer's Insert produces a new list; the walk sees a consistent past
            // state of the chain.
            foreach (var version in chain)
            {
                if (version.TransactionId == transactionId ||
                    snapshot.Contains(version.TransactionId) ||
                    (version.IsCommitted && version.TransactionId <= floorAtBegin))
                {
                    result = version.IsDeleted ? ((TValue)default!, false) : (version.Value, true);
                    break;
                }
            }
        }

        // A committed handle at or below a prune watermark is retired by that prune, and its
        // visible version may be dropped because retired handles are not pinned. The prune
        // removes the handle's snapshot before rewriting any chain, so if the chain this read
        // walked was rewritten, the snapshot is already gone here: report the retirement rather
        // than a key the handle's snapshot actually holds as absent.
        if (!_snapshots.ContainsKey(transactionId))
        {
            throw new InvalidOperationException(
                $"Transaction {transactionId} was pruned while this read was in flight. Its " +
                "snapshot may no longer be readable — begin a new transaction.");
        }

        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public async Task WriteAsync(TKey key, TValue value, long transactionId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentNullException.ThrowIfNull(key);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            // Reject writes from non-active transactions under the gate so the visibility
            // check is consistent with the chain mutation. A write to a Committed or Aborted
            // txn would otherwise sit in the chain forever with IsCommitted=false (invisible
            // to all snapshots) but still bloat the chain and the per-txn write list,
            // deferring prune cost.
            if (!_transactionStates.TryGetValue(transactionId, out var state))
            {
                throw new InvalidOperationException(
                    $"Transaction {transactionId} is not known to this MvccStore. Call " +
                    "BeginTransaction to obtain a valid id before WriteAsync.");
            }
            if (state != TransactionState.Active)
            {
                throw new InvalidOperationException(
                    $"Transaction {transactionId} is in state {state}. Writes are only valid " +
                    "while the transaction is Active (between BeginTransaction and " +
                    "CommitAsync/AbortTransaction).");
            }

            var version = new VersionedValue<TValue>(value, transactionId, IsDeleted: false);

            _versionChains.AddOrUpdate(
                key,
                _ => [version],
                (_, existing) => existing.Insert(0, version));

            // Track per-transaction writes so CommitAsync can flip every version's IsCommitted
            // in O(W_txn) rather than scanning every chain in the store.
            var writesList = _writesByTransaction.GetOrAdd(transactionId, _ => []);
            writesList.Add(version);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task DeleteAsync(TKey key, long transactionId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentNullException.ThrowIfNull(key);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (!_transactionStates.TryGetValue(transactionId, out var state))
            {
                throw new InvalidOperationException(
                    $"Transaction {transactionId} is not known to this MvccStore. Call " +
                    "BeginTransaction to obtain a valid id before DeleteAsync.");
            }
            if (state != TransactionState.Active)
            {
                throw new InvalidOperationException(
                    $"Transaction {transactionId} is in state {state}. Deletes are only valid " +
                    "while the transaction is Active (between BeginTransaction and " +
                    "CommitAsync/AbortTransaction).");
            }

            // A tombstone is a versioned entry with IsDeleted=true. ReadAsync already handles
            // this flag — the chain walk returns (default, false) when the newest visible version
            // is a tombstone, so the key appears absent to any reader whose snapshot includes
            // this transaction's commit.
            var version = new VersionedValue<TValue>(default!, transactionId, IsDeleted: true);

            _versionChains.AddOrUpdate(
                key,
                _ => [version],
                (_, existing) => existing.Insert(0, version));

            var writesList = _writesByTransaction.GetOrAdd(transactionId, _ => []);
            writesList.Add(version);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<bool> CommitAsync(long transactionId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            // A false return is reserved for a genuine write-write conflict. Plain MVCC
            // snapshot isolation (this type) does NOT detect such conflicts — that is the job
            // of the SnapshotIsolationStore wrapper, which validates rw/wr/ww dependencies
            // before delegating here (see src/Distributed/README.md, "Naming caveat"). So the
            // only outcomes here are: commit (true) or a caller error (throw). Committing an
            // id that was never begun, was already committed, or was already aborted is a
            // programmer error — not a conflict — and must throw, mirroring the active-only
            // guard WriteAsync already enforces. Returning false for these states (the prior
            // behaviour) conflated "aborted due to a conflict" with "you committed a handle
            // that was not active", making the documented false contract unverifiable.
            if (!_transactionStates.TryGetValue(transactionId, out var state))
            {
                throw new InvalidOperationException(
                    $"Transaction {transactionId} is not known to this MvccStore. Call " +
                    "BeginTransaction to obtain a valid id before CommitAsync.");
            }
            if (state != TransactionState.Active)
            {
                throw new InvalidOperationException(
                    $"Transaction {transactionId} is in state {state}. Only an Active " +
                    "transaction can be committed (a false return is reserved for a write-write " +
                    "conflict, which this plain-MVCC store does not detect — that is the " +
                    "SnapshotIsolationStore wrapper's responsibility).");
            }

            // Flip IsCommitted on every version this transaction wrote. The volatile write inside
            // MarkCommitted ensures concurrent readers observe the transition without a gate.
            if (_writesByTransaction.TryGetValue(transactionId, out var writes))
            {
                foreach (var version in writes)
                {
                    version.MarkCommitted();
                }
            }

            _transactionStates[transactionId] = TransactionState.Committed;
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void AbortTransaction(long transactionId)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        _gate.Wait();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            // Validate the handle's state before flipping it to Aborted. An unconditional
            // write inserts a phantom Aborted entry for an id that was never begun (table
            // bloat; a later ReadAsync on that id then throws on the aborted-handle path
            // instead of the cleaner unknown-id path). A committed transaction's writes are
            // durable and cannot be retroactively undone, so aborting one is a caller error.
            // Re-aborting an already-aborted handle is idempotent (the terminal state is
            // already correct) — a no-op rather than a throw, so retry paths do not raise on
            // an outcome they already achieved.
            if (!_transactionStates.TryGetValue(transactionId, out var state))
            {
                throw new InvalidOperationException(
                    $"Transaction {transactionId} is not known to this MvccStore. Call " +
                    "BeginTransaction to obtain a valid id before AbortTransaction.");
            }
            if (state == TransactionState.Committed)
            {
                throw new InvalidOperationException(
                    $"Transaction {transactionId} is in state Committed. A committed " +
                    "transaction's writes are durable and cannot be retroactively aborted.");
            }
            if (state == TransactionState.Aborted)
            {
                // Idempotent: the handle is already in the terminal Aborted state.
                return;
            }

            _transactionStates[transactionId] = TransactionState.Aborted;

            // Eagerly clean up the floor entry for aborted transactions to avoid unbounded
            // growth. Committed transactions' floor entries are pruned via PruneBelowAsync
            // (same lifetime as the txn-state entry).
            _floorAtBegin.TryRemove(transactionId, out _);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    /// <returns>
    /// The lowest transaction id currently in <see cref="TransactionState.Active"/>, or
    /// <see cref="long.MaxValue"/> when no transaction is active. The sentinel makes
    /// <c>watermark &lt; MinActiveTransactionId</c> trivially safe for any real watermark
    /// when the store is idle.
    /// </returns>
    public long MinActiveTransactionId
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            // Lock-free — ConcurrentDictionary read.
            var min = long.MaxValue;
            foreach (var (id, state) in _transactionStates)
            {
                if (state == TransactionState.Active && id < min)
                {
                    min = id;
                }
            }
            return min;
        }
    }

    /// <inheritdoc />
    /// <summary>Test-only observability: current version-chain length for a key (0 when absent).
    /// Same convention as the other internal seams in this assembly.</summary>
    internal int GetVersionChainLengthForTesting(TKey key)
        => _versionChains.TryGetValue(key, out var chain) ? chain.Count : 0;

    public async Task<int> PruneBelowAsync(long watermark, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            // Safety gate: the caller must pass a watermark strictly less than every active txn.
            // A pruned version must not be observable by any in-flight reader.
            var minActive = long.MaxValue;
            foreach (var (id, state) in _transactionStates)
            {
                if (state == TransactionState.Active && id < minActive)
                {
                    minActive = id;
                }
            }

            if (watermark >= minActive)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(watermark),
                    watermark,
                    $"Watermark must be strictly less than MinActiveTransactionId ({minActive}); " +
                    "pruning at or above that boundary could drop a version a live reader still expects.");
            }

            // Clamp to the highest id allocated so far. The committed floor below treats every
            // id <= watermark as "committed before any later transaction began", which holds only
            // for ids that already exist and are terminal. An idle store accepts any watermark
            // (MinActiveTransactionId is long.MaxValue), so without the clamp the floor would
            // absorb ids not yet allocated and a later reader would see a writer that commits
            // after it began — a non-repeatable read snapshot isolation forbids. BeginTransaction
            // allocates under the same gate, so this read is stable for the rest of the prune.
            watermark = Math.Min(watermark, Interlocked.Read(ref _nextTransactionId));

            var removed = 0;

            // Capture every active transaction's snapshot once. Snapshot isolation keys
            // visibility on commit-relative-to-begin (the committed-at-start set), NOT on
            // transaction-id order: a low-id version that commits late can be the newest
            // committed version with id <= watermark (the "backdrop") yet be invisible to an
            // active reader whose snapshot predates that commit. Pruning by the backdrop rule
            // alone would then drop the older version that reader still needs (DDIA 2e p. 295 —
            // MVCC must retain every version any open snapshot can still read). We therefore
            // also pin, per key, the exact version each active snapshot currently reads.
            // A COMMITTED handle above the watermark is a reader too: its snapshot survives this
            // prune and ReadAsync keeps serving it, so its visible version is pinned like an
            // active reader's. Aborted handles are unreadable, and handles at or below the
            // watermark lose their snapshot below (reads then throw), so neither pins anything.
            var activeIds = new List<long>();
            var activeSnapshots = new List<ImmutableHashSet<long>>();
            var activeFloors = new List<long>();
            foreach (var (id, state) in _transactionStates)
            {
                if (state != TransactionState.Aborted && id > watermark && _snapshots.TryGetValue(id, out var snap))
                {
                    activeIds.Add(id);
                    activeSnapshots.Add(snap);
                    _floorAtBegin.TryGetValue(id, out var floor);
                    activeFloors.Add(floor);
                }
            }

            // Retire the handles at or below the watermark BEFORE rewriting any chain. Their
            // snapshots are not pinned, so the rewrite below can drop the only version such a
            // handle sees; ReadAsync is lock-free and re-checks the handle's snapshot after its
            // walk, so removing the residue first means a read that fetched a rewritten chain
            // always observes the retirement and throws rather than reporting the key absent.
            // Active ids are all above the watermark by the safety gate above, so this only
            // touches committed/aborted entries. The snapshot goes first: it is what ReadAsync
            // checks.
            foreach (var id in _transactionStates.Keys)
            {
                if (id <= watermark)
                {
                    _snapshots.TryRemove(id, out _);
                    _transactionStates.TryRemove(id, out _);
                    _writesByTransaction.TryRemove(id, out _);
                    _floorAtBegin.TryRemove(id, out _);
                }
            }

            foreach (var key in _versionChains.Keys)
            {
                if (!_versionChains.TryGetValue(key, out var chain))
                {
                    continue;
                }

                // Chain is newest-first. Find the newest committed version with id <= watermark
                // (the backdrop) — the version a transaction begun AFTER this prune will read.
                // Drop everything older, plus every non-committed version with id <= watermark.
                // Commit state is read directly from the version — no dependency on the
                // transaction-state dictionary, so the prune loop is correct without holding
                // additional read locks against committers.
                var backdropIndex = -1;
                for (var i = 0; i < chain.Count; i++)
                {
                    var v = chain[i];
                    if (v.TransactionId > watermark)
                    {
                        continue;
                    }
                    if (v.IsCommitted)
                    {
                        backdropIndex = i;
                        break;
                    }
                }

                // Pin the index each active reader resolves (its ReadAsync winner): the first
                // chain entry satisfying ANY of the reader's three visibility clauses — own
                // write, begin-time snapshot, or the begin-time commit FLOOR. The floor clause
                // matters when the visible version's writer was pruned from the registry
                // before the reader began (it is then in no snapshot): omitting it lets a
                // SECOND prune drop the only version that reader can see, flipping its read
                // mid-transaction. That version may have id <= watermark and differ from the
                // backdrop, so it must not be dropped. Indices reference the original chain
                // and stay valid as the drop loop below removes only higher indices
                // (descending order).
                var pinnedByActive = new HashSet<int>();
                for (var a = 0; a < activeIds.Count; a++)
                {
                    var readerId = activeIds[a];
                    var readerSnapshot = activeSnapshots[a];
                    var readerFloor = activeFloors[a];
                    for (var i = 0; i < chain.Count; i++)
                    {
                        var v = chain[i];
                        if (v.TransactionId == readerId
                            || readerSnapshot.Contains(v.TransactionId)
                            || (v.IsCommitted && v.TransactionId <= readerFloor))
                        {
                            pinnedByActive.Add(i);
                            break;
                        }
                    }
                }

                var newChain = chain;
                for (var i = newChain.Count - 1; i >= 0; i--)
                {
                    if (i == backdropIndex || pinnedByActive.Contains(i))
                    {
                        continue;
                    }
                    if (newChain[i].TransactionId <= watermark)
                    {
                        newChain = newChain.RemoveAt(i);
                        removed++;
                    }
                }

                if (!ReferenceEquals(newChain, chain))
                {
                    _versionChains[key] = newChain;
                }
            }

            // Advance the committed floor to the watermark. Every committed version with id <=
            // watermark is now part of the "always-visible base": its writer's id is gone from
            // _transactionStates, so a future BeginTransaction snapshot cannot include it, but
            // the floor ensures those versions remain visible via the floor-check in ReadAsync.
            // The floor never retreats (take the max in case of concurrent-but-serialised
            // multiple prune calls with non-monotone watermarks, which are unlikely but allowed).
            if (watermark > _committedFloor)
            {
                _committedFloor = watermark;
            }

            return removed;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Marks the store disposed. Idempotent — concurrent disposers are gated by
    /// <see cref="Interlocked.Exchange(ref int, int)"/>. After dispose, every public method throws
    /// <see cref="ObjectDisposedException"/>, including a call already queued on the gate: it wakes
    /// when the current holder releases, observes the disposal and throws. An operation that held the
    /// gate when Dispose ran completes normally.
    /// </summary>
    /// <remarks>
    /// The gate <see cref="SemaphoreSlim"/> is deliberately never disposed. It allocates no kernel
    /// handle unless <see cref="SemaphoreSlim.AvailableWaitHandle"/> is read, which this type never
    /// does, and disposing it under a holder would make the holder's release throw after its mutation
    /// ran and strand every queued caller forever. Lock-free reads are not gated: a
    /// <see cref="ReadAsync"/> already past its entry check when Dispose runs may still complete.
    /// </remarks>
    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
}
