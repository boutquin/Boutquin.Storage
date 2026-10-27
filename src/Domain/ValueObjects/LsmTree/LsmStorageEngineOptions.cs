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
using Boutquin.Storage.Domain.ValueObjects.ValueLog;

namespace Boutquin.Storage.Domain.ValueObjects.LsmTree;

/// <summary>
/// Composite configuration passed to an <c>LsmStorageEngine&lt;TKey, TValue&gt;</c> constructor.
/// Threads the engine's orthogonal configuration axes — MemTable capacity, segment folder +
/// prefix + format, compaction strategy, WAL, and WiscKey value-log options — through one
/// named record.
/// </summary>
/// <typeparam name="TKey">Key type. Same constraints the engine enforces on <typeparamref name="TKey"/>.</typeparam>
/// <typeparam name="TValue">Value type. Same constraints the engine enforces on <typeparamref name="TValue"/>.</typeparam>
/// <param name="MemTableCapacity">Maximum MemTable entries before an auto-flush triggers.</param>
/// <param name="SegmentFolder">Directory holding on-disk segment files plus the <c>valuelog/</c>
/// subdirectory owned by the WiscKey subsystem.</param>
/// <param name="SegmentPrefix">Prefix for segment filenames (e.g. <c>segment</c>, yielding
/// <c>segment_000000.sst</c>).</param>
/// <param name="SegmentFormat">Strategy materialising every flush + compaction into an
/// immutable on-disk segment. Also drives recovery's file-discovery extension filter.</param>
/// <param name="WriteAheadLog">Optional WAL for durability. When <see langword="null"/>, writes
/// are not logged and crash recovery rebuilds only from on-disk segments.</param>
/// <param name="CompactionStrategy">Strategy that drives auto-compaction decisions. When
/// <see langword="null"/>, the engine defaults to <c>SizeTieredCompactionStrategy&lt;TKey&gt;()</c>
/// (Cassandra-style; DDIA Ch. 4 p. 124 — size-tiered compaction). Pluggable for consumers
/// that need a different policy (e.g., a future leveled strategy).</param>
/// <param name="ValueLog">Value-log dispatch knobs (threshold, rotation size, GC trigger,
/// handle-cache size, payload compression, MaxEntryBytes). Defaults to
/// <see cref="ValueLogOptions.Default"/>. Applies only over a segment format whose
/// <see cref="ISegmentFormat{TKey, TValue}.SupportsValueLogPointers"/> is <see langword="true"/>.
/// Over any other format (the columnar format, or a decorator that does not forward that member)
/// the engine keeps value-log dispatch off whatever this says, and refuses a value too large to
/// keep inline before acknowledging the write.</param>
/// <param name="BlockCache">Optional shared per-block cache (DDIA Ch. 4 p. 79). Reads go through
/// the cache the segment format was constructed with (<c>SstSegmentFormat(blockCache: …)</c>), so
/// point lookups avoid re-reading and re-decompressing the same block; the engine cannot hand a
/// cache to a format that already exists. When non-null, this must be that same cache instance —
/// the engine refuses any other value with <see cref="ArgumentException"/> at construction, rather
/// than report caching no read gets — and the engine surfaces it and clears it on
/// <c>ClearAsync</c>. Default <see langword="null"/> leaves caching entirely to the segment
/// format.</param>
/// <param name="MaxImmutableMemtables">Bound on the queue of sealed-but-not-yet-flushed
/// memtables (DDIA Ch. 4 p. 117–119; p. 122–124). When the active memtable fills, it is
/// sealed and appended to this queue while a fresh active accepts new writes; a background
/// flusher drains the head into an SSTable. Ingest blocks only when the queue saturates —
/// this is the engine's overload back-pressure signal. Default 2 mirrors RocksDB's
/// <c>max_write_buffer_number</c>; minimum 1 (a value of 1 forces seal-then-wait, matching
/// the historical inline-flush behaviour). Higher values mask flush slowness for longer at
/// the cost of more buffered memory and more expensive snapshot capture
/// (snapshot acquisition merges every memtable in the queue). Validation: the constructor
/// rejects values &lt; 1 with <see cref="ArgumentOutOfRangeException"/>.</param>
/// <remarks>
/// <para><b>Why a record instead of positional parameters.</b> The engine has multiple orthogonal
/// axes (memtable capacity, segment folder, segment prefix, segment format, optional WAL,
/// optional compaction strategy, optional value-log knobs, optional block cache, immutable
/// memtable queue depth); threading every knob through a named record keeps call sites
/// readable and lets future knobs land additively.</para>
/// <para><b>Per-segment bloom filtering.</b> The engine no longer accepts a bloom-filter
/// factory. Per-segment bloom is the segment format's responsibility — the SST footer carries
/// its own bloom (DDIA Ch. 4 p. 79–82) and the SST reader consults it on every point lookup.
/// Columnar segments do not have a bloom (the per-dataset zone-map filter substitutes).</para>
/// </remarks>
public sealed record LsmStorageEngineOptions<TKey, TValue>(
    int MemTableCapacity,
    string SegmentFolder,
    string SegmentPrefix,
    ISegmentFormat<TKey, TValue> SegmentFormat,
    IWriteAheadLog<TKey, TValue>? WriteAheadLog = null,
    ICompactionStrategy<TKey>? CompactionStrategy = null,
    ValueLogOptions? ValueLog = null,
    IBlockCache? BlockCache = null,
    int MaxImmutableMemtables = 2)
    where TKey : IStorageKey<TKey>, new()
    where TValue : ISerializable<TValue>, new()
{
    /// <summary>
    /// Resolved value-log knobs. Returns <see cref="ValueLogOptions.Default"/> when the
    /// caller left <c>ValueLog</c> unset.
    /// </summary>
    public ValueLogOptions ResolvedValueLog => ValueLog ?? ValueLogOptions.Default;

    /// <summary>
    /// How recovery treats a manifest-listed segment that fails its integrity check at open.
    /// Default <see cref="CorruptSegmentRecoveryDisposition.FailClosed"/> — see the enum's
    /// remarks for when the cache-tier <see cref="CorruptSegmentRecoveryDisposition.ExcludeAndSurface"/>
    /// opt-in is sound.
    /// </summary>
    public CorruptSegmentRecoveryDisposition CorruptSegmentRecovery { get; init; }
        = CorruptSegmentRecoveryDisposition.FailClosed;

    /// <summary>
    /// Invoked once per segment excluded at recovery under
    /// <see cref="CorruptSegmentRecoveryDisposition.ExcludeAndSurface"/> — the consumer's hook
    /// for shrinking coverage accounting and scheduling a re-ingest. Ignored under
    /// <see cref="CorruptSegmentRecoveryDisposition.FailClosed"/>. Exclusions are additionally
    /// trace-logged whether or not a callback is registered.
    /// </summary>
    public Action<CorruptSegmentExclusion>? OnCorruptSegmentExcluded { get; init; }

    /// <summary>Overrides the auto-generated property to enforce the lower-bound invariant.
    /// A value below 1 would leave the engine with no buffer between seal and flush,
    /// making every write that causes a seal block indefinitely until flush completes.
    /// The minimum of 1 provides at least one in-flight sealed slot.</summary>
    public int MaxImmutableMemtables { get; init; } =
        MaxImmutableMemtables >= 1
            ? MaxImmutableMemtables
            : throw new ArgumentOutOfRangeException(
                nameof(MaxImmutableMemtables),
                MaxImmutableMemtables,
                "MaxImmutableMemtables must be at least 1 — zero or negative leaves no sealed-memtable buffer between ingest and flush.");
}
