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

/// <summary>Allocates quorum-committed monotonically increasing tokens for external resource fencing.</summary>
/// <remarks>The protected resource must remember its highest accepted token and reject lower tokens.
/// Allocation alone does not enforce lock ownership or prevent a stale process from sending requests.</remarks>
public interface IFencingTokenService
{
    /// <summary>Allocates a new token, or returns the original token for an identical retry.</summary>
    /// <param name="resource">Nonblank resource identity.</param>
    /// <param name="requestId">Globally unique mutation identity.</param>
    /// <param name="ct">Cancellation token; an admitted allocation may still commit.</param>
    /// <returns>A positive globally monotonic fencing token.</returns>
    /// <exception cref="ArgumentException">A supplied argument violates its documented constraints.</exception>
    /// <exception cref="InvalidOperationException">The service rejects the request, for example because this node is not leader.</exception>
    /// <exception cref="TimeoutException">No committed response arrives before the virtual deadline; effects may still occur.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed; an admitted operation may still commit.</exception>
    /// <exception cref="ObjectDisposedException">The underlying service has been disposed.</exception>
    /// <exception cref="ArgumentNullException">A required reference argument is null.</exception>
    Task<long> NextAsync(string resource, string requestId, CancellationToken ct = default);
}
