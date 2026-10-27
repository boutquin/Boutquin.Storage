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
/// A Lamport logical timestamp implementation providing total ordering of events across distributed nodes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope:</b> In-process reference implementation in <c>Boutquin.Storage.Distributed</c>,
/// which is not published to NuGet (see <c>src/Distributed/README.md</c>). Not durable
/// across process restart, not intended for production deployment. Lamport logical timestamps
/// is implemented for educational fidelity to DDIA 2e Ch. 10, but production-grade extensions
/// — clock-skew bounds, hybrid logical clocks (HLC), and causal-broadcast integration — are not included. For production use, prefer Orleans / Akka.NET logical-clock primitives.
/// </para>
///
/// <para>
/// <b>Thread safety:</b> All counter operations use <see cref="Interlocked"/> for lock-free thread safety.
/// </para>
/// </remarks>
public sealed class LamportTimestamp : ILamportTimestamp
{
    private long _counter;

    /// <summary>
    /// Initializes a new instance of the <see cref="LamportTimestamp"/> class.
    /// </summary>
    /// <param name="nodeId">The unique node identifier used for tie-breaking.</param>
    /// <param name="initialCounter">The initial counter value. Defaults to 0.</param>
    public LamportTimestamp(string nodeId, long initialCounter = 0)
    {
        // Sibling convention (GCounter, ORSet, VectorClock, RaftNode, ConsistentHashRing):
        // the node id is the total-order tiebreaker, so empty/whitespace ids are rejected.
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        NodeId = nodeId;
        _counter = initialCounter;
    }

    /// <inheritdoc />
    public string NodeId { get; }

    /// <inheritdoc />
    public long GetCurrentTimestamp() => Interlocked.Read(ref _counter);

    /// <inheritdoc />
    /// <exception cref="OverflowException">The counter is already <see cref="long.MaxValue"/>.</exception>
    public long Increment()
    {
        // Interlocked.Increment wraps at long.MaxValue, which would order the next event before
        // every earlier one; fail loudly instead, matching MultiLeaderReplicaSet's checked clock.
        while (true)
        {
            var current = Interlocked.Read(ref _counter);
            var newValue = checked(current + 1);
            if (Interlocked.CompareExchange(ref _counter, newValue, current) == current)
            {
                return newValue;
            }
        }
    }

    /// <inheritdoc />
    /// <exception cref="OverflowException">
    /// The larger of the local and received timestamps is <see cref="long.MaxValue"/>, so no
    /// representable timestamp exceeds it; the clock is left unchanged.
    /// </exception>
    public long Update(long receivedTimestamp)
    {
        // Atomically set counter to max(local, received) + 1
        // Use a compare-and-swap loop for thread safety
        while (true)
        {
            var current = Interlocked.Read(ref _counter);
            var newValue = checked(Math.Max(current, receivedTimestamp) + 1);

            if (Interlocked.CompareExchange(ref _counter, newValue, current) == current)
            {
                return newValue;
            }
        }
    }

    /// <summary>
    /// Compares two Lamport events — each expressed as a (timestamp, nodeId) pair — using the
    /// DDIA-canonical total order: timestamps first, then node ID lexicographically for ties.
    /// This is the only comparison surface; the earlier instance form
    /// <c>CompareTo(long, string)</c> coupled a counter read with the comparison and was
    /// racy under concurrent <see cref="Update"/>. DDIA Ch. 9 (p. 342, "Lamport timestamps")
    /// describes Lamport ordering as a comparison over two <c>(counter, nodeId)</c> pairs —
    /// no notion of "the current node's timestamp" mutating mid-compare.
    /// </summary>
    /// <param name="timestamp1">The counter value of the first event.</param>
    /// <param name="nodeId1">The node identifier of the first event.</param>
    /// <param name="timestamp2">The counter value of the second event.</param>
    /// <param name="nodeId2">The node identifier of the second event.</param>
    /// <returns>
    /// Negative when event 1 precedes event 2 in total order, positive when event 2 precedes
    /// event 1, and zero when both fields are equal.
    /// </returns>
    public static int CompareEvents(long timestamp1, string nodeId1, long timestamp2, string nodeId2)
    {
        var timestampComparison = timestamp1.CompareTo(timestamp2);
        return timestampComparison != 0
            ? timestampComparison
            : string.Compare(nodeId1, nodeId2, StringComparison.Ordinal);
    }
}
