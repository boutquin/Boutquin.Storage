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
namespace Boutquin.Storage.Infrastructure.AppendOnlyFileStorage;

/// <summary>
/// Provides an append-only file-based storage engine with asynchronous operations,
/// using an index to speed up reads by storing the offset in the file.
/// </summary>
/// <typeparam name="TKey">The type of the keys in the store.</typeparam>
/// <typeparam name="TValue">The type of the values in the store.</typeparam>
/// <remarks>
/// <para>
/// <b>Why an index layer on top of append-only storage?</b> The base
/// <see cref="AppendOnlyFileStorageEngine{TKey, TValue}"/> requires O(n) sequential scan for reads.
/// The index trades memory for read performance by storing each key's file offset, enabling O(1)
/// direct reads via seek. This is the hash-index approach from Kleppmann Ch. 3 — the entire keyspace
/// must fit in memory, but reads become constant-time.
/// </para>
/// <para>
/// <b>Why store FileLocation(offset, length - offset)?</b> The offset is the stream position before
/// writing the entry. The count (length - offset) is the number of bytes the entry occupies. Together
/// they allow reading exactly the right bytes from disk — no scanning, no parsing neighboring entries.
/// </para>
/// </remarks>
public sealed class AppendOnlyFileStorageEngineWithIndex<TKey, TValue> :
    AppendOnlyFileStorageEngine<TKey, TValue>
    where TKey : ISerializable<TKey>, IComparable<TKey>, new()
    where TValue : ISerializable<TValue>, new()
{
    private readonly IFileStorageIndex<TKey> _index;

    /// <summary>
    /// Initializes a new instance of the <see cref="AppendOnlyFileStorageEngineWithIndex{TKey, TValue}"/> class.
    /// </summary>
    /// <param name="storageFile">The storage file to use for storing data.</param>
    /// <param name="entrySerializer">The serializer to use for serializing and deserializing entries.</param>
    /// <param name="index">The index to use for storing offsets of entries.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="storageFile"/>, <paramref name="entrySerializer"/>, or <paramref name="index"/> is null.</exception>
    public AppendOnlyFileStorageEngineWithIndex(
        IStorageFile storageFile,
        IEntrySerializer<TKey, TValue> entrySerializer,
        IFileStorageIndex<TKey> index)
        : base(storageFile, entrySerializer)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
    }

    /// <inheritdoc/>
    public override async Task SetAsync(TKey key, TValue value, CancellationToken cancellationToken = default)
    {
        Guard.AgainstNullOrDefault(() => key);
        Guard.AgainstNullOrDefault(() => value);
        cancellationToken.ThrowIfCancellationRequested();

        var stream = StorageFile.Open(FileMode.Append);
        await using (stream.ConfigureAwait(false))
        {
            var offset = stream.Length;
            await WriteEntryAsync(stream, key, value, cancellationToken).ConfigureAwait(false);
            var newLength = stream.Length;
            await _index.SetAsync(key, new FileLocation(offset, newLength - offset), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public override async Task<(TValue Value, bool Found)> TryGetValueAsync(TKey key, CancellationToken cancellationToken = default)
    {
        Guard.AgainstNullOrDefault(() => key);
        cancellationToken.ThrowIfCancellationRequested();

        var (fileLocation, found) = await _index.TryGetValueAsync(key, cancellationToken).ConfigureAwait(false);
        if (!found)
        {
            return (default!, false);
        }

        var buffer = StorageFile.ReadBytes(fileLocation.Offset, fileLocation.Count);

        using (var stream = new MemoryStream(buffer))
        {
            var entry = EntrySerializer.ReadEntry(stream);
            if (entry.HasValue)
            {
                return (entry.Value.Value, true);
            }
        }

        return (default!, false);
    }

    /// <inheritdoc/>
    public override async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await base.ClearAsync(cancellationToken).ConfigureAwait(false);
        await _index.ClearAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public override async Task SetBulkAsync(IEnumerable<KeyValuePair<TKey, TValue>> items, CancellationToken cancellationToken = default)
    {
        Guard.AgainstEmptyOrNullEnumerable(() => items);
        cancellationToken.ThrowIfCancellationRequested();

        var stream = StorageFile.Open(FileMode.Append);
        await using (stream.ConfigureAwait(false))
        {
            foreach (var item in items)
            {
                Guard.AgainstNullOrDefault(() => item.Key);
                Guard.AgainstNullOrDefault(() => item.Value);
                cancellationToken.ThrowIfCancellationRequested();

                var offset = stream.Length;
                await WriteEntryAsync(stream, item.Key, item.Value, cancellationToken).ConfigureAwait(false);
                var newLength = stream.Length;
                await _index.SetAsync(item.Key, new FileLocation(offset, newLength - offset), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc/>
    public override async Task CompactAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var items = await GetAllItemsAsync(cancellationToken).ConfigureAwait(false);
        var latestItems = items.GroupBy(x => x.Key).Select(g => g.Last()).ToList();

        // Why ClearAsync then SetBulkAsync (not in-place rewrite)? Compaction must rebuild both the
        // file and the index atomically. ClearAsync wipes both, then SetBulkAsync rewrites entries and
        // rebuilds index offsets in a single pass. This avoids index-file offset mismatches that would
        // occur if we only rewrote the file.
        await ClearAsync(cancellationToken).ConfigureAwait(false);

        await SetBulkAsync(latestItems.Select(x => new KeyValuePair<TKey, TValue>(x.Key, x.Value)), cancellationToken).ConfigureAwait(false);
    }
}
