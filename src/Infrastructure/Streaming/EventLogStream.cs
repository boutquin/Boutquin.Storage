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
using Boutquin.Storage.Domain.Interfaces.Events;
using Boutquin.Storage.Domain.Interfaces.Streaming;
using Boutquin.Storage.Domain.ValueObjects.Streaming;

namespace Boutquin.Storage.Infrastructure.Streaming;

/// <summary>Adapts an append-only event log to the replayable stream contract.</summary>
/// <typeparam name="T">The event-log value type.</typeparam>
/// <param name="eventLog">The borrowed append-only source log.</param>
/// <param name="eventTimeSelector">A deterministic selector for each event's source timestamp.</param>
/// <remarks>Enumeration is a catch-up snapshot whose exact tail boundary follows the event-log implementation.</remarks>
public sealed class EventLogStream<T>(IEventLog<T> eventLog, Func<T, DateTimeOffset> eventTimeSelector)
    : IStream<T>
{
    private readonly IEventLog<T> _eventLog = eventLog ?? throw new ArgumentNullException(nameof(eventLog));
    private readonly Func<T, DateTimeOffset> _eventTimeSelector =
        eventTimeSelector ?? throw new ArgumentNullException(nameof(eventTimeSelector));

    /// <inheritdoc />
    public async IAsyncEnumerable<StreamRecord<T>> ReadAsync(
        long offset = 0,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        await foreach (var (position, value) in _eventLog.ReadFromAsync(offset, ct).ConfigureAwait(false))
        {
            yield return new StreamRecord<T>(position, _eventTimeSelector(value), value);
        }
    }
}
