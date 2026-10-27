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

namespace Boutquin.Storage.Domain.Interfaces.Analytics;

/// <summary>Maintains a durable derived value from an ordered input log.</summary>
/// <typeparam name="TState">The immutable materialized state.</typeparam>
public interface IMaterializedView<TState>
{
    /// <summary>Gets the most recent state completed by this instance.</summary>
    ProjectionCheckpoint<TState> Current { get; }

    /// <summary>Processes entries after the durable checkpoint.</summary>
    /// <param name="cancellationToken">A token to monitor for cancellation.</param>
    /// <returns>The new durable state and input position.</returns>
    ValueTask<ProjectionCheckpoint<TState>> RefreshAsync(CancellationToken cancellationToken = default);

    /// <summary>Replays the complete log and atomically replaces the durable checkpoint.</summary>
    /// <param name="cancellationToken">A token to monitor for cancellation.</param>
    /// <returns>The rebuilt state and input position.</returns>
    ValueTask<ProjectionCheckpoint<TState>> RebuildAsync(CancellationToken cancellationToken = default);
}
