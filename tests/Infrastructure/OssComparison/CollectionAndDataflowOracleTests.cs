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

using Boutquin.Storage.Domain.ValueObjects.Streaming;
using Boutquin.Storage.Infrastructure.Search.Text;
using Boutquin.Storage.Infrastructure.Streaming.Joins;
using Boutquin.Storage.Infrastructure.Streaming.State;

namespace Boutquin.Storage.Infrastructure.Tests.OssComparison;

public sealed class CollectionAndDataflowOracleTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task MemoryStoresAndIndex_MatchDictionaryThroughMixedHistories(int implementation)
    {
        IKeyValueStore<SerializableWrapper<int>, SerializableWrapper<int>> store = implementation switch
        {
            0 => new InMemoryKeyValueStore<SerializableWrapper<int>, SerializableWrapper<int>>(),
            1 => new ConcurrentKeyValueStore<SerializableWrapper<int>, SerializableWrapper<int>>(),
            _ => new InMemoryStorageIndex<SerializableWrapper<int>, SerializableWrapper<int>>()
        };
        var model = new Dictionary<int, int>();
        var random = new Random(9023);
        for (var step = 1; step <= 250; step++)
        {
            var key = random.Next(1, 25);
            if (step % 4 == 0)
            {
                if (implementation == 0)
                {
                    // The append-only memory engine deliberately shares its file-backed
                    // sibling's deletion contract; a dictionary's wider API is not required.
                    await Assert.ThrowsAsync<NotSupportedException>(() => store.RemoveAsync(new SerializableWrapper<int>(key)))
                        .ConfigureAwait(true);
                }
                else
                {
                    model.Remove(key);
                    await store.RemoveAsync(new SerializableWrapper<int>(key)).ConfigureAwait(true);
                }
            }
            else
            {
                model[key] = step;
                await store.SetAsync(new SerializableWrapper<int>(key), new SerializableWrapper<int>(step)).ConfigureAwait(true);
            }
            if (step % 71 == 0)
            {
                model.Clear();
                await store.ClearAsync().ConfigureAwait(true);
            }
            for (var probe = 1; probe < 25; probe++)
            {
                var actual = await store.TryGetValueAsync(new SerializableWrapper<int>(probe)).ConfigureAwait(true);
                actual.Found.Should().Be(model.ContainsKey(probe));
                if (actual.Found) { actual.Value.Value.Should().Be(model[probe]); }
            }
        }
    }

    [Fact]
    public void PostingsList_MatchesOrderedDistinctMembershipModel()
    {
        var postings = new PostingsList<int>();
        var model = new List<int>();
        var random = new Random(132);
        for (var step = 0; step < 500; step++)
        {
            var key = random.Next(1, 30);
            if (step % 3 == 0) { postings.Remove(key).Should().Be(model.Remove(key)); }
            else
            {
                var absent = !model.Contains(key);
                postings.Add(key).Should().Be(absent);
                if (absent) { model.Add(key); }
            }
            postings.Documents.Should().Equal(model);
            postings.Count.Should().Be(model.Count);
            for (var probe = 1; probe < 30; probe++) { postings.Contains(probe).Should().Be(model.Contains(probe)); }
        }
    }

    [Fact]
    public async Task KWayMerge_MatchesOfflineLatestSourceRelation()
    {
        var random = new Random(57);
        var sources = new List<IAsyncEnumerable<KeyValuePair<int, int>>>();
        var expected = new SortedDictionary<int, int>();
        for (var source = 1; source <= 17; source++)
        {
            var keys = Enumerable.Range(0, 100).Select(_ => random.Next(1, 130)).Distinct().Order().ToArray();
            foreach (var key in keys) { expected[key] = source; }
            sources.Add(Async(keys.Select(key => new KeyValuePair<int, int>(key, source)).ToArray()));
        }
        var actual = new List<KeyValuePair<int, int>>();
        await foreach (var item in KWayMerge.MergeAsync(sources, Comparer<int>.Default).ConfigureAwait(true)) { actual.Add(item); }
        actual.Should().Equal(expected);
    }

    [Fact]
    public async Task StreamAndTableJoins_MatchNestedLoopOraclesWithDuplicatesAndBoundaryTimes()
    {
        var left = Enumerable.Range(0, 40).Select(i => new StreamRecord<int>(i, DateTimeOffset.UnixEpoch.AddSeconds(i * 2), i % 7)).ToArray();
        var right = Enumerable.Range(0, 30).Select(i => new StreamRecord<int>(i, DateTimeOffset.UnixEpoch.AddSeconds(i * 3), i % 7)).ToArray();
        var join = new StreamStreamJoin<int, int, int, (int, int)>(TimeSpan.FromSeconds(3), x => x, x => x, (l, r) => (l, r));
        var actual = new List<(long Offset, DateTimeOffset Time, (int, int) Value)>();
        await foreach (var row in join.JoinAsync(Async(left), Async(right)).ConfigureAwait(true))
        {
            actual.Add((row.Offset, row.EventTime, row.Value));
        }
        var expected = new List<(long, DateTimeOffset, (int, int))>();
        foreach (var l in left)
        {
            foreach (var r in right)
            {
                if (l.Value == r.Value && Math.Abs((l.EventTime - r.EventTime).TotalSeconds) <= 3)
                {
                    expected.Add((Math.Max(l.Offset, r.Offset), l.EventTime > r.EventTime ? l.EventTime : r.EventTime, (l.Value, r.Value)));
                }
            }
        }
        actual.Should().BeEquivalentTo(expected);

        var leftTable = new InMemoryStateStore<int, int>();
        var rightTable = new InMemoryStateStore<int, int>();
        for (var key = 0; key < 7; key++)
        {
            await leftTable.SetAsync(key, key * 2).ConfigureAwait(true);
            if (key % 2 == 0) { await rightTable.SetAsync(key, key * 3).ConfigureAwait(true); }
        }
        var tableJoin = new TableTableJoin<int, int, int, int>(leftTable, rightTable, (key, l, r) => l + r);
        (await tableJoin.JoinAsync().ConfigureAwait(true)).Order().Should().Equal(0, 10, 20, 30);
        var streamJoin = new StreamTableJoin<int, int, int, int>(rightTable, x => x, (l, r) => l + r);
        var values = new List<int>();
        await foreach (var row in streamJoin.ProcessAsync(Async(left)).ConfigureAwait(true)) { values.Add(row.Value); }
        values.Should().Equal(left.Where(row => row.Value % 2 == 0).Select(row => row.Value * 4));
    }

    private static async IAsyncEnumerable<T> Async<T>(IEnumerable<T> values)
    {
        foreach (var value in values) { yield return value; }
        await Task.CompletedTask.ConfigureAwait(true);
    }
}
