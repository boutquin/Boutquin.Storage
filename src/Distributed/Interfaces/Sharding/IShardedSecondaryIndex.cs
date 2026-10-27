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
namespace Boutquin.Storage.Distributed.Interfaces.Sharding;

/// <summary>Stores secondary-index entries in explicitly selected index shards.</summary>
public interface IShardedSecondaryIndex<TKey, TValue>
    where TKey : notnull
{
    /// <summary>Upserts an entry in one index shard.</summary>
    ValueTask UpsertAsync(
        string indexShardId,
        string indexTerm,
        ShardedIndexEntry<TKey, TValue> entry,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Withdraws one record's entry from a term's postings in one index shard.
    /// </summary>
    /// <returns><see langword="true"/> when an entry was present and removed.</returns>
    ValueTask<bool> RemoveAsync(
        string indexShardId,
        string indexTerm,
        TKey primaryKey,
        CancellationToken cancellationToken = default);

    /// <summary>Queries one index shard.</summary>
    ValueTask<IReadOnlyList<ShardedIndexEntry<TKey, TValue>>> QueryAsync(
        string indexShardId,
        string indexTerm,
        CancellationToken cancellationToken = default);
}
