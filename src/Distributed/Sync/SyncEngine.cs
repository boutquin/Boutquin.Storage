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
namespace Boutquin.Storage.Distributed.Sync;

/// <summary>Performs a symmetric delta exchange and delegates state conflicts to a resolver.</summary>
public sealed class SyncEngine<TChange, TState> : ISyncEngine<TChange, TState>
{
    private readonly IConflictResolver<TChange, TState> _resolver;

    /// <summary>Initializes an engine that borrows the supplied deterministic resolver.</summary>
    public SyncEngine(IConflictResolver<TChange, TState> resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _resolver = resolver;
    }

    /// <inheritdoc />
    public async Task<SyncSession> SynchronizeAsync(
        ISyncReplica<TChange> left,
        ISyncReplica<TChange> right,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (string.Equals(left.ReplicaId, right.ReplicaId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Sync peers must have distinct replica identifiers.", nameof(right));
        }

        var leftCheckpoint = await left.LoadCheckpointAsync(right.ReplicaId, cancellationToken).ConfigureAwait(false);
        var rightCheckpoint = await right.LoadCheckpointAsync(left.ReplicaId, cancellationToken).ConfigureAwait(false);

        // Read both deltas before applying either so one exchange never echoes freshly received
        // changes back to their source. Later sessions can still relay changes to third replicas.
        var leftDelta = await left.ReadChangesAsync(rightCheckpoint, cancellationToken).ConfigureAwait(false);
        var rightDelta = await right.ReadChangesAsync(leftCheckpoint, cancellationToken).ConfigureAwait(false);
        // Echoes of the destination's own changes are still delivered: the destination deduplicates them,
        // and comparing them with its journal is the only point that detects a rolled-back journal
        // reissuing an identifier a peer already holds. They are not counted as sent.
        var leftSent = CountForeign(leftDelta, right.ReplicaId);
        var rightSent = CountForeign(rightDelta, left.ReplicaId);
        var appliedToLeft = await left.ApplyAsync(rightDelta, cancellationToken).ConfigureAwait(false);
        var appliedToRight = await right.ApplyAsync(leftDelta, cancellationToken).ConfigureAwait(false);

        return new SyncSession(
            left.ReplicaId,
            right.ReplicaId,
            rightCheckpoint.Position,
            leftCheckpoint.Position,
            leftSent,
            rightSent,
            appliedToLeft,
            appliedToRight,
            new SyncCheckpoint(right.ReplicaId, rightDelta.ToPosition),
            new SyncCheckpoint(left.ReplicaId, leftDelta.ToPosition));
    }

    /// <inheritdoc />
    public async Task<TState> MaterializeAsync(
        ISyncReplica<TChange> replica,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replica);
        var changes = await replica.ReadAllAsync(cancellationToken).ConfigureAwait(false);
        return _resolver.Resolve(changes);
    }

    private static int CountForeign(ChangeSet<TChange> delta, string destinationReplicaId) =>
        delta.Changes.Count(change => !string.Equals(change.OriginReplicaId, destinationReplicaId, StringComparison.Ordinal));
}

/// <summary>Resolves key mutations by timestamp, origin identifier, then origin sequence.</summary>
public sealed class LastWriterWinsConflictResolver<TKey, TValue>
    : IConflictResolver<KeyValueSyncChange<TKey, TValue>, IReadOnlyDictionary<TKey, TValue>>
    where TKey : notnull
{
    /// <inheritdoc />
    public IReadOnlyDictionary<TKey, TValue> Resolve(
        IReadOnlyList<SyncChange<KeyValueSyncChange<TKey, TValue>>> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var winners = changes
            .GroupBy(change => change.Value.Key)
            .Select(group => group
                .OrderByDescending(change => change.Value.LogicalTimestamp)
                .ThenByDescending(change => change.OriginReplicaId, StringComparer.Ordinal)
                .ThenByDescending(change => change.OriginSequence)
                .First())
            .Where(change => !change.Value.IsDeleted)
            .ToDictionary(change => change.Value.Key, change => change.Value.Value!);
        return winners;
    }
}
