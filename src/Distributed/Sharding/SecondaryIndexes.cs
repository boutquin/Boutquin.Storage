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
using Boutquin.Storage.Distributed.Partitioning;

namespace Boutquin.Storage.Distributed.Sharding;

/// <summary>An in-memory sharded secondary index for the reference routers.</summary>
public sealed class InMemoryShardedSecondaryIndex<TKey, TValue> : IShardedSecondaryIndex<TKey, TValue>
    where TKey : notnull
{
    private readonly ConcurrentDictionary<string,
        ConcurrentDictionary<string, ConcurrentDictionary<TKey, ShardedIndexEntry<TKey, TValue>>>> _shards =
        new(StringComparer.Ordinal);

    /// <inheritdoc />
    public ValueTask UpsertAsync(
        string indexShardId,
        string indexTerm,
        ShardedIndexEntry<TKey, TValue> entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexShardId);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexTerm);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.SourceShardId);
        cancellationToken.ThrowIfCancellationRequested();

        var shard = _shards.GetOrAdd(
            indexShardId,
            static _ => new ConcurrentDictionary<string, ConcurrentDictionary<TKey, ShardedIndexEntry<TKey, TValue>>>(
                StringComparer.Ordinal));
        var term = shard.GetOrAdd(
            indexTerm,
            static _ => new ConcurrentDictionary<TKey, ShardedIndexEntry<TKey, TValue>>());
        term[entry.PrimaryKey] = entry;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<bool> RemoveAsync(
        string indexShardId,
        string indexTerm,
        TKey primaryKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexShardId);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexTerm);
        ArgumentNullException.ThrowIfNull(primaryKey);
        cancellationToken.ThrowIfCancellationRequested();

        var removed = _shards.TryGetValue(indexShardId, out var shard)
            && shard.TryGetValue(indexTerm, out var term)
            && term.TryRemove(primaryKey, out _);
        return ValueTask.FromResult(removed);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<ShardedIndexEntry<TKey, TValue>>> QueryAsync(
        string indexShardId,
        string indexTerm,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexShardId);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexTerm);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_shards.TryGetValue(indexShardId, out var shard)
            || !shard.TryGetValue(indexTerm, out var term))
        {
            return ValueTask.FromResult<IReadOnlyList<ShardedIndexEntry<TKey, TValue>>>([]);
        }

        IReadOnlyList<ShardedIndexEntry<TKey, TValue>> result = term.Values
            .OrderBy(entry => entry.SourceShardId, StringComparer.Ordinal)
            .ThenBy(entry => string.Create(CultureInfo.InvariantCulture, $"{entry.PrimaryKey}"), StringComparer.Ordinal)
            .ToArray();
        return ValueTask.FromResult(result);
    }
}

/// <summary>Stores each index entry beside its data and scatter-gathers reads.</summary>
public sealed class LocalSecondaryIndexRouter<TKey, TValue>
    where TKey : notnull
{
    private readonly IShardedSecondaryIndex<TKey, TValue> _index;

    /// <summary>Initializes a local-index router.</summary>
    public LocalSecondaryIndexRouter(IShardedSecondaryIndex<TKey, TValue> index)
    {
        ArgumentNullException.ThrowIfNull(index);
        _index = index;
    }

    /// <summary>Writes an entry to its source shard's local index.</summary>
    public ValueTask UpsertAsync(
        string indexTerm,
        ShardedIndexEntry<TKey, TValue> entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return _index.UpsertAsync(entry.SourceShardId, indexTerm, entry, cancellationToken);
    }

    /// <summary>Withdraws a record's entry from a term in its source shard's local index.</summary>
    /// <returns><see langword="true"/> when the entry was present and removed.</returns>
    public ValueTask<bool> RemoveAsync(
        string sourceShardId,
        string indexTerm,
        TKey primaryKey,
        CancellationToken cancellationToken = default) =>
        _index.RemoveAsync(sourceShardId, indexTerm, primaryKey, cancellationToken);

    /// <summary>
    /// Records that a record's indexed value changed: adds the entry under the new term, then
    /// withdraws it from the previous term of the same source shard.
    /// </summary>
    /// <remarks>
    /// The new posting is written first so a concurrent reader never sees the record under
    /// neither term. Replacing a term with itself only refreshes the entry.
    /// </remarks>
    public async ValueTask ReplaceAsync(
        string previousIndexTerm,
        string indexTerm,
        ShardedIndexEntry<TKey, TValue> entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(previousIndexTerm);
        ArgumentNullException.ThrowIfNull(entry);
        await UpsertAsync(indexTerm, entry, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(previousIndexTerm, indexTerm, StringComparison.Ordinal))
        {
            await _index.RemoveAsync(entry.SourceShardId, previousIndexTerm, entry.PrimaryKey, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Queries all supplied local index shards concurrently.</summary>
    public Task<IReadOnlyList<ShardedIndexEntry<TKey, TValue>>> QueryAsync(
        string indexTerm,
        IReadOnlyCollection<string> shardIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexTerm);
        ArgumentNullException.ThrowIfNull(shardIds);
        return QueryCoreAsync(indexTerm, shardIds, cancellationToken);
    }

    private async Task<IReadOnlyList<ShardedIndexEntry<TKey, TValue>>> QueryCoreAsync(
        string indexTerm,
        IReadOnlyCollection<string> shardIds,
        CancellationToken cancellationToken)
    {
        var tasks = shardIds
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(shardId => _index.QueryAsync(shardId, indexTerm, cancellationToken).AsTask())
            .ToArray();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results
            .SelectMany(entries => entries)
            .OrderBy(entry => entry.SourceShardId, StringComparer.Ordinal)
            .ThenBy(entry => string.Create(CultureInfo.InvariantCulture, $"{entry.PrimaryKey}"), StringComparer.Ordinal)
            .ToArray();
    }
}

/// <summary>
/// Maintains a global secondary index partitioned by term: each term's postings live on one
/// index shard, chosen by a term partitioner (term range or term hash).
/// </summary>
/// <remarks>
/// With no partitioner supplied the term is hashed across the index shards
/// (<see cref="HashPartitioner{TKey}"/>); pass a <see cref="RangePartitioner{TKey}"/> to place
/// contiguous term ranges together. A single index shard is the one-partition case, which keeps
/// every term on one node and so concentrates load.
/// </remarks>
public sealed class GlobalSecondaryIndexRouter<TKey, TValue>
    where TKey : notnull
{
    private readonly string[] _indexShardIds;
    private readonly IPartitioner<string> _termPartitioner;
    private readonly IShardedSecondaryIndex<TKey, TValue> _index;

    /// <summary>Initializes a global-index router.</summary>
    /// <param name="index">The backing sharded index.</param>
    /// <param name="globalIndexShardIds">The index shards, in partition order.</param>
    /// <param name="termPartitioner">
    /// Maps a term to a partition of <paramref name="globalIndexShardIds"/>; defaults to a term hash.
    /// Its partition count must equal the number of index shards.
    /// </param>
    public GlobalSecondaryIndexRouter(
        IShardedSecondaryIndex<TKey, TValue> index,
        IReadOnlyList<string> globalIndexShardIds,
        IPartitioner<string>? termPartitioner = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(globalIndexShardIds);
        if (globalIndexShardIds.Count == 0)
        {
            throw new ArgumentException("At least one global index shard is required.", nameof(globalIndexShardIds));
        }

        foreach (var shardId in globalIndexShardIds)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(shardId, nameof(globalIndexShardIds));
        }

        var partitioner = termPartitioner ?? new HashPartitioner<string>(globalIndexShardIds.Count);
        if (partitioner.PartitionCount != globalIndexShardIds.Count)
        {
            throw new ArgumentException(
                "The term partitioner's partition count must equal the number of global index shards.",
                nameof(termPartitioner));
        }

        _index = index;
        _indexShardIds = [.. globalIndexShardIds];
        _termPartitioner = partitioner;
    }

    /// <summary>Gets the index shard that holds a term's postings.</summary>
    public string GetIndexShardId(string indexTerm)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexTerm);
        return _indexShardIds[_termPartitioner.GetPartition(indexTerm)];
    }

    /// <summary>Writes an entry to the index shard chosen by its term.</summary>
    public ValueTask UpsertAsync(
        string indexTerm,
        ShardedIndexEntry<TKey, TValue> entry,
        CancellationToken cancellationToken = default) =>
        _index.UpsertAsync(GetIndexShardId(indexTerm), indexTerm, entry, cancellationToken);

    /// <summary>Withdraws a record's entry from a term's postings.</summary>
    /// <returns><see langword="true"/> when the entry was present and removed.</returns>
    public ValueTask<bool> RemoveAsync(
        string indexTerm,
        TKey primaryKey,
        CancellationToken cancellationToken = default) =>
        _index.RemoveAsync(GetIndexShardId(indexTerm), indexTerm, primaryKey, cancellationToken);

    /// <summary>
    /// Records that a record's indexed value changed: adds the entry under the new term, then
    /// withdraws it from the previous term, which may live on a different index shard.
    /// </summary>
    /// <remarks>
    /// The new posting is written first so a concurrent reader never sees the record under
    /// neither term. The two writes are not atomic across index shards; production systems need
    /// coordinated index maintenance. Replacing a term with itself only refreshes the entry.
    /// </remarks>
    public async ValueTask ReplaceAsync(
        string previousIndexTerm,
        string indexTerm,
        ShardedIndexEntry<TKey, TValue> entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(previousIndexTerm);
        ArgumentNullException.ThrowIfNull(entry);
        await UpsertAsync(indexTerm, entry, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(previousIndexTerm, indexTerm, StringComparison.Ordinal))
        {
            await RemoveAsync(previousIndexTerm, entry.PrimaryKey, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Queries the index shard chosen by the term.</summary>
    public ValueTask<IReadOnlyList<ShardedIndexEntry<TKey, TValue>>> QueryAsync(
        string indexTerm,
        CancellationToken cancellationToken = default) =>
        _index.QueryAsync(GetIndexShardId(indexTerm), indexTerm, cancellationToken);
}
