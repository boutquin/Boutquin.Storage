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
using Boutquin.Storage.Distributed.Simulation;

namespace Boutquin.Storage.Infrastructure.Tests;

public sealed class SimulationComponentInvariantTests
{
    [Theory]
    [InlineData(42u)]
    [InlineData(19088743u)]
    [InlineData(3735928559u)]
    public void Raft_ActualHandlersPreserveSafetyAcrossLongFaultSchedules(uint seed)
    {
        var runtime = new SimulatedNodeRuntime(seed, new FaultInjectionPolicy(0.08, 0.25, 5));
        var simulation = new RaftSimulation<int>(runtime);
        var logs = Enumerable.Range(0, 5).Select(_ => new InMemoryRaftLog<int>()).ToArray();
        var nodes = logs.Select((log, i) => new RaftNode<int>($"n{i}", log)).ToArray();
        foreach (var node in nodes)
        {
            simulation.AddNode(node);
        }
        runtime.Invariants.Add("one leader per term", () => nodes.Where(n => n.State == RaftNodeState.Leader)
            .GroupBy(n => n.CurrentTerm).All(group => group.Count() == 1));
        runtime.Invariants.Add("log matching", () => LogMatching(logs));
        runtime.Invariants.Add("committed prefix agreement", () => CommittedPrefixesAgree(nodes));
        var random = new PropertyTestGenerators(seed ^ 0xabcd);
        for (var i = 0; i < 2000; i++)
        {
            var tick = i * 10L;
            var node = nodes[random.Next(nodes.Length)];
            if (i % 13 == 0)
            {
                simulation.Elect(node.NodeId, tick);
            }
            var command = i;
            runtime.Schedule(node.NodeId, tick + 3, () =>
            {
                Completed(node.ProposeAsync(command));
                simulation.Pump();
            });
            if (i % 19 == 0)
            {
                runtime.Clock.Schedule(tick + 1, () => runtime.Pause(node.NodeId));
                runtime.Clock.Schedule(tick + 7, () => runtime.Resume(node.NodeId));
            }
        }
        runtime.RunUntil(20010, 1000000);
        nodes.Max(n => n.CurrentTerm).Should().BeGreaterThan(1);
        nodes.Sum(n => n.GetCommittedLog().Count).Should().BeGreaterThan(0);
    }

    [Fact]
    public void Raft_RegistrationAfterExternallyStartedElectionIsRejected()
    {
        var runtime = new SimulatedNodeRuntime(1);
        var simulation = new RaftSimulation<int>(runtime);
        var first = new RaftNode<int>("first");
        simulation.AddNode(first);
        first.StartElection();
        simulation.Invoking(s => s.AddNode(new RaftNode<int>("late"))).Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData(41u)]
    [InlineData(100003u)]
    public void ObservedRemoveSet_DelayedOldStateCannotResurrectRemovedTag(uint seed)
    {
        var runtime = new SimulatedNodeRuntime(seed, new FaultInjectionPolicy(0, 1, 20));
        var left = new ORSet<string>();
        left.Add("left", "removed");
        var old = new ORSet<string>();
        old = (ORSet<string>)old.Merge(left);
        left.Remove("removed");
        runtime.AddNode("left", message => left = (ORSet<string>)left.Merge((ORSet<string>)message));
        runtime.AddNode("right", _ => { });
        var concurrentAddSent = false;
        runtime.Invariants.Add("removed tag never resurrects", () => concurrentAddSent || !left.Contains("removed"));
        for (var i = 0; i < 500; i++)
        {
            runtime.Network.Send("right", "left", old);
        }
        runtime.RunUntil(100);
        var concurrent = new ORSet<string>();
        concurrent.Add("right", "removed");
        concurrentAddSent = true;
        runtime.Network.Send("right", "left", concurrent);
        // A new unseen add must survive the observed remove, so the old invariant no longer applies.
        runtime.RunUntil(200);
        left.Contains("removed").Should().BeTrue();
    }

    [Fact]
    public void CollisionHeavyRing_ReconfigurationKeepsUniqueLiveOwners()
    {
        var runtime = new SimulatedNodeRuntime(17);
        var ring = new ConsistentHashRing<string>(32, new ConstantHash());
        ring.AddNode("a");
        ring.AddNode("b");
        ring.AddNode("c");
        var expected = new HashSet<string> { "a", "b", "c" };
        runtime.AddNode("ring", _ => { });
        runtime.Invariants.Add("collision ownership is unique and complete",
            () => ring.GetNodes("key", expected.Count).ToHashSet().SetEquals(expected));
        for (var i = 0; i < 100; i++)
        {
            runtime.Schedule("ring", i * 2, () => { ring.RemoveNode("b"); expected.Remove("b"); });
            runtime.Schedule("ring", i * 2 + 1, () => { ring.AddNode("b"); expected.Add("b"); });
        }
        runtime.RunUntil(200);
    }

    [Theory]
    [InlineData(42u)]
    [InlineData(987654321u)]
    public void Gossip_ReorderingAndDuplicatesNeverLowerVersions(uint seed)
    {
        var runtime = new SimulatedNodeRuntime(seed, new FaultInjectionPolicy(0.1, 0.5, 50));
        var gossip = new GossipProtocol<string, int>();
        long before = 0;
        long after = 0;
        runtime.AddNode("sender", _ => { });
        runtime.AddNode("receiver", message =>
        {
            before = gossip.GetClusterState().GetValueOrDefault("owner").Version;
            gossip.ProcessSyncMessage((IReadOnlyDictionary<string, (int State, long Version)>)message);
            after = gossip.GetClusterState()["owner"].Version;
        });
        runtime.Invariants.Add("gossip version monotonicity", () => after >= before);
        for (var i = 1; i <= 2000; i++)
        {
            runtime.Network.Send("sender", "receiver",
                new Dictionary<string, (int, long)> { ["owner"] = (i, i) });
        }
        runtime.RunUntil(100);
        runtime.Network.Policy = new FaultInjectionPolicy();
        runtime.Network.Send("sender", "receiver",
            new Dictionary<string, (int, long)> { ["owner"] = (2000, 2000) });
        runtime.RunUntil(101);
        gossip.GetClusterState()["owner"].Version.Should().Be(2000);
    }

    [Fact]
    public void JepsenHarness_RecordsRaftCommitResponsesAndReads()
    {
        var runtime = new SimulatedNodeRuntime(53, new FaultInjectionPolicy(0, 0.5, 3));
        var simulation = new RaftSimulation<int>(runtime);
        var nodes = Enumerable.Range(0, 3).Select(i => new RaftNode<int>($"raft{i}")).ToArray();
        foreach (var node in nodes)
        {
            simulation.AddNode(node);
        }
        simulation.Elect("raft0");
        runtime.RunUntil(20);
        nodes[0].State.Should().Be(RaftNodeState.Leader);
        var harness = new JepsenLiteHarness<int, int>(runtime);
        harness.Schedule("raft0", 0, 19, (command, respond) =>
        {
            Completed(nodes[0].ProposeAsync(command));
            simulation.Pump();
            runtime.Schedule("raft0", 20, () =>
            {
                nodes[0].GetCommittedLog().Should().Contain(entry => entry.Command == command);
                respond(command);
            });
        });
        harness.Schedule("raft0", 30, -1, (_, respond) => respond(nodes[0].GetCommittedLog()[^1].Command));
        runtime.RunUntil(60);
        LinearizabilityChecker.Check(harness.History.Snapshot(), 0, Register).Should().Be(LinearizabilityResult.Valid);
    }

    [Fact]
    public void SingleLeaderReplication_HistoryExposesLagAndPassesAfterCatchup()
    {
        var runtime = new SimulatedNodeRuntime(3);
        using var replication = new SingleLeaderReplication<string, int>();
        replication.AddFollower("follower");
        runtime.AddNode("client", _ => { });
        runtime.AddNode("follower", _ => Completed(replication.SyncFollowerAsync("follower")));
        var harness = new JepsenLiteHarness<int, int>(runtime);
        harness.Schedule("client", 0, 7, (value, respond) =>
        {
            Completed(replication.WriteAsync("key", value));
            respond(value);
        });
        harness.Schedule("client", 1, -1, (_, respond) =>
            respond(Completed(replication.ReadAsync("key", "follower")).Value));
        runtime.RunUntil(2);
        LinearizabilityChecker.Check(harness.History.Snapshot(), 0, Register)
            .Should().Be(LinearizabilityResult.Invalid, "an unsynced follower is not a linearizable read source");
        runtime.Network.Send("client", "follower", "sync");
        runtime.RunUntil(3);
        var caughtUp = new JepsenLiteHarness<int, int>(runtime);
        caughtUp.Schedule("client", 0, -1, (_, respond) =>
            respond(Completed(replication.ReadAsync("key", "follower")).Value));
        runtime.RunUntil(4);
        LinearizabilityChecker.Check(caughtUp.History.Snapshot(), 7, Register).Should().Be(LinearizabilityResult.Valid);
    }

    private static T Completed<T>(Task<T> task)
    {
        task.IsCompletedSuccessfully.Should().BeTrue("the in-process component completes synchronously on a single driver");
        return task.GetAwaiter().GetResult();
    }

    private static void Completed(Task task)
    {
        task.IsCompletedSuccessfully.Should().BeTrue();
        task.GetAwaiter().GetResult();
    }

    private static (bool Allowed, int State) Register(int state, int input, int output)
        => input < 0 ? (output == state, state) : (output == input, input);

    private static bool CommittedPrefixesAgree(RaftNode<int>[] nodes)
        => nodes.All(left => nodes.All(right =>
            left.GetCommittedLog().Take(Math.Min(left.GetCommittedLog().Count, right.GetCommittedLog().Count))
                .SequenceEqual(right.GetCommittedLog().Take(Math.Min(left.GetCommittedLog().Count, right.GetCommittedLog().Count)))));

    private static bool LogMatching(InMemoryRaftLog<int>[] logs)
    {
        foreach (var left in logs)
        {
            foreach (var right in logs)
            {
                var prefixMatches = true;
                for (var i = 0; i < Math.Min(left.Count, right.Count); i++)
                {
                    prefixMatches &= left.GetEntry(i) == right.GetEntry(i);
                    if (left.GetEntry(i).Term == right.GetEntry(i).Term && !prefixMatches)
                    {
                        return false;
                    }
                }
            }
        }
        return true;
    }

    private sealed class ConstantHash : IHashAlgorithm
    {
        public uint ComputeHash(ReadOnlySpan<byte> data) => 7;
    }
}
