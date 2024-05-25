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

    public AppendOnlyFileStorageEngineWithIndex(
        string databaseFilePath,
        IFileStorageIndex<TKey> index,
        IEntrySerializer<TKey, TValue> entrySerializer)
        : base(databaseFilePath, entrySerializer)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
    }

    public override async Task SetAsync(TKey key, TValue value)
    {
        using (var stream = new FileStream(DatabaseFilePath, FileMode.Append, FileAccess.Write, FileShare.None))
        {
            var offset = (int)stream.Position;
            await WriteEntryAsync(stream, key, value);
            await _index.SetAsync(key, new FileLocation(offset, (int)stream.Length - offset));
        }
    }

    public override async Task<(TValue Value, bool Found)> TryGetValueAsync(TKey key)
    {
        Guard.AgainstNullOrDefault(() => key);

        var (fileLocation, found) = await _index.TryGetValueAsync(key);
        if (!found)
        {
            return (default, false);
        }

        var buffer = new byte[fileLocation.Count];
        using (var stream = new FileStream(DatabaseFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            stream.Seek(fileLocation.Offset, SeekOrigin.Begin);
            await stream.ReadAsync(buffer, 0, buffer.Length);
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

    public override async Task Clear()
    {
        await base.Clear();
        await _index.Clear();
    }
}
