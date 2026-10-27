// Copyright (c) 2024-2026 Pierre G. Boutquin. All rights reserved.
//
//   Licensed under the Apache License, Version 2.0 (the "License").
//   You may not use this file except in compliance with the License.
//   You may obtain a copy of the License at
//
//       http://www.apache.org/licenses/LICENSE-2.0
//
//   Unless required by applicable law or agreed to in writing, software
//   distributed under the License is distributed on an "AS IS" BASIS,
//   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//
//   See the License for the specific language governing permissions and
//   limitations under the License.
//

using Boutquin.Storage.Domain.Interfaces.Events;

namespace Boutquin.Storage.Infrastructure.Events;

/// <summary>A volatile read model with atomic per-key position deduplication and delete watermarks.</summary>
/// <typeparam name="TKey">Non-null row identity with stable equality.</typeparam>
/// <typeparam name="TValue">Non-null immutable row value.</typeparam>
/// <remarks>A lock makes each operation atomic. Lookup/update are expected O(1); snapshots are O(N).
/// Tombstone watermarks consume O(all keys ever seen). Rebuild this store after restart; pairing an
/// empty volatile store with an old durable checkpoint would skip previously projected rows.</remarks>
public sealed class InMemoryReadModelStore<TKey, TValue> : IReadModelStore<TKey, TValue>
    where TKey : notnull where TValue : notnull
{
    private readonly Dictionary<TKey, (long Position, TValue? Value, bool Deleted)> _rows = [];
    private readonly Lock _gate = new();

    /// <inheritdoc />
    public ValueTask<(TValue? Value, bool Found)> TryGetAsync(TKey key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult(_rows.TryGetValue(key, out var row) && !row.Deleted
                ? (row.Value, true) : (default(TValue), false));
        }
    }

    /// <inheritdoc />
    public ValueTask SetAsync(TKey key, TValue value, long position, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_rows.TryGetValue(key, out var row) || position > row.Position)
            {
                _rows[key] = (position, value, false);
            }
        }
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask RemoveAsync(TKey key, long position, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_rows.TryGetValue(key, out var row) || position > row.Position)
            {
                _rows[key] = (position, default, true);
            }
        }
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyDictionary<TKey, TValue>> SnapshotAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            IReadOnlyDictionary<TKey, TValue> snapshot = _rows.Where(pair => !pair.Value.Deleted)
                .ToDictionary(pair => pair.Key, pair => pair.Value.Value!);
            return ValueTask.FromResult(snapshot);
        }
    }
}
