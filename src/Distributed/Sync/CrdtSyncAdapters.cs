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
using Boutquin.Storage.Distributed.DistributedSystems;

namespace Boutquin.Storage.Distributed.Sync;

/// <summary>Merges existing grow-only counter states carried as sync changes.</summary>
public sealed class GCounterSyncAdapter : IConflictResolver<GCounter, long>
{
    /// <inheritdoc />
    public long Resolve(IReadOnlyList<SyncChange<GCounter>> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        GCounter merged = new();
        foreach (var change in changes.OrderBy(change => change.ChangeId, StringComparer.Ordinal))
        {
            merged = (GCounter)merged.Merge(change.Value);
        }

        return merged.Value;
    }
}

/// <summary>Merges existing observed-remove set states carried as sync changes.</summary>
public sealed class ORSetSyncAdapter<T> : IConflictResolver<ORSet<T>, IReadOnlySet<T>>
    where T : notnull
{
    /// <inheritdoc />
    public IReadOnlySet<T> Resolve(IReadOnlyList<SyncChange<ORSet<T>>> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ORSet<T> merged = new();
        foreach (var change in changes.OrderBy(change => change.ChangeId, StringComparer.Ordinal))
        {
            merged = (ORSet<T>)merged.Merge(change.Value);
        }

        return merged.Value;
    }
}
