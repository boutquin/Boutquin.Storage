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
using Boutquin.Storage.Distributed.Interfaces.Transactions;
using IntKey = Boutquin.Storage.Domain.Helpers.SerializableWrapper<int>;
using IntValue = Boutquin.Storage.Domain.Helpers.SerializableWrapper<int>;

namespace Boutquin.Storage.Infrastructure.Tests;

// Two interleavings no single-call test reaches. (1) Dispose racing an operation that holds a type's
// serializing gate: the holder must finish normally and every caller already queued on the gate must
// observe ObjectDisposedException rather than wait forever. (2) A transaction with two operations in
// flight, one waiting on a lock and one granted or cancelled: the settled operation must not erase the
// still-live wait, or a real deadlock through that transaction is never detected.
// Shares a collection with the other gate-blocking suite: both park thread-pool threads inside a
// gate, and running them side by side can starve a holder past the 5 s bound.
[Collection("GateBlockingDisposeTests")]
public sealed class DisposeUnderInflightAndDeadlockWaitTests
{
    private static readonly TimeSpan s_bound = TimeSpan.FromSeconds(5);

    // ---- BulkKeyValueStoreWithBloomFilter ----

    public static TheoryData<string> BloomQueuedCalls() =>
        ["SetAsync", "TryGetValueAsync", "ContainsKeyAsync", "RemoveAsync", "ClearAsync", "GetAllItemsAsync", "SetBulkAsync", "CompactAsync"];

    [Theory]
    [MemberData(nameof(BloomQueuedCalls))]
    public async Task Bloom_DisposeWhileASetHoldsTheGate_QueuedCallThrowsObjectDisposed(string call)
    {
        var inner = new ParkingStore();
        var store = await BulkKeyValueStoreWithBloomFilter<IntKey, IntValue>.CreateAsync(inner, BloomFilter.ForSerializable<IntKey>(100, 0.01)).ConfigureAwait(true);
        var inflight = store.SetAsync(new IntKey(1), new IntValue(10));
        await inner.Entered.Task.WaitAsync(s_bound).ConfigureAwait(true);
        Task queued = call switch
        {
            "SetAsync" => store.SetAsync(new IntKey(2), new IntValue(20)),
            "TryGetValueAsync" => store.TryGetValueAsync(new IntKey(1)),
            "ContainsKeyAsync" => store.ContainsKeyAsync(new IntKey(1)),
            "RemoveAsync" => store.RemoveAsync(new IntKey(1)),
            "ClearAsync" => store.ClearAsync(),
            "GetAllItemsAsync" => store.GetAllItemsAsync(),
            "SetBulkAsync" => store.SetBulkAsync([new(new IntKey(3), new IntValue(30))]),
            _ => store.CompactAsync(),
        };
        await WaitUntilQueuedAsync(queued).ConfigureAwait(true);

        store.Dispose();
        inner.Release.TrySetResult();

        var outcome = await Record.ExceptionAsync(() => queued.WaitAsync(s_bound)).ConfigureAwait(true);
        outcome.Should().BeOfType<ObjectDisposedException>($"a {call} queued behind the holder must observe the disposal, not wait forever");
        await inflight.WaitAsync(s_bound).ConfigureAwait(true);
    }

    [Fact]
    public async Task Bloom_DisposeWhileASetHoldsTheGate_TheInFlightSetCompletes()
    {
        var inner = new ParkingStore();
        var store = await BulkKeyValueStoreWithBloomFilter<IntKey, IntValue>.CreateAsync(inner, BloomFilter.ForSerializable<IntKey>(100, 0.01)).ConfigureAwait(true);
        var inflight = store.SetAsync(new IntKey(1), new IntValue(10));
        await inner.Entered.Task.WaitAsync(s_bound).ConfigureAwait(true);

        store.Dispose();
        inner.Release.TrySetResult();

        var outcome = await Record.ExceptionAsync(() => inflight.WaitAsync(s_bound)).ConfigureAwait(true);
        outcome.Should().BeNull("a write the inner store already applied must not be reported as failed");
        (await inner.TryGetValueAsync(new IntKey(1)).ConfigureAwait(true)).Found.Should().BeTrue();
    }

    // ---- SecondaryIndex ----

    public static TheoryData<string> IndexQueuedCalls() => ["IndexAsync", "LookupAsync", "RemoveAsync", "ClearAsync"];

    [Theory]
    [MemberData(nameof(IndexQueuedCalls))]
    public async Task SecondaryIndex_DisposeWhileAnIndexHoldsTheGate_QueuedCallThrowsObjectDisposed(string call)
    {
        var hold = new Hold();
        var index = new SecondaryIndex<HoldKey, string, string>(value => value);
        var inflight = Task.Run(() => index.IndexAsync(new HoldKey(1, hold), "red"));
        hold.Entered.Wait(s_bound).Should().BeTrue("the index write is inside the gate");
        Task queued = call switch
        {
            "IndexAsync" => index.IndexAsync(new HoldKey(2, null), "blue"),
            "LookupAsync" => index.LookupAsync("red"),
            "RemoveAsync" => index.RemoveAsync(new HoldKey(1, null), "red"),
            _ => index.ClearAsync(),
        };
        await WaitUntilQueuedAsync(queued).ConfigureAwait(true);

        index.Dispose();
        hold.Release.Set();

        var outcome = await Record.ExceptionAsync(() => queued.WaitAsync(s_bound)).ConfigureAwait(true);
        outcome.Should().BeOfType<ObjectDisposedException>($"a {call} queued behind the holder must observe the disposal, not wait forever");
        await inflight.WaitAsync(s_bound).ConfigureAwait(true);
    }

    [Fact]
    public async Task SecondaryIndex_DisposeWhileAnIndexHoldsTheGate_TheInFlightIndexCompletes()
    {
        var hold = new Hold();
        var index = new SecondaryIndex<HoldKey, string, string>(value => value);
        var inflight = Task.Run(() => index.IndexAsync(new HoldKey(1, hold), "red"));
        hold.Entered.Wait(s_bound).Should().BeTrue("the index write is inside the gate");

        index.Dispose();
        hold.Release.Set();

        var outcome = await Record.ExceptionAsync(() => inflight.WaitAsync(s_bound)).ConfigureAwait(true);
        outcome.Should().BeNull("a posting already added to the index must not be reported as failed");
    }

    // ---- MvccStore ----

    public static TheoryData<string> MvccQueuedCalls() =>
        ["WriteAsync", "DeleteAsync", "CommitAsync", "BeginTransaction", "AbortTransaction", "PruneBelowAsync"];

    [Theory]
    [MemberData(nameof(MvccQueuedCalls))]
    public async Task Mvcc_DisposeWhileAWriteHoldsTheGate_QueuedCallThrowsObjectDisposed(string call)
    {
        var hold = new Hold();
        var store = new MvccStore<HoldKey, int>();
        var t1 = store.BeginTransaction();
        var t2 = store.BeginTransaction();
        var inflight = Task.Run(() => store.WriteAsync(new HoldKey(1, hold), 1, t1));
        hold.Entered.Wait(s_bound).Should().BeTrue("the write is inside the gate");
        Task queued = call switch
        {
            "WriteAsync" => store.WriteAsync(new HoldKey(2, null), 2, t2),
            "DeleteAsync" => store.DeleteAsync(new HoldKey(2, null), t2),
            "CommitAsync" => store.CommitAsync(t2),
            "BeginTransaction" => Task.Run(store.BeginTransaction),
            "AbortTransaction" => Task.Run(() => store.AbortTransaction(t2)),
            _ => store.PruneBelowAsync(0),
        };
        await WaitUntilQueuedAsync(queued).ConfigureAwait(true);

        store.Dispose();
        hold.Release.Set();

        var outcome = await Record.ExceptionAsync(() => queued.WaitAsync(s_bound)).ConfigureAwait(true);
        outcome.Should().BeOfType<ObjectDisposedException>($"a {call} queued behind the holder must observe the disposal, not wait forever");
        await inflight.WaitAsync(s_bound).ConfigureAwait(true);
    }

    [Fact]
    public async Task Mvcc_DisposeWhileAWriteHoldsTheGate_TheInFlightWriteCompletes()
    {
        var hold = new Hold();
        var store = new MvccStore<HoldKey, int>();
        var t1 = store.BeginTransaction();
        var inflight = Task.Run(() => store.WriteAsync(new HoldKey(1, hold), 1, t1));
        hold.Entered.Wait(s_bound).Should().BeTrue("the write is inside the gate");

        store.Dispose();
        hold.Release.Set();

        var outcome = await Record.ExceptionAsync(() => inflight.WaitAsync(s_bound)).ConfigureAwait(true);
        outcome.Should().BeNull("a version already installed in the chain must not be reported as failed");
    }

    // ---- Deadlock detection with two operations of one transaction in flight ----

    public static TheoryData<string> LockingStores() => ["Strict2PL", "ReadCommitted"];

    // T1 holds x, T2 holds y. T1's write to y waits on T2; while it waits, T1's write to z is granted.
    // T2's write to x then closes the cycle T2 -> T1 -> T2 and must be chosen as the victim.
    [Theory]
    [MemberData(nameof(LockingStores))]
    public async Task Deadlock_ConcurrentGrantedOperationOfAWaitingTransaction_StillDetectsTheCycle(string kind)
    {
        using var store = Open(kind);
        var t1 = store.BeginTransaction(); var t2 = store.BeginTransaction();
        await store.WriteAsync("x", 1, t1).ConfigureAwait(true);
        await store.WriteAsync("y", 2, t2).ConfigureAwait(true);
        var t1WaitsY = store.WriteAsync("y", 10, t1);
        await WaitUntilQueuedAsync(t1WaitsY).ConfigureAwait(true);
        await store.WriteAsync("z", 11, t1).WaitAsync(s_bound).ConfigureAwait(true);

        var t2WaitsX = store.WriteAsync("x", 20, t2);

        var outcome = await Record.ExceptionAsync(() => t2WaitsX.WaitAsync(s_bound)).ConfigureAwait(true);
        outcome.Should().BeOfType<TransactionDeadlockException>("T1's granted write to z must not erase its live wait on y");
        await t1WaitsY.WaitAsync(s_bound).ConfigureAwait(true);
    }

    // As above, but T1's second operation is a wait on w (held by T3) that its caller cancels.
    [Theory]
    [MemberData(nameof(LockingStores))]
    public async Task Deadlock_ConcurrentCancelledWaitOfAWaitingTransaction_StillDetectsTheCycle(string kind)
    {
        using var store = Open(kind);
        var t1 = store.BeginTransaction(); var t2 = store.BeginTransaction(); var t3 = store.BeginTransaction();
        await store.WriteAsync("x", 1, t1).ConfigureAwait(true);
        await store.WriteAsync("y", 2, t2).ConfigureAwait(true);
        await store.WriteAsync("w", 3, t3).ConfigureAwait(true);
        var t1WaitsY = store.WriteAsync("y", 10, t1);
        await WaitUntilQueuedAsync(t1WaitsY).ConfigureAwait(true);
        using var cancel = new CancellationTokenSource();
        var t1WaitsW = store.WriteAsync("w", 12, t1, cancel.Token);
        await WaitUntilQueuedAsync(t1WaitsW).ConfigureAwait(true);
        await cancel.CancelAsync().ConfigureAwait(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t1WaitsW.WaitAsync(s_bound)).ConfigureAwait(true);

        var t2WaitsX = store.WriteAsync("x", 20, t2);

        var outcome = await Record.ExceptionAsync(() => t2WaitsX.WaitAsync(s_bound)).ConfigureAwait(true);
        outcome.Should().BeOfType<TransactionDeadlockException>("cancelling T1's wait on w must not erase its live wait on y");
        await t1WaitsY.WaitAsync(s_bound).ConfigureAwait(true);
    }

    private static IIsolationComparisonStore<string, int> Open(string kind) =>
        kind == "Strict2PL" ? new StrictTwoPhaseLockingStore<string, int>() : new ReadCommittedStore<string, int>();

    // A queued call has passed its entry check and parked on the gate; give it a moment to get there.
    private static async Task WaitUntilQueuedAsync(Task queued)
    {
        await Task.Delay(50).ConfigureAwait(true);
        queued.IsCompleted.Should().BeFalse("the call is queued behind the holder");
    }

    // Blocks the first hash of the key inside the gate until the test releases it, so the holder's
    // critical section is held open deterministically rather than by a slow key.
    private sealed class Hold
    {
        private int _armed = 1;
        public ManualResetEventSlim Entered { get; } = new();
        public ManualResetEventSlim Release { get; } = new();
        public void Block()
        {
            if (Interlocked.Exchange(ref _armed, 0) == 1)
            {
                Entered.Set();
                Release.Wait(TimeSpan.FromSeconds(30));
            }
        }
    }

    private readonly record struct HoldKey(int Value, Hold? Gate) : IComparable<HoldKey>
    {
        public int CompareTo(HoldKey other) => Value.CompareTo(other.Value);
        public bool Equals(HoldKey other) => Value == other.Value;
        public override int GetHashCode()
        {
            Gate?.Block();
            return Value;
        }
    }

    // Inner store whose first SetAsync applies the write and then parks until released, so the
    // decorator's gate is held by a call whose effect has already landed.
    private sealed class ParkingStore : ICompactableBulkStorageEngine<IntKey, IntValue>
    {
        private readonly InMemoryKeyValueStore<IntKey, IntValue> _inner = new();
        private int _armed = 1;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IEntrySerializer<IntKey, IntValue> EntrySerializer { get; } = new BinaryEntrySerializer<IntKey, IntValue>();
        public async Task SetAsync(IntKey key, IntValue value, CancellationToken cancellationToken = default)
        {
            await _inner.SetAsync(key, value, cancellationToken).ConfigureAwait(true);
            if (Interlocked.Exchange(ref _armed, 0) == 1)
            {
                Entered.TrySetResult();
                await Release.Task.ConfigureAwait(true);
            }
        }
        public Task<(IntValue Value, bool Found)> TryGetValueAsync(IntKey key, CancellationToken cancellationToken = default) => _inner.TryGetValueAsync(key, cancellationToken);
        public Task<bool> ContainsKeyAsync(IntKey key, CancellationToken cancellationToken = default) => _inner.ContainsKeyAsync(key, cancellationToken);
        public Task RemoveAsync(IntKey key, CancellationToken cancellationToken = default) => _inner.RemoveAsync(key, cancellationToken);
        public Task ClearAsync(CancellationToken cancellationToken = default) => _inner.ClearAsync(cancellationToken);
        public Task<IEnumerable<(IntKey Key, IntValue Value)>> GetAllItemsAsync(CancellationToken cancellationToken = default) => _inner.GetAllItemsAsync(cancellationToken);
        public Task SetBulkAsync(IEnumerable<KeyValuePair<IntKey, IntValue>> items, CancellationToken cancellationToken = default) => _inner.SetBulkAsync(items, cancellationToken);
        public Task CompactAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
