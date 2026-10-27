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

/// <summary>Allocates fencing tokens from the coordination cluster's committed sequence.</summary>
/// <remarks>Borrowed service; no local counter exists to reset on leader change. Fencing is effective only
/// when the external resource persistently remembers and rejects tokens below its highest accepted token.
/// Tokens identify allocation order, not authentication or automatic lock ownership.</remarks>
public sealed class FencingTokenService : IFencingTokenService
{
    private readonly ICoordinationService _service;

    /// <summary>Creates an inert facade without taking ownership of the service.</summary>
    /// <param name="service">Non-null borrowed coordination service.</param>
    /// <exception cref="ArgumentNullException">Service is null.</exception>
    public FencingTokenService(ICoordinationService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

    /// <inheritdoc />
    public Task<long> NextAsync(string resource, string requestId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);
        return _service.AllocateSequenceAsync($"fence:{resource}", requestId, ct);
    }
}
