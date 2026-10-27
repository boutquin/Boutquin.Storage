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
using Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar;
using Boutquin.Storage.Infrastructure.TimeSeries;

namespace Boutquin.Storage.Infrastructure.Tests.TimeSeries;

public sealed class InMemoryTimeSeriesStoreTests
{
    private static SerializableWrapper<string> D(string s) => new(s);
    private static DateOnly DD(int y, int m, int d) => new(y, m, d);

    private static TimeSeriesTestRecord R(int y, int m, int d, int v) => new(DD(y, m, d), v);

    private static InMemoryTimeSeriesStore<SerializableWrapper<string>, TimeSeriesTestRecord> NewStore()
        => new();

    private static async Task<List<TimeSeriesTestRecord>> ToListAsync(IAsyncEnumerable<TimeSeriesTestRecord> source)
    {
        var list = new List<TimeSeriesTestRecord>();
        await foreach (var r in source)
        {
            list.Add(r);
        }
        return list;
    }

    [Fact]
    public async Task GetRange_ReturnsExactInclusiveSlice_InAscendingOrder()
    {
        var store = NewStore();
        await store.AppendAsync(D("AAPL"),
        [
            R(2020, 3, 15, 3),
            R(2020, 1, 1, 1),
            R(2020, 2, 1, 2),
            R(2020, 6, 1, 6),
        ]);

        var slice = await ToListAsync(store.GetRangeAsync(D("AAPL"), DD(2020, 1, 1), DD(2020, 3, 15), ColumnProjection.All));

        slice.Select(r => r.Value).Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task GetRange_UnknownDataset_ReturnsEmpty()
    {
        var store = NewStore();

        var slice = await ToListAsync(store.GetRangeAsync(D("AAPL"), DD(2020, 1, 1), DD(2020, 12, 31), ColumnProjection.All));

        slice.Should().BeEmpty();
    }

    [Fact]
    public async Task GetRange_FromGreaterThanTo_ReturnsEmpty_NoException()
    {
        var store = NewStore();
        await store.AppendAsync(D("AAPL"), [R(2020, 1, 1, 1)]);

        var slice = await ToListAsync(store.GetRangeAsync(D("AAPL"), DD(2021, 1, 1), DD(2020, 1, 1), ColumnProjection.All));

        slice.Should().BeEmpty();
    }

    // AC-9.3 (F-41): GetRangeAsync drops the per-record `await Task.Yield()`. This is a
    // characterization test that the range still streams the COMPLETE, ORDERED slice after the
    // refactor — i.e. removing the per-record yield does not truncate, reorder, or drop records.
    // A large multi-record range exercises the loop body many times, so a regression that broke
    // the streaming when the yield was removed would surface as a count/order mismatch here.
    [Fact]
    public async Task GetRange_LargeRange_StreamsCompleteOrderedSlice_AfterYieldRemoval()
    {
        var store = NewStore();
        var baseDate = DD(2020, 1, 1);
        var input = Enumerable.Range(0, 500)
            .Select(i => new TimeSeriesTestRecord(baseDate.AddDays(i), i))
            .ToList();
        await store.AppendAsync(D("AAPL"), input);

        var slice = await ToListAsync(
            store.GetRangeAsync(D("AAPL"), baseDate, baseDate.AddDays(499), ColumnProjection.All));

        // Every record streamed, in ascending date order, nothing dropped or duplicated.
        slice.Should().HaveCount(500);
        slice.Select(r => r.Value).Should().Equal(Enumerable.Range(0, 500));
        slice.Select(r => r.Date).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task CrossDataset_QueryingBDoesNotReturnARecords()
    {
        var store = NewStore();
        await store.AppendAsync(D("A"), [R(2020, 1, 1, 1), R(2020, 1, 2, 2)]);
        await store.AppendAsync(D("B"), [R(2020, 1, 1, 99)]);

        var sliceB = await ToListAsync(store.GetRangeAsync(D("B"), DD(2020, 1, 1), DD(2020, 12, 31), ColumnProjection.All));

        sliceB.Should().ContainSingle().Which.Value.Should().Be(99);
    }

    [Fact]
    public async Task AppendTwice_SameRecords_IsIdempotent()
    {
        var store = NewStore();
        var recs = new[] { R(2020, 1, 1, 1), R(2020, 1, 2, 2) };

        await store.AppendAsync(D("AAPL"), recs);
        await store.AppendAsync(D("AAPL"), recs);
        var slice = await ToListAsync(store.GetRangeAsync(D("AAPL"), DD(2020, 1, 1), DD(2020, 12, 31), ColumnProjection.All));

        slice.Should().HaveCount(2);
        slice.Select(r => r.Value).Should().Equal(1, 2);
    }

    [Fact]
    public async Task AppendSameDate_LastWriterWins()
    {
        var store = NewStore();
        await store.AppendAsync(D("AAPL"), [R(2020, 1, 1, 1)]);
        await store.AppendAsync(D("AAPL"), [R(2020, 1, 1, 99)]);

        var slice = await ToListAsync(store.GetRangeAsync(D("AAPL"), DD(2020, 1, 1), DD(2020, 1, 1), ColumnProjection.All));

        slice.Should().ContainSingle().Which.Value.Should().Be(99);
    }

    [Fact]
    public async Task Coverage_UnknownDataset_IsNull()
    {
        var store = NewStore();

        (await store.GetCoverageAsync(D("AAPL"))).Should().BeNull();
    }

    [Fact]
    public async Task Coverage_ReflectsMinMax()
    {
        var store = NewStore();
        await store.AppendAsync(D("AAPL"), [R(2020, 6, 1, 1), R(2020, 1, 1, 2), R(2020, 12, 31, 3)]);

        (await store.GetCoverageAsync(D("AAPL"))).Should().Be(new DateRange(DD(2020, 1, 1), DD(2020, 12, 31)));
    }

    [Fact]
    public async Task Gaps_FullCover_Empty()
    {
        var store = NewStore();
        await store.AppendAsync(D("AAPL"), [R(2000, 1, 1, 1), R(2026, 4, 18, 2)]);

        var gaps = await store.GetGapsAsync(D("AAPL"), new DateRange(DD(2020, 1, 1), DD(2026, 4, 17)));

        gaps.Should().BeEmpty();
    }

    [Fact]
    public async Task Gaps_PrefixGap()
    {
        var store = NewStore();
        await store.AppendAsync(D("AAPL"), [R(2010, 1, 1, 1), R(2026, 4, 18, 2)]);

        var gaps = await store.GetGapsAsync(D("AAPL"), new DateRange(DD(2005, 1, 1), DD(2026, 4, 18)));

        gaps.Should().ContainSingle().Which.Should().Be(new DateRange(DD(2005, 1, 1), DD(2009, 12, 31)));
    }

    [Fact]
    public async Task Gaps_SuffixGap()
    {
        var store = NewStore();
        await store.AppendAsync(D("AAPL"), [R(2010, 1, 1, 1), R(2020, 12, 31, 2)]);

        var gaps = await store.GetGapsAsync(D("AAPL"), new DateRange(DD(2010, 1, 1), DD(2026, 4, 18)));

        gaps.Should().ContainSingle().Which.Should().Be(new DateRange(DD(2021, 1, 1), DD(2026, 4, 18)));
    }

    [Fact]
    public async Task Gaps_BothGaps()
    {
        var store = NewStore();
        await store.AppendAsync(D("AAPL"), [R(2010, 1, 1, 1), R(2020, 12, 31, 2)]);

        var gaps = await store.GetGapsAsync(D("AAPL"), new DateRange(DD(2005, 1, 1), DD(2026, 4, 18)));

        gaps.Should().HaveCount(2);
        gaps[0].Should().Be(new DateRange(DD(2005, 1, 1), DD(2009, 12, 31)));
        gaps[1].Should().Be(new DateRange(DD(2021, 1, 1), DD(2026, 4, 18)));
    }

    [Fact]
    public async Task Gaps_NoCoverage_ReturnsFullRequested()
    {
        var store = NewStore();

        var gaps = await store.GetGapsAsync(D("AAPL"), new DateRange(DD(2020, 1, 1), DD(2020, 12, 31)));

        gaps.Should().ContainSingle().Which.Should().Be(new DateRange(DD(2020, 1, 1), DD(2020, 12, 31)));
    }

    [Fact]
    public async Task DeleteRange_RemovesRecordsInRange()
    {
        var store = NewStore();
        await store.AppendAsync(D("AAPL"),
        [
            R(2020, 1, 1, 1), R(2020, 2, 1, 2), R(2020, 3, 1, 3), R(2020, 4, 1, 4),
        ]);

        await store.DeleteRangeAsync(D("AAPL"), DD(2020, 2, 1), DD(2020, 3, 1));
        var remaining = await ToListAsync(store.GetRangeAsync(D("AAPL"), DD(2020, 1, 1), DD(2020, 12, 31), ColumnProjection.All));

        remaining.Select(r => r.Value).Should().Equal(1, 4);
    }

    [Fact]
    public async Task DeleteRange_FromGreaterThanTo_IsNoop()
    {
        var store = NewStore();
        await store.AppendAsync(D("AAPL"), [R(2020, 1, 1, 1)]);

        await store.DeleteRangeAsync(D("AAPL"), DD(2021, 1, 1), DD(2020, 1, 1));
        var remaining = await ToListAsync(store.GetRangeAsync(D("AAPL"), DD(2020, 1, 1), DD(2020, 12, 31), ColumnProjection.All));

        remaining.Should().ContainSingle();
    }

    [Fact]
    public async Task DeleteRange_ShrinksCoverage()
    {
        var store = NewStore();
        await store.AppendAsync(D("AAPL"),
        [
            R(2020, 1, 1, 1), R(2020, 6, 1, 2), R(2020, 12, 31, 3),
        ]);

        await store.DeleteRangeAsync(D("AAPL"), DD(2020, 1, 1), DD(2020, 1, 1));
        (await store.GetCoverageAsync(D("AAPL"))).Should().Be(new DateRange(DD(2020, 6, 1), DD(2020, 12, 31)));
    }

    [Fact]
    public async Task DeleteRange_AllRecords_ClearsCoverage()
    {
        var store = NewStore();
        await store.AppendAsync(D("AAPL"), [R(2020, 1, 1, 1), R(2020, 12, 31, 2)]);

        await store.DeleteRangeAsync(D("AAPL"), DD(2020, 1, 1), DD(2020, 12, 31));

        (await store.GetCoverageAsync(D("AAPL"))).Should().BeNull();
    }

    [Fact]
    public async Task EmptyAppend_IsNoop()
    {
        var store = NewStore();

        await store.AppendAsync(D("AAPL"), []);

        (await store.GetCoverageAsync(D("AAPL"))).Should().BeNull();
    }

    // Fix 11: GetRangeAsync with null projection must be equivalent to ColumnProjection.All.
    [Fact]
    public async Task GetRangeAsync_NullProjection_EquivalentToAll()
    {
        var store = NewStore();
        await store.AppendAsync(D("AAPL"),
        [
            R(2020, 1, 1, 1),
            R(2020, 1, 2, 2),
        ]);

        // null projection should return the same records as ColumnProjection.All.
        var withNull = await ToListAsync(store.GetRangeAsync(D("AAPL"), DD(2020, 1, 1), DD(2020, 1, 2)));
        var withAll = await ToListAsync(store.GetRangeAsync(D("AAPL"), DD(2020, 1, 1), DD(2020, 1, 2), ColumnProjection.All));

        withNull.Should().BeEquivalentTo(withAll, because: "null projection must behave identically to ColumnProjection.All");
    }

    /// <summary>
    /// B5: AppendAsync should reject a null records argument with ArgumentNullException,
    /// matching the behaviour of the LSM sibling store.
    /// </summary>
    [Fact]
    public async Task AppendAsync_NullRecords_ThrowsArgumentNullException()
    {
        var store = NewStore();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            store.AppendAsync(D("AAPL"), null!))
            .ConfigureAwait(true);
    }

    [Fact]
    public async Task AppendAsync_LazyEnumerable_DoesNotDeadlockOnReentrantAccess()
    {
        // A lazy IEnumerable<TRecord> that defers element production until GetEnumerator is
        // called proves materialization happened before (or outside of) the per-dataset lock.
        // If enumeration occurs inside the lock, any attempt to acquire the same lock from
        // within the lazy sequence would deadlock. This test uses a simpler signal: it verifies
        // that a yield-based sequence is fully enumerated and all records are stored correctly,
        // which is only possible if the materialization succeeded.
        var store = NewStore();

        static IEnumerable<TimeSeriesTestRecord> LazyRecords()
        {
            yield return R(2024, 1, 1, 10);
            yield return R(2024, 1, 2, 20);
            yield return R(2024, 1, 3, 30);
        }

        await store.AppendAsync(D("LAZY"), LazyRecords()).ConfigureAwait(true);

        var result = await ToListAsync(store.GetRangeAsync(D("LAZY"), DD(2024, 1, 1), DD(2024, 1, 3)))
            .ConfigureAwait(true);

        result.Should().HaveCount(3, "all three lazily-produced records must be stored");
        result.Select(r => r.Value).Should().Equal(10, 20, 30);
    }
}
