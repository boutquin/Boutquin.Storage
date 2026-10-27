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

/// <summary>A point-key serializable comparison store retaining shared and exclusive locks until completion.</summary>
/// <typeparam name="TKey">Non-null immutable key with stable equality.</typeparam><typeparam name="TValue">Caller-owned immutable value.</typeparam>
/// <remarks><para>Shared locks cover absent-key reads too, preventing point-key insert anomalies. Writers and lock upgrades wait for all conflicting readers. Deadlocks are detected when a wait would close a wait-for cycle: the requester is aborted as the victim (its locks released, its writes discarded) and its pending operation fails with <see cref="TransactionDeadlockException"/>; acknowledge with AbortAsync and retry in a new transaction. No timeout releases locks implicitly.</para>
/// <para><b>Concurrency:</b> Internally serialized state with asynchronous cancellable lock waits. Lock scheduling is not FIFO;
/// starvation is possible. No ranges, predicates, disk persistence or distributed commit is supplied.</para>
/// <para><b>Cost:</b> Uncontended point access is O(1) expected; completion is O(touched keys), waking blocked operations to recheck.
/// Reference: DDIA, weak isolation and two-phase locking.</para></remarks>
public sealed class StrictTwoPhaseLockingStore<TKey, TValue> : IIsolationComparisonStore<TKey, TValue> where TKey : notnull
{
    private readonly PointIsolationEngine<TKey, TValue> _engine = new(true);
    /// <summary>Creates an empty in-memory comparison store without external resources.</summary>
    public StrictTwoPhaseLockingStore() { }
    /// <inheritdoc />
    public long BeginTransaction() => _engine.Begin();
    /// <inheritdoc />
    public Task<(TValue? Value, bool Found)> ReadAsync(TKey key, long transactionId, CancellationToken ct = default) => _engine.ReadAsync(key, transactionId, ct);
    /// <inheritdoc />
    public Task WriteAsync(TKey key, TValue value, long transactionId, CancellationToken ct = default) => _engine.WriteAsync(key, value, true, transactionId, ct);
    /// <inheritdoc />
    public Task DeleteAsync(TKey key, long transactionId, CancellationToken ct = default) => _engine.WriteAsync(key, default, false, transactionId, ct);
    /// <inheritdoc />
    public Task CommitAsync(long transactionId, CancellationToken ct = default) => _engine.CompleteAsync(transactionId, true, ct);
    /// <inheritdoc />
    public Task AbortAsync(long transactionId, CancellationToken ct = default) => _engine.CompleteAsync(transactionId, false, ct);
    /// <summary>Idempotently abandons all in-memory state and wakes pending operations with ObjectDisposedException; it is not a commit.</summary>
    public void Dispose() => _engine.Dispose();
}
