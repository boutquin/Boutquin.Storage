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
namespace Boutquin.Storage.Infrastructure.AppendOnlyFileStorage;

/// <summary>
/// Provides an append-only file-based storage engine with asynchronous operations,
/// using an index to speed up reads by storing the offset in the file.
/// </summary>
/// <typeparam name="TKey">The type of the keys in the store.</typeparam>
/// <typeparam name="TValue">The type of the values in the store.</typeparam>
public class AppendOnlyFileStorageEngineWithIndex<TKey, TValue> : AppendOnlyFileStorageEngineBase<TKey, TValue>
    where TKey : ISerializable<TKey>, IComparable<TKey>, new()
    where TValue : ISerializable<TValue>, new()
{
    private readonly IFileStorageIndex<TKey> _index;

    /// <summary>
    /// Initializes a new instance of the <see cref="AppendOnlyFileStorageEngineWithIndex{TKey, TValue}"/> class.
    /// </summary>
    /// <param name="databaseFilePath">The path to the database file.</param>
    /// <param name="entrySerializer">The serializer to use for serializing and deserializing entries.</param>
    /// <param name="index">The index to use for storing offsets of entries.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="databaseFilePath"/>, <paramref name="entrySerializer"/>, or <paramref name="index"/> is null.</exception>
    public AppendOnlyFileStorageEngineWithIndex(
        string databaseFilePath,
        IEntrySerializer<TKey, TValue> entrySerializer,
        IFileStorageIndex<TKey> index)
        : base(databaseFilePath, entrySerializer)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
    }

    /// <inheritdoc/>
    public override async Task SetAsync(TKey key, TValue value, CancellationToken cancellationToken = default)
    {
        Guard.AgainstNullOrDefault(() => key);
        Guard.AgainstNullOrDefault(() => value);
        cancellationToken.ThrowIfCancellationRequested();

        await using var stream = new FileStream(DatabaseFilePath, FileMode.Append, FileAccess.Write, FileShare.None);
        var offset = (int)stream.Position;
        await WriteEntryAsync(stream, key, value, cancellationToken);
        var length = (int)stream.Length;
        await _index.SetAsync(key, new FileLocation(offset, length - offset), cancellationToken);
    }

    /// <inheritdoc/>
    public override async Task<(TValue Value, bool Found)> TryGetValueAsync(TKey key, CancellationToken cancellationToken = default)
    {
        Guard.AgainstNullOrDefault(() => key);
        cancellationToken.ThrowIfCancellationRequested();

        var (fileLocation, found) = await _index.TryGetValueAsync(key, cancellationToken);
        if (!found)
        {
            return (default, false);
        }

        var buffer = new byte[fileLocation.Count];
        await using (var stream = new FileStream(DatabaseFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            stream.Seek(fileLocation.Offset, SeekOrigin.Begin);
            await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
        }

        using (var stream = new MemoryStream(buffer))
        {
            var entry = EntrySerializer.ReadEntry(stream);
            if (entry.HasValue)
            {
                return (entry.Value.Value, true);
            }
        }

        return (default, false);
    }

    /// <inheritdoc/>
    public override async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await base.ClearAsync(cancellationToken);
        await _index.ClearAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public override async Task SetBulkAsync(IEnumerable<KeyValuePair<TKey, TValue>> items, CancellationToken cancellationToken = default)
    {
        Guard.AgainstEmptyOrNullEnumerable(() => items);
        cancellationToken.ThrowIfCancellationRequested();

        await using var stream = new FileStream(DatabaseFilePath, FileMode.Append, FileAccess.Write, FileShare.None);

        foreach (var item in items)
        {
            Guard.AgainstNullOrDefault(() => item.Key);
            Guard.AgainstNullOrDefault(() => item.Value);
            cancellationToken.ThrowIfCancellationRequested();

            var offset = (int)stream.Position;
            await WriteEntryAsync(stream, item.Key, item.Value, cancellationToken);
            var length = (int)stream.Length;
            await _index.SetAsync(item.Key, new FileLocation(offset, length - offset), cancellationToken);
        }
    }

    /// <inheritdoc/>
    public override async Task CompactAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var items = await GetAllItemsAsync(cancellationToken);
        var latestItems = items.GroupBy(x => x.Key).Select(g => g.Last()).ToList();

        await ClearAsync(cancellationToken); // Clear the existing data

        await SetBulkAsync(latestItems.Select(x => new KeyValuePair<TKey, TValue>(x.Key, x.Value)), cancellationToken);

        // Rebuild the index after compaction is not needed because SetBulkAsync updates the index
    }
}