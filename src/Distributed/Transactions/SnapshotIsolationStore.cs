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
namespace Boutquin.Storage.Distributed.Transactions;

/// <summary>
/// Plain Snapshot Isolation store (DDIA 2e p. 293–299) that wraps an
/// <see cref="MvccStore{TKey, TValue}"/> and adds point-key conflict detection at commit time.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope:</b> In-process reference implementation in <c>Boutquin.Storage.Distributed</c>,
/// which is not published to NuGet (see <c>src/Distributed/README.md</c>). Not durable
/// across process restart, not intended for production deployment. Production-grade extensions —
/// commit-timestamp snapshots, predicate-range tracking (the SSI upgrade — DDIA p. 317–321),
/// read-only-transaction optimisation — are not included. For production use, prefer
/// <a href="https://www.foundationdb.org">FoundationDB</a>,
/// <a href="https://www.cockroachlabs.com">CockroachDB</a>, or SQL Server 2022 In-Memory OLTP.
/// </para>
///
/// <para>
/// <b>What this is.</b> Plain Snapshot Isolation per DDIA 2e p. 293–299. Readers see a consistent
/// snapshot; writers detect concurrent point-key writes at commit time and abort one. Read sets
/// and write sets are tracked per transaction so the commit-time check goes beyond pure
/// last-writer-wins (rw- and wr-dependency point-key checks), but the implementation does
/// <i>not</i> track predicate-range reads — write skew on phantoms is undetected.
/// </para>
///
/// <para>
/// <b>What this is NOT.</b> Serializable Snapshot Isolation (SSI; DDIA p. 317–321). SSI requires
/// predicate-range tracking to detect write-skew anomalies on phantoms; the type was
/// previously named <c>SsiStore</c>, which over-promised against the implementation. The rename to
/// <c>SnapshotIsolationStore</c> scopes the contract honestly; a future <c>SerializableSnapshotIsolationStore</c>
/// would layer predicate locks atop this type as a separate component.
/// </para>
///
/// <para>
/// <b>How point-key conflict detection works:</b>
/// </para>
/// <list type="bullet">
/// <item>For each key in this transaction's read set, check if any concurrent transaction wrote to that key
/// and committed since this transaction started (rw-dependency → abort).</item>
/// <item>For each key in this transaction's write set, check if any concurrent transaction read that key
/// and committed since this transaction started (wr-dependency forming a potential cycle → abort).</item>
/// </list>
///
/// <para>
/// <b>Why optimistic?</b> Snapshot Isolation is an optimistic concurrency-control scheme by
/// construction — commits validate without read locks. Transactions proceed without blocking;
/// conflicts surface at commit time only.
/// </para>
///
/// <para>
/// <b>Thread safety:</b> Delegates to the underlying MvccStore's thread safety, plus uses its own
/// <see cref="SemaphoreSlim"/> for read/write set tracking. Both <see cref="ReadAsync"/> and
/// <see cref="WriteAsync"/> serialise through that gate (gate-first, then MVCC) so that
/// cancellation cannot tear the bookkeeping invariant. MvccStore reads are lock-free, so the
/// gate-held duration is bounded by the chain-walk cost.
/// </para>
/// <para>
/// <b>Commit-time cost.</b> <see cref="CommitAsync"/> walks <c>_committedTxns</c> twice (once
/// for rw-conflicts, once for wr-conflicts), so commit cost grows with the size of the
/// committed-txn registry. Long-running workloads MUST schedule periodic
/// <see cref="PruneCommittedBelowAsync"/> calls with a watermark below
/// <see cref="MinActiveCommitTimestamp"/>; without prune, commit becomes O(N²) in the number
/// of completed transactions.
/// </para>
/// </remarks>
/// <typeparam name="TKey">The key type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
public sealed class SnapshotIsolationStore<TKey, TValue> : ISnapshotIsolationStore<TKey, TValue>
    where TKey : IComparable<TKey>
{
    private readonly MvccStore<TKey, TValue> _mvcc = new();

    /// <summary>Test-only observability of the inner MVCC store (chain-length assertions).</summary>
    internal MvccStore<TKey, TValue> InnerMvccForTesting => _mvcc;
    private readonly Dictionary<long, HashSet<TKey>> _readSets = [];
    private readonly Dictionary<long, HashSet<TKey>> _writeSets = [];
    private readonly Dictionary<long, HashSet<long>> _committedAtStart = [];
    private readonly HashSet<long> _committedTxns = [];
    private readonly Dictionary<long, HashSet<TKey>> _committedWriteSets = [];
    private readonly Dictionary<long, HashSet<TKey>> _committedReadSets = [];

    // Commit-ORDER bookkeeping for the registry prune. Transaction ids are BEGIN order, so
    // "id < every active id" does not imply "committed before every active txn began" — a
    // low-id txn that commits late is CONCURRENT with actives that began before its commit,
    // and its registry residue is exactly what their commit-time conflict loops consult.
    // _commitSequence is a monotone counter bumped at each successful commit;
    // _commitSeqAtBegin captures the counter each txn observed at begin; _commitSeqOf records
    // the counter value at which each committed txn landed. A committed txn is safely
    // prunable iff its commit sequence is <= every active txn's begin sequence.
    private long _commitSequence;
    private readonly Dictionary<long, long> _commitSeqAtBegin = [];
    private readonly Dictionary<long, long> _commitSeqOf = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _disposed;

    /// <inheritdoc />
    public long BeginTransaction()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        _gate.Wait();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            var txnId = _mvcc.BeginTransaction();
            _readSets[txnId] = [];
            _writeSets[txnId] = [];
            // Snapshot which txns are already committed — anything NOT in this set is concurrent
            _committedAtStart[txnId] = [.. _committedTxns];
            _commitSeqAtBegin[txnId] = _commitSequence;
            return txnId;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<(TValue Value, bool Found)> ReadAsync(TKey key, long transactionId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        // Gate-first ordering. MvccStore reads are lock-free so holding the SI gate
        // across the read costs only the chain walk, not any contended lock. The previous
        // mvcc-then-gate ordering meant a cancellation between the two awaits would persist
        // the MVCC visibility effect with no read-set entry — commit-time conflict detection
        // would then miss the operation and a should-have-aborted commit could succeed.
        // Atomic-or-nothing under one gate eliminates that window.
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            var result = await _mvcc.ReadAsync(key, transactionId, ct).ConfigureAwait(false);
            if (_readSets.TryGetValue(transactionId, out var readSet))
            {
                readSet.Add(key);
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task WriteAsync(TKey key, TValue value, long transactionId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        // Gate-first ordering — see ReadAsync above. Cancellation between the MVCC mutation
        // and the write-set update would otherwise leave a chain entry with no commit-time
        // conflict tracking, allowing a wr-cycle commit to slip through.
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            await _mvcc.WriteAsync(key, value, transactionId, ct).ConfigureAwait(false);
            if (_writeSets.TryGetValue(transactionId, out var writeSet))
            {
                writeSet.Add(key);
            }
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
            var myReadSet = _readSets.GetValueOrDefault(transactionId) ?? [];
            var myWriteSet = _writeSets.GetValueOrDefault(transactionId) ?? [];

            var snapshot = _committedAtStart.GetValueOrDefault(transactionId) ?? [];

            // Check for rw-dependency conflicts:
            // If any concurrent transaction (committed after we started) wrote to a key we read, abort.
            foreach (var committedTxn in _committedTxns)
            {
                if (snapshot.Contains(committedTxn))
                {
                    continue; // Not concurrent — was already committed when we started
                }

                if (_committedWriteSets.TryGetValue(committedTxn, out var theirWriteSet) &&
                    myReadSet.Overlaps(theirWriteSet))
                {
                    _mvcc.AbortTransaction(transactionId);
                    CleanupTransaction(transactionId);
                    return false;
                }
            }

            // Check for wr-dependency conflicts:
            // If any concurrent transaction (committed after we started) read a key we wrote, abort.
            foreach (var committedTxn in _committedTxns)
            {
                if (snapshot.Contains(committedTxn))
                {
                    continue;
                }

                if (_committedReadSets.TryGetValue(committedTxn, out var theirReadSet) &&
                    myWriteSet.Overlaps(theirReadSet))
                {
                    _mvcc.AbortTransaction(transactionId);
                    CleanupTransaction(transactionId);
                    return false;
                }
            }

            // Check for ww-dependency conflicts (first-committer-wins):
            // If any concurrent transaction wrote a key we also wrote, abort. The rw-check above
            // catches the read-modify-write lost-update path (the "modify" reads the key first),
            // but two BLIND concurrent writes to the same key have empty read sets and would both
            // commit — the lost update DDIA 2e p. 299 ("Preventing Lost Updates") prevents via first-committer-wins. This also
            // satisfies the ISnapshotIsolationStore contract ("detect concurrent point-key writes").
            foreach (var committedTxn in _committedTxns)
            {
                if (snapshot.Contains(committedTxn))
                {
                    continue;
                }

                if (_committedWriteSets.TryGetValue(committedTxn, out var theirWriteSet) &&
                    myWriteSet.Overlaps(theirWriteSet))
                {
                    _mvcc.AbortTransaction(transactionId);
                    CleanupTransaction(transactionId);
                    return false;
                }
            }

            // No conflicts — commit
            var committed = await _mvcc.CommitAsync(transactionId, ct).ConfigureAwait(false);
            if (committed)
            {
                _committedTxns.Add(transactionId);
                _committedWriteSets[transactionId] = myWriteSet;
                _committedReadSets[transactionId] = myReadSet;
                _commitSeqOf[transactionId] = ++_commitSequence;

                // Drop the ephemeral (active-transaction) bookkeeping now that the durable
                // committed-registry copies above carry the conflict-detection state. Leaving
                // the ephemeral entries behind would resurrect this id as "active" the moment
                // PruneCommittedBelowAsync removes it from _committedTxns — misreporting
                // MinActiveCommitTimestamp and wedging every subsequent prune.
                CleanupTransaction(transactionId);
            }

            return committed;
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
            _mvcc.AbortTransaction(transactionId);
            CleanupTransaction(transactionId);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void CleanupTransaction(long transactionId)
    {
        _readSets.Remove(transactionId);
        _writeSets.Remove(transactionId);
        _committedAtStart.Remove(transactionId);
        _commitSeqAtBegin.Remove(transactionId);
    }

    /// <inheritdoc />
    public long MinActiveCommitTimestamp
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            _gate.Wait();
            try
            {
                ObjectDisposedException.ThrowIf(_disposed != 0, this);
                return ComputeMinActiveLocked();
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    /// <summary>
    /// Computes the lowest transaction ID that is currently active. Ephemeral read/write-set
    /// entries exist exactly for the active transactions: <see cref="CommitAsync"/> (both the
    /// conflict-abort and the success path) and <see cref="AbortTransaction"/> all run
    /// <see cref="CleanupTransaction"/>, so <c>_readSets.Keys</c> IS the active set.
    /// </summary>
    private long ComputeMinActiveLocked()
    {
        var min = long.MaxValue;
        foreach (var id in _readSets.Keys)
        {
            if (id < min)
            {
                min = id;
            }
        }
        return min;
    }

    /// <inheritdoc />
    public async Task<int> PruneCommittedBelowAsync(long watermark, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            // Safety gate: watermark must be strictly less than the oldest active txn id.
            // A committed txn C with id < minActive may still have committed AFTER the oldest
            // active T began; dropping C's registry before T commits would cause T's rw/wr
            // conflict loop to miss a conflict with C. The caller is responsible for passing a
            // watermark that represents a point at which every committed txn below it is in
            // every active txn's snapshot — the library's API surface only checks the id bound.
            var minActive = ComputeMinActiveLocked();
            if (watermark >= minActive)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(watermark),
                    watermark,
                    $"Watermark must be strictly less than MinActiveCommitTimestamp ({minActive}); " +
                    "pruning at or above that boundary could drop committed-txn residue a " +
                    "still-active transaction needs at commit time for conflict detection.");
            }

            // The id bound alone is NOT sufficient: ids are begin order, and a low-id txn
            // that committed AFTER an active txn began is concurrent with it — its residue is
            // exactly what that active txn's conflict loops need at commit time. Drop a
            // committed txn only when its commit sequence is at or below every active txn's
            // begin sequence (it was already committed when each of them began, so each sees
            // it via _committedAtStart, never via the concurrent-conflict loops). Entries not
            // yet safe are retained and picked up by a later prune.
            var minActiveBeginSeq = long.MaxValue;
            foreach (var id in _readSets.Keys)
            {
                if (_commitSeqAtBegin.TryGetValue(id, out var beginSeq) && beginSeq < minActiveBeginSeq)
                {
                    minActiveBeginSeq = beginSeq;
                }
            }

            var removed = 0;
            var toDrop = _committedTxns
                .Where(id => id < watermark
                    && _commitSeqOf.TryGetValue(id, out var seq)
                    && seq <= minActiveBeginSeq)
                .ToList();
            foreach (var id in toDrop)
            {
                _committedTxns.Remove(id);
                _committedWriteSets.Remove(id);
                _committedReadSets.Remove(id);
                _commitSeqOf.Remove(id);
                removed++;
            }

            // Bound BOTH growth vectors the long-running-workload remark promises to solve:
            // the registry above AND the inner MVCC store's version chains + transaction
            // bookkeeping (the inner store is private, so its prune is unreachable except
            // through here). The inner prune applies its own pinning rules (backdrop + every
            // active reader's visible version incl. the begin-time floor), so the shared
            // watermark is safe: SI's guard already established watermark < minActive.
            _ = await _mvcc.PruneBelowAsync(watermark, ct).ConfigureAwait(false);

            return removed;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Disposes the store and its inner <see cref="MvccStore{TKey, TValue}"/>. Idempotent —
    /// concurrent disposers are gated by <see cref="Interlocked.Exchange(ref int, int)"/>. After
    /// dispose, every public method throws <see cref="ObjectDisposedException"/>, including a call
    /// already queued on the gate: it wakes, observes the disposal and throws. An operation that held
    /// the gate when Dispose ran completes normally first; Dispose waits for it.
    /// </summary>
    /// <remarks>
    /// The inner store is disposed only once that holder has released the gate, so an in-flight
    /// operation never meets a disposed inner store mid-way through its bookkeeping. The gate
    /// <see cref="SemaphoreSlim"/> is deliberately never disposed: it allocates no kernel handle
    /// unless <see cref="SemaphoreSlim.AvailableWaitHandle"/> is read, which this type never does,
    /// and a disposed semaphore neither wakes its waiters nor accepts the holder's release.
    /// </remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _gate.Wait();
        try
        {
            _mvcc.Dispose();
        }
        finally
        {
            _gate.Release();
        }
    }
}
