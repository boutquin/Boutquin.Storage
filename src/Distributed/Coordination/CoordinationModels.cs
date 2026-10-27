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

/// <summary>A register value at a committed change revision, optionally attached to a live lease.</summary>
/// <param name="Value">Immutable application text.</param>
/// <param name="Revision">Positive cluster-wide change cursor; use it for compare-and-swap.</param>
/// <param name="LeaseId">Ephemeral ownership, or null for a persistent register.</param>
public sealed record CoordinationValue(string Value, long Revision, long? LeaseId);

/// <summary>A quorum-confirmed read and the change cursor through its committed barrier.</summary>
/// <param name="Value">Current value, or null when absent.</param>
/// <param name="Revision">Cluster-wide cursor, including changes to other paths; suitable for starting a watch.</param>
public sealed record CoordinationRead(CoordinationValue? Value, long Revision);

/// <summary>The atomic outcome of a revision comparison and optional mutation.</summary>
/// <param name="Exchanged">True only when the expected revision matched.</param>
/// <param name="Current">Post-operation value; absent after a successful delete or failed absent comparison.</param>
public sealed record CompareExchangeResult(bool Exchanged, CoordinationValue? Current);

/// <summary>A committed, exclusive lease with a monotonically increasing fencing token.</summary>
/// <param name="Id">Cluster-wide lease identity, equal to its fencing token.</param>
/// <param name="Resource">Exclusive logical resource name.</param>
/// <param name="Owner">Application identity; descriptive, not an authentication principal.</param>
/// <param name="FencingToken">Strictly increasing token for each new successful acquisition.</param>
/// <param name="ExpiresAt">Exclusive deadline on the shared virtual clock; renewal retains identity and token.</param>
/// <remarks>Network delay consumes the lease lifetime. A returned lease may already be near or past its deadline.
/// External resources must reject tokens below their highest accepted token; this service cannot enforce external writes.</remarks>
public sealed record CoordinationLease(long Id, string Resource, string Owner, long FencingToken, long ExpiresAt);

/// <summary>Classifies a committed change delivered to a path watch.</summary>
public enum CoordinationEventKind
{
    /// <summary>A value was created or replaced.</summary>
    Changed,
    /// <summary>An explicit compare-and-swap deleted the value.</summary>
    Deleted,
    /// <summary>Lease expiry or release removed the ephemeral value.</summary>
    LeaseEnded
}

/// <summary>A replayable, totally ordered change to one coordination path.</summary>
/// <param name="Revision">Unique cluster-wide change cursor.</param>
/// <param name="Path">Exact path whose value changed.</param>
/// <param name="Kind">Reason for the change.</param>
/// <param name="Value">New value, or null for removal.</param>
public sealed record CoordinationEvent(long Revision, string Path, CoordinationEventKind Kind, CoordinationValue? Value);

/// <summary>A typed register snapshot; absent values use revision zero.</summary>
/// <typeparam name="TValue">Application value type.</typeparam>
/// <param name="Exists">Whether the key exists.</param>
/// <param name="Value">Decoded value when present.</param>
/// <param name="Revision">Revision for compare-and-swap, or zero when absent.</param>
public sealed record RegisterValue<TValue>(bool Exists, TValue? Value, long Revision);
