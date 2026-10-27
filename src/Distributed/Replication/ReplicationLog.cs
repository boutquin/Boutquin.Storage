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
namespace Boutquin.Storage.Distributed.Replication;

/// <summary>
/// An in-memory replication log that stores key-value mutations with monotonically increasing sequence numbers.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope:</b> In-process reference implementation in <c>Boutquin.Storage.Distributed</c>,
/// which is not published to NuGet (see <c>src/Distributed/README.md</c>). Not durable
/// across process restart, not intended for production deployment. Leader/follower and quorum replication
/// is implemented for educational fidelity to DDIA 2e Ch. 6, but production-grade extensions
/// — sync-vs-async replication modes, sloppy-quorum with hinted handoff, snapshot-stream catchup APIs, and version-vector / Lamport conflict resolution — are not included. For production use, prefer <a href="https://getakka.net">Akka.NET cluster</a>, <a href="https://orleans.dev">Orleans</a> silos, or a dedicated replicated-log service.
/// </para>
///
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

    // Highest watermark ever passed to TruncateBelowAsync. Readers whose requested
    // afterSequenceNumber + 1 is strictly below this value have a gap they cannot fill
    // from this log — the follower must rebootstrap from an out-of-band snapshot.
    private long _truncationWatermark;

    // Highest sequence number actually REMOVED by truncation. Gap detection compares against
    // this, not the watermark: the watermark is a caller-chosen threshold that may fall in a
    // sparse-sequence gap, and refusing a follower that lost nothing (its next needed entry
    // survived) forces a false-positive full-state rebootstrap.
    private long _maxRemovedSequence;

    // Highest sequence number ever appended, regardless of subsequent truncation. Preserved
    // across truncations so GetLatestSequenceNumber continues to report the leader's tip after
    // the log has been reclaimed (lag computations in SingleLeaderReplication depend on this).
    private long _latestSequenceNumber;

    /// <summary>
    /// The highest watermark ever passed to <see cref="TruncateBelowAsync"/>. Entries with
    /// <c>SequenceNumber &lt; TruncationWatermark</c> have been reclaimed and are no longer
    /// available for catch-up.
    /// </summary>
    public long TruncationWatermark => _truncationWatermark;

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
        _latestSequenceNumber = sequenceNumber;
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
        // A follower at or beyond the highest sequence number ever appended has missed
        // nothing — no entry beyond its position ever existed, so there is no gap regardless
        // of how far the truncation watermark was advanced. Checked before the watermark
        // guard so a caught-up follower is never spuriously told to rebootstrap.
        if (afterSequenceNumber >= _latestSequenceNumber)
        {
            return Task.FromResult<IReadOnlyList<(TKey Key, TValue Value, long SequenceNumber)>>([]);
        }

        // Rebootstrap is required only when an entry the follower still NEEDS was actually
        // removed — i.e. some removed sequence number is strictly greater than the follower's
        // position. Comparing against the watermark instead is pessimistic for sparse
        // sequences: with entries {10, 20} truncated below 15, only 10 was removed, and a
        // follower at 10 can catch up from the surviving 20.
        if (afterSequenceNumber < _maxRemovedSequence)
        {
            throw new FollowerTooFarBehindException(
                $"Follower asked for entries after sequence {afterSequenceNumber}, but the log " +
                $"has truncated below sequence {_truncationWatermark}. The gap cannot be " +
                "reconstructed from this log; rebootstrap from a full-state snapshot.",
                afterSequenceNumber,
                _truncationWatermark);
        }

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
        return _latestSequenceNumber;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Watermark is monotonic: a value at or below the current watermark is a no-op. Entries
    /// are removed in-place from the front of the list; <see cref="List{T}.RemoveRange"/>
    /// shifts the surviving tail down, so the cost is O(remaining), not O(removed).
    /// </remarks>
    public Task<int> TruncateBelowAsync(long watermark, CancellationToken ct = default)
    {
        if (watermark <= _truncationWatermark)
        {
            return Task.FromResult(0);
        }

        // Binary search for the first entry with SequenceNumber >= watermark. Everything
        // before that index is below the watermark and should be removed.
        var lo = 0;
        var hi = _entries.Count;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (_entries[mid].SequenceNumber < watermark)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        var removed = lo;
        if (removed > 0)
        {
            // Record the highest sequence actually removed — the exact gap-detection floor.
            _maxRemovedSequence = Math.Max(_maxRemovedSequence, _entries[removed - 1].SequenceNumber);
            _entries.RemoveRange(0, removed);
        }

        _truncationWatermark = watermark;
        return Task.FromResult(removed);
    }
}
