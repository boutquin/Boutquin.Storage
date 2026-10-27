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
using System.Globalization;
using Boutquin.Storage.Distributed.Coordination;
using Boutquin.Storage.Distributed.Interfaces.Coordination;
using Boutquin.Storage.Distributed.Simulation;

namespace Boutquin.Storage.Infrastructure.Tests;

public sealed class CoordinationServiceTests
{
    [Fact]
    public void Register_RequiresQuorumAndRejectsStaleCompareExchange()
    {
        using var cluster = new Cluster();
        var service = cluster.Services[0];
        var write = service.SetAsync("/key", "first", "write-1");
        write.IsCompleted.Should().BeFalse("admission is not commitment");
        var value = cluster.Finish(write);
        cluster.Finish(service.ReadAsync("/key")).Value.Should().Be(value);
        cluster.Finish(service.CompareExchangeAsync("/key", value.Revision, "second", "cas-1")).Exchanged.Should().BeTrue();
        cluster.Finish(service.CompareExchangeAsync("/key", value.Revision, "stale", "cas-2")).Exchanged.Should().BeFalse();
        cluster.Runtime.Network.SetPartition("n0", "n1", true);
        cluster.Runtime.Network.SetPartition("n0", "n2", true);
        var read = service.ReadAsync("/key");
        cluster.Runtime.RunUntil(cluster.Runtime.Clock.Now + 101);
        var outcome = () => read.GetAwaiter().GetResult();
        outcome.Should().Throw<TimeoutException>();
    }

    [Fact]
    public void Failover_RejectsStaleCandidateAndRecoversWithEligibleSurvivor()
    {
        using var cluster = new Cluster();
        cluster.Runtime.Network.SetPartition("n0", "n1", true);
        var value = cluster.Finish(cluster.Services[0].SetAsync("/key", "committed", "before-failover"));
        cluster.Runtime.Network.SetPartition("n0", "n2", true);
        cluster.Raft.Elect("n1");
        cluster.Runtime.RunUntil(cluster.Runtime.Clock.Now + 20);
        cluster.Nodes[1].State.Should().NotBe(RaftNodeState.Leader);
        cluster.Raft.Elect("n2");
        cluster.Runtime.RunUntil(cluster.Runtime.Clock.Now + 20);
        cluster.Nodes[2].State.Should().Be(RaftNodeState.Leader);
        cluster.Finish(cluster.Services[2].ReadAsync("/key")).Value.Should().Be(value);
    }

    [Fact]
    public void Watches_RespectFutureCursorForLiveEvents()
    {
        using var cluster = new Cluster();
        using var watch = cluster.Finish(cluster.Services[0].WatchAsync("/key", 2));
        cluster.Finish(cluster.Services[0].SetAsync("/key", "one", "one"));
        watch.TryRead(out _).Should().BeFalse("a future cursor filters live events as well as replay");
        cluster.Finish(cluster.Services[0].SetAsync("/key", "two", "two"));
        watch.TryRead(out _).Should().BeFalse();
        cluster.Finish(cluster.Services[0].SetAsync("/key", "three", "three"));
        watch.TryRead(out var change).Should().BeTrue();
        change!.Revision.Should().Be(3);
    }

    [Fact]
    public void Leases_RenewExpireAndReleaseEphemeralNodes_WithIncreasingTokens()
    {
        using var cluster = new Cluster();
        var service = cluster.Services[0];
        var first = cluster.Finish(service.AcquireLeaseAsync("/shard", "alice", 100, "lease-a"))!;
        first.FencingToken.Should().BePositive();
        cluster.Finish(service.AcquireLeaseAsync("/shard", "bob", 100, "busy")).Should().BeNull();
        using var watch = cluster.Finish(service.WatchAsync("/shards/0/leader"));
        cluster.Finish(service.SetAsync("/shards/0/leader", "alice", "assign", first.Id));
        watch.TryRead(out var assigned).Should().BeTrue();
        assigned!.Kind.Should().Be(CoordinationEventKind.Changed);
        cluster.Finish(service.RenewLeaseAsync(first.Id, 100, "renew")).Should().BeTrue();
        var renewed = cluster.Finish(service.ReadLeaseAsync("/shard"))!;
        renewed.ExpiresAt.Should().BeGreaterThan(first.ExpiresAt);
        renewed.FencingToken.Should().Be(first.FencingToken);
        cluster.Runtime.RunUntil(renewed.ExpiresAt + 20);
        cluster.Finish(service.ReadAsync("/shards/0/leader")).Value.Should().BeNull();
        watch.TryRead(out var expired).Should().BeTrue();
        expired!.Kind.Should().Be(CoordinationEventKind.LeaseEnded);
        cluster.Finish(service.RenewLeaseAsync(first.Id, 100, "too-late")).Should().BeFalse();
        var second = cluster.Finish(service.AcquireLeaseAsync("/shard", "bob", 100, "lease-b"))!;
        second.FencingToken.Should().BeGreaterThan(first.FencingToken);
        cluster.Finish(service.SetAsync("/shards/0/leader", "bob", "assign-b", second.Id));
        cluster.Finish(service.ReleaseLeaseAsync(second.Id, "release-b")).Should().BeTrue();
        cluster.Finish(service.ReadAsync("/shards/0/leader")).Value.Should().BeNull();
        var staleWrite = service.SetAsync("/stale", "bad", "stale-write", first.Id);
        cluster.Runtime.RunUntil(cluster.Runtime.Clock.Now + 20);
        var staleResult = () => staleWrite.GetAwaiter().GetResult();
        staleResult.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void LeaderChange_PreservesCasDedupWatchReplayAndMonotonicSequences()
    {
        using var cluster = new Cluster();
        var original = cluster.Finish(cluster.Services[0].SetAsync("/key", "one", "initial"));
        var cas = cluster.Finish(cluster.Services[0].CompareExchangeAsync("/key", original.Revision, "two", "cas"));
        var firstId = cluster.Finish(new LinearizableIdGenerator(cluster.Services[0]).NextAsync("id-a"));
        cluster.Runtime.Network.SetPartition("n0", "n1", true);
        cluster.Runtime.Network.SetPartition("n0", "n2", true);
        cluster.Raft.Elect("n1");
        cluster.Runtime.RunUntil(cluster.Runtime.Clock.Now + 20);
        var leader = cluster.Services[1];
        cluster.Finish(leader.ReadAsync("/key")).Value.Should().Be(cas.Current);
        cluster.Finish(leader.CompareExchangeAsync("/key", original.Revision, "two", "cas")).Should().Be(cas);
        cluster.Finish(new LinearizableIdGenerator(leader).NextAsync("id-a")).Should().Be(firstId);
        var nextId = cluster.Finish(new LinearizableIdGenerator(leader).NextAsync("id-b"));
        nextId.Should().BeGreaterThan(firstId);
        cluster.Finish(new FencingTokenService(leader).NextAsync("disk", "fence")).Should().BeGreaterThan(nextId);
        using var watch = cluster.Finish(leader.WatchAsync("/key", original.Revision));
        watch.TryRead(out var change).Should().BeTrue();
        change!.Value.Should().Be(cas.Current);
        watch.TryRead(out _).Should().BeFalse("an identical retry must not publish twice");
    }

    [Fact]
    public void TimedOutMutation_CanCommitAfterHealing_AndRetryReturnsOriginalId()
    {
        using var cluster = new Cluster();
        cluster.Runtime.Network.SetPartition("n0", "n1", true);
        cluster.Runtime.Network.SetPartition("n0", "n2", true);
        var first = cluster.Services[0].AllocateSequenceAsync("ids", "uncertain");
        cluster.Runtime.RunUntil(cluster.Runtime.Clock.Now + 101);
        var timeout = () => first.GetAwaiter().GetResult();
        timeout.Should().Throw<TimeoutException>();
        cluster.Runtime.Network.SetPartition("n0", "n1", false);
        cluster.Runtime.Network.SetPartition("n0", "n2", false);
        cluster.Runtime.RunUntil(cluster.Runtime.Clock.Now + 20);
        cluster.Finish(cluster.Services[0].AllocateSequenceAsync("ids", "uncertain")).Should().Be(1);
        cluster.Finish(cluster.Services[0].AllocateSequenceAsync("ids", "next")).Should().Be(2);
        var reused = cluster.Services[0].AllocateSequenceAsync("other", "uncertain");
        cluster.Runtime.RunUntil(cluster.Runtime.Clock.Now + 20);
        var conflict = () => reused.GetAwaiter().GetResult();
        conflict.Should().Throw<InvalidOperationException>().WithMessage("*different arguments*");
    }

    [Fact]
    public async Task CompetingCas_ExactlyOneWins_AndSameValueWritePreventsAba()
    {
        using var cluster = new Cluster();
        var service = cluster.Services[0];
        var first = cluster.Finish(service.SetAsync("/key", "a", "initial"));
        var left = service.CompareExchangeAsync("/key", first.Revision, "b", "left");
        var right = service.CompareExchangeAsync("/key", first.Revision, "c", "right");
        cluster.Runtime.RunUntil(cluster.Runtime.Clock.Now + 20);
        new[] { await left.ConfigureAwait(true), await right.ConfigureAwait(true) }.Count(result => result.Exchanged).Should().Be(1);
        cluster.Finish(service.SetAsync("/key", "a", "restore"));
        cluster.Finish(service.CompareExchangeAsync("/key", first.Revision, "stale", "aba")).Exchanged.Should().BeFalse();
        var current = cluster.Finish(service.ReadAsync("/key"));
        cluster.Finish(service.CompareExchangeAsync("/key", current.Value!.Revision, null, "delete")).Exchanged.Should().BeTrue();
        cluster.Finish(service.ReadAsync("/key")).Value.Should().BeNull();
    }

    [Fact]
    public async Task TypedRegister_EncodesKeysAndValues_WithoutDependingOnReferenceIdentity()
    {
        using var cluster = new Cluster();
        var register = new LinearizableRegister<string, int>(cluster.Services[0], "/typed");
        var write = register.SetAsync("key/with/slashes", 42, "typed-write");
        cluster.Runtime.RunUntil(cluster.Runtime.Clock.Now + 20);
        var revision = await write.ConfigureAwait(true);
        var read = register.ReadAsync("key/with/slashes");
        cluster.Runtime.RunUntil(cluster.Runtime.Clock.Now + 20);
        (await read.ConfigureAwait(true)).Should().Be(new RegisterValue<int>(true, 42, revision));
        var cas = register.CompareExchangeAsync("key/with/slashes", revision, 43, "typed-cas");
        cluster.Runtime.RunUntil(cluster.Runtime.Clock.Now + 20);
        (await cas.ConfigureAwait(true)).Should().BeTrue();
    }

    [Theory]
    [InlineData("read")]
    [InlineData("set")]
    [InlineData("cas")]
    public void TypedRegister_IllFormedKey_IsRefusedBeforeAdmission(string operation)
    {
        using var cluster = new Cluster();
        var register = new LinearizableRegister<string, string>(cluster.Services[0], "/typed");
        Action invoke = operation switch
        {
            "read" => () => register.ReadAsync("key\uD800"),
            "set" => () => register.SetAsync("key\uD800", "value", "bad-key"),
            _ => () => register.CompareExchangeAsync("key\uD800", 0, "value", "bad-key"),
        };

        invoke.Should().Throw<ArgumentException>();
        cluster.Finish(register.ReadAsync("key�")).Exists.Should().BeFalse();
        cluster.Finish(register.SetAsync("key😀", "value", "valid")).Should().Be(1);
    }

    private sealed record RegisterPayload(string Text);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TypedRegister_LossyNestedValue_IsRefusedBeforeAdmission(bool compareExchange)
    {
        using var cluster = new Cluster();
        var register = new LinearizableRegister<string, RegisterPayload>(cluster.Services[0], "/typed");
        var original = new RegisterPayload("original😀");
        var revision = cluster.Finish(register.SetAsync("key", original, "initial"));
        Action invoke = compareExchange
            ? () => register.CompareExchangeAsync("key", revision, new RegisterPayload("value\uDC00"), "bad-value")
            : () => register.SetAsync("key", new RegisterPayload("value\uDC00"), "bad-value");

        invoke.Should().Throw<ArgumentException>();
        cluster.Finish(register.ReadAsync("key")).Should().Be(new RegisterValue<RegisterPayload>(true, original, revision));
        cluster.Finish(register.CompareExchangeAsync("key", revision, new RegisterPayload("updated"), "valid-cas"))
            .Should().BeTrue();
    }

    [Theory]
    [InlineData("read")]
    [InlineData("set")]
    [InlineData("cas")]
    [InlineData("set-fault")]
    [InlineData("set-cancel")]
    public async Task TypedRegister_CompletesInsideTheServiceCompletion_SoVirtualTimeHostsStayDeterministic(string operation)
    {
        // A host drives virtual time until a task reports IsCompleted. If the typed result only
        // completes later on a thread-pool continuation, the number of ticks the host advances
        // depends on thread timing, and seeded simulations stop reproducing. Repeat so a deferred
        // completion cannot pass by happening to win the race once.
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var service = new ManualCoordinationService();
            var register = new LinearizableRegister<string, int>(service, "/typed");
            Task task = operation switch
            {
                "read" => register.ReadAsync("key"),
                "cas" => register.CompareExchangeAsync("key", 3, 7, $"cas-{attempt}"),
                _ => register.SetAsync("key", 7, $"set-{attempt}"),
            };
            task.IsCompleted.Should().BeFalse("nothing has committed yet");

            service.Complete(operation);

            task.IsCompleted.Should().BeTrue(
                "the typed result must be complete as soon as the service's own result is, on the completing (driver) thread");
        }

        var last = new ManualCoordinationService();
        var typed = new LinearizableRegister<string, int>(last, "/typed");
        switch (operation)
        {
            case "read":
                var read = typed.ReadAsync("key");
                last.Complete(operation);
                (await read.ConfigureAwait(true)).Should().Be(new RegisterValue<int>(true, 7, 4));
                break;
            case "cas":
                var cas = typed.CompareExchangeAsync("key", 3, 7, "cas-last");
                last.Complete(operation);
                (await cas.ConfigureAwait(true)).Should().BeTrue();
                break;
            case "set":
                var set = typed.SetAsync("key", 7, "set-last");
                last.Complete(operation);
                (await set.ConfigureAwait(true)).Should().Be(4);
                break;
            case "set-fault":
                var faulted = typed.SetAsync("key", 7, "fault-last");
                last.Complete(operation);
                var awaitFault = async () => await faulted.ConfigureAwait(true);
                await awaitFault.Should().ThrowAsync<InvalidOperationException>().WithMessage("not leader").ConfigureAwait(true);
                break;
            default:
                var canceled = typed.SetAsync("key", 7, "cancel-last");
                last.Complete(operation);
                canceled.IsCanceled.Should().BeTrue();
                break;
        }
    }

    [Fact]
    public void ReattachingService_Replayss_committedPrefixAndDedupOutcomes()
    {
        using var cluster = new Cluster();
        var first = cluster.Finish(cluster.Services[0].SetAsync("/key", "value", "dedup"));
        cluster.Services[0].Dispose();
        cluster.Services[0] = new RaftBackedCoordinationService(cluster.Nodes[0], cluster.Runtime, cluster.Raft.Pump);
        cluster.Finish(cluster.Services[0].SetAsync("/key", "value", "dedup")).Should().Be(first);
        using var watch = cluster.Finish(cluster.Services[0].WatchAsync("/key"));
        watch.TryRead(out _).Should().BeTrue();
        watch.TryRead(out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(271828u)]
    [InlineData(314159u)]
    [InlineData(4294967295u)]
    public void SeededFaults_RetransmitAndProduceLinearizableHistories(uint seed)
    {
        using var cluster = new Cluster(seed);
        cluster.Runtime.Network.Policy = new FaultInjectionPolicy(0.15, 0.5, 7);
        var history = new HistoryRecorder<int, int>();
        var random = new PropertyTestGenerators(seed);
        for (var i = 0; i < 40; i++)
        {
            var write = random.Next(2) == 0;
            var input = write ? i + 1 : -1;
            var id = history.Invoke(input);
            if (write)
            {
                var value = cluster.Finish(cluster.Services[0].SetAsync("/key", input.ToString(CultureInfo.InvariantCulture), $"seed:{i}"), 90);
                history.Complete(id, int.Parse(value.Value, CultureInfo.InvariantCulture));
            }
            else
            {
                var read = cluster.Finish(cluster.Services[0].ReadAsync("/key"), 90);
                history.Complete(id, read.Value is null ? 0 : int.Parse(read.Value.Value, CultureInfo.InvariantCulture));
            }
        }
        LinearizabilityChecker.Check(history.Snapshot(), 0,
            (int state, int input, int output) => input < 0 ? (output == state, state) : (output == input, input))
            .Should().Be(LinearizabilityResult.Valid);
    }

    [Fact]
    public void LeaseExpiry_WaitsForQuorumAndPublishesOnceAfterHealing()
    {
        using var cluster = new Cluster();
        var service = cluster.Services[0];
        var lease = cluster.Finish(service.AcquireLeaseAsync("/lock", "owner", 150, "lease"))!;
        using var watch = cluster.Finish(service.WatchAsync("/ephemeral"));
        cluster.Finish(service.SetAsync("/ephemeral", "value", "ephemeral", lease.Id));
        watch.TryRead(out _).Should().BeTrue();
        cluster.Runtime.Network.SetPartition("n0", "n1", true);
        cluster.Runtime.Network.SetPartition("n0", "n2", true);
        cluster.Runtime.RunUntil(lease.ExpiresAt + 20);
        watch.TryRead(out _).Should().BeFalse("local elapsed time cannot publish uncommitted expiration");
        cluster.Runtime.Network.SetPartition("n0", "n1", false);
        cluster.Runtime.Network.SetPartition("n0", "n2", false);
        cluster.Runtime.RunUntil(cluster.Runtime.Clock.Now + 20);
        watch.TryRead(out var expired).Should().BeTrue();
        expired!.Kind.Should().Be(CoordinationEventKind.LeaseEnded);
        watch.TryRead(out _).Should().BeFalse();
        cluster.Finish(service.ReadAsync("/ephemeral")).Value.Should().BeNull();
    }

    [Fact]
    public void PausedLeader_StillHonorsClientDeadlineAndDoesNotAdmitExpiredRequest()
    {
        using var cluster = new Cluster();
        cluster.Runtime.Pause("n0");
        var write = cluster.Services[0].SetAsync("/key", "never-admitted", "paused");
        cluster.Runtime.RunUntil(cluster.Runtime.Clock.Now + 101);
        var timeout = () => write.GetAwaiter().GetResult();
        timeout.Should().Throw<TimeoutException>();
        cluster.Runtime.Resume("n0");
        cluster.Finish(cluster.Services[0].ReadAsync("/key")).Value.Should().BeNull();
    }

    [Fact]
    public void PausedQuorumPeer_RecoversPendingWriteThroughHeartbeatRetry()
    {
        using var cluster = new Cluster();
        cluster.Runtime.Network.SetPartition("n0", "n2", true);
        cluster.Runtime.Pause("n1");
        var write = cluster.Services[0].SetAsync("/key", "eventual", "pending");
        cluster.Runtime.RunUntil(cluster.Runtime.Clock.Now + 20);
        write.IsCompleted.Should().BeFalse();
        cluster.Runtime.Resume("n1");
        cluster.Finish(write).Value.Should().Be("eventual");
    }

    [Fact]
    public void FailedCasRetry_RemainsFailedEvenWhenItsConditionLaterBecomesTrue()
    {
        using var cluster = new Cluster();
        var service = cluster.Services[0];
        var failed = cluster.Finish(service.CompareExchangeAsync("/key", 1, "replacement", "cached-failure"));
        failed.Exchanged.Should().BeFalse();
        cluster.Finish(service.SetAsync("/key", "first", "create")).Revision.Should().Be(1);
        cluster.Finish(service.CompareExchangeAsync("/key", 1, "replacement", "cached-failure")).Should().Be(failed);
        cluster.Finish(service.ReadAsync("/key")).Value!.Value.Should().Be("first");
    }

    [Fact]
    public async Task WatchAsyncEnumeration_Receivess_committedEventsAndSupportsCancellation()
    {
        using var cluster = new Cluster();
        using var watch = cluster.Finish(cluster.Services[0].WatchAsync("/watched"));
        using var cancellation = new CancellationTokenSource();
        await using var reader = watch.ReadAllAsync(cancellation.Token).GetAsyncEnumerator();
        var next = reader.MoveNextAsync().AsTask();
        cluster.Finish(cluster.Services[0].SetAsync("/watched", "yes", "watched-write"));
        (await next.ConfigureAwait(true)).Should().BeTrue();
        reader.Current.Value!.Value.Should().Be("yes");
        next = reader.MoveNextAsync().AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next).ConfigureAwait(true);
    }

    [Fact]
    public void CancellationAndDisposal_CompletePendingClientsWithoutMutatingBeforeAdmission()
    {
        using var cluster = new Cluster();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var canceled = cluster.Services[0].SetAsync("/key", "never", "canceled", ct: cancellation.Token);
        canceled.IsCanceled.Should().BeTrue();
        cluster.Finish(cluster.Services[0].ReadAsync("/key")).Value.Should().BeNull();
        var pending = cluster.Services[0].ReadAsync("/key");
        cluster.Services[0].Dispose();
        var disposed = () => pending.GetAwaiter().GetResult();
        disposed.Should().Throw<ObjectDisposedException>();
    }

    /// <summary>
    /// A coordination service whose results are completed by the test, through task sources that
    /// run continuations asynchronously — the same completion contract as the Raft-backed service.
    /// </summary>
    private sealed class ManualCoordinationService : ICoordinationService
    {
        private static readonly CoordinationValue s_committed = new("7", 4, null);
        private TaskCompletionSource<CoordinationRead>? _read;
        private TaskCompletionSource<CoordinationValue>? _set;
        private TaskCompletionSource<CompareExchangeResult>? _cas;

        public void Complete(string operation)
        {
            switch (operation)
            {
                case "read": _read!.SetResult(new CoordinationRead(s_committed, 4)); break;
                case "cas": _cas!.SetResult(new CompareExchangeResult(true, s_committed)); break;
                case "set": _set!.SetResult(s_committed); break;
                case "set-fault": _set!.SetException(new InvalidOperationException("not leader")); break;
                default: _set!.SetCanceled(); break;
            }
        }

        public Task<CoordinationRead> ReadAsync(string path, CancellationToken ct = default)
            => (_read = new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

        public Task<CoordinationValue> SetAsync(string path, string value, string requestId, long? leaseId = null, CancellationToken ct = default)
            => (_set = new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

        public Task<CompareExchangeResult> CompareExchangeAsync(string path, long expectedRevision, string? value,
            string requestId, long? leaseId = null, CancellationToken ct = default)
            => (_cas = new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

        public Task<CoordinationWatch> WatchAsync(string path, long afterRevision = 0, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<CoordinationLease?> AcquireLeaseAsync(string resource, string owner, long durationTicks, string requestId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<bool> RenewLeaseAsync(long leaseId, long durationTicks, string requestId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<bool> ReleaseLeaseAsync(long leaseId, string requestId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<CoordinationLease?> ReadLeaseAsync(string resource, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<long> AllocateSequenceAsync(string scope, string requestId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }

    private sealed class Cluster : IDisposable
    {
        public SimulatedNodeRuntime Runtime { get; }
        public RaftSimulation<CoordinationCommand> Raft { get; }
        public RaftBackedCoordinationService[] Services { get; }
        public RaftNode<CoordinationCommand>[] Nodes { get; }

        public Cluster(uint seed = 17)
        {
            Runtime = new SimulatedNodeRuntime(seed);
            Raft = new RaftSimulation<CoordinationCommand>(Runtime);
            Nodes = Enumerable.Range(0, 3).Select(i => new RaftNode<CoordinationCommand>($"n{i}")).ToArray();
            foreach (var node in Nodes) { Raft.AddNode(node); }
            Services = Nodes.Select(node => new RaftBackedCoordinationService(node, Runtime, Raft.Pump)).ToArray();
            Raft.Elect("n0");
            Runtime.RunUntil(10);
        }

        public T Finish<T>(Task<T> task, long ticks = 20)
        {
            Runtime.RunUntil(Runtime.Clock.Now + ticks);
            task.IsCompleted.Should().BeTrue();
            return task.GetAwaiter().GetResult();
        }

        public void Dispose()
        {
            foreach (var service in Services) { service.Dispose(); }
        }
    }
}
