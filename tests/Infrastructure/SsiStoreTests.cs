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
using Boutquin.Storage.Infrastructure.Transactions;

namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// Unit tests for the SsiStore class.
/// Each test follows the Arrange-Act-Assert pattern.
/// </summary>
public sealed class SsiStoreTests
{
    /// <summary>
    /// Test that a simple read-write transaction commits successfully.
    /// </summary>
    [Fact]
    public async Task CommitAsync_SimpleReadWriteTransaction_Succeeds()
    {
        // Arrange
        var store = new SsiStore<string, int>();
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
        var store = new SsiStore<string, int>();
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
    /// Test write skew detection: txn1 reads X, txn2 writes X and commits, txn1 tries to commit → aborted.
    /// </summary>
    [Fact]
    public async Task CommitAsync_WriteSkew_DetectedAndAborted()
    {
        // Arrange — set up initial state
        var store = new SsiStore<string, int>();
        var setup = store.BeginTransaction();
        await store.WriteAsync("account-A", 500, setup).ConfigureAwait(true);
        await store.WriteAsync("account-B", 500, setup).ConfigureAwait(true);
        await store.CommitAsync(setup).ConfigureAwait(true);

        // Act — classic write skew: both read total, both write one account
        var txn1 = store.BeginTransaction();
        var txn2 = store.BeginTransaction();

        // Both read account-A (to check total balance)
        await store.ReadAsync("account-A", txn1).ConfigureAwait(true);
        await store.ReadAsync("account-A", txn2).ConfigureAwait(true);

        // txn1 writes account-B, txn2 writes account-A
        await store.WriteAsync("account-B", 0, txn1).ConfigureAwait(true);
        await store.WriteAsync("account-A", 0, txn2).ConfigureAwait(true);

        // txn1 commits first
        var committed1 = await store.CommitAsync(txn1).ConfigureAwait(true);
        // txn2 should be aborted — it read account-A, and txn2 wrote account-A...
        // but the conflict is: txn2 read account-A, txn1 committed and wrote account-B
        // txn2's write set (account-A) overlaps txn1's read set (account-A)? No.
        // Actually: txn1 read account-A, and txn2 wrote account-A and wants to commit.
        // txn2's write set {account-A} overlaps txn1's read set {account-A}, and txn1 committed concurrently.
        var committed2 = await store.CommitAsync(txn2).ConfigureAwait(true);

        // Assert — one should succeed, the other should fail
        Assert.True(committed1);
        Assert.False(committed2);
    }

    /// <summary>
    /// Test rw-conflict: txn1 reads X, txn2 writes X and commits, txn1 tries to commit → aborted.
    /// </summary>
    [Fact]
    public async Task CommitAsync_ReadWriteConflict_AbortsReader()
    {
        // Arrange
        var store = new SsiStore<string, int>();
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
        var store = new SsiStore<string, int>();
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
        var store = new SsiStore<string, int>();
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
        var store = new SsiStore<string, int>();

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
        var store = new SsiStore<string, int>();
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
        var store = new SsiStore<string, int>();
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
    /// Test that write-only transactions on the same key don't conflict (no read sets overlap).
    /// </summary>
    [Fact]
    public async Task CommitAsync_WriteOnlyTransactions_SameKey_DontConflict()
    {
        // Arrange — no prior state to read
        var store = new SsiStore<string, int>();

        var txn1 = store.BeginTransaction();
        var txn2 = store.BeginTransaction();

        // Act — both write to key1 but neither reads
        await store.WriteAsync("key1", 1, txn1).ConfigureAwait(true);
        await store.WriteAsync("key1", 2, txn2).ConfigureAwait(true);

        var committed1 = await store.CommitAsync(txn1).ConfigureAwait(true);
        var committed2 = await store.CommitAsync(txn2).ConfigureAwait(true);

        // Assert — no read sets, so no rw/wr conflicts
        Assert.True(committed1);
        Assert.True(committed2);
    }

    /// <summary>
    /// Test that aborted transaction cleanup allows new transactions to proceed.
    /// </summary>
    [Fact]
    public async Task AbortTransaction_CleanupAllowsNewTransactions()
    {
        // Arrange
        var store = new SsiStore<string, int>();
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
        var store = new SsiStore<string, int>();
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
        var store = new SsiStore<string, int>();
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
}
