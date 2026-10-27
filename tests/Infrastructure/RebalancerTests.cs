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

public sealed class RebalancerTests
{
    [Fact]
    public void ManualRebalancer_EmitsOnlyRequestedChanges()
    {
        var snapshot = Snapshot(11, 2, 2);
        var requested = new[]
        {
            new ShardMove("shard-0001", "node-a", "node-b"),
            new ShardMove("shard-0002", "node-b", "node-b"),
        };

        var plan = new ManualRebalancer(requested).CreatePlan(snapshot);

        plan.BaseVersion.Should().Be(11);
        plan.Moves.Should().ContainSingle().Which.Should().BeEquivalentTo(requested[0]);
    }

    [Fact]
    public void ManualRebalancer_RejectsAnIncorrectSourceAssignment()
    {
        var snapshot = Snapshot(1, 1, 1);
        var rebalancer = new ManualRebalancer(
            [new ShardMove("shard-0000", "node-z", "node-b")]);

        var act = () => rebalancer.CreatePlan(snapshot);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void AutomaticRebalancer_AddingNodeMovesTheMinimumShardCount()
    {
        var snapshot = Snapshot(4, 4, 4);

        var plan = new AutomaticRebalancer(["node-a", "node-b", "node-c"])
            .CreatePlan(snapshot);

        plan.Moves.Should().HaveCount(2);
        plan.Moves.Should().OnlyContain(move => move.DestinationNodeId == "node-c");
    }

    [Fact]
    public void AutomaticRebalancer_RemovingNodeMovesOnlyItsShards()
    {
        var snapshot = Snapshot(5, 3, 3, 3);

        var plan = new AutomaticRebalancer(["node-a", "node-c"])
            .CreatePlan(snapshot);

        plan.Moves.Should().HaveCount(3);
        plan.Moves.Should().OnlyContain(move => move.SourceNodeId == "node-b");
    }

    [Fact]
    public void AutomaticRebalancer_SeededTopologiesAreBalancedAfterApplyingPlan()
    {
        var random = new Random(20_260_917);
        for (var iteration = 0; iteration < 100; iteration++)
        {
            var targetNodes = Enumerable.Range(0, random.Next(1, 8)).Select(i => $"node-{i}").ToArray();
            var assignments = Enumerable.Range(0, random.Next(1, 80))
                .Select(i => new ShardAssignment($"shard-{i:D4}", $"node-{random.Next(0, 10)}"))
                .ToArray();
            var snapshot = new ShardMapSnapshot(iteration, assignments);

            var plan = new AutomaticRebalancer(targetNodes).CreatePlan(snapshot);
            var final = assignments.ToDictionary(x => x.ShardId, x => x.NodeId, StringComparer.Ordinal);
            foreach (var move in plan.Moves)
            {
                final[move.ShardId] = move.DestinationNodeId;
            }

            var counts = targetNodes.Select(node => final.Values.Count(value => value == node)).ToArray();
            counts.Max().Should().BeLessThanOrEqualTo(counts.Min() + 1);
            final.Values.Should().OnlyContain(value => targetNodes.Contains(value));
        }
    }

    private static ShardMapSnapshot Snapshot(long version, params int[] shardsPerNode)
    {
        var assignments = new List<ShardAssignment>();
        var shard = 0;
        for (var node = 0; node < shardsPerNode.Length; node++)
        {
            for (var count = 0; count < shardsPerNode[node]; count++)
            {
                assignments.Add(new ShardAssignment($"shard-{shard++:D4}", $"node-{(char)('a' + node)}"));
            }
        }

        return new ShardMapSnapshot(version, assignments);
    }
}
