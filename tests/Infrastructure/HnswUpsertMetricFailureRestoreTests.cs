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
/// Pins that replacing an existing id is atomic against a throwing caller-supplied metric: when the
/// metric fails at any call, the id still holds its previous vector and the graph still answers every
/// stored vector's exact query; a successful retry then replaces it.
/// </summary>
public sealed class HnswUpsertMetricFailureRestoreTests
{
    private const int Count = 30;

    [Fact]
    public void Upsert_ExistingIdWithMetricThrowAtAnyCall_ShouldKeepThePreviousVector()
    {
        var injected = 0;
        for (var call = 1; call <= 2_000; call++)
        {
            var (index, metric) = Build(removals: 0);
            metric.ThrowOnCallNumber(call);
            var threw = Attempt(() => index.Upsert(7, [555F]));
            metric.Disarm();
            if (!threw)
            {
                break;
            }

            injected++;
            AssertAllReachable(index, Expected(removed: 0));
            index.Search([555F], Count + 1, 100_000).Count.Should().Be(Count);

            index.Upsert(7, [555F]);
            index.Search([555F], 1, 100_000).Single().Id.Should().Be(7);
            index.Search([70F], 1, 100_000).Single().Id.Should().NotBe(7, "the retry replaced the old vector");
        }

        injected.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Upsert_ExistingIdWhoseReplaceTriggersRebuildWithMetricThrow_ShouldRollBackCompletely()
    {
        var injected = 0;
        for (var call = 1; call <= 4_000; call++)
        {
            var (index, metric) = Build(removals: 14);
            metric.ThrowOnCallNumber(call);
            var threw = Attempt(() => index.Upsert(15, [555F]));
            metric.Disarm();
            if (!threw)
            {
                break;
            }

            injected++;
            AssertAllReachable(index, Expected(removed: 14));
            index.Remove(15).Should().BeTrue("the failed replace must leave the old entry stored");
            AssertAllReachable(index, Expected(removed: 14).Where(static pair => pair.Id != 15));
        }

        injected.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Upsert_ExistingIdSharingAVectorWithMetricThrow_ShouldKeepEveryMember()
    {
        var injected = 0;
        for (var call = 1; call <= 2_000; call++)
        {
            var (index, metric) = Build(removals: 0);
            index.Upsert(100, [70F]);
            metric.ThrowOnCallNumber(call);
            var threw = Attempt(() => index.Upsert(7, [555F]));
            metric.Disarm();
            if (!threw)
            {
                break;
            }

            injected++;
            index.Search([70F], 2, 100_000).Select(static hit => hit.Id).Should().BeEquivalentTo(new[] { 7, 100 });
            AssertAllReachable(index, Expected(removed: 0).Where(static pair => pair.Id != 7));
        }

        injected.Should().BeGreaterThan(0);
    }

    private static bool Attempt(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static IEnumerable<(int Id, float Value)> Expected(int removed) =>
        Enumerable.Range(removed, Count - removed).Select(static i => (i, i * 10F));

    private static (HnswVectorIndex<int> Index, ThrowingMetric Metric) Build(int removals)
    {
        var metric = new ThrowingMetric();
        var index = new HnswVectorIndex<int>(1, metric, maxConnections: 4, efConstruction: 8, efSearch: 8);
        for (var id = 0; id < Count; id++)
        {
            index.Upsert(id, [id * 10F]);
        }

        for (var id = 0; id < removals; id++)
        {
            index.Remove(id).Should().BeTrue();
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
