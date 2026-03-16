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
/// Provides a segmented file-based storage engine with asynchronous operations.
/// </summary>
/// <typeparam name="TKey">The type of the keys in the store.</typeparam>
/// <typeparam name="TValue">The type of the values in the store.</typeparam>
/// <remarks>
/// <para>
/// <b>Why segmented storage?</b> A single append-only file grows without bound, making compaction
/// expensive (must rewrite the entire file). Segmentation splits the log into fixed-size files. Each
/// segment can be compacted or deleted independently, and old segments can be garbage-collected once
/// compacted. This is the segmented log pattern from Kleppmann Ch. 3.
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
    where TKey : ISerializable<TKey>, IComparable<TKey>, new()
    where TValue : ISerializable<TValue>, new()
{
    private readonly string _folder;
    private readonly string _prefix;
    private readonly long _maxSegmentSize;
    private readonly Func<string, string, IEntrySerializer<TKey, TValue>, long, IFileBasedStorageEngine<TKey, TValue>> _storageEngineFactory;
    private IFileBasedStorageEngine<TKey, TValue> _currentSegment;
    private readonly Stack<IFileBasedStorageEngine<TKey, TValue>> _segments;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

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

    /// <inheritdoc/>
    public async Task<(TValue Value, bool Found)> TryGetValueAsync(TKey key, CancellationToken cancellationToken = default)
    {
        Guard.AgainstNullOrDefault(() => key);
        cancellationToken.ThrowIfCancellationRequested();

        // Why search all segments, not just current? A key may have been written to an older segment
        // before the current one was created. Segments are iterated newest-first (Stack order), so the
        // first match found is guaranteed to be the most recent value.
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

    /// <inheritdoc/>
    public async Task<bool> ContainsKeyAsync(TKey key, CancellationToken cancellationToken = default)
    {
        Guard.AgainstNullOrDefault(() => key);
        cancellationToken.ThrowIfCancellationRequested();

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
        var currentSegmentSize = _currentSegment.FileSize;

        foreach (var item in itemsList)
        {
            Guard.AgainstNullOrDefault(() => item.Key);
            Guard.AgainstNullOrDefault(() => item.Value);
            cancellationToken.ThrowIfCancellationRequested();

            var itemSize = await CalculateSizeAsync(EntrySerializer, item.Key, item.Value).ConfigureAwait(false);
            if (currentSegmentSize + itemSize > _maxSegmentSize)
            {
                _currentSegment = CreateNewSegment();
                _segments.Push(_currentSegment);
                currentSegmentSize = 0;
            }

            await _currentSegment.SetAsync(item.Key, item.Value, cancellationToken).ConfigureAwait(false);
            currentSegmentSize += itemSize;
        }
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<(TKey Key, TValue Value)>> GetAllItemsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var allItems = new List<(TKey Key, TValue Value)>();

        foreach (var segment in _segments)
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

    private async Task ClearInternalAsync(CancellationToken cancellationToken)
    {
        foreach (var segment in _segments)
        {
            await segment.ClearAsync(cancellationToken).ConfigureAwait(false);
        }

        _segments.Clear();
        _currentSegment = CreateNewSegment();
        _segments.Push(_currentSegment);
    }

    /// <inheritdoc/>
    public async Task CompactAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var allItems = await GetAllItemsAsync(cancellationToken).ConfigureAwait(false);
            var latestItems = allItems.GroupBy(x => x.Key).Select(g => g.Last()).ToList();

            await ClearInternalAsync(cancellationToken).ConfigureAwait(false);
            await SetBulkInternalAsync(latestItems.Select(x => new KeyValuePair<TKey, TValue>(x.Key, x.Value)), cancellationToken).ConfigureAwait(false);

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
        var smallSegments = _segments.Where(s => s.FileSize < _maxSegmentSize).ToList();
        var mergedSegments = new Stack<IFileBasedStorageEngine<TKey, TValue>>();
        var currentMergedSegment = CreateNewSegment();
        mergedSegments.Push(currentMergedSegment);

        foreach (var segment in smallSegments)
        {
            var segmentItems = await segment.GetAllItemsAsync(cancellationToken).ConfigureAwait(false);

            foreach (var item in segmentItems)
            {
                var itemSize = await CalculateSizeAsync(EntrySerializer, item.Key, item.Value).ConfigureAwait(false);
                if (currentMergedSegment.FileSize + itemSize > _maxSegmentSize)
                {
                    currentMergedSegment = CreateNewSegment();
                    mergedSegments.Push(currentMergedSegment);
                }

                await currentMergedSegment.SetAsync(item.Key, item.Value, cancellationToken).ConfigureAwait(false);
            }

            await segment.ClearAsync(cancellationToken).ConfigureAwait(false);
        }

        // Why Reverse()? Stack.ToList() enumerates in LIFO order (newest first).
        // Pushing back in that order would reverse the stack. Reversing restores
        // the original order so the newest segment remains on top.
        var remainingSegments = _segments.Where(s => !smallSegments.Contains(s)).Reverse().ToList();
        _segments.Clear();

        foreach (var segment in remainingSegments)
        {
            _segments.Push(segment);
        }

        // Push merged segments so they appear after remaining segments in search order
        foreach (var mergedSegment in mergedSegments)
        {
            _segments.Push(mergedSegment);
        }
    }

    /// <summary>
    /// Calculates the size of a key-value pair using the specified entry serializer.
    /// </summary>
    /// <param name="serializer">The entry serializer to use.</param>
    /// <param name="key">The key.</param>
    /// <param name="value">The value.</param>
    /// <returns>The size of the key-value pair in bytes.</returns>
    private static async Task<long> CalculateSizeAsync(IEntrySerializer<TKey, TValue> serializer, TKey key, TValue value)
    {
        // Why serialize to a MemoryStream to measure size? Entry size varies depending on key/value
        // content (e.g., string length). The only accurate way to determine the serialized size is to
        // actually serialize it. The MemoryStream is a throwaway buffer — the result is used to decide
        // whether the entry fits in the current segment.
        using var memoryStream = new MemoryStream();
        await serializer.WriteEntryAsync(memoryStream, key, value).ConfigureAwait(false);
        return memoryStream.Length;
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
        // Why timestamp-based filenames? Timestamps ensure unique, monotonically increasing filenames
        // without coordination. The millisecond precision (fff) avoids collisions even under rapid
        // segment creation. The prefix groups related segments together for easy identification and cleanup.
        var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture);
        return $"{_prefix}_segment_{timestamp}.log";
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
