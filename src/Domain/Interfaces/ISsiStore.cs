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
/// Defines a serializable snapshot isolation (SSI) key-value store.
///
/// <para>SSI extends snapshot isolation by detecting serialization anomalies (write skew) at commit time.
/// It tracks read sets and write sets per transaction and checks for conflicts: if transaction T1 read a key
/// that T2 wrote (and T2 committed after T1 started), T1 must be aborted to prevent write skew.</para>
///
/// <para>Unlike two-phase locking, SSI is optimistic — transactions proceed without blocking and are only
/// aborted at commit time if a conflict is detected. This gives it better performance under low contention
/// while still providing serializable isolation.</para>
///
/// <para><b>Reference:</b> Kleppmann, <i>Designing Data-Intensive Applications</i> (O'Reilly, 2017), Ch. 7 —
/// "Serializable Snapshot Isolation (SSI)", based on Cahill et al. (2008).</para>
/// </summary>
/// <typeparam name="TKey">The key type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
public interface ISsiStore<TKey, TValue>
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
}
