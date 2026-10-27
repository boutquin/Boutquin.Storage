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

/// <summary>A point-key transaction surface for comparing read-committed and strict two-phase locking.</summary>
/// <typeparam name="TKey">Non-null immutable key.</typeparam><typeparam name="TValue">Application value, treated as immutable by the caller.</typeparam>
/// <remarks>These comparison stores are in memory, not durable participants. No range reads or predicate locks are supplied.</remarks>
public interface IIsolationComparisonStore<TKey, TValue> : IDisposable where TKey : notnull
{
    /// <summary>Begins an active transaction with a monotonically increasing local identity.</summary>
    /// <returns>The identity required for subsequent calls.</returns>
    long BeginTransaction();
    /// <summary>Reads own staged writes or committed state, taking a retained shared lock in strict mode.</summary>
    /// <param name="key">Non-null key.</param><param name="transactionId">Active identity.</param><param name="ct">Cancels a lock wait, without ending the transaction or releasing earlier locks.</param>
    /// <returns>Value and presence flag after any required lock is acquired.</returns>
    /// <exception cref="InvalidOperationException">The transaction is no longer active.</exception>
    /// <exception cref="ObjectDisposedException">The comparison store has been disposed.</exception>
    /// <exception cref="OperationCanceledException">Cancellation interrupted admission or a lock wait; earlier transaction locks remain held.</exception>
    Task<(TValue? Value, bool Found)> ReadAsync(TKey key, long transactionId, CancellationToken ct = default);
    /// <summary>Stages a write under an exclusive lock held until commit or abort.</summary>
    /// <param name="key">Non-null key.</param><param name="value">Replacement value; the caller must not mutate it.</param><param name="transactionId">Active identity.</param><param name="ct">Cancels a lock wait; earlier locks remain held.</param>
    /// <returns>Completes after staging, without publishing the write.</returns>
    /// <exception cref="InvalidOperationException">The transaction is no longer active.</exception>
    /// <exception cref="ObjectDisposedException">The comparison store has been disposed.</exception>
    /// <exception cref="OperationCanceledException">Cancellation interrupted admission or a lock wait; earlier transaction locks remain held.</exception>
    Task WriteAsync(TKey key, TValue value, long transactionId, CancellationToken ct = default);
    /// <summary>Stages a deletion under an exclusive lock held until completion.</summary>
    /// <param name="key">Non-null key.</param><param name="transactionId">Active identity.</param><param name="ct">Cancels a lock wait; earlier locks remain held.</param><returns>Completes after staging.</returns>
    /// <exception cref="InvalidOperationException">The transaction is no longer active.</exception>
    /// <exception cref="ObjectDisposedException">The comparison store has been disposed.</exception>
    /// <exception cref="OperationCanceledException">Cancellation interrupted admission or a lock wait; earlier transaction locks remain held.</exception>
    Task DeleteAsync(TKey key, long transactionId, CancellationToken ct = default);
    /// <summary>Atomically publishes this transaction's local write set and releases every lock.</summary>
    /// <param name="transactionId">Active identity.</param><param name="ct">Cancellation checked before publication; once publishing starts it completes atomically.</param><returns>Completes after publication and lock release.</returns>
    /// <exception cref="InvalidOperationException">The transaction is no longer active.</exception>
    /// <exception cref="ObjectDisposedException">The comparison store has been disposed.</exception>
    /// <exception cref="OperationCanceledException">Cancellation interrupted admission or a lock wait; earlier transaction locks remain held.</exception>
    Task CommitAsync(long transactionId, CancellationToken ct = default);
    /// <summary>Discards staged values and releases every lock.</summary>
    /// <param name="transactionId">Active identity.</param><param name="ct">Cancellation checked before discard.</param><returns>Completes after discard and lock release.</returns>
    /// <exception cref="InvalidOperationException">The transaction is no longer active.</exception>
    /// <exception cref="ObjectDisposedException">The comparison store has been disposed.</exception>
    /// <exception cref="OperationCanceledException">Cancellation interrupted admission or a lock wait; earlier transaction locks remain held.</exception>
    Task AbortAsync(long transactionId, CancellationToken ct = default);
}
