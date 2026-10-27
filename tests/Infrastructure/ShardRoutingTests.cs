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

public sealed class ShardRoutingTests
{
    private sealed class FixedPartitioner(int partition) : IPartitioner<string>
    {
        public int PartitionCount => partition + 1;

        public int GetPartition(string key) => partition;
    }

    private sealed class MemoryAssignmentStore(ShardMapSnapshot snapshot) : IShardAssignmentStore
    {
        public ShardMapSnapshot Current { get; private set; } = snapshot;

        public Task<ShardMapSnapshot?> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<ShardMapSnapshot?>(Current);

        public Task<bool> TrySaveAsync(
            ShardMapSnapshot next,
            long expectedVersion,
            CancellationToken cancellationToken = default)
        {
            if (Current.Version != expectedVersion)
            {
                return Task.FromResult(false);
            }

            Current = next;
            return Task.FromResult(true);
        }
    }

    private sealed class RejectingAssignmentStore(ShardMapSnapshot snapshot) : IShardAssignmentStore
    {
        public Task<ShardMapSnapshot?> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<ShardMapSnapshot?>(snapshot);

        public Task<bool> TrySaveAsync(
            ShardMapSnapshot next,
            long expectedVersion,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    [Fact]
    public async Task Route_UsesTheCurrentExternalShardMap()
    {
        var snapshot = Snapshot(7, ("shard-0000", "node-a"));
        var store = new MemoryAssignmentStore(snapshot);
        var map = new ShardMap(store, snapshot);
        var router = new ShardRouter<string>(new FixedPartitioner(0), map);

        router.Route("customer-42").Should().BeEquivalentTo(
            new ShardRoute("shard-0000", "node-a", 7));

        await map.ApplyPlanAsync(new RebalancePlan(7,
            [new ShardMove("shard-0000", "node-a", "node-b")]));

        router.Route("customer-42").Should().BeEquivalentTo(
            new ShardRoute("shard-0000", "node-b", 8));
    }

    [Fact]
    public async Task ApplyPlanAsync_RejectsAStalePlanWithoutChangingAssignments()
    {
        var snapshot = Snapshot(3, ("shard-0000", "node-a"));
        var map = new ShardMap(new MemoryAssignmentStore(snapshot), snapshot);
        var stale = new RebalancePlan(2,
            [new ShardMove("shard-0000", "node-a", "node-b")]);

        var act = () => map.ApplyPlanAsync(stale);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*stale*");
        map.Resolve("shard-0000").NodeId.Should().Be("node-a");
        map.Snapshot.Version.Should().Be(3);
    }

    [Fact]
    public void Resolve_RejectsAnUnknownShard()
    {
        var snapshot = Snapshot(0, ("shard-0000", "node-a"));
        var map = new ShardMap(new MemoryAssignmentStore(snapshot), snapshot);

        var act = () => map.Resolve("shard-9999");

        act.Should().Throw<KeyNotFoundException>();
    }

    [Fact]
    public void Snapshot_AndPlan_CopyTheirCallerOwnedCollections()
    {
        var assignments = new[] { new ShardAssignment("shard-0000", "node-a") };
        var moves = new[] { new ShardMove("shard-0000", "node-a", "node-b") };
        var snapshot = new ShardMapSnapshot(0, assignments);
        var plan = new RebalancePlan(0, moves);

        assignments[0] = new ShardAssignment("shard-9999", "node-z");
        moves[0] = new ShardMove("shard-9999", "node-z", "node-y");

        snapshot.Assignments[0].ShardId.Should().Be("shard-0000");
        plan.Moves[0].ShardId.Should().Be("shard-0000");
    }

    [Fact]
    public async Task ApplyPlanAsync_DoesNotPublishWhenPersistenceLosesTheVersionRace()
    {
        var snapshot = Snapshot(3, ("shard-0000", "node-a"));
        var map = new ShardMap(new RejectingAssignmentStore(snapshot), snapshot);
        var plan = new RebalancePlan(3,
            [new ShardMove("shard-0000", "node-a", "node-b")]);

        var act = () => map.ApplyPlanAsync(plan);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*stale*");
        map.Resolve("shard-0000").Should().BeEquivalentTo(
            new ShardRoute("shard-0000", "node-a", 3));
    }

    private static ShardMapSnapshot Snapshot(long version, params (string Shard, string Node)[] assignments) =>
        new(version, assignments.Select(x => new ShardAssignment(x.Shard, x.Node)).ToArray());
}
