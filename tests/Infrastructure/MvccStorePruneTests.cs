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
/// Phase B tests for <see cref="MvccStore{TKey, TValue}"/> — MinActiveTransactionId + PruneBelowAsync.
/// </summary>
public sealed class MvccStorePruneTests
{
    [Fact]
    public void MinActiveTransactionId_NoActiveTxns_ReturnsMaxValue()
    {
        var store = new MvccStore<string, int>();

        store.MinActiveTransactionId.Should().Be(long.MaxValue);
    }

    [Fact]
    public async Task MinActiveTransactionId_WithActive_ReturnsOldestActive()
    {
        var store = new MvccStore<string, int>();

        var t1 = store.BeginTransaction();
        var t2 = store.BeginTransaction();
        await store.CommitAsync(t1);

        store.MinActiveTransactionId.Should().Be(t2);
    }

    [Fact]
    public async Task PruneBelow_RejectsWatermarkAboveMinActive()
    {
        var store = new MvccStore<string, int>();
        var t1 = store.BeginTransaction();

        var act = async () => await store.PruneBelowAsync(t1);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task PruneBelow_RemovesOldVersionResidue()
    {
        var store = new MvccStore<string, int>();

        // Produce a chain with multiple versions for key "k".
        var t1 = store.BeginTransaction();
        await store.WriteAsync("k", 1, t1);
        await store.CommitAsync(t1);

        var t2 = store.BeginTransaction();
        await store.WriteAsync("k", 2, t2);
        await store.CommitAsync(t2);

        var t3 = store.BeginTransaction();
        await store.WriteAsync("k", 3, t3);
        await store.CommitAsync(t3);

        // No active transactions — prune everything below t3.
        store.MinActiveTransactionId.Should().Be(long.MaxValue);
        var pruned = await store.PruneBelowAsync(t3 - 1);

        // Two of the three versions should have been dropped; the tip (v=3) remains visible,
        // and exactly one "backdrop" version with id <= watermark is retained so in-flight
        // readers (none in this test) cannot observe a disappeared value.
        pruned.Should().BeGreaterThan(0);

        // Fresh reader after the prune still sees the committed tip.
        var t4 = store.BeginTransaction();
        var (v, found) = await store.ReadAsync("k", t4);
        found.Should().BeTrue();
        v.Should().Be(3);
    }

    [Fact]
    public async Task PruneBelow_PreservesVersionsVisibleToActiveReader()
    {
        var store = new MvccStore<string, int>();

        var t1 = store.BeginTransaction();
        await store.WriteAsync("k", 1, t1);
        await store.CommitAsync(t1);

        // t2 begins while v=1 is the committed value — t2's snapshot contains t1.
        var t2 = store.BeginTransaction();

        var t3 = store.BeginTransaction();
        await store.WriteAsync("k", 2, t3);
        await store.CommitAsync(t3);

        // Prune below t2 (allowed: MinActive == t2). t1's version must be preserved
        // as the "backdrop" so t2 still reads v=1 (t3 is not in t2's snapshot).
        store.MinActiveTransactionId.Should().Be(t2);
        await store.PruneBelowAsync(t2 - 1);

        var (v, found) = await store.ReadAsync("k", t2);
        found.Should().BeTrue();
        v.Should().Be(1, "t2's snapshot predates t3; it must still see t1's value after the prune");
    }

    [Fact]
    public async Task PruneBelow_DoesNotDropVersionAnActiveReaderStillNeeds()
    {
        // #3: SI visibility is keyed on commit-relative-to-begin, not transaction-id order.
        // A low-id version that commits LATE can become the "newest committed id <= watermark"
        // backdrop yet be invisible to an active reader whose snapshot predates that commit —
        // and the older version the reader actually needs then gets pruned (DDIA 2e p. 295 —
        // MVCC must retain every version any open snapshot can still read).
        var store = new MvccStore<string, int>();

        var t1 = store.BeginTransaction();          // id 1
        await store.WriteAsync("k", 1, t1);
        await store.CommitAsync(t1);                 // v=1 committed @ id 1

        var t2 = store.BeginTransaction();          // id 2
        await store.WriteAsync("k", 2, t2);          // v=2 @ id 2 — not committed yet

        var reader = store.BeginTransaction();      // id 3 — snapshot = {t1} (t2 uncommitted)

        await store.CommitAsync(t2);                 // v=2 @ id 2 commits AFTER the reader began

        // Prune below the reader (allowed: MinActive == reader). watermark = id 2, so the late
        // backdrop becomes v=2(id 2) and the un-fixed code drops v=1(id 1) — the version the
        // reader still needs because t2 is not in its snapshot.
        store.MinActiveTransactionId.Should().Be(reader);
        await store.PruneBelowAsync(reader - 1);

        var (v, found) = await store.ReadAsync("k", reader);
        found.Should().BeTrue("the reader's snapshot predates t2's commit; it must still see t1's v=1 after the prune");
        v.Should().Be(1);
    }

    [Fact]
    public async Task PruneBelow_IsIdempotent()
    {
        var store = new MvccStore<string, int>();
        var t1 = store.BeginTransaction();
        await store.WriteAsync("k", 1, t1);
        await store.CommitAsync(t1);

        await store.PruneBelowAsync(t1);
        var pruned2 = await store.PruneBelowAsync(t1);

        pruned2.Should().Be(0, "re-pruning at the same watermark is a no-op");
    }
}
