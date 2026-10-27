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
using Boutquin.Storage.Domain.Interfaces.Search;

namespace Boutquin.Storage.Infrastructure.Cdc;

/// <summary>Maintains a volatile full-text index from one ordered CDC source, retaining per-key deletion positions.</summary>
/// <typeparam name="TKey">Stable non-null document identifiers.</typeparam>
/// <typeparam name="TValue">Immutable source values.</typeparam>
/// <remarks>Exclusively owns mutations to its index. Recreate sink and index together and bootstrap after restart.
/// Per-key positions are retained for the lifetime of the sink. Successful replay is idempotent; failures leave
/// the position unadvanced so the input can be retried.</remarks>
public sealed class InvertedIndexProjectionSink<TKey, TValue> : IProjectionSink<ChangeRecord<TKey, TValue>> where TKey : notnull
{
    private readonly IInvertedIndex<TKey> _index;
    private readonly Func<TValue, ReadOnlyMemory<char>> _text;
    private readonly Dictionary<TKey, long> _positions = [];
    private readonly object _gate = new();

    /// <summary>Uses a caller-owned empty index and a deterministic text projection.</summary>
    /// <param name="index">Thread-safe index whose mutations are exclusively owned by this sink.</param>
    /// <param name="text">Extracts searchable text before an input position is acknowledged.</param>
    public InvertedIndexProjectionSink(IInvertedIndex<TKey> index, Func<TValue, ReadOnlyMemory<char>> text)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(text);
        _index = index; _text = text;
    }

    /// <inheritdoc />
    public ValueTask ApplyAsync(ChangeRecord<TKey, TValue> change, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(change.Key);
        ArgumentOutOfRangeException.ThrowIfNegative(change.Position);
        if (change.Operation is not (ChangeOperation.Insert or ChangeOperation.Update or ChangeOperation.Delete or ChangeOperation.Snapshot))
        {
            throw new ArgumentOutOfRangeException(nameof(change));
        }
        if (change.Operation != ChangeOperation.Delete)
        {
            ArgumentNullException.ThrowIfNull(change.Value);
        }
        lock (_gate)
        {
            ct.ThrowIfCancellationRequested();
            if (_positions.TryGetValue(change.Key, out var previous) && previous >= change.Position)
            {
                return ValueTask.CompletedTask;
            }
            if (change.Operation == ChangeOperation.Delete)
            {
                _index.Remove(change.Key);
            }
            else
            {
                _index.Add(change.Key, _text(change.Value!));
            }
            _positions[change.Key] = change.Position;
            return ValueTask.CompletedTask;
        }
    }
}
