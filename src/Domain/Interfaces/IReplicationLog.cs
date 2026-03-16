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
/// Defines a replication log that records key-value mutations with sequence numbers for follower catch-up.
///
/// <para>The replication log is an ordered sequence of mutations that followers can replay to converge
/// with the leader's state. Each entry has a monotonically increasing sequence number that enables
/// followers to request only entries they haven't seen yet.</para>
///
/// <para><b>Reference:</b> Kleppmann, <i>Designing Data-Intensive Applications</i> (O'Reilly, 2017), Ch. 5 —
/// "Replication Logs and Change Data Capture".</para>
/// </summary>
/// <typeparam name="TKey">The key type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
public interface IReplicationLog<TKey, TValue>
    where TKey : IComparable<TKey>
{
    /// <summary>
    /// Appends an entry to the replication log.
    /// </summary>
    /// <param name="key">The key that was modified.</param>
    /// <param name="value">The new value.</param>
    /// <param name="sequenceNumber">The monotonically increasing sequence number for this entry.</param>
    /// <param name="ct">Cancellation token.</param>
    Task AppendAsync(TKey key, TValue value, long sequenceNumber, CancellationToken ct = default);

    /// <summary>
    /// Returns all log entries with sequence numbers greater than the specified value.
    /// Used by followers to catch up from their last applied sequence number.
    /// </summary>
    /// <param name="afterSequenceNumber">The sequence number to start from (exclusive).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An ordered list of entries after the given sequence number.</returns>
    Task<IReadOnlyList<(TKey Key, TValue Value, long SequenceNumber)>> GetEntriesAfterAsync(
        long afterSequenceNumber, CancellationToken ct = default);

    /// <summary>
    /// Returns the highest sequence number in the log, or 0 if the log is empty.
    /// </summary>
    long GetLatestSequenceNumber();
}
