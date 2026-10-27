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
namespace Boutquin.Storage.Infrastructure.LogSegmentFileStorage;

/// <summary>
/// A hash-indexed segmented log: writes append to the current segment, full segments roll over to new
/// ones, and a periodic <see cref="CompactAsync(System.Threading.CancellationToken)"/> collapses
/// superseded values. Keys are kept in insertion order (never sorted), and point lookups resolve
/// through the per-segment index of the injected segment engine.
/// </summary>
/// <typeparam name="TKey">The type of the keys in the store.</typeparam>
/// <typeparam name="TValue">The type of the values in the store.</typeparam>
/// <remarks>
/// <para>
/// <b>What this is — the Bitcask-family engine.</b> This is the append-log counterpart to
/// <see cref="Boutquin.Storage.Infrastructure.LsmTree.LsmStorageEngine{TKey, TValue}"/>; the two cover
/// the two storage-engine families Kleppmann contrasts in 2e Ch. 4 — the hash-indexed log (Bitcask) and
/// the LSM-tree. Because segments are unsorted, there is no global key order, no sparse block index,
/// and no per-segment Bloom filter; those belong to the LSM engine. The advantage the append log
/// trades for is a maximally simple, sequential write path and a direct hash-then-seek point read.
/// </para>
/// <para>
/// <b>When to prefer this over the LSM engine.</b> The append-log shape suits a workload that is all
/// three of:
/// <list type="bullet">
/// <item><description><b>point-lookup only</b> — get/put by exact key, never a range scan, ordered
/// iteration, or prefix query; the unsorted layout cannot serve those efficiently;</description></item>
/// <item><description><b>bounded in distinct keys</b> so the key→offset index fits in memory, yet
/// <b>high-churn</b> with many overwrites per key — the canonical Bitcask case (e.g. a hit-count or
/// latest-value-per-key store);</description></item>
/// <item><description><b>latency-predictable and append-heavy</b> — it wants the simplest write path
/// and a read path free of the LSM engine's compaction amplification and multi-level walk.</description></item>
/// </list>
/// Typical consumers: a latest-value device-shadow / session store, a counter or aggregate store, or a
/// small hot metadata cache. Reach for
/// <see cref="Boutquin.Storage.Infrastructure.LsmTree.LsmStorageEngine{TKey, TValue}"/> instead the
/// moment the workload needs range or ordered scans, snapshot isolation, a keyspace larger than the
/// in-memory index can hold, or time-series / analytical iteration.
/// </para>
/// <para>
/// <b>Limitation 1 — not durable across process restart.</b> The live segment set lives only in
/// memory: the constructor starts with a single fresh segment and does <b>not</b> read back segment
/// files left on disk by a previous instance (they are orphaned). There is no manifest and no
/// recovery, so <see cref="CompactAsync(System.Threading.CancellationToken)"/> is not crash-atomic in any
/// meaningful sense — a crash loses the in-memory segment list regardless. In-process, compaction
/// writes its output into fresh segments before it deletes the inputs, so a cancelled or faulted
/// compaction leaves the live segment set, and every acknowledged key, intact.
/// Durable, crash-recoverable segmented storage is the role of
/// <see cref="Boutquin.Storage.Infrastructure.LsmTree.LsmStorageEngine{TKey, TValue}"/>, whose
/// <c>CURRENT</c> manifest is the durable commit point for its segment set. Do not use this type where
/// data must survive a restart.
/// </para>
/// <para>
/// <b>Limitation 2 — a partial Bitcask.</b> A canonical Bitcask keeps a single global
/// key→(segment, offset) index for O(1) reads regardless of segment count. This engine keeps a
/// <b>per-segment</b> index and probes segments newest-first until a hit, so a point read — especially
/// a miss — costs up to O(segment count) index probes. Compaction mitigates by reducing the segment
/// count, but the read path is not the single-lookup Bitcask read.
/// </para>
/// <para>
/// <b>Limitation 3 — read cost depends on the injected segment factory.</b> The hash-index benefit
/// only materializes when the factory builds indexed segments
/// (<see cref="Boutquin.Storage.Infrastructure.AppendOnlyFileStorage.AppendOnlyFileStorageEngineWithIndex{TKey, TValue}"/>).
/// With a plain
/// <see cref="Boutquin.Storage.Infrastructure.AppendOnlyFileStorage.AppendOnlyFileStorageEngine{TKey, TValue}"/>
/// factory each segment read is a full O(n) scan of that segment file, which is strictly slower than
/// the LSM engine's indexed read.
/// </para>
/// <para>
/// <b>Why segmented storage?</b> A single append-only file grows without bound, making compaction
/// expensive (must rewrite the entire file). Segmentation splits the log into fixed-size files. Each
/// segment can be compacted or deleted independently, and old segments can be garbage-collected once
/// compacted. This is the segmented log pattern from Kleppmann 2e Ch. 4.
/// </para>
/// <para>
/// <b>Why a Stack for segments?</b> Segments are searched newest-first (LIFO order) because in an
/// append-only system, the most recent segment is most likely to contain the latest value for a key.
/// A Stack naturally provides this ordering — the current segment is always on top, and iteration
/// proceeds from newest to oldest. This minimizes unnecessary I/O for hot keys.
/// </para>
/// <para>
/// <b>Why a factory function for segment creation?</b> The engine needs to create new segments
/// dynamically as existing ones fill up. A factory function decouples segment creation from the engine,
/// allowing callers to inject different storage engine implementations (e.g., with or without indexing,
/// with different serializers). This is the Strategy pattern applied to segment instantiation.
/// </para>
/// </remarks>
public sealed class LogSegmentedStorageEngine<TKey, TValue> :
    ILogSegmentedStorageEngine<TKey, TValue>
    where TKey : IStorageKey<TKey>, new()
    where TValue : ISerializable<TValue>, new()
{
    private readonly string _folder;
    private readonly string _prefix;
    private readonly long _maxSegmentSize;
    private readonly Func<string, string, IEntrySerializer<TKey, TValue>, long, IFileBasedStorageEngine<TKey, TValue>> _storageEngineFactory;
    private IFileBasedStorageEngine<TKey, TValue> _currentSegment;
    private readonly Stack<IFileBasedStorageEngine<TKey, TValue>> _segments;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private long _segmentSequence;

    /// <summary>
    /// Initializes a new instance of the <see cref="LogSegmentedStorageEngine{TKey,TValue}"/> class.
    /// </summary>
    /// <param name="entrySerializer">The serializer to use for serializing and deserializing entries.</param>
    /// <param name="folder">The folder where segment files are stored.</param>
    /// <param name="prefix">The prefix used for segment file names.</param>
    /// <param name="maxSegmentSize">The maximum size of a segment before a new one is created.</param>
    /// <param name="storageEngineFactory">The factory method to create instances of <see cref="IFileBasedStorageEngine{TKey,TValue}"/>.</param>
    public LogSegmentedStorageEngine(
        IEntrySerializer<TKey, TValue> entrySerializer,
        string folder,
        string prefix,
        long maxSegmentSize,
        Func<string, string, IEntrySerializer<TKey, TValue>, long, IFileBasedStorageEngine<TKey, TValue>> storageEngineFactory)
    {
        EntrySerializer = entrySerializer ?? throw new ArgumentNullException(nameof(entrySerializer));
        _folder = folder ?? throw new ArgumentNullException(nameof(folder));
        _prefix = prefix ?? throw new ArgumentNullException(nameof(prefix));
        _maxSegmentSize = maxSegmentSize > 0 ? maxSegmentSize : throw new ArgumentOutOfRangeException(nameof(maxSegmentSize));
        _storageEngineFactory = storageEngineFactory ?? throw new ArgumentNullException(nameof(storageEngineFactory));
        _segments = new Stack<IFileBasedStorageEngine<TKey, TValue>>();

        EnsureDirectoryExists(_folder);

        _currentSegment = CreateNewSegment();
        _segments.Push(_currentSegment);
    }

    /// <inheritdoc/>
    public IEntrySerializer<TKey, TValue> EntrySerializer { get; }

    /// <inheritdoc/>
    public async Task SetAsync(TKey key, TValue value, CancellationToken cancellationToken = default)
    {
        Guard.AgainstNullOrDefault(() => key);
        Guard.AgainstNullOrDefault(() => value);
        cancellationToken.ThrowIfCancellationRequested();

        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_currentSegment.FileSize >= _maxSegmentSize)
            {
                _currentSegment = CreateNewSegment();
                _segments.Push(_currentSegment);
            }

            await _currentSegment.SetAsync(key, value, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Test seam: parks a reader between its segment-list snapshot and the segment probes so
    /// the read-vs-compaction interleaving can be reproduced deterministically. Null in
    /// production.
    /// </summary>
    internal Func<Task>? ReadSnapshotIterateHook { get; set; }

    /// <inheritdoc/>
    public async Task<(TValue Value, bool Found)> TryGetValueAsync(TKey key, CancellationToken cancellationToken = default)
    {
        Guard.AgainstNullOrDefault(() => key);
        cancellationToken.ThrowIfCancellationRequested();

        // Why search all segments, not just current? A key may have been written to an older segment
        // before the current one was created. Segments are iterated newest-first (Stack order), so the
        // first match found is guaranteed to be the most recent value.
        //
        // Why hold the semaphore across the probes? CompactAsync DELETES its input segment
        // files (segment names are never reused), so a reader iterating a pre-compaction
        // snapshot outside the lock probes missing files — which read as empty — and returns
        // a silent false negative for a durably-present key. Serialising reads with mutators
        // keeps the segment files alive for the duration of every probe (DDIA 2e Ch. 4:
        // merged input segments serve reads until the merge output replaces them).
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (ReadSnapshotIterateHook is { } snapshotIterateHook)
            {
                await snapshotIterateHook().ConfigureAwait(false);
            }

            foreach (var segment in _segments)
            {
                var (value, found) = await segment.TryGetValueAsync(key, cancellationToken).ConfigureAwait(false);
                if (found)
                {
                    return (value, true);
                }
            }

            return (default!, false);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<bool> ContainsKeyAsync(TKey key, CancellationToken cancellationToken = default)
    {
        Guard.AgainstNullOrDefault(() => key);
        cancellationToken.ThrowIfCancellationRequested();

        // Serialised with mutators for the same reason as TryGetValueAsync — compaction
        // deletes input segment files, and a probe of a deleted file is a silent miss.
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var segment in _segments)
            {
                var containsKey = await segment.ContainsKeyAsync(key, cancellationToken).ConfigureAwait(false);
                if (containsKey)
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <inheritdoc/>
    public Task RemoveAsync(TKey key, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("Remove operation is not supported in an append-only storage engine.");
    }

    /// <inheritdoc/>
    public async Task SetBulkAsync(IEnumerable<KeyValuePair<TKey, TValue>> items, CancellationToken cancellationToken = default)
    {
        Guard.AgainstEmptyOrNullEnumerable(() => items);
        cancellationToken.ThrowIfCancellationRequested();

        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SetBulkInternalAsync(items, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task SetBulkInternalAsync(IEnumerable<KeyValuePair<TKey, TValue>> items, CancellationToken cancellationToken)
    {
        var itemsList = items.ToList();

        // Validate the whole batch before the first write: each segment SetAsync is durable on
        // return, so rejecting a later item after writing earlier ones left a torn, half-applied
        // batch behind the thrown exception.
        foreach (var item in itemsList)
        {
            Guard.AgainstNullOrDefault(() => item.Key);
            Guard.AgainstNullOrDefault(() => item.Value);
        }

        if (itemsList.Count == 0)
        {
            return;
        }

        // A later append can fail after earlier appends are durable. Keep this batch detached
        // until all writes succeed, so cancellation or I/O failure cannot expose its prefix.
        // Existing segments remain authoritative; compaction later reclaims overwritten values.
        var pending = new List<IFileBasedStorageEngine<TKey, TValue>>();
        try
        {
            await WriteToNewSegmentsAsync(
                itemsList.Select(item => (item.Key, item.Value)).ToList(), pending, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch
        {
            await DeleteSegmentsAsync(pending).ConfigureAwait(false);
            throw;
        }

        foreach (var segment in pending)
        {
            _segments.Push(segment);
        }
        _currentSegment = _segments.Peek();
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The returned stream is CHRONOLOGICAL (oldest segment first, append order within each
    /// segment), so the append-log reading discipline — "the last occurrence of a key reflects
    /// the current state" — holds across segment boundaries. The snapshot is a Stack array
    /// (newest-first); without the reverse, a caller's last-occurrence dedup would resolve a
    /// key written in several segments to its OLDEST value, inverting last-writer-wins
    /// (DDIA 2e Ch. 4: keep only the more recent value).
    /// </remarks>
    public async Task<IEnumerable<(TKey Key, TValue Value)>> GetAllItemsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Serialised with mutators for the same reason as TryGetValueAsync — compaction
        // deletes input segment files, and reading a deleted file yields an empty segment,
        // silently dropping rows from the result.
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = _segments.ToArray();
            Array.Reverse(snapshot);
            return await GetAllItemsFromSnapshotAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Reads all items from the supplied snapshot. Callers that already hold the
    /// semaphore (e.g., <see cref="CompactAsync"/>) should call this directly with
    /// a pre-captured snapshot rather than re-acquiring the non-reentrant semaphore.
    /// </summary>
    private static async Task<IEnumerable<(TKey Key, TValue Value)>> GetAllItemsFromSnapshotAsync(
        IFileBasedStorageEngine<TKey, TValue>[] snapshot,
        CancellationToken cancellationToken)
    {
        var allItems = new List<(TKey Key, TValue Value)>();
        foreach (var segment in snapshot)
        {
            var segmentItems = await segment.GetAllItemsAsync(cancellationToken).ConfigureAwait(false);
            allItems.AddRange(segmentItems);
        }
        return allItems;
    }

    /// <inheritdoc/>
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ClearInternalAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private Task ClearInternalAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Detach-then-delete: publish the empty segment set first, then delete the detached
        // segments on CancellationToken.None. Deleting the live set in place (newest-first, on
        // the caller's token) let a cancellation or I/O fault after the first delete leave an
        // OLDER segment live — resurrecting a value the deleted newer segment had superseded.
        var detached = _segments.ToArray();
        var fresh = CreateNewSegment();
        _segments.Clear();
        _currentSegment = fresh;
        _segments.Push(_currentSegment);

        return DeleteSegmentsAsync(detached);
    }

    /// <inheritdoc/>
    public async Task CompactAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Use the snapshot helper directly — we already hold the non-reentrant semaphore,
            // so calling the public GetAllItemsAsync (which re-acquires it) would deadlock.
            //
            // Why Reverse()? _segments is a Stack, so ToArray() yields segments newest-first, while
            // GetAllItemsFromSnapshotAsync preserves each segment's internal append (oldest-first)
            // order. Without reversing, the flattened stream is "newest segment first, oldest write
            // first within a segment" — a non-chronological order in which GroupBy(...).Last() would
            // pick the OLDEST segment's value for a key written across segments, inverting
            // last-writer-wins. Reversing to oldest-segment-first makes the whole stream chronological
            // so g.Last() is the genuinely most recent value (DDIA Ch. 4 — keep only the more recent
            // value when a key appears in several segments).
            var snapshot = _segments.ToArray();
            Array.Reverse(snapshot);
            var allItems = await GetAllItemsFromSnapshotAsync(snapshot, cancellationToken).ConfigureAwait(false);
            var latestItems = allItems.GroupBy(x => x.Key).Select(g => g.Last()).ToList();

            // Build-then-swap: write the compacted set into fresh segments while the inputs stay
            // live, publish it, and only then delete the inputs. Deleting first left the
            // in-memory latestItems as the only copy of the data, so a cancellation or I/O fault
            // mid-rewrite lost acknowledged keys (DDIA 2e Ch. 4: merged input segments serve
            // reads until the merge output replaces them).
            var rewritten = new List<IFileBasedStorageEngine<TKey, TValue>>();
            try
            {
                await WriteToNewSegmentsAsync(latestItems, rewritten, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch
            {
                await DeleteSegmentsAsync(rewritten).ConfigureAwait(false);
                throw;
            }

            _segments.Clear();
            foreach (var segment in rewritten)
            {
                _segments.Push(segment);
            }
            _currentSegment = _segments.Peek();
            await DeleteSegmentsAsync(snapshot).ConfigureAwait(false);

            await MergeSmallSegmentsAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Merges small segments into larger ones if possible.
    /// </summary>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    private async Task MergeSmallSegmentsAsync(CancellationToken cancellationToken)
    {
        // Oldest-first (a Stack enumerates newest-first). The original design gathered ALL small
        // segments regardless of position and merged them into a single batch, then placed the
        // entire merged output on top of all retained full segments. That is incorrect when a full
        // segment sits chronologically between two small segments: merging S1-small + S3-small into
        // one block and pushing that block on top of S2-full makes S1's stale values visible before
        // S2's newer values — a last-writer-wins inversion. The fix: process the stack slot-by-slot
        // in oldest-to-newest order and merge only consecutive runs of small segments, placing each
        // run's merged output back in the run's original chronological position.

        // originalOrder is oldest-first (Reverse() of newest-first Stack enumeration).
        var originalOrder = _segments.Reverse().ToList();

        var anySmall = originalOrder.Any(s => s.FileSize < _maxSegmentSize);
        if (!anySmall)
        {
            // Nothing to merge — creating a fresh segment here would just push a dead,
            // empty segment onto the stack on every compaction.
            return;
        }

        // Walk the original sequence, collecting consecutive small-segment runs and flushing each
        // run's merged output before emitting the next full segment. Full segments pass through
        // unchanged. The result list is in oldest-first order; we push it oldest-first so the newest
        // segment lands on top of the Stack (newest-first read order).
        var outputOrder = new List<IFileBasedStorageEngine<TKey, TValue>>();

        IFileBasedStorageEngine<TKey, TValue>? currentMergedSegment = null;
        long currentMergedSize = 0;
        var currentRunMergedSegments = new List<IFileBasedStorageEngine<TKey, TValue>>();
        var currentRunSources = new List<IFileBasedStorageEngine<TKey, TValue>>();

        // Merged sources are deleted only after the rebuilt stack is published, and the merge
        // outputs are deleted if the merge aborts — the sources stay authoritative until then,
        // so a cancelled or faulted merge cannot lose an already-merged run's keys.
        var createdSegments = new List<IFileBasedStorageEngine<TKey, TValue>>();
        var retiredSources = new List<IFileBasedStorageEngine<TKey, TValue>>();

        void FlushCurrentRun()
        {
            if (currentRunSources.Count == 0)
            {
                return;
            }

            // Emit the merged segments produced for this consecutive run, oldest-first.
            outputOrder.AddRange(currentRunMergedSegments);
            retiredSources.AddRange(currentRunSources);

            currentRunMergedSegments.Clear();
            currentRunSources.Clear();
            currentMergedSegment = null;
            currentMergedSize = 0;
        }

        try
        {
            foreach (var segment in originalOrder)
            {
                if (segment.FileSize < _maxSegmentSize)
                {
                    // This segment is small — add it to the current consecutive run.
                    // Running size of the merge-target segment, tracked in memory from each write's
                    // bytes-written (the segment's cached post-write size) instead of re-serializing
                    // every item solely to measure it — the same single-serialize discipline as
                    // SetBulkInternalAsync.
                    if (currentMergedSegment == null)
                    {
                        currentMergedSegment = CreateNewSegment();
                        createdSegments.Add(currentMergedSegment);
                        currentRunMergedSegments.Add(currentMergedSegment);
                        currentMergedSize = currentMergedSegment.FileSize;
                    }

                    var segmentItems = await segment.GetAllItemsAsync(cancellationToken).ConfigureAwait(false);

                    foreach (var (Key, Value) in segmentItems)
                    {
                        if (currentMergedSize >= _maxSegmentSize)
                        {
                            currentMergedSegment = CreateNewSegment();
                            createdSegments.Add(currentMergedSegment);
                            currentRunMergedSegments.Add(currentMergedSegment);
                            currentMergedSize = 0;
                        }

                        await currentMergedSegment.SetAsync(Key, Value, cancellationToken).ConfigureAwait(false);
                        currentMergedSize = currentMergedSegment.FileSize;
                    }

                    currentRunSources.Add(segment);
                }
                else
                {
                    // This segment is full — flush any in-progress small run first, preserving its
                    // chronological position, then emit the full segment in its own position.
                    FlushCurrentRun();
                    outputOrder.Add(segment);
                }
            }

            // Flush any trailing run of small segments at the end of the stack (the common case
            // after compaction — the tail segment is small).
            FlushCurrentRun();
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch
        {
            await DeleteSegmentsAsync(createdSegments).ConfigureAwait(false);
            throw;
        }

        // Rebuild the stack from the output list. Push oldest-first so the newest segment
        // (last in outputOrder) ends up on top of the Stack (newest-first read order).
        _segments.Clear();
        foreach (var segment in outputOrder)
        {
            _segments.Push(segment);
        }

        // The pre-merge current segment was small by construction (it was just rewritten),
        // so it was merged and is retired below. Point the write cursor at the newest
        // live segment — leaving it on the deleted segment wedges every subsequent write
        // on a FileNotFoundException.
        _currentSegment = _segments.Peek();

        await DeleteSegmentsAsync(retiredSources).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes <paramref name="items"/> into freshly created segments, rolling over at the
    /// segment-size limit. Every segment created is appended to <paramref name="output"/>
    /// (oldest-first) as soon as it exists, so a caller can delete them if the write aborts.
    /// None of them is published to the live segment set here.
    /// </summary>
    private async Task WriteToNewSegmentsAsync(
        List<(TKey Key, TValue Value)> items,
        List<IFileBasedStorageEngine<TKey, TValue>> output,
        CancellationToken cancellationToken)
    {
        var segment = CreateNewSegment();
        output.Add(segment);
        var segmentSize = segment.FileSize;

        foreach (var (key, value) in items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (segmentSize >= _maxSegmentSize)
            {
                segment = CreateNewSegment();
                output.Add(segment);
                segmentSize = 0;
            }

            await segment.SetAsync(key, value, cancellationToken).ConfigureAwait(false);
            segmentSize = segment.FileSize;
        }
    }

    /// <summary>
    /// Deletes the files of segments that are not (or are no longer) part of the live set.
    /// </summary>
    /// <remarks>
    /// Runs with <see cref="CancellationToken.None"/>: it is either the cleanup of an aborted
    /// rewrite or the retirement of inputs whose replacement is already published, and abandoning
    /// it half-way on the caller's token would only leave orphaned files behind.
    /// </remarks>
    private static async Task DeleteSegmentsAsync(IEnumerable<IFileBasedStorageEngine<TKey, TValue>> segments)
    {
        foreach (var segment in segments)
        {
            await segment.ClearAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Creates a new segment file for storing key-value pairs.
    /// </summary>
    /// <returns>A new instance of <see cref="IFileBasedStorageEngine{TKey,TValue}"/>.</returns>
    private IFileBasedStorageEngine<TKey, TValue> CreateNewSegment()
    {
        var segmentFileName = GenerateSegmentFileName();
        return _storageEngineFactory(_folder, segmentFileName, EntrySerializer, _maxSegmentSize);
    }

    /// <summary>
    /// Generates a file path for a new segment file.
    /// </summary>
    /// <returns>The generated segment file path.</returns>
    private string GenerateSegmentFileName()
    {
        // Why timestamp + counter + GUID? The timestamp and the per-instance counter order this
        // instance's segments, but neither is unique ACROSS instances: two engines on one
        // folder+prefix constructed in the same millisecond both start their counter at 1 and
        // generate the same name, and the segment factory adopts an existing file — so one
        // engine's clear or compaction deleted the other's live segment. The random GUID makes
        // every name unique across instances and processes.
        var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture);
        var sequence = Interlocked.Increment(ref _segmentSequence);
        return $"{_prefix}_segment_{timestamp}_{sequence}_{Guid.NewGuid():N}.log";
    }

    /// <summary>
    /// Ensures that the specified directory exists.
    /// </summary>
    /// <param name="directoryPath">The path of the directory.</param>
    private static void EnsureDirectoryExists(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }
    }
}
