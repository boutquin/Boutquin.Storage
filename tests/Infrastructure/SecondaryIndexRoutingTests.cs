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
namespace Boutquin.Storage.Infrastructure.Tests;

public sealed class SecondaryIndexRoutingTests
{
    private sealed class BlockingQueryIndex(int expectedQueries) : IShardedSecondaryIndex<int, string>
    {
        private readonly TaskCompletionSource _allQueriesStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _started;

        public Task AllQueriesStarted => _allQueriesStarted.Task;

        public void Release() => _release.TrySetResult();

        public ValueTask UpsertAsync(
            string indexShardId,
            string indexTerm,
            ShardedIndexEntry<int, string> entry,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask<bool> RemoveAsync(
            string indexShardId,
            string indexTerm,
            int primaryKey,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(false);

        public async ValueTask<IReadOnlyList<ShardedIndexEntry<int, string>>> QueryAsync(
            string indexShardId,
            string indexTerm,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _started) == expectedQueries)
            {
                _allQueriesStarted.TrySetResult();
            }

            await _release.Task.WaitAsync(cancellationToken);
            return [];
        }
    }

    [Fact]
    public async Task LocalRouter_ScatterGathersEveryDataShard()
    {
        var index = new InMemoryShardedSecondaryIndex<int, string>();
        var router = new LocalSecondaryIndexRouter<int, string>(index);
        await router.UpsertAsync("city:Toronto", new ShardedIndexEntry<int, string>("shard-a", 1, "Ada"));
        await router.UpsertAsync("city:Toronto", new ShardedIndexEntry<int, string>("shard-b", 2, "Grace"));

        var results = await router.QueryAsync("city:Toronto", ["shard-a", "shard-b"]);

        results.Select(x => x.PrimaryKey).Should().Equal(1, 2);
    }

    [Fact]
    public async Task GlobalRouter_UsesOneIndexShardAndRetainsSourceShard()
    {
        var index = new InMemoryShardedSecondaryIndex<int, string>();
        var router = new GlobalSecondaryIndexRouter<int, string>(index, ["global-index"]);
        await router.UpsertAsync("city:Toronto", new ShardedIndexEntry<int, string>("shard-a", 1, "Ada"));
        await router.UpsertAsync("city:Toronto", new ShardedIndexEntry<int, string>("shard-b", 2, "Grace"));

        var results = await router.QueryAsync("city:Toronto");

        results.Select(x => x.SourceShardId).Should().Equal("shard-a", "shard-b");
        (await index.QueryAsync("shard-a", "city:Toronto")).Should().BeEmpty();
    }

    [Fact]
    public async Task LocalRouter_DeduplicatesRepeatedShardIds()
    {
        var index = new InMemoryShardedSecondaryIndex<int, string>();
        var router = new LocalSecondaryIndexRouter<int, string>(index);
        await router.UpsertAsync("role:admin", new ShardedIndexEntry<int, string>("shard-a", 1, "Ada"));

        var results = await router.QueryAsync("role:admin", ["shard-a", "shard-a"]);

        results.Should().ContainSingle();
    }

    [Fact]
    public async Task LocalRouter_StartsEveryShardQueryBeforeAwaitingResults()
    {
        var index = new BlockingQueryIndex(expectedQueries: 2);
        var router = new LocalSecondaryIndexRouter<int, string>(index);

        var query = router.QueryAsync("role:admin", ["shard-a", "shard-b"]);
        try
        {
            await index.AllQueriesStarted.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            index.Release();
        }

        (await query).Should().BeEmpty();
    }
}
