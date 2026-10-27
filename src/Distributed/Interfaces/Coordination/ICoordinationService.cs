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
using Boutquin.Storage.Distributed.Coordination;

namespace Boutquin.Storage.Distributed.Interfaces.Coordination;

/// <summary>Quorum-committed registers, leases, watches and monotonic sequences in a fixed Raft cluster.</summary>
/// <remarks>In-process reference contract. Mutation request IDs are globally unique: retry the same payload with
/// the same ID after an uncertain outcome. Reusing an ID for different arguments fails. Timeouts and cancellation
/// can occur after admission and do not prove absence of effects. All successful reads use committed barriers.
/// Paths are flat ordinal strings; there is no hierarchical ACL, socket transport, or production clock discipline.</remarks>
public interface ICoordinationService : IDisposable
{
    /// <summary>Reads a value after committing a current-term quorum barrier.</summary>
    /// <param name="path">Nonblank absolute logical path.</param>
    /// <param name="ct">Cancellation; admission may already have occurred when cancellation is observed.</param>
    /// <returns>The value and cursor at the committed barrier.</returns>
    /// <exception cref="ArgumentException">A supplied argument violates its documented constraints.</exception>
    /// <exception cref="InvalidOperationException">The service rejects the request, for example because this node is not leader.</exception>
    /// <exception cref="TimeoutException">No committed response arrives before the virtual deadline; effects may still occur.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed; an admitted operation may still commit.</exception>
    /// <exception cref="ObjectDisposedException">The underlying service has been disposed.</exception>
    /// <exception cref="ArgumentNullException">A required reference argument is null.</exception>
    Task<CoordinationRead> ReadAsync(string path, CancellationToken ct = default);

    /// <summary>Creates or replaces a persistent or ephemeral value after quorum commitment.</summary>
    /// <param name="path">Nonblank absolute logical path.</param>
    /// <param name="value">Non-null application text.</param>
    /// <param name="requestId">Nonblank globally unique mutation identity; reuse only for identical retries.</param>
    /// <param name="leaseId">Active lease identity for an ephemeral value, or null for persistent storage.</param>
    /// <param name="ct">Cancellation with an uncertain outcome after admission.</param>
    /// <returns>The committed value and its revision.</returns>
    /// <exception cref="ArgumentException">A supplied argument violates its documented constraints.</exception>
    /// <exception cref="InvalidOperationException">The service rejects the request, for example because this node is not leader.</exception>
    /// <exception cref="TimeoutException">No committed response arrives before the virtual deadline; effects may still occur.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed; an admitted operation may still commit.</exception>
    /// <exception cref="ObjectDisposedException">The underlying service has been disposed.</exception>
    /// <exception cref="ArgumentNullException">A required reference argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A numeric argument violates its documented bounds.</exception>
    Task<CoordinationValue> SetAsync(string path, string value, string requestId, long? leaseId = null, CancellationToken ct = default);

    /// <summary>Atomically compares the current revision and replaces or deletes the value.</summary>
    /// <param name="path">Nonblank absolute logical path.</param>
    /// <param name="expectedRevision">Expected value revision; zero requires absence.</param>
    /// <param name="value">Replacement text; null deletes a matching value.</param>
    /// <param name="requestId">Unique mutation identity.</param>
    /// <param name="leaseId">Active lease for a replacement; null produces a persistent value.</param>
    /// <param name="ct">Cancellation with an uncertain outcome after admission.</param>
    /// <returns>The committed comparison outcome and post-operation value; a mismatch does not mutate.</returns>
    /// <exception cref="ArgumentException">A supplied argument violates its documented constraints.</exception>
    /// <exception cref="InvalidOperationException">The service rejects the request, for example because this node is not leader.</exception>
    /// <exception cref="TimeoutException">No committed response arrives before the virtual deadline; effects may still occur.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed; an admitted operation may still commit.</exception>
    /// <exception cref="ObjectDisposedException">The underlying service has been disposed.</exception>
    /// <exception cref="ArgumentNullException">A required reference argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A numeric argument violates its documented bounds.</exception>
    Task<CompareExchangeResult> CompareExchangeAsync(string path, long expectedRevision, string? value,
        string requestId, long? leaseId = null, CancellationToken ct = default);

    /// <summary>Starts a replayable local subscription after committing a quorum barrier.</summary>
    /// <param name="path">Exact path to watch.</param>
    /// <param name="afterRevision">Exclusive change cursor; zero replays all retained changes.</param>
    /// <param name="ct">Cancellation for registration, not the resulting subscription lifetime.</param>
    /// <returns>An owned subscription; dispose it and resume on a new leader using the last consumed cursor.</returns>
    /// <exception cref="ArgumentException">A supplied argument violates its documented constraints.</exception>
    /// <exception cref="InvalidOperationException">The service rejects the request, for example because this node is not leader.</exception>
    /// <exception cref="TimeoutException">No committed response arrives before the virtual deadline; effects may still occur.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed; an admitted operation may still commit.</exception>
    /// <exception cref="ObjectDisposedException">The underlying service has been disposed.</exception>
    /// <exception cref="ArgumentNullException">A required reference argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A numeric argument violates its documented bounds.</exception>
    Task<CoordinationWatch> WatchAsync(string path, long afterRevision = 0, CancellationToken ct = default);

    /// <summary>Acquires an unheld resource and allocates a fresh fencing token.</summary>
    /// <param name="resource">Absolute logical resource path.</param>
    /// <param name="owner">Nonblank application owner label.</param>
    /// <param name="durationTicks">Positive duration from the replicated request timestamp.</param>
    /// <param name="requestId">Unique mutation identity.</param>
    /// <param name="ct">Cancellation with an uncertain outcome after admission.</param>
    /// <returns>The committed lease, or null if another active lease already owns the resource.</returns>
    /// <exception cref="ArgumentException">A supplied argument violates its documented constraints.</exception>
    /// <exception cref="InvalidOperationException">The service rejects the request, for example because this node is not leader.</exception>
    /// <exception cref="TimeoutException">No committed response arrives before the virtual deadline; effects may still occur.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed; an admitted operation may still commit.</exception>
    /// <exception cref="ObjectDisposedException">The underlying service has been disposed.</exception>
    /// <exception cref="ArgumentNullException">A required reference argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A numeric argument violates its documented bounds.</exception>
    Task<CoordinationLease?> AcquireLeaseAsync(string resource, string owner, long durationTicks, string requestId, CancellationToken ct = default);

    /// <summary>Extends an active lease without changing its token; expired leases cannot be revived.</summary>
    /// <param name="leaseId">Positive lease identity.</param>
    /// <param name="durationTicks">Positive extension measured from the replicated request timestamp.</param>
    /// <param name="requestId">Unique mutation identity.</param>
    /// <param name="ct">Cancellation with an uncertain outcome after admission.</param>
    /// <returns>True when the lease remained active and was renewed.</returns>
    /// <exception cref="ArgumentException">A supplied argument violates its documented constraints.</exception>
    /// <exception cref="InvalidOperationException">The service rejects the request, for example because this node is not leader.</exception>
    /// <exception cref="TimeoutException">No committed response arrives before the virtual deadline; effects may still occur.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed; an admitted operation may still commit.</exception>
    /// <exception cref="ObjectDisposedException">The underlying service has been disposed.</exception>
    /// <exception cref="ArgumentNullException">A required reference argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A numeric argument violates its documented bounds.</exception>
    Task<bool> RenewLeaseAsync(long leaseId, long durationTicks, string requestId, CancellationToken ct = default);

    /// <summary>Ends an active lease and removes every attached ephemeral register.</summary>
    /// <param name="leaseId">Positive lease identity.</param>
    /// <param name="requestId">Unique mutation identity.</param>
    /// <param name="ct">Cancellation with an uncertain outcome after admission.</param>
    /// <returns>True when an active lease was released.</returns>
    /// <exception cref="ArgumentException">A supplied argument violates its documented constraints.</exception>
    /// <exception cref="InvalidOperationException">The service rejects the request, for example because this node is not leader.</exception>
    /// <exception cref="TimeoutException">No committed response arrives before the virtual deadline; effects may still occur.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed; an admitted operation may still commit.</exception>
    /// <exception cref="ObjectDisposedException">The underlying service has been disposed.</exception>
    /// <exception cref="ArgumentNullException">A required reference argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A numeric argument violates its documented bounds.</exception>
    Task<bool> ReleaseLeaseAsync(long leaseId, string requestId, CancellationToken ct = default);

    /// <summary>Reads resource ownership after a committed barrier, including deterministic expiration.</summary>
    /// <param name="resource">Absolute logical resource path.</param>
    /// <param name="ct">Cancellation with an uncertain outcome after admission.</param>
    /// <returns>The active lease or null.</returns>
    /// <exception cref="ArgumentException">A supplied argument violates its documented constraints.</exception>
    /// <exception cref="InvalidOperationException">The service rejects the request, for example because this node is not leader.</exception>
    /// <exception cref="TimeoutException">No committed response arrives before the virtual deadline; effects may still occur.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed; an admitted operation may still commit.</exception>
    /// <exception cref="ObjectDisposedException">The underlying service has been disposed.</exception>
    /// <exception cref="ArgumentNullException">A required reference argument is null.</exception>
    Task<CoordinationLease?> ReadLeaseAsync(string resource, CancellationToken ct = default);

    /// <summary>Allocates from the shared monotonic sequence after quorum commitment.</summary>
    /// <param name="scope">Nonblank purpose label used to distinguish retry payloads; allocation is global across scopes.</param>
    /// <param name="requestId">Unique mutation identity.</param>
    /// <param name="ct">Cancellation with an uncertain outcome after admission.</param>
    /// <returns>A positive, unique value; identical retries return their original value and gaps are allowed.</returns>
    /// <exception cref="ArgumentException">A supplied argument violates its documented constraints.</exception>
    /// <exception cref="InvalidOperationException">The service rejects the request, for example because this node is not leader.</exception>
    /// <exception cref="TimeoutException">No committed response arrives before the virtual deadline; effects may still occur.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed; an admitted operation may still commit.</exception>
    /// <exception cref="ObjectDisposedException">The underlying service has been disposed.</exception>
    /// <exception cref="ArgumentNullException">A required reference argument is null.</exception>
    Task<long> AllocateSequenceAsync(string scope, string requestId, CancellationToken ct = default);
}
