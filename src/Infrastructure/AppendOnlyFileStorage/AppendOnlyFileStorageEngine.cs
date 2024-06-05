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
/// Provides an append-only file-based storage engine with asynchronous operations.
/// </summary>
/// <typeparam name="TKey">The type of the keys in the store.</typeparam>
/// <typeparam name="TValue">The type of the values in the store.</typeparam>
/// <remarks>
/// This class is not sealed to allow for extensibility and specialization. It serves as a base class 
/// for other storage engine implementations, such as <see cref="AppendOnlyFileStorageEngineWithIndex{TKey, TValue}"/>, 
/// which may add additional features or optimizations. For instance, the derived class 
/// <see cref="AppendOnlyFileStorageEngineWithIndex{TKey, TValue}"/> enhances the base functionality 
/// by adding indexing capabilities. Sealing this class would prevent such extensions and limit 
/// the flexibility needed to create specialized storage engines that build upon the core append-only 
/// functionality. Thus, to enable the creation of more advanced and feature-rich storage engine 
/// implementations, this class remains unsealed.
/// </remarks>
public class AppendOnlyFileStorageEngine<TKey, TValue> :
    IFileBasedStorageEngine<TKey, TValue>
    where TKey : ISerializable<TKey>, IComparable<TKey>, new()
    where TValue : ISerializable<TValue>, new()
{
    protected readonly IStorageFile StorageFile;
    public IEntrySerializer<TKey, TValue> EntrySerializer { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="AppendOnlyFileStorageEngine{TKey, TValue}"/> class.
    /// </summary>
    /// <param name="storageFile">The storage file to use for storing data.</param>
    /// <param name="entrySerializer">The serializer to use for serializing and deserializing entries.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="storageFile"/> or <paramref name="entrySerializer"/> is null.</exception>
    public AppendOnlyFileStorageEngine(
        IStorageFile storageFile,
        IEntrySerializer<TKey, TValue> entrySerializer)
    {
        Guard.AgainstNullOrDefault(() => storageFile);
        Guard.AgainstNullOrDefault(() => entrySerializer);

        StorageFile = storageFile;
        EntrySerializer = entrySerializer;
    }

    /// <inheritdoc/>
    public string FileName => StorageFile.FileName;

    /// <inheritdoc/>
    public string FileLocation => StorageFile.FileLocation;

    /// <inheritdoc/>
    public long FileSize => StorageFile.FileSize;

    /// <inheritdoc/>
    public virtual async Task SetAsync(TKey key, TValue value, CancellationToken cancellationToken = default)
    {
        Guard.AgainstNullOrDefault(() => key);
        Guard.AgainstNullOrDefault(() => value);
        cancellationToken.ThrowIfCancellationRequested();

        await using var stream = StorageFile.Open(FileMode.Append);
        await WriteEntryAsync(stream, key, value, cancellationToken);
    }

    /// <summary>
    /// Writes an entry to the given stream.
    /// </summary>
    /// <param name="stream">The stream to write to.</param>
    /// <param name="key">The key to write.</param>
    /// <param name="value">The value to write.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <exception cref="OperationCanceledException">Thrown if the operation is canceled.</exception>
    protected async Task WriteEntryAsync(Stream stream, TKey key, TValue value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await EntrySerializer.WriteEntryAsync(stream, key, value, cancellationToken);
    }

    /// <inheritdoc/>
    public virtual async Task<(TValue Value, bool Found)> TryGetValueAsync(TKey key, CancellationToken cancellationToken = default)
    {
        Guard.AgainstNullOrDefault(() => key);
        cancellationToken.ThrowIfCancellationRequested();

        var fileBytes = await StorageFile.ReadAllBytesAsync(cancellationToken);

        using (var stream = new MemoryStream(fileBytes))
        {
            while (EntrySerializer.CanRead(stream))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = EntrySerializer.ReadEntry(stream);
                if (entry.HasValue && entry.Value.Key.CompareTo(key) == 0)
                {
                    return (entry.Value.Value, true);
                }
            }
        }

        return (default, false);
    }

    /// <inheritdoc/>
    public async Task<bool> ContainsKeyAsync(TKey key, CancellationToken cancellationToken = default)
    {
        var (_, found) = await TryGetValueAsync(key, cancellationToken);
        return found;
    }

    /// <inheritdoc/>
    public Task RemoveAsync(TKey key, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("Remove operation is not supported in an append-only storage engine.");
    }

    /// <inheritdoc/>
    public virtual async Task SetBulkAsync(IEnumerable<KeyValuePair<TKey, TValue>> items, CancellationToken cancellationToken = default)
    {
        Guard.AgainstEmptyOrNullEnumerable(() => items);
        cancellationToken.ThrowIfCancellationRequested();

        await using var stream = StorageFile.Open(FileMode.Append);
        foreach (var item in items)
        {
            Guard.AgainstNullOrDefault(() => item.Key);
            Guard.AgainstNullOrDefault(() => item.Value);
            cancellationToken.ThrowIfCancellationRequested();

            await WriteEntryAsync(stream, item.Key, item.Value, cancellationToken);
        }
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<(TKey Key, TValue Value)>> GetAllItemsAsync(CancellationToken cancellationToken = default)
    {
        var items = new List<(TKey Key, TValue Value)>();
        cancellationToken.ThrowIfCancellationRequested();
        var fileBytes = await StorageFile.ReadAllBytesAsync(cancellationToken);

        using var stream = new MemoryStream(fileBytes);
        while (EntrySerializer.CanRead(stream))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = EntrySerializer.ReadEntry(stream);
            if (entry.HasValue)
            {
                items.Add(entry.Value);
            }
        }

        return items;
    }

    /// <inheritdoc/>
    public virtual async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StorageFile.Delete(FileDeletionHandling.DeleteIfExists);
    }

    /// <inheritdoc/>
    public virtual async Task CompactAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var items = await GetAllItemsAsync(cancellationToken);
        var latestItems = items
            .GroupBy(x => x.Key)
            .Select(g => g.Last())
            .ToList();

        await using var stream = StorageFile.Open(FileMode.Create); // Clears the existing data
        foreach (var item in latestItems)
        {
            await WriteEntryAsync(stream, item.Key, item.Value, cancellationToken);
        }

        await stream.FlushAsync(cancellationToken); // Ensure the stream is properly flushed
    }
}