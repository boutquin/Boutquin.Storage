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
/// Unit tests for the SnapshotIsolationStore class.
/// Each test follows the Arrange-Act-Assert pattern.
/// </summary>
public sealed class SnapshotIsolationStoreTests
{
    /// <summary>
    /// Test that a simple read-write transaction commits successfully.
    /// </summary>
    [Fact]
    public async Task CommitAsync_SimpleReadWriteTransaction_Succeeds()
    {
        // Arrange
        var store = new SnapshotIsolationStore<string, int>();
        var txn = store.BeginTransaction();

        // Act
        await store.WriteAsync("key1", 42, txn).ConfigureAwait(true);
        var (value, found) = await store.ReadAsync("key1", txn).ConfigureAwait(true);
        var committed = await store.CommitAsync(txn).ConfigureAwait(true);

        // Assert
        Assert.True(found);
        Assert.Equal(42, value);
        Assert.True(committed);
    }

    /// <summary>
    /// Test that concurrent non-conflicting transactions both commit.
    /// </summary>
    [Fact]
    public async Task CommitAsync_NonConflictingConcurrentTxns_BothCommit()
    {
        // Arrange — two transactions writing to different keys
        var store = new SnapshotIsolationStore<string, int>();
        var txn1 = store.BeginTransaction();
        var txn2 = store.BeginTransaction();

        // Act
        await store.WriteAsync("key1", 1, txn1).ConfigureAwait(true);
        await store.WriteAsync("key2", 2, txn2).ConfigureAwait(true);
        var committed1 = await store.CommitAsync(txn1).ConfigureAwait(true);
        var committed2 = await store.CommitAsync(txn2).ConfigureAwait(true);

        // Assert
        Assert.True(committed1);
        Assert.True(committed2);
    }

    /// <summary>
    /// A concurrent read-modify-write of the SAME key is a wr-dependency, not write skew: txn1 reads
    /// account-A and writes account-B; txn2 reads account-A and writes account-A. txn1 commits first,
    /// then txn2 aborts because txn2's write of account-A collides with txn1's concurrently-committed
    /// READ of account-A (wr-antidependency, first-committer-wins). This was previously (mis)named
    /// <c>CommitAsync_WriteSkew_DetectedAndAborted</c>; the canonical disjoint-write write-skew shape
    /// is pinned separately by <see cref="CommitAsync_MaterializedKeyWriteSkew_BothTxnsReadBothKeys_OneAborts"/>.
    /// </summary>
    [Fact]
    public async Task CommitAsync_ConcurrentWriteOfAKeyAnotherTxnRead_SecondCommitterAborts()
    {
        // Arrange — set up initial state
        var store = new SnapshotIsolationStore<string, int>();
        var setup = store.BeginTransaction();
        await store.WriteAsync("account-A", 500, setup).ConfigureAwait(true);
        await store.WriteAsync("account-B", 500, setup).ConfigureAwait(true);
        await store.CommitAsync(setup).ConfigureAwait(true);

        var txn1 = store.BeginTransaction();
        var txn2 = store.BeginTransaction();

        // txn1 reads account-A; txn2 reads account-A.
        await store.ReadAsync("account-A", txn1).ConfigureAwait(true);
        await store.ReadAsync("account-A", txn2).ConfigureAwait(true);

        // txn1 writes account-B; txn2 writes account-A.
        await store.WriteAsync("account-B", 0, txn1).ConfigureAwait(true);
        await store.WriteAsync("account-A", 0, txn2).ConfigureAwait(true);

        var committed1 = await store.CommitAsync(txn1).ConfigureAwait(true);
        // txn2 wrote account-A, which the concurrently-committed txn1 had READ → wr-conflict → abort.
        var committed2 = await store.CommitAsync(txn2).ConfigureAwait(true);

        Assert.True(committed1);
        Assert.False(committed2);
    }

    /// <summary>
    /// The canonical write-skew shape (DDIA 2e — doctors-on-call): two transactions read the SAME
    /// set of keys, then each writes a DIFFERENT key (disjoint write sets). Because this store tracks
    /// materialized read sets, the rw-antidependency check catches it — txn2 read on_call_A, which
    /// the concurrently-committed txn1 wrote — so txn2 aborts. The genuinely undetected case is
    /// PREDICATE / phantom write skew, which a point-key store cannot express; that is the documented
    /// SSI boundary (<see cref="SnapshotIsolationStore{TKey,TValue}"/> remarks), not this scenario.
    /// </summary>
    [Fact]
    public async Task CommitAsync_MaterializedKeyWriteSkew_BothTxnsReadBothKeys_OneAborts()
    {
        var store = new SnapshotIsolationStore<string, int>();
        var setup = store.BeginTransaction();
        await store.WriteAsync("on_call_A", 1, setup).ConfigureAwait(true);
        await store.WriteAsync("on_call_B", 1, setup).ConfigureAwait(true);
        await store.CommitAsync(setup).ConfigureAwait(true);

        var txn1 = store.BeginTransaction();
        var txn2 = store.BeginTransaction();

        // Both read BOTH on-call flags (the shared constraint each transaction checks).
        await store.ReadAsync("on_call_A", txn1).ConfigureAwait(true);
        await store.ReadAsync("on_call_B", txn1).ConfigureAwait(true);
        await store.ReadAsync("on_call_A", txn2).ConfigureAwait(true);
        await store.ReadAsync("on_call_B", txn2).ConfigureAwait(true);

        // Disjoint writes: txn1 takes A off-call, txn2 takes B off-call.
        await store.WriteAsync("on_call_A", 0, txn1).ConfigureAwait(true);
        await store.WriteAsync("on_call_B", 0, txn2).ConfigureAwait(true);

        var committed1 = await store.CommitAsync(txn1).ConfigureAwait(true);
        var committed2 = await store.CommitAsync(txn2).ConfigureAwait(true);

        committed1.Should().BeTrue("the first committer wins");
        committed2.Should().BeFalse(
            "txn2 read on_call_A, which the concurrently-committed txn1 wrote — a materialized rw-antidependency");
    }

    /// <summary>
    /// Test rw-conflict: txn1 reads X, txn2 writes X and commits, txn1 tries to commit → aborted.
    /// </summary>
    [Fact]
    public async Task CommitAsync_ReadWriteConflict_AbortsReader()
    {
        // Arrange
        var store = new SnapshotIsolationStore<string, int>();
        var setup = store.BeginTransaction();
        await store.WriteAsync("key1", 100, setup).ConfigureAwait(true);
        await store.CommitAsync(setup).ConfigureAwait(true);

        // Act
        var txn1 = store.BeginTransaction();
        var txn2 = store.BeginTransaction();

        // txn1 reads key1
        await store.ReadAsync("key1", txn1).ConfigureAwait(true);

        // txn2 writes key1 and commits
        await store.WriteAsync("key1", 200, txn2).ConfigureAwait(true);
        var committed2 = await store.CommitAsync(txn2).ConfigureAwait(true);

        // txn1 tries to commit — should be aborted (rw-conflict: read key1, txn2 wrote key1 concurrently)
        var committed1 = await store.CommitAsync(txn1).ConfigureAwait(true);

        // Assert
        Assert.True(committed2);
        Assert.False(committed1);
    }

    /// <summary>
    /// Test that aborted transaction's writes are invisible.
    /// </summary>
    [Fact]
    public async Task AbortTransaction_WritesAreInvisible()
    {
        // Arrange
        var store = new SnapshotIsolationStore<string, int>();
        var txn1 = store.BeginTransaction();
        await store.WriteAsync("key1", 42, txn1).ConfigureAwait(true);
        store.AbortTransaction(txn1);

        // Act
        var txn2 = store.BeginTransaction();
        var (_, found) = await store.ReadAsync("key1", txn2).ConfigureAwait(true);

        // Assert
        Assert.False(found);
    }

    /// <summary>
    /// Test that non-overlapping read/write sets don't produce false positives.
    /// </summary>
    [Fact]
    public async Task CommitAsync_NonOverlappingReadWriteSets_NoFalsePositives()
    {
        // Arrange
        var store = new SnapshotIsolationStore<string, int>();
        var setup = store.BeginTransaction();
        await store.WriteAsync("key1", 1, setup).ConfigureAwait(true);
        await store.WriteAsync("key2", 2, setup).ConfigureAwait(true);
        await store.CommitAsync(setup).ConfigureAwait(true);

        // Act — txn1 reads key1, writes key2; txn2 reads key2, writes key1 (no overlap in rw-sets)
        // Actually this IS a conflict in SSI. Let's use truly non-overlapping sets.
        var txn1 = store.BeginTransaction();
        var txn2 = store.BeginTransaction();

        // txn1 reads key1, writes key3
        await store.ReadAsync("key1", txn1).ConfigureAwait(true);
        await store.WriteAsync("key3", 30, txn1).ConfigureAwait(true);

        // txn2 reads key2, writes key4
        await store.ReadAsync("key2", txn2).ConfigureAwait(true);
        await store.WriteAsync("key4", 40, txn2).ConfigureAwait(true);

        var committed1 = await store.CommitAsync(txn1).ConfigureAwait(true);
        var committed2 = await store.CommitAsync(txn2).ConfigureAwait(true);

        // Assert — both should succeed, no overlap
        Assert.True(committed1);
        Assert.True(committed2);
    }

    /// <summary>
    /// Test that sequential (non-concurrent) transactions always succeed.
    /// </summary>
    [Fact]
    public async Task CommitAsync_SequentialTransactions_AllSucceed()
    {
        // Arrange
        var store = new SnapshotIsolationStore<string, int>();

        // Act — three sequential transactions on the same key
        var txn1 = store.BeginTransaction();
        await store.WriteAsync("key1", 1, txn1).ConfigureAwait(true);
        await store.CommitAsync(txn1).ConfigureAwait(true);

        var txn2 = store.BeginTransaction();
        await store.ReadAsync("key1", txn2).ConfigureAwait(true);
        await store.WriteAsync("key1", 2, txn2).ConfigureAwait(true);
        var committed2 = await store.CommitAsync(txn2).ConfigureAwait(true);

        var txn3 = store.BeginTransaction();
        await store.ReadAsync("key1", txn3).ConfigureAwait(true);
        await store.WriteAsync("key1", 3, txn3).ConfigureAwait(true);
        var committed3 = await store.CommitAsync(txn3).ConfigureAwait(true);

        // Assert
        Assert.True(committed2);
        Assert.True(committed3);
    }

    /// <summary>
    /// Test wr-dependency: txn1 writes X, commits; txn2 reads X during overlap, writes Y, commits → aborted.
    /// </summary>
    [Fact]
    public async Task CommitAsync_WrDependency_AbortsWriter()
    {
        // Arrange
        var store = new SnapshotIsolationStore<string, int>();
        var setup = store.BeginTransaction();
        await store.WriteAsync("key1", 100, setup).ConfigureAwait(true);
        await store.CommitAsync(setup).ConfigureAwait(true);

        // Act — txn1 and txn2 start concurrently
        var txn1 = store.BeginTransaction();
        var txn2 = store.BeginTransaction();

        // txn1 reads key1, writes key2
        await store.ReadAsync("key1", txn1).ConfigureAwait(true);
        await store.WriteAsync("key2", 200, txn1).ConfigureAwait(true);

        // txn2 writes key1 (overlaps txn1's read set)
        await store.WriteAsync("key1", 300, txn2).ConfigureAwait(true);

        // txn2 commits first
        var committed2 = await store.CommitAsync(txn2).ConfigureAwait(true);
        // txn1 should be aborted — txn2 wrote to key1 which txn1 read
        var committed1 = await store.CommitAsync(txn1).ConfigureAwait(true);

        // Assert
        Assert.True(committed2);
        Assert.False(committed1);
    }

    /// <summary>
    /// Test that read-only transactions never conflict.
    /// </summary>
    [Fact]
    public async Task CommitAsync_ReadOnlyTransactions_NeverConflict()
    {
        // Arrange
        var store = new SnapshotIsolationStore<string, int>();
        var setup = store.BeginTransaction();
        await store.WriteAsync("key1", 1, setup).ConfigureAwait(true);
        await store.CommitAsync(setup).ConfigureAwait(true);

        // Act — two concurrent read-only transactions
        var txn1 = store.BeginTransaction();
        var txn2 = store.BeginTransaction();

        await store.ReadAsync("key1", txn1).ConfigureAwait(true);
        await store.ReadAsync("key1", txn2).ConfigureAwait(true);

        var committed1 = await store.CommitAsync(txn1).ConfigureAwait(true);
        var committed2 = await store.CommitAsync(txn2).ConfigureAwait(true);

        // Assert — both succeed, no writes means no conflicts
        Assert.True(committed1);
        Assert.True(committed2);
    }

    /// <summary>
    /// Two concurrent BLIND writes to the same key must conflict: first-committer-wins, the second
    /// aborts (finding #9). Without a ww-check both would commit and silently lose the first write
    /// (DDIA 2e p. 299 lost-update prevention; the interface contract promises to "detect concurrent
    /// point-key writes").
    /// </summary>
    [Fact]
    public async Task CommitAsync_BlindWriteOnlyTransactions_SameKey_FirstCommitterWins()
    {
        // Arrange — no prior state to read
        var store = new SnapshotIsolationStore<string, int>();

        var txn1 = store.BeginTransaction();
        var txn2 = store.BeginTransaction();

        // Act — both write to key1, neither reads (blind writes)
        await store.WriteAsync("key1", 1, txn1).ConfigureAwait(true);
        await store.WriteAsync("key1", 2, txn2).ConfigureAwait(true);

        var committed1 = await store.CommitAsync(txn1).ConfigureAwait(true);
        var committed2 = await store.CommitAsync(txn2).ConfigureAwait(true);

        // Assert — first committer wins; the concurrent same-key writer aborts.
        Assert.True(committed1);
        Assert.False(committed2);
    }

    [Fact]
    public async Task CommitAsync_BlindWriteOnlyTransactions_DifferentKeys_BothCommit()
    {
        // Guard against over-correction: blind writes to DIFFERENT keys have no ww-overlap.
        var store = new SnapshotIsolationStore<string, int>();
        var txn1 = store.BeginTransaction();
        var txn2 = store.BeginTransaction();

        await store.WriteAsync("key1", 1, txn1).ConfigureAwait(true);
        await store.WriteAsync("key2", 2, txn2).ConfigureAwait(true);

        (await store.CommitAsync(txn1).ConfigureAwait(true)).Should().BeTrue();
        (await store.CommitAsync(txn2).ConfigureAwait(true)).Should().BeTrue();
    }

    /// <summary>
    /// Test that aborted transaction cleanup allows new transactions to proceed.
    /// </summary>
    [Fact]
    public async Task AbortTransaction_CleanupAllowsNewTransactions()
    {
        // Arrange
        var store = new SnapshotIsolationStore<string, int>();
        var txn1 = store.BeginTransaction();
        await store.WriteAsync("key1", 42, txn1).ConfigureAwait(true);
        store.AbortTransaction(txn1);

        // Act — new transaction should work fine
        var txn2 = store.BeginTransaction();
        await store.WriteAsync("key1", 100, txn2).ConfigureAwait(true);
        var committed = await store.CommitAsync(txn2).ConfigureAwait(true);

        // Assert
        Assert.True(committed);

        // Verify the value
        var txn3 = store.BeginTransaction();
        var (value, found) = await store.ReadAsync("key1", txn3).ConfigureAwait(true);
        Assert.True(found);
        Assert.Equal(100, value);
    }

    /// <summary>
    /// Test multiple key read-write conflict scenario.
    /// </summary>
    [Fact]
    public async Task CommitAsync_MultiKeyConflict_DetectsCorrectly()
    {
        // Arrange
        var store = new SnapshotIsolationStore<string, int>();
        var setup = store.BeginTransaction();
        await store.WriteAsync("key1", 1, setup).ConfigureAwait(true);
        await store.WriteAsync("key2", 2, setup).ConfigureAwait(true);
        await store.WriteAsync("key3", 3, setup).ConfigureAwait(true);
        await store.CommitAsync(setup).ConfigureAwait(true);

        // Act — txn1 reads key1 and key2; txn2 writes key2 and commits
        var txn1 = store.BeginTransaction();
        var txn2 = store.BeginTransaction();

        await store.ReadAsync("key1", txn1).ConfigureAwait(true);
        await store.ReadAsync("key2", txn1).ConfigureAwait(true);

        await store.WriteAsync("key2", 20, txn2).ConfigureAwait(true);
        var committed2 = await store.CommitAsync(txn2).ConfigureAwait(true);

        // txn1 writes key3 (non-overlapping with txn2's writes)
        await store.WriteAsync("key3", 30, txn1).ConfigureAwait(true);
        var committed1 = await store.CommitAsync(txn1).ConfigureAwait(true);

        // Assert — txn1 read key2, txn2 wrote key2 concurrently → conflict
        Assert.True(committed2);
        Assert.False(committed1);
    }

    /// <summary>
    /// Test three concurrent transactions where only the conflicting one is aborted.
    /// </summary>
    [Fact]
    public async Task CommitAsync_ThreeConcurrentTxns_OnlyConflictingAborted()
    {
        // Arrange
        var store = new SnapshotIsolationStore<string, int>();
        var setup = store.BeginTransaction();
        await store.WriteAsync("x", 0, setup).ConfigureAwait(true);
        await store.CommitAsync(setup).ConfigureAwait(true);

        // Act
        var txn1 = store.BeginTransaction();
        var txn2 = store.BeginTransaction();
        var txn3 = store.BeginTransaction();

        // txn1: reads x, writes y — no conflict with txn3
        await store.ReadAsync("x", txn1).ConfigureAwait(true);
        await store.WriteAsync("y", 1, txn1).ConfigureAwait(true);

        // txn2: writes x (conflicts with txn1's read)
        await store.WriteAsync("x", 2, txn2).ConfigureAwait(true);

        // txn3: writes z (no conflict with anyone)
        await store.WriteAsync("z", 3, txn3).ConfigureAwait(true);

        var committed2 = await store.CommitAsync(txn2).ConfigureAwait(true);
        var committed1 = await store.CommitAsync(txn1).ConfigureAwait(true);
        var committed3 = await store.CommitAsync(txn3).ConfigureAwait(true);

        // Assert — txn2 commits (first), txn1 aborted (read x, txn2 wrote x), txn3 commits (independent)
        Assert.True(committed2);
        Assert.False(committed1);
        Assert.True(committed3);
    }

    /// <summary>
    /// After <see cref="SnapshotIsolationStore{TKey, TValue}.Dispose"/>, every public method
    /// throws <see cref="ObjectDisposedException"/>. The disposer also disposes the inner
    /// <c>MvccStore</c> so a single <c>using</c> block on the SI store releases both gates.
    /// </summary>
    [Fact]
    public async Task Dispose_IsIdempotent_AndPostDisposeMethodsThrow()
    {
        var store = new SnapshotIsolationStore<string, int>();
        var txn = store.BeginTransaction();
        await store.WriteAsync("k", 1, txn).ConfigureAwait(true);

        store.Dispose();
        store.Dispose(); // second dispose is a no-op (Interlocked-gated)

        Assert.Throws<ObjectDisposedException>(() => store.BeginTransaction());
        Assert.Throws<ObjectDisposedException>(() => store.AbortTransaction(txn));
        Assert.Throws<ObjectDisposedException>(() => _ = store.MinActiveCommitTimestamp);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => store.ReadAsync("k", txn));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => store.WriteAsync("k", 2, txn));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => store.CommitAsync(txn));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => store.PruneCommittedBelowAsync(0));
    }
}
