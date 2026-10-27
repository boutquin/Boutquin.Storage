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
using Boutquin.Storage.Domain.Interfaces.Search;
using Boutquin.Storage.Infrastructure.Search.Vector;

namespace Boutquin.Storage.Infrastructure.Tests.Search;

public sealed class VectorSearchTests
{
    private readonly ITestOutputHelper _output;

    public VectorSearchTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void DistanceMetrics_ShouldReturnExpectedValues()
    {
        new L2Distance().Distance([0, 0], [3, 4]).Should().Be(5);
        new DotProductDistance().Distance([1, 2], [3, 4]).Should().Be(-11);
        new CosineDistance().Distance([1, 0], [0, 1]).Should().BeApproximately(1, 0.0001F);
    }

    [Fact]
    public void CosineDistance_ShouldDefineZeroVectorSemantics()
    {
        var metric = new CosineDistance();

        metric.Distance([0, 0], [0, 0]).Should().Be(0);
        metric.Distance([0, 0], [1, 0]).Should().Be(1);
    }

    [Fact]
    public void Metrics_ShouldRejectMismatchedOrNonFiniteVectors()
    {
        var metric = new L2Distance();

        var mismatched = () => metric.Distance([1], [1, 2]);
        var nonFinite = () => metric.Distance([float.NaN], [1]);

        mismatched.Should().Throw<ArgumentException>();
        nonFinite.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void FlatIndex_ShouldReturnExactNeighborsWithDeterministicTies()
    {
        var index = new FlatVectorIndex<string>(2, new L2Distance());
        index.Upsert("first", [1, 0]);
        index.Upsert("second", [-1, 0]);
        index.Upsert("far", [0, 10]);

        index.Search([0, 0], 2).Select(static result => result.Id).Should().Equal("first", "second");
    }

    [Fact]
    public void VectorIndexes_ShouldBreakEqualDistanceTiesByInsertionOrder()
    {
        foreach (var index in CreateIndexes())
        {
            index.Upsert(20, [1, 0]);
            index.Upsert(10, [-1, 0]);

            index.Search([0, 0], 2).Select(static result => result.Id).Should().Equal(20, 10);
        }
    }

    [Fact]
    public void VectorIndexes_ShouldReplaceAndDeleteEntries()
    {
        foreach (var index in CreateIndexes())
        {
            index.Upsert(1, [0, 0]);
            index.Upsert(2, [10, 10]);
            index.Upsert(1, [9, 9]);
            index.Search([9, 9], 1)[0].Id.Should().Be(1);
            index.Remove(1).Should().BeTrue();
            index.Remove(1).Should().BeFalse();
            index.Search([9, 9], 1)[0].Id.Should().Be(2);
        }
    }

    [Fact]
    public void VectorIndexes_ShouldRejectWrongDimensionsAndNonFiniteValues()
    {
        foreach (var index in CreateIndexes())
        {
            var wrongDimension = () => index.Upsert(1, [1]);
            var nonFinite = () => index.Upsert(1, [1, float.PositiveInfinity]);
            wrongDimension.Should().Throw<ArgumentException>();
            nonFinite.Should().Throw<ArgumentException>();
        }
    }

    [Fact]
    public void IvfRecall_ShouldBeMonotonicAsProbeCountIncreases()
    {
        var (vectors, queries) = CreateClusteredData();
        var exact = new FlatVectorIndex<int>(4, new L2Distance());
        var ivf = new IvfVectorIndex<int>(4, new L2Distance(), partitionCount: 8, probeCount: 1);
        foreach (var (id, vector) in vectors)
        {
            exact.Upsert(id, vector);
            ivf.Upsert(id, vector);
        }

        var recallAtOne = Recall(queries, exact, (query, count) => ivf.Search(query, count, 1));
        var recallAtFour = Recall(queries, exact, (query, count) => ivf.Search(query, count, 4));
        var recallAtEight = Recall(queries, exact, (query, count) => ivf.Search(query, count, 8));

        _output.WriteLine($"IVF recall@5: probes=1 {recallAtOne:P1}; probes=4 {recallAtFour:P1}; probes=8 {recallAtEight:P1}");

        recallAtFour.Should().BeGreaterThanOrEqualTo(recallAtOne);
        recallAtEight.Should().BeGreaterThanOrEqualTo(recallAtFour);
        recallAtEight.Should().Be(1);
    }

    [Fact]
    public void Hnsw_ShouldAchieveUsefulRecallOnClusteredData()
    {
        var (vectors, queries) = CreateClusteredData();
        var exact = new FlatVectorIndex<int>(4, new L2Distance());
        var hnsw = new HnswVectorIndex<int>(4, new L2Distance(), maxConnections: 12, efConstruction: 80, efSearch: 64);
        foreach (var (id, vector) in vectors)
        {
            exact.Upsert(id, vector);
            hnsw.Upsert(id, vector);
        }

        var recall = Recall(queries, exact, (query, count) => hnsw.Search(query, count));
        _output.WriteLine($"HNSW recall@5: efSearch=64 {recall:P1}");
        recall.Should().BeGreaterThanOrEqualTo(0.90);
    }

    private static IVectorIndex<int>[] CreateIndexes() =>
    [
        new FlatVectorIndex<int>(2, new L2Distance()),
        new IvfVectorIndex<int>(2, new L2Distance(), partitionCount: 2, probeCount: 2),
        new HnswVectorIndex<int>(2, new L2Distance())
    ];

    private static double Recall(
        IReadOnlyList<float[]> queries,
        FlatVectorIndex<int> exact,
        Func<float[], int, IReadOnlyList<(int Id, float Distance)>> approximate)
    {
        const int count = 5;
        var matches = 0;
        foreach (var query in queries)
        {
            var expected = exact.Search(query, count).Select(static result => result.Id).ToHashSet();
            matches += approximate(query, count).Count(result => expected.Contains(result.Id));
        }

        return matches / (double)(queries.Count * count);
    }

    private static (IReadOnlyList<(int Id, float[] Vector)> Vectors, IReadOnlyList<float[]> Queries) CreateClusteredData()
    {
        var random = new Random(731);
        var vectors = new List<(int, float[])>();
        for (var cluster = 0; cluster < 8; cluster++)
        {
            for (var point = 0; point < 25; point++)
            {
                vectors.Add((vectors.Count,
                [
                    cluster * 10 + (float)random.NextDouble(),
                    cluster * -7 + (float)random.NextDouble(),
                    cluster * 3 + (float)random.NextDouble(),
                    cluster * 5 + (float)random.NextDouble()
                ]));
            }
        }

        var queries = vectors.Where((_, index) => index % 10 == 0)
            .Select(item => item.Item2.Select(value => value + 0.01F).ToArray())
            .ToArray();
        return (vectors, queries);
    }
}
