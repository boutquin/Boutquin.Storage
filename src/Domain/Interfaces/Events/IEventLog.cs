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
namespace Boutquin.Storage.Domain.Interfaces.Events;

/// <summary>
/// An append-only, ordered log of events supporting positional reads.
///
/// <para>Events are appended in order and assigned monotonically increasing positions (0, 1, 2, ...).
/// Consumers can read from any position forward, enabling replay, catch-up subscriptions,
/// and event sourcing patterns.</para>
///
/// <para><b>Concurrency:</b> concurrent appends produce unique positions with no gaps.
/// Implementations serialize appends internally.</para>
///
/// <para><b>Complexity:</b></para>
/// <para>- <b>AppendAsync:</b> O(N) where N is the serialized event size, plus fsync for durable implementations.</para>
/// <para>- <b>ReadFromAsync:</b> O(K) where K is the number of events read.</para>
///
/// <para><b>Reference:</b> Kleppmann, <i>Designing Data-Intensive Applications</i> (O'Reilly, 2017),
/// Ch. 11 — "Stream Processing": append-only event logs as the foundation for stream processing
/// and event sourcing.</para>
/// </summary>
/// <typeparam name="TEvent">The event type.</typeparam>
public interface IEventLog<TEvent>
{
    /// <summary>
    /// Appends an event to the log.
    /// </summary>
    /// <param name="event">The event to append.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The position assigned to the event (0-based, monotonically increasing).</returns>
    ValueTask<long> AppendAsync(TEvent @event, CancellationToken ct = default);

    /// <summary>
    /// Reads events from the specified position forward.
    /// </summary>
    /// <param name="position">The position to start reading from (inclusive).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An async enumerable of (position, event) pairs in order.</returns>
    IAsyncEnumerable<(long Position, TEvent Event)> ReadFromAsync(long position, CancellationToken ct = default);
}
