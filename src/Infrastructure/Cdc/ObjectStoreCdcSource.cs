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
/// CDC source that watches an <see cref="IEventLog{TEvent}"/> of change records, emitting
/// change events for object store write and delete operations.
///
/// <para>This adapter provides CDC semantics over an event log where events represent
/// object store mutations. The key type is the generic <typeparamref name="TKey"/> parameter,
/// making the source reusable for any object store key type — not limited to <c>string</c>.
/// Typical usage with <see cref="Domain.Interfaces.ObjectStore.IObjectStore"/> passes
/// <c>TKey = string</c>.</para>
///
/// <para><b>Consistency is the producer's responsibility.</b> This adapter REPLAYS an event
/// log; it does not observe the object store itself. A producer that writes the store and
/// appends the change record as two separate operations is dual-writing — the race and
/// partial-failure hazards DDIA 2e Ch. 12 warns about apply in full ("dual writes have
/// serious problems, one of which is a race condition … one of the writes may fail while the
/// other succeeds"). The safe pattern is log-as-source-of-truth: append the change record
/// first and apply the store mutation FROM the log, in log order.</para>
///
/// <para><b>Reference:</b> Kleppmann, <i>Designing Data-Intensive Applications</i> (O'Reilly, 2nd ed., 2026),
/// Ch. 12 — "Stream Processing": change data capture and the dual-write hazard.</para>
/// </summary>
/// <typeparam name="TKey">The key type for object store change records.</typeparam>
public sealed class ObjectStoreCdcSource<TKey> : IChangeDataCaptureSource<TKey, byte[]>
{
    private readonly IEventLog<ChangeRecord<TKey, byte[]>> _eventLog;

    /// <summary>
    /// Initializes a new instance of <see cref="ObjectStoreCdcSource{TKey}"/>.
    /// </summary>
    /// <param name="eventLog">The backing event log containing object store change records.</param>
    public ObjectStoreCdcSource(IEventLog<ChangeRecord<TKey, byte[]>> eventLog)
    {
        ArgumentNullException.ThrowIfNull(eventLog);
        _eventLog = eventLog;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChangeRecord<TKey, byte[]>> ReadChangesAsync(
        long position, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var (logPosition, record) in _eventLog.ReadFromAsync(position, ct).ConfigureAwait(false))
        {
            // The backing log's position is the resume coordinate this method filters by, so
            // it is the position the yielded record must carry: consumers checkpoint the last
            // record's Position and pass it back here. The producer-stamped Position cannot be
            // trusted to match — the log assigns positions at append time, after the record
            // (and any Position field inside it) was already serialized.
            yield return record with { Position = logPosition };
        }
    }
}
