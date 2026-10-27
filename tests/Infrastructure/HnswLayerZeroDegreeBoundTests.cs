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
using System.Collections;
using System.Reflection;
using Boutquin.Storage.Domain.Interfaces.Search;
using Boutquin.Storage.Infrastructure.Search.Vector;

namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// Pins the HNSW connection bound: Malkov and Yashunin shrink every neighbour list that exceeds
/// <c>Mmax</c> (<c>Mmax0 = 2·M</c> on layer 0). The layer-0 spanning tree that keeps every vector
/// reachable may add at most one edge beyond that cap (the edge to the node's own parent), so no
/// input — however many vectors tie at the same distance — grows a hub whose list scales with the
/// index size. The degree is read from the graph itself because no public surface exposes it.
/// </summary>
public sealed class HnswLayerZeroDegreeBoundTests
{
    private const int MaxConnections = 8;

    [Fact]
    public void Hnsw_LayerZeroDegree_ShouldStayWithinMmax0PlusOneTreeEdgeAmongCosineCollinearVectors()
    {
        // Positive multiples of one direction are distinct vectors at cosine distance zero from each
        // other: every insert ties with every earlier one.
        var index = new HnswVectorIndex<int>(4, new CosineDistance(), maxConnections: MaxConnections, efConstruction: 64);
        for (var id = 0; id < 400; id++)
        {
            var scale = 1F + id;
            index.Upsert(id, [scale, 2F * scale, 3F * scale, 4F * scale]);
        }

        AssertDegreeBound(index);
    }

    [Fact]
    public void Hnsw_LayerZeroDegree_ShouldStayWithinMmax0PlusOneTreeEdgeAmongEquidistantVectors()
    {
        // The origin followed by the unit axis vectors: every axis vector is at L2 distance one from
        // the origin and at the same distance from every other axis vector. Every vector must still
        // be returned by its own exact query.
        const int dimension = 160;
        var index = new HnswVectorIndex<int>(dimension, new L2Distance(), maxConnections: MaxConnections, efConstruction: 64);
        var vectors = new List<float[]> { new float[dimension] };
        for (var axis = 0; axis < dimension; axis++)
        {
            var vector = new float[dimension];
            vector[axis] = 1F;
            vectors.Add(vector);
        }

        for (var id = 0; id < vectors.Count; id++)
        {
            index.Upsert(id, vectors[id]);
        }

        AssertDegreeBound(index);
        for (var id = 0; id < vectors.Count; id++)
        {
            index.Search(vectors[id], 1, vectors.Count).Should().ContainSingle()
                .Which.Id.Should().Be(id, "every stored vector must stay reachable under the degree bound");
        }
    }

    [Fact]
    public void Hnsw_LayerZeroDegree_ShouldStayWithinMmax0PlusOneTreeEdgeAfterRebuild()
    {
        // Removing more than half of the vectors rebuilds the graph from the survivors through the
        // same insertion path, which must honour the same bound.
        var index = new HnswVectorIndex<int>(4, new CosineDistance(), maxConnections: MaxConnections, efConstruction: 64);
        for (var id = 0; id < 600; id++)
        {
            var scale = 1F + id;
            index.Upsert(id, [scale, 2F * scale, 3F * scale, 4F * scale]);
        }

        for (var id = 0; id < 600; id += 3)
        {
            index.Remove(id).Should().BeTrue();
        }

        for (var id = 1; id < 600; id += 3)
        {
            index.Remove(id).Should().BeTrue();
        }

        AssertDegreeBound(index);
    }

    [Fact]
    public void Hnsw_Insert_ShouldDoBoundedWorkPerVectorAmongEquidistantVectors()
    {
        // Every pair of distinct vectors is at the same distance, so every insert ties with every
        // stored vector. Malkov and Yashunin's SEARCH-LAYER admits a candidate only when it is closer
        // than the furthest element of a full result list; a beam that also admits every tie walks
        // the whole graph, and the work per insert grows with the index size.
        var metric = new CountingEquidistantMetric();
        var index = new HnswVectorIndex<int>(1, metric, maxConnections: MaxConnections, efConstruction: 64);
        var evaluations = new List<long> { 0 };
        for (var id = 0; id < 1600; id++)
        {
            index.Upsert(id, [id]);
            if ((id + 1) % 400 == 0)
            {
                evaluations.Add(metric.Evaluations);
            }
        }

        var early = evaluations[2] - evaluations[1];
        var late = evaluations[4] - evaluations[3];
        late.Should().BeLessThan(
            early * 3 / 2,
            "inserts 1,200-1,600 must cost about what inserts 400-800 cost (early {0}, late {1}), not grow with the index",
            early,
            late);
        AssertDegreeBound(index);
    }

    private static void AssertDegreeBound(object index)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var nodes = (IDictionary)index.GetType().GetField("_nodesByVector", flags)!.GetValue(index)!;
        nodes.Count.Should().BeGreaterThan(0);
        var worstLayerZero = 0;
        var worstUpper = 0;
        foreach (var node in nodes.Values)
        {
            var neighbors = (IList[])node!.GetType().GetProperty("Neighbors", flags)!.GetValue(node)!;
            worstLayerZero = Math.Max(worstLayerZero, neighbors[0].Count);
            for (var layer = 1; layer < neighbors.Length; layer++)
            {
                worstUpper = Math.Max(worstUpper, neighbors[layer].Count);
            }
        }

        worstLayerZero.Should().BeLessThanOrEqualTo(
            (2 * MaxConnections) + 1,
            "a layer-0 list holds at most Mmax0 = 2·M connections plus the one edge to its own parent");
        worstUpper.Should().BeLessThanOrEqualTo(MaxConnections, "an upper-layer list holds at most Mmax = M connections");
    }

    /// <summary>Puts every pair of distinct vectors at distance one and counts each evaluation.</summary>
    private sealed class CountingEquidistantMetric : IVectorDistanceMetric
    {
        internal long Evaluations { get; private set; }

        public float Distance(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
        {
            Evaluations++;
            return left.SequenceEqual(right) ? 0F : 1F;
        }
    }
}
