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

/// <summary>Applies CDC upserts/deletes to a read model whose per-key position guards suppress replay.</summary>
/// <typeparam name="TKey">Stable non-null keys.</typeparam>
/// <typeparam name="TValue">Immutable non-null values.</typeparam>
public sealed class ReadModelProjectionSink<TKey, TValue> : IProjectionSink<ChangeRecord<TKey, TValue>>
    where TKey : notnull where TValue : notnull
{
    private readonly IReadModelStore<TKey, TValue> _store;
    /// <summary>Uses the supplied read model and its durability guarantees.</summary>
    /// <param name="store">A single-source position-deduplicating store.</param>
    public ReadModelProjectionSink(IReadModelStore<TKey, TValue> store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }
    /// <inheritdoc />
    public ValueTask ApplyAsync(ChangeRecord<TKey, TValue> change, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (change.Operation == ChangeOperation.Delete)
        {
            return _store.RemoveAsync(change.Key, change.Position, ct);
        }

        if (change.Operation is not (ChangeOperation.Insert or ChangeOperation.Update or ChangeOperation.Snapshot))
        {
            throw new ArgumentOutOfRangeException(nameof(change));
        }

        ArgumentNullException.ThrowIfNull(change.Value);
        return _store.SetAsync(change.Key, change.Value, change.Position, ct);
    }
}
