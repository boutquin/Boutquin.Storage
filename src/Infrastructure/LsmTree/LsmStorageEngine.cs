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
using System.Runtime.ExceptionServices;
using Boutquin.Storage.Domain.ValueObjects.LsmTree;
using Boutquin.Storage.Domain.ValueObjects.SortedStringTable;
using Boutquin.Storage.Domain.ValueObjects.ValueLog;
using Boutquin.Storage.Infrastructure.DataStructures;
using Boutquin.Storage.Infrastructure.KeyValueStore;
using Boutquin.Storage.Infrastructure.ValueLog;

namespace Boutquin.Storage.Infrastructure.LsmTree;

/// <summary>
/// Log-Structured Merge-tree (LSM-tree) storage engine that orchestrates an in-memory MemTable
/// with on-disk segments for high write throughput.
/// </summary>
/// <typeparam name="TKey">The type of the keys in the store.</typeparam>
/// <typeparam name="TValue">The type of the values in the store.</typeparam>
/// <remarks>
/// <para>
/// <b>How it works (DDIA Ch. 4 p. 117–119).</b> Writes go to the in-memory active MemTable
/// (a RedBlackTree with a fixed capacity). When the active MemTable fills, it is sealed and
/// appended to a bounded queue of immutable MemTables; a fresh active is allocated and accepts
/// new writes immediately. A background flusher Task drains the head of the queue into a new
/// on-disk segment file (an SSTable). Reads walk the active MemTable, then the immutables
/// newest-first, then the on-disk segments newest-first (most recent data wins). This design
/// produces the canonical LSM write-throughput shape — DDIA's textbook "active memtable + queue
/// of immutable memtables + background flusher" pattern — and makes the engine's only
/// ingest-blocking signal "the immutable queue is full" (DDIA p. 122–124), not "a flush is in
/// progress".
/// </para>
/// <para>
/// <b>WriteAheadLog:</b> When a WriteAheadLog is provided, every write is persisted to the WriteAheadLog
/// before being applied to the MemTable. This ensures durability — if the process crashes before
/// a flush, the MemTable can be reconstructed by replaying the WriteAheadLog on startup. After a successful
/// flush, the WriteAheadLog is truncated.
/// </para>
/// <para>
/// <b>Startup recovery:</b> On construction, the engine scans the segment folder for existing
/// segment files (matching the prefix pattern) and loads them in sorted order. If a WriteAheadLog is
/// provided, its entries are replayed into the MemTable before any operation runs: eagerly by
/// <see cref="OpenAsync"/>, or by the first operation on an engine built with the constructor.
/// Both entry points therefore recover the same state after a restart.
/// </para>
/// <para>
/// <b>Tombstones:</b> <see cref="RemoveAsync"/> marks keys as deleted by recording them
/// in an in-memory tombstone set. Tombstoned keys are excluded from reads and
/// <see cref="GetAllItemsAsync"/>. Tombstones are flushed alongside segment data and
/// stripped during compaction, reclaiming space. Tombstones are always on — DDIA p. 121
/// prescribes them as the correctness mechanism for deletes on an append-only LSM.
/// </para>
/// <para>
/// <b>Range queries:</b> <see cref="GetRangeAsync"/> returns all key-value pairs within an
/// inclusive key range [startKey, endKey], merging results from the MemTable and all segments.
/// </para>
/// <para>
/// <b>Thread safety:</b> All public operations are serialized via a <see cref="SemaphoreSlim"/>
/// to ensure thread safety. This is a simple approach suitable for single-node deployments.
/// Two long-running operations deliberately run OUTSIDE that lock so ingest and reads are
/// never blocked behind O(dataset) I/O: the flusher's SST write and compaction's merge +
/// segment write. Both follow the same protocol — pick under the lock, work without it,
/// revalidate and commit under it again (DDIA 2e p. 121 — reads are served from the input
/// segments while a merge runs).
/// </para>
/// <para>
/// <b>Segment naming:</b> Each segment is named with a monotonically increasing counter
/// (e.g., prefix_000000.sst, prefix_000001.sst with the default format) to preserve creation order and enable
/// newest-first reads without relying on filesystem timestamps.
/// </para>
/// <para>
/// <b>Compaction:</b> Auto-compaction is driven entirely by an <see cref="ICompactionStrategy{TKey}"/>
/// (default: <see cref="SizeTieredCompactionStrategy{TKey}"/> — Cassandra-style; DDIA Ch. 4
/// p. 124). When <see cref="ICompactionStrategy{TKey}.ShouldCompact"/> returns
/// <see langword="true"/>, the strategy's <see cref="ICompactionStrategy{TKey}.SelectSegments"/>
/// picks the segments to merge. The selected streams are merged with newest-value precedence;
/// tombstones are retained unless coverage and snapshot rules permit removal. The replacement
/// segment is written and committed in the manifest before old files are retired; snapshot pins
/// delay physical deletion. Partial compaction need not reduce the total segment count to one.
/// </para>
/// <para>
/// <b>Cost:</b> Memtable operations, segment lookup costs, and OS flush latency contribute to
/// writes and reads. Compaction reads the selected inputs and accumulates surviving entries in
/// memory before writing, so its peak memory grows with the compacted output. See
/// <c>docs/TRADEOFFS.md</c> for durability and resource boundaries.
/// </para>
/// <para>
/// <b>Inline-only segment formats:</b> the engine routes values to the value log only over a
/// segment format whose <see cref="ISegmentFormat{TKey, TValue}.SupportsValueLogPointers"/> is
/// <see langword="true"/> (<c>SstSegmentFormat</c>). Over any other format — the
/// <c>ColumnarSegmentFormat</c>, or a decorator that does not forward the capability — the
/// engine never routes a value to the value log, whatever
/// <see cref="LsmStorageEngineOptions{TKey, TValue}.ValueLog"/> says — a null <c>ValueLog</c>, which
/// otherwise resolves to the 1 KiB default threshold, included. Every value stays inline up to
/// 128 MiB; a larger value is refused with
/// <see cref="ValueTooLargeException"/> before the write is acknowledged. Routing it to the
/// value log instead would hand the flush a pointer the format refuses, faulting that flush and
/// losing every write it carried.
/// </para>
/// </remarks>
public sealed class LsmStorageEngine<TKey, TValue> : ILsmStorageEngine<TKey, TValue>, IValueLogGcHost, ILsmEntryAdmissionCheck<TKey, TValue>, IDisposable, IAsyncDisposable
    where TKey : IStorageKey<TKey>, new()
    where TValue : ISerializable<TValue>, new()
{
    ValueLogManifest? IValueLogGcHost.ValueLogManifest => _valueLogManifest;
    ValueLogWriter? IValueLogGcHost.ValueLogWriter => _valueLogWriter;
    string IValueLogGcHost.ValueLogDirectory => _valueLogDirectory;
    Func<HashSet<(uint FileIndex, ulong Offset)>, bool> IValueLogGcHost.SnapshotSegmentsUnderLock()
        => SnapshotSegmentsForGc();
    async ValueTask IValueLogGcHost.EnterLockForGcAsync(CancellationToken ct) =>
        await _lock.WaitAsync(ct).ConfigureAwait(false);
    void IValueLogGcHost.ExitLockForGc() => _lock.Release();

    private readonly int _memTableCapacity;
    private readonly string _segmentFolder;
    private readonly string _segmentPrefix;
    private readonly ISegmentFormat<TKey, TValue> _segmentFormat;
    private readonly IWriteAheadLog<TKey, TValue>? _wal;
    private readonly ICompactionStrategy<TKey> _compactionStrategy;
    private readonly IBlockCache? _blockCache;

    // Recovery disposition for manifest-listed segments that fail their integrity check.
    // FailClosed (default) throws at open; ExcludeAndSurface (cache-tier opt-in) skips the
    // segment and reports it through the callback. See CorruptSegmentRecoveryDisposition.
    private readonly CorruptSegmentRecoveryDisposition _corruptSegmentRecovery;
    private readonly Action<CorruptSegmentExclusion>? _onCorruptSegmentExcluded;
    private readonly SemaphoreSlim _lock = new(1, 1);

    // Single-compactor gate: at most one compaction (explicit CompactAsync or the flusher's
    // auto-compact tick) is in flight at a time. The engine lock cannot serve this role any
    // more — the O(dataset) merge step deliberately runs WITHOUT _lock (DDIA 2e p. 121:
    // reads are served from the input segments while the merge runs) — and two concurrent
    // merges over overlapping picked sets would double-retire segments at commit. Explicit
    // CompactAsync awaits the gate; the flusher's advisory tick try-acquires and skips when
    // a compaction is already running (the strategy re-fires on the next flush).
    private readonly SemaphoreSlim _compactionGate = new(1, 1);

    // Per-engine value-log GC gate. CompactValueLogAsync builds a fresh ValueLogGarbageCollector
    // per call, so a gate living on the GC instance cannot serialise two concurrent calls — each
    // would acquire its own. The gate must live on the engine to prevent two GC rounds racing the
    // shared value-log manifest (a double RecordDeleteAsync on the same closed file throws, and
    // unserialised manifest-stream appends can corrupt the live set). It is per-engine (not
    // static) so a GC round on engine A never blocks a GC round on engine B — they share no state.
    private readonly SemaphoreSlim _gcGate = new(1, 1);

    /// <summary>
    /// Engine-level reference to the per-block cache supplied at construction
    /// (<see cref="LsmStorageEngineOptions{TKey, TValue}.BlockCache"/>). The cache itself is wired
    /// into the SST read path through the segment format's ctor; the constructor refuses an options
    /// cache that is not the one the segment format reads through, so a non-null value here is the
    /// cache point lookups use.
    /// </summary>
    public IBlockCache? BlockCache => _blockCache;

    // Per-engine WiscKey value-log subsystem. _valueLogManifest / _valueLogWriter /
    // _valueLogReader are lazily materialised on the first large write; if the engine's
    // lifecycle never crosses the threshold the value-log directory stays empty. Reader
    // outlives the writer — snapshot reads may dereference pointers after the engine has
    // rotated the writer to a new segment.
    private readonly ValueLogOptions _valueLogOptions;
    // Non-null when the segment format does not store value-log pointers
    // (ISegmentFormat.SupportsValueLogPointers is false): value-log dispatch is then off whatever
    // _valueLogOptions says, and this is the largest admitted value.
    private readonly int? _inlineOnlyMaxValueBytes;

    // 128 MiB: the largest routing threshold ValueLogOptions accepts, i.e. the largest value any
    // engine configuration keeps inline, and half the SSTable block ceiling, so an inline value of
    // this size plus its key fits a block the reader accepts.
    private const int InlineOnlyMaxValueBytes = 128 * 1024 * 1024;
    private readonly string _valueLogDirectory;
    private ValueLogManifest? _valueLogManifest;
    private ValueLogWriter? _valueLogWriter;
    private ValueLogReader? _valueLogReader;

    // The MemTable's value slot is a discriminated union of "inline TValue" or
    // "ValueLogPointer". Each SetAsync produces exactly one MemTableValue and enters the
    // tree once; reads dispatch on Kind to either return the inline value directly or
    // dereference the pointer through ValueLogReader.Read + TValue.Deserialize.
    //
    // Active+Immutable rotation (DDIA Ch. 4 p. 117–119). The single
    // _memTable / _memTableTombstones pair has been replaced with one active slot plus a
    // bounded queue of sealed-but-not-yet-flushed immutable slots. A background flusher
    // Task drains the head of _immutables into an SSTable; ingest blocks only when the
    // queue saturates (the engine's overload back-pressure signal, DDIA p. 122–124).
    private MemtableSlot<TKey, TValue> _active;
    private readonly List<MemtableSlot<TKey, TValue>> _immutables = [];

    // Bounded-queue back-pressure gate. Initial / max count = MaxImmutableMemtables.
    // Every Seal consumes a slot; every Persist (flusher commit step) releases one.
    // ClearAsync releases one per discarded slot.
    private readonly SemaphoreSlim _ingestGate;

    // Cancels ingest-gate waits when the engine enters a terminal state. A faulted immutable
    // retains its queue permit until ClearAsync discards it, so the semaphore alone cannot wake
    // callers that were already waiting when the flusher failed. Linking this signal into each
    // wait lets those callers surface the captured fault instead of waiting forever.
    private readonly CancellationTokenSource _terminalStateCts = new();

    // Signal-only mailbox for the flusher loop. Capacity 1 with DropWrite collapses
    // multiple seals in rapid succession into a single wake-up; the flusher then loops
    // until _immutables drains.
    private readonly Channel<byte> _flushChannel;

    // Long-lived background flusher loop. Started at end of ctor; cancellation via
    // _flusherCts at Dispose causes the loop to exit cleanly.
    private readonly Task _flusherLoop;
    private readonly CancellationTokenSource _flusherCts = new();

    // Monotonic identity source for MemtableSlot.Generation. Incremented under _lock
    // by AllocateActiveSlot before any state mutation; sole purpose is the flusher's
    // post-SST-write head-of-queue equality check.
    private long _slotGenerationCounter;

    // Captured exception from the flusher's first SST-write or commit-step failure.
    // Engine transitions to a terminal Faulted state; subsequent writes throw, reads
    // continue to serve in-memory data. Volatile-read-pattern for fast path
    // (ThrowIfFaulted is on every public mutator).
    private ExceptionDispatchInfo? _faultedReason;

    // Set by the constructor when the segment folder holds a clear marker: the interrupted clear is
    // completed during recovery instead of replaying the WAL.
    private readonly bool _clearPendingAtOpen;

    private readonly int _maxImmutableMemtables;
    private readonly List<ISegmentReader<TKey, TValue>> _segments = [];

    // Engine-wide monotonic sequence-number counter. Tracked as a
    // long so Interlocked operations are lock-free; surfaced as ulong on the public API.
    // 0 means "nothing assigned yet"; the first assigned seqno is 1.
    private long _lastAssignedSequenceNumber;

    // Parallel list of segment filenames kept in lock-step with _segments, used to write
    // the CURRENT manifest after every segment-set change. The manifest is the durable
    // source of truth for recovery (see SegmentManifest).
    private readonly List<string> _segmentFileNames = [];

    // Parallel list of (CRC32C, SizeBytes) kept in lock-step with _segmentFileNames.
    // the manifest records these per segment so recovery can reject bit-rot
    // without re-reading the entire file each write. Populated at segment-write time.
    private readonly List<(uint Crc32C, long SizeBytes)> _segmentIntegrity = [];

    // Parallel list of per-segment max-seqno kept in lock-step with _segmentFileNames.
    // recovery reads this on startup to reseed the engine counter
    // to max(sst_max, wal_max) + 1 before accepting new writes. Populated at flush time
    // (MemTable seqnos) and at recovery time (SortedStringTable.MaxSequenceNumber).
    private readonly List<ulong> _segmentMaxSeqnos = [];

    // Per-segment level kept in lock-step with _segments (every mutation touches both lists).
    // The compaction strategy consumes this via BuildSegmentMetadataList; the segment's range
    // is read directly from ISegmentReader.TryGetRange — no parallel-key-range list to
    // maintain.
    private readonly List<int> _segmentLevels = [];
    private int _segmentCounter;
    // Volatile: written by Dispose/DisposeAsync and read without _lock by the ObjectDisposedException
    // guards on the public surface (e.g. SetAsync) and by OnSnapshotDisposedAsync. The volatile
    // read/write pair gives those lock-free reads a release/acquire fence against the teardown write.
    private volatile bool _disposed;

    // First-caller-wins dispose gate. A plain `if (_disposed) return;` at the top of Dispose /
    // DisposeAsync is a check-then-act race: two concurrent disposers can both observe _disposed
    // == false and BOTH run teardown, double-disposing the SemaphoreSlims / CTS (finding F-38).
    // Interlocked.Exchange makes exactly one caller win the race; every other caller sees the
    // gate already taken and returns without re-running teardown. 0 = not yet disposing, 1 =
    // a caller has claimed teardown.
    private int _disposeGate;

    // Disposal must not tear down state while an operation that entered before the disposed flag
    // is still running or suspended on one of the engine's gates. Every public operation that
    // takes _lock, _compactionGate or _gcGate — ingest, flush, reads, snapshots, clear, compaction,
    // value-log GC — is admitted through this lease. The gate closes admission atomically with the
    // disposed flag, and the drain event lets teardown wait until every admitted operation has
    // finished: the one holding a gate completes, and each one queued behind it wakes, re-checks
    // the flag and throws ObjectDisposedException. The semaphores themselves are never disposed —
    // SemaphoreSlim.Dispose does not complete existing waiters and makes the holder's Release
    // throw, and none of them ever allocates a kernel handle.
    private readonly object _ingestLifetimeGate = new();
    private readonly ManualResetEventSlim _ingestOperationsDrained = new(initialState: true);
    private int _activeIngestOperations;

    // Per-segment tombstone tracking: each segment carries the set of deleted keys plus
    // the parallel seqno map (snapshot-visibility filter + compaction GC predicate). The
    // active memtable's tombstones live on its MemtableSlot; sealed slots carry their
    // own tombstones forward through the flusher into _segmentTombstones at commit time.
    private readonly List<HashSet<TKey>> _segmentTombstones = [];

    // per-segment tombstone seqno
    // map kept in lock-step with _segmentTombstones. Snapshot visibility needs the
    // tombstone's seqno so a tombstone written after acquisition can be filtered from
    // the snapshot's view; the compactor needs it to defer tombstone GC while a live
    // snapshot can still observe the delete. Recovery populates empty maps because
    // per-segment tombstones are not on-disk metadata in v2.0.0.
    private readonly List<Dictionary<TKey, ulong>> _segmentTombstoneSeqnos = [];

    // registry of live snapshots. Compaction consults
    // this before physically deleting any segment; the tombstone-strip rule consults
    // it for the min-seqno-across-snapshots predicate.
    private readonly SnapshotRegistry _snapshotRegistry = new();

    // Segment files that compaction superseded but could not delete because a live snapshot
    // pinned them at the time. On every snapshot dispose + every compaction cycle, this set
    // is swept against the registry: any entry no longer pinned by any live snapshot is
    // physically deleted (pin atomicity).
    private readonly HashSet<string> _retiredPinnedFiles = new(StringComparer.Ordinal);

    // Diagnostic mirror of _retiredPinnedFiles.Count, surfaced lock-free through the public
    // RetiredPinnedFileCount property so operators can observe the deferred-reclamation backlog
    // a held-open snapshot creates (a long-lived snapshot defers physical delete of every
    // compaction-superseded segment it pinned until it disposes). Maintained alongside every
    // _retiredPinnedFiles Add/Remove under _lock; read via Volatile.Read so the diagnostic never
    // takes the async write lock.
    private int _retiredPinnedFileCount;

    // Readers kept alive for retired-but-pinned segments so the snapshots holding them can
    // still serve reads. Indexed by filename so we can resolve ClearAsync on physical delete.
    private readonly Dictionary<string, ISegmentReader<TKey, TValue>> _retiredPinnedReaders =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Value-log pointers belonging to writes whose value-log + WAL appends are durable but
    /// whose memtable apply is still pending (parked in the ingest-gate lock-release window
    /// of <see cref="EnsureActiveHasCapacityAsync"/>). Mutated and read only under
    /// <see cref="_lock"/>; included in the GC liveness snapshot so an in-flight acknowledged
    /// write's file can never be reclaimed.
    /// </summary>
    private readonly HashSet<(uint FileIndex, ulong Offset)> _inFlightValueLogPointers = [];

    // The asynchronous half of recovery (value-log open + WAL replay), started exactly once.
    // OpenAsync starts it eagerly with its caller's token; otherwise the first public operation
    // starts it (see EnsureInitialisedAsync). Without the lazy start, a constructor-built engine
    // over an existing WAL would serve reads without the unflushed frames, and its first flush
    // would truncate them from the log.
    private Task? _initialisation;
    private readonly object _initialisationGate = new();

    /// <summary>
    /// Sole constructor. Takes a composite
    /// <see cref="LsmStorageEngineOptions{TKey, TValue}"/> record carrying segment-format,
    /// compaction, WAL, and WiscKey value-log knobs, so the engine's orthogonal axes
    /// (MemTable capacity, segment folder + prefix + format, compaction threshold + strategy,
    /// WAL, value-log knobs) stay named rather than positional.
    /// </summary>
    /// <remarks>
    /// Recovers the on-disk segments synchronously. The asynchronous recovery steps — value-log
    /// open and WriteAheadLog replay — run before the first operation completes, so an engine
    /// reopened through this constructor serves every acknowledged WAL write. Use
    /// <see cref="OpenAsync"/> to run them eagerly and observe replay failures at open.
    /// </remarks>
    /// <param name="options">
    /// Composite configuration. Every field is validated (MemTableCapacity ≥ 1, non-null
    /// SegmentFolder / SegmentPrefix / SegmentFormat).
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="options"/> or any of its required fields is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// MemTableCapacity &lt; 1.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// BlockCache is non-null and is not the cache the segment format reads through.
    /// </exception>
    public LsmStorageEngine(LsmStorageEngineOptions<TKey, TValue> options)
    {
        Guard.AgainstNullOrDefault(() => options);

        if (options.MemTableCapacity < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), "MemTable capacity must be at least 1.");
        }

        if (options.MaxImmutableMemtables < 1)
        {
            // The active+immutable rotation requires at least one queue slot for the flusher
            // to pick up. A value of 1 is the smallest sensible setting (seal blocks until
            // the previous flush commits — equivalent to the historical inline-flush shape).
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MaxImmutableMemtables must be at least 1 — the active+immutable rotation "
                + "requires at least one queue slot for the flusher to pick up.");
        }

        Guard.AgainstNullOrDefault(() => options.SegmentFolder);
        Guard.AgainstNullOrDefault(() => options.SegmentPrefix);
        // Segment filenames are "{prefix}_{counter}{ext}" and are recorded in the manifest, whose reader
        // trims rows and splits on tab: a prefix it cannot round-trip would flush and acknowledge data
        // the next open cannot find. Refuse it here, before anything is written.
        if (!SegmentManifest.IsRoundTrippableName(options.SegmentPrefix))
        {
            throw new ArgumentException(
                $"SegmentPrefix '{options.SegmentPrefix}' cannot form a manifest-recordable segment filename: " +
                "it must not start with whitespace or contain a control character (tab, line break) or a path separator.",
                nameof(options));
        }
        Guard.AgainstNullOrDefault(() => options.SegmentFormat);

        // Reads consult only the cache the segment format was built with; the engine cannot hand a
        // cache to a format that already exists. A cache on the options that the format does not
        // read through would report caching no read ever gets, so it is refused.
        if (options.BlockCache is not null
            && !(options.SegmentFormat is SstSegmentFormat<TKey, TValue> sst && ReferenceEquals(sst.BlockCache, options.BlockCache)))
        {
            throw new ArgumentException(
                "LsmStorageEngineOptions.BlockCache must be the cache the segment format reads through: construct "
                + "the format as new SstSegmentFormat<TKey, TValue>(blockCache: cache) and pass the same cache here, "
                + "or leave BlockCache null.",
                nameof(options));
        }

        _memTableCapacity = options.MemTableCapacity;
        _segmentFolder = options.SegmentFolder;
        _segmentPrefix = options.SegmentPrefix;
        _segmentFormat = options.SegmentFormat;
        _wal = options.WriteAheadLog;
        // single compaction knob. The strategy is the only compaction-trigger source;
        // the engine no longer carries a separate `CompactionThreshold` integer that overlapped
        // with the strategy's `ShouldCompact` decision (DDIA Ch. 4 p. 124 prescribes one
        // strategy interface, not primitive obsession). Default: size-tiered (Cassandra-style),
        // the simpler model with lower write amplification, mapping cleanly to DDIA Ch. 4's
        // primary worked example. Consumers needing leveled compaction supply their own
        // `ICompactionStrategy<TKey>` implementation.
        _compactionStrategy = options.CompactionStrategy ?? new SizeTieredCompactionStrategy<TKey>();
        _blockCache = options.BlockCache;
        _corruptSegmentRecovery = options.CorruptSegmentRecovery;
        _onCorruptSegmentExcluded = options.OnCorruptSegmentExcluded;
        _valueLogOptions = options.ResolvedValueLog;
        // A format that does not declare it can store a pointer pins dispatch off here, at
        // resolution, so no configuration — a null ValueLog resolving to the 1 KiB default
        // included — can divert a value the flush would then refuse after the write was
        // acknowledged. The capability is a member of the segment-format contract, so a decorator
        // that does not forward it lands on the inline path every format can store.
        _inlineOnlyMaxValueBytes = _segmentFormat.SupportsValueLogPointers ? null : InlineOnlyMaxValueBytes;
        _valueLogDirectory = Path.Combine(_segmentFolder, "valuelog");
        _maxImmutableMemtables = options.MaxImmutableMemtables;

        // Bounded-queue back-pressure (DDIA Ch. 4 p. 122–124). Initial
        // and max count = MaxImmutableMemtables; every Seal consumes a slot, every Persist
        // releases one. Constructed BEFORE RecoverSegmentsFromDisk and the flusher Task so
        // ReplayWalAsync's seal-during-replay path finds a fully-wired engine.
        _ingestGate = new SemaphoreSlim(_maxImmutableMemtables, _maxImmutableMemtables);
        _flushChannel = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });
        _active = AllocateActiveSlot();

        // Ensure the segment folder exists.
        if (!Directory.Exists(_segmentFolder))
        {
            Directory.CreateDirectory(_segmentFolder);
        }

        // A clear marker means a WAL-backed clear committed and may not have finished. Finish its
        // segment half now — CURRENT empty, so the sweep below reclaims every segment file as an
        // orphan — and its WAL half in InitialiseAsync, which truncates the WAL instead of
        // replaying it.
        _clearPendingAtOpen = File.Exists(Path.Combine(_segmentFolder, ClearPendingMarkerFileName));
        if (_clearPendingAtOpen)
        {
            SegmentManifest.WriteEntries(_segmentFolder, []);
        }

        // Recovery: scan for existing segment files and load them in sorted order (sync).
        try
        {
            RecoverSegmentsFromDisk();
        }
        catch
        {
            // A later segment can require an unavailable Bloom codec after earlier readers
            // have already restored owned filters. A failed constructor has no caller to dispose it.
            foreach (var reader in _segments.OfType<IDisposable>())
            {
                reader.Dispose();
            }
            throw;
        }

        // Start the flusher loop. Idle until the first Seal writes a signal to the channel;
        // the loop drains every queued slot per signal, then awaits the next signal.
        _flusherLoop = Task.Run(() => FlusherLoopAsync(_flusherCts.Token));
    }

    /// <summary>
    /// Allocates a fresh active <see cref="MemtableSlot{TKey, TValue}"/>. Caller — the
    /// constructor or <c>SealActiveLocked</c> — must hold <see cref="_lock"/> when this is
    /// invoked from a mutator path. The generation counter increments before the slot is
    /// constructed so the slot's identity is established before any container mutation.
    /// </summary>
    private MemtableSlot<TKey, TValue> AllocateActiveSlot()
    {
        var generation = (ulong)Interlocked.Increment(ref _slotGenerationCounter);
        return new MemtableSlot<TKey, TValue>(
            generation,
            new RedBlackTree<TKey, MemTableValue<TValue>>(_memTableCapacity),
            tombstones: new Dictionary<TKey, ulong>());
    }

    /// <summary>
    /// Re-throws the captured flusher exception when the engine is in its terminal Faulted
    /// state. Wraps the original in <see cref="IOException"/>
    /// so callers can pattern-match on the engine's "flush failed" condition.
    /// </summary>
    private void ThrowIfFaulted()
    {
        var edi = Volatile.Read(ref _faultedReason);
        if (edi is not null)
        {
            throw new IOException(
                "LSM storage engine is in a faulted state; the most recent flush failed.",
                edi.SourceException);
        }
    }

    /// <summary>
    /// Async factory that constructs and fully initialises a
    /// <see cref="LsmStorageEngine{TKey, TValue}"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The constructor performs only cheap synchronous setup (validation, field
    /// initialisation, directory creation, SSTable recovery from disk). All async work —
    /// opening the value-log subsystem and replaying the write-ahead log — is deferred to
    /// this method, eliminating <c>GetAwaiter().GetResult()</c> sync-over-async calls at
    /// startup.
    /// </para>
    /// <para>
    /// On failure, <see cref="Dispose"/> is called on the partially-initialised engine
    /// before the exception propagates. If the underlying fault is an <see cref="IOException"/>
    /// it is wrapped in <see cref="InvalidDataException"/> so callers can distinguish
    /// corrupt-on-disk from other errors; non-I/O exceptions propagate directly.
    /// </para>
    /// </remarks>
    /// <param name="options">Composite configuration; every field is validated.</param>
    /// <param name="cancellationToken">Propagated to async recovery steps.</param>
    /// <returns>A fully-initialised engine ready to accept writes and reads.</returns>
    /// <exception cref="InvalidDataException">WAL or value-log replay failed due to I/O.</exception>
    public static async Task<LsmStorageEngine<TKey, TValue>> OpenAsync(
        LsmStorageEngineOptions<TKey, TValue> options,
        CancellationToken cancellationToken = default)
    {
        var engine = new LsmStorageEngine<TKey, TValue>(options);
        try
        {
            lock (engine._initialisationGate)
            {
                engine._initialisation = engine.InitialiseAsync(cancellationToken);
            }

            await engine._initialisation.ConfigureAwait(false);
            return engine;
        }
        catch (IOException ex)
        {
            engine.Dispose();
            throw new InvalidDataException("LSM engine initialisation failed due to I/O error.", ex);
        }
        catch
        {
            engine.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Performs async post-construction recovery (value-log open + WAL replay).
    /// Separated from the constructor to eliminate sync-over-async calls at startup.
    /// </summary>
    private async Task InitialiseAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Open the value-log subsystem BEFORE WAL replay so FileSizes reflects the
        // post-truncation durable EOF and the replay's dangling-pointer check can consult it.
        if (Directory.Exists(_valueLogDirectory))
        {
            await EnsureValueLogOpenAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_clearPendingAtOpen)
        {
            // Complete the interrupted clear: its WAL frames are pre-clear writes, never replayed.
            // The marker goes only after the truncation, durably, so a crash here retries it.
            if (_wal != null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await _wal.TruncateAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var markerPath = Path.Combine(_segmentFolder, ClearPendingMarkerFileName);
            File.Delete(markerPath);
            if (!OperatingSystem.IsWindows())
            {
                DurableFile.FsyncDirectory(markerPath);
            }

            return;
        }

        // Replay the write-ahead log into the MemTable.
        if (_wal != null)
        {
            await ReplayWalAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Completes the asynchronous half of recovery before an operation proceeds, starting it on
    /// first use for a constructor-built engine. Every public operation awaits this BEFORE taking
    /// <see cref="_lock"/>, because WAL replay takes that lock per entry. The shared recovery
    /// runs with the engine's terminal-state token so one caller's cancellation cannot fail it
    /// for every later caller; each caller's own token only abandons its wait. Teardown cancels
    /// and joins shared recovery before releasing its resources. A recovery
    /// failure is rethrown by every subsequent operation (fail closed).
    /// </summary>
    private Task EnsureInitialisedAsync(CancellationToken cancellationToken)
    {
        var initialisation = Volatile.Read(ref _initialisation);
        if (initialisation is null)
        {
            lock (_initialisationGate)
            {
                initialisation = _initialisation ??= InitialiseAsync(_terminalStateCts.Token);
            }
        }

        return initialisation.IsCompletedSuccessfully
            ? Task.CompletedTask
            : initialisation.WaitAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public int SegmentCount => _segments.Count;

    /// <inheritdoc />
    /// <remarks>
    /// Reports isolation-via-pinning capability, which is unconditionally available: the LSM
    /// engine enforces snapshot isolation by pinning the captured segment readers and filenames
    /// and taking an immutable at-acquisition memtable copy (see
    /// <see cref="AcquireSnapshotAsync"/>), a mechanism that does not depend on the segment
    /// format carrying a per-entry sequence number. A pinned segment is immutable
    /// pre-acquisition state; post-acquisition writes land in the live memtable or new segments
    /// the snapshot never captured, so they stay invisible regardless of whether the format
    /// stamps per-entry seqnos. The narrower "does this format carry a per-entry seqno slot for
    /// filtered reads?" predicate lives on
    /// <see cref="ISegmentFormat{TKey, TValue}.SupportsSnapshots"/> and is deliberately NOT the
    /// engine-level isolation gate.
    /// </remarks>
    public bool SupportsSnapshots => true;

    /// <summary>
    /// The number of compaction-superseded segment files currently parked (kept on disk and
    /// readable) because at least one live snapshot pinned them. Zero when no held-open snapshot
    /// is deferring reclamation.
    /// </summary>
    /// <remarks>
    /// A long-lived snapshot defers physical delete of every segment a concurrent compaction
    /// superseded while that snapshot held a pin (DDIA 2e Ch. 8 — pinned segments survive
    /// compaction so the snapshot's reads stay valid). This counter surfaces that deferred
    /// backlog so an operator can see why disk is not being reclaimed under a held-open snapshot,
    /// rather than discovering it empirically. The count drops to zero once the last pinning
    /// snapshot disposes and the sweep reclaims the parked files. Read lock-free via
    /// <c>Volatile.Read</c>; the value is a point-in-time diagnostic, not a synchronization point.
    /// </remarks>
    public int RetiredPinnedFileCount => Volatile.Read(ref _retiredPinnedFileCount);

    /// <summary>Highest sequence number assigned so far. 0 when no writes have happened.</summary>
    /// <remarks>
    /// Recovery-aware: once recovery has completed (on <see cref="OpenAsync"/> return, or after
    /// the first operation on a constructor-built engine) with a WriteAheadLog and surviving
    /// SSTable segments, this reports <c>max(sst_max, wal_max)</c> — the invariant
    /// prescribes for pre-accepting-new-writes state.
    /// </remarks>
    public ulong LastAssignedSequenceNumber => (ulong)Interlocked.Read(ref _lastAssignedSequenceNumber);

    /// <summary>Next sequence number the engine will assign. Always <c>LastAssignedSequenceNumber + 1</c>.</summary>
    public ulong NextSequenceNumber => LastAssignedSequenceNumber + 1;

    /// <summary>
    /// Test-only seam invoked between the lock release and the ingest-gate registration in
    /// <see cref="EnsureActiveHasCapacityAsync"/>. Lets a test deterministically park one
    /// mutator inside that window while a later-seqno mutator overtakes it. Always
    /// <see langword="null"/> in production.
    /// </summary>
    internal Func<Task>? IngestGateWindowHook { get; set; }

    // Test seam: parks a compaction inside its merge window (immediately before the compacted
    // segment write) so tests can deterministically overlap reads/writes/Clear with an
    // in-flight merge. Null in production — same convention as IngestGateWindowHook.
    internal Func<Task>? CompactionMergeHook { get; set; }

    // Test seam at the durable compaction commit boundary.
    internal Action? CompactionManifestCommittedHook { get; set; }

    /// <summary>
    /// Test-only seam invoked by <see cref="ClearAsync"/> between its in-flight drain and its
    /// lock acquisition. Lets a test deterministically inject a seal + flusher pickup into
    /// that window to reproduce the clear-vs-flusher-commit race. Always
    /// <see langword="null"/> in production.
    /// </summary>
    internal Func<Task>? ClearDrainToLockHook { get; set; }

    /// <summary>
    /// Test-only seam invoked by <see cref="ClearAsync"/> immediately before it empties
    /// <c>CURRENT</c>, inside the same failure handling as that write: an exception thrown here is
    /// observed exactly as a manifest write that failed before replacing <c>CURRENT</c> (the
    /// disk-full temp-file case). Always <see langword="null"/> in production.
    /// </summary>
    internal Action? ClearManifestWriteHook { get; set; }

    /// <summary>
    /// Test-only seam invoked after the background flusher acquires the commit lock and before
    /// it observes disposal. Lets a test deterministically reproduce disposal racing with the
    /// flusher's commit step. Always <see langword="null"/> in production.
    /// </summary>
    internal Func<CancellationToken, Task>? FlusherCommitLockHook { get; set; }

    /// <summary>
    /// Test-only seam invoked by the value-log GC's lock-free segment scan (Phase B) just
    /// before it reads any captured segment reader — i.e. in the window after the engine lock
    /// was released by <c>SnapshotSegmentsForGc</c>'s caller and before the scan touches a
    /// reader. Lets a test deterministically inject the reader-disposal a concurrent compaction
    /// would do, to reproduce the GC-scan-vs-compaction race without timing. Always
    /// <see langword="null"/> in production.
    /// </summary>
    internal Action? GcScanPhaseBHook { get; set; }

    /// <summary>
    /// The segment format this engine writes and opens. Lets a decorator verify that the format it
    /// is configured to describe is the one actually in use.
    /// </summary>
    internal ISegmentFormat<TKey, TValue> SegmentFormat => _segmentFormat;

    /// <inheritdoc/>
    public async Task SetAsync(TKey key, TValue value, CancellationToken cancellationToken = default)
    {
        using var operation = EnterIngestOperation();
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfFaulted();
        Guard.AgainstNullOrDefault(() => key);
        Guard.AgainstNullOrDefault(() => value);
        ValidateEntryAdmission(key, value);
        await EnsureInitialisedAsync(cancellationToken).ConfigureAwait(false);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ThrowIfFaulted();
            await SetInternalAsync(key, value, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc/>
    void ILsmEntryAdmissionCheck<TKey, TValue>.ValidateEntryAdmission(TKey key, TValue value)
    {
        Guard.AgainstNullOrDefault(() => key);
        Guard.AgainstNullOrDefault(() => value);
        ValidateEntryAdmission(key, value);
    }

    /// <summary>
    /// Refuses, before anything is acknowledged, an entry the segment format would refuse at
    /// flush: an acknowledged entry the flush cannot write faults that flush and every later one,
    /// and a WAL replays it into every reopen.
    /// </summary>
    private void ValidateEntryAdmission(TKey key, TValue value)
    {
        (_segmentFormat as ISegmentEntryAdmission<TKey, TValue>)?.ValidateEntry(key, value);
        if (_inlineOnlyMaxValueBytes is { } inlineCeiling)
        {
            // Dispatch is off, so a value above the ceiling has nowhere to go: refuse it now rather
            // than acknowledge a write no segment of this format can hold.
            var valueBytes = SerializedByteCount(value);
            if (valueBytes > inlineCeiling)
            {
                throw new ValueTooLargeException(valueBytes, inlineCeiling);
            }
        }

        if (_segmentFormat is not ISegmentEntrySizeLimit sizeLimit)
        {
            return;
        }

        // The flush stores a value's serialized bytes when they are at most the routing threshold
        // and a value-log pointer otherwise, so no stored value exceeds the larger of the two:
        // the value is measured only for a key too large for that bound. With dispatch off the
        // inline ceiling plays the threshold's part.
        var threshold = _inlineOnlyMaxValueBytes ?? _valueLogOptions.ValueLogThresholdBytes;
        var keyBytes = SerializedByteCount(key);
        if (keyBytes + Math.Max(threshold, ValueLogPointer.SerializedSize) <= sizeLimit.MaxEntryKeyAndValueBytes)
        {
            return;
        }

        var payloadBytes = SerializedByteCount(value);
        var storedValueBytes = payloadBytes > threshold ? ValueLogPointer.SerializedSize : payloadBytes;
        if (keyBytes + storedValueBytes > sizeLimit.MaxEntryKeyAndValueBytes)
        {
            throw new ArgumentException(
                $"An entry with a {keyBytes}-byte key and a {storedValueBytes}-byte stored value exceeds the "
                + $"{sizeLimit.MaxEntryKeyAndValueBytes}-byte key-plus-value ceiling of the segment format, "
                + "which would refuse it at flush.",
                nameof(key));
        }
    }

    private static long SerializedByteCount<T>(T item)
        where T : ISerializable<T>
    {
        using var counter = new ByteCountingStream();
        item.Serialize(counter);
        return counter.Length;
    }

    /// <summary>A write-only stream that counts the bytes written to it and keeps none.</summary>
    private sealed class ByteCountingStream : Stream
    {
        private long _length;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => _length;

        public override long Position
        {
            get => _length;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            ValidateBufferArguments(buffer, offset, count);
            _length += count;
        }

        public override void Write(ReadOnlySpan<byte> buffer) => _length += buffer.Length;

        public override void WriteByte(byte value) => _length++;
    }

    // Threshold-driven Set dispatch. Caller must hold <see cref="_lock"/>. Serialises TValue
    // once, routes through the value log when
    // the payload exceeds the threshold (durability ordering: value-log append + fsync →
    // WAL append + fsync → MemTable apply), and falls back to the inline path otherwise.
    private async Task SetInternalAsync(TKey key, TValue value, CancellationToken cancellationToken)
    {
        // Validate at the lowest shared apply point so EVERY caller (SetAsync and the
        // SetBulkAsync batch loop) rejects a null/default key or value with a typed
        // ArgumentException, rather than letting a default-keyed entry reach the memtable or an
        // NRE escape from value.Serialize. SetAsync also guards up front (fail-fast before the
        // lock); the duplicate guard here is cheap and closes the SetBulkAsync path (finding F-26).
        Guard.AgainstNullOrDefault(() => key);
        Guard.AgainstNullOrDefault(() => value);

        // Serialise TValue to a pooled buffer so the length check + value-log append reuse
        // the same bytes without a second serialise call. Unread buffers go back to the pool.
        using var payloadStream = new MemoryStream();
        value.Serialize(payloadStream);
        var payloadLength = (int)payloadStream.Length;
        if (payloadLength <= 0)
        {
            throw new InvalidOperationException(
                "TValue.Serialize emitted zero bytes; every value must carry at least one byte.");
        }

        // Rotation gate FIRST — before the seqno is assigned and before any WAL / value-log
        // frame is appended. EnsureActiveHasCapacityAsync releases _lock across the gate wait,
        // so anything already assigned/appended before it can be overtaken by later-seqno
        // mutators and re-applied out of order across SLOTS: a positional point read then
        // disagrees with the seqno-aware scan (a flushed newer tombstone loses to a memtable
        // hit), and the flusher's WAL-truncation watermark can cover a frame whose data has
        // not reached any slot yet — deleting the only durable copy of an acknowledged write.
        // With the gate first, seqno assignment, durable append, and memtable apply all happen
        // under ONE continuous lock hold: apply order provably equals seqno order, WAL frame
        // order equals seqno order, and every WAL frame at or below a flushed slot's max seqno
        // has provably been applied to that slot or an earlier one.
        await EnsureActiveHasCapacityAsync(cancellationToken).ConfigureAwait(false);

        // Assign the sequence number BEFORE the WAL / value-log append. If that append then
        // throws (disk full, I/O fault), this seqno is consumed but never lands in any frame —
        // the next successful write takes seqno+1, leaving a gap in the on-disk seqno sequence.
        // That gap is INTENTIONAL and harmless: seqnos are only required to be strictly
        // monotonic (newer > older) for last-writer-wins resolution, NOT contiguous. A gap never
        // reorders two writes and never resurrects a value. We do not roll the counter back on
        // failure because a concurrent writer may already have claimed seqno+1 (the counter is
        // shared and lock-free), so a rollback could hand the same number to two writes — a far
        // worse outcome than a skipped number (finding F-27).
        var seqno = (ulong)Interlocked.Increment(ref _lastAssignedSequenceNumber);

        if (_inlineOnlyMaxValueBytes is null && payloadLength > _valueLogOptions.ValueLogThresholdBytes)
        {
            // Pointer path — value bytes to value log first; pointer into WAL + MemTable.
            await EnsureValueLogOpenAsync(cancellationToken).ConfigureAwait(false);
            var payloadBuffer = payloadStream.TryGetBuffer(out var seg)
                ? seg
                : new ArraySegment<byte>(payloadStream.ToArray());
            var pointer = await _valueLogWriter!.AppendAsync(
                payloadBuffer.AsMemory(0, payloadLength), seqno, cancellationToken).ConfigureAwait(false);

            if (_wal != null)
            {
                await _wal.AppendPointerAsync(key, pointer, seqno, cancellationToken).ConfigureAwait(false);
            }

            // In-flight liveness pin: the GC's liveness scan has lock-free phases, so even
            // though append→apply now runs under one continuous _lock hold, a concurrent GC
            // round could still snapshot liveness in the instants between the value-log append
            // and the memtable apply. Registering the pointer keeps the acknowledged write's
            // file alive until the apply lands; the finally unpins on every exit path.
            _inFlightValueLogPointers.Add((pointer.FileIndex, pointer.Offset));
            try
            {
                // The WAL frame is the commit point: once it is durable, recovery will install this
                // write, so the apply runs to completion regardless of the caller's token — otherwise a
                // late cancellation leaves the write invisible live and resurrected after reopen.
                await ApplyToActiveIfNewestAsync(
                    key, MemTableValue<TValue>.ValueLogPointer(pointer, seqno), CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _inFlightValueLogPointers.Remove((pointer.FileIndex, pointer.Offset));
            }
        }
        else
        {
            // Inline path — original pre-WiscKey behaviour.
            if (_wal != null)
            {
                await _wal.AppendAsync(key, value, seqno, cancellationToken).ConfigureAwait(false);
            }

            // Past the commit point — see the pointer path above.
            await ApplyToActiveIfNewestAsync(
                key, MemTableValue<TValue>.Inline(value, seqno), CancellationToken.None).ConfigureAwait(false);
        }

        // Remove any existing tombstone for this key — a new write supersedes a previous
        // delete — but ONLY when this write's seqno is newer. EnsureActiveHasCapacityAsync
        // releases the lock across the gate wait, so a later-seqno Remove can apply before
        // this Set resumes; erasing that newer tombstone unconditionally would lose the
        // acknowledged delete. Active-slot scope only; older slots' tombstones remain in
        // their own maps and are shadowed by the read-walk's newest-first traversal.
        if (_active.Tombstones.TryGetValue(key, out var existingTombstoneSeqno)
            && existingTombstoneSeqno < seqno)
        {
            _active.Tombstones.Remove(key);
        }
    }

    /// <summary>
    /// Applies a mutation to the active memtable only when it is newer than the entry already
    /// present. The seqno is assigned (and the WAL frame appended) under <see cref="_lock"/>,
    /// but the apply step can run after a lock-release / gate-wait / reacquire in
    /// <see cref="EnsureActiveHasCapacityAsync"/> — so a later-seqno mutator may have applied
    /// first, and an unconditional overwrite would roll its value back. Caller holds
    /// <see cref="_lock"/>.
    /// </summary>
    private async Task ApplyToActiveIfNewestAsync(
        TKey key, MemTableValue<TValue> value, CancellationToken cancellationToken)
    {
        var (existing, found) = await _active.Tree.TryGetValueAsync(key, cancellationToken).ConfigureAwait(false);
        if (found && existing.Seqno >= value.Seqno)
        {
            return;
        }
        await _active.Tree.SetAsync(key, value, cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureValueLogOpenAsync(CancellationToken cancellationToken)
    {
        if (_valueLogWriter is not null)
        {
            return;
        }

        Directory.CreateDirectory(_valueLogDirectory);
        _valueLogManifest ??= new ValueLogManifest(Path.Combine(_valueLogDirectory, "valuelog.manifest"));
        _valueLogReader ??= new ValueLogReader(_valueLogDirectory, _valueLogOptions);
        _valueLogWriter = await ValueLogWriter.OpenAsync(
            _valueLogDirectory, _valueLogManifest, _valueLogOptions, cancellationToken).ConfigureAwait(false);
    }

    // Resolves a <see cref="MemTableValue{TValue}"/> to its underlying <typeparamref name="TValue"/>,
    // dereferencing through the value-log reader when the entry is a pointer. Caller must
    // hold <see cref="_lock"/>.
    private TValue ResolveMemTableValue(MemTableValue<TValue> slot)
    {
        if (slot.Kind == ValueKind.Inline)
        {
            return slot.InlineValue;
        }
        return ReadFromValueLog(slot.Pointer);
    }

    /// <summary>
    /// Resolves a <see cref="BlockEntry"/> produced by <see cref="ISegmentReader{TKey, TValue}"/>
    /// to its underlying <typeparamref name="TValue"/>, dereferencing pointer entries via
    /// <see cref="ValueLogReader"/>. The pointer-dispatch step lives at the engine layer;
    /// neither segment readers nor snapshots re-enter the value-log reader.
    /// Visible to <see cref="LsmSnapshot{TKey, TValue}"/>.
    /// </summary>
    internal TValue ResolveBlockEntryValue(BlockEntry entry)
    {
        if (entry.Kind == ValueKind.Inline)
        {
            using var ms = new MemoryStream(entry.Value.ToArray(), writable: false);
            return TValue.Deserialize(ms);
        }
        var pointer = ValueLogPointer.ReadFrom(entry.Value.Span);
        return ReadFromValueLog(pointer);
    }

    /// <summary>
    /// Resolves a <see cref="SegmentWriteEntry{TKey, TValue}"/> — the variant
    /// <see cref="ISegmentReader{TKey, TValue}.StreamBlockEntriesAsync"/> yields — to its
    /// underlying <typeparamref name="TValue"/>, dereferencing pointer entries through the
    /// value-log reader. Mirrors <see cref="ResolveBlockEntryValue"/> but consumes the
    /// already-typed <see cref="SegmentWriteEntry{TKey, TValue}.InlineValue"/> directly so the
    /// row-format fallback in <c>LsmTimeSeriesStorageEngine</c> never calls
    /// <c>TValue.Deserialize</c> a second time on inline entries.
    /// </summary>
    /// <param name="entry">The stream entry to resolve.</param>
    /// <returns>The inline value, or the value dereferenced from the value log.</returns>
    internal TValue ResolveSegmentWriteEntryValue(SegmentWriteEntry<TKey, TValue> entry)
    {
        if (entry.Kind == ValueKind.Inline)
        {
            return entry.InlineValue!;
        }
        var pointer = ValueLogPointer.ReadFrom(entry.PointerBytes.Span);
        return ReadFromValueLog(pointer);
    }

    private TValue ReadFromValueLog(ValueLogPointer pointer)
    {
        _valueLogReader ??= new ValueLogReader(_valueLogDirectory, _valueLogOptions);
        var bytes = _valueLogReader.Read(pointer);
        using var stream = new MemoryStream(bytes, writable: false);
        return TValue.Deserialize(stream);
    }

    /// <summary>
    /// Engine-level filename list for every closed (non-current-writer) value-log segment
    /// and the filename of the current active writer segment. Used by the GC to enumerate
    /// candidate-for-reclamation files without re-reading the manifest. Holds no lock.
    /// </summary>
    internal ValueLogManifest? ValueLogManifestHandle => _valueLogManifest;

    internal ValueLogWriter? ValueLogWriterHandle => _valueLogWriter;

    internal string ValueLogDirectory => _valueLogDirectory;

    internal ValueLogOptions ValueLogOptionsAccess => _valueLogOptions;

    internal SemaphoreSlim EngineLock => _lock;

    /// <summary>
    /// Collects every unique <see cref="ValueLogPointer"/> reachable from the engine's live
    /// segment set (and retired-but-pinned readers). Called by <see cref="ValueLogGarbageCollector"/>
    /// during a GC round to compute the liveness set over closed value-log segments. Caller
    /// must hold <see cref="_lock"/>. The returned delegate runs the actual segment scan
    /// lock-free and returns <see langword="false"/> if a captured reader was disposed by a
    /// concurrent compaction mid-scan, signalling the caller to skip reclaim this round.
    /// </summary>
    // Called while _lock is held; captures segment reader references only (no I/O).
    // Returns a delegate that the GC invokes AFTER releasing _lock to do the actual scan.
    // The segment *bytes* are immutable once flushed, so scanning lock-free avoids the
    // full-dataset lock hold time on the hot GC path — but the reader *object* is NOT
    // immune to a concurrent compaction (CommitCompactionLockedAsync / ClearAsync /
    // SweepRetiredPinnedFilesUnderLockAsync, all under _lock) disposing it via
    // ISegmentReader.ClearAsync after we released _lock and before/while we scan it. The scan
    // therefore tolerates an ObjectDisposedException from a mid-scan disposal: it reports the
    // round as incomplete (returns false) so the caller skips reclaim entirely rather than
    // reclaiming on a partial liveness set. The next GC round (after compaction settles) makes
    // progress. This is fail-closed — no value-log file is ever unlinked on an incomplete scan.
    private Func<HashSet<(uint FileIndex, ulong Offset)>, bool> SnapshotSegmentsForGc()
    {
        var segsSnapshot = _segments.ToList();
        var retiredSnapshot = _retiredPinnedReaders.Values.ToList();

        // Finding #3 — a WiscKey pointer lives in the active/immutable memtable from write time
        // until flush. The GC liveness set MUST include those unflushed pointers, or GC will
        // reclaim a closed value-log file the engine still references through an unflushed
        // memtable, leaving a dangling pointer (DDIA 2e — WiscKey: GC must not free a referenced
        // value). The memtables are mutable, so capture their pointers NOW under the engine lock
        // rather than in the deferred lock-free delegate; the in-memory RB-tree traversal
        // completes synchronously.
        var memtablePointers = new List<(uint FileIndex, ulong Offset)>();
        CollectMemtablePointers(_active, memtablePointers);
        foreach (var slot in _immutables)
        {
            CollectMemtablePointers(slot, memtablePointers);
        }

        // In-flight pointers: a pointer-path Set parked in the ingest-gate window has its
        // value durable in the value log + WAL but applied to NO memtable yet. Those
        // pointers are registered in _inFlightValueLogPointers (mutated only under _lock,
        // which we hold here); without them GC would reclaim a closed file an acknowledged
        // write still references (DDIA 2e — WiscKey: GC must not free a referenced value).
        memtablePointers.AddRange(_inFlightValueLogPointers);

        return set =>
        {
            // Test-only seam: lets a test deterministically dispose a captured reader here,
            // in the post-lock-release window, to reproduce the GC-scan-vs-compaction race.
            GcScanPhaseBHook?.Invoke();

            foreach (var p in memtablePointers)
            {
                set.Add(p);
            }
            // Memtable + in-flight pointers were captured under _lock, so they are always
            // complete. The segment readers are scanned lock-free and CAN be disposed by a
            // concurrent compaction; if that happens, report the round incomplete so the caller
            // skips reclaim (fail-closed) rather than acting on a partial liveness set.
            foreach (var seg in segsSnapshot)
            {
                if (!TryCollectFromSegment(seg, set))
                {
                    return false;
                }
            }
            foreach (var seg in retiredSnapshot)
            {
                if (!TryCollectFromSegment(seg, set))
                {
                    return false;
                }
            }
            return true;
        };
    }

    private static void CollectMemtablePointers(
        MemtableSlot<TKey, TValue> slot, List<(uint FileIndex, ulong Offset)> acc)
    {
        // In-memory RB-tree traversal completes synchronously; the GetAwaiter().GetResult()
        // mirrors CollectFromSegment's in-engine-lock synchronous drain pattern.
        var items = slot.Tree.GetAllItemsAsync().GetAwaiter().GetResult();
        foreach (var (_, value) in items)
        {
            if (value.Kind == ValueKind.ValueLogPointer)
            {
                acc.Add((value.Pointer.FileIndex, value.Pointer.Offset));
            }
        }
    }

    // Scans one captured segment reader for live value-log pointers. Returns false (without
    // partially-trusting the result) when the reader was retired by a concurrent compaction
    // mid-scan — the lock-free GC scan races segment retirement (CommitCompactionLockedAsync /
    // ClearAsync / SweepRetiredPinnedFilesUnderLockAsync call ISegmentReader.ClearAsync, which
    // disposes the SortedStringTable AND deletes its backing file). Two failure shapes follow
    // from that retirement, both meaning "this captured reader is no longer usable":
    //   • ObjectDisposedException — the FileStream was closed while a block read was in flight;
    //   • IOException (incl. FileNotFoundException) — the backing file was unlinked before the
    //     scan reopened it.
    // Either way the liveness set is partial, so we return false and the caller skips reclaim
    // this round instead of faulting the whole CompactValueLogAsync call (the prior behaviour).
    private static bool TryCollectFromSegment(ISegmentReader<TKey, TValue> seg, HashSet<(uint, ulong)> acc)
    {
        try
        {
            CollectFromSegment(seg, acc);
            return true;
        }
        catch (Exception ex) when (ex is ObjectDisposedException or IOException)
        {
            // Reader retired by a concurrent compaction after Phase A captured it. The
            // liveness set is now partial; signal the caller to skip reclaim this round.
            return false;
        }
    }

    private static void CollectFromSegment(ISegmentReader<TKey, TValue> seg, HashSet<(uint, ulong)> acc)
    {
        // Fully materialise the segment's entries — GC is a cold path and the set
        // construction is bounded by segment size.
        IAsyncEnumerable<SegmentWriteEntry<TKey, TValue>> stream = seg.StreamBlockEntriesAsync();
        var result = new List<SegmentWriteEntry<TKey, TValue>>();
        var enumerator = stream.GetAsyncEnumerator();
        try
        {
            while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
            {
                result.Add(enumerator.Current);
            }
        }
        finally
        {
            enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        foreach (var entry in result)
        {
            if (entry.Kind == ValueKind.ValueLogPointer)
            {
                var p = ValueLogPointer.ReadFrom(entry.PointerBytes.Span);
                acc.Add((p.FileIndex, p.Offset));
            }
        }
    }

    /// <summary>
    /// Public API for on-demand value-log GC. Serialises GC rounds on this engine under a
    /// per-engine semaphore — two concurrent calls run one after the other, never racing the
    /// shared value-log manifest — while concurrent writes proceed unblocked against the
    /// writer's current append-target.
    /// </summary>
    public async Task<GcResult> CompactValueLogAsync(CancellationToken cancellationToken = default)
    {
        using var operation = EnterIngestOperation();
        // Acquire the per-engine GC gate BEFORE constructing the collector so two concurrent
        // CompactValueLogAsync calls on this engine are serialised. The gate cannot live on the
        // ValueLogGarbageCollector: a fresh instance is built per call, so a per-instance gate
        // would never be contended.
        await EnsureInitialisedAsync(cancellationToken).ConfigureAwait(false);
        await _gcGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var gc = new ValueLogGarbageCollector(this);
            return await gc.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gcGate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<(TValue Value, bool Found)> TryGetValueAsync(TKey key, CancellationToken cancellationToken = default)
    {
        using var operation = EnterIngestOperation();
        Guard.AgainstNullOrDefault(() => key);
        await EnsureInitialisedAsync(cancellationToken).ConfigureAwait(false);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await TryGetValueInternalAsync(key, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<bool> ContainsKeyAsync(TKey key, CancellationToken cancellationToken = default)
    {
        using var operation = EnterIngestOperation();
        Guard.AgainstNullOrDefault(() => key);
        await EnsureInitialisedAsync(cancellationToken).ConfigureAwait(false);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var (_, found) = await TryGetValueInternalAsync(key, cancellationToken).ConfigureAwait(false);
            return found;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Internal lock-free implementation of TryGetValueAsync. Must be called while holding _lock.
    /// Walks the active memtable first, then immutables newest-first (so a more-recent
    /// rotation's tombstone shadows an older immutable's data), then on-disk segments.
    /// </summary>
    private async Task<(TValue Value, bool Found)> TryGetValueInternalAsync(TKey key, CancellationToken cancellationToken)
    {
        // Active first.
        if (await TryProbeMemtableAsync(_active, key, cancellationToken).ConfigureAwait(false)
            is { Resolved: true } activeProbe)
        {
            return (activeProbe.Value, activeProbe.Found);
        }

        // Immutables newest-first (= reverse order in _immutables since L2 makes index N
        // the most-recently-sealed). A tombstone or hit in any slot terminates the walk.
        for (var i = _immutables.Count - 1; i >= 0; i--)
        {
            if (await TryProbeMemtableAsync(_immutables[i], key, cancellationToken).ConfigureAwait(false)
                is { Resolved: true } slotProbe)
            {
                return (slotProbe.Value, slotProbe.Found);
            }
        }

        // Search on-disk segments from newest to oldest.
        for (var i = _segments.Count - 1; i >= 0; i--)
        {
            // Check segment tombstones before searching the segment data. The check is
            // seqno-aware (mirrors LsmSnapshot.TryGetValueAsync and the merge/compaction paths):
            // a same-segment tombstone suppresses the segment's own data only when it is
            // newer-or-equal. Compaction preserves a tombstone into the output segment when the
            // picked set was non-contiguous OR a live snapshot pinned its seqno; that segment can
            // then hold BOTH the preserved tombstone (1@2) and a kept re-insert (1@3), and the
            // re-insert must win (DDIA 2e p. 121 — a later write shadows an earlier tombstone).
            // Columnar segments carry no per-row seqno; their reader reports the segment's footer
            // max seqno for a hit, and the columnar writer drops data older than a same-key
            // tombstone, so the comparison below holds for both formats. When the tombstone carries
            // no recorded seqno (absent from the seqno map) fall back to unconditional suppression.
            if (i < _segmentTombstones.Count && _segmentTombstones[i].Contains(key))
            {
                if (i < _segmentTombstoneSeqnos.Count
                    && _segmentTombstoneSeqnos[i].TryGetValue(key, out var tombstoneSeqno)
                    && !(_segments[i].TryGetRange(out var tsMinKey, out var tsMaxKey)
                         && (key.CompareTo(tsMinKey) < 0 || key.CompareTo(tsMaxKey) > 0)))
                {
                    var (tsEntry, tsDataFound) = await _segments[i]
                        .TryGetBlockEntryAsync(key, ulong.MaxValue, cancellationToken).ConfigureAwait(false);
                    if (tsDataFound && tsEntry.SequenceNumber > tombstoneSeqno)
                    {
                        // Re-insert flushed newer than the preserved tombstone — it wins.
                        return (ResolveBlockEntryValue(tsEntry), true);
                    }
                }

                return (default!, false);
            }

            // Range-skip the data probe: if the key is outside this segment's footer-persisted
            // [min, max] key range, the segment data provably cannot contain it, so skip the
            // block-index / bloom / block-read I/O of TryGetBlockEntryAsync entirely (the call would
            // only return not-found). The tombstone check above is left UNCONDITIONAL, so a tombstone
            // tracked for this segment is never bypassed — the skip narrows only the data probe, never
            // the delete semantics. Under leveled compaction the non-overlapping per-level key ranges
            // turn the all-segments walk into roughly one probed segment per level.
            if (_segments[i].TryGetRange(out var segMinKey, out var segMaxKey)
                && (key.CompareTo(segMinKey) < 0 || key.CompareTo(segMaxKey) > 0))
            {
                continue;
            }

            // Per-segment bloom filtering happens inside the segment reader's TryGetBlockEntry
            // path — SST readers consult the footer-persisted bloom (DDIA Ch. 4 p. 79–82); the
            // engine no longer maintains a parallel bloom list.
            //
            // Dispatch on BlockEntry.Kind. Inline entries deserialise directly; pointer entries
            // decode the 12-byte body and read through the value-log reader. The segment reader
            // never re-enters ValueLogReader (V-INT-4 is maintained).
            var (entry, segFound) = await _segments[i]
                .TryGetBlockEntryAsync(key, ulong.MaxValue, cancellationToken).ConfigureAwait(false);
            if (segFound)
            {
                return (ResolveBlockEntryValue(entry), true);
            }
        }

        return (default!, false);
    }

    /// <summary>
    /// Probes a single memtable slot for a key. Returns a tuple where <c>Resolved</c>
    /// indicates whether THIS slot resolved the lookup (data hit, tombstone miss, or
    /// shadowed-by-tombstone). When <c>Resolved == true</c>, the caller stops walking;
    /// when <c>Resolved == false</c>, the caller continues to the next slot.
    /// </summary>
    /// <remarks>
    /// Tombstone resolution is per-slot: a tombstone in the active does not need a
    /// parallel tombstone in an older immutable to mask older segment data, because the
    /// walk stops at the first slot that has either a data or tombstone entry for the key.
    /// </remarks>
    private async Task<(bool Resolved, TValue Value, bool Found)> TryProbeMemtableAsync(
        MemtableSlot<TKey, TValue> slot, TKey key, CancellationToken cancellationToken)
    {
        var (slotValue, hit) = await slot.Tree.TryGetValueAsync(key, cancellationToken).ConfigureAwait(false);
        if (slot.Tombstones.TryGetValue(key, out var tombstoneSeqno))
        {
            // Tombstone present in this slot — a Set in the SAME slot wins iff its seqno
            // is strictly greater than the tombstone's (matches today's single-memtable
            // semantics, which the rotation preserves verbatim per spec G3).
            if (hit && slotValue.Seqno > tombstoneSeqno)
            {
                return (Resolved: true, Value: ResolveMemTableValue(slotValue), Found: true);
            }
            return (Resolved: true, Value: default!, Found: false);
        }
        if (hit)
        {
            return (Resolved: true, Value: ResolveMemTableValue(slotValue), Found: true);
        }
        return (Resolved: false, Value: default!, Found: false);
    }

    /// <inheritdoc/>
    public async Task RemoveAsync(TKey key, CancellationToken cancellationToken = default)
    {
        using var operation = EnterIngestOperation();
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfFaulted();
        Guard.AgainstNullOrDefault(() => key);

        await EnsureInitialisedAsync(cancellationToken).ConfigureAwait(false);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ThrowIfFaulted();
            // Rotation gate FIRST — before the seqno is assigned and the WAL tombstone frame
            // is appended, for the same cross-slot-inversion reasons as SetInternalAsync:
            // with the gate first, seqno assignment, WAL append, and tombstone apply run
            // under one continuous lock hold, so apply order equals seqno order.
            await EnsureActiveHasCapacityAsync(cancellationToken).ConfigureAwait(false);

            var seqno = (ulong)Interlocked.Increment(ref _lastAssignedSequenceNumber);

            // durability for tombstones. Without this, a crash between the
            // in-memory tombstone mutation and the next flush silently resurrects the
            // deleted value from an older segment on the next read (DDIA 2e p. 121).
            // WAL-first ordering is safe because Delete is idempotent: a crash after
            // the WAL append but before the tombstone-set mutation replays to the same
            // recovered state.
            if (_wal != null)
            {
                await _wal.AppendDeleteAsync(key, seqno, cancellationToken).ConfigureAwait(false);
            }

            // Tombstones land on the active slot's tombstone map. Active-slot scope only;
            // the active's tree is unchanged (RedBlackTree is append-only — RemoveAsync is
            // unsupported by design). Tombstones count toward the seal threshold (see
            // ActiveSlotIsFull), so a tombstone-only burst rotates like a write burst.
            // Seqno-conditional as defense-in-depth: under the gate-first ordering no newer
            // tombstone can have applied between our assignment and this line, but the guard
            // is cheap and keeps the invariant local.
            if (!_active.Tombstones.TryGetValue(key, out var existingTombstoneSeqno)
                || existingTombstoneSeqno < seqno)
            {
                _active.Tombstones[key] = seqno;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        using var operation = EnterIngestOperation();
        await EnsureInitialisedAsync(cancellationToken).ConfigureAwait(false);

        // ClearAsync is permitted in the Faulted state — it is part of the operator-recovery
        // surface (along with Dispose). DrainAllInFlightAsync below would re-throw the captured
        // fault when awaiting the in-flight slots' TCSes; in the Faulted case we skip the drain
        // and discard directly. The engine remains Faulted after ClearAsync (Faulted is
        // terminal); operators recover fully via Dispose + reopen.
        var faulted = Volatile.Read(ref _faultedReason) is not null;
        if (!faulted)
        {
            // Drain in-flight flushes first; otherwise a flusher commit running concurrently
            // with the clear would resurrect a segment we just deleted.
            await DrainAllInFlightAsync(cancellationToken).ConfigureAwait(false);
        }

        // Test seam: parks the clear between the drain and the lock acquisition so the
        // racing-seal window can be exercised deterministically. Null in production.
        if (ClearDrainToLockHook is { } drainToLockHook)
        {
            await drainToLockHook().ConfigureAwait(false);
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ExceptionDispatchInfo? firstFailure = null;

            // A WAL-backed store holds acknowledged data in two places — the segments CURRENT names
            // and the WAL's unflushed frames — so emptying CURRENT and truncating the WAL are two
            // separate durable steps. A durable clear marker written first is the commit point:
            // recovery that finds it completes the clear (empty CURRENT, truncate the WAL) instead
            // of replaying the WAL, so a crash at any later step reopens fully cleared, and a crash
            // before it reopens intact. A marker that cannot be created aborts the clear with
            // nothing changed.
            var markerPath = Path.Combine(_segmentFolder, ClearPendingMarkerFileName);
            if (_wal != null)
            {
                WriteClearPendingMarker(markerPath, ref firstFailure);
            }

            // Empty CURRENT before anything is discarded — the same manifest-first order
            // compaction's commit follows. Every segment file is then a file CURRENT does not name,
            // which recovery sweeps as an orphan. So an interrupted delete loop, or a snapshot pin
            // that keeps a retired file on disk past a crash, leaves a directory the next open
            // accepts: deleting CURRENT instead would leave "segment files, no CURRENT", which
            // recovery must refuse as corruption. The manifest write fsyncs the folder.
            //
            // A write that failed before replacing CURRENT changed nothing durable: CURRENT still
            // names every segment, so the clear stops here — unlinking a file CURRENT names would
            // leave a store the next open refuses — with the memtable, segments and WAL intact. A
            // write whose replace landed but whose fsync failed leaves CURRENT empty, so the
            // remaining steps still run and the first failure is reported once they have.
            try
            {
                ClearManifestWriteHook?.Invoke();
                SegmentManifest.WriteEntries(_segmentFolder, []);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (ManifestStillNamesSegments())
                {
                    if (_wal != null && !RollBackClearPendingMarker(markerPath))
                    {
                        // The marker survives (or its removal is not durable), so the next open
                        // completes this clear: writes accepted after it would be discarded then.
                        // Fail closed instead.
                        EnterClearFaultedState(ex);
                    }

                    throw;
                }

                firstFailure ??= ExceptionDispatchInfo.Capture(ex);
            }

            // Discard any immutables remaining (in the non-faulted case, racing seals between
            // DrainAllInFlightAsync and lock-reacquire; in the faulted case, every queued slot).
            // For each: cancel the Persisted TCS so any FlushAsync awaiter unblocks
            // deterministically with TaskCanceledException, and release its gate slot.
            foreach (var slot in _immutables)
            {
                slot.State = MemtableSlotState.Persisted;
                slot.Persisted.TrySetCanceled(cancellationToken);
                _ingestGate.Release();
            }
            _immutables.Clear();

            // From here on the clear is discarding acknowledged state, so it no longer honours the
            // caller's token: a cancellation landing between two discard steps would escape with
            // the queued slots gone, some segment files unlinked yet still listed in memory, and the
            // WAL untruncated — a half-cleared store whose reads hit missing files. Cancellation is
            // observed only up to the lock acquisition above, before anything is discarded.
            await _active.Tree.ClearAsync(CancellationToken.None).ConfigureAwait(false);
            _active = AllocateActiveSlot();

            // Retire the segment readers honouring live-snapshot pins — the same contract
            // compaction obeys. A reader pinned by a live snapshot must NOT be disposed here;
            // disposing it would surface as ObjectDisposedException on the snapshot's next read.
            // Pinned readers are parked in _retiredPinnedReaders and their files in
            // _retiredPinnedFiles; the last pinning snapshot's dispose (or a later sweep) deletes
            // them. Unpinned readers are disposed immediately.
            //
            // A failed segment delete (the unlink, or the directory fsync that makes it durable)
            // must not abort the clear here: the files already unlinked would stay listed in the
            // in-memory segment set, so reads on this engine would hit missing files. The clear
            // finishes every step, then reports the first failure.
            var pinnedSetForClear = _snapshotRegistry.CollectAllPinned();
            for (var i = 0; i < _segments.Count; i++)
            {
                var fileName = _segmentFileNames[i];
                var reader = _segments[i];
                if (pinnedSetForClear.Contains(fileName))
                {
                    if (_retiredPinnedFiles.Add(fileName))
                    {
                        Interlocked.Increment(ref _retiredPinnedFileCount);
                    }

                    _retiredPinnedReaders[fileName] = reader;
                    continue;
                }

                try
                {
                    await reader.ClearAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    firstFailure ??= ExceptionDispatchInfo.Capture(ex);
                }
            }

            _segments.Clear();
            _segmentFileNames.Clear();
            _segmentIntegrity.Clear();
            _segmentMaxSeqnos.Clear();
            _segmentLevels.Clear();
            _segmentTombstones.Clear();
            _segmentTombstoneSeqnos.Clear();

            // _segmentCounter is deliberately NOT reset. Segment identity is the filename, and
            // the pre-clear names outlive this call: a live snapshot's pin and its parked reader
            // (_retiredPinnedReaders) and an in-flight compaction plan all still refer to them.
            // Reusing a name would let a post-clear flush atomically replace a pinned file (the
            // snapshot then reads foreign bytes), let the pin's release delete the post-clear
            // segment, and let a racing compaction commit match the reused names and swap the
            // cleared data back in. Monotonic names keep every pre-clear reference pointing at
            // a file no post-clear writer can produce.
            Interlocked.Exchange(ref _lastAssignedSequenceNumber, 0L);

            // Drop cached blocks of the cleared segments to release their memory. Correctness
            // does not depend on it: the SST format keys the cache by a process-wide identity per
            // open table instance, so no later segment, here or after a reopen, can collide with
            // these entries.
            _blockCache?.Clear();

            // The clear marker is removed only once the WAL is truncated: until then recovery must
            // still discard the WAL rather than replay it.
            var clearPendingMarkerRemains = false;
            if (_wal != null)
            {
                try
                {
                    await _wal.TruncateAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    firstFailure ??= ExceptionDispatchInfo.Capture(ex);
                    clearPendingMarkerRemains = true;
                }

                if (!clearPendingMarkerRemains && DeleteClearPendingMarker(markerPath) is { } markerFailure)
                {
                    firstFailure ??= ExceptionDispatchInfo.Capture(markerFailure);
                    clearPendingMarkerRemains = true;
                }
            }

            // Reclaim any retired-but-pinned files whose last pin was already released — mirrors
            // the post-compaction sweep so a Clear that follows a snapshot dispose does not leak.
            await SweepRetiredPinnedFilesUnderLockAsync(CancellationToken.None).ConfigureAwait(false);

            // Make the segment deletions (and the marker's removal) durable before reporting
            // success, so a crash cannot resurrect any of them after the clear was acknowledged. A
            // failure here is reported: the clear did not become durable. Windows skips this
            // barrier; that path does not establish directory persistence across power loss.
            var folderFsyncFailed = false;
            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    DurableFile.FsyncDirectory(Path.Combine(_segmentFolder, SegmentManifest.FileName));
                }
                catch (IOException ex)
                {
                    // An earlier failure, if any, is the one reported below.
                    firstFailure ??= ExceptionDispatchInfo.Capture(ex);
                    folderFsyncFailed = true;
                }
            }

            // A marker left on disk — or one whose removal may not be durable — makes the next open
            // complete this clear, discarding any write accepted after it. Fail closed instead: the
            // engine accepts no further writes, and a reopen finishes the clear.
            if (_wal != null && (clearPendingMarkerRemains || folderFsyncFailed))
            {
                EnterClearFaultedState(firstFailure!.SourceException);
            }

            firstFailure?.Throw();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Name of the durable marker <see cref="ClearAsync"/> writes into the segment folder before a
    /// WAL-backed clear discards anything. Its presence at open means a clear committed but may
    /// not have finished: recovery completes it rather than replaying the WAL.
    /// </summary>
    internal const string ClearPendingMarkerFileName = "CLEAR-PENDING";

    /// <summary>
    /// Creates the clear marker and makes it durable. A failure to create, fsync or close it
    /// propagates — the clear has changed nothing yet — but the marker's directory entry can
    /// already exist by then, and a marker left on disk makes the next open complete a clear that
    /// never discarded anything. It is therefore rolled back first; when the rollback fails too,
    /// the engine faults, because any write accepted afterwards would be discarded by that open.
    /// A failing folder fsync is recorded and the clear proceeds, the same disposition as the
    /// folder fsync of the manifest write that follows.
    /// </summary>
    private void WriteClearPendingMarker(string markerPath, ref ExceptionDispatchInfo? firstFailure)
    {
        try
        {
            using var marker = new FileStream(markerPath, FileMode.Create, FileAccess.Write, FileShare.None);
            marker.Flush(flushToDisk: true);
        }
        catch (Exception ex)
        {
            if (!RollBackClearPendingMarker(markerPath))
            {
                EnterClearFaultedState(ex);
            }

            throw;
        }

        if (!OperatingSystem.IsWindows())
        {
            try
            {
                DurableFile.FsyncDirectory(markerPath);
            }
            catch (IOException ex)
            {
                firstFailure ??= ExceptionDispatchInfo.Capture(ex);
            }
        }
    }

    /// <summary>Removes the clear marker; returns the failure when it could not be removed.</summary>
    private static Exception? DeleteClearPendingMarker(string markerPath)
    {
        try
        {
            File.Delete(markerPath);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex;
        }
    }

    /// <summary>
    /// Undoes the clear marker of a clear that stopped before discarding anything: removes it and
    /// makes the removal durable. <see langword="false"/> when either step failed.
    /// </summary>
    private static bool RollBackClearPendingMarker(string markerPath)
    {
        if (DeleteClearPendingMarker(markerPath) is not null)
        {
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            return true;
        }

        try
        {
            DurableFile.FsyncDirectory(markerPath);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads <c>CURRENT</c> back after a failed manifest write: <see langword="true"/> when it still
    /// names a segment (the replace never landed) or cannot be read, so the clear must not discard
    /// anything.
    /// </summary>
    private bool ManifestStillNamesSegments()
    {
        try
        {
            return SegmentManifest.ReadEntries(_segmentFolder) is { Count: > 0 };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return true;
        }
    }

    /// <summary>
    /// Enters the terminal Faulted state after a WAL-backed clear committed (its marker is durable)
    /// but could not finish: the marker makes the next open complete the clear, so the engine
    /// must accept no write that open would then discard. Caller holds <see cref="_lock"/>.
    /// </summary>
    private void EnterClearFaultedState(Exception cause)
    {
        var reason = new IOException(
            "ClearAsync committed but could not finish discarding the store; reopening the engine completes the clear.",
            cause);
        Interlocked.CompareExchange(ref _faultedReason, ExceptionDispatchInfo.Capture(reason), null);
        _terminalStateCts.Cancel();
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<(TKey Key, TValue Value)>> GetAllItemsAsync(CancellationToken cancellationToken = default)
    {
        using var operation = EnterIngestOperation();
        await EnsureInitialisedAsync(cancellationToken).ConfigureAwait(false);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await GetAllItemsInternalAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Returns all key-value pairs within the inclusive key range [startKey, endKey],
    /// merging results from the MemTable and all on-disk segments with last-writer-wins
    /// deduplication. Tombstoned keys are excluded.
    /// </summary>
    /// <param name="startKey">The inclusive lower bound of the range.</param>
    /// <param name="endKey">The inclusive upper bound of the range.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>Sorted key-value pairs within the range.</returns>
    /// <remarks>
    /// Streams the <see cref="Algorithms.KWayMerge"/> and stops as soon as a key exceeds
    /// <paramref name="endKey"/> (sources are sorted, so no later source can re-enter the
    /// range). SST segments implement <c>ISstSegmentSeekable</c>, so each segment stream is
    /// positioned at the first data block that may contain <paramref name="startKey"/> rather
    /// than scanning from block 0 — whole prior blocks are skipped via the sparse block index
    /// (O(log B) seek where B = number of blocks). The MemTable scan is left as-is: it is
    /// capacity-bounded and in-memory, so its O(n) start-scan is negligible.
    /// </remarks>
    public async IAsyncEnumerable<(TKey Key, TValue Value)> GetRangeAsync(
        TKey startKey, TKey endKey,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Guard.AgainstNullOrDefault(() => startKey);
        Guard.AgainstNullOrDefault(() => endKey);

        // Fail fast on an inverted range. An empty result for startKey > endKey would mask a
        // caller error (a swapped bound returns nothing, indistinguishable from a genuinely empty
        // range), so reject it with ArgumentException instead (finding F-39). The bounds are
        // inclusive, so startKey == endKey is a valid single-key range and is allowed.
        if (startKey.CompareTo(endKey) > 0)
        {
            throw new ArgumentException(
                $"startKey must be less than or equal to endKey (got startKey > endKey).", nameof(startKey));
        }

        // Buffer the range under the lock — MergedStreamAsync holds the lock for the duration of
        // the merge. Yielding across lock boundaries is unsound (a writer could mutate state
        // between yields), so we collect all matching entries while holding the lock, then yield
        // the buffered results after releasing it.
        // Pass startKey so MergedStreamAsync can use block-seek on SST sources: each segment's
        // stream is positioned at the first block that may contain startKey rather than scanning
        // from block 0. MemTable scan is left as-is (capacity-bounded, in-memory — negligible).
        // The disposal lease covers only the buffering phase: it is released before the first
        // yield, so a consumer that abandons the enumeration cannot hold Dispose open.
        var buffer = new List<(TKey Key, TValue Value)>();
        using (EnterIngestOperation())
        {
            await EnsureInitialisedAsync(cancellationToken).ConfigureAwait(false);
            await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                await foreach (var kv in MergedStreamAsync(cancellationToken, startKey, hasStartKey: true).ConfigureAwait(false))
                {
                    if (kv.Key.CompareTo(startKey) < 0)
                    {
                        continue;
                    }
                    if (kv.Key.CompareTo(endKey) > 0)
                    {
                        // Sources are sorted ascending and KWayMerge preserves that order; once we
                        // pass endKey no further source can re-enter the range, so stop the scan.
                        break;
                    }
                    buffer.Add((kv.Key, kv.Value));
                }
            }
            finally
            {
                _lock.Release();
            }
        }

        foreach (var item in buffer)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return item;
        }
    }

    /// <summary>
    /// Atomic snapshot of the memtable range + segment readers + combined tombstones, for the
    /// <c>LsmTimeSeriesStorageEngine&lt;TDatasetKey, TRecord&gt;</c> columnar k-way-merge path.
    /// Taken under <see cref="_lock"/> so the memtable snapshot and the segment-reader list
    /// are consistent with each other — no concurrent flush can insert a new segment whose
    /// data is ALSO in the memtable snapshot.
    /// </summary>
    /// <remarks>
    /// <para><b>Why a single method instead of three accessors.</b> Exposing
    /// <c>StreamMemtableRangeAsync</c> + <c>GetSegmentReaders</c> + <c>GetAllTombstones</c>
    /// separately lets a caller observe an intermediate state where one list has been
    /// re-read after the engine already flushed the memtable into a new segment; the caller
    /// would then process both copies and double-count rows. Snapshotting under a single lock
    /// hold keeps the decorator's merge tombstone-consistent.</para>
    /// <para><b>Pointer dereference.</b> Memtable entries whose <see cref="ValueKind"/> is
    /// <see cref="ValueKind.ValueLogPointer"/> are resolved eagerly inside the lock via
    /// <see cref="ResolveMemTableValue"/>, so the decorator never re-enters the value-log
    /// reader.</para>
    /// <para><b>Segment pin.</b> The captured segment files are registered with the snapshot
    /// registry, exactly as <see cref="AcquireSnapshotAsync"/> pins them, so a compaction or
    /// <see cref="ClearAsync"/> that retires one parks its reader until the caller disposes the
    /// returned snapshot. The caller must dispose it once it has finished reading the segments.</para>
    /// <para><b>Visibility.</b> Internal — the decorator lives in the same assembly. External
    /// consumers should continue to use the public <c>GetRangeAsync</c> surface.</para>
    /// </remarks>
    internal async Task<ProjectedRangeSnapshot<TKey, TValue>> AcquireProjectedRangeSnapshotAsync(
        TKey startKey,
        TKey endKey,
        CancellationToken cancellationToken = default)
    {
        using var operation = EnterIngestOperation();
        Guard.AgainstNullOrDefault(() => startKey);
        Guard.AgainstNullOrDefault(() => endKey);

        // Same lazy-recovery entry step as every public read: a constructor-built engine replays
        // its WAL on first use, and this internal read is the first use when the columnar
        // decorator serves a column-subset range read.
        await EnsureInitialisedAsync(cancellationToken).ConfigureAwait(false);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // Walk active first (newest), then immutables newest-first so a more-recent
            // overwrite in the active wins over an older immutable's value at the same key.
            // Each surviving entry carries the winning slot's seqno so the consumer can apply
            // tombstones with newest-wins semantics (mirrors MergedStreamAsync; finding #1).
            var memtableEntries = new List<MemtableSnapshotEntry<TKey, TValue>>();
            var seen = new HashSet<TKey>();

            async Task ProjectSlotAsync(MemtableSlot<TKey, TValue> slot)
            {
                var slotItems = await slot.Tree.GetAllItemsAsync(cancellationToken).ConfigureAwait(false);
                foreach (var (k, v) in slotItems)
                {
                    if (k.CompareTo(startKey) < 0 || k.CompareTo(endKey) > 0)
                    {
                        continue;
                    }
                    if (seen.Add(k))
                    {
                        memtableEntries.Add(new MemtableSnapshotEntry<TKey, TValue>(
                            k, ResolveMemTableValue(v), v.Seqno));
                    }
                }
            }

            await ProjectSlotAsync(_active).ConfigureAwait(false);
            for (var i = _immutables.Count - 1; i >= 0; i--)
            {
                await ProjectSlotAsync(_immutables[i]).ConfigureAwait(false);
            }

            // Max tombstone seqno per key across active + immutables + segments. Seqno-keyed so
            // the consumer suppresses a key only when the tombstone is newer than a live re-insert.
            var tombstones = new Dictionary<TKey, ulong>();
            void RecordTombstone(TKey key, ulong seqno)
            {
                if (!tombstones.TryGetValue(key, out var existing) || seqno > existing)
                {
                    tombstones[key] = seqno;
                }
            }
            foreach (var (key, seqno) in _active.Tombstones)
            {
                RecordTombstone(key, seqno);
            }
            foreach (var slot in _immutables)
            {
                foreach (var (key, seqno) in slot.Tombstones)
                {
                    RecordTombstone(key, seqno);
                }
            }
            foreach (var segSeqnos in _segmentTombstoneSeqnos)
            {
                foreach (var (key, seqno) in segSeqnos)
                {
                    RecordTombstone(key, seqno);
                }
            }

            var segments = new List<ISegmentReader<TKey, TValue>>(_segments);
            var segmentMaxSeqnos = new List<ulong>(_segmentMaxSeqnos);

            // The consumer streams these readers after _lock is released, so pin them the same way
            // AcquireSnapshotAsync does: a compaction or ClearAsync that retires a pinned segment
            // parks its reader instead of deleting the file mid-read (DDIA 2e p. 121 — reads keep
            // using the input segments until the merge's inputs are no longer needed).
            var pinId = Guid.NewGuid();
            var pinSeqno = (ulong)Interlocked.Read(ref _lastAssignedSequenceNumber);
            _snapshotRegistry.Register(
                pinId,
                new SnapshotState(pinSeqno, new HashSet<string>(_segmentFileNames, StringComparer.Ordinal)));

            return new ProjectedRangeSnapshot<TKey, TValue>(
                memtableEntries,
                tombstones,
                segments,
                segmentMaxSeqnos,
                () => OnSnapshotDisposedAsync(pinId));
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Internal implementation of GetAllItemsAsync. Must be called while holding _lock.
    /// replaces the previous <c>SortedDictionary</c> materialisation with a
    /// streaming k-way merge over all segments + the memtable.
    /// </summary>
    private async Task<List<(TKey Key, TValue Value)>> GetAllItemsInternalAsync(CancellationToken cancellationToken)
    {
        var result = new List<(TKey Key, TValue Value)>();
        await foreach (var kv in MergedStreamAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add((kv.Key, kv.Value));
        }
        return result;
    }

    /// <summary>
    /// Produces the merged, tombstone-filtered stream over segments (oldest→newest) + the
    /// MemTable (newest source, highest k-way index). Caller must hold <see cref="_lock"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Duplicate keys across sources collapse via <see cref="Algorithms.KWayMerge"/>'s
    /// last-writer-wins rule (highest source index wins), matching the previous
    /// <c>SortedDictionary</c> overwrite semantics.
    /// </para>
    /// <para>
    /// Tombstones are applied as a filter over the merged stream rather than up front so a
    /// live <c>Set</c> in the MemTable that shadows an older tombstone still surfaces. The
    /// filter set is O(|tombstones|), typically orders of magnitude smaller than the dataset.
    /// </para>
    /// </remarks>
    private static async IAsyncEnumerable<KeyValuePair<TKey, SegmentWriteEntry<TKey, TValue>>> MemtableSlotAsKeyedStreamAsync(
        MemtableSlot<TKey, TValue> slot,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var items = await slot.Tree.GetAllItemsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = item.Value.Kind == ValueKind.Inline
                ? SegmentWriteEntry<TKey, TValue>.Inline(item.Key, item.Value.InlineValue, item.Value.Seqno)
                : SegmentWriteEntry<TKey, TValue>.Pointer(item.Key, item.Value.Pointer.ToArray(), item.Value.Seqno);
            yield return new KeyValuePair<TKey, SegmentWriteEntry<TKey, TValue>>(item.Key, entry);
        }
    }

    private async IAsyncEnumerable<KeyValuePair<TKey, TValue>> MergedStreamAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken,
        TKey? startKey = default,
        bool hasStartKey = false)
    {
        // Collect the maximum tombstone seqno per key across every memtable slot + segment.
        // A tombstone suppresses a key only when it is *newer* than the winning data entry
        // for that key — exactly the rule TryProbeMemtableAsync enforces per-slot. A flat
        // membership set (no seqno) drops a live re-insert whose seqno is newer than an older
        // slot/segment tombstone (DDIA 2e p. 121 — a later write shadows an earlier tombstone).
        // _segmentTombstoneSeqnos is kept in lock-step with _segmentTombstones and carries the
        // seqnos; the per-slot Tombstones maps are already key→seqno.
        var tombstoneSeqnos = new Dictionary<TKey, ulong>();
        static void RecordTombstone(Dictionary<TKey, ulong> map, TKey key, ulong seqno)
        {
            if (!map.TryGetValue(key, out var existing) || seqno > existing)
            {
                map[key] = seqno;
            }
        }
        foreach (var (key, seqno) in _active.Tombstones)
        {
            RecordTombstone(tombstoneSeqnos, key, seqno);
        }
        foreach (var slot in _immutables)
        {
            foreach (var (key, seqno) in slot.Tombstones)
            {
                RecordTombstone(tombstoneSeqnos, key, seqno);
            }
        }
        foreach (var segSeqnos in _segmentTombstoneSeqnos)
        {
            foreach (var (key, seqno) in segSeqnos)
            {
                RecordTombstone(tombstoneSeqnos, key, seqno);
            }
        }

        // The k-way merge carries SegmentWriteEntry end-to-end so pointer entries never
        // trigger TValue.Deserialize inside the merge; dereference is deferred to the
        // per-entry yield step below, at the engine layer.
        var sources = new List<IAsyncEnumerable<KeyValuePair<TKey, SegmentWriteEntry<TKey, TValue>>>>(
            _segments.Count + _immutables.Count + 1);

        // Whether to use block-seek for SST sources. When startKey is provided and a segment
        // implements ISstSegmentSeekable, position its stream at the first block that may
        // contain startKey rather than scanning from block 0 (the dominant on-disk cost for
        // GetRangeAsync with a high startKey). Non-SST readers (columnar) fall back to the
        // full-scan path — they are in-memory-decoded anyway and the optimisation does not apply.
        //
        // The explicit flag — never a `startKey is not null` sentinel: TKey is an unconstrained
        // generic, so for a struct TKey `TKey?` is NOT Nullable<TKey>; `default` is the zero
        // struct and the null sentinel is always true. A sentinel here silently block-seeks
        // every no-startKey scan from default(TKey), dropping on-disk entries that sort below
        // the zero struct.
        var useSeek = hasStartKey;

        for (var i = 0; i < _segments.Count; i++)
        {
            // Stamp each segment's max seqno so seqno-less columnar entries compare correctly in
            // the tombstone filter below (finding C-3, ColumnProjection.All path). Row segments
            // carry real per-entry seqnos and are left untouched. _segmentMaxSeqnos is kept in
            // lock-step with _segments.
            var segmentMaxSeqno = i < _segmentMaxSeqnos.Count ? _segmentMaxSeqnos[i] : 0UL;

            IAsyncEnumerable<SegmentWriteEntry<TKey, TValue>> segStream;
            if (useSeek && _segments[i] is ISstSegmentSeekable<TKey, TValue> seekable)
            {
                // Block-seek: skip whole data blocks before startKey via the sparse index.
                // Entries in the starting block that are still < startKey are also skipped
                // inside EnumerateBlockEntriesFrom, so the k-way merge receives a stream
                // that is already positioned at startKey — no wasted decoding of pre-range keys.
                segStream = seekable.StreamBlockEntriesFromAsync(startKey!, cancellationToken: cancellationToken);
            }
            else
            {
                // Full-scan path: used for compaction, GetAllItemsAsync, and non-seekable sources.
                segStream = _segments[i].StreamAsync(cancellationToken);
            }
            sources.Add(ToKeyedSegmentStream(segStream, segmentMaxSeqno));
        }
        // Memtable sources land in oldest-first order: oldest immutable → … → newest immutable
        // → active. Last source index wins on duplicate keys (KWayMerge contract), so the
        // active beats every immutable, and a newer immutable beats an older one.
        // MemTable is capacity-bounded (small, in-memory) — the O(n) start-scan is negligible;
        // block-seek optimisation is not applied here (deliberate scope decision).
        for (var i = 0; i < _immutables.Count; i++)
        {
            sources.Add(MemtableSlotAsKeyedStreamAsync(_immutables[i], cancellationToken));
        }
        sources.Add(MemtableSlotAsKeyedStreamAsync(_active, cancellationToken));

        await foreach (var kv in KWayMerge.MergeAsync(sources, Comparer<TKey>.Default, cancellationToken)
            .ConfigureAwait(false))
        {
            // Suppress only when the newest tombstone for this key is strictly newer than the
            // winning data entry's seqno (the k-way merge already elected the newest data entry
            // per key via the highest-source-index tiebreaker, and source order = seqno order).
            if (tombstoneSeqnos.TryGetValue(kv.Key, out var tombstoneSeqno)
                && tombstoneSeqno > kv.Value.SequenceNumber)
            {
                continue;
            }
            var entry = kv.Value;
            var resolved = entry.Kind == ValueKind.Inline
                ? entry.InlineValue!
                : ReadFromValueLog(ValueLogPointer.ReadFrom(entry.PointerBytes.Span));
            yield return new KeyValuePair<TKey, TValue>(kv.Key, resolved);
        }
    }

    private static async IAsyncEnumerable<KeyValuePair<TKey, SegmentWriteEntry<TKey, TValue>>> ToKeyedSegmentStream(
        IAsyncEnumerable<SegmentWriteEntry<TKey, TValue>> source,
        ulong segmentMaxSeqno)
    {
        await foreach (var entry in source.ConfigureAwait(false))
        {
            // Columnar segments carry no per-entry sequence number, so every entry streams back with
            // SequenceNumber == 0. The seqno-aware tombstone filter (both the scan-path merge and the
            // compaction merge) would then suppress a re-insert flushed to a newer segment than an
            // older tombstone, because 0 is never greater than the tombstone's seqno (finding C-3). A
            // segment is exactly one ordered memtable flush, so its max seqno is a sound stamp for
            // every entry it holds. Row segments carry real per-entry seqnos (≥ 1) and pass through
            // untouched. Columnar formats drop the seqno again on the next write, so the stamp affects
            // only the in-merge comparison, never on-disk bytes.
            var stamped = entry.SequenceNumber == 0UL
                ? entry with { SequenceNumber = segmentMaxSeqno }
                : entry;
            yield return new KeyValuePair<TKey, SegmentWriteEntry<TKey, TValue>>(stamped.Key, stamped);
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// <b>Atomicity (all-or-nothing).</b> The batch is fully materialised and every key/value is
    /// validated (<see cref="Guard.AgainstNullOrDefault{T}"/>) BEFORE any item is applied to the
    /// active memtable. A null/default key or value anywhere in the batch throws
    /// <see cref="ArgumentException"/> with no item applied — the caller observes either the
    /// whole batch or none of it, never a partial prefix (finding F-24). Cancellation follows the
    /// same rule: the token is observed only until the first item's WAL frame is durable (the
    /// commit point), so a cancellation either leaves the batch unapplied or arrives after the
    /// first item committed and is then ignored while the rest of the batch is applied. The batch is
    /// applied under <see cref="_lock"/>, but the memtable rotation gate releases the lock while it
    /// waits for capacity, so a concurrent writer can interleave between two items of a batch that
    /// fills the memtable; each item still takes a fresh sequence number in apply order.
    /// </para>
    /// <para>
    /// <b>Faulted-state contract.</b> Like <see cref="SetAsync"/>, a bulk write against a
    /// faulted engine throws (the captured flush failure is re-surfaced via
    /// <c>ThrowIfFaulted</c>) before any work is attempted (finding F-25).
    /// </para>
    /// <para>
    /// <b>Note on durability vs. memtable atomicity.</b> "No partial apply" is a memtable-state
    /// guarantee: a validation failure leaves the in-memory store unchanged. A mid-apply I/O
    /// fault (WAL/value-log) still faults the engine per the single-write durability path; the
    /// validate-first phase removes the common partial-apply cause — a malformed item discovered
    /// only after earlier items were already committed.
    /// </para>
    /// </remarks>
    public async Task SetBulkAsync(IEnumerable<KeyValuePair<TKey, TValue>> items, CancellationToken cancellationToken = default)
    {
        using var operation = EnterIngestOperation();
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfFaulted();
        Guard.AgainstNullOrDefault(() => items);

        // Materialise once, into a list this call owns, so the validate pass and the apply pass see
        // the same items and validation completes fully before any apply begins. A caller's list is
        // copied too: a live collection another thread appends to yields a different sequence on
        // each enumeration, and an entry that appears only in the apply pass would bypass admission.
        var batch = items.ToList();

        // Validate-before-apply: reject the WHOLE batch on the first null/default key or value,
        // before mutating any state. This is the all-or-nothing guarantee (DR-4 — true
        // buffer+validate, not documented partial-apply).
        foreach (var item in batch)
        {
            var key = item.Key;
            var value = item.Value;
            Guard.AgainstNullOrDefault(() => key);
            Guard.AgainstNullOrDefault(() => value);
            ValidateEntryAdmission(key, value);
        }

        await EnsureInitialisedAsync(cancellationToken).ConfigureAwait(false);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ThrowIfFaulted();
            // Route every (already-validated) bulk entry through the same threshold-driven
            // dispatch as SetAsync. The caller's token is honoured only until the first item's WAL
            // frame commits: past that point recovery will reinstall that item, so stopping on a
            // later cancellation would leave a durable, visible partial prefix of the batch.
            var itemToken = cancellationToken;
            foreach (var item in batch)
            {
                await SetInternalAsync(item.Key, item.Value, itemToken).ConfigureAwait(false);
                itemToken = CancellationToken.None;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// <b>Drain semantics.</b> Awaits every currently in-flight memtable, plus
    /// the active if it carries any data. On return, every write made up to the
    /// <c>FlushAsync</c> entry is durable in an SSTable on disk and the WAL has been
    /// truncated. Writes made by other callers DURING the await are not promised to be
    /// drained — <c>FlushAsync</c> draws a snapshot at entry, not at exit.
    /// </para>
    /// <para>
    /// <b>No-op on idle.</b> A fresh engine with no writes returns without sealing or
    /// signalling the flusher.
    /// </para>
    /// </remarks>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        using var operation = EnterIngestOperation();
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfFaulted();

        List<Task> waiters;
        Task? newlySealedWaiter = null;

        await EnsureInitialisedAsync(cancellationToken).ConfigureAwait(false);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ThrowIfFaulted();
            // Snapshot existing in-flight slots — every queued immutable's Persisted task is
            // a waiter we need to await.
            waiters = _immutables.Select(s => (Task)s.Persisted.Task).ToList();

            // If the active has any data (tree entries OR tombstones), seal it too. Sealing
            // requires an _ingestGate slot; mirror EnsureActiveHasCapacityAsync's release-
            // and-reacquire pattern so the flusher can commit while we wait.
            var activeHasTombstones = _active.Tombstones.Count > 0;
            var activeHasData = _active.Tree.Count > 0;
            if (activeHasData || activeHasTombstones)
            {
                _lock.Release();
                var lockHeld = false;
                var gateAcquired = false;
                try
                {
                    try
                    {
                        await WaitForIngestPermitAsync(cancellationToken).ConfigureAwait(false);
                        gateAcquired = true;
                    }
                    finally
                    {
                        await _lock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                        lockHeld = true;
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    ThrowIfFaulted();

                    // Re-check: another caller may have sealed during our wait (or the tree
                    // may have been cleared by a concurrent ClearAsync — guarded above by
                    // ThrowIfFaulted/disposal checks).
                    var stillHasTombstones = _active.Tombstones.Count > 0;
                    var stillHasData = _active.Tree.Count > 0;
                    if (stillHasData || stillHasTombstones)
                    {
                        SealActiveLocked();
                        newlySealedWaiter = _immutables[^1].Persisted.Task;
                    }
                    else if (gateAcquired)
                    {
                        _ingestGate.Release();
                    }
                }
                catch
                {
                    if (!lockHeld)
                    {
                        // Cancellation raced with the inner WaitAsync — ensure we leave the
                        // lock in a sensible state for the outer finally.
                        await _lock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    if (gateAcquired && newlySealedWaiter is null)
                    {
                        _ingestGate.Release();
                    }
                    throw;
                }
            }
        }
        finally
        {
            _lock.Release();
        }

        if (newlySealedWaiter is not null)
        {
            waiters.Add(newlySealedWaiter);
        }

        await Task.WhenAll(waiters).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<ISnapshot<TKey, TValue>> AcquireSnapshotAsync(CancellationToken cancellationToken = default)
    {
        using var operation = EnterIngestOperation();
        await EnsureInitialisedAsync(cancellationToken).ConfigureAwait(false);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // atomic capture of the engine's state:
            // the seqno watermark, the segment set, per-segment tombstones + seqnos, and
            // an immutable MemTable copy with per-key seqnos. Every write up to this point
            // is visible; every write after returns is invisible.
            var seqno = (ulong)Interlocked.Read(ref _lastAssignedSequenceNumber);

            var pinnedSegments = new List<ISegmentReader<TKey, TValue>>(_segments);
            var pinnedFileNames = new List<string>(_segmentFileNames);
            var pinnedTombstones = new List<IReadOnlyDictionary<TKey, ulong>>(_segmentTombstoneSeqnos.Count);
            foreach (var map in _segmentTombstoneSeqnos)
            {
                // Copy each map; the engine may mutate the underlying dictionary later on
                // tombstones-only flushes attaching to the newest segment.
                pinnedTombstones.Add(new Dictionary<TKey, ulong>(map));
            }

            // MemTable snapshot — enumerate every memtable slot (active first, then
            // immutables newest-first) and pair with per-key seqno. Discriminated slots are
            // resolved to their underlying <typeparamref name="TValue"/> at capture time so
            // the snapshot's read path never re-enters the value-log reader. Newest-first
            // ordering lets TryAdd preserve the latest version of each key.
            var memSnapshot = new Dictionary<TKey, (TValue Value, ulong Seqno)>();
            var memTombstones = new Dictionary<TKey, ulong>();

            async Task MergeSlotAsync(MemtableSlot<TKey, TValue> slot)
            {
                foreach (var (k, ts) in slot.Tombstones)
                {
                    memTombstones.TryAdd(k, ts);
                }
                var slotItems = await slot.Tree.GetAllItemsAsync(cancellationToken).ConfigureAwait(false);
                foreach (var (k, v) in slotItems)
                {
                    if (!memSnapshot.ContainsKey(k))
                    {
                        memSnapshot[k] = (ResolveMemTableValue(v), v.Seqno);
                    }
                }
            }

            await MergeSlotAsync(_active).ConfigureAwait(false);
            for (var i = _immutables.Count - 1; i >= 0; i--)
            {
                await MergeSlotAsync(_immutables[i]).ConfigureAwait(false);
            }

            var id = Guid.NewGuid();
            var pinnedNameSet = new HashSet<string>(pinnedFileNames, StringComparer.Ordinal);
            _snapshotRegistry.Register(id, new SnapshotState(seqno, pinnedNameSet));

            return new LsmSnapshot<TKey, TValue>(
                this,
                id,
                seqno,
                pinnedSegments,
                pinnedFileNames,
                pinnedTombstones,
                memSnapshot,
                memTombstones);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Called by <see cref="LsmSnapshot{TKey, TValue}.DisposeAsync"/> to release the snapshot's
    /// segment pins. Runs a best-effort reclaim of any retired-but-pinned segment files whose
    /// last pin this disposal released.
    /// </summary>
    internal async ValueTask OnSnapshotDisposedAsync(Guid snapshotId)
    {
        // Releasing a pin on a disposed or disposing engine is a no-op: teardown releases every
        // retired reader itself.
        if (!TryEnterIngestOperation(out var operation))
        {
            return;
        }

        using var lease = operation;
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed || !_snapshotRegistry.Unregister(snapshotId))
            {
                return;
            }

            await SweepRetiredPinnedFilesUnderLockAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Runs the three-step background-compaction pipeline (DDIA 2e p. 121 — reads continue to
    /// be served from the input segments while the merge runs): pick + filename reservation
    /// under <see cref="_lock"/>, the O(dataset) merge + segment write WITHOUT the lock, then
    /// revalidate + swap under the lock again — the same shape as the flusher's 3-step
    /// protocol. Serialized against the flusher's auto-compact tick by
    /// <see cref="_compactionGate"/>. A failure after the segment write leaves at most one
    /// orphan output file (the manifest never flipped); the startup orphan sweep removes it.
    /// </remarks>
    public async Task CompactAsync(CancellationToken cancellationToken = default)
    {
        using var operation = EnterIngestOperation();
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureInitialisedAsync(cancellationToken).ConfigureAwait(false);

        await _compactionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            CompactionPlan? plan;
            await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                plan = PickCompactionPlanLocked(forceFullCompaction: true, prebuiltMetadata: null);
            }
            finally
            {
                _lock.Release();
            }

            if (plan is null)
            {
                return;
            }

            var (segment, crc, size) = await MergeCompactionAsync(plan, cancellationToken).ConfigureAwait(false);
            await CommitCompactionAsync(plan, segment, crc, size, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _compactionGate.Release();
        }
    }

    /// <summary>
    /// Acquires <see cref="_lock"/> and runs <see cref="CommitCompactionLockedAsync"/>. The merge
    /// output is already durable on disk but listed nowhere, so if the lock wait itself fails
    /// (cancellation, teardown) the output is deleted here: nothing else in the process ever
    /// learns its name, and leaving it would park a dataset-sized orphan on disk until the next
    /// open's sweep.
    /// </summary>
    private async Task CommitCompactionAsync(
        CompactionPlan plan,
        ISegmentReader<TKey, TValue> compactedSegment,
        uint compactedCrc,
        long compactedSize,
        CancellationToken cancellationToken)
    {
        try
        {
            await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await DiscardUnlistedSegmentAsync(compactedSegment).ConfigureAwait(false);
            throw;
        }

        try
        {
            await CommitCompactionLockedAsync(plan, compactedSegment, compactedCrc, compactedSize, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Deletes a fully written segment that no manifest lists (its commit was abandoned).
    /// Best-effort: when the delete fails, a later open's orphan sweep is the backstop.
    /// </summary>
    private static async Task DiscardUnlistedSegmentAsync(ISegmentReader<TKey, TValue> segment)
    {
        try
        {
            await segment.ClearAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // Never listed in the manifest, so the orphan cannot be served as live data.
        }
    }

    /// <summary>
    /// Deletes whatever a failed segment write left at <paramref name="segmentFileName"/> — no
    /// manifest lists it. Best-effort, with the same backstop as
    /// <see cref="DiscardUnlistedSegmentAsync"/>.
    /// </summary>
    private void DeleteUnlistedSegmentFile(string segmentFileName)
    {
        try
        {
            File.Delete(Path.Combine(_segmentFolder, segmentFileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Never listed in the manifest, so the orphan cannot be served as live data.
        }
    }

    /// <summary>
    /// Everything the unlocked merge step needs, captured under <see cref="_lock"/> by
    /// <see cref="PickCompactionPlanLocked"/>. Picked readers/tombstone maps are references to
    /// immutable state: segments never mutate, and under the single-compactor gate nothing
    /// reorders or replaces the picked entries during the merge window — the flusher only
    /// APPENDS new segments, which the commit step re-derives around by filename.
    /// </summary>
    private sealed record CompactionPlan(
        IReadOnlyList<string> PickedFileNames,
        IReadOnlyList<ISegmentReader<TKey, TValue>> PickedReaders,
        IReadOnlyList<ulong> PickedMaxSeqnos,
        ulong MaxPickedSeqno,
        int NewLevel,
        Dictionary<TKey, ulong>? TombstoneFilter,
        HashSet<TKey> NewTombstones,
        Dictionary<TKey, ulong> NewTombstoneSeqnos,
        string CompactedFileName);

    /// <summary>
    /// Pick step. Must be called while holding <see cref="_lock"/>. Selects the segments to
    /// merge, snapshots every input the unlocked merge needs, and reserves the output filename
    /// from <see cref="_segmentCounter"/> (flusher discipline — deriving the name outside the
    /// lock would let a concurrent flush pick the identical name and silently clobber it).
    /// Returns <see langword="null"/> when there is nothing to compact.
    /// </summary>
    private CompactionPlan? PickCompactionPlanLocked(
        bool forceFullCompaction,
        IReadOnlyList<SegmentMetadata<TKey>>? prebuiltMetadata)
    {
        // Nothing to compact if fewer than 2 segments.
        if (_segments.Count < 2)
        {
            return null;
        }

        // compute the set of picked indexes. When a strategy is registered, its
        // SelectSegments method consumes the full metadata list and returns a subset; otherwise
        // we fall back to full compaction (the pre-B3 default).
        // forceFullCompaction bypasses the strategy so explicit CompactAsync always merges everything.
        // prebuiltMetadata lets the flusher's auto-compact pass its already-built snapshot through
        // so ShouldCompact + SelectSegments share one BuildSegmentMetadataList call per cycle.
        var pickedIndexes = PickCompactionIndexes(forceFullCompaction, prebuiltMetadata);

        // A SINGLE-segment strategy pick is a level-move request (DDIA 2e leveled compaction:
        // merge one or more level-i SSTables into level i+1; the leveled strategy picks
        // exactly one source segment when the next level is empty) and must execute as a
        // rewrite at level+1 — refusing it would cap the leveled pyramid at L1 forever while
        // ShouldCompact keeps firing. Only the forced-full path treats a single segment as a
        // no-op: explicitly compacting a one-segment store is a pointless self-rewrite.
        if (pickedIndexes.Count == 0 || (forceFullCompaction && pickedIndexes.Count < 2))
        {
            return null;
        }

        // Tombstones are stripped per-key only when the picked set covers a contiguous prefix
        // [0..k] of segment indexes — otherwise an unpicked older segment may still carry a
        // shadowed value for the tombstoned key, and the delete would silently resurrect on the
        // next read (DDIA 2e p. 124; finding #2). Gating on Contains(0) alone is unsafe because
        // size-tiered and leveled strategies routinely pick non-contiguous sets such as {0, 2}.
        var pickedSet = new HashSet<int>(pickedIndexes);
        var canStripBottom = IsContiguousFromBottom(pickedSet);

        // compaction strips a tombstone only when BOTH (a) the picked set includes the
        // bottommost segment so no older shadow can resurrect, AND (b) no live snapshot can
        // still observe the delete. Snapshotting the filter AT PICK TIME is safe for the
        // unlocked merge: a tombstone that arrives during the merge is strictly newer than
        // every entry in the picked segments, so missing it from the filter only leaves an
        // entry in the compacted output that the memtable/newer segments still mask at read
        // time (positional LWW); a later compaction strips it. A snapshot acquired during the
        // merge has a begin seqno at or above every seqno in every pre-existing segment and
        // pins the OLD files by name, so pick-time strip decisions stay valid too.
        var minSnapSeqno = _snapshotRegistry.GetMinSequenceNumber();
        var tombstoneFilter = canStripBottom
            ? BuildCompactionTombstoneFilter(pickedSet, minSnapSeqno)
            : null;

        var pickedFileNames = new List<string>(pickedIndexes.Count);
        var pickedReaders = new List<ISegmentReader<TKey, TValue>>(pickedIndexes.Count);
        var pickedMaxSeqnos = new List<ulong>(pickedIndexes.Count);
        var maxPickedSeqno = 0UL;
        foreach (var idx in pickedIndexes)
        {
            pickedFileNames.Add(_segmentFileNames[idx]);
            pickedReaders.Add(_segments[idx]);
            // Same C-3 stamp as the scan-path merge: a seqno-less columnar entry is stamped
            // with its segment's max seqno so the tombstone filter keeps a re-insert that
            // out-versions an older tombstone instead of dropping it.
            var segMaxSeqno = idx < _segmentMaxSeqnos.Count ? _segmentMaxSeqnos[idx] : 0UL;
            pickedMaxSeqnos.Add(segMaxSeqno);
            if (segMaxSeqno > maxPickedSeqno)
            {
                maxPickedSeqno = segMaxSeqno;
            }
        }

        // Determine the new segment's level. If every picked segment sits at the same level L
        // we promote to L+1 (LevelDB convention — a compaction always moves data down a level
        // unless it bridges two levels). If picked spans multiple levels the output level is the
        // highest one of the picked set (the picked L_(i+1) siblings were rewritten in place).
        // Levels of picked segments cannot change during the merge window: only compaction
        // assigns levels and the single-compactor gate is held.
        var pickedLevels = pickedIndexes.Select(i => _segmentLevels[i]).ToList();
        var maxLevel = pickedLevels.Max();
        var minLevel = pickedLevels.Min();
        // At the last supported level a rewrite stays in place; publishing a higher level
        // would create a manifest this engine's own recovery refuses to read.
        var newLevel = minLevel == maxLevel ? Math.Min(maxLevel + 1, SegmentManifest.MaxLevel) : maxLevel;

        // Compute new segment's tombstone slot + parallel seqno map. Strip tombstones only
        // when canStripBottom AND their seqno is below every live snapshot; otherwise preserve
        // them + their seqnos so the new compacted segment masks older unpicked segments AND a
        // live snapshot that can observe the delete still sees both the tombstone and the
        // pre-delete value-versions (the pinned pre-compaction segment files on disk carry
        // those older versions).
        var newTombstones = new HashSet<TKey>();
        var newTombstoneSeqnos = new Dictionary<TKey, ulong>();
        foreach (var idx in pickedIndexes)
        {
            var idxTombstones = _segmentTombstones[idx];
            var idxTombstoneSeqnos = _segmentTombstoneSeqnos[idx];
            foreach (var key in idxTombstones)
            {
                idxTombstoneSeqnos.TryGetValue(key, out var tSeqno);
                var stripThis = canStripBottom && tSeqno < minSnapSeqno;
                if (stripThis)
                {
                    continue;
                }

                newTombstones.Add(key);
                if (tSeqno != 0UL)
                {
                    // Preserve the highest-seen seqno for this key across picked segments.
                    if (!newTombstoneSeqnos.TryGetValue(key, out var existing) || tSeqno > existing)
                    {
                        newTombstoneSeqnos[key] = tSeqno;
                    }
                }
            }
        }

        // Reserve the output filename UNDER the lock — see the flusher's identical rationale.
        var compactedFileName = $"{_segmentPrefix}_{_segmentCounter:D6}{_segmentFormat.FileExtension}";
        _segmentCounter++;

        return new CompactionPlan(
            pickedFileNames,
            pickedReaders,
            pickedMaxSeqnos,
            maxPickedSeqno,
            newLevel,
            tombstoneFilter,
            newTombstones,
            newTombstoneSeqnos,
            compactedFileName);
    }

    /// <summary>
    /// Merge step. Runs WITHOUT <see cref="_lock"/> — this is the O(dataset) part, and reads,
    /// writes, flushes, and snapshot acquisition all proceed concurrently against the (still
    /// registered, immutable) input segments. Segment readers already support lock-free
    /// concurrent streams (live snapshots and the value-log GC scan do exactly this).
    /// </summary>
    private async Task<(ISegmentReader<TKey, TValue> Segment, uint Crc32C, long SizeBytes)> MergeCompactionAsync(
        CompactionPlan plan,
        CancellationToken cancellationToken)
    {
        // k-way merge across the picked segments (oldest→newest). The heap holds O(k)
        // cursors, but the surviving merged entries are accumulated in a List before the
        // segment write below, so peak memory is O(compacted output) — streaming the merge
        // straight into the segment writer would need an IAsyncEnumerable-accepting
        // ISegmentFormat.WriteAsync overload (accepted arena finding).
        var sources = new List<IAsyncEnumerable<KeyValuePair<TKey, SegmentWriteEntry<TKey, TValue>>>>(
            plan.PickedReaders.Count);
        for (var i = 0; i < plan.PickedReaders.Count; i++)
        {
            sources.Add(ToKeyedSegmentStream(
                plan.PickedReaders[i].StreamAsync(cancellationToken), plan.PickedMaxSeqnos[i]));
        }

        var compactedEntries = new List<SegmentWriteEntry<TKey, TValue>>();

        await foreach (var kv in KWayMerge.MergeAsync(sources, Comparer<TKey>.Default, cancellationToken)
            .ConfigureAwait(false))
        {
            // Seqno-aware suppression (matches MergedStreamAsync and the point-lookup path): a
            // strippable tombstone suppresses the surviving merge entry only when the tombstone is
            // STRICTLY NEWER than that entry. A re-insert flushed to a newer segment than the
            // tombstone (tombstone seqno < data seqno) wins and must survive (DDIA 2e p. 121). A
            // flat key-membership check dropped the live re-insert here — the inverse of the
            // scan-path resurrection bug.
            if (plan.TombstoneFilter is not null
                && plan.TombstoneFilter.TryGetValue(kv.Key, out var suppressingSeqno)
                && suppressingSeqno > kv.Value.SequenceNumber)
            {
                continue;
            }

            // kv.Value is a SegmentWriteEntry with the original seqno + kind preserved.
            // No TValue.Deserialize call is reachable here — pointer entries flow through
            // untouched (V-INT-4 / V-SST-2 invariant).
            compactedEntries.Add(kv.Value);
        }

        if (CompactionMergeHook is { } compactionMergeHook)
        {
            await compactionMergeHook().ConfigureAwait(false);
        }

        // Write the new compacted segment (payload durable). Compaction's tombstone-GC step
        // (DDIA Ch. 4 p. 124) drops surviving tombstones older than the engine's
        // tombstone-keep watermark; every survivor is persisted into the new segment's footer
        // so the deletion seqno round-trips with the segment file.
        var compactedSegment = await _segmentFormat.WriteAsync(
            _segmentFolder,
            plan.CompactedFileName,
            compactedEntries,
            tombstones: plan.NewTombstoneSeqnos,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // compute the compacted segment's CRC32C + size before flipping the
        // manifest so the durable commit point carries the integrity envelope.
        var compactedPath = Path.Combine(_segmentFolder, plan.CompactedFileName);
        var (compactedCrc, compactedSize) = SegmentManifest.ComputeFileCrc32C(compactedPath);

        return (compactedSegment, compactedCrc, compactedSize);
    }

    /// <summary>
    /// Commit step. Must be called while holding <see cref="_lock"/>. Revalidates that every
    /// picked segment is still registered (a concurrent <see cref="ClearAsync"/> is the only
    /// legal remover — the single-compactor gate excludes rival compactions), then flips the
    /// manifest, swaps the in-memory segment lists, and retires the picked files honouring
    /// live-snapshot pins. On a lost race the written output file is deleted (best-effort;
    /// the startup orphan sweep is the backstop) and no state changes.
    /// </summary>
    private async Task<bool> CommitCompactionLockedAsync(
        CompactionPlan plan,
        ISegmentReader<TKey, TValue> compactedSegment,
        uint compactedCrc,
        long compactedSize,
        CancellationToken cancellationToken)
    {
        // Abort on teardown, and on a Clear that raced the merge: the picked filenames are
        // matched against the CURRENT list (the flusher may have appended new segments during
        // the merge — that is fine and expected; removal is not).
        var abort = _disposed;
        var currentPickedIndexes = new List<int>(plan.PickedFileNames.Count);
        if (!abort)
        {
            foreach (var fileName in plan.PickedFileNames)
            {
                var idx = _segmentFileNames.IndexOf(fileName);
                if (idx < 0)
                {
                    abort = true;
                    break;
                }

                currentPickedIndexes.Add(idx);
            }
        }

        if (abort)
        {
            try
            {
                await compactedSegment.ClearAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // Orphan output stays on disk; the manifest never listed it, so the next
                // startup's orphan-reconciliation sweep removes it.
            }

            return false;
        }

        var pickedSet = new HashSet<int>(currentPickedIndexes);

        // Compute the new post-compaction segment ordering: unpicked segments keep their
        // relative order (oldest→newest), with the new compacted segment slotted after the
        // last picked index so "oldest wins last-writer-wins" across segments is stable
        // (every segment before the new one is strictly older in write order; flusher-appended
        // segments land after it, which is correct — they are newer than the merged data).
        var newSegmentList = new List<ISegmentReader<TKey, TValue>>();
        var newFileNames = new List<string>();
        var newIntegrity = new List<(uint Crc32C, long SizeBytes)>();
        var newMaxSeqnos = new List<ulong>();
        var newLevels = new List<int>();
        var newSegmentTombstones = new List<HashSet<TKey>>();
        var newSegmentTombstoneSeqnos = new List<Dictionary<TKey, ulong>>();

        var insertionIndex = currentPickedIndexes.Max();
        var inserted = -1;
        for (var i = 0; i < _segments.Count; i++)
        {
            if (!pickedSet.Contains(i))
            {
                newSegmentList.Add(_segments[i]);
                newFileNames.Add(_segmentFileNames[i]);
                newIntegrity.Add(_segmentIntegrity[i]);
                newMaxSeqnos.Add(_segmentMaxSeqnos[i]);
                newLevels.Add(_segmentLevels[i]);
                newSegmentTombstones.Add(_segmentTombstones[i]);
                newSegmentTombstoneSeqnos.Add(_segmentTombstoneSeqnos[i]);
            }

            if (i == insertionIndex && inserted == -1)
            {
                newSegmentList.Add(compactedSegment);
                newFileNames.Add(plan.CompactedFileName);
                newIntegrity.Add((compactedCrc, compactedSize));
                newMaxSeqnos.Add(plan.MaxPickedSeqno);
                newLevels.Add(plan.NewLevel);
                newSegmentTombstones.Add(plan.NewTombstones);
                newSegmentTombstoneSeqnos.Add(plan.NewTombstoneSeqnos);
                inserted = newSegmentList.Count - 1;
            }
        }

        // Flip the durable commit point: the manifest now lists the new segment set. A crash
        // before this write leaves the old set authoritative plus one orphan output file.
        var newManifest = new SegmentManifest.Entry[newFileNames.Count];
        for (var i = 0; i < newFileNames.Count; i++)
        {
            var (crc, size) = newIntegrity[i];
            newManifest[i] = new SegmentManifest.Entry(newFileNames[i], crc, size, newLevels[i]);
        }

        SegmentManifest.WriteEntries(_segmentFolder, newManifest);
        CompactionManifestCommittedHook?.Invoke();

        // Swap in-memory state. Capture the picked segments' filenames paired with their
        // readers first so we can skip ClearAsync on any that a live snapshot pinned at
        // acquisition time. The file stays on disk and the reader remains alive until the
        // last pinning snapshot disposes.
        var pickedForDelete = new List<(string FileName, ISegmentReader<TKey, TValue> Reader)>();
        foreach (var idx in currentPickedIndexes)
        {
            pickedForDelete.Add((_segmentFileNames[idx], _segments[idx]));
        }

        _segments.Clear();
        _segmentFileNames.Clear();
        _segmentIntegrity.Clear();
        _segmentMaxSeqnos.Clear();
        _segmentLevels.Clear();
        _segmentTombstones.Clear();
        _segmentTombstoneSeqnos.Clear();
        _segments.AddRange(newSegmentList);
        _segmentFileNames.AddRange(newFileNames);
        _segmentIntegrity.AddRange(newIntegrity);
        _segmentMaxSeqnos.AddRange(newMaxSeqnos);
        _segmentLevels.AddRange(newLevels);
        _segmentTombstones.AddRange(newSegmentTombstones);
        _segmentTombstoneSeqnos.AddRange(newSegmentTombstoneSeqnos);

        // Best-effort delete of the picked (now-retired) segment files, honouring any
        // live-snapshot pins. An exception here leaves orphans on disk; that's recoverable
        // and does not roll back the compaction.
        var pinnedSet = _snapshotRegistry.CollectAllPinned();
        foreach (var (fileName, reader) in pickedForDelete)
        {
            if (pinnedSet.Contains(fileName))
            {
                // Defer physical deletion until every pinning snapshot disposes. The reader
                // stays alive so live snapshots that captured it continue to serve reads.
                if (_retiredPinnedFiles.Add(fileName))
                {
                    Interlocked.Increment(ref _retiredPinnedFileCount);
                }

                _retiredPinnedReaders[fileName] = reader;
                continue;
            }

            try
            {
                await reader.ClearAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // Orphan file remains on disk; next startup's orphan-reconciliation sweep
                // will remove it. Invariant "every file tracked" is preserved via the manifest.
            }
        }

        // Sweep previously-retired pinned files whose last pin was released before or during
        // this compaction cycle. No lock juggling needed — we already hold _lock.
        await SweepRetiredPinnedFilesUnderLockAsync(CancellationToken.None).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Physically deletes any file in <see cref="_retiredPinnedFiles"/> that is no longer
    /// pinned by any live snapshot. Must be called while holding <see cref="_lock"/>.
    /// </summary>
    /// <remarks>
    /// Called from three sites: the end of compaction,
    /// <see cref="OnSnapshotDisposedAsync"/> (so the last snapshot to release a pin triggers
    /// immediate reclamation without waiting for the next compaction round), and
    /// <see cref="ClearAsync"/> (so a Clear that follows a snapshot dispose reclaims the
    /// now-unpinned files instead of leaking them).
    /// </remarks>
    private async Task SweepRetiredPinnedFilesUnderLockAsync(CancellationToken cancellationToken)
    {
        if (_retiredPinnedFiles.Count == 0)
        {
            return;
        }

        var stillPinned = _snapshotRegistry.CollectAllPinned();
        var reclaim = new List<string>();
        foreach (var fileName in _retiredPinnedFiles)
        {
            if (!stillPinned.Contains(fileName))
            {
                reclaim.Add(fileName);
            }
        }

        foreach (var fileName in reclaim)
        {
            if (_retiredPinnedFiles.Remove(fileName))
            {
                Interlocked.Decrement(ref _retiredPinnedFileCount);
            }

            if (_retiredPinnedReaders.Remove(fileName, out var reader))
            {
                try
                {
                    await reader.ClearAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    // Same fallback as the primary compaction-time delete: orphan survives
                    // in-process, next-start recovery cleans it up.
                }
            }
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> iff <paramref name="pickedSet"/> covers a contiguous prefix
    /// of segment indexes starting at 0 (i.e. <c>{0, 1, …, k}</c>). Only then is it safe for
    /// compaction to strip a tombstone: every segment older than the newest picked one is itself
    /// picked, so no unpicked older segment can carry a shadowed value that the strip would
    /// resurrect (DDIA 2e p. 124 — a tombstone may be dropped only after it has shadowed every
    /// older occurrence). Gating on <c>Contains(0)</c> alone is unsafe because size-tiered and
    /// leveled strategies produce non-contiguous picks such as <c>{0, 2}</c> (finding #2).
    /// </summary>
    internal static bool IsContiguousFromBottom(HashSet<int> pickedSet)
    {
        if (pickedSet.Count == 0 || !pickedSet.Contains(0))
        {
            return false;
        }
        var max = 0;
        foreach (var i in pickedSet)
        {
            if (i > max)
            {
                max = i;
            }
        }
        // Contiguous prefix [0..max] iff the count equals max + 1 (no gaps).
        return pickedSet.Count == max + 1;
    }

    /// <summary>
    /// Returns the list of segment indexes to compact this cycle, in ascending order. The
    /// strategy is the sole selection source; explicit compaction (<c>forceAll=true</c>)
    /// merges every segment regardless of the strategy's selection.
    /// </summary>
    private List<int> PickCompactionIndexes(
        bool forceAll = false,
        IReadOnlyList<SegmentMetadata<TKey>>? prebuiltMetadata = null)
    {
        if (forceAll)
        {
            // Explicit (operator-driven) compaction — full merge of all segments.
            return [.. Enumerable.Range(0, _segments.Count)];
        }

        var metadata = prebuiltMetadata ?? BuildSegmentMetadataList();
        var picked = _compactionStrategy.SelectSegments(metadata);
        if (picked.Count == 0)
        {
            return [];
        }

        // Metadata is a value, not a unique identity. Consume each selected occurrence once;
        // a set would silently select every segment whose metadata equals one chosen row.
        var remainingPicks = picked.GroupBy(item => item).ToDictionary(group => group.Key, group => group.Count());
        var result = new List<int>(picked.Count);
        for (var i = 0; i < metadata.Count; i++)
        {
            if (remainingPicks.TryGetValue(metadata[i], out var count) && count > 0)
            {
                result.Add(i);
                remainingPicks[metadata[i]] = count - 1;
            }
        }

        if (result.Count == 0)
        {
            return result;
        }

        // Normalize every pick to the contiguous index run [min..max]. Position-based
        // last-writer-wins across segments (point reads, scans, and the compacted segment's
        // insertion slot) is sound only while the segment list stays ordered by per-key write
        // recency — merging a NON-contiguous set (e.g. {0, 2}) and slotting the output after
        // index 2 would place segment 0's old versions at a newer position than the unpicked
        // middle segment's newer versions, shadowing them on reads and durably discarding
        // them on the next compaction (DDIA 2e Ch. 4: "keep only the more recent value").
        // Including the gap segments costs extra merge work but preserves correctness for
        // any strategy pick shape.
        var minIndex = result[0];
        var maxIndex = result[^1];
        if (result.Count != maxIndex - minIndex + 1)
        {
            result.Clear();
            for (var i = minIndex; i <= maxIndex; i++)
            {
                result.Add(i);
            }
        }
        return result;
    }

    /// <summary>
    /// Builds the <see cref="SegmentMetadata{TKey}"/> snapshot passed to the compaction strategy.
    /// </summary>
    private IReadOnlyList<SegmentMetadata<TKey>> BuildSegmentMetadataList()
    {
        var metadata = new List<SegmentMetadata<TKey>>(_segments.Count);
        for (var i = 0; i < _segments.Count; i++)
        {
            var (crc, size) = _segmentIntegrity[i];
            var hasRange = _segments[i].TryGetRange(out var minKey, out var maxKey);
            metadata.Add(new SegmentMetadata<TKey>(
                _segmentLevels[i],
                hasRange ? minKey : default!,
                hasRange ? maxKey : default!,
                size,
                crc)
            { HasKeyRange = hasRange });
        }
        return metadata;
    }

    /// <summary>
    /// Active+immutable rotation gate. Caller holds <see cref="_lock"/>;
    /// returns with <see cref="_lock"/> still held. When <see cref="ActiveSlotIsFull"/> is
    /// false, returns immediately. Otherwise: releases <see cref="_lock"/>, awaits an
    /// <see cref="_ingestGate"/> slot (the engine's only ingest-blocking signal), reacquires
    /// <see cref="_lock"/>, re-checks the active under the lock, then seals + allocates a
    /// fresh active. Cancellation propagates from <paramref name="cancellationToken"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why release <see cref="_lock"/> across the gate await.</b> The flusher's commit
    /// step needs <see cref="_lock"/> to free a queue slot; holding the lock while awaiting
    /// the gate would deadlock. Releasing it lets the flusher commit and signal a slot's
    /// release; the await then completes and we reacquire the lock for the seal.
    /// </para>
    /// <para>
    /// <b>Re-check after reacquire.</b> Concurrent mutators may have sealed the same active
    /// during our wait. We re-check <see cref="ActiveSlotIsFull"/>; if it's no longer full, we
    /// return the gate slot we just acquired and proceed.
    /// </para>
    /// </remarks>
    private async Task EnsureActiveHasCapacityAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfFaulted();

        if (!ActiveSlotIsFull())
        {
            return;
        }

        _lock.Release();

        // Test seam: parks the caller inside the release→gate-registration window so the
        // seqno apply-order inversion (a later-seqno mutator overtaking an earlier one across
        // this lock gap) can be reproduced deterministically. Null in production.
        if (IngestGateWindowHook is { } gateWindowHook)
        {
            await gateWindowHook().ConfigureAwait(false);
        }

        var gateAcquired = false;
        try
        {
            try
            {
                await WaitForIngestPermitAsync(cancellationToken).ConfigureAwait(false);
                gateAcquired = true;
            }
            finally
            {
                // Reacquire _lock with CancellationToken.None — if the caller already
                // cancelled we still need the lock to release the gate slot we may have
                // acquired and to leave the engine in a consistent state. The
                // OperationCanceledException is rethrown from the catch block below if
                // gateAcquired is false (cancellation raced with the wait).
                await _lock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            }

            ObjectDisposedException.ThrowIf(_disposed, this);
            ThrowIfFaulted();

            if (!ActiveSlotIsFull())
            {
                // Another caller already sealed during our wait — return the slot.
                if (gateAcquired)
                {
                    _ingestGate.Release();
                }
                cancellationToken.ThrowIfCancellationRequested();
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();

            SealActiveLocked();
            // Gate slot stays consumed; it is released at flusher commit (E-Persist).
        }
        catch
        {
            // Cleanup: if anything between gate-wait and SealActiveLocked throws after the
            // gate was acquired, release it so the slot is not leaked. If SealActiveLocked
            // threw (AllocateActiveSlot OOM), no state has
            // mutated yet — the gate slot is the only resource we own and must release.
            if (gateAcquired)
            {
                _ingestGate.Release();
            }
            throw;
        }
    }

    /// <summary>
    /// True when the active slot has reached <see cref="_memTableCapacity"/> entries, counting
    /// tombstones alongside tree entries. A tombstone occupies the slot just as a value does — it
    /// is held in memory and backed by a WAL frame that only the slot's flush can discard — so a
    /// delete-only workload must seal on the same threshold as a write workload; counting tree
    /// entries alone let tombstones and the WAL grow without bound. Caller holds
    /// <see cref="_lock"/>.
    /// </summary>
    private bool ActiveSlotIsFull()
        => _active.Tree.IsFull || _active.Tree.Count + _active.Tombstones.Count >= _memTableCapacity;

    /// <summary>
    /// Waits for one immutable-queue permit, aborting promptly when the engine faults or is
    /// disposed. A terminal-state cancellation is translated back to the engine's public typed
    /// exception rather than escaping as an unrelated <see cref="OperationCanceledException"/>.
    /// </summary>
    private async Task WaitForIngestPermitAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfFaulted();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _terminalStateCts.Token);
        try
        {
            await _ingestGate.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_terminalStateCts.IsCancellationRequested)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ThrowIfFaulted();
            throw;
        }
    }

    private IngestOperationLease EnterIngestOperation()
    {
        lock (_ingestLifetimeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_activeIngestOperations++ == 0)
            {
                _ingestOperationsDrained.Reset();
            }
            return new IngestOperationLease(this);
        }
    }

    // Admission for a caller that must not throw on a disposed engine (snapshot release).
    private bool TryEnterIngestOperation(out IngestOperationLease lease)
    {
        lock (_ingestLifetimeGate)
        {
            if (_disposed)
            {
                lease = default;
                return false;
            }
            if (_activeIngestOperations++ == 0)
            {
                _ingestOperationsDrained.Reset();
            }
            lease = new IngestOperationLease(this);
            return true;
        }
    }

    private void ExitIngestOperation()
    {
        lock (_ingestLifetimeGate)
        {
            if (--_activeIngestOperations == 0)
            {
                _ingestOperationsDrained.Set();
            }
        }
    }

    private readonly struct IngestOperationLease(LsmStorageEngine<TKey, TValue> owner) : IDisposable
    {
        public void Dispose() => owner.ExitIngestOperation();
    }

    /// <summary>
    /// Atomically seals the current active and allocates a fresh one. Caller holds
    /// <see cref="_lock"/> AND has already acquired one <see cref="_ingestGate"/> slot.
    /// </summary>
    /// <remarks>
    /// Allocate-before-enqueue: the fresh active is built first so an
    /// <see cref="OutOfMemoryException"/> from <c>AllocateActiveSlot</c> leaves invariants
    /// intact (the gate slot still belongs to the caller, who releases it in the catch
    /// block of <see cref="EnsureActiveHasCapacityAsync"/>). After the swap, a single
    /// signal is written to the flush channel; duplicates collapse via the channel's
    /// <see cref="BoundedChannelFullMode.DropWrite"/> policy.
    /// </remarks>
    private void SealActiveLocked()
    {
        var fresh = AllocateActiveSlot();
        var oldActive = _active;
        oldActive.State = MemtableSlotState.Sealed;
        oldActive.MaxSeqnoAtSeal = (ulong)Interlocked.Read(ref _lastAssignedSequenceNumber);
        _immutables.Add(oldActive);
        _active = fresh;
        _flushChannel.Writer.TryWrite(0);
    }

    /// <summary>
    /// Long-lived background flusher loop. Waits for seal signals on the flush
    /// channel; on each signal, drains every queued sealed slot. Each slot is committed via
    /// a three-step protocol:
    /// (1) under <see cref="_lock"/>, snapshot the slot's data + tombstones into local
    /// copies so the SST writer can run without holding the lock;
    /// (2) without <see cref="_lock"/>, write the SSTable;
    /// (3) under <see cref="_lock"/>, append to <c>_segments</c>, write the manifest,
    /// remove the slot from <c>_immutables</c>, release one <see cref="_ingestGate"/> slot,
    /// complete the slot's <see cref="MemtableSlot{TKey, TValue}.Persisted"/>, truncate the
    /// WAL when the queue empties, and run the post-flush compaction-strategy check.
    /// On cancellation (Dispose), the loop exits via <see cref="OperationCanceledException"/>.
    /// </summary>
    private async Task FlusherLoopAsync(CancellationToken ct)
    {
        // The slot whose commit removed it from _immutables but whose Persisted task is not yet
        // resolved (the post-flush compaction is still settling). Disposal's slot cancellation
        // walks _immutables only, so a cancellation that ends the loop in that window must
        // resolve this slot itself, or its FlushAsync never returns the ingest lease Dispose
        // waits for.
        MemtableSlot<TKey, TValue>? committedUnresolved = null;
        try
        {
            await foreach (var _ in _flushChannel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                while (true)
                {
                    MemtableSlot<TKey, TValue>? toFlush = null;
                    List<(TKey Key, MemTableValue<TValue> Value)>? dataCopy = null;
                    Dictionary<TKey, ulong>? tombCopy = null;
                    string? segmentFileName = null;
                    ulong maxSeqno;
                    ulong slotGen;
                    CompactionPlan? autoCompactionPlan = null;
                    var compactionGateHeld = false;

                    await _lock.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        if (_immutables.Count == 0)
                        {
                            break;
                        }
                        toFlush = _immutables[0];
                        if (toFlush.State != MemtableSlotState.Sealed)
                        {
                            // Already mid-flush (re-entry into the loop after a previous
                            // iteration), or Faulted (terminal — the slot persists at the
                            // head and ThrowIfFaulted surfaces the cause to consumers).
                            break;
                        }
                        toFlush.State = MemtableSlotState.Flushing;
                        slotGen = toFlush.Generation;
                        maxSeqno = toFlush.MaxSeqnoAtSeal;
                        // Copy the slot's data + tombstones under _lock so the SST writer
                        // walks an isolated snapshot. The slot itself stays in _immutables
                        // and remains read-visible (invariant L3).
                        var items = await toFlush.Tree.GetAllItemsAsync(ct).ConfigureAwait(false);
                        dataCopy = items.ToList();
                        tombCopy = new Dictionary<TKey, ulong>(toFlush.Tombstones);

                        // Reserve the segment filename UNDER the lock: CompactAsync names its
                        // output from the same counter (also under the lock), so deriving the
                        // name in the unlocked write window below would let a concurrent
                        // compaction pick the identical filename — the later atomic replace
                        // silently clobbers the earlier segment's bytes and the manifest ends
                        // up listing the same file twice with conflicting CRCs. A reserved
                        // name whose write later faults just leaves a gap in the numbering.
                        if (dataCopy.Count > 0 || tombCopy.Count > 0)
                        {
                            segmentFileName = $"{_segmentPrefix}_{_segmentCounter:D6}{_segmentFormat.FileExtension}";
                            _segmentCounter++;
                        }
                    }
                    finally
                    {
                        _lock.Release();
                    }

                    ISegmentReader<TKey, TValue>? segment = null;
                    List<SegmentWriteEntry<TKey, TValue>>? entries = null;

                    // A tombstone-only flush writes its own data-less, tombstone-carrying segment
                    // (entries is empty; both segment formats persist the tombstone map in the
                    // footer with zero data blocks). Persisting it to disk — rather than attaching
                    // the tombstones to the newest segment's in-memory map only — is what lets a
                    // delete flushed in a separate flush survive a no-WAL reopen (DDIA Ch. 4 p. 121).
                    var writeSegment = segmentFileName is not null;

                    if (writeSegment)
                    {
                        try
                        {
                            // SST write runs WITHOUT _lock — concurrent ingest is permitted.
                            entries = BuildSegmentWriteEntries(dataCopy!);
                            segment = await _segmentFormat.WriteAsync(
                                _segmentFolder, segmentFileName!,
                                entries, tombstones: tombCopy,
                                cancellationToken: ct).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            // The format may have created the segment's final path before the write
                            // failed. No manifest lists it, so it is discarded like the disposal
                            // paths below discard theirs.
                            DeleteUnlistedSegmentFile(segmentFileName!);
                            await _lock.WaitAsync(ct).ConfigureAwait(false);
                            try { EnterFaultedState(toFlush!, ex); }
                            finally { _lock.Release(); }
                            continue;
                        }
                    }

                    // Commit step — under _lock; atomic vs. ingest and snapshot acquisition.
                    // The segment written above is listed nowhere until the commit appends it
                    // to the manifest; when disposal cancels the lock wait, or is observed once
                    // the lock is held, the file is deleted rather than left behind. One that
                    // survives a crash is an unlisted orphan the next open sweeps (a fresh engine
                    // writes an empty CURRENT before its first flush); its writes remain
                    // recoverable from the WAL, which was never truncated.
                    try
                    {
                        await _lock.WaitAsync(ct).ConfigureAwait(false);
                    }
                    catch
                    {
                        if (segment is not null)
                        {
                            await DiscardUnlistedSegmentAsync(segment).ConfigureAwait(false);
                        }

                        throw;
                    }

                    try
                    {
                        if (FlusherCommitLockHook is { } commitLockHook)
                        {
                            await commitLockHook(ct).ConfigureAwait(false);
                        }

                        if (_disposed)
                        {
                            if (segment is not null)
                            {
                                await DiscardUnlistedSegmentAsync(segment).ConfigureAwait(false);
                            }

                            toFlush!.Persisted.TrySetCanceled(ct);
                            break;
                        }

                        // Head-of-queue check (L2). A missing/mismatched slot here means a
                        // concurrent ClearAsync discarded it after this flusher started the
                        // write — the only legal remover besides the flusher itself. That is
                        // a benign consequence of a legal call sequence, not an invariant
                        // violation: treat it as cancellation. ClearAsync already released
                        // the slot's gate slot and canceled its Persisted TCS; the segment
                        // file written above is an orphan no manifest references and must be
                        // deleted, or the next open fails closed on "segment files without a
                        // CURRENT manifest".
                        if (_immutables.Count == 0 || _immutables[0].Generation != slotGen)
                        {
                            if (writeSegment)
                            {
                                try
                                {
                                    File.Delete(Path.Combine(_segmentFolder, segmentFileName!));
                                }
                                catch (IOException)
                                {
                                    // Best-effort; the startup orphan sweep also covers it
                                    // when a manifest exists.
                                }
                            }
                            toFlush!.Persisted.TrySetCanceled(CancellationToken.None);
                            continue;
                        }

                        if (writeSegment)
                        {
                            // Registers the segment + its tombstones. For a tombstone-only flush the
                            // segment carries zero data blocks but a real tombstone footer, so the
                            // delete is durable: recovery rebuilds _segmentTombstoneSeqnos from
                            // GetTombstoneSeqnos and the deletion seqno survives the reopen.
                            AppendSegmentBookkeeping(
                                segment!, segmentFileName!, maxSeqno, tombCopy!);
                        }

                        _immutables.RemoveAt(0);
                        committedUnresolved = toFlush;
                        toFlush!.State = MemtableSlotState.Persisted;
                        _ingestGate.Release();
                        // _segmentCounter was already advanced when the filename was reserved
                        // under the step-1 lock hold.

                        // WAL truncation runs OUTSIDE the Faulted-promoting catch: at this
                        // point the segment is durable and the manifest reflects it, so a
                        // Truncate failure is recoverable housekeeping (the un-truncated WAL
                        // is replayed idempotently on next OpenAsync). Failing here MUST NOT
                        // promote to Faulted — doing so would terminally fault an engine
                        // that has just successfully committed a flush.
                        //
                        // The truncation watermark is the max seqno CONTAINED in the flushed
                        // slot — never the slot's MaxSeqnoAtSeal: the write that triggers a
                        // seal appends its WAL frame before the seal but applies its data to
                        // the fresh active, so MaxSeqnoAtSeal can overshoot the slot's actual
                        // contents by exactly those post-seal writes. Frames at or below the
                        // contained watermark are provably all in this slot or earlier (a full
                        // memtable stays full until sealed, so apply order cannot cross a seal
                        // boundary downward), making segment-level deletion below it safe.
                        if (_wal is not null)
                        {
                            var walWatermark = 0UL;
                            for (var i = 0; i < dataCopy!.Count; i++)
                            {
                                if (dataCopy[i].Value.Seqno > walWatermark)
                                {
                                    walWatermark = dataCopy[i].Value.Seqno;
                                }
                            }
                            foreach (var tombSeqno in tombCopy!.Values)
                            {
                                if (tombSeqno > walWatermark)
                                {
                                    walWatermark = tombSeqno;
                                }
                            }

                            if (walWatermark > 0)
                            {
                                try
                                {
                                    await _wal.TruncateCompletedSegmentsAsync(walWatermark, ct).ConfigureAwait(false);
                                }
                                catch (Exception truncateEx) when (truncateEx is not OperationCanceledException)
                                {
                                    // Best-effort; surfaced via the engine's diagnostics seam.
                                    System.Diagnostics.Trace.TraceWarning(
                                        "Boutquin.Storage: WAL truncation after flush failed: "
                                        + truncateEx.Message);
                                }
                            }
                        }

                        // Auto-compact tick: the strategy consult and the PLAN capture run
                        // under this same lock hold (both cheap); the O(dataset) merge runs
                        // after the lock releases below so ingest and reads are never blocked
                        // behind it. Try-acquire: if an explicit CompactAsync is mid-flight,
                        // skip the tick — the strategy re-fires on the next flush.
                        try
                        {
                            if (_compactionGate.Wait(0, CancellationToken.None))
                            {
                                compactionGateHeld = true;
                                var metadata = BuildSegmentMetadataList();
                                if (_compactionStrategy.ShouldCompact(metadata))
                                {
                                    autoCompactionPlan = PickCompactionPlanLocked(
                                        forceFullCompaction: false, prebuiltMetadata: metadata);
                                }

                                if (autoCompactionPlan is null)
                                {
                                    _compactionGate.Release();
                                    compactionGateHeld = false;
                                }
                            }
                        }
                        catch
                        {
                            if (compactionGateHeld)
                            {
                                _compactionGate.Release();
                                compactionGateHeld = false;
                            }

                            throw;
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        EnterFaultedState(toFlush!, ex);
                        break;
                    }
                    finally
                    {
                        _lock.Release();
                    }

                    // Run the picked auto-compaction: merge WITHOUT the lock, commit under a
                    // fresh lock hold. FlushAsync's contract is that, on return, the WAL has
                    // been truncated and post-flush compaction has settled — so the Persisted
                    // TCS resolves strictly AFTER the compaction settles (and its exceptions
                    // fault the slot exactly as the old under-lock compaction did).
                    if (autoCompactionPlan is not null)
                    {
                        try
                        {
                            var (compacted, crc, size) =
                                await MergeCompactionAsync(autoCompactionPlan, ct).ConfigureAwait(false);
                            await CommitCompactionAsync(
                                autoCompactionPlan, compacted, crc, size, ct).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            await _lock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                            try
                            {
                                EnterFaultedState(toFlush!, ex);
                            }
                            finally
                            {
                                _lock.Release();
                            }

                            _compactionGate.Release();
                            compactionGateHeld = false;
                            break;
                        }
                        finally
                        {
                            if (compactionGateHeld)
                            {
                                _compactionGate.Release();
                                compactionGateHeld = false;
                            }
                        }
                    }

                    toFlush!.Persisted.TrySetResult(true);
                    committedUnresolved = null;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Dispose path — graceful exit. A slot already committed but still waiting on its
            // post-flush compaction is cancelled like every queued slot disposal cancels.
            committedUnresolved?.Persisted.TrySetCanceled(CancellationToken.None);
        }
    }

    /// <summary>
    /// Builds the per-key <see cref="SegmentWriteEntry{TKey, TValue}"/> list the segment
    /// format consumes. Pointer-valued slots flow straight through with their 12-byte body
    /// intact (no value-log read at flush time, no <c>TValue.Deserialize</c>); inline slots
    /// serialise inside the SSTable writer. The per-entry seqno carries through unchanged.
    /// </summary>
    private static List<SegmentWriteEntry<TKey, TValue>> BuildSegmentWriteEntries(
        List<(TKey Key, MemTableValue<TValue> Value)> items)
    {
        var entries = new List<SegmentWriteEntry<TKey, TValue>>(items.Count);
        foreach (var (key, slot) in items)
        {
            var seqno = slot.Seqno;
            entries.Add(slot.Kind == ValueKind.Inline
                ? SegmentWriteEntry<TKey, TValue>.Inline(key, slot.InlineValue, seqno)
                : SegmentWriteEntry<TKey, TValue>.Pointer(key, slot.Pointer.ToArray(), seqno));
        }
        return entries;
    }

    /// <summary>
    /// Appends one freshly-flushed segment to every parallel bookkeeping list and writes
    /// the manifest. Caller holds <see cref="_lock"/>.
    /// </summary>
    private void AppendSegmentBookkeeping(
        ISegmentReader<TKey, TValue> segment,
        string segmentFileName,
        ulong maxSeqno,
        Dictionary<TKey, ulong> slotTombstones)
    {
        _segments.Add(segment);
        _segmentFileNames.Add(segmentFileName);
        _segmentMaxSeqnos.Add(maxSeqno);

        uint segCrc;
        long segSize;
        if (segment is ISegmentReaderWithIntegrity ir)
        {
            segCrc = ir.WrittenFileCrc32C;
            segSize = ir.WrittenFileSizeBytes;
        }
        else
        {
            var segmentPath = Path.Combine(_segmentFolder, segmentFileName);
            (segCrc, segSize) = SegmentManifest.ComputeFileCrc32C(segmentPath);
        }
        _segmentIntegrity.Add((segCrc, segSize));
        _segmentLevels.Add(0);
        _segmentTombstones.Add([.. slotTombstones.Keys]);
        _segmentTombstoneSeqnos.Add(new Dictionary<TKey, ulong>(slotTombstones));

        // Every parallel list is extended before the manifest write, so a write that throws
        // (the CURRENT temp file cannot be created) faults the engine with the lists still in
        // lock-step: the faulted engine keeps serving reads and snapshots, which index all of
        // them by the same segment position. The segment is then registered in memory but
        // possibly not in CURRENT; the un-truncated WAL and the slot still at the head of the
        // immutable queue keep its writes recoverable either way.
        WriteManifest();
    }

    /// <summary>
    /// Faulted-state entry point. Caller MUST hold <see cref="_lock"/> —
    /// iterating <c>_immutables</c> outside the lock would race with concurrent ingest
    /// mutators (<c>SealActiveLocked</c> appends, <c>ClearAsync</c> clears).
    /// </summary>
    private void EnterFaultedState(MemtableSlot<TKey, TValue> failedSlot, Exception ex)
    {
        failedSlot.State = MemtableSlotState.Faulted;
        var edi = ExceptionDispatchInfo.Capture(ex);
        Interlocked.CompareExchange(ref _faultedReason, edi, null);
        _terminalStateCts.Cancel();
        // The failed slot is completed explicitly: a failure in the write or commit step leaves
        // it at the head of _immutables, but one raised after the commit removed it (the
        // post-flush compaction-strategy consult, the auto-compaction merge and its commit)
        // finds it in no queue, and a FlushAsync awaiting it would wait forever while holding
        // the ingest lease Dispose drains. TrySetException is idempotent.
        failedSlot.Persisted.TrySetException(ex);
        foreach (var slot in _immutables)
        {
            slot.Persisted.TrySetException(ex);
        }
    }

    /// <summary>
    /// Awaits every currently-queued immutable slot's <see cref="MemtableSlot{TKey, TValue}.Persisted"/>
    /// task. Used by recovery (<see cref="ReplayWalAsync"/>) to ensure the engine returns
    /// to the caller in steady state, and by <see cref="ClearAsync"/> to drain before
    /// discarding.
    /// </summary>
    private async Task DrainAllInFlightAsync(CancellationToken cancellationToken)
    {
        List<Task> waiters;
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            waiters = _immutables.Select(s => (Task)s.Persisted.Task).ToList();
        }
        finally
        {
            _lock.Release();
        }
        if (waiters.Count > 0)
        {
            await Task.WhenAll(waiters).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Builds the seqno-carrying tombstone filter applied during streaming compaction: a map from
    /// each strippable-tombstone key to the highest tombstone seqno observed for it. Only tombstones
    /// whose seqno is strictly less than <paramref name="minSnapSeqno"/> are included — tombstones
    /// still observable by a live snapshot must flow through compaction so the snapshot's captured
    /// view remains correct. The merge loop suppresses a surviving data entry only when the mapped
    /// seqno is strictly newer than that entry's seqno, so a re-insert that out-versions the
    /// tombstone survives. See <c>docs/snapshot-isolation.md</c>.
    /// </summary>
    /// <param name="pickedSet">Indexes of segments participating in this compaction round.</param>
    /// <param name="minSnapSeqno">
    /// <see cref="SnapshotRegistry.GetMinSequenceNumber"/> at the start of the round. When no
    /// live snapshot exists this is <see cref="ulong.MaxValue"/> and every tombstone is strippable.
    /// </param>
    private Dictionary<TKey, ulong>? BuildCompactionTombstoneFilter(HashSet<int> pickedSet, ulong minSnapSeqno)
    {
        // Maps each strippable-tombstone key to the highest tombstone seqno seen for it. The merge
        // loop suppresses a surviving data entry only when this seqno is strictly newer than the
        // entry's own seqno, so a re-insert that out-versions the tombstone survives compaction.
        var filter = new Dictionary<TKey, ulong>();

        void Record(TKey key, ulong t)
        {
            if (t >= minSnapSeqno)
            {
                // A live snapshot can still observe the delete — do not strip; the pinned
                // pre-compaction segments carry the versions the snapshot needs.
                return;
            }
            if (!filter.TryGetValue(key, out var existing) || t > existing)
            {
                filter[key] = t;
            }
        }

        for (var i = 0; i < _segmentTombstones.Count; i++)
        {
            if (!pickedSet.Contains(i))
            {
                // Tombstones on unpicked segments are not in this round's merge stream, so
                // filtering them would be a no-op. Skipping keeps the filter small.
                continue;
            }
            var segTombstones = _segmentTombstones[i];
            var segSeqnos = _segmentTombstoneSeqnos[i];
            foreach (var key in segTombstones)
            {
                // Skip tombstones whose seqno isn't recorded — no seqno means we cannot
                // determine whether a live snapshot still observes the delete; defer to safety.
                if (!segSeqnos.TryGetValue(key, out var t))
                {
                    continue;
                }
                Record(key, t);
            }
        }

        // A MemTable delete that hasn't flushed yet must still mask older segment values on
        // the compaction path. The tombstone seqno lives in each slot's tombstone map;
        // apply the same live-snapshot-defers-strip rule across active + immutables.
        foreach (var (key, t) in _active.Tombstones)
        {
            Record(key, t);
        }
        foreach (var slot in _immutables)
        {
            foreach (var (key, t) in slot.Tombstones)
            {
                Record(key, t);
            }
        }

        return filter.Count > 0 ? filter : null;
    }

    /// <summary>
    /// Writes the <c>CURRENT</c> manifest from the in-memory per-segment tracking lists
    /// (<see cref="_segmentFileNames"/> paired with <see cref="_segmentIntegrity"/>).
    /// Callers must have already populated both lists in lock-step.
    /// </summary>
    private void WriteManifest()
    {
        var entries = new SegmentManifest.Entry[_segmentFileNames.Count];
        for (var i = 0; i < _segmentFileNames.Count; i++)
        {
            var (crc, size) = _segmentIntegrity[i];
            entries[i] = new SegmentManifest.Entry(_segmentFileNames[i], crc, size, _segmentLevels[i]);
        }
        SegmentManifest.WriteEntries(_segmentFolder, entries);
    }

    /// <summary>
    /// Recovers the segment set on engine start. The <c>CURRENT</c> manifest is the
    /// authoritative segment list. Once every row is proven to name a present, CRC-matching
    /// file, an unlisted segment file under an engine-issued name is an orphan from an
    /// interrupted compaction or flush and is deleted; while any row is rejected nothing is
    /// deleted. A missing manifest alongside existing
    /// segment files is a corrupted-state condition and surfaces as
    /// <see cref="InvalidDataException"/>.
    /// </summary>
    /// <remarks>
    /// Every manifest row carries a CRC32C + size recorded at flush time. Recovery recomputes
    /// the CRC of each segment file and compares unconditionally; a mismatch fails the open
    /// with <see cref="InvalidDataException"/> — the segment holds acknowledged data the
    /// engine can no longer trust, and silently excluding it would serve reads as if its keys
    /// never existed (resurrecting older values and deletes from elder segments).
    /// </remarks>
    private void RecoverSegmentsFromDisk()
    {
        var pattern = $"{_segmentPrefix}_*{_segmentFormat.FileExtension}";
        var existingFiles = Directory.GetFiles(_segmentFolder, pattern);
        var existingFileNames = existingFiles
            .Select(Path.GetFileName)
            .Where(n => n is not null)
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);

        var manifestEntries = SegmentManifest.ReadEntries(_segmentFolder);

        List<SegmentManifest.Entry> segmentsToLoad;
        var canSweepTemps = true;
        if (manifestEntries is not null)
        {
            // Every row is proven to name a present, CRC-matching file BEFORE anything is
            // deleted. CURRENT carries no checksum of its own, so a damaged filename byte makes a
            // row name a file that does not exist while the live file it should have named looks
            // unlisted. Until every row resolves, the listing and the directory disagree for a
            // reason the engine cannot attribute, and an unlisted file is not evidence of an
            // orphan: it may be exactly the segment the open is about to tell the operator to
            // restore. So a rejected row fails the open (FailClosed) before the sweep, and under
            // ExcludeAndSurface it suppresses the sweep for this open.
            segmentsToLoad = ValidateManifestRows(manifestEntries, out var anyRowRejected);
            canSweepTemps = !anyRowRejected;
            if (!anyRowRejected)
            {
                // Manifest is authoritative and verified. Sweep orphans: an unlisted segment
                // file under a name the engine issues is residue from an interrupted compaction or
                // a failed flush. The glob also matches names the engine never issues; those are
                // not its files, so they are left alone.
                var manifestSet = manifestEntries.Select(e => e.FileName).ToHashSet(StringComparer.Ordinal);
                foreach (var fileName in existingFileNames)
                {
                    if (!manifestSet.Contains(fileName) && IsEngineIssuedSegmentName(fileName))
                    {
                        var orphanPath = Path.Combine(_segmentFolder, fileName);
                        try { File.Delete(orphanPath); } catch (IOException) { /* best-effort */ }
                    }
                }
            }
        }
        else if (existingFileNames.Count > 0)
        {
            // A missing manifest alongside segment files is a corrupted-state condition. It
            // cannot arise from a well-behaved write path — a fresh engine writes an empty
            // manifest before its first flush can create a segment file — so the only producer
            // is external tooling or a lost manifest. Fail closed.
            throw new InvalidDataException(
                $"Segment directory '{_segmentFolder}' contains {existingFileNames.Count} " +
                $"segment file(s) but no CURRENT manifest. Either restore the manifest or " +
                $"remove the orphan segment files before reopening the engine.");
        }
        else
        {
            // Fresh engine — no manifest, no segment files. An empty manifest is written now, so
            // an unfinished segment a crash leaves during the first flush is an unlisted orphan
            // the next open sweeps, not "segment files without a manifest", which it refuses.
            SegmentManifest.WriteEntries(_segmentFolder, []);
            segmentsToLoad = [];
        }

        if (canSweepTemps)
        {
            foreach (var path in Directory.EnumerateFiles(_segmentFolder))
            {
                var name = Path.GetFileName(path);
                var isSegmentTemp = name.EndsWith(".tmp", StringComparison.Ordinal)
                    && IsEngineIssuedSegmentName(name[..^4]);
                var isManifestTemp = name.StartsWith("CURRENT.tmp.", StringComparison.Ordinal)
                    && name.Length == "CURRENT.tmp.".Length + 32
                    && Guid.TryParseExact(name["CURRENT.tmp.".Length..], "N", out _);
                if (isSegmentTemp || isManifestTemp)
                {
                    try { File.Delete(path); } catch (IOException) { /* Retry cleanup on the next open. */ }
                }
            }
        }

        foreach (var entry in segmentsToLoad)
        {
            var segment = _segmentFormat.Open(_segmentFolder, entry.FileName);
            _segments.Add(segment);
            _segmentFileNames.Add(entry.FileName);
            _segmentIntegrity.Add((entry.Crc32C, entry.SizeBytes));
            _segmentLevels.Add(entry.Level);

            // read the segment's max persisted seqno so the engine can
            // reseed its monotonic counter to max(sst_max, wal_max) + 1 before accepting new
            // writes. SstSegmentReader surfaces the SSTable footer's derived max; columnar
            // readers surface the footer's persisted watermark seeded during recovery.
            var segmentMaxSeqno = segment.MaxSequenceNumber;
            _segmentMaxSeqnos.Add(segmentMaxSeqno);
            if ((long)segmentMaxSeqno > Interlocked.Read(ref _lastAssignedSequenceNumber))
            {
                Interlocked.Exchange(ref _lastAssignedSequenceNumber, (long)segmentMaxSeqno);
            }

            // recovery no longer scans every block to derive MinKey/MaxKey. The
            // SSTable footer carries them directly; engine consumers call segment.TryGetRange()
            // on demand. Recovery time on a 100-segment engine drops to footer-read-only.

            // recovery rebuilds per-segment tombstones from the segment footer
            // (DDIA Ch. 4 p. 124). Pre-3.C.3 segments — which never reach disk under the
            // greenfield premise — surface an empty map and the engine continues with
            // empty-tombstone state, which matches the previous behaviour.
            var persistedTombstones = segment.GetTombstoneSeqnos();
            if (persistedTombstones.Count == 0)
            {
                _segmentTombstones.Add([]);
                _segmentTombstoneSeqnos.Add([]);
            }
            else
            {
                _segmentTombstones.Add([.. persistedTombstones.Keys]);
                _segmentTombstoneSeqnos.Add(new Dictionary<TKey, ulong>(persistedTombstones));

                // Reseed the counter from tombstone seqnos too. A tombstone-only segment carries a
                // real deletion seqno but its data-derived MaxSequenceNumber may be lower than
                // persisted tombstone seqnos, so without this a post-recovery re-insert of a
                // deleted key would be assigned a seqno BELOW the recovered tombstone and the
                // seqno-aware filter would wrongly suppress it.
                foreach (var tombstoneSeqno in persistedTombstones.Values)
                {
                    if ((long)tombstoneSeqno > Interlocked.Read(ref _lastAssignedSequenceNumber))
                    {
                        Interlocked.Exchange(ref _lastAssignedSequenceNumber, (long)tombstoneSeqno);
                    }
                }
            }
        }

        // Every manifest row counts, including one excluded under ExcludeAndSurface, and so does
        // every segment file still on disk — a file the sweep kept because a row was rejected
        // included: a new segment must never take a name a row claims or a kept file holds, or
        // the flush would replace the file the open declined to delete.
        var counterNames = (manifestEntries ?? []).Select(e => e.FileName)
            .Concat(existingFileNames.Where(n => File.Exists(Path.Combine(_segmentFolder, n))))
            .ToList();
        if (counterNames.Count == 0)
        {
            return;
        }

        // Advance the counter past the highest counter seen so new segment names never collide
        // with the recovered ones. Parse from "{prefix}_NNNNNN.{ext}" where NNNNNN is the counter.
        var maxCounter = -1;
        foreach (var name in counterNames)
        {
            var stem = Path.GetFileNameWithoutExtension(name);
            var counterPart = stem[(stem.LastIndexOf('_') + 1)..];
            if (int.TryParse(counterPart, out var counter) && counter > maxCounter)
            {
                maxCounter = counter;
            }
        }

        _segmentCounter = maxCounter >= 0 ? maxCounter + 1 : counterNames.Count;
    }

    /// <summary>
    /// <see langword="true"/> when <paramref name="fileName"/> has exactly the shape the engine
    /// gives a segment it writes: <c>{prefix}_{counter:D6}{extension}</c> — six digits below
    /// one million, then an unpadded decimal counter with no leading zeroes.
    /// </summary>
    private bool IsEngineIssuedSegmentName(string fileName)
    {
        var head = _segmentPrefix + "_";
        var extension = _segmentFormat.FileExtension;
        if (!fileName.StartsWith(head, StringComparison.Ordinal)
            || !fileName.EndsWith(extension, StringComparison.Ordinal)
            || fileName.Length < head.Length + 6 + extension.Length)
        {
            return false;
        }

        var counter = fileName.AsSpan(head.Length, fileName.Length - head.Length - extension.Length);
        foreach (var c in counter)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return int.TryParse(counter, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            && counter.SequenceEqual(parsed.ToString("D6", System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Proves every manifest row names a present file whose CRC32C and size match the row, and
    /// returns the rows that do. A rejected row fails the open under
    /// <see cref="CorruptSegmentRecoveryDisposition.FailClosed"/>; under
    /// <see cref="CorruptSegmentRecoveryDisposition.ExcludeAndSurface"/> it is reported, left out,
    /// and <paramref name="anyRowRejected"/> is set.
    /// </summary>
    /// <remarks>
    /// A mismatch means a manifest-listed segment — acknowledged, flushed data — can no longer be
    /// trusted. Default disposition fails closed (matching the missing-manifest disposition):
    /// silently skipping would serve reads as if the segment's keys never existed, resurrecting
    /// older values and deletes from elder segments. ExcludeAndSurface is the cache-tier opt-in:
    /// the segment is skipped and the exclusion reported, because the consumer can rebuild the
    /// lost span from an upstream source (see CorruptSegmentRecoveryDisposition remarks).
    /// </remarks>
    private List<SegmentManifest.Entry> ValidateManifestRows(
        IReadOnlyList<SegmentManifest.Entry> manifestEntries,
        out bool anyRowRejected)
    {
        anyRowRejected = false;
        var verified = new List<SegmentManifest.Entry>(manifestEntries.Count);
        foreach (var entry in manifestEntries)
        {
            var segmentPath = Path.Combine(_segmentFolder, entry.FileName);
            uint observedCrc;
            long observedSize;
            var fileMissing = !File.Exists(segmentPath);
            if (fileMissing)
            {
                (observedCrc, observedSize) = (0u, 0L);
            }
            else
            {
                (observedCrc, observedSize) = SegmentManifest.ComputeFileCrc32C(segmentPath);
            }

            if (!fileMissing && observedCrc == entry.Crc32C && observedSize == entry.SizeBytes)
            {
                verified.Add(entry);
                continue;
            }

            if (_corruptSegmentRecovery == CorruptSegmentRecoveryDisposition.ExcludeAndSurface)
            {
                anyRowRejected = true;
                var exclusion = new CorruptSegmentExclusion(
                    entry.FileName, entry.Crc32C, entry.SizeBytes, observedCrc, observedSize, fileMissing);
                System.Diagnostics.Trace.TraceWarning(
                    "Recovery excluded segment '{0}' under ExcludeAndSurface ({1}; manifest=0x{2:x8}/{3} bytes, observed=0x{4:x8}/{5} bytes). " +
                    "Its key range is unserved until re-ingest; unlisted segment files are kept for this open.",
                    entry.FileName, fileMissing ? "file missing" : "integrity mismatch",
                    entry.Crc32C, entry.SizeBytes, observedCrc, observedSize);
                _onCorruptSegmentExcluded?.Invoke(exclusion);
                continue;
            }

            throw new InvalidDataException(
                $"Segment '{entry.FileName}' " +
                (fileMissing
                    ? "is listed in the manifest but missing from disk "
                    : "failed its integrity check ") +
                $"(manifest=0x{entry.Crc32C:x8}/{entry.SizeBytes} bytes, " +
                $"observed=0x{observedCrc:x8}/{observedSize} bytes). The segment holds " +
                "acknowledged data the engine can no longer trust; restore the file or " +
                "explicitly remove it (and its manifest row) before reopening. No segment file " +
                "was deleted by this open.");
        }

        return verified;
    }

    /// <summary>
    /// Replays WriteAheadLog entries into the MemTable. Delete entries are replayed into
    /// the tombstone set; Set entries into the MemTable. A Set followed by a Delete for the
    /// same key collapses to Delete in the recovered state. Reseeds the engine's seqno counter
    /// to <c>max(current, max(wal_entry.SequenceNumber))</c> so the first post-recovery
    /// <c>Set</c>/<c>Remove</c> assigns a strictly-greater seqno.
    /// </summary>
    private async Task ReplayWalAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entries = await _wal!.RecoverAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var entry in entries)
        {
            // Per-entry lock acquisition: the flusher Task is alive and may seal during
            // replay (a WAL big enough to fill the active rotates here, draining in
            // parallel into segments). Holding _lock for the entire replay would block the
            // flusher's commit step; per-entry release lets the flusher commit between WAL
            // entries.
            await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await EnsureActiveHasCapacityAsync(cancellationToken).ConfigureAwait(false);

                // max(current, entry.SequenceNumber): seqno counter is monotonic
                // non-decreasing across recovery.
                var current = Interlocked.Read(ref _lastAssignedSequenceNumber);
                var observed = (long)entry.SequenceNumber;
                if (observed > current)
                {
                    Interlocked.Exchange(ref _lastAssignedSequenceNumber, observed);
                }

                if (entry.IsTombstone)
                {
                    // Tombstone-set membership shadows any Set entry in the active during
                    // reads (TryProbeMemtableAsync); no explicit MemTable removal is
                    // needed. RedBlackTree (the default MemTable) does not support
                    // removal by design — the LSM-tree uses tombstones, not mutation,
                    // for deletes.
                    _active.Tombstones[entry.Key] = entry.SequenceNumber;
                    continue;
                }

                if (entry.ValueKind == ValueKind.ValueLogPointer)
                {
                    var pointer = entry.Pointer ?? throw new InvalidDataException(
                        "WAL Set+ValueLogPointer frame carried a null pointer; manifest is corrupt.");
                    if (_valueLogWriter is not null &&
                        _valueLogWriter.FileSizes.TryGetValue(pointer.FileIndex, out var fileSize) &&
                        pointer.Offset >= (ulong)fileSize)
                    {
                        throw new DanglingValueLogPointerException(
                            pointer.FileIndex, pointer.Offset, entry.SequenceNumber,
                            $"WAL replay references value-log entry at (file={pointer.FileIndex}, offset={pointer.Offset}) " +
                            $"past the durable EOF ({fileSize}); value-log entry was not fsync'd before the WAL frame.");
                    }
                    ValidateValueLogPointerKindByte(pointer);
                    await _active.Tree.SetAsync(
                        entry.Key,
                        MemTableValue<TValue>.ValueLogPointer(pointer, entry.SequenceNumber),
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await _active.Tree.SetAsync(
                        entry.Key,
                        MemTableValue<TValue>.Inline(entry.InlineValue!, entry.SequenceNumber),
                        cancellationToken).ConfigureAwait(false);
                }
                _active.Tombstones.Remove(entry.Key);
            }
            finally
            {
                _lock.Release();
            }
        }

        // Drain every seal triggered during replay so the engine returns to the caller in
        // steady state with _immutables.Count == 0 and a fully truncated WAL.
        await DrainAllInFlightAsync(cancellationToken).ConfigureAwait(false);
    }

    private void ValidateValueLogPointerKindByte(Boutquin.Storage.Domain.ValueObjects.ValueLog.ValueLogPointer pointer)
    {
        var path = ValueLogWriter.ResolveSegmentPath(_valueLogDirectory, pointer.FileIndex);
        if (!File.Exists(path))
        {
            return; // segment absent — dangling-pointer check above already handles durable EOF; skip here
        }

        using var fh = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        Span<byte> kindBuf = stackalloc byte[1];
        var read = RandomAccess.Read(fh, kindBuf, (long)pointer.Offset + 8);
        if (read == 1 && kindBuf[0] != ValueLogFrame.KindPayload && kindBuf[0] != ValueLogFrame.KindReserved)
        {
            throw new CorruptValueLogPointerException(
                pointer.FileIndex, pointer.Offset,
                $"WAL replay: value-log entry at (file={pointer.FileIndex}, offset={pointer.Offset}) " +
                $"carries unrecognised kind byte 0x{kindBuf[0]:X2} at frame+8; pointer is corrupt.");
        }
    }

    /// <summary>
    /// Releases resources used by the LSM storage engine.
    /// </summary>
    /// <remarks>
    /// <para>On-disk segment files are intentionally not deleted during disposal. They contain
    /// the durable data that was flushed from the MemTable and represent the persistent
    /// state of the storage engine. Segment cleanup is handled by <see cref="CompactAsync"/>
    /// or <see cref="ClearAsync"/>.</para>
    /// <para>This synchronous path blocks the calling thread on the value-log subsystem's
    /// async disposal (the file-stream closes are I/O). Prefer <see cref="DisposeAsync"/>
    /// (<c>await using</c>) to avoid the sync-over-async block.</para>
    /// </remarks>
    public void Dispose()
    {
        // First-caller-wins: only the caller that flips the gate 0→1 runs teardown.
        if (Interlocked.Exchange(ref _disposeGate, 1) != 0)
        {
            return;
        }

        ShutdownFlusherAndDrainSlots();
        ReleaseRemainingResources(
            disposeManifest: manifest => manifest.DisposeAsync().AsTask().GetAwaiter().GetResult(),
            disposeWriter: writer => writer.DisposeAsync().AsTask().GetAwaiter().GetResult());
    }

    /// <summary>
    /// Asynchronously releases resources used by the LSM storage engine, awaiting the
    /// value-log subsystem's <see cref="IAsyncDisposable.DisposeAsync"/> and the retired
    /// segment readers' clear rather than blocking the calling thread on those I/O closes.
    /// </summary>
    /// <remarks>
    /// On-disk segment files are not deleted during disposal — see <see cref="Dispose"/>.
    /// Use <c>await using</c> to consume this path.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        // First-caller-wins: only the caller that flips the gate 0→1 runs teardown. A
        // concurrent Dispose()/DisposeAsync() race would otherwise double-dispose the
        // SemaphoreSlims / CTS (finding F-38).
        if (Interlocked.Exchange(ref _disposeGate, 1) != 0)
        {
            return;
        }

        ShutdownFlusherAndDrainSlots();

        // Dispose retired-but-pinned segment readers asynchronously (their ClearAsync closes
        // the underlying StorageFile's FileStream — an I/O close).
        foreach (var reader in _retiredPinnedReaders.Values)
        {
            try
            {
                await reader.ClearAsync().ConfigureAwait(false);
            }
            catch (IOException)
            {
                // Best-effort handle release on teardown; an orphan file is reconciled at next start.
            }
        }
        _retiredPinnedReaders.Clear();

        foreach (var reader in _segments.OfType<IDisposable>())
        {
            reader.Dispose();
        }

        if (_valueLogWriter is { } writer)
        {
            await writer.DisposeAsync().ConfigureAwait(false);
            _valueLogWriter = null;
        }

        _valueLogReader?.Dispose();
        _valueLogReader = null;

        if (_valueLogManifest is { } manifest)
        {
            await manifest.DisposeAsync().ConfigureAwait(false);
            _valueLogManifest = null;
        }

        // _lock, _compactionGate, _gcGate and _ingestGate are deliberately not disposed: see the
        // _ingestLifetimeGate field remarks.
        _terminalStateCts.Dispose();
        _ingestOperationsDrained.Dispose();
    }

    // Shared teardown step 1: flip _disposed, stop the flusher, cancel queued slots' TCSs.
    // Idempotency is guaranteed by the _disposed short-circuit in the two public entry points.
    private void ShutdownFlusherAndDrainSlots()
    {
        lock (_ingestLifetimeGate)
        {
            _disposed = true;
        }
        _terminalStateCts.Cancel();

        // Cancel the flusher loop and await graceful exit. The loop observes cancellation
        // through the channel reader's ReadAllAsync(ct).
        _flusherCts.Cancel();
        try
        {
            _flusherLoop?.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // expected on dispose
        }
        catch (AggregateException ae) when (ae.InnerExceptions.All(e => e is OperationCanceledException))
        {
            // expected on dispose
        }
        _flusherCts.Dispose();
        _flushChannel.Writer.TryComplete();

        // Cancel every queued in-flight slot's Persisted TCS so any concurrent FlushAsync
        // whose Task.WhenAll captured them unblocks deterministically with
        // TaskCanceledException. TrySetCanceled is idempotent — slots already completed by
        // the flusher remain so. The flusher loop has already joined above, so it is the
        // only background mutator of _immutables and is gone; we take _lock anyway to fence
        // against any public mutator that passed its _disposed guard before the flip.
        _lock.Wait();
        try
        {
            foreach (var slot in _immutables)
            {
                slot.Persisted.TrySetCanceled();
            }
        }
        finally
        {
            _lock.Release();
        }

        // Every admitted writer/flush waiter can now observe either terminal cancellation or
        // the canceled slot task. Let each one reacquire and release _lock before teardown
        // disposes the synchronization primitives it is using.
        _ingestOperationsDrained.Wait();

        // A canceled caller can leave its shared recovery running after releasing its lease.
        // Join that task too: even a caller-owned WAL that ignores cancellation must finish
        // before Dispose returns and a successor engine can safely use the same folder.
        Task? initialisation;
        lock (_initialisationGate)
        {
            initialisation = _initialisation;
        }
        if (initialisation is not null)
        {
            try
            {
                initialisation.GetAwaiter().GetResult();
            }
            catch (Exception) when (initialisation.IsFaulted || initialisation.IsCanceled)
            {
                // Observe the cached recovery failure while still releasing resources. Public
                // recovery waiters receive it; OpenAsync also disposes here before rethrowing it.
            }
        }
    }

    // Shared teardown step 2 (synchronous Dispose path only): dispose the retired-but-pinned
    // segment readers (so their file handles are released, not leaked until finalization) and
    // release the value-log subsystem, using the supplied bridges to run the async disposals
    // synchronously. The async path (DisposeAsync) inlines the awaited equivalents instead.
    private void ReleaseRemainingResources(
        Action<ValueLogManifest> disposeManifest,
        Action<ValueLogWriter> disposeWriter)
    {
        // Dispose retired-but-pinned readers — Dispose previously skipped these, leaking the
        // StorageFile handle each wraps when the engine was torn down with a live snapshot pin.
        foreach (var reader in _retiredPinnedReaders.Values)
        {
            try
            {
                reader.ClearAsync().GetAwaiter().GetResult();
            }
            catch (IOException)
            {
                // Best-effort handle release on teardown; an orphan file is reconciled at next start.
            }
        }
        _retiredPinnedReaders.Clear();

        // Release the WiscKey value-log subsystem. The writer's DisposeAsync fsyncs any
        // pending bytes; after this point pointer dereferences fail with ObjectDisposed.
        foreach (var reader in _segments.OfType<IDisposable>())
        {
            reader.Dispose();
        }
        if (_valueLogWriter is { } writer)
        {
            disposeWriter(writer);
            _valueLogWriter = null;
        }

        _valueLogReader?.Dispose();
        _valueLogReader = null;

        if (_valueLogManifest is { } manifest)
        {
            disposeManifest(manifest);
            _valueLogManifest = null;
        }

        // _lock, _compactionGate, _gcGate and _ingestGate are deliberately not disposed: see the
        // _ingestLifetimeGate field remarks.
        _terminalStateCts.Dispose();
        _ingestOperationsDrained.Dispose();
    }
}

/// <summary>
/// Opt-in capability for an <see cref="ISegmentFormat{TKey, TValue}"/> that cannot persist every
/// entry the engine would otherwise accept. The engine calls <see cref="ValidateEntry"/> before it
/// acknowledges a write, so an entry the format would refuse at flush is refused up front instead
/// of being acknowledged and then faulting every later flush.
/// </summary>
/// <typeparam name="TKey">The key type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
internal interface ISegmentEntryAdmission<in TKey, in TValue>
{
    /// <summary>Throws <see cref="ArgumentException"/> when the format cannot persist the entry.</summary>
    /// <param name="key">The entry's key.</param>
    /// <param name="value">The entry's value.</param>
    void ValidateEntry(TKey key, TValue value);
}

/// <summary>
/// The engine-side admission check <see cref="LsmStorageEngine{TKey, TValue}.SetAsync"/> applies
/// before it acknowledges a write, exposed without the write. A caller that must commit an entry
/// somewhere else first (the CDC adapter's authoritative journal) runs it beforehand, so an entry
/// the engine would refuse is never committed where replay would refuse it again forever.
/// </summary>
/// <typeparam name="TKey">The key type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
internal interface ILsmEntryAdmissionCheck<in TKey, in TValue>
{
    /// <summary>Throws <see cref="ArgumentException"/> when the engine would refuse the entry.</summary>
    /// <param name="key">The entry's key.</param>
    /// <param name="value">The entry's value.</param>
    void ValidateEntryAdmission(TKey key, TValue value);
}

/// <summary>
/// Opt-in capability for an <see cref="ISegmentFormat{TKey, TValue}"/> whose writer refuses an
/// entry above a size ceiling. The engine knows what the format will store for a value — its
/// serialized bytes, or a value-log pointer once the value outgrows the routing threshold — so it
/// applies the ceiling before it acknowledges a write.
/// </summary>
internal interface ISegmentEntrySizeLimit
{
    /// <summary>The most serialized key bytes plus stored value bytes one entry may carry.</summary>
    long MaxEntryKeyAndValueBytes { get; }
}
