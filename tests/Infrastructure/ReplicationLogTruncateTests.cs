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
/// Phase B tests for <see cref="ReplicationLog{TKey, TValue}"/> — TruncateBelowAsync and
/// <see cref="FollowerTooFarBehindException"/>.
/// </summary>
public sealed class ReplicationLogTruncateTests
{
    private static async Task SeedAsync(ReplicationLog<string, int> log, params long[] sequences)
    {
        foreach (var s in sequences)
        {
            await log.AppendAsync($"k{s}", (int)s, s);
        }
    }

    [Fact]
    public async Task TruncateBelow_RemovesEntriesStrictlyBelowWatermark()
    {
        var log = new ReplicationLog<string, int>();
        await SeedAsync(log, 1, 2, 3, 4, 5);

        var removed = await log.TruncateBelowAsync(3);

        removed.Should().Be(2, "entries with seq 1 and 2 are below 3");
        // Use afterSeq = 2 (watermark-1) — the lowest value that does not throw
        // FollowerTooFarBehindException after TruncateBelow(3).
        var remaining = await log.GetEntriesAfterAsync(2);
        remaining.Select(e => e.SequenceNumber).Should().Equal(3, 4, 5);
    }

    [Fact]
    public async Task TruncateBelow_IsIdempotent()
    {
        var log = new ReplicationLog<string, int>();
        await SeedAsync(log, 1, 2, 3);

        await log.TruncateBelowAsync(2);
        var secondCall = await log.TruncateBelowAsync(2);

        secondCall.Should().Be(0);
    }

    [Fact]
    public async Task TruncateBelow_NonDecreasingWatermark()
    {
        var log = new ReplicationLog<string, int>();
        await SeedAsync(log, 1, 2, 3, 4, 5);

        await log.TruncateBelowAsync(3);
        // A lower watermark after a higher one should be a no-op (watermark is monotonic).
        var result = await log.TruncateBelowAsync(2);

        result.Should().Be(0);
    }

    [Fact]
    public async Task GetEntriesAfter_BelowTruncationWatermark_ThrowsFollowerTooFarBehind()
    {
        var log = new ReplicationLog<string, int>();
        await SeedAsync(log, 1, 2, 3, 4, 5);
        await log.TruncateBelowAsync(4); // remove seq 1,2,3

        // A follower whose hwm=1 asks for entries > 1 — the next needed entry is 2, which is gone.
        var act = async () => await log.GetEntriesAfterAsync(1);

        var ex = await act.Should().ThrowAsync<FollowerTooFarBehindException>();
        ex.Which.TruncationWatermark.Should().Be(4);
        ex.Which.RequestedAfterSequenceNumber.Should().Be(1);
    }

    [Fact]
    public async Task GetEntriesAfter_AtOrAboveWatermarkBoundary_Allowed()
    {
        var log = new ReplicationLog<string, int>();
        await SeedAsync(log, 1, 2, 3, 4, 5);
        await log.TruncateBelowAsync(4);

        // Follower whose hwm=3: next needed is seq 4, which is present. Must not throw.
        var after3 = await log.GetEntriesAfterAsync(3);
        after3.Select(e => e.SequenceNumber).Should().Equal(4, 5);

        // Follower whose hwm=4: next needed is 5. Must not throw.
        var after4 = await log.GetEntriesAfterAsync(4);
        after4.Select(e => e.SequenceNumber).Should().Equal(5);
    }

    /// <summary>
    /// A follower whose high-water mark is at (or beyond) the highest sequence number ever
    /// appended has missed nothing — there is no gap to reconstruct, so it must receive an
    /// empty list rather than <see cref="FollowerTooFarBehindException"/>, even when the
    /// truncation watermark was set far above the latest appended entry.
    /// </summary>
    [Fact]
    public async Task GetEntriesAfter_CaughtUpFollower_ReturnsEmpty_EvenWithHighWatermark()
    {
        var log = new ReplicationLog<string, int>();
        await SeedAsync(log, 1, 2, 3, 4, 5);
        await log.TruncateBelowAsync(100); // removes everything; watermark far above latest (5)

        // hwm=5 equals the latest ever appended: caught up, no gap, no rebootstrap demand.
        var entries = await log.GetEntriesAfterAsync(5);
        entries.Should().BeEmpty("a caught-up follower has no gap regardless of the watermark");

        // hwm=4 DID miss entry 5 (truncated): the gap is real, the exception stands.
        await FluentActions.Invoking(() => log.GetEntriesAfterAsync(4))
            .Should().ThrowAsync<FollowerTooFarBehindException>();
    }

    [Fact]
    public async Task TruncateBelow_DoesNotAffectMonotonicityOfAppend()
    {
        var log = new ReplicationLog<string, int>();
        await SeedAsync(log, 1, 2, 3);
        await log.TruncateBelowAsync(3); // only seq 3 remains

        // Next append must still have seq > 3.
        var act = async () => await log.AppendAsync("k2", 2, 2);
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();

        await log.AppendAsync("k4", 4, 4);
        log.GetLatestSequenceNumber().Should().Be(4);
    }
}
