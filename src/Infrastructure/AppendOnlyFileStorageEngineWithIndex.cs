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
namespace Boutquin.Storage.Infrastructure;

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

    /// <summary>
    /// Sets or updates the value for the specified key.
    /// If the key already exists in the store, the value is updated.
    /// If the key does not exist, a new key-value pair is added.
    /// </summary>
    /// <param name="key">The key to set or update.</param>
    /// <param name="value">The value to associate with the key.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <exception cref="ArgumentNullException">Thrown if the key or value is null.</exception>
    /// <exception cref="ArgumentException">Thrown if the key or value is the default value.</exception>
    /// <exception cref="OperationCanceledException">Thrown if the operation is canceled.</exception>
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

    /// <summary>
    /// Attempts to retrieve the value associated with the specified key.
    /// </summary>
    /// <param name="key">The key whose value to retrieve.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. 
    /// The task result contains a tuple with the value associated with the key 
    /// and a boolean indicating whether the key was found.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown if the key is null.</exception>
    /// <exception cref="ArgumentException">Thrown if the key is the default value.</exception>
    /// <exception cref="OperationCanceledException">Thrown if the operation is canceled.</exception>
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

    /// <summary>
    /// Removes all key-value pairs from the store and clears the index.
    /// </summary>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous clear operation.</returns>
    /// <exception cref="OperationCanceledException">Thrown if the operation is canceled.</exception>
    public override async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await base.ClearAsync(cancellationToken);
        await _index.ClearAsync(cancellationToken);
    }
}