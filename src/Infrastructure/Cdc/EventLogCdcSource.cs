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
using Boutquin.Storage.Domain.Interfaces.Cdc;
using Boutquin.Storage.Domain.Interfaces.Events;

namespace Boutquin.Storage.Infrastructure.Cdc;

/// <summary>
/// Adapts an <see cref="IEventLog{TEvent}"/> containing <see cref="ChangeRecord{TKey,TValue}"/>
/// into a <see cref="IChangeDataCaptureSource{TKey,TValue}"/>.
///
/// <para>This is a thin adapter — all ordering, durability, and concurrency guarantees
/// are delegated to the underlying event log.</para>
///
/// <para><b>Reference:</b> Kleppmann, <i>Designing Data-Intensive Applications</i> (O'Reilly, 2017),
/// Ch. 11 — "Stream Processing": CDC derived from an event log.</para>
/// </summary>
/// <typeparam name="TKey">The key type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
public sealed class EventLogCdcSource<TKey, TValue> : IChangeDataCaptureSource<TKey, TValue>
{
    private readonly IEventLog<ChangeRecord<TKey, TValue>> _eventLog;

    /// <summary>
    /// Initializes a new instance of <see cref="EventLogCdcSource{TKey, TValue}"/>.
    /// </summary>
    /// <param name="eventLog">The backing event log containing change records.</param>
    public EventLogCdcSource(IEventLog<ChangeRecord<TKey, TValue>> eventLog)
    {
        ArgumentNullException.ThrowIfNull(eventLog);
        _eventLog = eventLog;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChangeRecord<TKey, TValue>> ReadChangesAsync(
        long position, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var (_, record) in _eventLog.ReadFromAsync(position, ct).ConfigureAwait(false))
        {
            yield return record;
        }
    }
}
