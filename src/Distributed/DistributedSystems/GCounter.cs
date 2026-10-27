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
namespace Boutquin.Storage.Distributed.DistributedSystems;

/// <summary>
/// A grow-only counter CRDT. Each node maintains its own counter; the total value is the sum of all counters.
/// Merge takes the element-wise maximum of per-node counters.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope:</b> In-process reference implementation in <c>Boutquin.Storage.Distributed</c>,
/// which is not published to NuGet (see <c>src/Distributed/README.md</c>). Not durable
/// across process restart, not intended for production deployment. The grow-only counter CRDT
/// is implemented for educational fidelity to DDIA 2e Ch. 6, but production-grade extensions
/// — delta-state propagation and GC of quiescent replica-id slots — are not included. For production use, prefer <a href="https://github.com/CRDT-NET">automerge-net</a> or AntidoteDB.
/// </para>
///
/// <para>
/// <b>Why per-node counters?</b> A single shared counter cannot be merged without losing increments.
/// If node A has counter=5 and node B has counter=3, merging to max(5,3)=5 loses B's increments.
/// Per-node counters solve this: each node only increments its own entry, and merge takes the max
/// of each node's entry independently.
/// </para>
///
/// <para>
/// <b>Thread safety:</b> This class is not thread-safe. External synchronization is required
/// for concurrent use.
/// </para>
/// </remarks>
public sealed class GCounter : IGCounter
{
    private readonly Dictionary<string, long> _counters = [];

    /// <summary>
    /// Initializes a new empty GCounter.
    /// </summary>
    public GCounter()
    {
    }

    internal GCounter(Dictionary<string, long> initialState)
    {
        foreach (var kvp in initialState)
        {
            _counters[kvp.Key] = kvp.Value;
        }
    }

    // Persistence must retain each component: serializing only Value loses merge semantics.
    internal Dictionary<string, long> CaptureSyncState() => new(_counters, StringComparer.Ordinal);

    /// <inheritdoc/>
    public long Value => _counters.Values.Sum();

    /// <inheritdoc/>
    public void Increment(string nodeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);

        if (_counters.TryGetValue(nodeId, out var current))
        {
            _counters[nodeId] = current + 1;
        }
        else
        {
            _counters[nodeId] = 1;
        }
    }

    /// <inheritdoc/>
    public ICrdt<long> Merge(ICrdt<long> other)
    {
        ArgumentNullException.ThrowIfNull(other);

        if (other is not GCounter otherGCounter)
        {
            throw new ArgumentException("Can only merge with another GCounter.", nameof(other));
        }

        var merged = new Dictionary<string, long>(_counters);
        foreach (var kvp in otherGCounter._counters)
        {
            if (merged.TryGetValue(kvp.Key, out var existingValue))
            {
                merged[kvp.Key] = Math.Max(existingValue, kvp.Value);
            }
            else
            {
                merged[kvp.Key] = kvp.Value;
            }
        }

        return new GCounter(merged);
    }
}
