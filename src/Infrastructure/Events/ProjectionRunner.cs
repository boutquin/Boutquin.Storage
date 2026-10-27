// Copyright (c) 2024-2026 Pierre G. Boutquin. All rights reserved.
//
//   Licensed under the Apache License, Version 2.0 (the "License").
//   You may not use this file except in compliance with the License.
//   You may obtain a copy of the License at
//
//       http://www.apache.org/licenses/LICENSE-2.0
//
//   Unless required by applicable law or agreed to in writing, software
//   distributed under the License is distributed on an "AS IS" BASIS,
//   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//
//   See the License for the specific language governing permissions and
//   limitations under the License.
//

using Boutquin.Storage.Domain.Interfaces.Events;
using Boutquin.Storage.Domain.Interfaces.Streaming;

namespace Boutquin.Storage.Infrastructure.Events;

/// <summary>Resumes a deterministic projection from an atomically stored state and log position.</summary>
/// <typeparam name="TEvent">The input event.</typeparam>
/// <typeparam name="TState">The immutable projection state.</typeparam>
/// <remarks><para>Each event is folded, optionally delivered to an idempotent projector, then
/// checkpointed. A failure before checkpoint acknowledgement causes replay; projector deduplication
/// prevents duplicate effects. FileCheckpointStore makes state and position durable together.</para>
/// <para>One runner owns each processor ID. Concurrent calls on this instance are rejected; separate
/// instances must not run the same ID concurrently. Catch-up is bounded by the log implementation's
/// enumeration semantics; repeated calls process newly appended events.</para>
/// <para>Time is O(events × transition/checkpoint cost). A checkpoint per event favors simple recovery
/// over throughput. Reference: Kleppmann, Designing Data-Intensive Applications, event sourcing.</para></remarks>
public sealed class ProjectionRunner<TEvent, TState>
{
    private readonly string _processorId;
    private readonly IEventLog<TEvent> _log;
    private readonly IProjection<TEvent, TState> _projection;
    private readonly ICheckpointStore _checkpoints;
    private readonly Func<TState, byte[]> _serialize;
    private readonly Func<byte[], TState> _deserialize;
    private readonly IProjector<TEvent>? _projector;
    private int _running;

    /// <summary>Creates an inert runner; no collaborators are owned or disposed.</summary>
    /// <param name="processorId">Stable checkpoint identity, unique to this projection and log.</param>
    /// <param name="log">Ordered, contiguous log beginning at position zero.</param>
    /// <param name="projection">Pure deterministic transitions with immutable state.</param>
    /// <param name="checkpoints">Atomically persists the offset and encoded state.</param>
    /// <param name="serialize">Encodes the state; must round-trip through deserialize.</param>
    /// <param name="deserialize">Decodes state and rejects incompatible or corrupt bytes.</param>
    /// <param name="projector">Optional external handler; must durably deduplicate positions.</param>
    /// <exception cref="ArgumentException">The processor ID is blank.</exception>
    /// <exception cref="ArgumentNullException">A required collaborator is null.</exception>
    public ProjectionRunner(string processorId, IEventLog<TEvent> log, IProjection<TEvent, TState> projection,
        ICheckpointStore checkpoints, Func<TState, byte[]> serialize, Func<byte[], TState> deserialize,
        IProjector<TEvent>? projector = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processorId);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentNullException.ThrowIfNull(checkpoints);
        ArgumentNullException.ThrowIfNull(serialize);
        ArgumentNullException.ThrowIfNull(deserialize);
        _processorId = processorId;
        _log = log;
        _projection = projection;
        _checkpoints = checkpoints;
        _serialize = serialize;
        _deserialize = deserialize;
        _projector = projector;
    }

    /// <summary>Catches up from the saved offset and returns the final acknowledged checkpoint.</summary>
    /// <param name="ct">Cancellation between events or during I/O; prior checkpoints remain committed.
    /// An interrupted external handler may already have completed, so it must tolerate replay.</param>
    /// <returns>The state and position after all enumerated events have been checkpointed.</returns>
    /// <exception cref="InvalidOperationException">Another call on this runner is active.</exception>
    /// <exception cref="InvalidDataException">A checkpoint offset is invalid or the log is not contiguous.</exception>
    /// <exception cref="IOException">The log, projector, or checkpoint could not complete its I/O.</exception>
    public async ValueTask<ProjectionCheckpoint<TState>> RunAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            throw new InvalidOperationException("A projection catch-up is already running.");
        }

        try
        {
            var saved = await _checkpoints.LoadAsync(_processorId, ct).ConfigureAwait(false);
            var position = saved?.Offset ?? -1;
            if (position < -1 || position == long.MaxValue)
            {
                throw new InvalidDataException("The projection checkpoint position cannot be resumed.");
            }
            var state = saved.HasValue ? _deserialize(saved.Value.State) : _projection.InitialState;
            await foreach (var entry in _log.ReadFromAsync(position + 1, ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                if (position == long.MaxValue || entry.Position != position + 1 || entry.Position == long.MaxValue)
                {
                    throw new InvalidDataException("The projection input must have contiguous, increasing positions.");
                }
                var next = _projection.Apply(state, entry.Event);
                // Encode before publishing external effects: a codec failure must not produce an
                // output that cannot ever be checkpointed with this projection configuration.
                var bytes = _serialize(next);
                ArgumentNullException.ThrowIfNull(bytes);
                if (_projector is not null)
                {
                    await _projector.ApplyAsync(entry.Position, entry.Event, ct).ConfigureAwait(false);
                }
                await _checkpoints.SaveAsync(_processorId, entry.Position, bytes, ct).ConfigureAwait(false);
                state = next;
                position = entry.Position;
            }
            return new ProjectionCheckpoint<TState>(position, state);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }
}
