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

/// <summary>A typed register whose reads and revision-based compare-and-swap operations are quorum ordered.</summary>
/// <typeparam name="TKey">Non-null immutable key with stable, distinct JSON encoding that round-trips exactly.</typeparam>
/// <typeparam name="TValue">Non-null application value that JSON can round-trip exactly, including its runtime types.</typeparam>
/// <remarks>Revision comparison avoids ABA: writing an equal value still creates a new revision.
/// Request IDs share the underlying coordination service's global deduplication namespace.
/// A key or value whose JSON decoding changes its content or runtime type is refused with
/// <see cref="ArgumentException"/> before admission.</remarks>
public interface ILinearizableRegister<TKey, TValue> where TKey : notnull where TValue : notnull
{
    /// <summary>Reads the committed value or an absent snapshot with revision zero.</summary>
    /// <param name="key">Immutable key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A quorum-confirmed decoded snapshot.</returns>
    /// <exception cref="ArgumentException">A supplied argument violates its documented constraints.</exception>
    /// <exception cref="InvalidOperationException">The service rejects the request, for example because this node is not leader.</exception>
    /// <exception cref="TimeoutException">No committed response arrives before the virtual deadline; effects may still occur.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed; an admitted operation may still commit.</exception>
    /// <exception cref="ObjectDisposedException">The underlying service has been disposed.</exception>
    /// <exception cref="ArgumentNullException">A required reference argument is null.</exception>
    Task<RegisterValue<TValue>> ReadAsync(TKey key, CancellationToken ct = default);

    /// <summary>Serializes a value and commits its replacement.</summary>
    /// <param name="key">Immutable key.</param>
    /// <param name="value">Non-null value serialized at invocation.</param>
    /// <param name="requestId">Globally unique mutation identity.</param>
    /// <param name="ct">Cancellation token; effects may occur after admission.</param>
    /// <returns>The new committed value revision.</returns>
    /// <exception cref="ArgumentException">A supplied argument violates its documented constraints.</exception>
    /// <exception cref="InvalidOperationException">The service rejects the request, for example because this node is not leader.</exception>
    /// <exception cref="TimeoutException">No committed response arrives before the virtual deadline; effects may still occur.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed; an admitted operation may still commit.</exception>
    /// <exception cref="ObjectDisposedException">The underlying service has been disposed.</exception>
    /// <exception cref="ArgumentNullException">A required reference argument is null.</exception>
    Task<long> SetAsync(TKey key, TValue value, string requestId, CancellationToken ct = default);

    /// <summary>Commits a replacement only when the expected revision still identifies the value.</summary>
    /// <param name="key">Immutable key.</param>
    /// <param name="expectedRevision">Expected revision, or zero for absence.</param>
    /// <param name="value">Replacement value.</param>
    /// <param name="requestId">Globally unique mutation identity.</param>
    /// <param name="ct">Cancellation token; effects may occur after admission.</param>
    /// <returns>Whether the atomic comparison succeeded.</returns>
    /// <exception cref="ArgumentException">A supplied argument violates its documented constraints.</exception>
    /// <exception cref="InvalidOperationException">The service rejects the request, for example because this node is not leader.</exception>
    /// <exception cref="TimeoutException">No committed response arrives before the virtual deadline; effects may still occur.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed; an admitted operation may still commit.</exception>
    /// <exception cref="ObjectDisposedException">The underlying service has been disposed.</exception>
    /// <exception cref="ArgumentNullException">A required reference argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A numeric argument violates its documented bounds.</exception>
    Task<bool> CompareExchangeAsync(TKey key, long expectedRevision, TValue value, string requestId, CancellationToken ct = default);
}
