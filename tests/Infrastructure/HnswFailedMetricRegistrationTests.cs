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
/// Pins that an exception thrown by the caller-supplied metric part-way through an HNSW mutation leaves
/// the graph exactly as it was: no vector is registered without being linked, and a rebuild never
/// leaves survivors registered but unlinked. Reachability is probed by querying each stored vector with
/// itself at a beam as wide as the index, which only a linked node can answer.
/// </summary>
public sealed class HnswFailedMetricRegistrationTests
{
    private const int Count = 30;

    [Fact]
    public void Upsert_RetriedAfterMetricThrow_ShouldLinkTheVectorSoSearchFindsIt()
    {
        var (index, metric) = Build();
        metric.ThrowOnCallNumber(1);

        var first = () => index.Upsert(999, [5F]);
        first.Should().Throw<InvalidOperationException>();
        metric.Disarm();
        index.Search([5F], Count + 1, 100_000).Select(static hit => hit.Id).Should().NotContain(999, "the failed upsert must leave no trace");

        index.Upsert(999, [5F]);

        index.Search([5F], 1, 100_000).Single().Id.Should().Be(999);
        AssertAllReachable(index, Enumerable.Range(0, Count).Select(static i => (i, i * 10F)).Append((999, 5F)));
    }

    [Fact]
    public void Upsert_WithMetricThrowAtAnyCall_ShouldLeaveGraphIntactAndRetryLinked()
    {
        for (var call = 1; call <= 400; call++)
        {
            var (index, metric) = Build();
            metric.ThrowOnCallNumber(call);
            var threw = false;
            try
            {
                index.Upsert(999, [5F]);
            }
            catch (InvalidOperationException)
            {
                threw = true;
            }

            metric.Disarm();
            if (!threw)
            {
                break;
            }

            AssertAllReachable(index, Enumerable.Range(0, Count).Select(static i => (i, i * 10F)));
            index.Upsert(999, [5F]);
            AssertAllReachable(index, Enumerable.Range(0, Count).Select(static i => (i, i * 10F)).Append((999, 5F)));
        }
    }

    [Fact]
    public void Remove_WithMetricThrowDuringRebuild_ShouldLeaveEverySurvivorReachable()
    {
        var (index, metric) = Build();
        for (var id = 0; id < 15; id++)
        {
            index.Remove(id).Should().BeTrue();
        }

        metric.ThrowOnCallNumber(1);
        var remove = () => index.Remove(15);
        remove.Should().Throw<InvalidOperationException>();
        metric.Disarm();

        AssertAllReachable(index, Enumerable.Range(15, Count - 15).Select(static i => (i, i * 10F)));
        index.Remove(15).Should().BeTrue("the failed removal must not have been half-applied");
        AssertAllReachable(index, Enumerable.Range(16, Count - 16).Select(static i => (i, i * 10F)));
    }

    [Fact]
    public void Remove_WithMetricThrowAtAnyRebuildCall_ShouldRollBackCompletely()
    {
        for (var call = 1; call <= 2_000; call++)
        {
            var (index, metric) = Build();
            for (var id = 0; id < 15; id++)
            {
                index.Remove(id);
            }

            metric.ThrowOnCallNumber(call);
            var threw = false;
            try
            {
                index.Remove(15);
            }
            catch (InvalidOperationException)
            {
                threw = true;
            }

            metric.Disarm();
            if (!threw)
            {
                break;
            }

            AssertAllReachable(index, Enumerable.Range(15, Count - 15).Select(static i => (i, i * 10F)));
            index.Remove(15).Should().BeTrue();
            AssertAllReachable(index, Enumerable.Range(16, Count - 16).Select(static i => (i, i * 10F)));
        }
    }

    private static (HnswVectorIndex<int> Index, ThrowingMetric Metric) Build()
    {
        var metric = new ThrowingMetric();
        var index = new HnswVectorIndex<int>(1, metric, maxConnections: 4, efConstruction: 8, efSearch: 8);
        for (var id = 0; id < Count; id++)
        {
            index.Upsert(id, [id * 10F]);
        }

        return (index, metric);
    }

    private static void AssertAllReachable(HnswVectorIndex<int> index, IEnumerable<(int Id, float Value)> expected)
    {
        var missed = new List<int>();
        foreach (var (id, value) in expected)
        {
            var hits = index.Search([value], 1, 100_000);
            if (hits.Count != 1 || hits[0].Id != id)
            {
                missed.Add(id);
            }
        }

        missed.Should().BeEmpty("every stored vector must answer its own exact query");
    }

    private sealed class ThrowingMetric : IVectorDistanceMetric
    {
        private int _calls;
        private int _throwAt = -1;

        internal void ThrowOnCallNumber(int callNumber)
        {
            _calls = 0;
            _throwAt = callNumber;
        }

        internal void Disarm() => _throwAt = -1;

        public float Distance(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
        {
            if (++_calls == _throwAt)
            {
                throw new InvalidOperationException("injected metric failure");
            }

            return MathF.Abs(left[0] - right[0]);
        }
    }
}
