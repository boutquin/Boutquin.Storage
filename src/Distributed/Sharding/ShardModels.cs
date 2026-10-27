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

/// <summary>Assigns a logical shard to a node.</summary>
public sealed class ShardAssignment
{
    /// <summary>Initializes an assignment.</summary>
    public ShardAssignment(string shardId, string nodeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shardId);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        ShardId = shardId;
        NodeId = nodeId;
    }

    /// <summary>Gets the logical shard identifier.</summary>
    public string ShardId { get; }

    /// <summary>Gets the assigned node identifier.</summary>
    public string NodeId { get; }
}

/// <summary>An immutable, monotonically versioned shard-assignment snapshot.</summary>
public sealed class ShardMapSnapshot
{
    /// <summary>Initializes an immutable snapshot.</summary>
    /// <exception cref="ArgumentException">An entry is null or a shard is assigned more than once.</exception>
    public ShardMapSnapshot(long version, IReadOnlyList<ShardAssignment> assignments)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(version);
        ArgumentNullException.ThrowIfNull(assignments);
        if (assignments.Any(assignment => assignment is null))
        {
            throw new ArgumentException("Assignments cannot contain null entries.", nameof(assignments));
        }

        // Every shard has exactly one owner. Enforcing it here keeps the store from persisting a
        // snapshot that its own loader, and ShardMap, would reject.
        var shardIds = new HashSet<string>(StringComparer.Ordinal);
        var duplicate = assignments.FirstOrDefault(assignment => !shardIds.Add(assignment.ShardId));
        if (duplicate is not null)
        {
            throw new ArgumentException($"Duplicate shard assignment '{duplicate.ShardId}'.", nameof(assignments));
        }

        Version = version;
        Assignments = assignments.ToArray();
    }

    /// <summary>Gets the snapshot version.</summary>
    public long Version { get; }

    /// <summary>Gets the complete assignment set.</summary>
    public IReadOnlyList<ShardAssignment> Assignments { get; }
}

/// <summary>Moves assignment metadata for one shard between nodes.</summary>
public sealed class ShardMove
{
    /// <summary>Initializes a metadata move.</summary>
    public ShardMove(string shardId, string sourceNodeId, string destinationNodeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shardId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceNodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationNodeId);
        ShardId = shardId;
        SourceNodeId = sourceNodeId;
        DestinationNodeId = destinationNodeId;
    }

    /// <summary>Gets the logical shard identifier.</summary>
    public string ShardId { get; }

    /// <summary>Gets the expected source node.</summary>
    public string SourceNodeId { get; }

    /// <summary>Gets the destination node.</summary>
    public string DestinationNodeId { get; }
}

/// <summary>A version-bound set of shard-assignment metadata moves.</summary>
public sealed class RebalancePlan
{
    /// <summary>Initializes an immutable plan.</summary>
    public RebalancePlan(long baseVersion, IReadOnlyList<ShardMove> moves)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(baseVersion);
        ArgumentNullException.ThrowIfNull(moves);
        if (moves.Any(move => move is null))
        {
            throw new ArgumentException("Moves cannot contain null entries.", nameof(moves));
        }

        BaseVersion = baseVersion;
        Moves = moves.ToArray();
    }

    /// <summary>Gets the shard-map version this plan was created from.</summary>
    public long BaseVersion { get; }

    /// <summary>Gets the assignment metadata moves.</summary>
    public IReadOnlyList<ShardMove> Moves { get; }
}

/// <summary>The node route selected from one shard-map version.</summary>
public sealed class ShardRoute
{
    /// <summary>Initializes a route selected from one map version.</summary>
    public ShardRoute(string shardId, string nodeId, long mapVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shardId);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        ArgumentOutOfRangeException.ThrowIfNegative(mapVersion);
        ShardId = shardId;
        NodeId = nodeId;
        MapVersion = mapVersion;
    }

    /// <summary>Gets the logical shard identifier.</summary>
    public string ShardId { get; }

    /// <summary>Gets the assigned node identifier.</summary>
    public string NodeId { get; }

    /// <summary>Gets the shard-map version used for this route.</summary>
    public long MapVersion { get; }
}

/// <summary>A secondary-index entry that retains the source data shard.</summary>
public sealed class ShardedIndexEntry<TKey, TValue>
    where TKey : notnull
{
    /// <summary>Initializes an index entry.</summary>
    public ShardedIndexEntry(string sourceShardId, TKey primaryKey, TValue value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceShardId);
        ArgumentNullException.ThrowIfNull(primaryKey);
        SourceShardId = sourceShardId;
        PrimaryKey = primaryKey;
        Value = value;
    }

    /// <summary>Gets the shard holding the primary record.</summary>
    public string SourceShardId { get; }

    /// <summary>Gets the primary key.</summary>
    public TKey PrimaryKey { get; }

    /// <summary>Gets the indexed value.</summary>
    public TValue Value { get; }
}
