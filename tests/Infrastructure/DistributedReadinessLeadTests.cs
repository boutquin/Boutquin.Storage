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
using Boutquin.Storage.Infrastructure.Cdc;
using Boutquin.Storage.Infrastructure.Streaming.Checkpointing;

namespace Boutquin.Storage.Infrastructure.Tests;

public sealed class DistributedReadinessLeadTests
{
    [Fact]
    public async Task CompactedTopic_LossyValue_DoesNotBlameTheValidKeyOrConsumeAnOffset()
    {
        var store = new InMemoryCheckpointStore();
        var topic = new CheckpointedLogCompactedTopic<string, string>(store, "lead");
        await topic.RestoreAsync().ConfigureAwait(true);
        var append = () => topic.AppendAsync("valid-key", "bad\uD800", ChangeOperation.Insert).AsTask();

        var refused = await append.Should().ThrowAsync<ArgumentException>().ConfigureAwait(true);
        refused.Which.ParamName.Should().BeNull("the whole checkpoint comparison can also fail on an older retained row");
        (await topic.AppendAsync("valid-key", "good", ChangeOperation.Insert).ConfigureAwait(true)).Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FreshMembership_RejectsAUsedFollower(bool simulation)
    {
        var state = new InMemoryRaftPersistentState();
        state.SetTermAndVote(7, "former-peer");
        var log = new InMemoryRaftLog<string>();
        log.Append((7, "previous-cluster-command"));
        var restored = new RaftNode<string>("restored", log, state);
        restored.State.Should().Be(RaftNodeState.Follower);

        Action register = simulation
            ? () => new RaftSimulation<string>(new SimulatedNodeRuntime(17)).AddNode(restored)
            : () => new RaftCluster<string>().AddNode(restored);

        register.Should().Throw<InvalidOperationException>(
            "current Follower state does not mean this node has never participated in another membership");
        log.GetEntry(0).Should().Be((7L, "previous-cluster-command"));
        state.CurrentTerm.Should().Be(7);
    }

    [Fact]
    public void InMemoryLog_ImmutableCommandReferences_AreTheDocumentedOwnershipContract()
    {
        var command = new ImmutableCommand("value");
        var log = new InMemoryRaftLog<ImmutableCommand>();
        log.Append((1, command));

        log.GetEntry(0).Command.Should().BeSameAs(command);
        log.GetRange(0, 1)[0].Command.Should().BeSameAs(command);
        log.GetEntry(0).Command.Value.Should().Be("value");
    }

    private sealed record ImmutableCommand(string Value);

    [Fact]
    public void FreshMembership_ExistingFollowerHistoryAlsoClosesRegistration()
    {
        var cluster = new RaftCluster<string>();
        var state = new InMemoryRaftPersistentState();
        cluster.AddNode(new RaftNode<string>("a", persistentState: state));
        state.SetTermAndVote(2, "former-peer");
        var register = () => cluster.AddNode(new RaftNode<string>("b"));
        register.Should().Throw<InvalidOperationException>();
        cluster.GetNodes().Should().ContainSingle();
    }

    [Fact]
    public async Task RestoredMembership_RequiresTheWholeOriginalSet_AndRejectsAnUnlistedNode()
    {
        var original = new[] { "a", "b", "c" };
        var cluster = new RaftCluster<string>(original);
        original[0] = "changed-after-construction";
        var state = new InMemoryRaftPersistentState();
        state.SetTermAndVote(7, "a");
        cluster.AddNode(new RaftNode<string>("a", persistentState: state));

        var electTooSoon = () => cluster.TriggerElectionAsync("a");
        await electTooSoon.Should().ThrowAsync<InvalidOperationException>().ConfigureAwait(true);
        var mixMembership = () => cluster.AddNode(new RaftNode<string>("unlisted"));
        mixMembership.Should().Throw<InvalidOperationException>();
        cluster.GetNodes().Should().ContainSingle();

        cluster.AddNode(new RaftNode<string>("b"));
        cluster.AddNode(new RaftNode<string>("c"));
        await cluster.TriggerElectionAsync("a").ConfigureAwait(true);
        for (var tick = 0; tick < 8; tick++)
        {
            await cluster.TickAsync().ConfigureAwait(true);
        }
        cluster.GetLeader().Should().NotBeNull().And.Match<RaftNode<string>>(n => n.NodeId == "a" && n.CurrentTerm == 8);
        var late = () => cluster.AddNode(new RaftNode<string>("late"));
        late.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void RestoredSimulation_RejectsChangedMembershipAndIncompleteExecution()
    {
        var runtime = new SimulatedNodeRuntime(73);
        var simulation = new RaftSimulation<string>(runtime, ["a", "b"]);
        var state = new InMemoryRaftPersistentState();
        state.SetTermAndVote(3, "a");
        simulation.AddNode(new RaftNode<string>("a", persistentState: state));
        Action incomplete = simulation.Pump;
        incomplete.Should().Throw<InvalidOperationException>();
        var unlisted = () => simulation.AddNode(new RaftNode<string>("c"));
        unlisted.Should().Throw<InvalidOperationException>();
        simulation.AddNode(new RaftNode<string>("b"));
        simulation.Elect("a");
        runtime.RunUntil(10);
        var late = () => simulation.AddNode(new RaftNode<string>("b"));
        late.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FreshMembership_RejectsLogHistoryWithoutATerm(bool simulation)
    {
        var log = new InMemoryRaftLog<string>();
        log.Append((0, "already-appended"));
        var node = new RaftNode<string>("a", log);
        Action register = simulation
            ? () => new RaftSimulation<string>(new SimulatedNodeRuntime(17)).AddNode(node)
            : () => new RaftCluster<string>().AddNode(node);
        register.Should().Throw<InvalidOperationException>();
    }
}
