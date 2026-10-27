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
namespace Boutquin.Storage.Infrastructure.Tests.TestHelpers;

/// <summary>
/// Helpers for tests and benchmarks that construct
/// <see cref="SegmentWriteEntry{TKey, TValue}"/> batches from pre-WiscKey
/// <see cref="KeyValuePair{TKey, TValue}"/> or tuple sequences. Every produced entry is
/// <see cref="ValueKind.Inline"/> with <c>SequenceNumber = 0</c> — the stand-alone-test
/// shape the seqno contract prescribes for callers without an LSM-assigned seqno.
/// </summary>
internal static class SeqnoTripleExtensions
{
    public static IEnumerable<SegmentWriteEntry<TKey, TValue>> WithZeroSeqno<TKey, TValue>(
        this IEnumerable<KeyValuePair<TKey, TValue>> items)
        where TKey : IStorageKey<TKey>, new()
        where TValue : ISerializable<TValue>, new()
        => items.Select(kv => SegmentWriteEntry<TKey, TValue>.Inline(kv.Key, kv.Value, 0UL));

    public static IReadOnlyList<SegmentWriteEntry<TKey, TValue>> ToSeqnoTriples<TKey, TValue>(
        this IEnumerable<KeyValuePair<TKey, TValue>> items)
        where TKey : IStorageKey<TKey>, new()
        where TValue : ISerializable<TValue>, new()
        => items.Select(kv => SegmentWriteEntry<TKey, TValue>.Inline(kv.Key, kv.Value, 0UL)).ToList();

    /// <summary>
    /// Converts pre-WiscKey seqno-triples to inline <see cref="SegmentWriteEntry{TKey, TValue}"/>
    /// so test call sites that already carry a seqno do not need to rewrite every factory.
    /// </summary>
    public static IEnumerable<SegmentWriteEntry<TKey, TValue>> ToSegmentWriteEntries<TKey, TValue>(
        this IEnumerable<(TKey Key, TValue Value, ulong SequenceNumber)> items)
        where TKey : IStorageKey<TKey>, new()
        where TValue : ISerializable<TValue>, new()
        => items.Select(t => SegmentWriteEntry<TKey, TValue>.Inline(t.Key, t.Value, t.SequenceNumber));
}
