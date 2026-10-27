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
/// Visibility tests for <see cref="MvccStore{TKey, TValue}"/> across a prune watermark.
/// A prune/GC watermark must NEVER change read results: a committed version whose writer
/// committed at or below the watermark must remain visible to every transaction (it was
/// committed before any still-relevant begin). These tests pin that invariant plus the
/// preserved properties — newest-wins, tombstone visibility, and active-transaction
/// snapshot stability.
/// </summary>
public sealed class MvccStorePostPruneVisibilityTests
{
    /// <summary>
    /// The primary repro: after PruneBelowAsync drops the committed writer's id from the
    /// transaction-state map, a transaction begun AFTER the prune must still read the
    /// committed version. Pre-fix this returned (0, false) — a phantom read / data loss.
    /// </summary>
    [Fact]
    public async Task PostPrune_CommittedVersion_VisibleToNewTransaction()
    {
        var store = new MvccStore<string, int>();

        var t1 = store.BeginTransaction();
        await store.WriteAsync("k", 1, t1);
        await store.CommitAsync(t1);

        var t2 = store.BeginTransaction();              // active; pins the version, keeps the watermark legal
        var removed = await store.PruneBelowAsync(t2 - 1); // prunes t1 from _transactionStates
        var t3 = store.BeginTransaction();              // snapshot built after prune excludes t1

        var (value, found) = await store.ReadAsync("k", t3);

        found.Should().BeTrue("a committed version below the prune watermark must stay globally visible");
        value.Should().Be(1);
        removed.Should().Be(0, "the version is pinned by the active t2 backdrop — this is a visibility bug, not a GC bug");
    }

    /// <summary>
    /// Snapshot stability: an ACTIVE transaction begun BEFORE the prune must read exactly the
    /// value its begin-time snapshot resolved, undisturbed by the prune. (Its captured floor
    /// predates the prune, so the prune cannot shift its view.)
    /// </summary>
    [Fact]
    public async Task PostPrune_ActivePrePruneTransaction_SnapshotUnchanged()
    {
        var store = new MvccStore<string, int>();

        var t1 = store.BeginTransaction();
        await store.WriteAsync("k", 1, t1);
        await store.CommitAsync(t1);

        var t2 = store.BeginTransaction();              // active, sees committed t1 (value 1)
        await store.PruneBelowAsync(t2 - 1);

        var (value, found) = await store.ReadAsync("k", t2);

        found.Should().BeTrue();
        value.Should().Be(1, "t2's begin-time snapshot already saw committed t1; the prune must not disturb it");
    }

    /// <summary>
    /// Tombstone visibility: a committed DELETE (tombstone) below the prune watermark must
    /// still HIDE the key for a transaction begun after the prune. The visibility floor must
    /// not resurrect a deleted key.
    /// </summary>
    [Fact]
    public async Task PostPrune_CommittedTombstone_StillHidesKey()
    {
        var store = new MvccStore<string, int>();

        var t1 = store.BeginTransaction();
        await store.WriteAsync("k", 1, t1);
        await store.CommitAsync(t1);

        var t2 = store.BeginTransaction();
        await store.DeleteAsync("k", t2);               // committed tombstone
        await store.CommitAsync(t2);

        var t3 = store.BeginTransaction();              // active; keeps watermark legal
        await store.PruneBelowAsync(t3 - 1);
        var t4 = store.BeginTransaction();              // begun after prune

        var (value, found) = await store.ReadAsync("k", t4);

        found.Should().BeFalse("a committed tombstone below the watermark must keep the key deleted, not resurrect it");
        value.Should().Be(0);
    }

    /// <summary>
    /// Newest-wins: a pruned committed version (k=1) must not shadow a later committed
    /// version (k=2). A reader begun after the prune sees the newest committed value.
    /// </summary>
    [Fact]
    public async Task PostPrune_NewerCommittedVersion_WinsOverPrunedBase()
    {
        var store = new MvccStore<string, int>();

        var t1 = store.BeginTransaction();
        await store.WriteAsync("k", 1, t1);
        await store.CommitAsync(t1);

        var t2 = store.BeginTransaction();
        await store.WriteAsync("k", 2, t2);
        await store.CommitAsync(t2);

        var t3 = store.BeginTransaction();              // active; keeps watermark legal
        await store.PruneBelowAsync(t3 - 1);
        var t4 = store.BeginTransaction();              // begun after prune

        var (value, found) = await store.ReadAsync("k", t4);

        found.Should().BeTrue();
        value.Should().Be(2, "the newest committed version wins; the pruned base (k=1) must not shadow it");
    }

    /// <summary>
    /// The second-prune direction of the invariant: an active reader whose visible version
    /// rides ONLY on its begin-time commit floor (the writer was pruned from the transaction
    /// registry before the reader began, so the version is in neither the reader's snapshot
    /// nor its own writes) must keep that version across a LATER prune. The prune pin loop
    /// must honour all three visibility clauses ReadAsync uses — own-write, snapshot, AND
    /// floor — or the second prune drops the version and the reader's result flips
    /// mid-transaction (DDIA 2e Ch. 8: GC removes only versions "no longer visible to any
    /// transaction").
    /// </summary>
    [Fact]
    public async Task SecondPrune_PreservesFloorVisibleVersion_ForActivePrePruneReader()
    {
        var store = new MvccStore<string, int>();

        // t1 commits k=1; t2 begins and writes k=2 but stays uncommitted for now.
        var t1 = store.BeginTransaction();
        await store.WriteAsync("k", 1, t1);
        await store.CommitAsync(t1);

        var t2 = store.BeginTransaction();
        await store.WriteAsync("k", 2, t2);

        // First prune removes t1 from the registry (watermark = t1; minActive = t2).
        await store.PruneBelowAsync(t1);

        // Reader R begins AFTER the first prune: its snapshot is empty of t1 (pruned) and of
        // t2 (active) — k=1 is visible to R exclusively via R's begin-time commit floor.
        var reader = store.BeginTransaction();
        var (before, foundBefore) = await store.ReadAsync("k", reader);
        foundBefore.Should().BeTrue("precondition: the floor clause resolves k=1 for R");
        before.Should().Be(1);

        // t2 commits AFTER R began — R must keep ignoring it (snapshot rule 1).
        await store.CommitAsync(t2);

        // Second prune (watermark = t2; minActive = R). The pin loop must keep k=1 alive:
        // it is R's only visible version.
        await store.PruneBelowAsync(t2);

        var (after, foundAfter) = await store.ReadAsync("k", reader);
        foundAfter.Should().BeTrue("an active reader's visible version must survive every prune");
        after.Should().Be(1, "R's read must not flip mid-transaction — the floor-visible version was R's ReadAsync winner");
    }
}
