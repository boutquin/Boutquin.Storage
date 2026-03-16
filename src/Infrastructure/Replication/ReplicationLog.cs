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
namespace Boutquin.Storage.Infrastructure.Replication;

/// <summary>
/// An in-memory replication log that stores key-value mutations with monotonically increasing sequence numbers.
/// </summary>
/// <remarks>
/// <para>
/// <b>Thread safety:</b> This class is not thread-safe. The caller (e.g., <see cref="SingleLeaderReplication{TKey, TValue}"/>)
/// is responsible for synchronization.
/// </para>
/// </remarks>
/// <typeparam name="TKey">The key type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
public sealed class ReplicationLog<TKey, TValue> : IReplicationLog<TKey, TValue>
    where TKey : IComparable<TKey>
{
    private readonly List<(TKey Key, TValue Value, long SequenceNumber)> _entries = [];

    /// <inheritdoc />
    /// <remarks>
    /// <b>Monotonicity enforcement:</b> The sequence number must be strictly greater than the
    /// previous entry's sequence number. This is a fail-fast invariant — accepting out-of-order
    /// entries would violate the replication log contract and cause followers to miss mutations
    /// or apply them in the wrong order.
    /// </remarks>
    public Task AppendAsync(TKey key, TValue value, long sequenceNumber, CancellationToken ct = default)
    {
        var latest = GetLatestSequenceNumber();
        if (sequenceNumber <= latest)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sequenceNumber),
                sequenceNumber,
                $"Sequence number must be strictly greater than the latest ({latest}). " +
                "Replication log entries must be monotonically increasing.");
        }

        _entries.Add((key, value, sequenceNumber));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <b>Why binary search?</b> The log is append-only with monotonically increasing sequence
    /// numbers, so entries are always sorted. Binary search finds the starting point in O(log n)
    /// instead of scanning all entries in O(n). For a replication log that may contain millions
    /// of entries, this is a significant performance improvement for follower catch-up.
    /// </remarks>
    public Task<IReadOnlyList<(TKey Key, TValue Value, long SequenceNumber)>> GetEntriesAfterAsync(
        long afterSequenceNumber, CancellationToken ct = default)
    {
        // Binary search for the first entry with SequenceNumber > afterSequenceNumber
        var lo = 0;
        var hi = _entries.Count;

        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (_entries[mid].SequenceNumber <= afterSequenceNumber)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        // lo is now the index of the first entry with SequenceNumber > afterSequenceNumber
        var result = _entries.GetRange(lo, _entries.Count - lo);
        return Task.FromResult<IReadOnlyList<(TKey Key, TValue Value, long SequenceNumber)>>(result);
    }

    /// <inheritdoc />
    public long GetLatestSequenceNumber()
    {
        return _entries.Count == 0 ? 0 : _entries[^1].SequenceNumber;
    }
}
