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
    /// Committing an already-aborted transaction is a caller error, not a conflict.
    /// <c>CommitAsync</c> reserves a <c>false</c> return for a
    /// genuine write-write conflict; an aborted (i.e. non-active) handle must throw
    /// <see cref="InvalidOperationException"/> instead — mirroring the active-only guard
    /// <see cref="MvccStore{TKey,TValue}.WriteAsync"/> already enforces.
    /// </summary>
    /// <remarks>
    /// This replaces an earlier assertion that committing an aborted transaction returned
    /// <c>false</c>. That assertion encoded the buggy contract this fix corrects: <c>false</c>
    /// conflated "aborted due to a conflict" (the documented meaning) with "you committed a
    /// handle that was not active" (a programmer error). The new assertion locks the corrected
    /// throw-on-non-active contract.
    /// </remarks>
    [Fact]
    public async Task CommitAsync_AlreadyAbortedTransaction_ThrowsInvalidOperationException()
    {
        // Arrange
        var store = new MvccStore<string, int>();
        var txn = store.BeginTransaction();
        await store.WriteAsync("key1", 42, txn).ConfigureAwait(true);
        store.AbortTransaction(txn);

        // Act & Assert — committing an already-aborted handle is a caller error, not a conflict.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.CommitAsync(txn)).ConfigureAwait(true);
        ex.Message.Should().Contain("Aborted");
    }

    /// <summary>
    /// Committing a transaction id that was never returned by
    /// <see cref="MvccStore{TKey,TValue}.BeginTransaction"/> is a caller error, not a conflict —
    /// it must throw <see cref="InvalidOperationException"/>, not return <c>false</c>.
    /// </summary>
    [Fact]
    public async Task CommitAsync_UnknownTransactionId_ThrowsInvalidOperationException()
    {
        var store = new MvccStore<string, int>();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.CommitAsync(9999L)).ConfigureAwait(true);
        ex.Message.Should().Contain("not known");
    }

    /// <summary>
    /// Committing an already-committed transaction is a caller error (double-commit), not a
    /// conflict — it must throw <see cref="InvalidOperationException"/>, not return <c>false</c>.
    /// </summary>
    [Fact]
    public async Task CommitAsync_AlreadyCommittedTransaction_ThrowsInvalidOperationException()
    {
        var store = new MvccStore<string, int>();
        var txn = store.BeginTransaction();
        await store.WriteAsync("key1", 42, txn).ConfigureAwait(true);
        Assert.True(await store.CommitAsync(txn).ConfigureAwait(true));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.CommitAsync(txn)).ConfigureAwait(true);
        ex.Message.Should().Contain("Committed");
    }

    /// <summary>
    /// Aborting a transaction id that was never begun must throw
    /// <see cref="InvalidOperationException"/> rather than silently inserting a phantom
    /// <c>Aborted</c> entry into the transaction-state table (table bloat; a later
    /// <see cref="MvccStore{TKey,TValue}.ReadAsync"/> on that id would then throw on the
    /// aborted-handle path instead of the cleaner unknown-id path).
    /// </summary>
    [Fact]
    public void AbortTransaction_UnknownTransactionId_ThrowsInvalidOperationException()
    {
        var store = new MvccStore<string, int>();

        var ex = Assert.Throws<InvalidOperationException>(() => store.AbortTransaction(9999L));
        ex.Message.Should().Contain("not known");
    }

    /// <summary>
    /// Aborting an already-committed transaction must throw
    /// <see cref="InvalidOperationException"/> — a committed transaction's writes are durable
    /// and cannot be retroactively undone by an abort.
    /// </summary>
    [Fact]
    public async Task AbortTransaction_AlreadyCommittedTransaction_ThrowsInvalidOperationException()
    {
        var store = new MvccStore<string, int>();
        var txn = store.BeginTransaction();
        await store.WriteAsync("key1", 42, txn).ConfigureAwait(true);
        Assert.True(await store.CommitAsync(txn).ConfigureAwait(true));

        var ex = Assert.Throws<InvalidOperationException>(() => store.AbortTransaction(txn));
        ex.Message.Should().Contain("Committed");
    }

    /// <summary>
    /// Aborting an already-aborted transaction is idempotent: the handle is already in the
    /// terminal <c>Aborted</c> state, so a repeat abort is a no-op rather than a throw. This
    /// keeps double-abort (e.g. a retry path) from raising on an already-correct outcome.
    /// </summary>
    [Fact]
    public async Task AbortTransaction_AlreadyAborted_IsIdempotentNoOp()
    {
        var store = new MvccStore<string, int>();
        var txn = store.BeginTransaction();
        await store.WriteAsync("key1", 42, txn).ConfigureAwait(true);
        store.AbortTransaction(txn);

        // A second abort of the same handle must not throw — the outcome is already correct.
        store.AbortTransaction(txn);
    }

    /// <summary>
    /// Concurrent readers do not block writers and vice versa. The earlier store
    /// awaited a single <see cref="SemaphoreSlim"/> on every read, so a concurrent writer
    /// would serialise N readers behind itself; the snapshot-isolation point of MVCC was
    /// defeated. Reads are now lock-free (ConcurrentDictionary + ImmutableList) and
    /// many parallel readers can proceed alongside an in-flight writer.
    /// </summary>
    [Fact]
    public async Task ReadAsync_LockFree_ManyReadersAndOneWriter_AllSucceed()
    {
        // Arrange — write an initial committed version visible to subsequent readers.
        var store = new MvccStore<string, int>();
        var seedTxn = store.BeginTransaction();
        await store.WriteAsync("k", 100, seedTxn).ConfigureAwait(true);
        Assert.True(await store.CommitAsync(seedTxn).ConfigureAwait(true));

        // Spawn one writer transaction that produces a long chain of versions for "k"
        // alongside many reader transactions whose snapshots all predate the writer's
        // commit. Every reader must observe the seeded value 100 — never the writer's
        // in-flight versions, never a torn read.
        var writerTxn = store.BeginTransaction();
        var readerTxns = Enumerable.Range(0, 16).Select(_ => store.BeginTransaction()).ToList();

        var writer = Task.Run(async () =>
        {
            for (var i = 0; i < 200; i++)
            {
                await store.WriteAsync("k", 200 + i, writerTxn).ConfigureAwait(false);
            }
        });

        var readers = readerTxns.Select(rt => Task.Run(async () =>
        {
            for (var i = 0; i < 200; i++)
            {
                var (value, found) = await store.ReadAsync("k", rt).ConfigureAwait(false);
                Assert.True(found);
                Assert.Equal(100, value);
            }
        })).ToList();

        // Act
        await Task.WhenAll(readers.Concat([writer])).ConfigureAwait(true);

        // After the writer commits its long chain, a fresh reader transaction sees the
        // latest value (one of the writer's 200 writes — the most recently appended).
        Assert.True(await store.CommitAsync(writerTxn).ConfigureAwait(true));
        var freshReader = store.BeginTransaction();
        var (latestValue, latestFound) = await store.ReadAsync("k", freshReader).ConfigureAwait(true);
        Assert.True(latestFound);
        Assert.InRange(latestValue, 200, 399);
    }

    /// <summary>
    /// After <see cref="MvccStore{TKey, TValue}.Dispose"/>, every public method throws
    /// <see cref="ObjectDisposedException"/>. The class implements <see cref="IDisposable"/>
    /// so callers using <c>using</c> blocks release the gate's kernel waitable handle
    /// deterministically; subsequent method calls fail loudly instead of mutating disposed
    /// state.
    /// </summary>
    [Fact]
    public async Task Dispose_IsIdempotent_AndPostDisposeMethodsThrow()
    {
        var store = new MvccStore<string, int>();
        var txn = store.BeginTransaction();
        await store.WriteAsync("k", 1, txn).ConfigureAwait(true);

        store.Dispose();
        store.Dispose(); // second dispose is a no-op (Interlocked-gated)

        Assert.Throws<ObjectDisposedException>(() => store.BeginTransaction());
        Assert.Throws<ObjectDisposedException>(() => store.AbortTransaction(txn));
        Assert.Throws<ObjectDisposedException>(() => _ = store.MinActiveTransactionId);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => store.ReadAsync("k", txn));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => store.WriteAsync("k", 2, txn));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => store.CommitAsync(txn));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => store.PruneBelowAsync(0));
    }

    [Fact]
    public async Task WriteAsync_ToCommittedTransaction_ThrowsInvalidOperationException()
    {
        var store = new MvccStore<string, int>();
        var txn = store.BeginTransaction();
        await store.CommitAsync(txn).ConfigureAwait(true);

        Task act() => store.WriteAsync("k", 1, txn);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(act).ConfigureAwait(true);
        ex.Message.Should().Contain("Committed");
    }

    [Fact]
    public async Task WriteAsync_ToAbortedTransaction_ThrowsInvalidOperationException()
    {
        var store = new MvccStore<string, int>();
        var txn = store.BeginTransaction();
        store.AbortTransaction(txn);

        Task act() => store.WriteAsync("k", 1, txn);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(act).ConfigureAwait(true);
        ex.Message.Should().Contain("Aborted");
    }

    [Fact]
    public async Task WriteAsync_UnknownTransactionId_ThrowsInvalidOperationException()
    {
        var store = new MvccStore<string, int>();

        Task act() => store.WriteAsync("k", 1, 9999L);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(act).ConfigureAwait(true);
        ex.Message.Should().Contain("not known");
    }

    /// <summary>
    /// An aborted transaction's writes logically never happened — including to the aborted
    /// transaction itself. Reading through an aborted handle must throw (mirroring the
    /// WriteAsync active-only guard) rather than serve the transaction its own dead writes.
    /// </summary>
    [Fact]
    public async Task ReadAsync_OnAbortedTransaction_Throws()
    {
        var store = new MvccStore<string, int>();
        var txn = store.BeginTransaction();
        await store.WriteAsync("key1", 42, txn).ConfigureAwait(true);
        store.AbortTransaction(txn);

        // The aborted handle must not see its own aborted write (nor anything else).
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.ReadAsync("key1", txn)).ConfigureAwait(true);
    }

    /// <summary>
    /// DDIA 2e Ch. 8 tombstone visibility, plain (pre-prune) regime: a row stays visible when
    /// "the transaction that requested deletion had not yet committed at the time when the
    /// reader's transaction started" — and disappears for readers begun after the delete's
    /// commit.
    /// </summary>
    [Fact]
    public async Task DeleteAsync_SnapshotPredatingDelete_StillSeesValue_NewSnapshotDoesNot()
    {
        var store = new MvccStore<string, int>();

        var writer = store.BeginTransaction();
        await store.WriteAsync("k", 1, writer);
        await store.CommitAsync(writer);

        // Reader R begins BEFORE the delete commits.
        var reader = store.BeginTransaction();

        var deleter = store.BeginTransaction();
        await store.DeleteAsync("k", deleter);
        await store.CommitAsync(deleter);

        var (value, found) = await store.ReadAsync("k", reader);
        found.Should().BeTrue("the delete committed after R's snapshot was taken");
        value.Should().Be(1);

        var late = store.BeginTransaction();
        var (_, foundLate) = await store.ReadAsync("k", late);
        foundLate.Should().BeFalse("a reader begun after the delete's commit sees the tombstone");
    }

    /// <summary>
    /// An ABORTED delete leaves the key visible to everyone — aborted writes (tombstones
    /// included) are invisible to all other transactions.
    /// </summary>
    [Fact]
    public async Task DeleteAsync_AbortedTombstone_KeyStillVisible()
    {
        var store = new MvccStore<string, int>();

        var writer = store.BeginTransaction();
        await store.WriteAsync("k", 1, writer);
        await store.CommitAsync(writer);

        var deleter = store.BeginTransaction();
        await store.DeleteAsync("k", deleter);
        store.AbortTransaction(deleter);

        var reader = store.BeginTransaction();
        var (value, found) = await store.ReadAsync("k", reader);
        found.Should().BeTrue("an aborted tombstone must not hide the committed value");
        value.Should().Be(1);
    }
}
