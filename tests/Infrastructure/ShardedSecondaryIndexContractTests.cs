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

/// <summary>
/// Pins the partitioned-global-index and entry-maintenance contract of the sharded secondary
/// indexes: each term's postings live on one term-chosen index shard, and a record that changes
/// its indexed value is withdrawn from the old term.
/// </summary>
public sealed class ShardedSecondaryIndexContractTests
{
    private static readonly string[] s_globalShards = ["gidx-0", "gidx-1", "gidx-2"];

    private static ShardedIndexEntry<int, string> Entry(string shard, int key) => new(shard, key, $"v{key}");

    private static async Task<string[]> ShardsHoldingAsync(
        InMemoryShardedSecondaryIndex<int, string> index,
        IEnumerable<string> shardIds,
        string term)
    {
        var holders = new List<string>();
        foreach (var shardId in shardIds)
        {
            if ((await index.QueryAsync(shardId, term)).Count > 0)
            {
                holders.Add(shardId);
            }
        }

        return [.. holders];
    }

    [Fact]
    public async Task GlobalRouter_PlacesEachTermOnExactlyOneIndexShard_AndSpreadsTermsAcrossShards()
    {
        var index = new InMemoryShardedSecondaryIndex<int, string>();
        var router = new GlobalSecondaryIndexRouter<int, string>(index, s_globalShards);
        var terms = Enumerable.Range(0, 60).Select(i => $"city:{i:D3}").ToArray();

        for (var i = 0; i < terms.Length; i++)
        {
            await router.UpsertAsync(terms[i], Entry("shard-a", i));
        }

        var used = new HashSet<string>();
        for (var i = 0; i < terms.Length; i++)
        {
            var holders = await ShardsHoldingAsync(index, s_globalShards, terms[i]);
            holders.Should().ContainSingle($"term {terms[i]} must be stored on exactly one index shard");
            holders[0].Should().Be(router.GetIndexShardId(terms[i]));
            (await router.QueryAsync(terms[i])).Select(e => e.PrimaryKey).Should().Equal(i);
            used.Add(holders[0]);
        }

        used.Should().HaveCountGreaterThan(1, "a global index must be sharded, not held on one node");
    }

    [Fact]
    public async Task GlobalRouter_WithTermRangePartitioner_RoutesOppositeEndsOfTheAlphabetToDifferentShards()
    {
        var index = new InMemoryShardedSecondaryIndex<int, string>();
        var ranges = new RangePartitioner<string>(["n"]);
        var router = new GlobalSecondaryIndexRouter<int, string>(index, ["low", "high"], ranges);

        await router.UpsertAsync("aqua", Entry("shard-a", 1));
        await router.UpsertAsync("zinc", Entry("shard-a", 2));

        (await index.QueryAsync("low", "aqua")).Select(e => e.PrimaryKey).Should().Equal(1);
        (await index.QueryAsync("high", "zinc")).Select(e => e.PrimaryKey).Should().Equal(2);
        (await index.QueryAsync("high", "aqua")).Should().BeEmpty();
        (await index.QueryAsync("low", "zinc")).Should().BeEmpty();
        (await router.QueryAsync("zinc")).Select(e => e.PrimaryKey).Should().Equal(2);
    }

    [Fact]
    public async Task GlobalRouter_WithOneIndexShard_IsTheOnePartitionCase()
    {
        var index = new InMemoryShardedSecondaryIndex<int, string>();
        var router = new GlobalSecondaryIndexRouter<int, string>(index, ["only"]);

        await router.UpsertAsync("aqua", Entry("shard-a", 1));
        await router.UpsertAsync("zinc", Entry("shard-a", 2));

        router.GetIndexShardId("aqua").Should().Be("only");
        router.GetIndexShardId("zinc").Should().Be("only");
        (await index.QueryAsync("only", "zinc")).Should().ContainSingle();
    }

    [Fact]
    public void GlobalRouter_RejectsInvalidShardListsAndMismatchedPartitioners()
    {
        var index = new InMemoryShardedSecondaryIndex<int, string>();

        var empty = () => new GlobalSecondaryIndexRouter<int, string>(index, []);
        var blank = () => new GlobalSecondaryIndexRouter<int, string>(index, ["a", " "]);
        var mismatch = () => new GlobalSecondaryIndexRouter<int, string>(
            index, ["a", "b", "c"], new HashPartitioner<string>(2));

        empty.Should().Throw<ArgumentException>();
        blank.Should().Throw<ArgumentException>();
        mismatch.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task InMemoryIndex_RemoveAsync_WithdrawsOnlyThatRecordsEntry()
    {
        var index = new InMemoryShardedSecondaryIndex<int, string>();
        await index.UpsertAsync("s", "city:Toronto", Entry("shard-a", 1));
        await index.UpsertAsync("s", "city:Toronto", Entry("shard-a", 2));

        (await index.RemoveAsync("s", "city:Toronto", 1)).Should().BeTrue();
        (await index.RemoveAsync("s", "city:Toronto", 1)).Should().BeFalse();
        (await index.RemoveAsync("s", "city:Absent", 2)).Should().BeFalse();
        (await index.RemoveAsync("other", "city:Toronto", 2)).Should().BeFalse();

        (await index.QueryAsync("s", "city:Toronto")).Select(e => e.PrimaryKey).Should().Equal(2);
    }

    [Fact]
    public async Task LocalRouter_ReplaceAsync_WithdrawsTheRecordFromItsOldTerm()
    {
        var index = new InMemoryShardedSecondaryIndex<int, string>();
        var router = new LocalSecondaryIndexRouter<int, string>(index);
        await router.UpsertAsync("city:Toronto", Entry("shard-a", 7));
        await router.UpsertAsync("city:Toronto", Entry("shard-a", 8));

        await router.ReplaceAsync("city:Toronto", "city:Paris", Entry("shard-a", 7));

        (await router.QueryAsync("city:Toronto", ["shard-a"])).Select(e => e.PrimaryKey).Should().Equal(8);
        (await router.QueryAsync("city:Paris", ["shard-a"])).Select(e => e.PrimaryKey).Should().Equal(7);
    }

    [Fact]
    public async Task LocalRouter_RemoveAsync_LeavesOtherShardsUntouched()
    {
        var index = new InMemoryShardedSecondaryIndex<int, string>();
        var router = new LocalSecondaryIndexRouter<int, string>(index);
        await router.UpsertAsync("role:admin", Entry("shard-a", 1));
        await router.UpsertAsync("role:admin", Entry("shard-b", 1));

        (await router.RemoveAsync("shard-a", "role:admin", 1)).Should().BeTrue();

        (await router.QueryAsync("role:admin", ["shard-a", "shard-b"]))
            .Select(e => e.SourceShardId).Should().Equal("shard-b");
    }

    [Fact]
    public async Task GlobalRouter_ReplaceAsync_MovesTheEntryBetweenTermsOnDifferentIndexShards()
    {
        var index = new InMemoryShardedSecondaryIndex<int, string>();
        var router = new GlobalSecondaryIndexRouter<int, string>(
            index, ["low", "high"], new RangePartitioner<string>(["n"]));
        await router.UpsertAsync("aqua", Entry("shard-a", 7));
        await router.UpsertAsync("aqua", Entry("shard-a", 8));

        await router.ReplaceAsync("aqua", "zinc", Entry("shard-a", 7));

        (await router.QueryAsync("aqua")).Select(e => e.PrimaryKey).Should().Equal(8);
        (await router.QueryAsync("zinc")).Select(e => e.PrimaryKey).Should().Equal(7);
        (await index.QueryAsync("low", "zinc")).Should().BeEmpty();
    }

    [Fact]
    public async Task Routers_ReplaceAsync_WithTheSameTermKeepsTheEntry()
    {
        var index = new InMemoryShardedSecondaryIndex<int, string>();
        var local = new LocalSecondaryIndexRouter<int, string>(index);
        var global = new GlobalSecondaryIndexRouter<int, string>(index, s_globalShards);
        await local.UpsertAsync("t", Entry("shard-a", 1));
        await global.UpsertAsync("t", Entry("shard-a", 1));

        await local.ReplaceAsync("t", "t", Entry("shard-a", 1));
        await global.ReplaceAsync("t", "t", Entry("shard-a", 1));

        (await local.QueryAsync("t", ["shard-a"])).Should().ContainSingle();
        (await global.QueryAsync("t")).Should().ContainSingle();
    }

    [Fact]
    public async Task GlobalRouter_RemoveAsync_WithdrawsFromTheTermsShard()
    {
        var index = new InMemoryShardedSecondaryIndex<int, string>();
        var router = new GlobalSecondaryIndexRouter<int, string>(index, s_globalShards);
        await router.UpsertAsync("t", Entry("shard-a", 1));

        (await router.RemoveAsync("t", 1)).Should().BeTrue();
        (await router.RemoveAsync("t", 1)).Should().BeFalse();

        (await router.QueryAsync("t")).Should().BeEmpty();
    }
}
