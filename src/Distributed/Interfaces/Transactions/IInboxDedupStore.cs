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

/// <summary>Atomically records a consumed identity and its local processing effects.</summary>
/// <remarks>Handlers must be pure local mutation builders. No check-then-external-effect protocol is supplied.
/// Identities are retained indefinitely; include handler/schema version in intent when changing semantics.</remarks>
public interface IInboxDedupStore
{
    /// <summary>Runs a new request against committed state, or returns an identical request's cached result.</summary>
    /// <param name="id">Stable request identity.</param><param name="intent">Nonblank input fingerprint or semantic intent, including version.</param>
    /// <param name="handler">Pure mutation builder over an immutable committed snapshot; must not re-enter the store.</param>
    /// <param name="ct">Cancellation token; a faulted append may already be durable, so reopen and retry.</param>
    /// <returns>The result after effects and dedup identity are fsynced together.</returns>
    /// <exception cref="InvalidOperationException">The ID is already bound to different intent or operation kind.</exception>
    /// <exception cref="ArgumentException">An identity, key, intent or plan violates its documented constraints.</exception>
    /// <exception cref="ObjectDisposedException">The concrete state machine has been disposed.</exception>
    /// <exception cref="IOException">Storage or participant delivery failed; a durable effect may already exist.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was observed; an admitted durable effect may already exist.</exception>
    Task<string> ProcessAsync(RequestId id, string intent, Func<IReadOnlyDictionary<string, string>, MessageMutation> handler, CancellationToken ct = default);
}
