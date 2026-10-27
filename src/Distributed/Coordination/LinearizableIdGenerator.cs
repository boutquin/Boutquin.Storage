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
using Boutquin.Storage.Distributed.Interfaces.Coordination;

namespace Boutquin.Storage.Distributed.Coordination;

/// <summary>Generates retry-safe IDs from an actual quorum-committed sequence.</summary>
/// <remarks>Distinct allocations are ordered by the replicated log and remain unique across leader changes.
/// Concurrent responses can arrive in different order; only non-overlapping successful calls guarantee
/// increasing observed IDs. The borrowed service owns all state; this facade has no local sequence cache.</remarks>
public sealed class LinearizableIdGenerator : ILinearizableIdGenerator
{
    private readonly ICoordinationService _service;

    /// <summary>Creates an inert ID facade without taking ownership of the service.</summary>
    /// <param name="service">Non-null borrowed coordination service.</param>
    /// <exception cref="ArgumentNullException">Service is null.</exception>
    public LinearizableIdGenerator(ICoordinationService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

    /// <inheritdoc />
    public Task<long> NextAsync(string requestId, CancellationToken ct = default)
        => _service.AllocateSequenceAsync("id", requestId, ct);
}
