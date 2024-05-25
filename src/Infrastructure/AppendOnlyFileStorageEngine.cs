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
/// Provides an append-only file-based storage engine with asynchronous operations.
/// </summary>
/// <typeparam name="TKey">The type of the keys in the store.</typeparam>
/// <typeparam name="TValue">The type of the values in the store.</typeparam>
public class AppendOnlyFileStorageEngine<TKey, TValue> : AppendOnlyFileStorageEngineBase<TKey, TValue>
    where TKey : ISerializable<TKey>, IComparable<TKey>, new()
    where TValue : ISerializable<TValue>, new()
{
    public AppendOnlyFileStorageEngine(
        string databaseFilePath,
        IEntrySerializer<TKey, TValue> entrySerializer)
        : base(databaseFilePath, entrySerializer)
    {
    }

    public override async Task SetAsync(TKey key, TValue value)
    {
        using (var stream = new FileStream(DatabaseFilePath, FileMode.Append, FileAccess.Write, FileShare.None))
        {
            await WriteEntryAsync(stream, key, value);
        }
    }
}
