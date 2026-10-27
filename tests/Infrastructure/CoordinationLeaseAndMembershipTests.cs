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
using Boutquin.Storage.Distributed.Coordination;
using Boutquin.Storage.Distributed.Simulation;

namespace Boutquin.Storage.Infrastructure.Tests;

public sealed class CoordinationLeaseAndMembershipTests
{
    [Fact]
    public async Task AcquireLease_AppliedAfterLaterStampedCommit_SaturatesDeadlineAndKeepsLeaderServing()
    {
        using var cluster = new Cluster();
        var service = cluster.Services[0];
        var big = cluster.SubmitBehindRetainedPoll(now => service.AcquireLeaseAsync("/b", "w2", long.MaxValue - now, "acq-b"));

        var advance = () => cluster.Runtime.RunUntil(cluster.Runtime.Clock.Now + 20);

        advance.Should().NotThrow("a committed acquisition must apply even when replicated time has passed its timestamp");
        big.IsCompleted.Should().BeTrue();
        (await big)!.ExpiresAt.Should().Be(long.MaxValue);
        cluster.Finish(service.ReadAsync("/b")).Should().NotBeNull("the leader's poller keeps completing requests");
    }

    [Fact]
    public async Task RenewLease_AppliedAfterLaterStampedCommit_SaturatesDeadlineAndKeepsLeaderServing()
    {
        using var cluster = new Cluster();
        var service = cluster.Services[0];
        var renewed = cluster.Finish(service.AcquireLeaseAsync("/c", "w", 1000, "acq-c"))!;
        var renewal = cluster.SubmitBehindRetainedPoll(now => service.RenewLeaseAsync(renewed.Id, long.MaxValue - now, "renew-c"));

        var advance = () => cluster.Runtime.RunUntil(cluster.Runtime.Clock.Now + 20);

        advance.Should().NotThrow("a committed renewal must apply even when replicated time has passed its timestamp");
        renewal.IsCompleted.Should().BeTrue();
        (await renewal).Should().BeTrue();
        cluster.Finish(service.ReadLeaseAsync("/c"))!.ExpiresAt.Should().Be(long.MaxValue);
    }

    [Fact]
    public void AcquireLease_AppliedAfterLaterStampedCommit_ReplaysOnEveryReplicaAndReattachedService()
    {
        using var cluster = new Cluster();
        var service = cluster.Services[0];
        _ = cluster.SubmitBehindRetainedPoll(now => service.AcquireLeaseAsync("/b", "w2", long.MaxValue - now, "acq-b"));
        // Drive replication to the followers; the replay assertion below is what this test pins.
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                cluster.Runtime.RunUntil(cluster.Runtime.Clock.Now + 5);
            }
            catch (OverflowException)
            {
                // Tolerated here so that a regression is reported by the replay assertion, not the driver.
            }
        }
        cluster.Nodes[0].GetCommittedLog().Should().Contain(entry => IsBigAcquisition(entry.Command));

        var reattach = () => new RaftBackedCoordinationService(cluster.Nodes[0], cluster.Runtime, () => { }).Dispose();

        reattach.Should().NotThrow("replaying a committed prefix on a reattached service must be total");
        cluster.Nodes.Should().AllSatisfy(node => node.GetCommittedLog().Should().Contain(entry => IsBigAcquisition(entry.Command)));
    }

    // ICoordinationService documents ArgumentOutOfRangeException for a numeric argument outside its bounds; a
    // duration whose deadline the virtual clock cannot represent is such an argument, not an arithmetic fault.
    [Fact]
    public void AcquireLease_DurationPastClockHorizon_ThrowsArgumentOutOfRangeNamingDuration()
    {
        using var cluster = new Cluster();
        var service = cluster.Services[0];
        var horizon = long.MaxValue - cluster.Runtime.Clock.Now;

        Action acquire = () => _ = service.AcquireLeaseAsync("/h", "w", horizon + 1, "acq-h");

        acquire.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("durationTicks");
        cluster.Finish(service.AcquireLeaseAsync("/h", "w", horizon, "acq-h2"))!.ExpiresAt.Should().Be(long.MaxValue,
            "the largest representable duration stays admissible");
    }

    [Fact]
    public void RenewLease_DurationPastClockHorizon_ThrowsArgumentOutOfRangeNamingDuration()
    {
        using var cluster = new Cluster();
        var service = cluster.Services[0];
        var lease = cluster.Finish(service.AcquireLeaseAsync("/r", "w", 1000, "acq-r"))!;
        var horizon = long.MaxValue - cluster.Runtime.Clock.Now;

        Action renew = () => _ = service.RenewLeaseAsync(lease.Id, horizon + 1, "renew-r");

        renew.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("durationTicks");
        cluster.Finish(service.RenewLeaseAsync(lease.Id, horizon, "renew-r2")).Should().BeTrue(
            "the largest representable duration stays admissible");
    }

    // The constructor admits any positive client wait bound, so a bound larger than the remaining virtual time
    // (long.MaxValue is the natural "no client deadline") must wait until the clock's horizon, not overflow.
    [Fact]
    public void Operations_WithUnboundedOperationTimeout_CompleteInsteadOfOverflowing()
    {
        using var cluster = new Cluster(operationTimeoutTicks: long.MaxValue);
        var service = cluster.Services[0];

        Task<CoordinationValue>? pending = null;
        Action set = () => pending = service.SetAsync("/u", "v1", "set-u");

        set.Should().NotThrow();
        var written = cluster.Finish(pending!);
        written.Value.Should().Be("v1");
        cluster.Finish(service.ReadAsync("/u")).Value!.Value.Should().Be("v1");
    }

    [Fact]
    public async Task RaftCluster_AddNode_RejectsNodeThatAlreadyLeftFollowerState()
    {
        var elected = new RaftNode<string>("A");
        var solo = new RaftCluster<string>();
        solo.AddNode(elected);
        await solo.TriggerElectionAsync("A");
        elected.State.Should().Be(RaftNodeState.Leader);
        var cluster = new RaftCluster<string>();
        var fresh = new RaftNode<string>("B");
        cluster.AddNode(fresh);

        var add = () => cluster.AddNode(elected);

        add.Should().Throw<InvalidOperationException>();
        cluster.GetNodes().Select(node => node.NodeId).Should().Equal("B");
        fresh.PeerIds.Should().BeEmpty();
    }

    private static bool IsBigAcquisition(CoordinationCommand command) =>
        command.Operation == CoordinationOperation.AcquireLease && command.Path == "/b";

    private sealed class Cluster : IDisposable
    {
        public SimulatedNodeRuntime Runtime { get; }
        public RaftNode<CoordinationCommand>[] Nodes { get; }
        public RaftBackedCoordinationService[] Services { get; }

        public Cluster(long operationTimeoutTicks = 100)
        {
            Runtime = new SimulatedNodeRuntime(7);
            var raft = new RaftSimulation<CoordinationCommand>(Runtime);
            Nodes = Enumerable.Range(0, 3).Select(i => new RaftNode<CoordinationCommand>($"n{i}")).ToArray();
            foreach (var node in Nodes) { raft.AddNode(node); }
            Services = Nodes.Select(node => new RaftBackedCoordinationService(node, Runtime, raft.Pump, operationTimeoutTicks)).ToArray();
            raft.Elect("n0");
            Runtime.RunUntil(10);
        }

        public T Finish<T>(Task<T> task, long ticks = 20)
        {
            Runtime.RunUntil(Runtime.Clock.Now + ticks);
            task.IsCompleted.Should().BeTrue();
            return task.GetAwaiter().GetResult();
        }

        // A short lease that expires while the leader is paused gives the leader's retained poll an expiration
        // to propose. On resume that poll runs ahead of the client's queued proposal and stamps the expiration
        // with the resume tick, so the earlier-stamped request applies at a later replicated time.
        public Task<T> SubmitBehindRetainedPoll<T>(Func<long, Task<T>> submit)
        {
            Finish(Services[0].AcquireLeaseAsync("/timer", "w", 25, "acq-timer"));
            Runtime.Pause("n0");
            Runtime.RunUntil(Runtime.Clock.Now + 1);
            var request = submit(Runtime.Clock.Now);
            Runtime.RunUntil(Runtime.Clock.Now + 40);
            Runtime.Resume("n0");
            return request;
        }

        public void Dispose()
        {
            foreach (var service in Services) { service.Dispose(); }
        }
    }
}
