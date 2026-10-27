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
namespace Boutquin.Storage.Distributed.Sharding;

/// <summary>Turns an explicit set of requested moves into a versioned plan.</summary>
public sealed class ManualRebalancer : IRebalancer
{
    private readonly IReadOnlyList<ShardMove> _requestedMoves;

    /// <summary>Initializes a manual rebalancer.</summary>
    public ManualRebalancer(IReadOnlyList<ShardMove> requestedMoves)
    {
        ArgumentNullException.ThrowIfNull(requestedMoves);
        _requestedMoves = requestedMoves.ToArray();
    }

    /// <inheritdoc />
    public RebalancePlan CreatePlan(ShardMapSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var assignments = snapshot.Assignments.ToDictionary(
            assignment => assignment.ShardId,
            assignment => assignment.NodeId,
            StringComparer.Ordinal);
        var planned = new HashSet<string>(StringComparer.Ordinal);
        var moves = new List<ShardMove>();

        foreach (var move in _requestedMoves)
        {
            ArgumentNullException.ThrowIfNull(move);
            ArgumentException.ThrowIfNullOrWhiteSpace(move.ShardId);
            ArgumentException.ThrowIfNullOrWhiteSpace(move.SourceNodeId);
            ArgumentException.ThrowIfNullOrWhiteSpace(move.DestinationNodeId);
            if (!planned.Add(move.ShardId))
            {
                throw new InvalidOperationException($"Shard '{move.ShardId}' is listed more than once.");
            }

            if (!assignments.TryGetValue(move.ShardId, out var currentNode))
            {
                throw new KeyNotFoundException($"Shard '{move.ShardId}' does not exist.");
            }

            if (!string.Equals(currentNode, move.SourceNodeId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Shard '{move.ShardId}' is assigned to '{currentNode}', not '{move.SourceNodeId}'.");
            }

            if (!string.Equals(move.SourceNodeId, move.DestinationNodeId, StringComparison.Ordinal))
            {
                moves.Add(move);
            }
        }

        return new RebalancePlan(snapshot.Version, moves);
    }
}

/// <summary>Balances fixed logical shards over a configured node set with minimal movement.</summary>
public sealed class AutomaticRebalancer : IRebalancer
{
    private readonly string[] _targetNodeIds;

    /// <summary>Initializes an automatic rebalancer.</summary>
    public AutomaticRebalancer(IReadOnlyCollection<string> targetNodeIds)
    {
        ArgumentNullException.ThrowIfNull(targetNodeIds);
        if (targetNodeIds.Count == 0)
        {
            throw new ArgumentException("At least one target node is required.", nameof(targetNodeIds));
        }

        if (targetNodeIds.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Target node identifiers cannot be blank.", nameof(targetNodeIds));
        }

        _targetNodeIds = targetNodeIds.Order(StringComparer.Ordinal).ToArray();
        if (_targetNodeIds.Distinct(StringComparer.Ordinal).Count() != _targetNodeIds.Length)
        {
            throw new ArgumentException("Target node identifiers must be unique.", nameof(targetNodeIds));
        }
    }

    /// <inheritdoc />
    public RebalancePlan CreatePlan(ShardMapSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(snapshot.Assignments);

        var assignments = snapshot.Assignments
            .OrderBy(assignment => assignment.ShardId, StringComparer.Ordinal)
            .ToArray();
        if (assignments.Select(assignment => assignment.ShardId).Distinct(StringComparer.Ordinal).Count()
            != assignments.Length)
        {
            throw new ArgumentException("Shard identifiers must be unique.", nameof(snapshot));
        }

        var baseCapacity = assignments.Length / _targetNodeIds.Length;
        var remainder = assignments.Length % _targetNodeIds.Length;
        var currentCounts = _targetNodeIds.ToDictionary(
            node => node,
            node => assignments.Count(assignment =>
                string.Equals(assignment.NodeId, node, StringComparison.Ordinal)),
            StringComparer.Ordinal);

        // Extra capacity goes first to nodes that can retain an additional existing shard.
        // That maximizes retained assignments, which minimizes the number of moves.
        var extraCapacityOrder = _targetNodeIds
            .OrderByDescending(node => currentCounts[node] > baseCapacity)
            .ThenByDescending(node => currentCounts[node])
            .ThenBy(node => node, StringComparer.Ordinal)
            .ToArray();
        var capacity = _targetNodeIds.ToDictionary(node => node, _ => baseCapacity, StringComparer.Ordinal);
        for (var i = 0; i < remainder; i++)
        {
            capacity[extraCapacityOrder[i]]++;
        }

        var targetSet = _targetNodeIds.ToHashSet(StringComparer.Ordinal);
        var displaced = new List<ShardAssignment>();
        var retainedCount = _targetNodeIds.ToDictionary(node => node, _ => 0, StringComparer.Ordinal);
        foreach (var group in assignments.GroupBy(assignment => assignment.NodeId, StringComparer.Ordinal))
        {
            var ordered = group.OrderBy(assignment => assignment.ShardId, StringComparer.Ordinal).ToArray();
            if (!targetSet.Contains(group.Key))
            {
                displaced.AddRange(ordered);
                continue;
            }

            var keepCount = Math.Min(ordered.Length, capacity[group.Key]);
            retainedCount[group.Key] = keepCount;
            displaced.AddRange(ordered.Skip(keepCount));
        }

        var destinations = _targetNodeIds
            .SelectMany(node => Enumerable.Repeat(node, capacity[node] - retainedCount[node]))
            .ToArray();
        if (destinations.Length != displaced.Count)
        {
            throw new InvalidOperationException("Rebalancing capacity calculation did not conserve shards.");
        }

        var moves = displaced
            .OrderBy(assignment => assignment.ShardId, StringComparer.Ordinal)
            .Zip(destinations, (assignment, destination) =>
                new ShardMove(assignment.ShardId, assignment.NodeId, destination))
            .ToArray();
        return new RebalancePlan(snapshot.Version, moves);
    }
}
