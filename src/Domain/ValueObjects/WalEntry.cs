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
using Boutquin.Storage.Domain.ValueObjects.ValueLog;

namespace Boutquin.Storage.Domain.ValueObjects;

/// <summary>
/// A durable WriteAheadLog entry recovered from disk. Carries the two orthogonal
/// discriminators v2.0.0 threads through the write path: <see cref="Kind"/>
/// (<see cref="EntryKind.Set"/> vs. <see cref="EntryKind.Delete"/>) and
/// <see cref="ValueKind"/> (<see cref="Enums.ValueKind.Inline"/> vs.
/// <see cref="Enums.ValueKind.ValueLogPointer"/>). The per-entry
/// <see cref="SequenceNumber"/> lets recovery reseed the engine's seqno counter to
/// <c>max(sst_max, wal_max) + 1</c>.
/// </summary>
/// <typeparam name="TKey">Key type — serialisable and comparable.</typeparam>
/// <typeparam name="TValue">Value type — serialisable.</typeparam>
/// <param name="Key">Mutation key.</param>
/// <param name="SequenceNumber">Engine-assigned monotonic sequence number at append time.</param>
/// <param name="Kind">Whether the entry is a <see cref="EntryKind.Set"/> or
/// <see cref="EntryKind.Delete"/>.</param>
/// <param name="ValueKind">When <see cref="Kind"/> is <see cref="EntryKind.Set"/>, whether
/// the payload is inline or a value-log pointer. When <see cref="Kind"/> is
/// <see cref="EntryKind.Delete"/>, always <see cref="Enums.ValueKind.Inline"/> (no value to
/// discriminate).</param>
/// <param name="InlineValue">The payload when <see cref="Kind"/> = <see cref="EntryKind.Set"/>
/// AND <see cref="ValueKind"/> = <see cref="Enums.ValueKind.Inline"/>; otherwise the default
/// for <typeparamref name="TValue"/>.</param>
/// <param name="Pointer">The value-log reference when <see cref="Kind"/> =
/// <see cref="EntryKind.Set"/> AND <see cref="ValueKind"/> =
/// <see cref="Enums.ValueKind.ValueLogPointer"/>; otherwise <see langword="null"/>.</param>
/// <remarks>
/// <para>
/// <b>Replay invariants.</b>
/// (1) WAL replay is strictly position-ordered. (2) <see cref="EntryKind.Delete"/> is
/// idempotent — replaying it N times yields the same MemTable ∪ tombstone-set state.
/// (3) A Set followed by a Delete for the same key at a higher position collapses to
/// Delete in the recovered MemTable. (4) SequenceNumbers are monotonically non-decreasing
/// in position order.
/// </para>
/// <para>
/// <b>(Kind, ValueKind) dispatch.</b>
/// <c>(Set, Inline)</c> applies the inline value to the MemTable;
/// <c>(Set, ValueLogPointer)</c> applies the pointer (the engine verifies the referenced
/// value-log file's EOF covers it before trusting the pointer — dangling pointers surface
/// as <c>DanglingValueLogPointerException</c>);
/// <c>(Delete, *)</c> records the tombstone.
/// </para>
/// </remarks>
public sealed record WalEntry<TKey, TValue>(
    TKey Key,
    ulong SequenceNumber,
    EntryKind Kind,
    ValueKind ValueKind,
    TValue? InlineValue,
    ValueLogPointer? Pointer)
{
    /// <summary>
    /// <see langword="true"/> when this entry was written by <c>AppendDeleteAsync</c>
    /// (<see cref="Kind"/> = <see cref="EntryKind.Delete"/>). Convenience getter.
    /// </summary>
    public bool IsTombstone => Kind == EntryKind.Delete;

    /// <summary>
    /// Constructs a Set + Inline WAL entry.
    /// </summary>
    public static WalEntry<TKey, TValue> SetInline(TKey key, TValue value, ulong sequenceNumber)
        => new(key, sequenceNumber, EntryKind.Set, ValueKind.Inline, value, Pointer: null);

    /// <summary>
    /// Constructs a Set + ValueLogPointer WAL entry.
    /// </summary>
    public static WalEntry<TKey, TValue> SetPointer(TKey key, ValueLogPointer pointer, ulong sequenceNumber)
        => new(key, sequenceNumber, EntryKind.Set, ValueKind.ValueLogPointer, InlineValue: default, pointer);

    /// <summary>
    /// Constructs a Delete (tombstone) WAL entry.
    /// </summary>
    public static WalEntry<TKey, TValue> Tombstone(TKey key, ulong sequenceNumber)
        => new(key, sequenceNumber, EntryKind.Delete, ValueKind.Inline, InlineValue: default, Pointer: null);
}
