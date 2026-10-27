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
using Boutquin.Storage.Domain.Interfaces.Streaming;
using Boutquin.Storage.Domain.ValueObjects.Streaming;

namespace Boutquin.Storage.Infrastructure.Streaming.Joins;

/// <summary>Enriches each stream record from the latest value in a CDC-derived state table.</summary>
/// <typeparam name="TStream">The stream value type.</typeparam>
/// <typeparam name="TKey">The non-null table lookup key type.</typeparam>
/// <typeparam name="TTable">The CDC-derived table value type.</typeparam>
/// <typeparam name="TOut">The joined output value type.</typeparam>
/// <remarks>
/// Output retains each matching stream record's offset and <see cref="StreamRecord{T}.Producer"/>,
/// so per-producer event-time consumers downstream of the join keep tracking each producer's
/// completeness threshold individually. Missing table keys are omitted, so output
/// offsets can have gaps. Append results to a fresh event log before checkpointed consumption.
/// </remarks>
public sealed class StreamTableJoin<TStream, TKey, TTable, TOut>
    : IStreamProcessor<StreamRecord<TStream>, StreamRecord<TOut>>
    where TKey : notnull
{
    private readonly IStateStore<TKey, TTable> _table;
    private readonly Func<TStream, TKey> _keySelector;
    private readonly Func<TStream, TTable, TOut> _project;

    /// <summary>Creates an inert stream-table inner join over a borrowed state table.</summary>
    /// <param name="table">The latest CDC-derived table state.</param>
    /// <param name="keySelector">Selects a lookup key from a stream value.</param>
    /// <param name="project">Combines the stream and table values.</param>
    /// <exception cref="ArgumentNullException">A collaborator is <see langword="null"/>.</exception>
    public StreamTableJoin(
        IStateStore<TKey, TTable> table,
        Func<TStream, TKey> keySelector,
        Func<TStream, TTable, TOut> project)
    {
        _table = table ?? throw new ArgumentNullException(nameof(table));
        _keySelector = keySelector ?? throw new ArgumentNullException(nameof(keySelector));
        _project = project ?? throw new ArgumentNullException(nameof(project));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<StreamRecord<TOut>> ProcessAsync(
        IAsyncEnumerable<StreamRecord<TStream>> input,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        await foreach (var record in input.WithCancellation(ct).ConfigureAwait(false))
        {
            var (tableValue, found) = await _table.TryGetAsync(_keySelector(record.Value), ct)
                .ConfigureAwait(false);
            if (found)
            {
                yield return new StreamRecord<TOut>(
                    record.Offset, record.EventTime, _project(record.Value, tableValue!))
                {
                    Producer = record.Producer,
                };
            }
        }
    }
}
