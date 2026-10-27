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

/// <summary>An externally persisted, versioned shard map.</summary>
public sealed class ShardMap : IShardMap
{
    private readonly IShardAssignmentStore _store;
    private readonly SemaphoreSlim _applyGate = new(1, 1);
    private State _state;

    /// <summary>Initializes a shard map from an already loaded snapshot.</summary>
    public ShardMap(IShardAssignmentStore store, ShardMapSnapshot initialSnapshot)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(initialSnapshot);

        _store = store;
        _state = CreateState(initialSnapshot);
    }

    /// <inheritdoc />
    public ShardMapSnapshot Snapshot => Volatile.Read(ref _state).Snapshot;

    /// <inheritdoc />
    public ShardRoute Resolve(string shardId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shardId);

        var state = Volatile.Read(ref _state);
        if (!state.Assignments.TryGetValue(shardId, out var assignment))
        {
            throw new KeyNotFoundException($"Shard '{shardId}' does not exist in map version {state.Snapshot.Version}.");
        }

        return new ShardRoute(assignment.ShardId, assignment.NodeId, state.Snapshot.Version);
    }

    /// <inheritdoc />
    public async Task<ShardMapSnapshot> ApplyPlanAsync(
        RebalancePlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        await _applyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = Volatile.Read(ref _state);
            if (plan.BaseVersion != current.Snapshot.Version)
            {
                throw new InvalidOperationException(
                    $"Rebalance plan is stale: base version {plan.BaseVersion}, current version {current.Snapshot.Version}.");
            }

            var assignments = current.Assignments.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            var movedShards = new HashSet<string>(StringComparer.Ordinal);
            foreach (var move in plan.Moves)
            {
                ValidateMove(move);
                if (!movedShards.Add(move.ShardId))
                {
                    throw new InvalidOperationException($"Plan moves shard '{move.ShardId}' more than once.");
                }

                if (!assignments.TryGetValue(move.ShardId, out var assignment))
                {
                    throw new KeyNotFoundException($"Shard '{move.ShardId}' does not exist.");
                }

                if (!string.Equals(assignment.NodeId, move.SourceNodeId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Shard '{move.ShardId}' is assigned to '{assignment.NodeId}', not expected source '{move.SourceNodeId}'.");
                }

                if (!string.Equals(move.SourceNodeId, move.DestinationNodeId, StringComparison.Ordinal))
                {
                    assignments[move.ShardId] = new ShardAssignment(move.ShardId, move.DestinationNodeId);
                }
            }

            if (assignments.Values.All(assignment =>
                    current.Assignments[assignment.ShardId].NodeId == assignment.NodeId))
            {
                return current.Snapshot;
            }

            var next = new ShardMapSnapshot(
                current.Snapshot.Version + 1,
                assignments.Values.OrderBy(assignment => assignment.ShardId, StringComparer.Ordinal).ToArray());
            if (!await _store.TrySaveAsync(next, current.Snapshot.Version, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    $"Rebalance plan is stale because assignment version {current.Snapshot.Version} was replaced concurrently.");
            }

            Volatile.Write(ref _state, CreateState(next));
            return next;
        }
        finally
        {
            _applyGate.Release();
        }
    }

    private static State CreateState(ShardMapSnapshot snapshot)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(snapshot.Version);
        ArgumentNullException.ThrowIfNull(snapshot.Assignments);

        var assignments = new Dictionary<string, ShardAssignment>(StringComparer.Ordinal);
        foreach (var assignment in snapshot.Assignments)
        {
            ArgumentNullException.ThrowIfNull(assignment);
            ArgumentException.ThrowIfNullOrWhiteSpace(assignment.ShardId);
            ArgumentException.ThrowIfNullOrWhiteSpace(assignment.NodeId);
            if (!assignments.TryAdd(assignment.ShardId, assignment))
            {
                throw new ArgumentException($"Duplicate shard assignment '{assignment.ShardId}'.", nameof(snapshot));
            }
        }

        var normalized = new ShardMapSnapshot(
            snapshot.Version,
            assignments.Values.OrderBy(assignment => assignment.ShardId, StringComparer.Ordinal).ToArray());
        return new State(normalized, assignments);
    }

    private static void ValidateMove(ShardMove move)
    {
        ArgumentNullException.ThrowIfNull(move);
        ArgumentException.ThrowIfNullOrWhiteSpace(move.ShardId);
        ArgumentException.ThrowIfNullOrWhiteSpace(move.SourceNodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(move.DestinationNodeId);
    }

    private sealed record State(
        ShardMapSnapshot Snapshot,
        IReadOnlyDictionary<string, ShardAssignment> Assignments);
}
