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
/// Defines a multi-version concurrency control (MVCC) key-value store.
///
/// <para>MVCC keeps multiple versions of each value, keyed by transaction ID. Each transaction sees
/// a consistent snapshot determined by which transactions had committed when it started. Readers never
/// block writers and vice versa — the fundamental concurrency guarantee that underpins PostgreSQL's
/// MVCC implementation and similar systems.</para>
///
/// <para><b>Snapshot visibility rule:</b> A version is visible to transaction T if it was written by
/// a transaction that committed before T started, or if it was written by T itself.</para>
///
/// <para><b>Reference:</b> Kleppmann, <i>Designing Data-Intensive Applications</i> (O'Reilly, 2nd ed., 2026), Ch. 8 —
/// "Transaction Isolation and Multi-Version Concurrency Control".</para>
/// </summary>
/// <typeparam name="TKey">The key type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
/// <remarks>
/// <para>
/// <b>Scope:</b> In-process reference implementation in <c>Boutquin.Storage.Distributed</c>,
/// which is not published to NuGet (see <c>src/Distributed/README.md</c>). Not durable
/// across process restart, not intended for production deployment. The MVCC snapshot-isolation contract
/// is implemented for educational fidelity to DDIA 2e Ch. 8, but production-grade extensions
/// — commit-timestamp snapshots and pluggable garbage-collection policy — are not included. For production use, prefer FoundationDB / CockroachDB MVCC APIs.
/// </para>
/// <para>
/// <b>Disposable.</b> Implementations own a <see cref="System.Threading.SemaphoreSlim"/>
/// (or equivalent) for serialising chain mutation. Long-lived processes that create and
/// drop MVCC stores must dispose every instance to release the kernel waitable handle.
/// After <see cref="IDisposable.Dispose"/>, every public method throws
/// <see cref="ObjectDisposedException"/>.
/// </para>
/// </remarks>
public interface IMvccStore<TKey, TValue> : IDisposable
    where TKey : IComparable<TKey>
{
    /// <summary>
    /// Begins a new transaction and returns its unique, monotonically increasing transaction ID.
    /// </summary>
    /// <returns>A new transaction ID.</returns>
    long BeginTransaction();

    /// <summary>
    /// Reads the latest version of the given key that is visible to the specified transaction.
    /// </summary>
    /// <param name="key">The key to read.</param>
    /// <param name="transactionId">The reading transaction's ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A tuple of (Value, Found) where Found indicates whether a visible version exists.</returns>
    /// <exception cref="InvalidOperationException">The transaction was never begun, was aborted, or was retired by
    /// <see cref="PruneBelowAsync"/> — including a prune that retires it while this read is in flight, so a read racing the
    /// prune either returns the handle's snapshot value or throws, never a false not-found.</exception>
    Task<(TValue Value, bool Found)> ReadAsync(TKey key, long transactionId, CancellationToken ct = default);

    /// <summary>
    /// Writes a new version of the given key, tagged with the specified transaction ID.
    /// </summary>
    /// <param name="key">The key to write.</param>
    /// <param name="value">The value to store.</param>
    /// <param name="transactionId">The writing transaction's ID.</param>
    /// <param name="ct">Cancellation token.</param>
    Task WriteAsync(TKey key, TValue value, long transactionId, CancellationToken ct = default);

    /// <summary>
    /// Records a tombstone (logical delete) for the given key under the specified transaction.
    /// Readers that resolve the tombstone as the latest visible version observe the key as absent.
    /// </summary>
    /// <param name="key">The key to delete.</param>
    /// <param name="transactionId">The deleting transaction's ID; must be currently active.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// A tombstone is implemented as a <see cref="VersionedValue{TValue}"/> with
    /// <c>IsDeleted = true</c>. The version is subject to the same snapshot-visibility rules as
    /// any other version: a reader whose snapshot includes the deleting transaction will see the
    /// key as absent; a reader whose snapshot predates it still sees the prior value.
    /// After <see cref="PruneBelowAsync"/> the tombstone remains visible via the commit-floor
    /// mechanism, ensuring the delete is never undone by GC.
    /// </remarks>
    Task DeleteAsync(TKey key, long transactionId, CancellationToken ct = default);

    /// <summary>
    /// Commits the specified transaction, making its writes visible to future transactions.
    /// </summary>
    /// <param name="transactionId">The transaction ID to commit; must be currently active.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <see langword="true"/> when the transaction commits. A <see langword="false"/> return is
    /// reserved for an abort caused by a genuine write-write conflict detected at commit time.
    /// Plain MVCC (this contract) provides snapshot isolation without commit-time conflict
    /// detection, so a conforming plain-MVCC implementation returns <see langword="true"/> for
    /// every active transaction and never returns <see langword="false"/>; conflict detection is
    /// layered above by a snapshot-isolation store. A <see langword="false"/> return is therefore
    /// only observed through such a wrapping store, not through a bare MVCC store.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <paramref name="transactionId"/> is not in an active state — i.e. it was never
    /// returned by <see cref="BeginTransaction"/> (or has been pruned), or it has already been
    /// committed or aborted. These are caller errors, distinct from the conflict abort that a
    /// <see langword="false"/> return signals.
    /// </exception>
    Task<bool> CommitAsync(long transactionId, CancellationToken ct = default);

    /// <summary>
    /// Aborts the specified transaction. Versions written by it become invisible to all transactions.
    /// </summary>
    /// <param name="transactionId">The transaction ID to abort; must be active or already aborted.</param>
    /// <remarks>
    /// Aborting an already-aborted transaction is an idempotent no-op (the terminal state is
    /// already correct).
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <paramref name="transactionId"/> was never returned by
    /// <see cref="BeginTransaction"/> (or has been pruned), or when it has already committed — a
    /// committed transaction's writes are durable and cannot be retroactively undone.
    /// </exception>
    void AbortTransaction(long transactionId);

    /// <summary>
    /// The lowest transaction ID that is currently <c>Active</c>, or <see cref="long.MaxValue"/> if
    /// no transaction is active. Used to compute a safe watermark for <see cref="PruneBelowAsync"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The caller passes <c>MinActiveTransactionId - 1</c> (or any strictly lower value) to
    /// <see cref="PruneBelowAsync"/>; higher values are rejected with
    /// <see cref="ArgumentOutOfRangeException"/> because a live reader may still observe the
    /// versions being dropped.
    /// </para>
    /// </remarks>
    long MinActiveTransactionId { get; }

    /// <summary>
    /// Drops version-chain entries and transaction residue whose transaction IDs are at or
    /// below <paramref name="watermark"/>. Intended to bound the long-running growth of the
    /// per-key version chains and the <c>transactionStates</c> / <c>snapshots</c> dictionaries
    /// in systems where transactions are long-lived (DDIA 2e Ch. 8, snapshot-isolation
    /// garbage collection).
    /// </summary>
    /// <param name="watermark">
    /// A transaction ID strictly less than <see cref="MinActiveTransactionId"/>. No version
    /// visible to any in-flight reader can be dropped: for each chain, the store preserves the
    /// newest <em>committed</em> version whose ID is <c>&lt;= watermark</c> so snapshots that
    /// predate the prune still observe the correct pre-prune value, plus the version each
    /// retained handle (active, or committed with an ID above the watermark) currently reads.
    /// A watermark above the highest transaction ID allocated so far — e.g. the idle-store
    /// <c>MinActiveTransactionId - 1</c> — is clamped to that ID, so transactions begun after
    /// the prune keep snapshot visibility against each other. Handles at or below the watermark are retired: a read through
    /// one that is in flight while the prune runs returns its snapshot's value or throws <see cref="InvalidOperationException"/>.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The total number of version-chain entries removed (diagnostic).</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="watermark"/> is <c>&gt;= MinActiveTransactionId</c>; pruning
    /// at or above that boundary could drop a version a live reader still expects.
    /// </exception>
    Task<int> PruneBelowAsync(long watermark, CancellationToken ct = default);
}
