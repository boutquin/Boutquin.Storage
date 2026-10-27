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

namespace Boutquin.Storage.Domain.Interfaces.Events;

/// <summary>A key-addressed projection whose updates are deduplicated by source-log position.</summary>
/// <typeparam name="TKey">Non-null identity of a read-model row.</typeparam>
/// <typeparam name="TValue">Non-null row value.</typeparam>
/// <remarks>Positions belong to one ordered log. A position no newer than a key's last update
/// is ignored, including after deletion. Values should be immutable; implementations need not clone them.</remarks>
public interface IReadModelStore<TKey, TValue> where TKey : notnull where TValue : notnull
{
    /// <summary>Reads a current row or reports absence.</summary>
    /// <param name="key">The row identity.</param>
    /// <param name="ct">Cancellation before lookup has no effect.</param>
    /// <returns>The value and presence flag; an absent value is the type's default.</returns>
    ValueTask<(TValue? Value, bool Found)> TryGetAsync(TKey key, CancellationToken ct = default);

    /// <summary>Replaces a row only when the source position is newer than its current watermark.</summary>
    /// <param name="key">The row identity.</param>
    /// <param name="value">The replacement value.</param>
    /// <param name="position">A nonnegative position from the store's one source log.</param>
    /// <param name="ct">Cancellation before mutation has no effect.</param>
    /// <returns>Completion after value and watermark become visible together.</returns>
    ValueTask SetAsync(TKey key, TValue value, long position, CancellationToken ct = default);

    /// <summary>Deletes a row while retaining its watermark to prevent replay resurrection.</summary>
    /// <param name="key">The row identity.</param>
    /// <param name="position">A nonnegative source position.</param>
    /// <param name="ct">Cancellation before mutation has no effect.</param>
    /// <returns>Completion after deletion and watermark become visible together.</returns>
    ValueTask RemoveAsync(TKey key, long position, CancellationToken ct = default);

    /// <summary>Copies all live rows at a single point in time.</summary>
    /// <param name="ct">Cancellation before the snapshot has no effect.</param>
    /// <returns>An independent dictionary, empty when no live rows remain.</returns>
    ValueTask<IReadOnlyDictionary<TKey, TValue>> SnapshotAsync(CancellationToken ct = default);
}
