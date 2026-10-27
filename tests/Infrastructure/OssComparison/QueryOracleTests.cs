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

// Selected behavioral adaptations: Faiss (MIT), Apache Flink and Lucene (Apache-2.0).
// Exact upstream revisions, case mapping and licenses: QueryOracleSources.md.

using Boutquin.Storage.Domain.Interfaces.Search;
using Boutquin.Storage.Domain.ValueObjects.Analytics;
using Boutquin.Storage.Domain.ValueObjects.Streaming;
using Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar;
using Boutquin.Storage.Infrastructure.Analytics;
using Boutquin.Storage.Infrastructure.Batch;
using Boutquin.Storage.Infrastructure.Search.Text;
using Boutquin.Storage.Infrastructure.Search.Vector;
using Boutquin.Storage.Infrastructure.Streaming;
using Boutquin.Storage.Infrastructure.Streaming.Watermarks;
using Boutquin.Storage.Infrastructure.Streaming.Windows;
using Boutquin.Storage.Infrastructure.Tests.TimeSeries.Columnar;

namespace Boutquin.Storage.Infrastructure.Tests.OssComparison;

public sealed class QueryOracleTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 1)]
    [InlineData(0, 2)]
    [InlineData(1, 0)]
    [InlineData(1, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 0)]
    [InlineData(2, 1)]
    [InlineData(2, 2)]
    public void VectorIndexes_MatchScalarExhaustiveOracleAfterReplacementAndDeletion(int kind, int metricKind)
    {
        IVectorDistanceMetric metric = metricKind switch
        {
            0 => new L2Distance(),
            1 => new DotProductDistance(),
            _ => new CosineDistance()
        };
        IVectorIndex<int> index = kind switch
        {
            0 => new FlatVectorIndex<int>(4, metric),
            1 => new IvfVectorIndex<int>(4, metric, partitionCount: 5, probeCount: 5),
            _ => new HnswVectorIndex<int>(4, metric, maxConnections: 16, efConstruction: 128, efSearch: 128)
        };
        var random = new Random(491);
        var vectors = Enumerable.Range(1, 70).ToDictionary(id => id,
            _ => Enumerable.Range(0, 4).Select(_ => (float)random.Next(-100, 100)).ToArray());
        foreach (var item in vectors) { index.Upsert(item.Key, item.Value); }
        foreach (var id in new[] { 2, 17, 33 })
        {
            index.Remove(id).Should().BeTrue();
            vectors.Remove(id);
        }
        vectors[7] = [31, -12, 55, -78];
        index.Upsert(7, vectors[7]);
        for (var queryIndex = 0; queryIndex < 15; queryIndex++)
        {
            var query = Enumerable.Range(0, 4).Select(_ => (float)random.Next(-100, 100)).ToArray();
            var expected = vectors.Select(pair => (pair.Key, Distance: ScalarDistance(query, pair.Value, metricKind)))
                .OrderBy(item => item.Distance).ThenBy(item => item.Key).Take(8).ToArray();
            var actual = index.Search(query, 8);
            actual.Select(item => item.Id).Should().Equal(expected.Select(item => item.Key));
            for (var rank = 0; rank < actual.Count; rank++)
            {
                actual[rank].Distance.Should().BeApproximately(expected[rank].Distance,
                    Math.Max(0.0001F, Math.Abs(expected[rank].Distance) * 0.00001F));
            }
        }
    }

    private static float ScalarDistance(float[] left, float[] right, int metric)
    {
        double dot = 0, leftNorm = 0, rightNorm = 0, squared = 0;
        for (var i = 0; i < left.Length; i++)
        {
            dot += (double)left[i] * right[i];
            leftNorm += (double)left[i] * left[i];
            rightNorm += (double)right[i] * right[i];
            squared += Math.Pow((double)left[i] - right[i], 2);
        }
        return (float)(metric switch { 0 => Math.Sqrt(squared), 1 => -dot, _ => 1 - dot / Math.Sqrt(leftNorm * rightNorm) });
    }

    [Fact]
    public void InvertedIndex_ExactAndOrderedGapPhrasesMatchTokenScanAfterUpdates()
    {
        var index = new InvertedIndex<int>(new TextAnalyzer(new WhitespaceTokenizer(), new LowercaseNormalizer()));
        var documents = new Dictionary<int, string[]>();
        var vocabulary = new[] { "alpha", "beta", "gamma", "delta" };
        var random = new Random(143);
        for (var step = 0; step < 120; step++)
        {
            var id = random.Next(1, 21);
            if (step % 5 == 0)
            {
                index.Remove(id).Should().Be(documents.Remove(id));
            }
            else
            {
                var words = Enumerable.Range(0, random.Next(1, 12)).Select(_ => vocabulary[random.Next(4)]).ToArray();
                documents[id] = words;
                index.Add(id, string.Join(' ', words).ToUpperInvariant().AsMemory());
            }
            foreach (var first in vocabulary)
            {
                index.Lookup(first).Order().Should().Equal(documents.Where(pair => pair.Value.Contains(first)).Select(pair => pair.Key).Order());
                foreach (var second in vocabulary)
                {
                    index.SearchAll([first, second]).Order().Should().Equal(documents
                        .Where(pair => pair.Value.Contains(first) && pair.Value.Contains(second)).Select(pair => pair.Key).Order());
                    for (var gap = 0; gap <= 2; gap++)
                    {
                        var expected = documents.Where(pair => Enumerable.Range(0, pair.Value.Length)
                            .Any(i => pair.Value[i] == first && Enumerable.Range(i + 1, Math.Min(gap + 1, pair.Value.Length - i - 1))
                                .Any(j => pair.Value[j] == second))).Select(pair => pair.Key).Order();
                        index.SearchPhrase($"{first} {second}".AsMemory(), gap).Order().Should().Equal(expected);
                    }
                }
            }
        }
        // Storage uses smoothed TF-IDF, not Lucene's BM25 scoring.
        new TfIdfScorer().Score(3, 2, 10).Should().BeApproximately(3 * (Math.Log(11D / 3) + 1), 1e-12);
        new NGramTokenizer(2).Tokenize("abcd".AsMemory()).Should().Equal("ab", "bc", "cd");
        new TextAnalyzer(new WhitespaceTokenizer(), new LowercaseNormalizer(), new StopWordNormalizer(["the"]))
            .Analyze("THE cat".AsMemory()).Should().Equal("cat");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4999)]
    [InlineData(5000)]
    public void FlinkBoundaryCases_UseHalfOpenTumblingAndSlidingWindows(int milliseconds)
    {
        var epoch = DateTimeOffset.UnixEpoch;
        var actual = new TumblingWindow(TimeSpan.FromSeconds(5)).AssignWindows(epoch.AddMilliseconds(milliseconds));
        var start = milliseconds < 5000 ? 0 : 5000;
        actual.Should().Equal(new TimeWindow(epoch.AddMilliseconds(start), epoch.AddMilliseconds(start + 5000)));
        var expected = Enumerable.Range(-4, 10).Select(i => i * 1000)
            .Where(s => s <= milliseconds && milliseconds < s + 5000)
            .Select(s => new TimeWindow(epoch.AddMilliseconds(s), epoch.AddMilliseconds(s + 5000)));
        new SlidingWindow(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1))
            .AssignWindows(epoch.AddMilliseconds(milliseconds)).Should().Equal(expected);
    }

    [Fact]
    public async Task SlidingAggregation_ShuffledLateEventsMatchOfflineMembershipSum()
    {
        var random = new Random(881);
        var events = Enumerable.Range(0, 80).Select(i => (Second: random.Next(0, 90), Value: i + 1)).ToArray();
        var processor = new WindowedAggregator<int, int, int>(
            new SlidingWindow(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5)),
            new BoundedOutOfOrdernessWatermarkPolicy(TimeSpan.Zero), LateEventPolicy.Include,
            value => value % 3, () => 0, (sum, value) => sum + value);
        var result = new Dictionary<(TimeWindow Window, int Key), int>();
        await foreach (var update in processor.ProcessAsync(Stream(events)).ConfigureAwait(true))
        {
            if (update.Kind == WindowEmissionKind.Retraction) { result.Remove((update.Window, update.Key)); }
            else { result[(update.Window, update.Key)] = update.Value; }
        }
        var expected = new Dictionary<(TimeWindow, int), int>();
        for (var start = -5; start <= 90; start += 5)
        {
            for (var key = 0; key < 3; key++)
            {
                var members = events.Where(e => e.Second >= start && e.Second < start + 10 && e.Value % 3 == key).ToArray();
                if (members.Length > 0)
                {
                    expected[(new TimeWindow(DateTimeOffset.UnixEpoch.AddSeconds(start), DateTimeOffset.UnixEpoch.AddSeconds(start + 10)), key)]
                        = members.Sum(e => e.Value);
                }
            }
        }
        result.Should().BeEquivalentTo(expected);
    }

    private static async IAsyncEnumerable<StreamRecord<int>> Stream((int Second, int Value)[] events)
    {
        for (var i = 0; i < events.Length; i++)
        {
            yield return new StreamRecord<int>(i, DateTimeOffset.UnixEpoch.AddSeconds(events[i].Second), events[i].Value);
        }
        await Task.CompletedTask.ConfigureAwait(true);
    }

    [Fact]
    public void DataFrameAndCube_MatchNestedLoopRelationalOracle()
    {
        var rows = Enumerable.Range(1, 75).Select(i => (Region: i % 3, Product: i % 5, Value: i)).ToArray();
        var right = Enumerable.Range(1, 17).Select(i => (Region: i % 3, Value: i * 7)).ToArray();
        var expectedJoin = new List<(int, int)>();
        foreach (var left in rows)
        {
            foreach (var r in right)
            {
                if (left.Region == r.Region) { expectedJoin.Add((left.Value, r.Value)); }
            }
        }
        var frame = new MiniDataFrame<(int Region, int Product, int Value)>(rows);
        frame.Join(new MiniDataFrame<(int Region, int Value)>(right), r => r.Region, r => r.Region,
            (l, r) => (l.Value, r.Value)).Rows.Should().Equal(expectedJoin);
        frame.Where(r => r.Value % 2 == 0).Select(r => r.Value).Rows.Should().Equal(rows.Where(r => r.Value % 2 == 0).Select(r => r.Value));
        frame.GroupBy(r => r.Region, (key, group) => (key, group.Sum(r => r.Value))).Rows.OrderBy(r => r.key)
            .Should().Equal(Enumerable.Range(0, 3).Select(key => (key, rows.Where(r => r.Region == key).Sum(r => r.Value))));
        var cube = new DataCubeBuilder<(int Region, int Product, int Value), int>(
            [new CubeDimension<(int Region, int Product, int Value)>("region", r => r.Region),
             new CubeDimension<(int Region, int Product, int Value)>("product", r => r.Product)],
            r => r.Value, 0, (sum, value) => sum + value).Build(rows);
        cube.Should().HaveCount(1 + 3 + 5 + 15);
        foreach (var cell in cube)
        {
            var selected = rows.Where(r => (!cell.Dimensions.TryGetValue("region", out var region) || Equals(region, r.Region))
                && (!cell.Dimensions.TryGetValue("product", out var product) || Equals(product, r.Product))).ToArray();
            cell.RowCount.Should().Be(selected.Length);
            cell.Value.Should().Be(selected.Sum(r => r.Value));
        }
    }

    [Fact]
    public void VectorizedOperators_MatchScalarRows()
    {
        var dates = Enumerable.Range(0, 90).Select(i => new DateOnly(2026, 1, 1).AddDays(i)).ToArray();
        var prices = Enumerable.Range(0, 90).Select(i => (i * 37 % 47) / 10m).ToArray();
        var batch = new ColumnBatch<ColumnarBar>(90,
            new Dictionary<string, Array> { ["Date"] = dates, ["Close"] = prices }, dates[0], dates[^1]);
        var filtered = new FilterOperator<ColumnarBar, decimal>("Close", value => value > 2.1m).Execute(batch);
        var projected = new ProjectionOperator<ColumnarBar>(["Close"]).Execute(filtered);
        var expected = prices.Where(value => value > 2.1m).ToArray();
        ((decimal[])projected.Columns["Close"]).Should().Equal(expected);
        new AggregateOperator<ColumnarBar, decimal, decimal>("Close", 0m, (sum, value) => sum + value)
            .Execute(projected).Should().Be(expected.Sum());
    }
}
