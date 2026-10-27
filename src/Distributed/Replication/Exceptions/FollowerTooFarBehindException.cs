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
/// A follower asked a replication log for entries past a sequence number that has already
/// been truncated. The gap between the requested sequence and the log's current
/// <see cref="TruncationWatermark"/> cannot be reconstructed from the log alone — the follower
/// must reprovision via an out-of-band full-state snapshot (rebootstrap) before it can rejoin
/// the normal incremental-catch-up path.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope:</b> In-process reference implementation in <c>Boutquin.Storage.Distributed</c>,
/// which is not published to NuGet (see <c>src/Distributed/README.md</c>). Not durable
/// across process restart, not intended for production deployment. The replication-log truncation signal
/// is implemented for educational fidelity to DDIA 2e Ch. 6, but production-grade extensions
/// — snapshot-based rebootstrap coordination and replication-lag back-pressure — are not included. For production use, prefer an Akka.NET durable-log shard or Orleans streams.
/// </para>
///
/// Thrown by <c>IReplicationLog.GetEntriesAfterAsync</c> when <c>afterSequenceNumber + 1 &lt;
/// TruncationWatermark</c> and the follower is not already caught up (a follower at or beyond
/// the highest sequence number ever appended has missed nothing and receives an empty list
/// instead). Callers that see this exception must treat the follower as stale
/// and fall back to a snapshot-based catch-up — the replication log intentionally sheds
/// history beyond the watermark to keep its memory footprint bounded (DDIA 2e Ch. 6 —
/// truncation discipline for replication logs).
/// </remarks>
public sealed class FollowerTooFarBehindException(string message, long requestedAfterSequenceNumber, long truncationWatermark) : Exception(message)
{
    /// <summary>The sequence number the follower asked the log to continue from.</summary>
    public long RequestedAfterSequenceNumber { get; } = requestedAfterSequenceNumber;

    /// <summary>
    /// The truncation cutoff: entries with <c>SequenceNumber &lt; TruncationWatermark</c>
    /// have been truncated and are no longer available through this log. (The lowest entry
    /// actually retained may sit above this value when sequence numbers are sparse, and the
    /// log may have been emptied entirely.)
    /// </summary>
    public long TruncationWatermark { get; } = truncationWatermark;
}
