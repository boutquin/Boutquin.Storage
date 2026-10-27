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
namespace Boutquin.Storage.Domain.Interfaces.Streaming;

/// <summary>
/// Persists and recovers stream processor state (offset + opaque state blob).
///
/// <para>Checkpoint stores enable <b>at-least-once</b>, resumable stream processing by recording the
/// last successfully processed position alongside any processor-specific state; on recovery a
/// processor loads its checkpoint and resumes from the saved offset. Exactly-once (or effectively-once)
/// is NOT provided by this primitive on its own: it requires the processor's output and the offset
/// advance to commit atomically together (or idempotent output keyed by offset). A standalone offset
/// store cannot guarantee that — a crash between the output write and the checkpoint save reprocesses
/// from the last saved offset.</para>
///
/// <para><b>Concurrency:</b> concurrent saves to the same <c>processorId</c> follow last-writer-wins
/// semantics. Implementations must guarantee no corruption.</para>
///
/// <para><b>Complexity:</b></para>
/// <para>- <b>SaveAsync:</b> O(N) where N is the state size, plus fsync overhead for durable implementations.</para>
/// <para>- <b>LoadAsync:</b> O(N) where N is the state size.</para>
///
/// <para><b>Reference:</b> Kleppmann, <i>Designing Data-Intensive Applications</i> (O'Reilly, 2nd ed., 2026),
/// Ch. 12 — "Stream Processing": checkpointing consumer offsets for fault-tolerant processing.</para>
/// </summary>
public interface ICheckpointStore
{
    /// <summary>
    /// Saves a checkpoint for the specified processor.
    /// </summary>
    /// <param name="processorId">Unique identifier for the processor. Must not be null, empty, or whitespace.</param>
    /// <param name="offset">The stream position to checkpoint.</param>
    /// <param name="state">Opaque processor state. May be empty but not null.
    /// AC-8.7 (F-49, DR-6): unified with <see cref="LoadAsync"/> on <c>byte[]</c> so Save
    /// and Load share the same storage representation — the previous <c>ReadOnlyMemory&lt;byte&gt;</c>
    /// was a type asymmetry with no benefit over <c>byte[]</c> for a checkpoint boundary.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="processorId"/> is null, empty, or whitespace.</exception>
    ValueTask SaveAsync(string processorId, long offset, byte[] state, CancellationToken ct = default);

    /// <summary>
    /// Loads the most recent checkpoint for the specified processor.
    /// </summary>
    /// <param name="processorId">Unique identifier for the processor. Must not be null, empty, or whitespace.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The saved offset and state, or <c>null</c> if no checkpoint exists for this processor.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="processorId"/> is null, empty, or whitespace.</exception>
    ValueTask<(long Offset, byte[] State)?> LoadAsync(string processorId, CancellationToken ct = default);
}
