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
namespace Boutquin.Storage.Infrastructure.WriteAheadLog;

/// <summary>
/// Construction-time configuration for <see cref="WriteAheadLog{TKey, TValue}"/>. Carries
/// the group-commit batching knobs (`GroupCommitBatchSize`, `GroupCommitLatencyMs`) and
/// the segment-rotation threshold (`SegmentMaxBytes`).
/// </summary>
/// <remarks>
/// <para>
/// <b>Theory:</b> The defaults match the standard group-commit literature
/// (Hellerstein et al., <i>Architecture of a Database System</i>, 2007, §4.1) — a 256-append
/// batch coalesces into a single fsync, lifting the WAL throughput ceiling from "one fsync
/// per append" (hundreds of IOPS) to "one fsync per batch" (whatever the underlying disk
/// sustains at the chosen batch size). The 5 ms latency cap bounds tail latency for partial
/// batches under low load.
/// </para>
/// <para>
/// <b>Durability invariants preserved (DDIA p. 128).</b> (a) The WAL frame is fsync'd before
/// any batched <c>SetResult</c> fires — writers see completion only after durable write.
/// (b) MemTable mutation may happen before, during, or after fsync — recovery replays the WAL
/// independently. (c) Group commit preserves the WAL-durable-before-apply invariant; batch
/// size only affects latency, not correctness.
/// </para>
/// <para>
/// <b>Segment rotation (D2).</b> When the active segment reaches
/// <see cref="SegmentMaxBytes"/>, the WAL closes it and opens a new
/// <c>wal_NNNNNN.log</c> file (monotonic counter, same pattern as SSTable segments). On
/// <c>TruncateAsync</c>, all segments are deleted and the counter resets — this matches the
/// "watermark = entire log" case used by <c>LsmStorageEngine</c> after a successful MemTable
/// flush.
/// </para>
/// <para>
/// <b>Reference:</b> Kleppmann &amp; Riccomini, <i>Designing Data-Intensive Applications</i>,
/// 2nd ed. (O'Reilly, 2026), Ch. 4 p. 128 — WAL durability invariants; Hellerstein et al.,
/// <i>Architecture of a Database System</i>, 2007, §4.1 — group commit.
/// </para>
/// </remarks>
public sealed record WriteAheadLogOptions
{
    /// <summary>
    /// Maximum number of appends coalesced into a single fsync (D1). Defaults to 256, matching
    /// the canonical group-commit literature batch size. Valid range 1–4096.
    /// </summary>
    public int GroupCommitBatchSize { get; init; } = 256;

    /// <summary>
    /// Maximum wait, in milliseconds, before the flusher loop forces an fsync on a partial
    /// batch (D1). Defaults to 5 ms — bounds tail latency under low load. Valid range 1–1000.
    /// </summary>
    public int GroupCommitLatencyMs { get; init; } = 5;

    /// <summary>
    /// Rotation threshold in bytes for a single WAL segment file (D2). Defaults to 64 MiB —
    /// the standard LSM WAL segment size; configurable for low-memory consumers. Must be at
    /// least 1 MiB so individual frames always fit.
    /// </summary>
    public long SegmentMaxBytes { get; init; } = 64L * 1024L * 1024L;

    /// <summary>
    /// Maximum number of un-flushed append requests the group-commit write queue may hold
    /// before producers apply back-pressure. Defaults to 1024. Valid range 1–65536.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why bounded.</b> Each queued append pins a rented <c>ArrayPool</c> frame buffer until
    /// the flusher fsyncs it. An unbounded queue lets the backlog — and the pinned-buffer
    /// memory it holds — grow without limit whenever the flusher falls behind under sustained
    /// write pressure with high fan-in concurrency, a memory-exhaustion hazard. A bounded queue
    /// with <c>BoundedChannelFullMode.Wait</c> turns "the flusher is behind" into asynchronous
    /// producer back-pressure (<c>AppendAsync</c> awaits until space frees up) instead of
    /// unbounded growth, while still never dropping a frame — durability is preserved.
    /// </para>
    /// <para>
    /// <b>Default rationale.</b> 1024 sits four batches above the default
    /// <see cref="GroupCommitBatchSize"/> of 256, so the flusher can keep coalescing full
    /// batches without the queue acting as the bottleneck under normal load, yet caps the
    /// in-flight backlog (and its pinned buffers) at a bounded, predictable ceiling under
    /// overload.
    /// </para>
    /// </remarks>
    public int WriteQueueCapacity { get; init; } = 1024;
}
