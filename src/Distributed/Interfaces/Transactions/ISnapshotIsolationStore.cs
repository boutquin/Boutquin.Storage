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
namespace Boutquin.Storage.Distributed.Interfaces.Transactions;

/// <summary>
/// Plain Snapshot Isolation (SI) key-value store, per DDIA 2e p. 293–299.
///
/// <para>Readers see a consistent snapshot of every key (the version visible under the
/// reader's start-time snapshot); writers detect concurrent point-key writes at commit time
/// and abort one. This is textbook Snapshot Isolation — readers never block writers, writers
/// never block readers. Tracking read sets enables a degree of conflict detection that goes
/// beyond plain SI but is still narrower than full SSI.</para>
///
/// <para><b>NOT Serializable Snapshot Isolation (SSI).</b> The type was previously named
/// <c>ISsiStore</c>/<c>SsiStore</c> but lacked the predicate-range tracking that DDIA p. 317–321
/// identifies as the load-bearing distinction between SI and SSI — without it, the store cannot
/// detect write-skew anomalies on phantoms. The rename brings the type's name in line with its
/// actual contract. A future <c>SerializableSnapshotIsolationStore</c> would layer predicate
/// locks atop this type as a separate component.</para>
///
/// <para><b>Reference:</b> Kleppmann &amp; Riccomini, <i>Designing Data-Intensive Applications</i>,
/// 2nd ed. (O'Reilly, 2026), Ch. 8 — "Snapshot Isolation and Repeatable Read" (p. 293–299).</para>
/// </summary>
/// <typeparam name="TKey">The key type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
/// <remarks>
/// <para>
/// <b>Scope:</b> In-process reference implementation in <c>Boutquin.Storage.Distributed</c>,
/// which is not published to NuGet (see <c>src/Distributed/README.md</c>). Not durable
/// across process restart, not intended for production deployment. Production-grade extensions —
/// predicate-range tracking (the SSI upgrade), rw-dependency indexes, read-only-transaction
/// fast paths — are not included. For production use, prefer FoundationDB / CockroachDB.
/// </para>
/// <para>
/// <b>Disposable.</b> Implementations own a <see cref="System.Threading.SemaphoreSlim"/>
/// (or equivalent) for read/write-set bookkeeping, and may compose an inner
/// <see cref="IMvccStore{TKey, TValue}"/> that is itself disposable. Disposing the SI store
/// disposes the inner MVCC store. After <see cref="IDisposable.Dispose"/>, every public
/// method throws <see cref="ObjectDisposedException"/>, including a call already queued behind an
/// in-flight one; the in-flight call completes before the inner store is disposed.
/// </para>
/// </remarks>
public interface ISnapshotIsolationStore<TKey, TValue> : IDisposable
    where TKey : IComparable<TKey>
{
    /// <summary>
    /// Begins a new transaction and returns its unique, monotonically increasing transaction ID.
    /// </summary>
    long BeginTransaction();

    /// <summary>
    /// Reads the latest visible version of the given key. Records the key in the transaction's read set
    /// for conflict detection at commit time.
    /// </summary>
    Task<(TValue Value, bool Found)> ReadAsync(TKey key, long transactionId, CancellationToken ct = default);

    /// <summary>
    /// Writes a new version of the given key. Records the key in the transaction's write set
    /// for conflict detection at commit time.
    /// </summary>
    Task WriteAsync(TKey key, TValue value, long transactionId, CancellationToken ct = default);

    /// <summary>
    /// Commits the transaction if no serialization conflicts are detected.
    /// Returns false (and aborts) if write skew or other serialization anomalies are found.
    /// </summary>
    Task<bool> CommitAsync(long transactionId, CancellationToken ct = default);

    /// <summary>
    /// Aborts the specified transaction.
    /// </summary>
    void AbortTransaction(long transactionId);

    /// <summary>
    /// The lowest transaction ID that is currently active (has not yet committed or aborted),
    /// or <see cref="long.MaxValue"/> if no transaction is active. Used to compute a safe watermark
    /// for <see cref="PruneCommittedBelowAsync"/>.
    /// </summary>
    long MinActiveCommitTimestamp { get; }

    /// <summary>
    /// Drops committed-transaction residue (<c>_committedTxns</c>, <c>_committedWriteSets</c>,
    /// <c>_committedReadSets</c>) for transaction IDs strictly less than
    /// <paramref name="watermark"/>. Intended to bound long-running growth of the committed-txn
    /// registry used for SSI's commit-time conflict detection — every commit adds to these sets,
    /// and under a long-lived process they grow without bound otherwise.
    /// </summary>
    /// <param name="watermark">
    /// A transaction ID strictly less than <see cref="MinActiveCommitTimestamp"/>. The id bound
    /// alone is necessary but NOT sufficient: transaction ids are begin order, not commit order,
    /// so a low-id transaction that committed after a still-active transaction began is
    /// CONCURRENT with it and its residue is still needed for that transaction's commit-time
    /// conflict detection. The store therefore additionally verifies commit order internally
    /// and retains any entry some active transaction may still conflict with — a later prune
    /// (after that transaction finishes) removes it. The returned count reflects only entries
    /// actually removed.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of committed-transaction entries removed.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="watermark"/> is greater than or equal to
    /// <see cref="MinActiveCommitTimestamp"/>; pruning at or beyond that boundary could drop residue
    /// that a still-active transaction needs at commit time to detect a conflict.
    /// </exception>
    Task<int> PruneCommittedBelowAsync(long watermark, CancellationToken ct = default);
}
