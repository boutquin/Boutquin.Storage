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
namespace Boutquin.Storage.Distributed.Coordination;

/// <summary>Opaque immutable state-machine command carried by the existing Raft implementation.</summary>
/// <remarks>Only coordination services construct commands. Values are immutable strings and timestamps
/// use the cluster's shared virtual clock. This in-process payload has no disk or socket wire format.</remarks>
public sealed class CoordinationCommand
{
    internal CoordinationCommand(CoordinationOperation operation, long time, string path = "", string? value = null,
        string? requestId = null, long expectedRevision = 0, long? leaseId = null, string? owner = null, long duration = 0)
    {
        Operation = operation;
        Time = time;
        Path = path;
        Value = value;
        RequestId = requestId;
        ExpectedRevision = expectedRevision;
        LeaseId = leaseId;
        Owner = owner;
        Duration = duration;
    }

    internal CoordinationOperation Operation { get; }
    internal long Time { get; }
    internal string Path { get; }
    internal string? Value { get; }
    internal string? RequestId { get; }
    internal long ExpectedRevision { get; }
    internal long? LeaseId { get; }
    internal string? Owner { get; }
    internal long Duration { get; }

    internal bool SameRequest(CoordinationCommand other) =>
        Operation == other.Operation && Path == other.Path && Value == other.Value &&
        ExpectedRevision == other.ExpectedRevision && LeaseId == other.LeaseId &&
        Owner == other.Owner && Duration == other.Duration;
}

internal enum CoordinationOperation
{
    Read, Set, CompareExchange, AcquireLease, RenewLease, ReleaseLease, ReadLease, Allocate, Expire
}

internal sealed record CoordinationOutcome(object? Value, string? Error = null);
