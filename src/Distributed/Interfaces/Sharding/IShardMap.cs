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
namespace Boutquin.Storage.Distributed.Interfaces.Sharding;

/// <summary>Resolves logical shards to nodes from versioned assignment metadata.</summary>
public interface IShardMap
{
    /// <summary>Gets the current immutable assignment snapshot.</summary>
    ShardMapSnapshot Snapshot { get; }

    /// <summary>Resolves a logical shard identifier.</summary>
    ShardRoute Resolve(string shardId);

    /// <summary>
    /// Atomically applies assignment metadata when the plan's base version is current.
    /// This operation does not copy, stream, or delete shard data.
    /// </summary>
    Task<ShardMapSnapshot> ApplyPlanAsync(
        RebalancePlan plan,
        CancellationToken cancellationToken = default);
}
