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

public sealed class IsolationComparisonTests
{
    [Fact]
    public async Task ReadCommitted_HidesDirtyWritesButAllowsNonrepeatableReads()
    {
        using var store = new ReadCommittedStore<string, int>();
        var writer = store.BeginTransaction(); var reader = store.BeginTransaction();
        await store.WriteAsync("x", 1, writer).ConfigureAwait(true);
        (await store.ReadAsync("x", reader).ConfigureAwait(true)).Found.Should().BeFalse();
        await store.CommitAsync(writer).ConfigureAwait(true);
        (await store.ReadAsync("x", reader).ConfigureAwait(true)).Value.Should().Be(1);
        var next = store.BeginTransaction();
        await store.WriteAsync("x", 2, next).ConfigureAwait(true);
        await store.CommitAsync(next).ConfigureAwait(true);
        (await store.ReadAsync("x", reader).ConfigureAwait(true)).Value.Should().Be(2);
        await store.CommitAsync(reader).ConfigureAwait(true);
    }
    [Fact]
    public async Task StrictTwoPhaseLocking_HoldsReadAndWriteLocksThroughCompletion()
    {
        using var store = new StrictTwoPhaseLockingStore<string, int>();
        var reader = store.BeginTransaction(); var writer = store.BeginTransaction();
        (await store.ReadAsync("x", reader).ConfigureAwait(true)).Found.Should().BeFalse();
        var blockedWrite = store.WriteAsync("x", 1, writer);
        blockedWrite.IsCompleted.Should().BeFalse();
        await store.CommitAsync(reader).ConfigureAwait(true);
        await blockedWrite.ConfigureAwait(true);
        var other = store.BeginTransaction();
        var blockedRead = store.ReadAsync("x", other);
        blockedRead.IsCompleted.Should().BeFalse();
        await store.AbortAsync(writer).ConfigureAwait(true);
        (await blockedRead.ConfigureAwait(true)).Found.Should().BeFalse();
        await store.CommitAsync(other).ConfigureAwait(true);
    }
    [Fact]
    public async Task LockWaitCancellation_DoesNotLeakNewLocksOrReleaseEarlierLocks()
    {
        using var store = new StrictTwoPhaseLockingStore<string, int>();
        var first = store.BeginTransaction(); var second = store.BeginTransaction();
        await store.WriteAsync("x", 1, first).ConfigureAwait(true);
        await store.WriteAsync("y", 2, second).ConfigureAwait(true);
        using var cancellation = new CancellationTokenSource();
        var blocked = store.WriteAsync("x", 3, second, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked).ConfigureAwait(true);
        var third = store.BeginTransaction(); var blockedY = store.ReadAsync("y", third);
        blockedY.IsCompleted.Should().BeFalse("canceling one request preserves earlier transaction locks");
        await store.AbortAsync(second).ConfigureAwait(true);
        (await blockedY.ConfigureAwait(true)).Found.Should().BeFalse();
        await store.CommitAsync(first).ConfigureAwait(true);
        await store.WriteAsync("x", 4, third).ConfigureAwait(true);
        await store.CommitAsync(third).ConfigureAwait(true);
    }
    [Fact]
    public async Task ReadCommitted_WritersWaitAndAbortedDeleteNeverLeaks()
    {
        using var store = new ReadCommittedStore<string, int>();
        var first = store.BeginTransaction(); var second = store.BeginTransaction();
        await store.WriteAsync("key", 1, first).ConfigureAwait(true);
        var blocked = store.WriteAsync("key", 2, second);
        blocked.IsCompleted.Should().BeFalse();
        await store.CommitAsync(first).ConfigureAwait(true); await blocked.ConfigureAwait(true);
        var reader = store.BeginTransaction();
        (await store.ReadAsync("key", reader).ConfigureAwait(true)).Value.Should().Be(1);
        await store.DeleteAsync("key", second).ConfigureAwait(true);
        await store.AbortAsync(second).ConfigureAwait(true);
        (await store.ReadAsync("key", reader).ConfigureAwait(true)).Value.Should().Be(1);
        await store.DeleteAsync("key", reader).ConfigureAwait(true); await store.CommitAsync(reader).ConfigureAwait(true);
        var final = store.BeginTransaction(); (await store.ReadAsync("key", final).ConfigureAwait(true)).Found.Should().BeFalse();
    }

    [Fact]
    public async Task StrictLockUpgradeDeadlock_AbortsTheClosingRequester_AndDisposeWakesWaiters()
    {
        using var store = new StrictTwoPhaseLockingStore<string, int>();
        var first = store.BeginTransaction(); var second = store.BeginTransaction();
        await store.ReadAsync("x", first).ConfigureAwait(true); await store.ReadAsync("x", second).ConfigureAwait(true);
        var firstUpgrade = store.WriteAsync("x", 1, first);
        firstUpgrade.IsCompleted.Should().BeFalse("the upgrade waits for the other shared holder");
        var victim = await Assert.ThrowsAsync<TransactionDeadlockException>(() => store.WriteAsync("x", 2, second).WaitAsync(TimeSpan.FromSeconds(3))).ConfigureAwait(true);
        victim.TransactionId.Should().Be(second);
        await firstUpgrade.ConfigureAwait(true);
        await Assert.ThrowsAsync<TransactionDeadlockException>(() => store.ReadAsync("x", second)).ConfigureAwait(true);
        await store.AbortAsync(second).ConfigureAwait(true);
        var third = store.BeginTransaction(); var waiting = store.ReadAsync("x", third);
        store.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => waiting).ConfigureAwait(true);
    }

    // DDIA 2PL: the database detects a deadlock and aborts one transaction so the others can
    // make progress. The victim's pending acquire fails, its locks are released, and the
    // surviving transaction's wait is granted without any caller cancellation.
    [Fact]
    public async Task StrictTwoPhaseLocking_CrossLockDeadlock_AbortsOneTransactionSoTheOtherProceeds()
    {
        using var store = new StrictTwoPhaseLockingStore<string, int>();
        var first = store.BeginTransaction(); var second = store.BeginTransaction();
        await store.WriteAsync("x", 1, first).ConfigureAwait(true);
        await store.WriteAsync("y", 2, second).ConfigureAwait(true);
        var firstWaits = store.WriteAsync("y", 10, first);
        var secondWaits = store.WriteAsync("x", 20, second);

        await Task.WhenAny(Task.WhenAll(firstWaits.ContinueWith(_ => { }, TaskScheduler.Default), secondWaits.ContinueWith(_ => { }, TaskScheduler.Default)), Task.Delay(TimeSpan.FromSeconds(3))).ConfigureAwait(true);
        (firstWaits.IsCompleted && secondWaits.IsCompleted).Should().BeTrue("a detected deadlock aborts one waiter and grants the other");

        var victim = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => secondWaits).ConfigureAwait(true);
        victim.Message.Should().Contain("deadlock");
        await firstWaits.ConfigureAwait(true);
        await store.AbortAsync(second).ConfigureAwait(true);
        await store.CommitAsync(first).ConfigureAwait(true);
        var reader = store.BeginTransaction();
        (await store.ReadAsync("y", reader).ConfigureAwait(true)).Should().Be((10, true), "the victim's staged write to y never published");
        (await store.ReadAsync("x", reader).ConfigureAwait(true)).Should().Be((1, true));
    }

    [Fact]
    public async Task ReadCommitted_WriteLockDeadlock_AbortsOneTransactionSoTheOtherProceeds()
    {
        using var store = new ReadCommittedStore<string, int>();
        var first = store.BeginTransaction(); var second = store.BeginTransaction();
        await store.WriteAsync("x", 1, first).ConfigureAwait(true);
        await store.WriteAsync("y", 2, second).ConfigureAwait(true);
        var firstWaits = store.WriteAsync("y", 10, first);
        var secondWaits = store.WriteAsync("x", 20, second);

        await Task.WhenAny(Task.WhenAll(firstWaits.ContinueWith(_ => { }, TaskScheduler.Default), secondWaits.ContinueWith(_ => { }, TaskScheduler.Default)), Task.Delay(TimeSpan.FromSeconds(3))).ConfigureAwait(true);
        (firstWaits.IsCompleted && secondWaits.IsCompleted).Should().BeTrue("a detected deadlock aborts one waiter and grants the other");

        (await Assert.ThrowsAnyAsync<InvalidOperationException>(() => secondWaits).ConfigureAwait(true)).Message.Should().Contain("deadlock");
        await firstWaits.ConfigureAwait(true);
        await store.CommitAsync(first).ConfigureAwait(true);
        var reader = store.BeginTransaction();
        (await store.ReadAsync("x", reader).ConfigureAwait(true)).Should().Be((1, true));
        (await store.ReadAsync("y", reader).ConfigureAwait(true)).Should().Be((10, true));
    }
}
