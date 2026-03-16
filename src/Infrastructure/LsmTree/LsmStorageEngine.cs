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
using Boutquin.Storage.Infrastructure.AppendOnlyFileStorage;
using Boutquin.Storage.Infrastructure.DataStructures;
using Boutquin.Storage.Infrastructure.KeyValueStore;

namespace Boutquin.Storage.Infrastructure.LsmTree;

/// <summary>
/// Log-Structured Merge-tree (LSM-tree) storage engine that orchestrates an in-memory MemTable
/// with on-disk segments for high write throughput.
/// </summary>
/// <typeparam name="TKey">The type of the keys in the store.</typeparam>
/// <typeparam name="TValue">The type of the values in the store.</typeparam>
/// <remarks>
/// <para>
/// <b>How it works:</b> Writes go to the in-memory MemTable (a RedBlackTree with a fixed capacity).
/// When the MemTable is full, it is automatically flushed to a new on-disk segment file
/// (an AppendOnlyFileStorageEngine). Reads check the MemTable first, then search on-disk segments
/// from newest to oldest (most recent data wins). This design optimizes for write throughput at
/// the cost of read amplification.
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
/// provided, its entries are replayed into the MemTable. This allows the engine to recover
/// its full state after a restart.
/// </para>
/// <para>
/// <b>Tombstones:</b> When <c>enableTombstones</c> is true, <see cref="RemoveAsync"/> marks keys
/// as deleted by recording them in an in-memory tombstone set rather than throwing
/// <see cref="NotSupportedException"/>. Tombstoned keys are excluded from reads and
/// <see cref="GetAllItemsAsync"/>. Tombstones are flushed alongside segment data and stripped
/// during compaction, reclaiming space.
/// </para>
/// <para>
/// <b>Range queries:</b> <see cref="GetRangeAsync"/> returns all key-value pairs within an
/// inclusive key range [startKey, endKey], merging results from the MemTable and all segments.
/// </para>
/// <para>
/// <b>Thread safety:</b> All public operations are serialized via a <see cref="SemaphoreSlim"/>
/// to ensure thread safety. This is a simple approach suitable for single-node deployments.
/// </para>
/// <para>
/// <b>Segment naming:</b> Each segment is named with a monotonically increasing counter
/// (e.g., prefix_000000.dat, prefix_000001.dat) to preserve creation order and enable
/// newest-first reads without relying on filesystem timestamps.
/// </para>
/// <para>
/// <b>Compaction:</b> Auto-compaction can be configured via a legacy integer <c>compactionThreshold</c>
/// or a pluggable <see cref="ICompactionStrategy"/> (strategy takes precedence when provided).
/// When triggered, all on-disk segments are merged into a single new segment using a sorted merge with
/// last-writer-wins deduplication. Tombstoned keys are stripped during compaction. This reduces
/// read amplification from O(k) to O(1) where k = number of segments. Old segment files are
/// deleted only after the compacted segment is fully written (crash-safe).
/// </para>
/// <para>
/// <b>Complexity:</b> Write: O(log M) amortized where M = MemTable capacity, with O(M) flush cost
/// when full. Read: O(M + S*N) worst case where S = number of segments and N = average entries per
/// segment. Compaction: O(N) where N = total entries across all segments — reduces S to 1, restoring
/// read performance to O(M + N).
/// </para>
/// </remarks>
public sealed class LsmStorageEngine<TKey, TValue> : ILsmStorageEngine<TKey, TValue>, IDisposable
    where TKey : ISerializable<TKey>, IComparable<TKey>, new()
    where TValue : ISerializable<TValue>, new()
{
    /// <summary>
    /// Default compaction threshold: compact when segment count reaches this value.
    /// A value of 0 disables auto-compaction (compaction must be triggered explicitly).
    /// </summary>
    public const int DefaultCompactionThreshold = 0;

    private readonly int _memTableCapacity;
    private readonly string _segmentFolder;
    private readonly string _segmentPrefix;
    private readonly IEntrySerializer<TKey, TValue> _entrySerializer;
    private readonly int _compactionThreshold;
    private readonly IWriteAheadLog<TKey, TValue>? _wal;
    private readonly bool _enableTombstones;
    private readonly Func<IBloomFilter<TKey>>? _bloomFilterFactory;
    private readonly ICompactionStrategy? _compactionStrategy;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private RedBlackTree<TKey, TValue> _memTable;
    private readonly List<AppendOnlyFileStorageEngine<TKey, TValue>> _segments = [];
    private int _segmentCounter;
    private bool _disposed;

    // Tombstone tracking: keys that have been deleted but may still exist in on-disk segments.
    private readonly HashSet<TKey> _memTableTombstones = [];
    private readonly List<HashSet<TKey>> _segmentTombstones = [];

    // Per-segment bloom filters for read optimization.
    private readonly List<IBloomFilter<TKey>?> _segmentBloomFilters = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="LsmStorageEngine{TKey, TValue}"/> class.
    /// </summary>
    /// <param name="memTableCapacity">
    /// The maximum number of entries the MemTable can hold before flushing to an SSTable.
    /// Recommended: 1,000–100,000 depending on entry size. Larger values reduce flush frequency
    /// (fewer SSTables, less compaction work) but increase memory usage and WriteAheadLog replay time on
    /// crash recovery. For entries averaging 1 KB, 10,000 entries ≈ 10 MB of memory.
    /// </param>
    /// <param name="segmentFolder">The directory where on-disk segment files are stored.</param>
    /// <param name="segmentPrefix">The prefix for segment file names.</param>
    /// <param name="entrySerializer">The serializer for key-value entries.</param>
    /// <param name="compactionThreshold">
    /// The number of segments that triggers automatic compaction after a flush. When the segment count
    /// reaches this value, all segments are merged into one. A value of 0 (the default) disables
    /// auto-compaction — compaction must be triggered explicitly via <see cref="CompactAsync"/>.
    /// </param>
    /// <param name="writeAheadLog">Optional WriteAheadLog for durability. If null, writes are not logged to a WriteAheadLog.</param>
    /// <param name="enableTombstones">
    /// When true, <see cref="RemoveAsync"/> records tombstones instead of throwing. Default is false
    /// for backward compatibility.
    /// </param>
    /// <param name="bloomFilterFactory">
    /// Optional factory to create per-segment bloom filters. When provided, each segment gets a bloom
    /// filter populated during flush. Reads skip segments where the bloom filter reports a definite miss.
    /// </param>
    /// <param name="compactionStrategy">
    /// Optional compaction strategy that determines when and which segments to compact. When provided,
    /// this takes precedence over <paramref name="compactionThreshold"/> for auto-compaction decisions.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="memTableCapacity"/> is less than 1 or <paramref name="compactionThreshold"/> is negative.</exception>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="segmentFolder"/>, <paramref name="segmentPrefix"/>, or <paramref name="entrySerializer"/> is null.</exception>
    public LsmStorageEngine(
        int memTableCapacity,
        string segmentFolder,
        string segmentPrefix,
        IEntrySerializer<TKey, TValue> entrySerializer,
        int compactionThreshold = DefaultCompactionThreshold,
        IWriteAheadLog<TKey, TValue>? writeAheadLog = null,
        bool enableTombstones = false,
        Func<IBloomFilter<TKey>>? bloomFilterFactory = null,
        ICompactionStrategy? compactionStrategy = null)
    {
        if (memTableCapacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(memTableCapacity), "MemTable capacity must be at least 1.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(compactionThreshold);

        Guard.AgainstNullOrDefault(() => segmentFolder);
        Guard.AgainstNullOrDefault(() => segmentPrefix);
        Guard.AgainstNullOrDefault(() => entrySerializer);

        _memTableCapacity = memTableCapacity;
        _segmentFolder = segmentFolder;
        _segmentPrefix = segmentPrefix;
        _entrySerializer = entrySerializer;
        _compactionThreshold = compactionThreshold;
        _wal = writeAheadLog;
        _enableTombstones = enableTombstones;
        _bloomFilterFactory = bloomFilterFactory;
        _compactionStrategy = compactionStrategy;
        _memTable = new RedBlackTree<TKey, TValue>(memTableCapacity);

        // Ensure the segment folder exists.
        if (!Directory.Exists(_segmentFolder))
        {
            Directory.CreateDirectory(_segmentFolder);
        }

        // Recovery: scan for existing segment files and load them in sorted order.
        RecoverSegmentsFromDisk();

        // Recovery: replay WriteAheadLog entries into the MemTable.
        if (_wal != null)
        {
            ReplayWal();
        }
    }

    /// <inheritdoc/>
    public int SegmentCount => _segments.Count;

    /// <inheritdoc/>
    public async Task SetAsync(TKey key, TValue value, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Guard.AgainstNullOrDefault(() => key);
        Guard.AgainstNullOrDefault(() => value);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Log to WriteAheadLog before applying to MemTable (write-ahead guarantee).
            if (_wal != null)
            {
                await _wal.AppendAsync(key, value, cancellationToken).ConfigureAwait(false);
            }

            // If the MemTable is full, flush it to disk before writing the new entry.
            if (_memTable.IsFull)
            {
                await FlushInternalAsync(cancellationToken).ConfigureAwait(false);
            }

            await _memTable.SetAsync(key, value, cancellationToken).ConfigureAwait(false);

            // If tombstones are enabled, remove any existing tombstone for this key
            // (a new write supersedes a previous delete).
            if (_enableTombstones)
            {
                _memTableTombstones.Remove(key);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<(TValue Value, bool Found)> TryGetValueAsync(TKey key, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
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
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
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
    /// </summary>
    private async Task<(TValue Value, bool Found)> TryGetValueInternalAsync(TKey key, CancellationToken cancellationToken)
    {
        // Check MemTable tombstones first — if the key is tombstoned, it's deleted.
        if (_enableTombstones && _memTableTombstones.Contains(key))
        {
            return (default!, false);
        }

        // Check the MemTable — it contains the most recent writes.
        var (value, found) = await _memTable.TryGetValueAsync(key, cancellationToken).ConfigureAwait(false);
        if (found)
        {
            return (value, true);
        }

        // Search on-disk segments from newest to oldest.
        for (var i = _segments.Count - 1; i >= 0; i--)
        {
            // Check segment tombstones before searching the segment data.
            if (_enableTombstones && i < _segmentTombstones.Count && _segmentTombstones[i].Contains(key))
            {
                return (default!, false);
            }

            // Bloom filter optimization: skip this segment if the bloom filter says
            // the key is definitely not present.
            if (i < _segmentBloomFilters.Count && _segmentBloomFilters[i] is { } bf && !bf.Contains(key))
            {
                continue;
            }

            var (segValue, segFound) = await _segments[i].TryGetValueAsync(key, cancellationToken).ConfigureAwait(false);
            if (segFound)
            {
                return (segValue, true);
            }
        }

        return (default!, false);
    }

    /// <inheritdoc/>
    public async Task RemoveAsync(TKey key, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_enableTombstones)
        {
            throw new NotSupportedException("Remove operation is not supported in an LSM-tree storage engine. Enable tombstones to support deletes.");
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _memTableTombstones.Add(key);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _memTable.ClearAsync(cancellationToken).ConfigureAwait(false);

            foreach (var segment in _segments)
            {
                await segment.ClearAsync(cancellationToken).ConfigureAwait(false);
            }

            _segments.Clear();
            _segmentTombstones.Clear();
            _segmentBloomFilters.Clear();
            _memTableTombstones.Clear();
            _segmentCounter = 0;
            _memTable = new RedBlackTree<TKey, TValue>(_memTableCapacity);

            if (_wal != null)
            {
                await _wal.TruncateAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<(TKey Key, TValue Value)>> GetAllItemsAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
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
    public async Task<IEnumerable<(TKey Key, TValue Value)>> GetRangeAsync(
        TKey startKey, TKey endKey, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var allItems = await GetAllItemsInternalAsync(cancellationToken).ConfigureAwait(false);
            return allItems
                .Where(item => item.Key.CompareTo(startKey) >= 0 && item.Key.CompareTo(endKey) <= 0)
                .ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Internal implementation of GetAllItemsAsync. Must be called while holding _lock.
    /// </summary>
    private async Task<List<(TKey Key, TValue Value)>> GetAllItemsInternalAsync(CancellationToken cancellationToken)
    {
        // Collect all tombstoned keys across MemTable and segments.
        HashSet<TKey>? allTombstones = null;
        if (_enableTombstones)
        {
            allTombstones = new HashSet<TKey>(_memTableTombstones);
            foreach (var segTombstones in _segmentTombstones)
            {
                foreach (var key in segTombstones)
                {
                    allTombstones.Add(key);
                }
            }
        }

        // Collect items from all segments (oldest first) then MemTable (newest).
        // Later entries overwrite earlier ones for the same key, so we process
        // oldest-to-newest and keep the last value per key.
        var merged = new SortedDictionary<TKey, TValue>();

        foreach (var segment in _segments)
        {
            var segItems = await segment.GetAllItemsAsync(cancellationToken).ConfigureAwait(false);
            foreach (var (key, value) in segItems)
            {
                merged[key] = value;
            }
        }

        var memItems = await _memTable.GetAllItemsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var (key, value) in memItems)
        {
            merged[key] = value;
        }

        // Filter out tombstoned keys.
        if (allTombstones is { Count: > 0 })
        {
            foreach (var tombstoned in allTombstones)
            {
                merged.Remove(tombstoned);
            }
        }

        return merged.Select(kvp => (kvp.Key, kvp.Value)).ToList();
    }

    /// <inheritdoc/>
    public async Task SetBulkAsync(IEnumerable<KeyValuePair<TKey, TValue>> items, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Guard.AgainstNullOrDefault(() => items);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var item in items)
            {
                if (_wal != null)
                {
                    await _wal.AppendAsync(item.Key, item.Value, cancellationToken).ConfigureAwait(false);
                }

                if (_memTable.IsFull)
                {
                    await FlushInternalAsync(cancellationToken).ConfigureAwait(false);
                }

                await _memTable.SetAsync(item.Key, item.Value, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await FlushInternalAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task CompactAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await CompactInternalAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Internal compaction implementation. Must be called while holding _lock.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Algorithm:</b> Reads all entries from every on-disk segment (oldest to newest),
    /// performs a sorted merge with last-writer-wins deduplication, strips tombstoned keys,
    /// writes the result to a new segment file, then deletes the old segment files.
    /// </para>
    /// <para>
    /// <b>Crash safety:</b> The new compacted segment is fully written before any old segment
    /// is deleted. If a crash occurs during compaction:
    /// - Before new segment is written: old segments are intact, no data loss.
    /// - After new segment, before old deletion: extra segment on disk, but all data is valid.
    /// </para>
    /// </remarks>
    private async Task CompactInternalAsync(CancellationToken cancellationToken)
    {
        // Nothing to compact if fewer than 2 segments.
        if (_segments.Count < 2)
        {
            return;
        }

        // Phase 1: Read all entries from all segments, merging with last-writer-wins.
        var merged = new SortedDictionary<TKey, TValue>();
        foreach (var segment in _segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var segItems = await segment.GetAllItemsAsync(cancellationToken).ConfigureAwait(false);
            foreach (var (key, value) in segItems)
            {
                merged[key] = value;
            }
        }

        // Strip tombstoned keys during compaction.
        if (_enableTombstones)
        {
            // Collect all segment tombstones.
            var allSegmentTombstones = new HashSet<TKey>();
            foreach (var segTombstones in _segmentTombstones)
            {
                foreach (var key in segTombstones)
                {
                    allSegmentTombstones.Add(key);
                }
            }

            // Also include MemTable tombstones (a delete may not have been flushed yet).
            foreach (var key in _memTableTombstones)
            {
                allSegmentTombstones.Add(key);
            }

            foreach (var tombstoned in allSegmentTombstones)
            {
                merged.Remove(tombstoned);
            }
        }

        // Phase 2: Write merged entries to a new segment.
        var compactedFileName = $"{_segmentPrefix}_{_segmentCounter:D6}.dat";
        var compactedStorageFile = new StorageFile(_segmentFolder, compactedFileName);
        var compactedSegment = new AppendOnlyFileStorageEngine<TKey, TValue>(compactedStorageFile, _entrySerializer);

        if (merged.Count > 0)
        {
            await compactedSegment.SetBulkAsync(
                merged.Select(kvp => new KeyValuePair<TKey, TValue>(kvp.Key, kvp.Value)),
                cancellationToken).ConfigureAwait(false);
        }

        // Build bloom filter for the compacted segment if a factory is provided.
        IBloomFilter<TKey>? compactedBf = null;
        if (_bloomFilterFactory != null && merged.Count > 0)
        {
            compactedBf = _bloomFilterFactory();
            foreach (var kvp in merged)
            {
                compactedBf.Add(kvp.Key);
            }
        }

        // Phase 3: Delete old segment files and replace the segment list.
        var oldSegments = _segments.ToList();
        _segments.Clear();
        _segmentTombstones.Clear();
        _segmentBloomFilters.Clear();
        _segments.Add(compactedSegment);
        _segmentTombstones.Add(new HashSet<TKey>());
        _segmentBloomFilters.Add(compactedBf);
        _segmentCounter++;

        foreach (var oldSegment in oldSegments)
        {
            await oldSegment.ClearAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Internal flush implementation. Must be called while holding _lock.
    /// </summary>
    private async Task FlushInternalAsync(CancellationToken cancellationToken)
    {
        var items = await _memTable.GetAllItemsAsync(cancellationToken).ConfigureAwait(false);
        var itemList = items.ToList();

        // Nothing to flush if the MemTable is empty and there are no tombstones.
        if (itemList.Count == 0 && _memTableTombstones.Count == 0)
        {
            return;
        }

        if (itemList.Count > 0)
        {
            // Create a new segment file with a monotonically increasing counter.
            var segmentFileName = $"{_segmentPrefix}_{_segmentCounter:D6}.dat";
            var storageFile = new StorageFile(_segmentFolder, segmentFileName);
            var segment = new AppendOnlyFileStorageEngine<TKey, TValue>(storageFile, _entrySerializer);

            // Write all MemTable entries to the new segment.
            await segment.SetBulkAsync(
                itemList.Select(i => new KeyValuePair<TKey, TValue>(i.Key, i.Value)),
                cancellationToken).ConfigureAwait(false);

            _segments.Add(segment);
            _segmentTombstones.Add(new HashSet<TKey>(_memTableTombstones));

            // Populate bloom filter for this segment if a factory is provided.
            if (_bloomFilterFactory != null)
            {
                var bf = _bloomFilterFactory();
                foreach (var (key, _) in itemList)
                {
                    bf.Add(key);
                }
                _segmentBloomFilters.Add(bf);
            }
            else
            {
                _segmentBloomFilters.Add(null);
            }

            _segmentCounter++;
        }
        else if (_memTableTombstones.Count > 0)
        {
            // Tombstones only, no data — attach tombstones to the newest existing segment.
            if (_segmentTombstones.Count > 0)
            {
                var newest = _segmentTombstones[^1];
                foreach (var key in _memTableTombstones)
                {
                    newest.Add(key);
                }
            }
        }

        // Reset the MemTable and tombstones.
        await _memTable.ClearAsync(cancellationToken).ConfigureAwait(false);
        _memTable = new RedBlackTree<TKey, TValue>(_memTableCapacity);
        _memTableTombstones.Clear();

        // Truncate WriteAheadLog after successful flush — the data is now safely on disk.
        if (_wal != null)
        {
            await _wal.TruncateAsync(cancellationToken).ConfigureAwait(false);
        }

        // Auto-compact: strategy takes precedence over legacy threshold.
        var shouldCompact = _compactionStrategy != null
            ? _compactionStrategy.ShouldCompact(_segments.Count)
            : _compactionThreshold > 0 && _segments.Count >= _compactionThreshold;

        if (shouldCompact)
        {
            await CompactInternalAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Scans the segment folder for existing segment files matching the prefix pattern
    /// and loads them in sorted order. Called during construction for recovery.
    /// </summary>
    private void RecoverSegmentsFromDisk()
    {
        var pattern = $"{_segmentPrefix}_*.dat";
        var existingFiles = Directory.GetFiles(_segmentFolder, pattern);

        if (existingFiles.Length == 0)
        {
            return;
        }

        // Sort by name to maintain creation order (names contain zero-padded counters).
        Array.Sort(existingFiles, StringComparer.Ordinal);

        foreach (var filePath in existingFiles)
        {
            var fileName = Path.GetFileName(filePath);
            var storageFile = new StorageFile(_segmentFolder, fileName);
            var segment = new AppendOnlyFileStorageEngine<TKey, TValue>(storageFile, _entrySerializer);
            _segments.Add(segment);
            _segmentTombstones.Add(new HashSet<TKey>());
            _segmentBloomFilters.Add(null); // Recovered segments don't have bloom filters in memory
        }

        // Set the segment counter past the highest existing file to avoid name collisions.
        // Parse the counter from the last file name (format: prefix_NNNNNN.dat).
        var lastFileName = Path.GetFileNameWithoutExtension(existingFiles[^1]);
        var counterPart = lastFileName[(lastFileName.LastIndexOf('_') + 1)..];
        if (int.TryParse(counterPart, out var lastCounter))
        {
            _segmentCounter = lastCounter + 1;
        }
        else
        {
            _segmentCounter = existingFiles.Length;
        }
    }

    /// <summary>
    /// Replays WriteAheadLog entries into the MemTable. Called during construction for crash recovery.
    /// </summary>
    private void ReplayWal()
    {
        // RecoverAsync is async but we need it in the constructor. Use GetAwaiter().GetResult()
        // since this is a one-time startup operation and the WriteAheadLog's semaphore is not yet contended.
        var entries = _wal!.RecoverAsync().GetAwaiter().GetResult();

        foreach (var (key, value) in entries)
        {
            // Replay into MemTable synchronously. If the MemTable fills up during replay,
            // we flush synchronously — again acceptable during startup.
            if (_memTable.IsFull)
            {
                FlushInternalAsync(CancellationToken.None).GetAwaiter().GetResult();
            }

            _memTable.SetAsync(key, value).GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// Releases resources used by the LSM storage engine.
    /// </summary>
    /// <remarks>
    /// On-disk segment files are intentionally not deleted during disposal. They contain
    /// the durable data that was flushed from the MemTable and represent the persistent
    /// state of the storage engine. Segment cleanup is handled by <see cref="CompactAsync"/>
    /// or <see cref="ClearAsync"/>.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _lock.Dispose();
        _disposed = true;
    }
}
