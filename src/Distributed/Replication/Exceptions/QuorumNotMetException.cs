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
namespace Boutquin.Storage.Distributed.Replication.Exceptions;

/// <summary>
/// A quorum-replicated operation (write or read) was rejected before any state was observed
/// or mutated because fewer than <see cref="RequiredQuorum"/> replicas were available. No
/// state transition occurred for writes; no consistent slice could be assembled for reads.
/// The caller may retry once enough replicas come back online.
/// </summary>
/// <remarks>
/// <para>
/// <b>DDIA reference.</b> Kleppmann &amp; Riccomini, <i>Designing Data-Intensive Applications</i>,
/// 2nd ed. (O'Reilly, 2026), Ch. 6 — "Quorums for reading and writing" (p. 230). Strict-quorum
/// semantics require either reject-before-mutate (this exception) or sloppy-quorum-with-hinted-handoff;
/// the in-process reference implementation chooses reject-before-mutate so partial-state mutation
/// never surfaces. The separate LeaderlessReplicaSet reference model allows explicit sloppy substitution
/// and raises this exception when even the permitted substitutes cannot meet the response count.
/// </para>
/// <para>
/// <b>Symmetry between read and write.</b> The exception is raised symmetrically for writes
/// (fewer than W reachable replicas) and reads (fewer than R reachable replicas). Returning
/// a silent <c>(default, false, 0)</c> on a read-quorum miss would conflate "key not present
/// in this consistent slice" with "the slice could not be assembled," and a caller observing
/// the false would take it as authoritative absence rather than transient infrastructure
/// failure.
/// </para>
/// <para>
/// <b>TOCTOU note.</b> The pre-check (available-replica count) and the subsequent fan-out
/// execute under the same lock that gates replica-set mutation, so a replica drop between
/// the check and the operation cannot reproduce the partial-state hazard the exception
/// exists to prevent.
/// </para>
/// <para>
/// <b>Scope:</b> In-process reference implementation in <c>Boutquin.Storage.Distributed</c>,
/// which is not published to NuGet (see <c>src/Distributed/README.md</c>). Not durable across
/// process restart, not intended for production deployment. For production use, prefer
/// <a href="https://getakka.net">Akka.NET cluster</a>, <a href="https://orleans.dev">Orleans</a>
/// silos, or a dedicated replicated-log service.
/// </para>
/// </remarks>
/// <remarks>
/// Initializes a new instance with the operation discriminator, the available-replica
/// count, and the required quorum size.
/// </remarks>
/// <param name="operation">The operation that was rejected (e.g., <c>"Write"</c> or <c>"Read"</c>).</param>
/// <param name="availableReplicas">Number of replicas reachable at the moment of the check.</param>
/// <param name="requiredQuorum">The configured quorum (W for writes, R for reads).</param>
public sealed class QuorumNotMetException(string operation, int availableReplicas, int requiredQuorum) : Exception($"{operation} rejected: only {availableReplicas} replica(s) available; {operation} quorum requires {requiredQuorum}.")
{
    /// <summary>
    /// Identifier of the operation that was rejected — typically <c>"Write"</c> or
    /// <c>"Read"</c>. Surfaces in the exception message and lets diagnostics distinguish a
    /// write-quorum miss from a read-quorum miss without parsing free-form text.
    /// </summary>
    public string Operation { get; } = operation;

    /// <summary>The number of replicas that were available at the moment of the check.</summary>
    public int AvailableReplicas { get; } = availableReplicas;

    /// <summary>
    /// The required quorum — <c>W</c> for write operations, <c>R</c> for read operations.
    /// </summary>
    public int RequiredQuorum { get; } = requiredQuorum;
}
