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

namespace Boutquin.Storage.Infrastructure.Events;

/// <summary>Recomputes a projection from its empty state without overwriting a live checkpoint.</summary>
/// <typeparam name="TEvent">The immutable input event.</typeparam>
/// <typeparam name="TState">The immutable folded state.</typeparam>
/// <remarks>Rebuild into a separate model and switch consumers only after success. This class has no
/// external effects and uses O(state size) memory; the log may impose additional I/O or buffering.</remarks>
public sealed class ProjectionRebuilder<TEvent, TState>
{
    private readonly IEventLog<TEvent> _log;
    private readonly IProjection<TEvent, TState> _projection;

    /// <summary>Associates a replayable log with a pure projection; no I/O occurs.</summary>
    /// <param name="log">Borrowed log beginning at zero.</param>
    /// <param name="projection">Borrowed deterministic immutable-state fold.</param>
    /// <exception cref="ArgumentNullException">A collaborator is null.</exception>
    public ProjectionRebuilder(IEventLog<TEvent> log, IProjection<TEvent, TState> projection)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(projection);
        _log = log;
        _projection = projection;
    }

    /// <summary>Replays the complete enumerated log prefix from the initial state.</summary>
    /// <param name="ct">Cancellation abandons the new state and leaves live checkpoints unchanged.</param>
    /// <returns>The rebuilt state and its last incorporated position, or -1 for an empty log.</returns>
    /// <exception cref="InvalidDataException">The log has a gap, duplicate, or decreasing position.</exception>
    public async ValueTask<ProjectionCheckpoint<TState>> RebuildAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var state = _projection.InitialState;
        long position = -1;
        await foreach (var entry in _log.ReadFromAsync(0, ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            if (position == long.MaxValue || entry.Position != position + 1 || entry.Position == long.MaxValue)
            {
                throw new InvalidDataException("The projection input must have contiguous, increasing positions.");
            }
            state = _projection.Apply(state, entry.Event);
            position = entry.Position;
        }
        return new ProjectionCheckpoint<TState>(position, state);
    }
}
