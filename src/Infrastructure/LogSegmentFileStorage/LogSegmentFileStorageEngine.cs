// Copyright (c) 2024 Pierre G. Boutquin. All rights reserved.
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

using Boutquin.Storage.Infrastructure.KeyValueStore;

namespace Boutquin.Storage.Infrastructure.LogSegmentFileStorage;

/// <summary>
/// Implements a log-segmented file storage engine that supports appending, bulk operations,
/// and compaction. The log is broken into segments, and each segment is managed separately.
/// </summary>
/// <typeparam name="TKey">The type of the keys in the store.</typeparam>
/// <typeparam name="TValue">The type of the values in the store.</typeparam>
public class LogSegmentFileStorageEngine<TKey, TValue> : ILogSegmentFileStorageEngine<TKey, TValue>
    where TKey : ISerializable<TKey>, IComparable<TKey>, new()
    where TValue : ISerializable<TValue>, new()
{
    private readonly ICompactableBulkStorageEngine<TKey, TValue> _storageEngine;
    private readonly Stack<ILogSegmentFile<TKey, TValue>> _segments = new();
    private readonly string _folder;
    private readonly string _prefix;
    private readonly long _maxSegmentSize;
    private ILogSegmentFile<TKey, TValue> _currentSegment;

    /// <summary>
    /// Initializes a new instance of the <see cref="LogSegmentFileStorageEngine{TKey, TValue}"/> class.
    /// </summary>
    /// <param name="storageEngine">The storage engine to use for managing segments.</param>
    /// <param name="folder">The folder where segment files are stored.</param>
    /// <param name="prefix">The prefix used for segment file names.</param>
    /// <param name="maxSegmentSize">The maximum size of a segment before a new one is created.</param>
    public LogSegmentFileStorageEngine(
        ICompactableBulkStorageEngine<TKey, TValue> storageEngine,
        string folder,
        string prefix,
        long maxSegmentSize)
    {
        _storageEngine = storageEngine ?? throw new ArgumentNullException(nameof(storageEngine));
        _folder = folder ?? throw new ArgumentNullException(nameof(folder));
        _prefix = prefix ?? throw new ArgumentNullException(nameof(prefix));
        _maxSegmentSize = maxSegmentSize > 0 ? maxSegmentSize : throw new ArgumentOutOfRangeException(nameof(maxSegmentSize));

        _currentSegment = CreateNewSegment();
        _segments.Push(_currentSegment);
    }

    /// <inheritdoc/>
    /// <example>
    /// <code>
    /// var storageEngine = new LogSegmentFileStorageEngine&lt;Key, Value&gt;(storageEngine, folder, prefix, maxSegmentSize);
    /// await storageEngine.SetAsync(new Key(1), new Value("value1"));
    /// </code>
    /// </example>
    public async Task SetAsync(TKey key, TValue value, CancellationToken cancellationToken = default)
    {
        Guard.AgainstNullOrDefault(() => key);
        Guard.AgainstNullOrDefault(() => value);
        cancellationToken.ThrowIfCancellationRequested();

        if (_currentSegment.SegmentSize >= _maxSegmentSize)
        {
            _currentSegment = CreateNewSegment();
            _segments.Push(_currentSegment);
        }

        await _currentSegment.SetAsync(key, value, cancellationToken);
    }

    /// <inheritdoc/>
    /// <example>
    /// <code>
    /// var storageEngine = new LogSegmentFileStorageEngine&lt;Key, Value&gt;(storageEngine, folder, prefix, maxSegmentSize);
    /// var (value, found) = await storageEngine.TryGetValueAsync(new Key(1));
    /// if (found)
    /// {
    ///     Console.WriteLine($"Value: {value}");
    /// }
    /// </code>
    /// </example>
    public async Task<(TValue Value, bool Found)> TryGetValueAsync(TKey key, CancellationToken cancellationToken = default)
    {
        Guard.AgainstNullOrDefault(() => key);
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var segment in _segments)
        {
            var (value, found) = await segment.TryGetValueAsync(key, cancellationToken);
            if (found)
            {
                return (value, true);
            }
        }

        return (default, false);
    }

    /// <inheritdoc/>
    /// <example>
    /// <code>
    /// var storageEngine = new LogSegmentFileStorageEngine&lt;Key, Value&gt;(storageEngine, folder, prefix, maxSegmentSize);
    /// var containsKey = await storageEngine.ContainsKeyAsync(new Key(1));
    /// Console.WriteLine($"Contains Key: {containsKey}");
    /// </code>
    /// </example>
    public async Task<bool> ContainsKeyAsync(TKey key, CancellationToken cancellationToken = default)
    {
        Guard.AgainstNullOrDefault(() => key);
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var segment in _segments)
        {
            var containsKey = await segment.ContainsKeyAsync(key, cancellationToken);
            if (containsKey)
            {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc/>
    /// <example>
    /// <code>
    /// var storageEngine = new LogSegmentFileStorageEngine&lt;Key, Value&gt;(storageEngine, folder, prefix, maxSegmentSize);
    /// await storageEngine.RemoveAsync(new Key(1));
    /// </code>
    /// </example>
    public Task RemoveAsync(TKey key, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("Remove operation is not supported in an append-only storage engine.");
    }

    /// <inheritdoc/>
    /// <example>
    /// <code>
    /// var storageEngine = new LogSegmentFileStorageEngine&lt;Key, Value&gt;(storageEngine, folder, prefix, maxSegmentSize);
    /// await storageEngine.ClearAsync();
    /// </code>
    /// </example>
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var segment in _segments)
        {
            await segment.ClearAsync(cancellationToken);
        }

        _segments.Clear();
        _currentSegment = CreateNewSegment();
        _segments.Push(_currentSegment);
    }

    /// <inheritdoc/>
    /// <example>
    /// <code>
    /// var storageEngine = new LogSegmentFileStorageEngine&lt;Key, Value&gt;(storageEngine, folder, prefix, maxSegmentSize);
    /// var allItems = await storageEngine.GetAllItemsAsync();
    /// foreach (var item in allItems)
    /// {
    ///     Console.WriteLine($"Key: {item.Key}, Value: {item.Value}");
    /// }
    /// </code>
    /// </example>
    public async Task<IEnumerable<(TKey Key, TValue Value)>> GetAllItemsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var allItems = new List<(TKey Key, TValue Value)>();

        foreach (var segment in _segments)
        {
            var segmentItems = await segment.GetAllItemsAsync(cancellationToken);
            allItems.AddRange(segmentItems);
        }

        return allItems;
    }

    /// <inheritdoc/>
    /// <example>
    /// <code>
    /// var storageEngine = new LogSegmentFileStorageEngine&lt;Key, Value&gt;(storageEngine, folder, prefix, maxSegmentSize);
    /// var bulkItems = new[]
    /// {
    ///     new KeyValuePair&lt;Key, Value&gt;(new Key(1), new Value("value1")),
    ///     new KeyValuePair&lt;Key, Value&gt;(new Key(2), new Value("value2"))
    /// };
    /// await storageEngine.SetBulkAsync(bulkItems);
    /// </code>
    /// </example>
    public async Task SetBulkAsync(IEnumerable<KeyValuePair<TKey, TValue>> items, CancellationToken cancellationToken = default)
    {
        Guard.AgainstEmptyOrNullEnumerable(() => items);
        cancellationToken.ThrowIfCancellationRequested();

        var itemsList = items.ToList();
        var currentSegmentSize = _currentSegment.SegmentSize;

        foreach (var item in itemsList)
        {
            Guard.AgainstNullOrDefault(() => item.Key);
            Guard.AgainstNullOrDefault(() => item.Value);
            cancellationToken.ThrowIfCancellationRequested();

            var itemSize = CalculateSize(_currentSegment.EntrySerializer, item.Key, item.Value);
            if (currentSegmentSize + itemSize > _maxSegmentSize)
            {
                _currentSegment = CreateNewSegment();
                _segments.Push(_currentSegment);
                currentSegmentSize = 0;
            }

            await _currentSegment.SetAsync(item.Key, item.Value, cancellationToken);
            currentSegmentSize += itemSize;
        }
    }

    /// <inheritdoc/>
    /// <example>
    /// <code>
    /// var storageEngine = new LogSegmentFileStorageEngine&lt;Key, Value&gt;(storageEngine, folder, prefix, maxSegmentSize);
    /// await storageEngine.CompactAsync();
    /// </code>
    /// </example>
    public async Task CompactAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var allItems = await GetAllItemsAsync(cancellationToken);
        var latestItems = allItems.GroupBy(x => x.Key).Select(g => g.Last()).ToList();

        await ClearAsync(cancellationToken);
        await SetBulkAsync(latestItems.Select(x => new KeyValuePair<TKey, TValue>(x.Key, x.Value)), cancellationToken);

        await MergeSmallSegmentsAsync(cancellationToken);
    }

    /// <summary>
    /// Merges small segments into larger ones if possible.
    /// </summary>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    private async Task MergeSmallSegmentsAsync(CancellationToken cancellationToken)
    {
        var smallSegments = _segments.Where(s => s.SegmentSize < _maxSegmentSize).ToList();
        var mergedSegments = new Stack<ILogSegmentFile<TKey, TValue>>();
        var currentMergedSegment = CreateNewSegment();
        mergedSegments.Push(currentMergedSegment);

        foreach (var segment in smallSegments)
        {
            var segmentItems = await segment.GetAllItemsAsync(cancellationToken);

            foreach (var item in segmentItems)
            {
                var itemSize = CalculateSize(currentMergedSegment.EntrySerializer, item.Key, item.Value);
                if (currentMergedSegment.SegmentSize + itemSize > _maxSegmentSize)
                {
                    currentMergedSegment = CreateNewSegment();
                    mergedSegments.Push(currentMergedSegment);
                }

                await currentMergedSegment.SetAsync(item.Key, item.Value, cancellationToken);
            }

            await segment.ClearAsync(cancellationToken);
        }

        foreach (var segment in mergedSegments)
        {
            _segments.Push(segment);
        }
    }

    /// <summary>
    /// Calculates the size of a key-value pair using the specified entry serializer.
    /// </summary>
    /// <param name="serializer">The entry serializer to use.</param>
    /// <param name="key">The key.</param>
    /// <param name="value">The value.</param>
    /// <returns>The size of the key-value pair in bytes.</returns>
    private static long CalculateSize(IEntrySerializer<TKey, TValue> serializer, TKey key, TValue value)
    {
        using var memoryStream = new MemoryStream();
        serializer.WriteEntryAsync(memoryStream, key, value).Wait();
        return memoryStream.Length;
    }

    /// <summary>
    /// Creates a new segment file for storing key-value pairs.
    /// </summary>
    /// <returns>A new instance of <see cref="ILogSegmentFile{TKey, TValue}"/>.</returns>
    private ILogSegmentFile<TKey, TValue> CreateNewSegment()
    {
        var segmentFilePath = GenerateSegmentFilePath();
        return new LogSegmentFile<TKey, TValue>(
            new StorageFile(segmentFilePath), 
            _storageEngine, 
            _maxSegmentSize);
    }

    /// <summary>
    /// Generates a file path for a new segment file.
    /// </summary>
    /// <returns>The generated segment file path.</returns>
    private string GenerateSegmentFilePath()
    {
        var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
        return System.IO.Path.Combine(_folder, $"{_prefix}_segment_{timestamp}.log");
    }

    public IEntrySerializer<TKey, TValue> EntrySerializer => _storageEngine.EntrySerializer;
}