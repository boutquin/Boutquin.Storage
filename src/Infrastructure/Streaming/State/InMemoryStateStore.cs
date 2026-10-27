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

namespace Boutquin.Storage.Infrastructure.Streaming.State;

/// <summary>Concurrent in-memory processor state with point-in-time snapshot reads.</summary>
/// <typeparam name="TKey">The non-null dictionary key type.</typeparam>
/// <typeparam name="TValue">The state value type.</typeparam>
/// <remarks>Thread-safe but non-durable; snapshots are detached shallow copies.</remarks>
public sealed class InMemoryStateStore<TKey, TValue> : IStateStore<TKey, TValue>
    where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, TValue> _state = new();

    /// <inheritdoc />
    public ValueTask<(TValue? Value, bool Found)> TryGetAsync(TKey key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ct.ThrowIfCancellationRequested();
        var found = _state.TryGetValue(key, out var value);
        return ValueTask.FromResult((value, found));
    }

    /// <inheritdoc />
    public ValueTask SetAsync(TKey key, TValue value, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        ct.ThrowIfCancellationRequested();
        _state[key] = value;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<bool> RemoveAsync(TKey key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_state.TryRemove(key, out _));
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyDictionary<TKey, TValue>> SnapshotAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        // ToArray acquires every bucket lock and copies under them — a true point-in-time copy.
        // Enumerating the ConcurrentDictionary directly (what the Dictionary(IDictionary) copy
        // constructor does) is lock-free and observes concurrent writes key by key, so it can
        // pair one key's old value with another's new one: a state the table never held.
        IReadOnlyDictionary<TKey, TValue> snapshot = new Dictionary<TKey, TValue>(_state.ToArray());
        return ValueTask.FromResult(snapshot);
    }
}
