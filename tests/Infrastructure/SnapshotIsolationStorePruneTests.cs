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
/// Phase B tests for <see cref="SnapshotIsolationStore{TKey, TValue}"/> — MinActiveCommitTimestamp +
/// PruneCommittedBelowAsync.
/// </summary>
public sealed class SnapshotIsolationStorePruneTests
{
    [Fact]
    public void MinActiveCommitTimestamp_NoActiveTxns_ReturnsMaxValue()
    {
        var store = new SnapshotIsolationStore<string, int>();

        store.MinActiveCommitTimestamp.Should().Be(long.MaxValue);
    }

    [Fact]
    public async Task MinActiveCommitTimestamp_ExcludesCommittedTxns()
    {
        var store = new SnapshotIsolationStore<string, int>();
        var t1 = store.BeginTransaction();
        var t2 = store.BeginTransaction();

        await store.CommitAsync(t1);

        store.MinActiveCommitTimestamp.Should().Be(t2, "t1 is no longer active; t2 is the oldest active");
    }

    [Fact]
    public async Task PruneCommittedBelow_RemovesCommittedRegistryEntries()
    {
        var store = new SnapshotIsolationStore<string, int>();

        // Commit three transactions.
        var t1 = store.BeginTransaction();
        await store.WriteAsync("k", 1, t1);
        await store.CommitAsync(t1);

        var t2 = store.BeginTransaction();
        await store.WriteAsync("k", 2, t2);
        await store.CommitAsync(t2);

        var t3 = store.BeginTransaction();
        await store.WriteAsync("k", 3, t3);
        await store.CommitAsync(t3);

        // No active transactions — prune below t3.
        store.MinActiveCommitTimestamp.Should().Be(long.MaxValue);
        var pruned = await store.PruneCommittedBelowAsync(t3);

        pruned.Should().BeGreaterThan(0, "committed-registry entries for txns below t3 must be removed");

        // New transactions still work correctly post-prune.
        var t4 = store.BeginTransaction();
        var (v, found) = await store.ReadAsync("k", t4);
        found.Should().BeTrue();
        v.Should().Be(3);
        (await store.CommitAsync(t4)).Should().BeTrue();
    }

    [Fact]
    public async Task PruneCommittedBelow_RejectsWatermarkAtOrAboveMinActive()
    {
        var store = new SnapshotIsolationStore<string, int>();
        var t1 = store.BeginTransaction();
        // t1 is still active; MinActive == t1. Both watermark == t1 and watermark == t1+1 must throw
        // (strict inequality — dropping committed residue with id == MinActive could race with
        // t1's own commit-time conflict check).

        await FluentActions.Invoking(() => store.PruneCommittedBelowAsync(t1))
            .Should().ThrowAsync<ArgumentOutOfRangeException>();
        await FluentActions.Invoking(() => store.PruneCommittedBelowAsync(t1 + 1))
            .Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task PruneCommittedBelow_DoesNotBreakConflictDetection()
    {
        var store = new SnapshotIsolationStore<string, int>();

        // Phase 1: t1 commits a write, t2 reads it and commits (t1 is in t2's snapshot, no
        // conflict). Both complete cleanly. No active txns remain.
        var t1 = store.BeginTransaction();
        await store.WriteAsync("x", 1, t1);
        (await store.CommitAsync(t1)).Should().BeTrue();

        var t2 = store.BeginTransaction();
        await store.ReadAsync("x", t2);
        (await store.CommitAsync(t2)).Should().BeTrue();

        store.MinActiveCommitTimestamp.Should().Be(long.MaxValue);
        var pruned = await store.PruneCommittedBelowAsync(t2 + 1);
        pruned.Should().Be(2, "both t1 and t2 registry entries should be dropped");

        // Phase 2: start a fresh rw-conflict scenario AFTER the prune. If pruning broke
        // conflict detection, t3's read of x would not be flagged against t4's write.
        var t3 = store.BeginTransaction();
        await store.ReadAsync("x", t3);

        var t4 = store.BeginTransaction();
        await store.WriteAsync("x", 42, t4);
        (await store.CommitAsync(t4)).Should().BeTrue();

        (await store.CommitAsync(t3)).Should().BeFalse(
            "rw-conflict against t4 (concurrent writer) must still be detected after pruning old registry");
    }

    /// <summary>
    /// Pruning a committed transaction must not resurrect it as "active": the ephemeral
    /// read/write-set bookkeeping for a committed transaction is cleaned up at commit time,
    /// so removing its committed-registry entry leaves no residue that
    /// <see cref="SnapshotIsolationStore{TKey, TValue}.MinActiveCommitTimestamp"/> would
    /// misreport as an active transaction — which would wedge every subsequent prune
    /// (watermark &gt;= the phantom "active" id throws).
    /// </summary>
    [Fact]
    public async Task PruneCommittedBelow_DoesNotResurrectPrunedTxnsAsActive()
    {
        var store = new SnapshotIsolationStore<string, int>();

        var t1 = store.BeginTransaction();
        await store.WriteAsync("k", 1, t1);
        (await store.CommitAsync(t1)).Should().BeTrue();

        var t2 = store.BeginTransaction();
        await store.WriteAsync("k", 2, t2);
        (await store.CommitAsync(t2)).Should().BeTrue();

        (await store.PruneCommittedBelowAsync(t2 + 1)).Should().Be(2);

        // No transaction is active — pruned committed txns must not come back as "active".
        store.MinActiveCommitTimestamp.Should().Be(long.MaxValue,
            "t1 and t2 committed and were pruned; neither is active");

        // And a repeat prune is a clean no-op, not an ArgumentOutOfRangeException wedge.
        (await store.PruneCommittedBelowAsync(t2 + 1)).Should().Be(0);
    }

    /// <summary>
    /// Transaction ids are BEGIN order, not commit order — so "id &lt; every active id" does
    /// NOT imply "committed before every active transaction began". A committed transaction
    /// that committed AFTER an active transaction began is CONCURRENT with it, and its
    /// registry entry is exactly what the active transaction's commit-time conflict loops
    /// need. Pruning it via the documented recipe (watermark &lt; MinActiveCommitTimestamp)
    /// silently skips first-committer-wins — a textbook lost update (DDIA 2e Ch. 8: a
    /// database "must prevent lost updates in order to qualify" as snapshot isolation).
    /// </summary>
    [Fact]
    public async Task PruneCommittedBelow_RetainsTxnThatCommittedAfterAnActiveTxnBegan()
    {
        using var store = new SnapshotIsolationStore<string, int>();

        // t1 begins first (lowest id) and writes k — but does not commit yet.
        var t1 = store.BeginTransaction();
        await store.WriteAsync("k", 1, t1);

        // Filler t2 begins and commits so the watermark t2 is a plausible operator choice.
        var t2 = store.BeginTransaction();
        await store.WriteAsync("filler", 2, t2);
        (await store.CommitAsync(t2)).Should().BeTrue();

        // t3 begins BEFORE t1 commits — t1 and t3 are concurrent.
        var t3 = store.BeginTransaction();

        // t1 commits after t3 began.
        (await store.CommitAsync(t1)).Should().BeTrue();

        // The documented recipe: watermark (t2) < MinActiveCommitTimestamp (t3). The prune
        // must NOT drop t1's registry residue — t1 committed after the active t3 began.
        await store.PruneCommittedBelowAsync(t2);

        // t3 blind-writes the same key and tries to commit: first-committer-wins says abort.
        await store.WriteAsync("k", 3, t3);
        (await store.CommitAsync(t3)).Should().BeFalse(
            "t1 committed the same key first while concurrent with t3; pruning t1's residue must not erase the conflict");
    }

    /// <summary>
    /// The prune must bound BOTH growth vectors the long-running-workload remark promises to
    /// solve: the SI committed-txn registry AND the inner MVCC store's version chains /
    /// transaction bookkeeping. Pruning only the registry leaves the wrapped store growing
    /// without bound — the inner prune is unreachable by the operator (the field is private).
    /// </summary>
    [Fact]
    public async Task PruneCommittedBelow_AlsoPrunesInnerMvccVersionChains()
    {
        using var store = new SnapshotIsolationStore<string, int>();

        long last = 0;
        for (var i = 0; i < 5; i++)
        {
            var t = store.BeginTransaction();
            await store.WriteAsync("k", i, t);
            (await store.CommitAsync(t)).Should().BeTrue();
            last = t;
        }

        store.InnerMvccForTesting.GetVersionChainLengthForTesting("k").Should().Be(5, "precondition");

        await store.PruneCommittedBelowAsync(last + 1);

        store.InnerMvccForTesting.GetVersionChainLengthForTesting("k")
            .Should().Be(1, "the prune must collapse the chain to the newest committed backdrop");
    }
}
