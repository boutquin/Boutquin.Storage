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

namespace Boutquin.Storage.Infrastructure.Tests;

// Two progress guarantees that only show up when an operation interleaves with another one:
// a lock-free MVCC read through a committed handle racing the prune that retires that handle, and
// two-phase-commit decision delivery when one participant never answers.
public sealed class TransactionRecoveryAndPruneRaceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "txn-race-" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _opened = [];
    private string FilePath(string name) => Path.Combine(_directory, name + ".log");

    // ---- MVCC: committed handle at or below the watermark racing PruneBelowAsync ----

    // The prune retires the committed handle (its snapshot is removed) and drops the version only
    // that handle could see. A read through the handle may resolve before the prune (the old value)
    // or after it (the documented unknown-handle exception) — never "absent", which no snapshot of
    // this history contains. This read runs inside the prune, after it rewrote one key's chain.
    [Fact]
    public async Task CommittedHandleRead_IssuedWhilePruneRewritesChains_ReturnsOldValueOrThrows_NeverAbsent()
    {
        var hook = new HashHook();
        using var store = new MvccStore<HookKey, string>();
        var handle = await CommittedHandleOverTwoVersionsAsync(store, hook).ConfigureAwait(true);
        int? firstKey = null;
        Task<(string Value, bool Found)>? observed = null;
        Exception? thrown = null;
        hook.OnHash = id =>
        {
            // The prune hashes one key, rewrites its chain, then hashes the next key: that is the
            // moment to read the first key through the handle.
            if (firstKey is null) { firstKey = id; return; }
            if (id == firstKey) { return; }
            hook.OnHash = null;
            try { observed = store.ReadAsync(new HookKey(firstKey.Value, hook), handle); }
            catch (InvalidOperationException ex) { thrown = ex; }
        };

        await store.PruneBelowAsync(store.MinActiveTransactionId - 1).ConfigureAwait(true);

        (observed is not null || thrown is not null).Should().BeTrue("the read must have run inside the prune");
        var outcome = thrown is not null ? "threw" : Describe(await observed!.ConfigureAwait(true));
        outcome.Should().BeOneOf(["v1", "threw"], "a committed handle reads its snapshot's value or is reported as pruned");
    }

    // The read captured the handle's snapshot before the prune and fetched the chain after it: the
    // whole prune ran in between. The read must notice the handle was retired instead of walking a
    // chain that no longer holds the handle's version.
    [Fact]
    public async Task CommittedHandleRead_PruneCompletesBetweenSnapshotAndChainFetch_ThrowsNeverAbsent()
    {
        var hook = new HashHook();
        using var store = new MvccStore<HookKey, string>();
        var handle = await CommittedHandleOverTwoVersionsAsync(store, hook).ConfigureAwait(true);
        Task<int>? prune = null;
        var pruneRanInsideRead = false;
        hook.OnHash = _ =>
        {
            // ReadAsync hashes the key only for the chain lookup, after it captured the snapshot.
            hook.OnHash = null;
            prune = store.PruneBelowAsync(store.MinActiveTransactionId - 1);
            pruneRanInsideRead = prune.IsCompleted;
        };

        string outcome;
        try { outcome = Describe(await store.ReadAsync(new HookKey(0, hook), handle).ConfigureAwait(true)); }
        catch (InvalidOperationException) { outcome = "threw"; }

        pruneRanInsideRead.Should().BeTrue("the uncontended prune completes synchronously inside the read");
        await prune!.ConfigureAwait(true);
        outcome.Should().BeOneOf(["v1", "threw"], "a committed handle reads its snapshot's value or is reported as pruned");
    }

    // t1 writes v1, a committed handle sees exactly t1, t3 overwrites with v3: the backdrop prune
    // keeps v3 and drops v1, the only version the handle can see.
    private static async Task<long> CommittedHandleOverTwoVersionsAsync(MvccStore<HookKey, string> store, HashHook hook)
    {
        var t1 = store.BeginTransaction();
        await store.WriteAsync(new HookKey(0, hook), "v1", t1).ConfigureAwait(true);
        await store.WriteAsync(new HookKey(1, hook), "v1", t1).ConfigureAwait(true);
        await store.CommitAsync(t1).ConfigureAwait(true);
        var handle = store.BeginTransaction();
        await store.CommitAsync(handle).ConfigureAwait(true);
        var t3 = store.BeginTransaction();
        await store.WriteAsync(new HookKey(0, hook), "v3", t3).ConfigureAwait(true);
        await store.WriteAsync(new HookKey(1, hook), "v3", t3).ConfigureAwait(true);
        await store.CommitAsync(t3).ConfigureAwait(true);
        (await store.ReadAsync(new HookKey(0, hook), handle).ConfigureAwait(true)).Should().Be(("v1", true));
        return handle;
    }

    private static string Describe((string Value, bool Found) read) => read.Found ? read.Value : "absent";

    // ---- 2PC: one participant that never answers ----

    // Recovery attempts every transaction: t1's participant h never answers its decision, which
    // must not keep t2's durable abort from reaching b and releasing b's prepared lock.
    [Fact]
    public async Task Recovery_ParticipantThatNeverAnswers_DoesNotStrandLaterTransactionsLocks()
    {
        var a = await OpenAsync("a"); var b = await OpenAsync("b"); var h = new NeverAnswersDecision(await OpenAsync("h"));
        var observedA = new ObserveDecision(a); var observedB = new ObserveDecision(b);
        using var prepared = new CancellationTokenSource();
        using var coordinator = await TwoPhaseCommitCoordinator.CreateAsync(FilePath("coordinator"), [observedA, new CancelAfterPrepare(observedB, prepared), h]).ConfigureAwait(true);
        var t1 = new TransactionIntent[] { new("a", Writes("x", "a")), new("h", Writes("x", "h")) };
        using (var cancellation = new CancellationTokenSource())
        {
            var execution = coordinator.ExecuteAsync(new RequestId("t1"), t1, null, cancellation.Token);
            await CancelAfterProgressAsync(execution, Task.WhenAll(observedA.Committed.Task, h.DecisionEntered.Task), cancellation).ConfigureAwait(true);
        }
        var t2 = new TransactionIntent[] { new("b", Writes("y", "b")) };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.ExecuteAsync(new RequestId("t2"), t2, null, prepared.Token)).ConfigureAwait(true);
        (await b.PrepareAsync(new RequestId("probe-before"), Writes("y", "z")).ConfigureAwait(true)).Should().Be(PrepareResult.Rejected, "t2 is prepared at b and holds y");

        using (var cancellation = new CancellationTokenSource())
        {
            var recovery = coordinator.RecoverAsync(cancellation.Token);
            await CancelAfterProgressAsync(recovery, observedB.Aborted.Task, cancellation).ConfigureAwait(true);
        }

        (await b.PrepareAsync(new RequestId("probe-after"), Writes("y", "z")).ConfigureAwait(true)).Should().Be(PrepareResult.Prepared,
            "recovery delivered t2's durable abort to b although t1's participant h never answered");
        (await b.PrepareAsync(new RequestId("t2"), t2[0].Writes).ConfigureAwait(true)).Should().Be(PrepareResult.Rejected, "b recorded the abort for t2");
        (await a.ReadAsync("x").ConfigureAwait(true)).Should().Be("a", "a received t1's durable commit");
    }

    // Once the decision is logged every participant is sent it: h, first in plan order, never
    // answers, and z must still receive the commit before the caller gives up waiting for h.
    [Fact]
    public async Task Execute_ParticipantThatNeverAnswers_DoesNotHoldBackTheDecisionForItsSiblings()
    {
        var h = new NeverAnswersDecision(await OpenAsync("h")); var z = await OpenAsync("z");
        var observedZ = new ObserveDecision(z);
        using var coordinator = await TwoPhaseCommitCoordinator.CreateAsync(FilePath("coordinator"), [h, observedZ]).ConfigureAwait(true);
        var plan = new TransactionIntent[] { new("h", Writes("x", "h")), new("z", Writes("x", "z")) };

        using (var cancellation = new CancellationTokenSource())
        {
            var execution = coordinator.ExecuteAsync(new RequestId("t1"), plan, null, cancellation.Token);
            await CancelAfterProgressAsync(execution, observedZ.Committed.Task, cancellation).ConfigureAwait(true);
        }

        (await z.ReadAsync("x").ConfigureAwait(true)).Should().Be("z", "the logged commit reached z while h never answered");
        (await z.PrepareAsync(new RequestId("later"), Writes("x", "q")).ConfigureAwait(true)).Should().Be(PrepareResult.Prepared, "z's prepared lock was released");
    }

    // Cancellation follows observed durable progress, not a 200–500 ms disk-speed assumption.
    // The outer bound still fails a coordinator that serializes delivery behind the hung peer.
    private static async Task CancelAfterProgressAsync(Task operation, Task progress, CancellationTokenSource cancellation)
    {
        try { await progress.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(true); }
        finally
        {
            await cancellation.CancelAsync().ConfigureAwait(true);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation).ConfigureAwait(true);
        }
    }

    private sealed class ObserveDecision(ITransactionalResource inner) : ITransactionalResource
    {
        public TaskCompletionSource Committed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Aborted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string ResourceId => inner.ResourceId;
        public Task<PrepareResult> PrepareAsync(RequestId id, IReadOnlyDictionary<string, string?> writes, CancellationToken ct = default) => inner.PrepareAsync(id, writes, ct);
        public async Task<CommitResult> CommitAsync(RequestId id, CancellationToken ct = default)
        {
            var result = await inner.CommitAsync(id, ct).ConfigureAwait(true);
            Committed.TrySetResult();
            return result;
        }
        public async Task<AbortResult> AbortAsync(RequestId id, CancellationToken ct = default)
        {
            var result = await inner.AbortAsync(id, ct).ConfigureAwait(true);
            Aborted.TrySetResult();
            return result;
        }
    }

    private static Dictionary<string, string?> Writes(string key, string value) => new() { [key] = value };

    private async Task<DurableTransactionalResource> OpenAsync(string id)
    {
        var resource = await DurableTransactionalResource.CreateAsync(id, FilePath(id)).ConfigureAwait(true);
        _opened.Add(resource);
        return resource;
    }

    // Votes through the wrapped participant but never answers a commit or abort request: the shape of
    // a node that became unreachable after voting. Only the caller's cancellation ends the wait.
    private sealed class NeverAnswersDecision(ITransactionalResource inner) : ITransactionalResource
    {
        public TaskCompletionSource DecisionEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string ResourceId => inner.ResourceId;
        public Task<PrepareResult> PrepareAsync(RequestId id, IReadOnlyDictionary<string, string?> writes, CancellationToken ct = default) => inner.PrepareAsync(id, writes, ct);
        public async Task<CommitResult> CommitAsync(RequestId id, CancellationToken ct = default)
        {
            DecisionEntered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(true);
            return CommitResult.Committed;
        }
        public async Task<AbortResult> AbortAsync(RequestId id, CancellationToken ct = default)
        {
            DecisionEntered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(true);
            return AbortResult.Aborted;
        }
    }

    // Prepares through the wrapped participant, then cancels the caller's token: the start stays undecided.
    private sealed class CancelAfterPrepare(ITransactionalResource inner, CancellationTokenSource cancellation) : ITransactionalResource
    {
        public string ResourceId => inner.ResourceId;
        public async Task<PrepareResult> PrepareAsync(RequestId id, IReadOnlyDictionary<string, string?> writes, CancellationToken ct = default)
        {
            var vote = await inner.PrepareAsync(id, writes, ct).ConfigureAwait(true);
            await cancellation.CancelAsync().ConfigureAwait(true);
            ct.ThrowIfCancellationRequested();
            return vote;
        }
        public Task<CommitResult> CommitAsync(RequestId id, CancellationToken ct = default) => inner.CommitAsync(id, ct);
        public Task<AbortResult> AbortAsync(RequestId id, CancellationToken ct = default) => inner.AbortAsync(id, ct);
    }

    // Lets a test run code at the exact point the store hashes a key: the lock-free read hashes it for
    // its chain lookup (after capturing the snapshot), the prune hashes each key as it rewrites it.
    private sealed class HashHook
    {
        public Action<int>? OnHash { get; set; }
    }

    private sealed class HookKey(int id, HashHook hook) : IComparable<HookKey>, IEquatable<HookKey>
    {
        public int Id { get; } = id;
        public int CompareTo(HookKey? other) => other is null ? 1 : Id.CompareTo(other.Id);
        public bool Equals(HookKey? other) => other is not null && Id == other.Id;
        public override bool Equals(object? obj) => Equals(obj as HookKey);
        public override int GetHashCode()
        {
            hook.OnHash?.Invoke(Id);
            return Id;
        }
    }

    public void Dispose()
    {
        foreach (var resource in _opened) { resource.Dispose(); }
        if (Directory.Exists(_directory)) { Directory.Delete(_directory, true); }
    }
}
