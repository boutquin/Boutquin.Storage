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
using System.Runtime.CompilerServices;

namespace Boutquin.Storage.Infrastructure.Tests.TestHelpers;

/// <summary>
/// Test-only shims that adapt pre-WiscKey tuple-list call shapes
/// (<c>(TKey, TValue, ulong)</c>) and removed reader convenience methods
/// (<c>TryGetValueAsync</c>, <c>GetAllItemsAsync</c>, <c>StreamWithSeqnosAsync</c>)
/// over the v2.0.0 interface.
/// Keeps ~87 pre-existing test call sites from rewriting piecewise.
/// </summary>
internal static class SegmentTestExtensions
{
    public static Task<ISegmentReader<TKey, TValue>> WriteAsync<TKey, TValue>(
        this ISegmentFormat<TKey, TValue> format,
        string segmentFolder,
        string segmentFileName,
        IReadOnlyList<(TKey Key, TValue Value, ulong SequenceNumber)> sortedEntries,
        CancellationToken cancellationToken = default)
        where TKey : IStorageKey<TKey>, new()
        where TValue : ISerializable<TValue>, new()
        => format.WriteAsync(segmentFolder, segmentFileName,
            sortedEntries.Select(t => SegmentWriteEntry<TKey, TValue>.Inline(t.Key, t.Value, t.SequenceNumber)).ToList(),
            cancellationToken: cancellationToken);

    public static async Task<(TValue Value, bool Found)> TryGetValueAsync<TKey, TValue>(
        this ISegmentReader<TKey, TValue> reader,
        TKey key,
        CancellationToken cancellationToken = default)
        where TKey : IStorageKey<TKey>, new()
        where TValue : ISerializable<TValue>, new()
    {
        var (value, _, found) = await reader.TryGetValueWithSeqnoAsync(key, ulong.MaxValue, cancellationToken);
        return (value, found);
    }

    public static async Task<IEnumerable<(TKey Key, TValue Value)>> GetAllItemsAsync<TKey, TValue>(
        this ISegmentReader<TKey, TValue> reader,
        CancellationToken cancellationToken = default)
        where TKey : IStorageKey<TKey>, new()
        where TValue : ISerializable<TValue>, new()
    {
        var results = new List<(TKey Key, TValue Value)>();
        await foreach (var entry in reader.StreamBlockEntriesAsync(ulong.MaxValue, cancellationToken: cancellationToken))
        {
            if (entry.Kind == ValueKind.Inline)
            {
                results.Add((entry.Key, entry.InlineValue!));
            }
        }
        return results;
    }

    public static async IAsyncEnumerable<(TKey Key, TValue Value, ulong SequenceNumber)> StreamWithSeqnosAsync<TKey, TValue>(
        this ISegmentReader<TKey, TValue> reader,
        ulong maxSequenceNumber,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
        where TKey : IStorageKey<TKey>, new()
        where TValue : ISerializable<TValue>, new()
    {
        await foreach (var entry in reader.StreamBlockEntriesAsync(maxSequenceNumber, cancellationToken: cancellationToken))
        {
            if (entry.Kind == ValueKind.Inline)
            {
                yield return (entry.Key, entry.InlineValue!, entry.SequenceNumber);
            }
        }
    }

    public static void Write<TKey, TValue>(
        this Boutquin.Storage.Infrastructure.SortedStringTable.SortedStringTable<TKey, TValue> table,
        IEnumerable<(TKey Key, TValue Value, ulong SequenceNumber)> items)
        where TKey : IStorageKey<TKey>, new()
        where TValue : ISerializable<TValue>, new()
        => table.Write(items.Select(t => SegmentWriteEntry<TKey, TValue>.Inline(t.Key, t.Value, t.SequenceNumber)));
}
