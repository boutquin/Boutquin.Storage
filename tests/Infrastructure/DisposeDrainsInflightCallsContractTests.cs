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
using System.Reflection;
using Boutquin.Storage.Distributed.Interfaces.Transactions;
using Boutquin.Storage.Infrastructure.ValueLog;
using IntKey = Boutquin.Storage.Domain.Helpers.SerializableWrapper<int>;
using StrValue = Boutquin.Storage.Domain.Helpers.SerializableWrapper<string>;

namespace Boutquin.Storage.Infrastructure.Tests;

// The dispose-drain contract for every type that serializes its callers through a gate and owns a
// resource the gate protects (a journal, a manifest stream, an inner store, the LSM engine's
// segment set). Dispose runs while one call holds the gate and others are queued on it:
//   * the holder finishes normally — its effect already landed, so it must not be reported failed;
//   * every queued caller ends in ObjectDisposedException within a bound — never waits forever;
//   * Dispose itself returns once the holder is done.
// Dispose waits for the holder here, so every test runs it off the test thread and only then
// releases the holder.
[Collection("GateBlockingDisposeTests")]
public sealed class DisposeDrainsInflightCallsContractTests : IDisposable
{
    private static readonly TimeSpan s_bound = TimeSpan.FromSeconds(5);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dispose-drain-" + Guid.NewGuid().ToString("N"));

    public DisposeDrainsInflightCallsContractTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort temp cleanup.
        }
    }

    // ---- ValueLogManifest ----

    public static TheoryData<string> ManifestQueuedCalls() =>
        ["RecordAddAsync", "RecordDeleteAsync", "RecordSealAsync", "GetLiveFilesAsync", "GetDeletedFilesAsync", "GetSealsAsync"];

    [Theory]
    [MemberData(nameof(ManifestQueuedCalls))]
    public async Task ValueLogManifest_DisposeWhileACallHoldsTheLock_QueuedCallThrowsObjectDisposed(string call)
    {
        var manifest = new ValueLogManifest(Path.Combine(_dir, "vlog.manifest"));
        await manifest.RecordAddAsync(1).ConfigureAwait(true);
        var gate = ManifestLock(manifest);
        await gate.WaitAsync().ConfigureAwait(true); // the in-flight call's critical section
        Task queued = call switch
        {
            "RecordAddAsync" => manifest.RecordAddAsync(2),
            "RecordDeleteAsync" => manifest.RecordDeleteAsync(1),
            "RecordSealAsync" => manifest.RecordSealAsync(1, new ValueLogManifest.SegmentSeal(0, 0, 0)),
            "GetLiveFilesAsync" => manifest.GetLiveFilesAsync(),
            "GetDeletedFilesAsync" => manifest.GetDeletedFilesAsync(),
            _ => manifest.GetSealsAsync(),
        };
        await WaitUntilQueuedAsync(queued).ConfigureAwait(true);

        var dispose = Task.Run(() => manifest.DisposeAsync().AsTask());
        await WaitUntilDisposalStartedAsync(manifest).ConfigureAwait(true);
        try
        {
            gate.Release();
        }
        catch (ObjectDisposedException)
        {
            // The holder's own fault is pinned by the sibling test; this one is about the queued call.
        }

        var outcome = await Record.ExceptionAsync(() => queued.WaitAsync(s_bound)).ConfigureAwait(true);
        outcome.Should().BeOfType<ObjectDisposedException>($"a {call} queued behind the holder must observe the disposal, not wait forever");
        await dispose.WaitAsync(s_bound).ConfigureAwait(true);
    }

    [Fact]
    public async Task ValueLogManifest_DisposeWhileACallHoldsTheLock_TheHolderReleasesNormally()
    {
        var manifest = new ValueLogManifest(Path.Combine(_dir, "vlog.manifest"));
        await manifest.RecordAddAsync(1).ConfigureAwait(true);
        var gate = ManifestLock(manifest);
        await gate.WaitAsync().ConfigureAwait(true);

        var dispose = Task.Run(() => manifest.DisposeAsync().AsTask());
        await WaitUntilDisposalStartedAsync(manifest).ConfigureAwait(true);

        // The holder's finally block: an add whose record is already fsync'd must not then fault.
        var release = Record.Exception(() => gate.Release());
        release.Should().BeNull("Dispose must not free the lock while a call still holds it");
        await dispose.WaitAsync(s_bound).ConfigureAwait(true);
    }

    private static SemaphoreSlim ManifestLock(ValueLogManifest manifest) =>
        (SemaphoreSlim)typeof(ValueLogManifest).GetField("_lock", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(manifest)!;

    // ---- SnapshotIsolationStore ----

    public static TheoryData<string> SnapshotIsolationQueuedCalls() =>
        ["BeginTransaction", "ReadAsync", "WriteAsync", "CommitAsync", "AbortTransaction", "MinActiveCommitTimestamp", "PruneCommittedBelowAsync"];

    [Theory]
    [MemberData(nameof(SnapshotIsolationQueuedCalls))]
    public async Task SnapshotIsolation_DisposeWhileAWriteHoldsTheGate_QueuedCallThrowsObjectDisposed(string call)
    {
        var hold = new Hold();
        var store = new SnapshotIsolationStore<HoldKey, int>();
        var t1 = store.BeginTransaction();
        var t2 = store.BeginTransaction();
        var inflight = Task.Run(() => store.WriteAsync(new HoldKey(1, hold), 1, t1));
        hold.Entered.Wait(s_bound).Should().BeTrue("the write is inside the gate");
        Task queued = call switch
        {
            "BeginTransaction" => Task.Run(store.BeginTransaction),
            "ReadAsync" => store.ReadAsync(new HoldKey(2, null), t2),
            "WriteAsync" => store.WriteAsync(new HoldKey(2, null), 2, t2),
            "CommitAsync" => store.CommitAsync(t2),
            "AbortTransaction" => Task.Run(() => store.AbortTransaction(t2)),
            "MinActiveCommitTimestamp" => Task.Run(() => store.MinActiveCommitTimestamp),
            _ => store.PruneCommittedBelowAsync(0),
        };
        await WaitUntilQueuedAsync(queued).ConfigureAwait(true);

        var dispose = Task.Run(store.Dispose);
        await WaitUntilDisposalStartedAsync(store).ConfigureAwait(true);
        hold.Release.Set();

        var outcome = await Record.ExceptionAsync(() => queued.WaitAsync(s_bound)).ConfigureAwait(true);
        outcome.Should().BeOfType<ObjectDisposedException>($"a {call} queued behind the holder must observe the disposal, not wait forever");
        await inflight.WaitAsync(s_bound).ConfigureAwait(true);
        await dispose.WaitAsync(s_bound).ConfigureAwait(true);
    }

    [Fact]
    public async Task SnapshotIsolation_DisposeWhileAWriteHoldsTheGate_TheInFlightWriteCompletes()
    {
        var hold = new Hold();
        var store = new SnapshotIsolationStore<HoldKey, int>();
        var t1 = store.BeginTransaction();
        var inflight = Task.Run(() => store.WriteAsync(new HoldKey(1, hold), 1, t1));
        hold.Entered.Wait(s_bound).Should().BeTrue("the write is inside the gate");

        var dispose = Task.Run(store.Dispose);
        await WaitUntilDisposalStartedAsync(store).ConfigureAwait(true);
        hold.Release.Set();

        var outcome = await Record.ExceptionAsync(() => inflight.WaitAsync(s_bound)).ConfigureAwait(true);
        outcome.Should().BeNull("a version already installed in the chain must not be reported as failed");
        store.InnerMvccForTesting.GetVersionChainLengthForTesting(new HoldKey(1, null)).Should().Be(1);
        await dispose.WaitAsync(s_bound).ConfigureAwait(true);
    }

    // ---- TwoPhaseCommitCoordinator ----

    public static TheoryData<string> CoordinatorQueuedCalls() => ["ExecuteAsync", "RecoverAsync"];

    [Theory]
    [MemberData(nameof(CoordinatorQueuedCalls))]
    public async Task Coordinator_DisposeWhileAnExecuteHoldsTheGate_QueuedCallThrowsObjectDisposed(string call)
    {
        using var participant = await DurableTransactionalResource.CreateAsync("a", Path.Combine(_dir, "a.log")).ConfigureAwait(true);
        var parked = new ParkingPrepare(participant);
        var coordinator = await TwoPhaseCommitCoordinator.CreateAsync(Path.Combine(_dir, "c.log"), [parked]).ConfigureAwait(true);
        var inflight = coordinator.ExecuteAsync(new RequestId("t1"), [new("a", new Dictionary<string, string?> { ["x"] = "1" })]);
        await parked.Entered.Task.WaitAsync(s_bound).ConfigureAwait(true);
        var queued = call == "ExecuteAsync"
            ? coordinator.ExecuteAsync(new RequestId("t2"), [new("a", new Dictionary<string, string?> { ["y"] = "2" })])
            : coordinator.RecoverAsync();
        await WaitUntilQueuedAsync(queued).ConfigureAwait(true);

        var dispose = Task.Run(coordinator.Dispose);
        await WaitUntilDisposalStartedAsync(coordinator).ConfigureAwait(true);
        parked.Release.TrySetResult();

        var outcome = await Record.ExceptionAsync(() => queued.WaitAsync(s_bound)).ConfigureAwait(true);
        outcome.Should().BeOfType<ObjectDisposedException>($"a {call} queued behind the holder must observe the disposal, not wait forever");
        await inflight.WaitAsync(s_bound).ConfigureAwait(true);
        await dispose.WaitAsync(s_bound).ConfigureAwait(true);
    }

    [Fact]
    public async Task Coordinator_DisposeWhileAnExecuteHoldsTheGate_TheInFlightExecuteCompletes()
    {
        using var participant = await DurableTransactionalResource.CreateAsync("a", Path.Combine(_dir, "a.log")).ConfigureAwait(true);
        var parked = new ParkingPrepare(participant);
        var coordinator = await TwoPhaseCommitCoordinator.CreateAsync(Path.Combine(_dir, "c.log"), [parked]).ConfigureAwait(true);
        var inflight = coordinator.ExecuteAsync(new RequestId("t1"), [new("a", new Dictionary<string, string?> { ["x"] = "1" })]);
        await parked.Entered.Task.WaitAsync(s_bound).ConfigureAwait(true);

        var dispose = Task.Run(coordinator.Dispose);
        await WaitUntilDisposalStartedAsync(coordinator).ConfigureAwait(true);
        parked.Release.TrySetResult();

        var outcome = await Record.ExceptionAsync(() => inflight.WaitAsync(s_bound)).ConfigureAwait(true);
        outcome.Should().BeNull("the transaction the coordinator was driving must reach its decision");
        (await inflight.ConfigureAwait(true)).Should().Be(CommitResult.Committed);
        (await participant.ReadAsync("x").ConfigureAwait(true)).Should().Be("1", "the commit decision reached the participant");
        await dispose.WaitAsync(s_bound).ConfigureAwait(true);
    }

    // ---- DurableTransactionalResource ----

    public static TheoryData<string> ResourceQueuedCalls() => ["PrepareAsync", "CommitAsync", "AbortAsync", "ReadAsync"];

    [Theory]
    [MemberData(nameof(ResourceQueuedCalls))]
    public async Task Resource_DisposeWhileAPrepareHoldsTheGate_QueuedCallThrowsObjectDisposed(string call)
    {
        var resource = await DurableTransactionalResource.CreateAsync("a", Path.Combine(_dir, "r.log")).ConfigureAwait(true);
        var hold = new Hold();
        resource.FaultInjector = (stage, persisted) => { if (stage == "Prepare" && persisted) { hold.Block(); } };
        var inflight = Task.Run(() => resource.PrepareAsync(new RequestId("t1"), new Dictionary<string, string?> { ["x"] = "1" }));
        hold.Entered.Wait(s_bound).Should().BeTrue("the prepare record is durable and the call still holds the gate");
        Task queued = call switch
        {
            "PrepareAsync" => resource.PrepareAsync(new RequestId("t2"), new Dictionary<string, string?> { ["y"] = "2" }),
            "CommitAsync" => resource.CommitAsync(new RequestId("t1")),
            "AbortAsync" => resource.AbortAsync(new RequestId("t1")),
            _ => resource.ReadAsync("x"),
        };
        await WaitUntilQueuedAsync(queued).ConfigureAwait(true);

        var dispose = Task.Run(resource.Dispose);
        await WaitUntilDisposalStartedAsync(resource).ConfigureAwait(true);
        hold.Release.Set();

        var outcome = await Record.ExceptionAsync(() => queued.WaitAsync(s_bound)).ConfigureAwait(true);
        outcome.Should().BeOfType<ObjectDisposedException>($"a {call} queued behind the holder must observe the disposal, not wait forever");
        await inflight.WaitAsync(s_bound).ConfigureAwait(true);
        await dispose.WaitAsync(s_bound).ConfigureAwait(true);
    }

    [Fact]
    public async Task Resource_DisposeWhileAPrepareHoldsTheGate_TheDurableVoteIsReported()
    {
        var path = Path.Combine(_dir, "r.log");
        var resource = await DurableTransactionalResource.CreateAsync("a", path).ConfigureAwait(true);
        var hold = new Hold();
        resource.FaultInjector = (stage, persisted) => { if (stage == "Prepare" && persisted) { hold.Block(); } };
        var inflight = Task.Run(() => resource.PrepareAsync(new RequestId("t1"), new Dictionary<string, string?> { ["x"] = "1" }));
        hold.Entered.Wait(s_bound).Should().BeTrue("the prepare record is durable and the call still holds the gate");

        var dispose = Task.Run(resource.Dispose);
        await WaitUntilDisposalStartedAsync(resource).ConfigureAwait(true);
        hold.Release.Set();

        var outcome = await Record.ExceptionAsync(() => inflight.WaitAsync(s_bound)).ConfigureAwait(true);
        outcome.Should().BeNull("a durable yes vote must reach the coordinator as a vote, not as a disposal fault");
        (await inflight.ConfigureAwait(true)).Should().Be(PrepareResult.Prepared);
        await dispose.WaitAsync(s_bound).ConfigureAwait(true);

        using var reopened = await DurableTransactionalResource.CreateAsync("a", path).ConfigureAwait(true);
        (await reopened.PrepareAsync(new RequestId("t1"), new Dictionary<string, string?> { ["x"] = "1" }).ConfigureAwait(true))
            .Should().Be(PrepareResult.Prepared, "the reported vote is the one the journal holds");
    }

    // ---- TransactionalMessageStore ----

    public static TheoryData<string> MessageStoreQueuedCalls() =>
        ["CommitAsync", "ProcessAsync", "ExecuteAsync", "PendingMessagesAsync", "AcknowledgeAsync", "ReadAsync"];

    [Theory]
    [MemberData(nameof(MessageStoreQueuedCalls))]
    public async Task MessageStore_DisposeWhileAHandlerHoldsTheGate_QueuedCallThrowsObjectDisposed(string call)
    {
        var store = await TransactionalMessageStore.CreateAsync(Path.Combine(_dir, "m.log")).ConfigureAwait(true);
        var hold = new Hold();
        var inflight = Task.Run(() => store.ExecuteAsync(new RequestId("r1"), "v1", _ => { hold.Block(); return Mutation("k", "v", "ok"); }));
        hold.Entered.Wait(s_bound).Should().BeTrue("the handler runs under the gate");
        Task queued = call switch
        {
            "CommitAsync" => store.CommitAsync(new RequestId("r2"), "v1", Mutation("k2", "v2", "ok2")),
            "ProcessAsync" => store.ProcessAsync(new RequestId("r3"), "v1", _ => Mutation("k3", "v3", "ok3")),
            "ExecuteAsync" => store.ExecuteAsync(new RequestId("r4"), "v1", _ => Mutation("k4", "v4", "ok4")),
            "PendingMessagesAsync" => store.PendingMessagesAsync(),
            "AcknowledgeAsync" => store.AcknowledgeAsync(new RequestId("m1")),
            _ => store.ReadAsync("k"),
        };
        await WaitUntilQueuedAsync(queued).ConfigureAwait(true);

        var dispose = Task.Run(store.Dispose);
        await WaitUntilDisposalStartedAsync(store).ConfigureAwait(true);
        hold.Release.Set();

        var outcome = await Record.ExceptionAsync(() => queued.WaitAsync(s_bound)).ConfigureAwait(true);
        outcome.Should().BeOfType<ObjectDisposedException>($"a {call} queued behind the holder must observe the disposal, not wait forever");
        await inflight.WaitAsync(s_bound).ConfigureAwait(true);
        await dispose.WaitAsync(s_bound).ConfigureAwait(true);
    }

    [Fact]
    public async Task MessageStore_DisposeWhileAHandlerHoldsTheGate_TheInFlightMutationCompletes()
    {
        var path = Path.Combine(_dir, "m.log");
        var store = await TransactionalMessageStore.CreateAsync(path).ConfigureAwait(true);
        var hold = new Hold();
        var inflight = Task.Run(() => store.ExecuteAsync(new RequestId("r1"), "v1", _ => { hold.Block(); return Mutation("k", "v", "ok"); }));
        hold.Entered.Wait(s_bound).Should().BeTrue("the handler runs under the gate");

        var dispose = Task.Run(store.Dispose);
        await WaitUntilDisposalStartedAsync(store).ConfigureAwait(true);
        hold.Release.Set();

        var outcome = await Record.ExceptionAsync(() => inflight.WaitAsync(s_bound)).ConfigureAwait(true);
        outcome.Should().BeNull("a mutation whose record is durable must not be reported as failed");
        (await inflight.ConfigureAwait(true)).Should().Be("ok");
        await dispose.WaitAsync(s_bound).ConfigureAwait(true);

        using var reopened = await TransactionalMessageStore.CreateAsync(path).ConfigureAwait(true);
        (await reopened.ReadAsync("k").ConfigureAwait(true)).Should().Be("v");
    }

    private static async Task WaitUntilDisposalStartedAsync(object target)
    {
        // Scheduling Dispose is not evidence it ran. Observe its entry flag before releasing
        // the holder, otherwise a slow worker can let the queued operation win legitimately.
        var field = target.GetType().GetField("_disposed", BindingFlags.NonPublic | BindingFlags.Instance)!;
        using var deadline = new CancellationTokenSource(s_bound);
        while (field.GetValue(target) is not (true or 1))
        {
            await Task.Delay(10, deadline.Token).ConfigureAwait(true);
        }
    }

    private static MessageMutation Mutation(string key, string value, string result) =>
        new(new Dictionary<string, string?> { [key] = value }, [], result);

    // ---- LsmStorageEngine ----

    [Fact]
    public async Task Lsm_DisposeWhileACompactionMerges_QueuedCompactionThrowsObjectDisposed_AndTheMergeCompletes()
    {
        var gate = new SegmentGate();
        var engine = await TwoSegmentEngineAsync(gate).ConfigureAwait(true);

        gate.ArmStream();
        var inflight = Task.Run(() => engine.CompactAsync());
        await gate.Entered.Task.WaitAsync(s_bound).ConfigureAwait(true);
        var queued = Task.Run(() => engine.CompactAsync());
        await WaitUntilQueuedAsync(queued).ConfigureAwait(true);

        var dispose = Task.Run(engine.Dispose);
        await WaitUntilDisposalStartedAsync(engine).ConfigureAwait(true);
        gate.Release.TrySetResult();

        var queuedOutcome = await Record.ExceptionAsync(() => queued.WaitAsync(s_bound)).ConfigureAwait(true);
        queuedOutcome.Should().BeOfType<ObjectDisposedException>("a CompactAsync queued behind the merge must observe the disposal, not wait forever");
        var inflightOutcome = await Record.ExceptionAsync(() => inflight.WaitAsync(s_bound)).ConfigureAwait(true);
        inflightOutcome.Should().BeNull("Dispose must not tear the gates down under the running merge");
        await dispose.WaitAsync(s_bound).ConfigureAwait(true);
    }

    public static TheoryData<string> LsmQueuedReads() =>
        ["TryGetValueAsync", "ContainsKeyAsync", "GetAllItemsAsync", "GetRangeAsync", "AcquireSnapshotAsync", "ClearAsync", "CompactAsync", "SetAsync"];

    [Theory]
    [MemberData(nameof(LsmQueuedReads))]
    public async Task Lsm_DisposeWhileAReadHoldsTheEngineLock_QueuedCallThrowsObjectDisposed(string call)
    {
        var gate = new SegmentGate();
        var engine = await TwoSegmentEngineAsync(gate).ConfigureAwait(true);

        gate.ArmPointRead();
        var inflight = Task.Run(() => engine.TryGetValueAsync(new IntKey(1)));
        await gate.Entered.Task.WaitAsync(s_bound).ConfigureAwait(true);
        Task queued = call switch
        {
            "TryGetValueAsync" => Task.Run(() => engine.TryGetValueAsync(new IntKey(2))),
            "ContainsKeyAsync" => Task.Run(() => engine.ContainsKeyAsync(new IntKey(2))),
            "GetAllItemsAsync" => Task.Run(() => engine.GetAllItemsAsync()),
            "GetRangeAsync" => Task.Run(async () =>
            {
                await foreach (var _ in engine.GetRangeAsync(new IntKey(0), new IntKey(9)).ConfigureAwait(true))
                {
                }
            }),
            "AcquireSnapshotAsync" => Task.Run(() => engine.AcquireSnapshotAsync()),
            "ClearAsync" => Task.Run(() => engine.ClearAsync()),
            "CompactAsync" => Task.Run(() => engine.CompactAsync()),
            _ => Task.Run(() => engine.SetAsync(new IntKey(3), new StrValue("c"))),
        };
        await WaitUntilQueuedAsync(queued).ConfigureAwait(true);

        var dispose = Task.Run(engine.Dispose);
        await WaitUntilDisposalStartedAsync(engine).ConfigureAwait(true);
        gate.Release.TrySetResult();

        var outcome = await Record.ExceptionAsync(() => queued.WaitAsync(s_bound)).ConfigureAwait(true);
        outcome.Should().BeOfType<ObjectDisposedException>($"a {call} queued behind the read must observe the disposal, not wait forever");
        var read = await inflight.WaitAsync(s_bound).ConfigureAwait(true);
        read.Found.Should().BeTrue("the read that held the lock completes against the live engine");
        await dispose.WaitAsync(s_bound).ConfigureAwait(true);
    }

    [Fact]
    public async Task Lsm_DisposeWhileAReadHoldsTheEngineLock_QueuedSnapshotReleaseCompletes()
    {
        var gate = new SegmentGate();
        var engine = await TwoSegmentEngineAsync(gate).ConfigureAwait(true);
        var snapshot = await engine.AcquireSnapshotAsync().ConfigureAwait(true);

        gate.ArmPointRead();
        var inflight = Task.Run(() => engine.TryGetValueAsync(new IntKey(1)));
        await gate.Entered.Task.WaitAsync(s_bound).ConfigureAwait(true);
        var queued = Task.Run(() => snapshot.DisposeAsync().AsTask());
        await WaitUntilQueuedAsync(queued).ConfigureAwait(true);

        var dispose = Task.Run(engine.Dispose);
        await WaitUntilDisposalStartedAsync(engine).ConfigureAwait(true);
        gate.Release.TrySetResult();

        var outcome = await Record.ExceptionAsync(() => queued.WaitAsync(s_bound)).ConfigureAwait(true);
        outcome.Should().BeNull("releasing a snapshot of a disposing engine is a no-op, not a call that waits forever");
        await inflight.WaitAsync(s_bound).ConfigureAwait(true);
        await dispose.WaitAsync(s_bound).ConfigureAwait(true);
    }

    private async Task<LsmStorageEngine<IntKey, StrValue>> TwoSegmentEngineAsync(SegmentGate gate)
    {
        var folder = Path.Combine(_dir, "lsm");
        Directory.CreateDirectory(folder);
        var engine = LsmEngineFactory.Build(1000, folder, "seg", new GatedFormat(new SstSegmentFormat<IntKey, StrValue>(), gate));
        await engine.SetAsync(new IntKey(1), new StrValue("a")).ConfigureAwait(true);
        await engine.FlushAsync().ConfigureAwait(true);
        await engine.SetAsync(new IntKey(2), new StrValue("b")).ConfigureAwait(true);
        await engine.FlushAsync().ConfigureAwait(true);
        return engine;
    }

    // ---- shared harness ----

    // A queued call has passed its entry check and parked on the gate; give it a moment to get there.
    private static async Task WaitUntilQueuedAsync(Task queued)
    {
        await Task.Delay(100).ConfigureAwait(true);
        queued.IsCompleted.Should().BeFalse("the call is queued behind the holder");
    }

    // Blocks the first call to Block until the test releases it, so the holder's critical section is
    // held open deterministically.
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

    // Participant whose first prepare parks until released, so the coordinator holds its gate across
    // a participant call.
    private sealed class ParkingPrepare(ITransactionalResource inner) : ITransactionalResource
    {
        private int _armed = 1;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string ResourceId => inner.ResourceId;
        public async Task<PrepareResult> PrepareAsync(RequestId id, IReadOnlyDictionary<string, string?> writes, CancellationToken ct = default)
        {
            if (Interlocked.Exchange(ref _armed, 0) == 1)
            {
                Entered.TrySetResult();
                await Release.Task.ConfigureAwait(false);
            }
            return await inner.PrepareAsync(id, writes, ct).ConfigureAwait(false);
        }
        public Task<CommitResult> CommitAsync(RequestId id, CancellationToken ct = default) => inner.CommitAsync(id, ct);
        public Task<AbortResult> AbortAsync(RequestId id, CancellationToken ct = default) => inner.AbortAsync(id, ct);
    }

    // Parks the first armed segment access (a merge stream or a point read) until released.
    private sealed class SegmentGate
    {
        private int _streamArmed;
        private int _pointArmed;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void ArmStream() => Volatile.Write(ref _streamArmed, 1);
        public void ArmPointRead() => Volatile.Write(ref _pointArmed, 1);
        public Task ParkStreamAsync() => Park(ref _streamArmed);
        public Task ParkPointReadAsync() => Park(ref _pointArmed);
        private Task Park(ref int armed)
        {
            if (Interlocked.Exchange(ref armed, 0) != 1)
            {
                return Task.CompletedTask;
            }
            Entered.TrySetResult();
            return Release.Task;
        }
    }

    private sealed class GatedFormat(ISegmentFormat<IntKey, StrValue> inner, SegmentGate gate) : ISegmentFormat<IntKey, StrValue>
    {
        public string FileExtension => inner.FileExtension;
        public bool SupportsSnapshots => inner.SupportsSnapshots;
        public async Task<ISegmentReader<IntKey, StrValue>> WriteAsync(string segmentFolder, string segmentFileName,
            IReadOnlyList<SegmentWriteEntry<IntKey, StrValue>> sortedEntries, IReadOnlyDictionary<IntKey, ulong>? tombstones = null,
            CancellationToken cancellationToken = default)
            => new GatedReader(await inner.WriteAsync(segmentFolder, segmentFileName, sortedEntries, tombstones, cancellationToken).ConfigureAwait(false), gate);
        public ISegmentReader<IntKey, StrValue> Open(string segmentFolder, string segmentFileName) => new GatedReader(inner.Open(segmentFolder, segmentFileName), gate);
    }

    private sealed class GatedReader(ISegmentReader<IntKey, StrValue> inner, SegmentGate gate) : ISegmentReader<IntKey, StrValue>
    {
        public async Task<(BlockEntry Entry, bool Found)> TryGetBlockEntryAsync(IntKey key, ulong maxSequenceNumber = ulong.MaxValue, CancellationToken cancellationToken = default)
        {
            await gate.ParkPointReadAsync().ConfigureAwait(false);
            return await inner.TryGetBlockEntryAsync(key, maxSequenceNumber, cancellationToken).ConfigureAwait(false);
        }

        public async IAsyncEnumerable<SegmentWriteEntry<IntKey, StrValue>> StreamBlockEntriesAsync(ulong maxSequenceNumber = ulong.MaxValue,
            ScanDirection direction = ScanDirection.Ascending, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await gate.ParkStreamAsync().ConfigureAwait(false);
            await foreach (var entry in inner.StreamBlockEntriesAsync(maxSequenceNumber, direction, cancellationToken).ConfigureAwait(false))
            {
                yield return entry;
            }
        }

        public Task ClearAsync(CancellationToken cancellationToken = default) => inner.ClearAsync(cancellationToken);
        public ulong MaxSequenceNumber => inner.MaxSequenceNumber;
        public bool TryGetRange(out IntKey min, out IntKey max) => inner.TryGetRange(out min, out max);
        public IReadOnlyDictionary<IntKey, ulong> GetTombstoneSeqnos() => inner.GetTombstoneSeqnos();

        public async Task<(StrValue Value, ulong SequenceNumber, bool Found)> TryGetValueWithSeqnoAsync(IntKey key, ulong maxSequenceNumber, CancellationToken cancellationToken = default)
        {
            await gate.ParkPointReadAsync().ConfigureAwait(false);
            return await inner.TryGetValueWithSeqnoAsync(key, maxSequenceNumber, cancellationToken).ConfigureAwait(false);
        }
    }
}
