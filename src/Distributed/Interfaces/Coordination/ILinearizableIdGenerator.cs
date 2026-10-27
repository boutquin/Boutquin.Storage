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
namespace Boutquin.Storage.Distributed.Interfaces.Coordination;

/// <summary>Generates globally ordered IDs from a quorum-committed sequence, with retry deduplication.</summary>
/// <remarks>IDs increase in committed order; non-overlapping successful calls observe increasing values. IDs are not gapless.
/// The ordering survives Raft leader changes while the in-process log state survives.</remarks>
public interface ILinearizableIdGenerator
{
    /// <summary>Allocates an ID after its sequence update commits.</summary>
    /// <param name="requestId">Globally unique mutation identity; an identical retry returns its prior ID.</param>
    /// <param name="ct">Cancellation token; admission can precede cancellation.</param>
    /// <returns>A positive unique ID.</returns>
    /// <exception cref="ArgumentException">A supplied argument violates its documented constraints.</exception>
    /// <exception cref="InvalidOperationException">The service rejects the request, for example because this node is not leader.</exception>
    /// <exception cref="TimeoutException">No committed response arrives before the virtual deadline; effects may still occur.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed; an admitted operation may still commit.</exception>
    /// <exception cref="ObjectDisposedException">The underlying service has been disposed.</exception>
    /// <exception cref="ArgumentNullException">A required reference argument is null.</exception>
    Task<long> NextAsync(string requestId, CancellationToken ct = default);
}
