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
namespace Boutquin.Storage.Infrastructure.LsmTree;

// A single memtable entry captured in a projected-range snapshot, carrying the winning slot's
// sequence number so the consumer can apply tombstones with newest-wins tombstone semantics.
internal readonly record struct MemtableSnapshotEntry<TKey, TValue>(
    TKey Key,
    TValue Value,
    ulong Seqno);

// Single-shot snapshot consumed by LsmTimeSeriesStorageEngine's projected k-way-merge path.
// The tuple of
// (memtable entries, tombstones, segment readers) is captured atomically under the LSM
// engine's lock so no concurrent flush can straddle the merge — a flush would otherwise
// duplicate rows between the memtable snapshot and the new segment.
//
// Tombstones is keyed by the maximum tombstone seqno per key so the consumer suppresses a key
// only when the delete is newer than a live re-insert (newest-wins tombstone semantics — a flat key
// set drops a re-insert whose seqno is newer than an older slot/segment tombstone, DDIA 2e p. 121).
//
// SegmentMaxSeqnos[i] is the maximum sequence number written into Segments[i]. Columnar segments
// carry no per-row seqno, so the consumer uses the segment's max seqno as the effective seqno of
// any value it sourced from that segment — sound because a segment is one ordered memtable flush
// (a key cannot have both data and a strictly-later same-segment tombstone). This lets the
// projected read suppress a tombstone only when it is newer than the winning data even when the
// winner came from a flushed (columnar) segment rather than the memtable (source-priority deduplication rule).
//
// Segments are pinned in the engine's snapshot registry from acquisition until DisposeAsync, so a
// compaction or ClearAsync that retires one of them parks its reader instead of deleting the file
// while the consumer is still streaming it. The consumer must dispose the snapshot once it has
// finished reading Segments; until then the retired files stay on disk. ReleasePinAsync is
// idempotent, so a second dispose is a no-op.
//
// Internal by design: the only consumer is the Infrastructure-assembly-local decorator.
// The public ILsmStorageEngine surface stays unchanged.
internal sealed record ProjectedRangeSnapshot<TKey, TValue>(
    IReadOnlyList<MemtableSnapshotEntry<TKey, TValue>> MemtableEntries,
    IReadOnlyDictionary<TKey, ulong> Tombstones,
    IReadOnlyList<Boutquin.Storage.Domain.Interfaces.ISegmentReader<TKey, TValue>> Segments,
    IReadOnlyList<ulong> SegmentMaxSeqnos,
    Func<ValueTask> ReleasePinAsync) : IAsyncDisposable
    where TKey : Boutquin.Storage.Domain.Interfaces.IStorageKey<TKey>, new()
    where TValue : Boutquin.Storage.Domain.Interfaces.ISerializable<TValue>, new()
{
    public ValueTask DisposeAsync() => ReleasePinAsync();
}
