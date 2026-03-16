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
namespace Boutquin.Storage.Domain.Interfaces;

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
/// <para><b>Reference:</b> Kleppmann, <i>Designing Data-Intensive Applications</i> (O'Reilly, 2017), Ch. 7 —
/// "Transaction Isolation and Multi-Version Concurrency Control".</para>
/// </summary>
/// <typeparam name="TKey">The key type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
public interface IMvccStore<TKey, TValue>
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
    /// Commits the specified transaction, making its writes visible to future transactions.
    /// </summary>
    /// <param name="transactionId">The transaction ID to commit.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the transaction committed successfully; false if it was aborted due to a conflict.</returns>
    Task<bool> CommitAsync(long transactionId, CancellationToken ct = default);

    /// <summary>
    /// Aborts the specified transaction. Versions written by it become invisible to all transactions.
    /// </summary>
    /// <param name="transactionId">The transaction ID to abort.</param>
    void AbortTransaction(long transactionId);
}
