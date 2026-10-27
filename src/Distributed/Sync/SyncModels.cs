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

/// <summary>A globally unique change and its origin sequence.</summary>
/// <remarks>The identifier is the origin identifier, a colon, and the positive origin sequence
/// formatted with invariant culture. Replicas reject envelopes that violate this relationship.</remarks>
public sealed record SyncChange<TChange>(string ChangeId, string OriginReplicaId, long OriginSequence, TChange Value);

/// <summary>An ordered slice of one replica's durable journal.</summary>
public sealed record ChangeSet<TChange>(
    string SourceReplicaId,
    long FromPosition,
    long ToPosition,
    IReadOnlyList<SyncChange<TChange>> Changes);

/// <summary>A durable next-read position in one source replica's journal.</summary>
public sealed record SyncCheckpoint(string ReplicaId, long Position);

/// <summary>Records the durable effects of one bidirectional synchronization.</summary>
public sealed record SyncSession(
    string LeftReplicaId,
    string RightReplicaId,
    long LeftReadFrom,
    long RightReadFrom,
    int LeftSent,
    int RightSent,
    int AppliedToLeft,
    int AppliedToRight,
    SyncCheckpoint LeftCheckpointForRight,
    SyncCheckpoint RightCheckpointForLeft);

/// <summary>A timestamped key mutation for deterministic last-writer-wins materialization.</summary>
public sealed record KeyValueSyncChange<TKey, TValue>(
    TKey Key,
    TValue? Value,
    long LogicalTimestamp,
    bool IsDeleted)
    where TKey : notnull;
