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
/// Unit tests for <see cref="KWayMerge"/>.
/// </summary>
/// <remarks>
/// The helper is the substrate for Phase B1/B2/B5 — streaming compaction, streaming
/// <c>SortedStringTable&lt;TKey, TValue&gt;.Merge</c>, and streaming
/// <see cref="LsmStorageEngine{TKey, TValue}.GetRangeAsync"/>. Every test here pins an
/// invariant the callers rely on:
/// <list type="bullet">
/// <item>the emitted stream is sorted by key ascending,</item>
/// <item>duplicate keys collapse with last-writer-wins by source index (higher index wins),</item>
/// <item>memory is bounded by source count, not total entry count (iterator-based),</item>
/// <item>cancellation propagates without leaking enumerators.</item>
/// </list>
/// </remarks>
public sealed class KWayMergeTests
{
    // Plain int-keyed fixture. KWayMerge is generic; these tests cover the
    // IComparable<int> default comparer and the explicit-comparer paths.

    [Fact]
    public async Task MergeAsync_EmptySources_YieldsEmpty()
    {
        var result = await CollectAsync(KWayMerge.MergeAsync(
            Array.Empty<IAsyncEnumerable<KeyValuePair<int, string>>>(),
            Comparer<int>.Default));

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task MergeAsync_SingleSource_YieldsSourceInOrder()
    {
        var source = AsAsync([Kv(1, "a"), Kv(3, "b"), Kv(7, "c")]);

        var result = await CollectAsync(KWayMerge.MergeAsync(
            [source], Comparer<int>.Default));

        result.Should().Equal(Kv(1, "a"), Kv(3, "b"), Kv(7, "c"));
    }

    [Fact]
    public async Task MergeAsync_TwoDisjointSources_InterleavesInSortedOrder()
    {
        var a = AsAsync([Kv(1, "a"), Kv(5, "a"), Kv(9, "a")]);
        var b = AsAsync([Kv(2, "b"), Kv(6, "b"), Kv(10, "b")]);

        var result = await CollectAsync(KWayMerge.MergeAsync(
            [a, b], Comparer<int>.Default));

        result.Should().Equal(
            Kv(1, "a"), Kv(2, "b"), Kv(5, "a"), Kv(6, "b"), Kv(9, "a"), Kv(10, "b"));
    }

    [Fact]
    public async Task MergeAsync_ThreeInterleavedSources_YieldsSortedByKey()
    {
        var a = AsAsync([Kv(1, "a"), Kv(4, "a")]);
        var b = AsAsync([Kv(2, "b"), Kv(5, "b")]);
        var c = AsAsync([Kv(3, "c"), Kv(6, "c")]);

        var result = await CollectAsync(KWayMerge.MergeAsync(
            [a, b, c], Comparer<int>.Default));

        result.Select(kv => kv.Key).Should().BeInAscendingOrder();
        result.Should().HaveCount(6);
    }

    [Fact]
    public async Task MergeAsync_DuplicateKey_HigherSourceIndexWins()
    {
        // Oldest segment first (index 0), newest last (index 2). Last writer wins.
        var oldest = AsAsync([Kv(5, "old")]);
        var middle = AsAsync([Kv(5, "mid")]);
        var newest = AsAsync([Kv(5, "new")]);

        var result = await CollectAsync(KWayMerge.MergeAsync(
            [oldest, middle, newest], Comparer<int>.Default));

        result.Should().ContainSingle()
            .Which.Value.Should().Be("new",
                "KWayMerge must pick the highest source index when keys collide so newer "
                + "segments override older ones (DDIA 2e p. 120 last-writer-wins).");
    }

    [Fact]
    public async Task MergeAsync_DuplicateKeyAcrossTwoSources_YieldsOnlyLatestValue()
    {
        var older = AsAsync([Kv(1, "old"), Kv(2, "old"), Kv(3, "old")]);
        var newer = AsAsync([Kv(2, "new"), Kv(3, "new"), Kv(4, "new")]);

        var result = await CollectAsync(KWayMerge.MergeAsync(
            [older, newer], Comparer<int>.Default));

        result.Should().Equal(
            Kv(1, "old"), Kv(2, "new"), Kv(3, "new"), Kv(4, "new"));
    }

    [Fact]
    public async Task MergeAsync_UsesProvidedComparer()
    {
        // Reverse comparer so the emitted sequence is descending.
        var descending = Comparer<int>.Create((x, y) => y.CompareTo(x));
        var a = AsAsync([Kv(9, "a"), Kv(5, "a"), Kv(1, "a")]);
        var b = AsAsync([Kv(10, "b"), Kv(6, "b"), Kv(2, "b")]);

        var result = await CollectAsync(KWayMerge.MergeAsync([a, b], descending));

        result.Select(kv => kv.Key).Should().Equal(10, 9, 6, 5, 2, 1);
    }

    [Fact]
    public async Task MergeAsync_OneSourceEmpty_StillYieldsOthers()
    {
        var empty = AsAsync(Array.Empty<KeyValuePair<int, string>>());
        var filled = AsAsync([Kv(1, "a"), Kv(2, "b")]);

        var result = await CollectAsync(KWayMerge.MergeAsync(
            [empty, filled], Comparer<int>.Default));

        result.Should().Equal(Kv(1, "a"), Kv(2, "b"));
    }

    [Fact]
    public async Task MergeAsync_Cancellation_StopsEnumerationEarly()
    {
        var cts = new CancellationTokenSource();
        var source = GenerateThenSignal(cts);

        var emitted = new List<KeyValuePair<int, int>>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var kv in KWayMerge.MergeAsync(
                [source], Comparer<int>.Default, cts.Token).ConfigureAwait(true))
            {
                emitted.Add(kv);
                if (emitted.Count >= 3)
                {
                    cts.Cancel();
                }
            }
        }).ConfigureAwait(true);

        emitted.Count.Should().BeGreaterThanOrEqualTo(3,
            "the iterator must surface results until the caller cancels, then stop promptly.");
    }

    [Fact]
    public async Task MergeAsync_ManySources_PreservesSortedInvariant()
    {
        // Stress the min-heap with 16 sources of 100 entries each.
        const int sourceCount = 16;
        const int perSource = 100;

        var rng = new Random(42);
        var sources = new List<IAsyncEnumerable<KeyValuePair<int, int>>>(sourceCount);
        var allKeys = new List<int>(sourceCount * perSource);

        for (var s = 0; s < sourceCount; s++)
        {
            var sorted = new List<KeyValuePair<int, int>>(perSource);
            var lastKey = 0;
            for (var i = 0; i < perSource; i++)
            {
                lastKey += rng.Next(1, 5);
                sorted.Add(Kv(lastKey, s));
                allKeys.Add(lastKey);
            }
            sources.Add(AsAsync(sorted));
        }

        var result = await CollectAsync(KWayMerge.MergeAsync(sources, Comparer<int>.Default));

        result.Select(kv => kv.Key).Should().BeInAscendingOrder(
            "the min-heap invariant must hold for any number of sources.");
        // Duplicates collapse to one entry per distinct key (last-writer-wins).
        result.Select(kv => kv.Key).Should().OnlyHaveUniqueItems();
        result.Select(kv => kv.Key).Should().BeEquivalentTo(
            allKeys.Distinct().OrderBy(k => k),
            "the merged stream must contain every distinct key exactly once.");
    }

    // ---- helpers ------------------------------------------------------------------

    private static KeyValuePair<TKey, TValue> Kv<TKey, TValue>(TKey key, TValue value) =>
        new(key, value);

    private static async IAsyncEnumerable<T> AsAsync<T>(IEnumerable<T> items)
    {
        foreach (var item in items)
        {
            await Task.Yield();
            yield return item;
        }
    }

    private static async Task<List<T>> CollectAsync<T>(IAsyncEnumerable<T> source)
    {
        var result = new List<T>();
        await foreach (var item in source.ConfigureAwait(true))
        {
            result.Add(item);
        }
        return result;
    }

    private static async IAsyncEnumerable<KeyValuePair<int, int>> GenerateThenSignal(
        CancellationTokenSource _,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken ct = default)
    {
        for (var i = 0; i < 1_000; i++)
        {
            ct.ThrowIfCancellationRequested();
            yield return new KeyValuePair<int, int>(i, i);
            await Task.Yield();
        }
    }
}
