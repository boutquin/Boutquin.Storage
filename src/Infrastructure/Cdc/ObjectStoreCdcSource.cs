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
/// CDC source that watches an <see cref="IEventLog{TEvent}"/> of change records keyed by string,
/// emitting change records for object store operations.
///
/// <para>This adapter provides CDC semantics over an event log where events represent
/// object store write and delete operations. The key type is fixed to <c>string</c>
/// (matching <see cref="Domain.Interfaces.ObjectStore.IObjectStore"/> key semantics).</para>
///
/// <para><b>Reference:</b> Kleppmann, <i>Designing Data-Intensive Applications</i> (O'Reilly, 2017),
/// Ch. 11 — "Stream Processing": CDC from polling-based sources.</para>
/// </summary>
/// <typeparam name="TKey">The key type (typically string for object store keys).</typeparam>
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
        await foreach (var (_, record) in _eventLog.ReadFromAsync(position, ct).ConfigureAwait(false))
        {
            yield return record;
        }
    }
}
