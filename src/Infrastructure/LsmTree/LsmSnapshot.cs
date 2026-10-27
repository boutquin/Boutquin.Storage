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
using Boutquin.Storage.Domain.ValueObjects.SortedStringTable;

namespace Boutquin.Storage.Infrastructure.LsmTree;

/// <summary>
/// <see cref="ISnapshot{TKey,TValue}"/> implementation backed by a captured segment set
/// and MemTable state from an <see cref="LsmStorageEngine{TKey,TValue}"/>.
/// </summary>
/// <remarks>
/// <para><b>Snapshot contents.</b> At acquisition time the engine captures, under its
/// write lock: (a) the engine seqno watermark, (b) the ordered list of
/// <see cref="ISegmentReader{TKey,TValue}"/> instances, (c) each segment's filename +
/// per-segment tombstone map, (d) a copy of the MemTable data + MemTable tombstones,
/// each annotated with its per-entry seqno. The snapshot owns this immutable copy for
/// its lifetime — post-acquisition mutations on the engine never leak in.</para>
///
/// <para><b>Visibility algorithm.</b> For <see cref="TryGetValueAsync"/>: walk
/// (MemTable → segments newest-to-oldest); at each source, check whether the data
/// entry and/or the tombstone for the key are visible under the seqno filter, keep
/// the higher-seqno winner, and if any source yields a visible winner, stop — tombstone
/// ⇒ absent, write ⇒ return value. For <see cref="GetRangeAsync"/>: stream every source
/// filtered by seqno, group by key, resolve each key's visible winner, project the
/// survivors in sorted order.</para>
///
/// <para><b>Disposal.</b> Removes the snapshot from the engine's
/// <see cref="SnapshotRegistry"/> under the engine's lock. The engine's next compaction
/// cycle sweeps any retired-but-previously-pinned segment files that are no longer
/// pinned by any live snapshot.</para>
/// </remarks>
/// <typeparam name="TKey">The key type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
internal sealed class LsmSnapshot<TKey, TValue> : ISnapshot<TKey, TValue>
    where TKey : IStorageKey<TKey>, new()
    where TValue : ISerializable<TValue>, new()
{
    private readonly LsmStorageEngine<TKey, TValue> _engine;
    private readonly Guid _id;
    private readonly ulong _sequenceNumber;
    private readonly IReadOnlyList<ISegmentReader<TKey, TValue>> _segments;
    private readonly IReadOnlyList<string> _segmentFileNames;
    private readonly IReadOnlyList<IReadOnlyDictionary<TKey, ulong>> _segmentTombstoneSeqnos;
    private readonly IReadOnlyDictionary<TKey, (TValue Value, ulong Seqno)> _memTableSnapshot;
    private readonly IReadOnlyDictionary<TKey, ulong> _memTableTombstoneSeqnos;
    private int _disposed;

    internal LsmSnapshot(
        LsmStorageEngine<TKey, TValue> engine,
        Guid id,
        ulong sequenceNumber,
        IReadOnlyList<ISegmentReader<TKey, TValue>> segments,
        IReadOnlyList<string> segmentFileNames,
        IReadOnlyList<IReadOnlyDictionary<TKey, ulong>> segmentTombstoneSeqnos,
        IReadOnlyDictionary<TKey, (TValue Value, ulong Seqno)> memTableSnapshot,
        IReadOnlyDictionary<TKey, ulong> memTableTombstoneSeqnos)
    {
        _engine = engine;
        _id = id;
        _sequenceNumber = sequenceNumber;
        _segments = segments;
        _segmentFileNames = segmentFileNames;
        _segmentTombstoneSeqnos = segmentTombstoneSeqnos;
        _memTableSnapshot = memTableSnapshot;
        _memTableTombstoneSeqnos = memTableTombstoneSeqnos;
    }

    public ulong SequenceNumber => _sequenceNumber;

    internal Guid Id => _id;

    internal IReadOnlyList<string> PinnedSegmentFileNames => _segmentFileNames;

    public async Task<(TValue Value, bool Found)> TryGetValueAsync(
        TKey key, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        Guard.AgainstNullOrDefault(() => key);

        // Walk MemTable → segments newest-to-oldest. At each source record the highest
        // visible seqno seen for this key and whether it is a tombstone. Return on the
        // first source that surfaces any visible entry — newer sources mask older ones.

        if (TryResolveMemTable(key, out var memWinnerSeqno, out var memIsTombstone, out var memValue))
        {
            return memIsTombstone ? (default!, false) : (memValue, true);
        }

        for (var i = _segments.Count - 1; i >= 0; i--)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var hasTombstone = _segmentTombstoneSeqnos[i].TryGetValue(key, out var tombstoneSeqno)
                && tombstoneSeqno <= _sequenceNumber;

            // Snapshot reads consult the pinned segment list via TryGetBlockEntryAsync and
            // dereference pointers through the engine's ValueLogReader (never the segment
            // reader's own machinery, keeping the dereference step single-sourced). Inline
            // entries deserialise here; pointer entries flow through ResolveBlockEntryValue.
            var (entry, dataFound) = await _segments[i]
                .TryGetBlockEntryAsync(key, _sequenceNumber, cancellationToken)
                .ConfigureAwait(false);
            var dataSeqno = dataFound ? entry.SequenceNumber : 0UL;

            if (!hasTombstone && !dataFound)
            {
                continue;
            }

            // Both present → higher seqno wins. Only tombstone → absent. Only data → value.
            if (hasTombstone && dataFound)
            {
                if (tombstoneSeqno >= dataSeqno)
                {
                    return (default!, false);
                }
                return (_engine.ResolveBlockEntryValue(entry), true);
            }

            if (hasTombstone)
            {
                return (default!, false);
            }

            return (_engine.ResolveBlockEntryValue(entry), true);
        }

        return (default!, false);
    }

    public async IAsyncEnumerable<KeyValuePair<TKey, TValue>> GetRangeAsync(
        TKey startKey, TKey endKey,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        Guard.AgainstNullOrDefault(() => startKey);
        Guard.AgainstNullOrDefault(() => endKey);

        // Reject inverted ranges — an empty result for startKey > endKey would mask a caller
        // error, mirroring the same guard in LsmStorageEngine.GetRangeAsync.
        if (startKey.CompareTo(endKey) > 0)
        {
            throw new ArgumentException(
                "startKey must be less than or equal to endKey (got startKey > endKey).", nameof(startKey));
        }

        // P4.1 — streaming k-way merge. Sources are ordered oldest-to-newest:
        // [seg_0, seg_1, …, seg_N-1, memtable]. KWayMerge's "highest source index wins"
        // maps to "newest source wins" — the correct LSM deduplication rule. Each source
        // carries SnapshotEntry (seqno + tombstone flag + value) so the post-merge pass
        // can skip deleted keys without a second dictionary lookup.
        var sources = new List<IAsyncEnumerable<KeyValuePair<TKey, SnapshotEntry>>>(
            _segments.Count + 1);

        for (var i = 0; i < _segments.Count; i++)
        {
            sources.Add(BuildSegmentSourceAsync(i, startKey, endKey, cancellationToken));
        }
        sources.Add(BuildMemTableSourceAsync(startKey, endKey, cancellationToken));

        await foreach (var kv in KWayMerge.MergeAsync(
            sources, Comparer<TKey>.Default, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Sources are sorted ascending; once we pass endKey no further entry can be in range.
            if (kv.Key.CompareTo(endKey) > 0)
            {
                break;
            }

            if (!kv.Value.IsTombstone)
            {
                yield return new KeyValuePair<TKey, TValue>(kv.Key, kv.Value.Value);
            }
        }
    }

    // Per-key visibility carrier for the k-way merge.
    private readonly record struct SnapshotEntry(ulong Seqno, bool IsTombstone, TValue Value);

    // Builds the sorted source for one on-disk segment. Data entries come from the
    // segment's StreamBlockEntriesAsync (already sorted ascending) and tombstones come
    // from the per-segment seqno map (sorted here by key). Within a single segment a key
    // appears at most once across both sources (LSM flush invariant).
    private IAsyncEnumerable<KeyValuePair<TKey, SnapshotEntry>> BuildSegmentSourceAsync(
        int segmentIndex, TKey startKey, TKey endKey, CancellationToken ct)
    {
        // Sort tombstones once. Per-segment dicts are small (one entry per deleted key in
        // the corresponding flush batch), so this sort is negligible vs segment I/O.
        var tombstones = _segmentTombstoneSeqnos[segmentIndex]
            .Where(kv => kv.Value <= _sequenceNumber
                         && kv.Key.CompareTo(startKey) >= 0
                         && kv.Key.CompareTo(endKey) <= 0)
            .OrderBy(kv => kv.Key)
            .Select(kv => new KeyValuePair<TKey, SnapshotEntry>(
                kv.Key, new SnapshotEntry(kv.Value, true, default!)))
            .ToList();

        return MergeTwoSortedAsync(
            _segments[segmentIndex].StreamBlockEntriesAsync(_sequenceNumber, cancellationToken: ct),
            tombstones, startKey, endKey, ct);
    }

    // Builds the sorted source for the MemTable snapshot. Both the data dict and
    // tombstone dict are unsorted — they are sorted here by key and 2-way merged.
    private async IAsyncEnumerable<KeyValuePair<TKey, SnapshotEntry>> BuildMemTableSourceAsync(
        TKey startKey, TKey endKey,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var data = _memTableSnapshot
            .Where(kv => kv.Value.Seqno <= _sequenceNumber
                         && kv.Key.CompareTo(startKey) >= 0
                         && kv.Key.CompareTo(endKey) <= 0)
            .OrderBy(kv => kv.Key)
            .Select(kv => new KeyValuePair<TKey, SnapshotEntry>(
                kv.Key, new SnapshotEntry(kv.Value.Seqno, false, kv.Value.Value)))
            .ToList();

        var tombstones = _memTableTombstoneSeqnos
            .Where(kv => kv.Value <= _sequenceNumber
                         && kv.Key.CompareTo(startKey) >= 0
                         && kv.Key.CompareTo(endKey) <= 0)
            .OrderBy(kv => kv.Key)
            .Select(kv => new KeyValuePair<TKey, SnapshotEntry>(
                kv.Key, new SnapshotEntry(kv.Value, true, default!)))
            .ToList();

        var di = 0;
        var ti = 0;
        while (di < data.Count || ti < tombstones.Count)
        {
            ct.ThrowIfCancellationRequested();
            var hasD = di < data.Count;
            var hasT = ti < tombstones.Count;
            if (hasD && hasT)
            {
                var cmp = data[di].Key.CompareTo(tombstones[ti].Key);
                if (cmp < 0) { yield return data[di++]; }
                else if (cmp > 0) { yield return tombstones[ti++]; }
                else
                {
                    // Same key in both — pick higher seqno. Should not occur because
                    // SetAsync removes the tombstone before recording the write; but if
                    // a snapshot copy captured a race window, the seqno comparison resolves it.
                    yield return data[di].Value.Seqno >= tombstones[ti].Value.Seqno
                        ? data[di] : tombstones[ti];
                    di++; ti++;
                }
            }
            else if (hasD) { yield return data[di++]; }
            else { yield return tombstones[ti++]; }
        }
    }

    // Performs a 2-way sorted merge of a streaming segment data source with a pre-sorted
    // tombstone list. The data stream is already sorted ascending from the segment file;
    // the tombstone list was sorted by BuildSegmentSourceAsync. A key can appear in at
    // most one of the two inputs within the same segment (LSM flush invariant); the equal-
    // key branch is a safety fallback that picks the higher-seqno entry.
    private async IAsyncEnumerable<KeyValuePair<TKey, SnapshotEntry>> MergeTwoSortedAsync(
        IAsyncEnumerable<SegmentWriteEntry<TKey, TValue>> dataStream,
        IReadOnlyList<KeyValuePair<TKey, SnapshotEntry>> sortedTombstones,
        TKey startKey, TKey endKey,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var ti = 0;
        await foreach (var entry in dataStream.WithCancellation(ct).ConfigureAwait(false))
        {
            var key = entry.Key;
            if (key.CompareTo(startKey) < 0 || key.CompareTo(endKey) > 0)
            {
                continue;
            }

            // Yield tombstones that sort before this data entry.
            while (ti < sortedTombstones.Count && sortedTombstones[ti].Key.CompareTo(key) < 0)
            {
                yield return sortedTombstones[ti++];
            }

            // Pointer entries are resolved at the engine layer — the snapshot view must
            // carry TValue, not raw SegmentWriteEntry, for the caller.
            var value = entry.Kind == ValueKind.Inline
                ? entry.InlineValue!
                : _engine.ResolveBlockEntryValue(
                    new BlockEntry(default, entry.SequenceNumber, ValueKind.ValueLogPointer, entry.PointerBytes));

            if (ti < sortedTombstones.Count && sortedTombstones[ti].Key.CompareTo(key) == 0)
            {
                // Same key in both — pick higher seqno (safety fallback; normally can't happen).
                yield return sortedTombstones[ti].Value.Seqno >= entry.SequenceNumber
                    ? sortedTombstones[ti]
                    : new KeyValuePair<TKey, SnapshotEntry>(
                        key, new SnapshotEntry(entry.SequenceNumber, false, value));
                ti++;
                continue;
            }

            yield return new KeyValuePair<TKey, SnapshotEntry>(
                key, new SnapshotEntry(entry.SequenceNumber, false, value));
        }

        // Yield any tombstones that sort after the last data entry.
        while (ti < sortedTombstones.Count)
        {
            ct.ThrowIfCancellationRequested();
            yield return sortedTombstones[ti++];
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _engine.OnSnapshotDisposedAsync(_id).ConfigureAwait(false);
    }

    private bool TryResolveMemTable(
        TKey key,
        out ulong winnerSeqno,
        out bool winnerIsTombstone,
        out TValue winnerValue)
    {
        winnerSeqno = 0UL;
        winnerIsTombstone = false;
        winnerValue = default!;

        var hasData = _memTableSnapshot.TryGetValue(key, out var dataEntry);
        var hasTombstone = _memTableTombstoneSeqnos.TryGetValue(key, out var tombstoneSeqno);

        // Apply seqno filter. MemTable entries are captured at acquisition under the engine's
        // lock so their seqnos are authoritative; but a seqno > snapshot can still sneak in if
        // the MemTable snapshot-copy step ran right after a write that raced with the lock
        // acquisition. Defensive filter here is cheap.
        var dataVisible = hasData && dataEntry.Seqno <= _sequenceNumber;
        var tombstoneVisible = hasTombstone && tombstoneSeqno <= _sequenceNumber;

        if (!dataVisible && !tombstoneVisible)
        {
            return false;
        }

        if (dataVisible && tombstoneVisible)
        {
            if (tombstoneSeqno >= dataEntry.Seqno)
            {
                winnerSeqno = tombstoneSeqno;
                winnerIsTombstone = true;
            }
            else
            {
                winnerSeqno = dataEntry.Seqno;
                winnerValue = dataEntry.Value;
            }
            return true;
        }

        if (tombstoneVisible)
        {
            winnerSeqno = tombstoneSeqno;
            winnerIsTombstone = true;
            return true;
        }

        winnerSeqno = dataEntry.Seqno;
        winnerValue = dataEntry.Value;
        return true;
    }
}
