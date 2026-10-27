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

namespace Boutquin.Storage.BenchMark;

internal static class SegmentTestExtensions
{
    public static void Write<TKey, TValue>(
        this Boutquin.Storage.Infrastructure.SortedStringTable.SortedStringTable<TKey, TValue> table,
        IEnumerable<(TKey Key, TValue Value, ulong SequenceNumber)> items)
        where TKey : IStorageKey<TKey>, new()
        where TValue : ISerializable<TValue>, new()
        => table.Write(items.Select(t => SegmentWriteEntry<TKey, TValue>.Inline(t.Key, t.Value, t.SequenceNumber)));
}
