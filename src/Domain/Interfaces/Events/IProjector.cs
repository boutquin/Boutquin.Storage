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

namespace Boutquin.Storage.Domain.Interfaces.Events;

/// <summary>An idempotent external read-model handler keyed by event-log position.</summary>
/// <typeparam name="TEvent">The immutable input event.</typeparam>
/// <remarks>The handler owns its deduplication and durability. It must atomically persist its
/// effect and position before acknowledgement; replaying the same position must have no additional
/// effect. A checkpoint alone cannot make arbitrary external effects exactly once.</remarks>
public interface IProjector<in TEvent>
{
    /// <summary>Applies an event once, tolerating retries after an ambiguous acknowledgement.</summary>
    /// <param name="position">The nonnegative position in the handler's single input log.</param>
    /// <param name="event">The event at that position.</param>
    /// <param name="ct">Cancellation may be observed after the effect; retry with the same position.</param>
    /// <returns>Completion after the effect and deduplication position have been recorded.</returns>
    ValueTask ApplyAsync(long position, TEvent @event, CancellationToken ct = default);
}
