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
/// Unit tests for the MvccStore class.
/// Each test follows the Arrange-Act-Assert pattern.
/// </summary>
public sealed class MvccStoreTests
{
    /// <summary>
    /// Test that a transaction can read its own writes.
    /// </summary>
    [Fact]
    public async Task ReadAsync_OwnWrites_ReturnsWrittenValue()
    {
        // Arrange
        var store = new MvccStore<string, int>();
        var txn = store.BeginTransaction();

        // Act
        await store.WriteAsync("key1", 42, txn).ConfigureAwait(true);
        var (value, found) = await store.ReadAsync("key1", txn).ConfigureAwait(true);

        // Assert
        Assert.True(found);
        Assert.Equal(42, value);
    }

    /// <summary>
    /// Test that snapshot isolation prevents seeing uncommitted writes from other transactions.
    /// </summary>
    [Fact]
    public async Task ReadAsync_UncommittedWriteFromOtherTxn_NotVisible()
    {
        // Arrange
        var store = new MvccStore<string, int>();
        var txn1 = store.BeginTransaction();
        var txn2 = store.BeginTransaction();

        // Act — txn1 writes, txn2 tries to read (txn1 not committed)
        await store.WriteAsync("key1", 100, txn1).ConfigureAwait(true);
        var (_, found) = await store.ReadAsync("key1", txn2).ConfigureAwait(true);

        // Assert
        Assert.False(found);
    }

    /// <summary>
    /// Test that committed writes are visible to new transactions.
    /// </summary>
    [Fact]
    public async Task ReadAsync_CommittedWrite_VisibleToNewTransaction()
    {
        // Arrange
        var store = new MvccStore<string, int>();
        var txn1 = store.BeginTransaction();
        await store.WriteAsync("key1", 42, txn1).ConfigureAwait(true);
        await store.CommitAsync(txn1).ConfigureAwait(true);

        // Act — new transaction should see the committed value
        var txn2 = store.BeginTransaction();
        var (value, found) = await store.ReadAsync("key1", txn2).ConfigureAwait(true);

        // Assert
        Assert.True(found);
        Assert.Equal(42, value);
    }

    /// <summary>
    /// Test that aborted writes are invisible to all transactions.
    /// </summary>
    [Fact]
    public async Task ReadAsync_AbortedWrite_InvisibleToAll()
    {
        // Arrange
        var store = new MvccStore<string, int>();
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
    /// Test that read returns the latest committed version, not a future uncommitted version.
    /// </summary>
    [Fact]
    public async Task ReadAsync_ReturnsLatestCommittedVersion_NotFutureVersion()
    {
        // Arrange
        var store = new MvccStore<string, int>();

        // txn1 writes and commits value 1
        var txn1 = store.BeginTransaction();
        await store.WriteAsync("key1", 1, txn1).ConfigureAwait(true);
        await store.CommitAsync(txn1).ConfigureAwait(true);

        // txn2 starts (sees txn1's commit)
        var txn2 = store.BeginTransaction();

        // txn3 writes and commits value 2 (after txn2 started)
        var txn3 = store.BeginTransaction();
        await store.WriteAsync("key1", 2, txn3).ConfigureAwait(true);
        await store.CommitAsync(txn3).ConfigureAwait(true);

        // Act — txn2 should see value 1, not value 2
        var (value, found) = await store.ReadAsync("key1", txn2).ConfigureAwait(true);

        // Assert
        Assert.True(found);
        Assert.Equal(1, value);
    }

    /// <summary>
    /// Test that BeginTransaction returns monotonically increasing IDs.
    /// </summary>
    [Fact]
    public void BeginTransaction_ReturnsMonotonicallyIncreasingIds()
    {
        // Arrange
        var store = new MvccStore<string, int>();

        // Act
        var txn1 = store.BeginTransaction();
        var txn2 = store.BeginTransaction();
        var txn3 = store.BeginTransaction();

        // Assert
        Assert.True(txn1 < txn2);
        Assert.True(txn2 < txn3);
    }

    /// <summary>
    /// Test that null key throws ArgumentNullException on read.
    /// </summary>
    [Fact]
    public async Task ReadAsync_NullKey_ThrowsArgumentNullException()
    {
        // Arrange
        var store = new MvccStore<string, int>();
        var txn = store.BeginTransaction();

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => store.ReadAsync(null!, txn)).ConfigureAwait(true);
    }

    /// <summary>
    /// Test that null key throws ArgumentNullException on write.
    /// </summary>
    [Fact]
    public async Task WriteAsync_NullKey_ThrowsArgumentNullException()
    {
        // Arrange
        var store = new MvccStore<string, int>();
        var txn = store.BeginTransaction();

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => store.WriteAsync(null!, 42, txn)).ConfigureAwait(true);
    }

    /// <summary>
    /// Test that CommitAsync returns true for a successful commit.
    /// </summary>
    [Fact]
    public async Task CommitAsync_ActiveTransaction_ReturnsTrue()
    {
        // Arrange
        var store = new MvccStore<string, int>();
        var txn = store.BeginTransaction();
        await store.WriteAsync("key1", 42, txn).ConfigureAwait(true);

        // Act
        var result = await store.CommitAsync(txn).ConfigureAwait(true);

        // Assert
        Assert.True(result);
    }

    /// <summary>
    /// Test that multiple writes to the same key within a transaction returns the latest write.
    /// </summary>
    [Fact]
    public async Task ReadAsync_MultipleWritesSameKey_ReturnsLatest()
    {
        // Arrange
        var store = new MvccStore<string, int>();
        var txn = store.BeginTransaction();

        // Act
        await store.WriteAsync("key1", 1, txn).ConfigureAwait(true);
        await store.WriteAsync("key1", 2, txn).ConfigureAwait(true);
        await store.WriteAsync("key1", 3, txn).ConfigureAwait(true);
        var (value, found) = await store.ReadAsync("key1", txn).ConfigureAwait(true);

        // Assert
        Assert.True(found);
        Assert.Equal(3, value);
    }

    /// <summary>
    /// Test that reading a key that was never written returns not found.
    /// </summary>
    [Fact]
    public async Task ReadAsync_NonExistentKey_ReturnsNotFound()
    {
        // Arrange
        var store = new MvccStore<string, int>();
        var txn = store.BeginTransaction();

        // Act
        var (_, found) = await store.ReadAsync("nonexistent", txn).ConfigureAwait(true);

        // Assert
        Assert.False(found);
    }

    /// <summary>
    /// Test that CommitAsync returns false for an already-aborted transaction.
    /// This validates the defensive path in CommitAsync where the transaction
    /// state is no longer Active.
    /// </summary>
    [Fact]
    public async Task CommitAsync_AlreadyAbortedTransaction_ReturnsFalse()
    {
        // Arrange
        var store = new MvccStore<string, int>();
        var txn = store.BeginTransaction();
        await store.WriteAsync("key1", 42, txn).ConfigureAwait(true);
        store.AbortTransaction(txn);

        // Act — try to commit an already-aborted transaction
        var result = await store.CommitAsync(txn).ConfigureAwait(true);

        // Assert
        Assert.False(result);
    }
}
