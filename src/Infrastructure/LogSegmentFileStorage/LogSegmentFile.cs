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
/// Represents a log segment file.
/// </summary>
/// <typeparam name="TKey">The type of the keys in the store.</typeparam>
/// <typeparam name="TValue">The type of the values in the store.</typeparam>
public sealed class LogSegmentFile<TKey, TValue> : ILogSegmentFile<TKey, TValue>
    where TKey : ISerializable<TKey>, IComparable<TKey>, new()
    where TValue : ISerializable<TValue>, new()
{
    private readonly IStorageFile _segmentFile;
    private readonly IEntrySerializer<TKey, TValue> _entrySerializer;
    private readonly AppendOnlyFileStorageEngine<TKey, TValue> _storageEngine;
    private readonly long _maxSegmentSize;

    /// <summary>
    /// Initializes a new instance of the <see cref="LogSegmentFile{TKey, TValue}"/> class.
    /// </summary>
    /// <param name="segmentFile">The storage file representing the segment.</param>
    /// <param name="entrySerializer">The serializer to use for serializing and deserializing entries.</param>
    /// <param name="maxSegmentSize">The maximum size of the segment file in bytes.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="segmentFile"/> or <paramref name="entrySerializer"/> is null.</exception>
    public LogSegmentFile(
        IStorageFile segmentFile,
        IEntrySerializer<TKey, TValue> entrySerializer,
        long maxSegmentSize)
    {
        _segmentFile = segmentFile ?? throw new ArgumentNullException(nameof(segmentFile));
        _entrySerializer = entrySerializer ?? throw new ArgumentNullException(nameof(entrySerializer));
        _maxSegmentSize = maxSegmentSize;

        _segmentFile.Create(FileExistenceHandling.DoNothingIfExists);
        _storageEngine = new AppendOnlyFileStorageEngine<TKey, TValue>(_segmentFile, _entrySerializer);
    }

    public string SegmentFilePath => _segmentFile.FileLocation;

    public long SegmentSize => _segmentFile.Length;

    public IEntrySerializer<TKey, TValue> EntrySerializer => _entrySerializer;

    /// <inheritdoc/>
    public async Task SetAsync(TKey key, TValue value, CancellationToken cancellationToken = default)
    {
        if (_segmentFile.Length >= _maxSegmentSize)
        {
            throw new InvalidOperationException("Segment size exceeded.");
        }
        await _storageEngine.SetAsync(key, value, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<(TValue Value, bool Found)> TryGetValueAsync(TKey key, CancellationToken cancellationToken = default)
    {
        return _storageEngine.TryGetValueAsync(key, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<bool> ContainsKeyAsync(TKey key, CancellationToken cancellationToken = default)
    {
        return _storageEngine.ContainsKeyAsync(key, cancellationToken);
    }

    /// <inheritdoc/>
    public Task RemoveAsync(TKey key, CancellationToken cancellationToken = default)
    {
        return _storageEngine.RemoveAsync(key, cancellationToken);
    }

    /// <inheritdoc/>
    public Task SetBulkAsync(IEnumerable<KeyValuePair<TKey, TValue>> items, CancellationToken cancellationToken = default)
    {
        return _storageEngine.SetBulkAsync(items, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<IEnumerable<(TKey Key, TValue Value)>> GetAllItemsAsync(CancellationToken cancellationToken = default)
    {
        return _storageEngine.GetAllItemsAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        return _storageEngine.ClearAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public Task CompactAsync(CancellationToken cancellationToken = default)
    {
        return _storageEngine.CompactAsync(cancellationToken);
    }
}