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
namespace Boutquin.Storage.Distributed.Interfaces.Sync;

/// <summary>Exchanges durable deltas between two replicas and materializes converged state.</summary>
public interface ISyncEngine<TChange, TState>
{
    /// <summary>Exchanges each replica's unseen durable changes.</summary>
    /// <returns>A session receipt after both replicas have durably applied the exchange.</returns>
    Task<SyncSession> SynchronizeAsync(
        ISyncReplica<TChange> left,
        ISyncReplica<TChange> right,
        CancellationToken cancellationToken = default);

    /// <summary>Resolves all durable changes currently held by one replica.</summary>
    /// <returns>The deterministically materialized application state.</returns>
    Task<TState> MaterializeAsync(
        ISyncReplica<TChange> replica,
        CancellationToken cancellationToken = default);
}

/// <summary>A durable replica journal with per-peer receive checkpoints.</summary>
public interface ISyncReplica<TChange>
{
    /// <summary>Gets the stable device replica identifier.</summary>
    string ReplicaId { get; }

    /// <summary>Appends and fsyncs a new locally originated change.</summary>
    /// <remarks>Every call allocates a new envelope. Retrying after an uncertain completion may
    /// duplicate a logical operation; reopening preserves sequence allocation, not request deduplication.</remarks>
    /// <returns>The durable change envelope.</returns>
    Task<SyncChange<TChange>> AppendLocalAsync(TChange change, CancellationToken cancellationToken = default);

    /// <summary>Reads journal entries after a source checkpoint.</summary>
    /// <returns>An ordered delta, or an empty delta at the journal tail.</returns>
    Task<ChangeSet<TChange>> ReadChangesAsync(
        SyncCheckpoint checkpoint,
        CancellationToken cancellationToken = default);

    /// <summary>Idempotently applies a peer delta and advances its checkpoint atomically.</summary>
    /// <remarks>A delta carries exactly one change per position from its start to its end, each with a
    /// distinct identifier, as <see cref="ReadChangesAsync"/> produces it; one that does not (too few
    /// changes, or a change repeated in place of another) is rejected before any mutation,
    /// because advancing the checkpoint past undelivered changes would exclude them from every later delta.</remarks>
    /// <returns>The number of previously unseen changes durably appended.</returns>
    Task<int> ApplyAsync(ChangeSet<TChange> changeSet, CancellationToken cancellationToken = default);

    /// <summary>Loads how far this replica has consumed a peer journal.</summary>
    /// <returns>The durable peer checkpoint, starting at position zero.</returns>
    Task<SyncCheckpoint> LoadCheckpointAsync(
        string peerReplicaId,
        CancellationToken cancellationToken = default);

    /// <summary>Reads every unique change held by this replica.</summary>
    /// <returns>A stable journal-order snapshot.</returns>
    Task<IReadOnlyList<SyncChange<TChange>>> ReadAllAsync(CancellationToken cancellationToken = default);
}

/// <summary>Deterministically folds an unordered set of unique changes into application state.</summary>
public interface IConflictResolver<TChange, TState>
{
    /// <summary>Resolves the supplied change envelopes independently of arrival order.</summary>
    /// <returns>The resolved application state.</returns>
    TState Resolve(IReadOnlyList<SyncChange<TChange>> changes);
}

/// <summary>Provides a stable identifier for one device replica across process restarts.</summary>
public interface IDeviceReplicaIdProvider
{
    /// <summary>Loads or creates the stable replica identifier.</summary>
    /// <returns>The non-empty device replica identifier.</returns>
    Task<string> GetReplicaIdAsync(CancellationToken cancellationToken = default);
}
