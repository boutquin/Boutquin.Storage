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

/// <summary>Persists immutable shard-assignment snapshots with optimistic concurrency.</summary>
public interface IShardAssignmentStore
{
    /// <summary>Loads the latest snapshot, or <see langword="null"/> when none exists.</summary>
    Task<ShardMapSnapshot?> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Saves <paramref name="snapshot"/> only when the stored version equals <paramref name="expectedVersion"/>.</summary>
    Task<bool> TrySaveAsync(
        ShardMapSnapshot snapshot,
        long expectedVersion,
        CancellationToken cancellationToken = default);
}
