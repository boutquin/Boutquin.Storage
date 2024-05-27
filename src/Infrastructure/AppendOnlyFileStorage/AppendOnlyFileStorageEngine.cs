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
public class AppendOnlyFileStorageEngine<TKey, TValue> : AppendOnlyFileStorageEngineBase<TKey, TValue>
    where TKey : ISerializable<TKey>, IComparable<TKey>, new()
    where TValue : ISerializable<TValue>, new()
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AppendOnlyFileStorageEngine{TKey, TValue}"/> class.
    /// </summary>
    /// <param name="databaseFilePath">The path to the database file.</param>
    /// <param name="entrySerializer">The serializer to use for serializing and deserializing entries.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="databaseFilePath"/> or <paramref name="entrySerializer"/> is null.</exception>
    public AppendOnlyFileStorageEngine(
        string databaseFilePath,
        IEntrySerializer<TKey, TValue> entrySerializer)
        : base(databaseFilePath, entrySerializer)
    {
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
        await WriteEntryAsync(stream, key, value, cancellationToken);
    }
}