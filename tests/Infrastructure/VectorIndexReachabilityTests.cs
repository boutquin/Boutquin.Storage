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
using Boutquin.Storage.Infrastructure.Search.Vector;

namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// Pins that deleting or replacing HNSW vectors never disconnects the vectors that remain: a stored
/// vector queried with itself at a beam as wide as the live set explores the entry point's whole
/// reachable component, so a miss there means the vector cannot be returned by any search.
/// </summary>
public sealed class VectorIndexReachabilityTests
{
    private const int PointCount = 200;

    [Fact]
    public void Hnsw_Remove_ShouldKeepEverySurvivorReachableThatWasReachableBefore()
    {
        var lost = new List<string>();
        for (var seed = 0; seed < 20; seed++)
        {
            var (index, points) = Build(seed);
            var reachableBefore = SelfFound(index, points);
            var random = new Random(1_000 + seed);
            foreach (var id in points.Keys.Where(_ => random.NextDouble() < 0.4).ToArray())
            {
                index.Remove(id).Should().BeTrue();
                points.Remove(id);
            }

            var reachableAfter = SelfFound(index, points);
            lost.AddRange(reachableBefore.Where(id => points.ContainsKey(id) && !reachableAfter.Contains(id)).Select(id => $"seed {seed} id {id}"));
        }

        lost.Should().BeEmpty("removing other vectors must not disconnect a survivor");
    }

    [Fact]
    public void Hnsw_Upsert_ShouldKeepEveryUntouchedVectorReachableThatWasReachableBefore()
    {
        var lost = new List<string>();
        for (var seed = 0; seed < 20; seed++)
        {
            var (index, points) = Build(seed);
            var reachableBefore = SelfFound(index, points);
            var random = new Random(2_000 + seed);
            var replaced = points.Keys.Where(_ => random.NextDouble() < 0.4).ToHashSet();
            foreach (var id in replaced)
            {
                points[id] = [10_000 + id, 10_000];
                index.Upsert(id, points[id]);
            }

            var reachableAfter = SelfFound(index, points);
            lost.AddRange(reachableBefore.Where(id => !replaced.Contains(id) && !reachableAfter.Contains(id)).Select(id => $"seed {seed} id {id}"));
        }

        lost.Should().BeEmpty("replacing other vectors must not disconnect an untouched vector");
    }

    [Fact]
    public void Hnsw_Search_ShouldReturnLiveVectorsWhenTheBeamReachesOnlyRoutingPoints()
    {
        // Pruning at maxConnections 2 leaves the entry point's layer-0 component holding only the
        // vectors removed below, while ids 1, 3 and 5 stay live elsewhere in the graph.
        var (index, live) = BuildRoutingOnlyComponent();
        var exact = new FlatVectorIndex<int>(2, new L2Distance());
        foreach (var (id, vector) in live)
        {
            exact.Upsert(id, vector);
        }

        index.Search([7, 95], 3, 100_000).Should().Equal(exact.Search([7, 95], 3),
            "a populated index must return its nearest live vectors, never an empty list");
    }

    [Fact]
    public void Hnsw_Upsert_ShouldLinkNewVectorsWhenTheBeamReachesOnlyRoutingPoints()
    {
        var (index, _) = BuildRoutingOnlyComponent();
        var unfindable = new List<int>();
        for (var k = 0; k < 20; k++)
        {
            float[] vector = [60 + k, 60 - k];
            index.Upsert(100 + k, vector);
        }

        for (var k = 0; k < 20; k++)
        {
            if (index.Search([60 + k, 60 - k], 1, 100_000) is not [(var id, _)] || id != 100 + k)
            {
                unfindable.Add(100 + k);
            }
        }

        unfindable.Should().BeEmpty("an upserted vector must be linked into the graph a search starts from");
    }

    [Fact]
    public void Hnsw_Search_ShouldReturnEveryLiveVectorWhenPruningSplitsTheGraph()
    {
        // At maxConnections 2 these seven points leave layer 0 split into components; once id 1 is
        // removed, a search starting in one component cannot reach the live vectors of the other.
        var index = new HnswVectorIndex<int>(1, new L2Distance(), maxConnections: 2, randomSeed: 819);
        var exact = new FlatVectorIndex<int>(1, new L2Distance());
        var live = new Dictionary<int, float[]>();
        float[][] vectors = [[9], [28], [4], [99], [42], [15], [22]];
        for (var id = 0; id < vectors.Length; id++)
        {
            index.Upsert(id, vectors[id]);
            exact.Upsert(id, vectors[id]);
            live[id] = vectors[id];
        }

        index.Remove(1).Should().BeTrue();
        exact.Remove(1);
        live.Remove(1);

        foreach (var vector in live.Values)
        {
            index.Search(vector, live.Count, 100_000).Should().Equal(exact.Search(vector, live.Count),
                "a populated index must not answer with fewer results than it holds");
        }
    }

    [Fact]
    public void Hnsw_Upsert_ShouldLinkNewVectorsInsertedAfterTheEntryComponentIsRemoved()
    {
        // Ids 3, 5, 7, 8 and 10 form the entry point's whole layer-0 component at maxConnections 2;
        // removing them leaves a component of routing points only, which every insert's beam reaches.
        var index = new HnswVectorIndex<int>(1, new L2Distance(), maxConnections: 2, randomSeed: 241);
        float[][] vectors = [[60], [43], [66], [17], [39], [16], [88], [2], [25], [71], [13]];
        for (var id = 0; id < vectors.Length; id++)
        {
            index.Upsert(id, vectors[id]);
        }

        foreach (var id in new[] { 3, 5, 7, 8, 10 })
        {
            index.Remove(id).Should().BeTrue();
        }

        float[][] inserted = [[99.5F], [77.5F], [84.5F], [68.5F], [0.5F], [59.5F]];
        for (var k = 0; k < inserted.Length; k++)
        {
            index.Upsert(100 + k, inserted[k]);
        }

        var unfindable = Enumerable.Range(0, inserted.Length)
            .Where(k => index.Search(inserted[k], 1, 100_000) is not [(var id, _)] || id != 100 + k)
            .Select(static k => 100 + k)
            .ToArray();
        unfindable.Should().BeEmpty("an insert whose beam reaches only routing points must still be linked to them");
    }

    [Fact]
    public void Hnsw_Upsert_ShouldKeepAnInEdgeOnLayerZeroWhenNeighbourListsAreFull()
    {
        // With one cap of maxConnections (2) on every layer, both neighbours [3] links to already
        // hold two nearer vectors, so pruning drops their edges back to [3] and no search reaches
        // it. Layer 0 carries twice the cap (Malkov and Yashunin's Mmax0 = 2·M).
        var index = new HnswVectorIndex<int>(1, new L2Distance(), maxConnections: 2);
        float[][] vectors = [[-3], [-1], [-2], [3]];
        for (var id = 0; id < vectors.Length; id++)
        {
            index.Upsert(id, vectors[id]);
        }

        for (var id = 0; id < vectors.Length; id++)
        {
            index.Search(vectors[id], 1, 100).Should().ContainSingle()
                .Which.Id.Should().Be(id, "every stored vector must be returned by its own exact query");
        }
    }

    [Fact]
    public void Hnsw_ShouldKeepEveryVectorReachableAmongExactDuplicates()
    {
        // Identical embeddings (repeated text, zero vectors) are ordinary input. Twenty copies per
        // cluster exceed every connection cap, so distance-zero neighbours would otherwise fill each
        // list and prune away the edges to every distinct vector inserted later.
        var lost = new List<string>();
        for (var seed = 0; seed < 10; seed++)
        {
            var random = new Random(seed);
            var index = new HnswVectorIndex<int>(2, new L2Distance(), randomSeed: seed);
            float[][] centers = [[0, 0], [10, 10], [20, 20]];
            var members = centers.Select(static _ => new List<int>()).ToArray();
            var order = Enumerable.Range(0, centers.Length).SelectMany(static cluster => Enumerable.Repeat(cluster, 20))
                .OrderBy(_ => random.Next()).ToArray();
            var next = 0;
            foreach (var cluster in order)
            {
                index.Upsert(next, centers[cluster]);
                members[cluster].Add(next++);
            }

            for (var k = 0; k < 10; k++)
            {
                float[] vector = [random.Next(0, 40) + 0.5F + k, random.Next(0, 40) + 0.25F];
                index.Upsert(next, vector);
                if (index.Search(vector, 1, 100_000) is not [(var id, _)] || id != next)
                {
                    lost.Add($"seed {seed} new id {next}");
                }

                next++;
            }

            for (var cluster = 0; cluster < centers.Length; cluster++)
            {
                var returned = index.Search(centers[cluster], members[cluster].Count, 100_000).Select(static result => result.Id).ToHashSet();
                lost.AddRange(members[cluster].Where(id => !returned.Contains(id)).Select(id => $"seed {seed} duplicate id {id}"));
            }
        }

        lost.Should().BeEmpty("every stored vector, duplicated or not, must be returned by its own exact query");
    }

    private static (HnswVectorIndex<int> Index, Dictionary<int, float[]> Live) BuildRoutingOnlyComponent()
    {
        var index = new HnswVectorIndex<int>(2, new L2Distance(), maxConnections: 2, randomSeed: 490);
        var points = new Dictionary<int, float[]>
        {
            [0] = [46, 15],
            [1] = [7, 95],
            [2] = [19, 37],
            [3] = [91, 35],
            [4] = [12, 15],
            [5] = [97, 19],
        };
        foreach (var (id, vector) in points)
        {
            index.Upsert(id, vector);
        }

        // Three removals against three survivors: removed nodes do not yet outnumber live ones,
        // so no rebuild runs and the removed vectors stay in the graph as routing points.
        foreach (var id in new[] { 4, 0, 2 })
        {
            index.Remove(id).Should().BeTrue();
            points.Remove(id);
        }

        return (index, points);
    }

    private static (HnswVectorIndex<int> Index, Dictionary<int, float[]> Points) Build(int seed)
    {
        var random = new Random(seed);
        var index = new HnswVectorIndex<int>(2, new L2Distance(), randomSeed: seed);
        var points = new Dictionary<int, float[]>();
        for (var id = 0; id < PointCount; id++)
        {
            points[id] = [(float)random.NextDouble() * 100, (float)random.NextDouble() * 100];
            index.Upsert(id, points[id]);
        }

        return (index, points);
    }

    private static HashSet<int> SelfFound(HnswVectorIndex<int> index, Dictionary<int, float[]> points) =>
        points.Where(point => index.Search(point.Value, 1, points.Count) is [var (id, _)] && id == point.Key)
            .Select(static point => point.Key)
            .ToHashSet();
}
