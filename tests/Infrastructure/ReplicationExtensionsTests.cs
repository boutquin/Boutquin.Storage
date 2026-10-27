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
using Boutquin.Storage.Distributed.Interfaces.Replication;
using Boutquin.Storage.Distributed.Simulation;

namespace Boutquin.Storage.Infrastructure.Tests;

public sealed class ReplicationExtensionsTests
{
    [Fact]
    public void MultiLeader_ConcurrentSiblingsConvergeWithoutErasingConflicts()
    {
        var replicas = Create();
        replicas.Write("a", "x", 1);
        replicas.Write("b", "x", 2);
        Exchange(replicas);
        replicas.Read("a", "x").Values.Should().Equal(1, 2);
        replicas.Read("b", "x").Values.Should().Equal(1, 2);
        var session = replicas.Read("a", "x").Session;
        replicas.Write("a", "x", 3, session);
        Exchange(replicas);
        replicas.Read("b", "x").Values.Should().Equal(3);
    }

    [Fact]
    public void Session_ReadYourWritesAndMonotonicReadsRejectLag()
    {
        var replicas = Create();
        var written = replicas.Write("a", "x", 1);
        Action stale = () => replicas.Read("b", "x", written);
        stale.Should().Throw<SessionConsistencyException>();
        Exchange(replicas);
        var read = replicas.Read("b", "x", written);
        replicas.Write("b", "y", 2, read.Session);
        var later = replicas.Read("b", "y").Session;
        Action regressed = () => replicas.Read("a", "x", later);
        regressed.Should().Throw<SessionConsistencyException>();
    }

    [Fact]
    public void Session_MonotonicWritesAndWriteFollowsReadsRequireDependencies()
    {
        var replicas = Create();
        var first = replicas.Write("a", "x", 1);
        Action reorderedWrite = () => replicas.Write("b", "y", 2, first);
        reorderedWrite.Should().Throw<SessionConsistencyException>();
        Exchange(replicas);
        var observed = replicas.Read("b", "x", first).Session;
        replicas.Write("b", "y", 2, observed);
        var dependent = replicas.Capture("b").Single(op => op.Version.Origin == "b");
        replicas.Deliver("c", dependent);
        replicas.Read("c", "y").Values.Should().BeEmpty();
        foreach (var op in replicas.Capture("a"))
        {
            replicas.Deliver("c", op);
        }

        replicas.Read("c", "x").Values.Should().Equal(1);
        replicas.Read("c", "y").Values.Should().Equal(2);
    }

    [Fact]
    public void Resolvers_AreDeterministicAcrossDeliveryOrder()
    {
        var lww = new MultiLeaderReplicaSet<string, int>(["a", "b"], new LastWriteWinsResolver<int>());
        lww.Write("a", "x", 1);
        lww.Write("b", "x", 2);
        Exchange(lww);
        lww.Read("a", "x").Values.Should().Equal(2);
        var merge = new MultiLeaderReplicaSet<string, int>(["a", "b"], new MergeFunctionResolver<int>(values => values.Sum()));
        merge.Write("a", "x", 1);
        merge.Write("b", "x", 2);
        Exchange(merge);
        merge.Read("a", "x").Values.Should().Equal(3);
        merge.Read("b", "x").Values.Should().Equal(3);
    }

    [Fact]
    public void SloppyQuorum_RetainsHintsUntilBothEndpointsRecover()
    {
        var replicas = Leaderless();
        replicas.SetAvailability("a", false);
        replicas.SetAvailability("b", false);
        replicas.Write("x", 7, ReplicationConsistency.Quorum, sloppy: true);
        replicas.PendingHintCount.Should().Be(1);
        replicas.SetAvailability("d", false);
        replicas.SetAvailability("a", true);
        replicas.Handoff().Should().Be(0);
        replicas.SetAvailability("d", true);
        replicas.Handoff().Should().Be(1);
        replicas.PendingHintCount.Should().Be(0);
        replicas.ReadReplica("a", "x").Values.Should().Equal(7);
    }

    [Fact]
    public void Leaderless_ReadRepairAndMerkleRepairConvergeColdKeys()
    {
        var replicas = Leaderless();
        replicas.Write("hot", 1, ReplicationConsistency.One);
        replicas.Write("cold", 2, ReplicationConsistency.One);
        replicas.ReadReplica("b", "hot").Values.Should().BeEmpty();
        replicas.Read("hot", ReplicationConsistency.Quorum).Values.Should().Equal(1);
        replicas.ReadReplica("b", "hot").Values.Should().Equal(1);
        replicas.Repair("a", "c").Should().BeGreaterThan(0);
        replicas.ReadReplica("c", "cold").Values.Should().Equal(2);
        replicas.Repair("a", "c").Should().Be(0);
    }

    [Fact]
    public void StrictQuorum_RejectsBeforeMutatingAndSessionsRejectForeignClusters()
    {
        var replicas = Leaderless();
        replicas.SetAvailability("a", false);
        replicas.SetAvailability("b", false);
        Action write = () => replicas.Write("x", 1, ReplicationConsistency.Quorum);
        write.Should().Throw<QuorumNotMetException>();
        replicas.ReadReplica("c", "x").Values.Should().BeEmpty();
        var token = Create().Write("a", "x", 1);
        Action foreign = () => Create().Read("a", "x", token);
        foreign.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(1U)]
    [InlineData(20260917U)]
    [InlineData(uint.MaxValue)]
    public void SeededFaults_DuplicatesReorderingPauseAndPartitionsConverge(uint seed)
    {
        var replicas = Create();
        var runtime = new SimulatedNodeRuntime(seed, new FaultInjectionPolicy(.25, .5, 5));
        foreach (var node in new[] { "a", "b", "c" })
        {
            runtime.AddNode(node, message => replicas.Deliver(node, (ReplicationOperation<string, int>)message));
        }
        runtime.Network.SetPartition("a", "b", true);
        runtime.Pause("c");
        for (var i = 0; i < 20; i++)
        {
            var node = i % 2 == 0 ? "a" : "b";
            replicas.Write(node, "x", i);
            foreach (var target in new[] { "a", "b", "c" })
            {
                foreach (var op in replicas.Capture(node))
                {
                    runtime.Network.Send(node, target, op);
                }
            }

            runtime.RunUntil(i * 10 + 10);
        }
        runtime.Network.SetPartition("a", "b", false);
        runtime.Resume("c");
        runtime.Network.Policy = new FaultInjectionPolicy(0, 1, 7);
        foreach (var node in new[] { "a", "b", "c" })
        {
            foreach (var target in new[] { "a", "b", "c" })
            {
                foreach (var op in replicas.Capture(node))
                {
                    runtime.Network.Send(node, target, op);
                }
            }
        }

        runtime.RunUntil(300);
        replicas.Read("a", "x").Values.Should().Equal(replicas.Read("b", "x").Values);
        replicas.Read("c", "x").Values.Should().Equal(replicas.Read("b", "x").Values);
        replicas.Capture("c").Should().HaveCount(20);
    }

    [Fact]
    public void DuplicateAndForeignDelivery_EnforceIdentityAndScope()
    {
        var replicas = Create();
        replicas.Write("a", "x", 1);
        var operation = replicas.Capture("a").Single();
        replicas.Deliver("b", operation);
        replicas.Deliver("b", operation);
        replicas.Capture("b").Should().HaveCount(1);
        Action foreign = () => Create().Deliver("a", operation);
        foreign.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void TokenSnapshot_CannotBeMutatedAndDoesNotAdvanceWithReplica()
    {
        var replicas = Create();
        var first = replicas.Write("a", "x", 1);
        replicas.Write("a", "x", 2);
        first.Counters["a"].Should().Be(1);
        Action mutate = () => ((IDictionary<string, long>)first.Counters)["a"] = 100;
        mutate.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void CausalOverwrite_WinsDespiteOriginTieBreakAndOldRedelivery()
    {
        var replicas = new MultiLeaderReplicaSet<string, int>(["a", "z"], new LastWriteWinsResolver<int>());
        replicas.Write("z", "x", 1);
        var old = replicas.Capture("z").Single();
        replicas.Deliver("a", old);
        replicas.Write("a", "x", 2, replicas.Read("a", "x").Session);
        foreach (var operation in replicas.Capture("a"))
        {
            replicas.Deliver("z", operation);
        }

        replicas.Deliver("z", old);
        replicas.Read("z", "x").Values.Should().Equal(2);
    }

    [Fact]
    public void TunableConsistency_OneCanBeStaleWhileSessionRejectsAndQuorumRepairs()
    {
        var replicas = Leaderless();
        var token = replicas.Write("x", 8, ReplicationConsistency.One);
        replicas.SetAvailability("a", false);
        replicas.Read("x", ReplicationConsistency.One).Values.Should().BeEmpty();
        Action stale = () => replicas.Read("x", ReplicationConsistency.One, session: token);
        stale.Should().Throw<SessionConsistencyException>();
        Action reordered = () => replicas.Write("y", 9, ReplicationConsistency.Quorum, session: token);
        reordered.Should().Throw<SessionConsistencyException>();
        replicas.ReadReplica("b", "y").Values.Should().BeEmpty();
        replicas.SetAvailability("a", true);
        replicas.Read("x", ReplicationConsistency.All, session: token).Values.Should().Equal(8);
        replicas.ReadReplica("c", "x", token).Values.Should().Equal(8);
    }

    [Fact]
    public void SloppyQuorum_CanMissSuccessfulWriteUntilHintDelivery()
    {
        var replicas = Leaderless();
        replicas.SetAvailability("a", false);
        replicas.SetAvailability("b", false);
        var token = replicas.Write("x", 9, ReplicationConsistency.Quorum, sloppy: true);
        replicas.SetAvailability("a", true);
        replicas.SetAvailability("b", true);
        replicas.SetAvailability("c", false);
        replicas.Read("x", ReplicationConsistency.Quorum).Values.Should().BeEmpty();
        Action stale = () => replicas.Read("x", ReplicationConsistency.Quorum, session: token);
        stale.Should().Throw<SessionConsistencyException>();
        replicas.Handoff().Should().Be(1);
        replicas.Read("x", ReplicationConsistency.Quorum, session: token).Values.Should().Equal(9);
    }

    [Fact]
    public void MerkleRepair_IsBidirectionalAndPreservesConcurrentSiblings()
    {
        var replicas = Leaderless();
        replicas.Write("x", 1, ReplicationConsistency.One);
        replicas.SetAvailability("a", false);
        replicas.Write("x", 2, ReplicationConsistency.One);
        replicas.SetAvailability("a", true);
        replicas.Repair("a", "b").Should().Be(2);
        replicas.ReadReplica("a", "x").Values.Should().Equal(1, 2);
        replicas.ReadReplica("b", "x").Values.Should().Equal(1, 2);
        replicas.Repair("a", "b").Should().Be(0);
    }

    [Theory]
    [InlineData(2U)]
    [InlineData(7919U)]
    [InlineData(314159U)]
    [InlineData(4294967294U)]
    public void SeededLeaderlessPartitions_SessionRetriesPreserveCausalChain(uint seed)
    {
        var replicas = Leaderless();
        var random = new PropertyTestGenerators(seed);
        var runtime = new SimulatedNodeRuntime(seed);
        runtime.AddNode("client", _ => { });
        var token = SessionToken.Empty;
        var rejected = 0;
        for (var step = 1; step <= 24; step++)
        {
            var value = step;
            runtime.Schedule("client", step, () =>
            {
                foreach (var node in new[] { "a", "b", "c", "d", "e" })
                {
                    replicas.SetAvailability(node, value != 1 && random.Next(3) != 0);
                }

                var previous = token;
                try
                {
                    token = replicas.Write("x", value, ReplicationConsistency.Quorum, sloppy: true, session: token);
                }
                catch (Exception error) when (error is SessionConsistencyException or QuorumNotMetException)
                {
                    rejected++;
                    Heal();
                    token = replicas.Write("x", value, ReplicationConsistency.Quorum, session: token);
                }
                token.Covers(previous).Should().BeTrue();
            });
        }
        runtime.RunUntil(30);
        Heal();
        rejected.Should().BeGreaterThan(0);
        replicas.ReadReplica("e", "x", token).Values.Should().Equal(24);
        void Heal()
        {
            foreach (var node in new[] { "a", "b", "c", "d", "e" })
            {
                replicas.SetAvailability(node, true);
            }

            replicas.Handoff();
            foreach (var node in new[] { "b", "c", "d", "e" })
            {
                replicas.Repair("a", node);
            }

            foreach (var node in new[] { "b", "c", "d", "e" })
            {
                replicas.Repair("a", node);
            }
        }
    }

    [Fact]
    public void InvalidMembershipAndConsistency_AreRejected()
    {
        Action duplicate = () => new MultiLeaderReplicaSet<string, int>(["a", "a"], new VersionVectorResolver<int>());
        duplicate.Should().Throw<ArgumentException>();
        Action overlap = () => new LeaderlessReplicaSet<string, int>(["a"], ["a"], new VersionVectorResolver<int>());
        overlap.Should().Throw<ArgumentException>();
        var replicas = Leaderless();
        Action invalid = () => replicas.Read("x", (ReplicationConsistency)99);
        invalid.Should().Throw<ArgumentOutOfRangeException>();
        Action unknown = () => replicas.SetAvailability("unknown", true);
        unknown.Should().Throw<KeyNotFoundException>();
    }

    [Fact]
    public void Resolvers_RejectMixedReplicaScopesEvenWhenWriteIdentitiesCollide()
    {
        var first = Create();
        var second = Create();
        first.Write("a", "x", 1);
        second.Write("a", "x", 2);
        var versions = new[] { first.Capture("a").Single().Version, second.Capture("a").Single().Version };
        IConflictResolver<int>[] resolvers = [new VersionVectorResolver<int>(), new LastWriteWinsResolver<int>(), new MergeFunctionResolver<int>(values => values.Sum())];
        foreach (var resolver in resolvers)
        {
            Action mixed = () => resolver.Resolve(versions);
            mixed.Should().Throw<ArgumentException>();
        }
    }

    [Fact]
    public void AllResponses_SloppySubstitutionRetainsEveryHintAndStrictReadRejects()
    {
        var replicas = Leaderless();
        replicas.SetAvailability("a", false);
        replicas.SetAvailability("b", false);
        var token = replicas.Write("x", 6, ReplicationConsistency.All, sloppy: true);
        replicas.PendingHintCount.Should().Be(2);
        Action strict = () => replicas.Read("x", ReplicationConsistency.All);
        strict.Should().Throw<QuorumNotMetException>();
        replicas.SetAvailability("a", true);
        replicas.SetAvailability("b", true);
        replicas.Handoff().Should().Be(2);
        replicas.Handoff().Should().Be(0);
        replicas.Read("x", ReplicationConsistency.All, session: token).Values.Should().Equal(6);
    }

    [Fact]
    public void ConcurrentCallsAndReversedDelivery_PreserveEveryOriginSequence()
    {
        var replicas = Create();
        Parallel.For(0, 40, value => replicas.Write("a", "x", value));
        var operations = replicas.Capture("a");
        operations.Select(operation => operation.Version.Sequence).Should().Equal(Enumerable.Range(1, 40).Select(value => (long)value));
        foreach (var operation in operations.Reverse())
        {
            replicas.Deliver("b", operation);
        }
        replicas.Read("b", "x").Values.Should().Equal(replicas.Read("a", "x").Values);
        replicas.Capture("b").Should().HaveCount(40);
    }

    private static MultiLeaderReplicaSet<string, int> Create() => new(["a", "b", "c"], new VersionVectorResolver<int>());
    private static LeaderlessReplicaSet<string, int> Leaderless() => new(["a", "b", "c"], ["d", "e"], new VersionVectorResolver<int>());
    private static void Exchange(MultiLeaderReplicaSet<string, int> replicas)
    {
        foreach (var node in new[] { "a", "b" })
        {
            foreach (var target in new[] { "a", "b" })
            {
                foreach (var op in replicas.Capture(node))
                {
                    replicas.Deliver(target, op);
                }
            }
        }
    }
}
