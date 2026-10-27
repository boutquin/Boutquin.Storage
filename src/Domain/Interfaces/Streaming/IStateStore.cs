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
namespace Boutquin.Storage.Domain.Interfaces.Streaming;

/// <summary>Stores processor-local keyed state independently of the storage-engine API.</summary>
/// <typeparam name="TKey">The non-null key that identifies one state entry.</typeparam>
/// <typeparam name="TValue">The value held in processor-local state.</typeparam>
public interface IStateStore<TKey, TValue>
    where TKey : notnull
{
    /// <summary>Reads the latest value for a key.</summary>
    /// <param name="key">The state key.</param>
    /// <param name="ct">Cancels the read before it completes.</param>
    /// <returns>The value and <see langword="true"/> when found; otherwise the default value and <see langword="false"/>.</returns>
    ValueTask<(TValue? Value, bool Found)> TryGetAsync(TKey key, CancellationToken ct = default);

    /// <summary>Creates or replaces one state entry.</summary>
    /// <param name="key">The state key.</param>
    /// <param name="value">The new state value.</param>
    /// <param name="ct">Cancels the write before it completes.</param>
    /// <returns>A task that completes after the implementation accepts the mutation.</returns>
    ValueTask SetAsync(TKey key, TValue value, CancellationToken ct = default);

    /// <summary>Removes a key and reports whether it existed.</summary>
    /// <param name="key">The state key.</param>
    /// <param name="ct">Cancels the removal before it completes.</param>
    /// <returns><see langword="true"/> when an entry was removed; otherwise <see langword="false"/>.</returns>
    ValueTask<bool> RemoveAsync(TKey key, CancellationToken ct = default);

    /// <summary>Returns a point-in-time copy of the state table.</summary>
    /// <param name="ct">Cancels snapshot creation before it completes.</param>
    /// <returns>A detached read-only dictionary containing the captured entries.</returns>
    ValueTask<IReadOnlyDictionary<TKey, TValue>> SnapshotAsync(CancellationToken ct = default);
}
