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
using Boutquin.Storage.Domain.ValueObjects.SortedStringTable;

namespace Boutquin.Storage.Infrastructure.LsmTree;

/// <summary>
/// Adapters that surface segment readers and memtables as <see cref="IAsyncEnumerable{T}"/>
/// so the engine can feed them into <see cref="Algorithms.KWayMerge"/>.
/// </summary>
internal static class SegmentStreamExtensions
{
    /// <summary>
    /// Wraps <see cref="ISegmentReader{TKey,TValue}.StreamBlockEntriesAsync"/> as an
    /// <see cref="IAsyncEnumerable{T}"/> of <see cref="SegmentWriteEntry{TKey, TValue}"/>
    /// so it can drive a k-way merge. Pointer entries flow through untouched — the merge
    /// never calls <c>TValue.Deserialize</c> on them
    /// (dereference is deferred to the engine layer).
    /// </summary>
    public static IAsyncEnumerable<SegmentWriteEntry<TKey, TValue>> StreamAsync<TKey, TValue>(
        this ISegmentReader<TKey, TValue> segment,
        CancellationToken cancellationToken = default)
        where TKey : IStorageKey<TKey>, new()
        where TValue : ISerializable<TValue>, new()
        => segment.StreamBlockEntriesAsync(ulong.MaxValue, cancellationToken: cancellationToken);

    /// <summary>
    /// Wraps an in-memory <see cref="SegmentWriteEntry{TKey, TValue}"/> list (assumed sorted
    /// ascending by key) as an <see cref="IAsyncEnumerable{T}"/>.
    /// </summary>
    public static async IAsyncEnumerable<SegmentWriteEntry<TKey, TValue>> AsSortedAsyncEnumerable<TKey, TValue>(
        this IEnumerable<SegmentWriteEntry<TKey, TValue>> items,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
        where TKey : IStorageKey<TKey>, new()
        where TValue : ISerializable<TValue>, new()
    {
        // Async-iterator contract requires the method be `async`; this source is an in-memory
        // list with no I/O, so satisfy CS1998 once with a synchronously-completed await rather
        // than a per-item `Task.Yield()` (which would post a thread-pool continuation for every
        // element — a 64K-entry memtable flush would pay 64K round-trips before the merge sees
        // any data). Cooperative cancellation is preserved by the per-item check below.
        await Task.CompletedTask.ConfigureAwait(false);
        foreach (var entry in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return entry;
        }
    }
}
