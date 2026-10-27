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
/// A positive-negative counter CRDT that supports both increment and decrement. Internally uses two GCounters:
/// one for increments (P) and one for decrements (N). The value is P - N.
///
/// <para><b>Reference:</b> Kleppmann, <i>Designing Data-Intensive Applications</i> (O'Reilly, 2nd ed., 2026), Ch. 6 —
/// "Detecting Concurrent Writes" (p.237) and Shapiro et al. (2011), "Conflict-free Replicated Data Types".</para>
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope:</b> In-process reference implementation in <c>Boutquin.Storage.Distributed</c>,
/// which is not published to NuGet (see <c>src/Distributed/README.md</c>). Not durable
/// across process restart, not intended for production deployment. The positive/negative counter CRDT contract
/// is implemented for educational fidelity to DDIA 2e Ch. 6, but production-grade extensions
/// — delta-state propagation and quiescent-slot GC — are not included. For production use, prefer automerge-net.
/// </para>
/// </remarks>
public interface IPNCounter : ICrdt<long>
{
    /// <summary>
    /// Increments the counter for the specified node.
    /// </summary>
    /// <param name="nodeId">The identifier of the node performing the increment.</param>
    void Increment(string nodeId);

    /// <summary>
    /// Decrements the counter for the specified node.
    /// </summary>
    /// <param name="nodeId">The identifier of the node performing the decrement.</param>
    void Decrement(string nodeId);
}
