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
using Boutquin.Storage.Distributed.Transactions;
namespace Boutquin.Storage.Distributed.Interfaces.Transactions;

/// <summary>A durable two-phase participant retaining prepared locks until an authoritative final decision.</summary>
/// <remarks>Commit/abort callers must be the sole owning coordinator. Cancellation or I/O failure can leave a durable effect;
/// reopen and retry the same ID and intent. Prepared participants must never time out and unilaterally abort. A coordinator
/// calls one participant one request at a time but may call distinct participants concurrently.</remarks>
public interface ITransactionalResource
{
    /// <summary>Stable ordinal participant identity, unchanged across recovery.</summary>
    string ResourceId { get; }
    /// <summary>Durably votes and retains write-key locks without publishing changes.</summary>
    /// <param name="id">Stable transaction identity.</param><param name="writes">Nonblank keys and replacement text; null deletes.</param><param name="ct">Cancellation token; an interrupted vote may already be durable.</param>
    /// <returns>The durable vote, or the identical transaction's existing terminal outcome.</returns>
    /// <exception cref="InvalidOperationException">The ID was used with another write set.</exception>
    /// <exception cref="ArgumentException">An identity, key, intent or plan violates its documented constraints.</exception>
    /// <exception cref="ObjectDisposedException">The concrete state machine has been disposed.</exception>
    /// <exception cref="IOException">Storage or participant delivery failed; a durable effect may already exist.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was observed; an admitted durable effect may already exist.</exception>
    Task<PrepareResult> PrepareAsync(RequestId id, IReadOnlyDictionary<string, string?> writes, CancellationToken ct = default);
    /// <summary>Durably commits a previously prepared transaction before publishing its local writes.</summary>
    /// <param name="id">Prepared transaction ID.</param><param name="ct">Cancellation token; commit may already be durable.</param>
    /// <returns>The durable terminal outcome; repeats do not apply the writes again.</returns>
    /// <exception cref="InvalidOperationException">The ID was never prepared.</exception>
    /// <exception cref="ArgumentException">An identity, key, intent or plan violates its documented constraints.</exception>
    /// <exception cref="ObjectDisposedException">The concrete state machine has been disposed.</exception>
    /// <exception cref="IOException">Storage or participant delivery failed; a durable effect may already exist.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was observed; an admitted durable effect may already exist.</exception>
    Task<CommitResult> CommitAsync(RequestId id, CancellationToken ct = default);
    /// <summary>Durably aborts and releases locks, or refuses to undo a committed outcome.</summary>
    /// <param name="id">Transaction ID, including an as-yet unknown ID.</param><param name="ct">Cancellation token; abort may already be durable.</param>
    /// <returns>The terminal abort classification after fsync.</returns>
    /// <exception cref="ArgumentException">An identity, key, intent or plan violates its documented constraints.</exception>
    /// <exception cref="ObjectDisposedException">The concrete state machine has been disposed.</exception>
    /// <exception cref="IOException">Storage or participant delivery failed; a durable effect may already exist.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was observed; an admitted durable effect may already exist.</exception>
    Task<AbortResult> AbortAsync(RequestId id, CancellationToken ct = default);
}
