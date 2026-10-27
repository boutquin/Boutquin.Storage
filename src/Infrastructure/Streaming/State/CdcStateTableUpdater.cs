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
using Boutquin.Storage.Domain.Interfaces.Streaming;

namespace Boutquin.Storage.Infrastructure.Streaming.State;

/// <summary>
/// Applies an ordered change-data-capture snapshot and tail to a keyed stream-processing table.
/// </summary>
/// <typeparam name="TKey">The CDC and state-table key type.</typeparam>
/// <typeparam name="TValue">The materialized table value type.</typeparam>
/// <remarks>
/// <para><b>Ordering:</b> changes are applied in source enumeration order. The returned position can
/// be checkpointed and the next run can resume at the following position.</para>
/// <para><b>Atomicity:</b> one state mutation is applied at a time. The whole snapshot-and-tail run is
/// not atomic with a concurrent stream-table lookup.</para>
/// <para><b>Reference:</b> Kleppmann, <i>Designing Data-Intensive Applications</i>
/// (O'Reilly, 2nd ed., 2026), Ch. 12, change data capture and stream-table joins.</para>
/// </remarks>
public sealed class CdcStateTableUpdater<TKey, TValue>
    where TKey : notnull
{
    private readonly IChangeDataCaptureSource<TKey, TValue> _source;
    private readonly IStateStore<TKey, TValue> _stateStore;

    /// <summary>Creates an updater over a borrowed CDC source and borrowed state store.</summary>
    /// <param name="source">The ordered source of snapshot and tail change records.</param>
    /// <param name="stateStore">The table to mutate. The caller retains its lifetime.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="source"/> or <paramref name="stateStore"/> is
    /// <see langword="null"/>.
    /// </exception>
    public CdcStateTableUpdater(
        IChangeDataCaptureSource<TKey, TValue> source,
        IStateStore<TKey, TValue> stateStore)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
    }

    /// <summary>Applies changes from an inclusive source position.</summary>
    /// <param name="position">The first source position to read.</param>
    /// <param name="ct">Cancels between source records and state mutations.</param>
    /// <returns>
    /// A task that completes with the last applied source position, or <paramref name="position"/>
    /// minus one when the source yields no changes.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="position"/> is negative or cannot be resumed.
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// Thrown for non-increasing or terminal source positions, unknown operations, or a missing value.
    /// </exception>
    public async ValueTask<long> ApplyAsync(long position = 0, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        ArgumentOutOfRangeException.ThrowIfEqual(position, long.MaxValue);
        var lastApplied = position - 1;

        await foreach (var change in _source.ReadChangesAsync(position, ct).ConfigureAwait(false))
        {
            if (change.Position <= lastApplied || change.Position == long.MaxValue)
            {
                throw new InvalidDataException("CDC positions must be strictly increasing and resumable.");
            }

            switch (change.Operation)
            {
                case ChangeOperation.Delete:
                    _ = await _stateStore.RemoveAsync(change.Key, ct).ConfigureAwait(false);
                    break;
                case ChangeOperation.Insert:
                case ChangeOperation.Update:
                case ChangeOperation.Snapshot:
                    if (change.Value is null)
                    {
                        throw new InvalidDataException(
                            $"CDC {change.Operation} at position {change.Position} has no value.");
                    }

                    await _stateStore.SetAsync(change.Key, change.Value, ct).ConfigureAwait(false);
                    break;
                default:
                    throw new InvalidDataException(
                        $"CDC position {change.Position} has unknown operation {change.Operation}.");
            }

            lastApplied = change.Position;
        }

        return lastApplied;
    }
}
