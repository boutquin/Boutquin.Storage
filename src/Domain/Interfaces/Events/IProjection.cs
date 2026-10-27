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

/// <summary>A deterministic fold of an ordered event log into a read-model state.</summary>
/// <typeparam name="TEvent">The immutable input event.</typeparam>
/// <typeparam name="TState">The immutable state returned by each transition.</typeparam>
/// <remarks>Implementations must not mutate the previous state or perform external effects.
/// The same initial state and event sequence must produce the same result on replay.
/// Reference: Kleppmann, Designing Data-Intensive Applications, event sourcing and derived data.</remarks>
public interface IProjection<in TEvent, TState>
{
    /// <summary>The state before the first event; repeated access must return an equivalent value.</summary>
    TState InitialState { get; }

    /// <summary>Derives a new state without modifying either input.</summary>
    /// <param name="state">The preceding committed state.</param>
    /// <param name="event">The next event in log order.</param>
    /// <returns>The replacement state.</returns>
    TState Apply(TState state, TEvent @event);
}
