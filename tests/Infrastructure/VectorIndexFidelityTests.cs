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

namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// Pins the vector-index behaviours that small two-vector fixtures cannot reach: HNSW deletion once
/// neighbour pruning has made the graph asymmetric, the HNSW insert hand-off between layers, IVF list
/// membership after the final centroid update, and distance metrics on large finite components.
/// </summary>
public sealed class VectorIndexFidelityTests
{
    [Fact]
    public void Hnsw_Remove_ShouldNeverReturnRemovedIdsAfterPruning()
    {
        // Forty points on a line with the default maxConnections (8) prune every interior node, so
        // some nodes keep edges to neighbours that no longer link back to them.
        var index = new HnswVectorIndex<int>(1, new L2Distance());
        for (var id = 0; id < 40; id++)
        {
            index.Upsert(id, [id]);
        }

        for (var id = 0; id < 40; id++)
        {
            if (id != 20)
            {
                index.Remove(id).Should().BeTrue();
            }
        }

        index.Search([5], 5).Should().Equal((20, 15F));
        index.Search([5], 100, 1000).Should().Equal((20, 15F));
    }

    [Fact]
    public void Hnsw_Upsert_ShouldNeverReturnTheSupersededVector()
    {
        var metric = new L2Distance();
        var index = new HnswVectorIndex<int>(1, metric);
        var current = new Dictionary<int, float[]>();
        for (var id = 0; id < 40; id++)
        {
            current[id] = [id];
            index.Upsert(id, current[id]);
        }

        for (var id = 0; id < 40; id += 2)
        {
            current[id] = [10_000 + id];
            index.Upsert(id, current[id]);
        }

        float[] query = [5];
        var results = index.Search(query, 40, 1000);

        results.Select(static result => result.Id).Should().OnlyHaveUniqueItems();
        foreach (var (id, distance) in results)
        {
            distance.Should().Be(metric.Distance(query, current[id]), $"id {id} must be ranked by its current vector");
        }
    }

    [Fact]
    public void Hnsw_Insert_ShouldSeedEachLowerLayerFromTheWholeResultList()
    {
        // With efConstruction = 2 the insert-time result list on an upper layer holds two elements;
        // seeding the next layer from only the nearest one leaves (for this seeded level draw) one
        // stored vector unreachable from the entry point, so it is not found even by itself.
        float[][] points = [[94, 79], [59, 85], [92, 18], [46, 8], [39, 79], [26, 19]];
        var index = new HnswVectorIndex<int>(2, new L2Distance(), maxConnections: 2, efConstruction: 2, efSearch: 1, randomSeed: 42);
        for (var id = 0; id < points.Length; id++)
        {
            index.Upsert(id, points[id]);
        }

        for (var id = 0; id < points.Length; id++)
        {
            index.Search(points[id], 1, points.Length).Should().Equal([(id, 0F)], $"stored vector {id} queried with itself");
        }
    }

    [Fact]
    public void Ivf_ShouldListEachVectorUnderItsNearestFinalCentroid()
    {
        // One training iteration moves the centroids after the assignment step; 4.9 ends nearer
        // the centroid that absorbed 8, 9 and 10, so it must be listed there.
        float[][] points = [[0], [10], [4.9F], [9], [8], [-6]];
        var index = new IvfVectorIndex<int>(1, new L2Distance(), partitionCount: 2, probeCount: 1, trainingIterations: 1);
        for (var id = 0; id < points.Length; id++)
        {
            index.Upsert(id, points[id]);
        }

        for (var id = 0; id < points.Length; id++)
        {
            index.Search(points[id], 1, 1).Should().Equal([(id, 0F)], $"stored vector {id} queried with itself at probe=1");
        }
    }

    [Fact]
    public void Ivf_CentroidMean_ShouldStayFiniteForMembersNearFloatMaxValue()
    {
        // The mean of finite members never exceeds the largest member, but summing per-member
        // float quotients rounds past float.MaxValue for these member counts.
        var index = new IvfVectorIndex<int>(1, new L2Distance(), partitionCount: 1, probeCount: 1);
        for (var id = 0; id < 10; id++)
        {
            index.Upsert(id, [float.MaxValue]);
        }

        var nearest = () => index.Search([float.MaxValue], 1);
        nearest.Should().NotThrow("a centroid of finite members is finite").Which.Should().Equal([(0, 0F)]);
        index.Search([float.MaxValue], 10).Select(static result => result.Id).Should().BeEquivalentTo(Enumerable.Range(0, 10));
    }

    [Fact]
    public void CosineDistance_ShouldStayExactForLargeFiniteComponents()
    {
        var metric = new CosineDistance();

        metric.Distance([1e20F, 0], [1e20F, 0]).Should().BeApproximately(0, 1e-6F);
        metric.Distance([1e20F, 0], [-1e20F, 0]).Should().BeApproximately(2, 1e-6F);
        metric.Distance([1, 1], [2e19F, 2e19F]).Should().BeApproximately(0, 1e-6F);
    }

    [Fact]
    public void L2Distance_ShouldStayExactForLargeFiniteComponents()
    {
        var metric = new L2Distance();

        metric.Distance([2e19F, 0], [0, 0]).Should().Be(2e19F);
        metric.Distance([1e20F], [0]).Should().Be(1e20F);
    }

    [Fact]
    public void DotProductDistance_ShouldStayExactForLargeFiniteComponents()
    {
        var metric = new DotProductDistance();

        metric.Distance([2e19F, 2e19F], [2e19F, -2e19F]).Should().Be(0);
    }

    [Fact]
    public void FlatIndex_ShouldRankLargeFiniteVectorsExactlyUnderEveryMetric()
    {
        var cosine = new FlatVectorIndex<string>(2, new CosineDistance());
        cosine.Upsert("opposite-huge", [-2e19F, -2e19F]);
        cosine.Upsert("same-direction", [1, 1]);
        cosine.Search([2e19F, 2e19F], 1).Single().Id.Should().Be("same-direction");

        var l2 = new FlatVectorIndex<string>(1, new L2Distance());
        l2.Upsert("far", [2e20F]);
        l2.Upsert("near", [1e20F]);
        l2.Search([0], 1).Single().Id.Should().Be("near");

        var dot = new FlatVectorIndex<string>(2, new DotProductDistance());
        dot.Upsert("cancelling", [2e19F, -2e19F]);
        dot.Upsert("best", [1, 1]);
        dot.Search([2e19F, 2e19F], 1).Single().Id.Should().Be("best");
    }

    [Fact]
    public void FlatIndex_ShouldRankDotProductsBeyondFloatRangeExactly()
    {
        // True distances are -4e38 and -6e38: both exceed float range, but they are distinct and
        // the larger dot product must rank first rather than tie with the other at -Infinity.
        var dot = new FlatVectorIndex<string>(2, new DotProductDistance());
        dot.Upsert("weaker", [2e19F, 0]);
        dot.Upsert("best", [3e19F, 0]);

        dot.Search([2e19F, 0], 2).Select(static result => result.Id).Should().Equal("best", "weaker");
    }

    [Fact]
    public void FlatIndex_ShouldRankEuclideanDistancesBeyondFloatRangeExactly()
    {
        // True distances are 6e38 and 5e38: both exceed float range, but the nearer vector must
        // still rank first rather than tie with the farther one at +Infinity.
        var l2 = new FlatVectorIndex<string>(1, new L2Distance());
        l2.Upsert("far", [-3e38F]);
        l2.Upsert("near", [-2e38F]);

        l2.Search([3e38F], 2).Select(static result => result.Id).Should().Equal("near", "far");
    }

    [Fact]
    public void ApproximateIndexes_ShouldRankDistancesBeyondFloatRangeExactly()
    {
        // IVF and HNSW order candidates with the same metric; at full probe / full beam they must
        // agree with the exact ordering even when every distance exceeds float range.
        var ivfL2 = new IvfVectorIndex<string>(1, new L2Distance(), partitionCount: 1, probeCount: 1);
        var hnswL2 = new HnswVectorIndex<string>(1, new L2Distance());
        var ivfDot = new IvfVectorIndex<string>(2, new DotProductDistance(), partitionCount: 1, probeCount: 1);
        var hnswDot = new HnswVectorIndex<string>(2, new DotProductDistance());
        foreach (IVectorIndex<string> index in new IVectorIndex<string>[] { ivfL2, hnswL2 })
        {
            index.Upsert("far", [-3e38F]);
            index.Upsert("near", [-2e38F]);
        }

        foreach (IVectorIndex<string> index in new IVectorIndex<string>[] { ivfDot, hnswDot })
        {
            index.Upsert("weaker", [2e19F, 0]);
            index.Upsert("best", [3e19F, 0]);
        }

        ivfL2.Search([3e38F], 2).Select(static result => result.Id).Should().Equal("near", "far");
        hnswL2.Search([3e38F], 2, 10).Select(static result => result.Id).Should().Equal("near", "far");
        ivfDot.Search([2e19F, 0], 2).Select(static result => result.Id).Should().Equal("best", "weaker");
        hnswDot.Search([2e19F, 0], 2, 10).Select(static result => result.Id).Should().Equal("best", "weaker");
    }

    [Fact]
    public void Ivf_Search_ShouldNeverRouteAProbeToAnEmptyPartition()
    {
        // Under the dot-product metric k-means collapses onto the large-norm centroids, so some
        // centroids end training with no members; the query below is nearest to one of those.
        var index = new IvfVectorIndex<int>(2, new DotProductDistance(), partitionCount: 3, probeCount: 1, trainingIterations: 4);
        float[][] vectors = [[-3.9F, -0.3F], [2.7F, 1.6F], [-0.7F, -1.5F], [4.4F, -4F], [1.4F, -4.7F]];
        for (var id = 0; id < vectors.Length; id++)
        {
            index.Upsert(id, vectors[id]);
        }

        float[] query = [-0.342F, -0.940F];

        index.Search(query, 1).Should().ContainSingle("a probe must reach a partition that holds vectors");
        index.Search(query, 5, 1).Should().NotBeEmpty();
    }
}
