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
using Boutquin.Storage.Distributed.Replication;

namespace Boutquin.Storage.Distributed.Interfaces.Replication;

/// <summary>Resolves versions without mutating stored history or manufacturing a new write.</summary>
/// <typeparam name="T">The immutable payload type.</typeparam>
/// <remarks>Implementations must be pure and deterministic. Sibling order is canonical; replicas converge
/// when they hold the same writes. A resolved read becomes a new write only through an explicit client write.</remarks>
public interface IConflictResolver<T>
{
    /// <summary>Resolves the supplied versions, excluding causally superseded writes.</summary>
    /// <param name="versions">Versions from one replica set; empty input represents absence.</param>
    /// <returns>Zero values for absence, one resolved value, or all concurrent siblings.</returns>
    /// <exception cref="ArgumentNullException">The list or one of its entries is null.</exception>
    /// <exception cref="ArgumentException">Versions belong to different replica sets.</exception>
    IReadOnlyList<T> Resolve(IReadOnlyList<ReplicationVersion<T>> versions);
}
