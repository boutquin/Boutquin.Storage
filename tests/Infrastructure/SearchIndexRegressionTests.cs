// Copyright (c) 2024-2026 Pierre G. Boutquin. All rights reserved.
//
//   Licensed under the Apache License, Version 2.0 (the "License").
//   You may not use this file except in compliance with the License.
//   You may obtain a copy of the License at
//
//       http://www.apache.org/licenses/LICENSE-2.0
//
//   Unless required by applicable law or agreed to in writing, software
//   distributed under the License is distributed on an "AS IS" BASIS,
//   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//
//   See the License for the specific language governing permissions and
//   limitations under the License.
//

using System.Reflection;
using Boutquin.Storage.Domain.ValueObjects.Analytics;
using Boutquin.Storage.Infrastructure.Analytics;
using Boutquin.Storage.Infrastructure.Search.Vector;

namespace Boutquin.Storage.Infrastructure.Tests.Analytics;

/// <summary>
/// Pins two regressions of the search and analytics indexes. A data cube counts rows in a 64-bit
/// counter end to end: <c>Build</c> streams an unbounded <see cref="IEnumerable{T}"/>, so a 32-bit
/// count silently wraps negative once a cell absorbs more than <see cref="int.MaxValue"/> rows;
/// streaming that many rows is minutes of work, so the counter width is pinned on both the published
/// cell and the builder's accumulator. An HNSW index returns every stored vector by its own exact
/// query at a beam as wide as the live set, however full the neighbour lists pruning leaves.
/// </summary>
public sealed class SearchIndexRegressionTests
{
    [Fact]
    public void DataCube_RowCount_ShouldBeSixtyFourBitOnTheCellAndTheAccumulator()
    {
        typeof(DataCubeCell<long>).GetProperty(nameof(DataCubeCell<long>.RowCount))!.PropertyType
            .Should().Be<long>("a cell can absorb more than int.MaxValue streamed rows");

        var accumulator = typeof(DataCubeBuilder<int, long>).GetNestedType("Accumulator", BindingFlags.NonPublic);
        accumulator.Should().NotBeNull();
        accumulator!.GetProperty("RowCount")!.PropertyType
            .Should().Be<long>("the running count must not wrap before it reaches the cell");
    }

    [Fact]
    public void DataCube_RowCount_ShouldCountEveryStreamedRowInEveryGroupingSet()
    {
        var builder = new DataCubeBuilder<int, long>(
            [new CubeDimension<int>("Parity", static row => row % 2)],
            static _ => 1L, 0L, static (sum, one) => sum + one);

        var cells = builder.Build(Enumerable.Range(0, 1_001));

        cells.Should().OnlyContain(cell => cell.RowCount == cell.Value);
        cells.Single(cell => cell.Dimensions.Count == 0).RowCount.Should().Be(1_001);
    }

    [Fact]
    public void Hnsw_ShouldReturnEveryVectorByItsOwnExactQueryWhenPruningDropsItsLastInEdge()
    {
        // Seven distinct vectors at maxConnections 2: both nodes [0,2] links to already hold four
        // nearer neighbours on layer 0, so pruning drops their edges back to it and nothing else
        // points at it on any layer.
        var index = new HnswVectorIndex<int>(2, new L2Distance(), maxConnections: 2, randomSeed: 273);
        float[][] vectors = [[0, 0], [0, -1], [0, 2], [1, 0], [-1, 0], [2, -1], [1, 1]];
        for (var id = 0; id < vectors.Length; id++)
        {
            index.Upsert(id, vectors[id]);
        }

        var missed = Enumerable.Range(0, vectors.Length)
            .Where(id => index.Search(vectors[id], 1, 100_000) is not [(var found, 0F)] || found != id)
            .ToArray();

        missed.Should().BeEmpty("every stored vector must be returned by its own exact query");
    }

    [Theory]
    [InlineData(2)]
    [InlineData(8)]
    public void Hnsw_ShouldKeepEveryVectorReachableAmongCosineScaledCopies(int maxConnections)
    {
        // The cosine counterpart of exact duplicates: positive multiples of one direction are
        // distinct values at distance zero from each other, so each is a node of its own, and twenty
        // per direction exceed every connection cap before the distinct vectors arrive.
        var lost = new List<string>();
        for (var seed = 0; seed < 10; seed++)
        {
            var random = new Random(seed);
            var index = new HnswVectorIndex<int>(2, new CosineDistance(), maxConnections: maxConnections, randomSeed: seed);
            float[][] directions = [[1, 0], [1, 1], [0, 1]];
            var members = directions.Select(static _ => new List<int>()).ToArray();
            var order = Enumerable.Range(0, directions.Length).SelectMany(static cluster => Enumerable.Repeat(cluster, 20))
                .OrderBy(_ => random.Next()).ToArray();
            var next = 0;
            foreach (var cluster in order)
            {
                var scale = members[cluster].Count + 1F;
                index.Upsert(next, [directions[cluster][0] * scale, directions[cluster][1] * scale]);
                members[cluster].Add(next++);
            }

            for (var k = 0; k < 10; k++)
            {
                float[] vector = [(float)random.NextDouble() + 0.01F, (float)random.NextDouble() + 0.02F];
                index.Upsert(next, vector);
                if (index.Search(vector, 1, 100_000) is not [(var id, _)] || id != next)
                {
                    lost.Add($"seed {seed} new id {next}");
                }

                next++;
            }

            for (var cluster = 0; cluster < directions.Length; cluster++)
            {
                var returned = index.Search(directions[cluster], members[cluster].Count, 100_000).Select(static result => result.Id).ToHashSet();
                lost.AddRange(members[cluster].Where(id => !returned.Contains(id)).Select(id => $"seed {seed} copy id {id}"));
            }
        }

        lost.Should().BeEmpty("every stored vector, a scaled copy or not, must be returned by its own exact query");
    }
}
