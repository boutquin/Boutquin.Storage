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
namespace Boutquin.Storage.Distributed.Interfaces.DistributedSystems;

/// <summary>
/// Base interface for Conflict-free Replicated Data Types (CRDTs).
///
/// <para>CRDTs are data structures that can be replicated across multiple nodes in a distributed system,
/// where each replica can be updated independently and concurrently without coordination, and all replicas
/// are guaranteed to converge to the same state when they have received the same set of updates.</para>
///
/// <para>This guarantee is achieved through the mathematical properties of the merge operation:
/// it must be commutative (a ⊕ b = b ⊕ a), associative ((a ⊕ b) ⊕ c = a ⊕ (b ⊕ c)), and
/// idempotent (a ⊕ a = a).</para>
///
/// <para><b>Reference:</b> Kleppmann, <i>Designing Data-Intensive Applications</i> (O'Reilly, 2nd ed., 2026), Ch. 6 — "Replication",
/// section on "Automatic Conflict Resolution". CRDTs enable conflict-free replication without consensus protocols.</para>
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope:</b> In-process reference implementation in <c>Boutquin.Storage.Distributed</c>,
/// which is not published to NuGet (see <c>src/Distributed/README.md</c>). Not durable
/// across process restart, not intended for production deployment. The CRDT base contract
/// is implemented for educational fidelity to DDIA 2e Ch. 6, but production-grade extensions
/// — delta-state / op-based CRDTs and metadata pruning — are not included. For production use, prefer automerge-net or AntidoteDB.
/// </para>
/// </remarks>
/// <typeparam name="T">The type of the CRDT's value.</typeparam>
public interface ICrdt<T>
{
    /// <summary>
    /// Gets the current value of the CRDT.
    /// </summary>
    T Value { get; }

    /// <summary>
    /// Merges this CRDT with another, producing a new CRDT that reflects both states.
    /// The merge operation must be commutative, associative, and idempotent.
    /// </summary>
    /// <param name="other">The other CRDT to merge with.</param>
    /// <returns>A new CRDT representing the merged state.</returns>
    ICrdt<T> Merge(ICrdt<T> other);
}
