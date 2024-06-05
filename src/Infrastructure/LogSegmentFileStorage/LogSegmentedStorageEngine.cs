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
namespace Boutquin.Storage.Infrastructure.LogSegmentFileStorage;

/// <summary>
/// Provides a segmented file-based storage engine with asynchronous operations.
/// </summary>
/// <typeparam name="TKey">The type of the keys in the store.</typeparam>
/// <typeparam name="TValue">The type of the values in the store.</typeparam>
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

        if (_currentSegment.FileSize >= _maxSegmentSize)
        {
            _currentSegment = CreateNewSegment();
            _segments.Push(_currentSegment);
        }

        await _currentSegment.SetAsync(key, value, cancellationToken);
    }

    /// <inheritdoc/>
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
    public Task RemoveAsync(TKey key, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("Remove operation is not supported in an append-only storage engine.");
    }

    /// <inheritdoc/>
    public async Task SetBulkAsync(IEnumerable<KeyValuePair<TKey, TValue>> items, CancellationToken cancellationToken = default)
    {
        Guard.AgainstEmptyOrNullEnumerable(() => items);
        cancellationToken.ThrowIfCancellationRequested();

        var itemsList = items.ToList();
        var currentSegmentSize = _currentSegment.FileSize;

        foreach (var item in itemsList)
        {
            Guard.AgainstNullOrDefault(() => item.Key);
            Guard.AgainstNullOrDefault(() => item.Value);
            cancellationToken.ThrowIfCancellationRequested();

            var itemSize = CalculateSize(EntrySerializer, item.Key, item.Value);
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
        var smallSegments = _segments.Where(s => s.FileSize < _maxSegmentSize).ToList();
        var mergedSegments = new Stack<IFileBasedStorageEngine<TKey, TValue>>();
        var currentMergedSegment = CreateNewSegment();
        mergedSegments.Push(currentMergedSegment);

        foreach (var segment in smallSegments)
        {
            var segmentItems = await segment.GetAllItemsAsync(cancellationToken);

            foreach (var item in segmentItems)
            {
                var itemSize = CalculateSize(EntrySerializer, item.Key, item.Value);
                if (currentMergedSegment.FileSize + itemSize > _maxSegmentSize)
                {
                    currentMergedSegment = CreateNewSegment();
                    mergedSegments.Push(currentMergedSegment);
                }

                await currentMergedSegment.SetAsync(item.Key, item.Value, cancellationToken);
            }

            await segment.ClearAsync(cancellationToken);
        }

        // Merge small segments into main segments stack
        var remainingSegments = _segments.Where(s => !smallSegments.Contains(s)).ToList();
        _segments.Clear();

        foreach (var segment in remainingSegments)
        {
            _segments.Push(segment);
        }

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
    private static long CalculateSize(IEntrySerializer<TKey, TValue> serializer, TKey key, TValue value)
    {
        using var memoryStream = new MemoryStream();
        serializer.WriteEntryAsync(memoryStream, key, value).Wait();
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
        var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
        return $"{_prefix}_segment_{timestamp}.log";
    }

    /// <summary>
    /// Ensures that the specified directory exists.
    /// </summary>
    /// <param name="directoryPath">The path of the directory.</param>
    private void EnsureDirectoryExists(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }
    }
}