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

namespace Boutquin.Storage.Infrastructure.Events;

/// <summary>
/// Volatile, concurrent-safe in-memory event log for testing and composition.
///
/// <para><b>Reference:</b> Kleppmann, <i>Designing Data-Intensive Applications</i> (O'Reilly, 2nd ed., 2026),
/// Ch. 12 — "Stream Processing": append-only event logs.</para>
/// </summary>
/// <typeparam name="TEvent">The event type.</typeparam>
public sealed class InMemoryEventLog<TEvent> : IEventLog<TEvent>
{
    private readonly List<TEvent> _events = [];
    private readonly Lock _lock = new();

    /// <inheritdoc />
    public ValueTask<long> AppendAsync(TEvent @event, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // A null event would occupy a position that every consumer (CDC source, stream,
        // projections) dereferences on replay, so the log would fail through it for its whole
        // lifetime. Reject it before a position is assigned, matching AppendOnlyEventLog. (`is null`
        // rather than ThrowIfNull so a value-type TEvent is not boxed on the append path.)
        if (@event is null)
        {
            throw new ArgumentNullException(nameof(@event));
        }

        lock (_lock)
        {
            var position = (long)_events.Count;
            _events.Add(@event);
            return new ValueTask<long>(position);
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<(long Position, TEvent Event)> ReadFromAsync(
        long position, [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Snapshot only the tail starting at `position` to avoid cloning the full list
        // on every call — for a 1M-event log this is a huge allocation.
        ArgumentOutOfRangeException.ThrowIfNegative(position);

        TEvent[] snapshot;
        long startIndex;
        lock (_lock)
        {
            if (position >= _events.Count)
            {
                snapshot = [];
                startIndex = position;
            }
            else
            {
                var start = (int)position;
                var count = _events.Count - start;
                snapshot = new TEvent[count];
                _events.CopyTo(start, snapshot, 0, count);
                startIndex = position;
            }
        }

        for (var i = 0; i < snapshot.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            yield return (startIndex + i, snapshot[i]);
        }

        // AC-8.7 (F-46): removed the no-op `await Task.CompletedTask.ConfigureAwait(false)`
        // that was here. An async iterator does not need a no-op await to satisfy the compiler —
        // the `yield return` statements make the method an async iterator already. The no-op
        // added a synthetic state-machine transition on every call with an empty range.
    }
}
