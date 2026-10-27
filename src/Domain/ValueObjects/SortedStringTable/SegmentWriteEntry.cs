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

namespace Boutquin.Storage.Domain.ValueObjects.SortedStringTable;

/// <summary>
/// Discriminated per-entry record handed to <c>ISegmentFormat.WriteAsync</c> and
/// <c>ISortedStringTable.Write</c>. Carries either an inline
/// <typeparamref name="TValue"/> or the 12 raw bytes of a <see cref="ValueLogPointer"/>,
/// selected by <see cref="Kind"/>.
/// </summary>
/// <typeparam name="TKey">Key type.</typeparam>
/// <typeparam name="TValue">Value type carried when <see cref="Kind"/> is
/// <see cref="ValueKind.Inline"/>.</typeparam>
/// <param name="Key">The per-entry key. Ascending across a write batch.</param>
/// <param name="SequenceNumber">Monotonic LSM sequence number.</param>
/// <param name="Kind">Discriminator selecting the value-slot branch.</param>
/// <param name="InlineValue">The caller-supplied <typeparamref name="TValue"/> when
/// <see cref="Kind"/> is <see cref="ValueKind.Inline"/>; otherwise <c>default</c>.</param>
/// <param name="PointerBytes">The 12-byte <c>(fileIndex, offset)</c> pointer body when
/// <see cref="Kind"/> is <see cref="ValueKind.ValueLogPointer"/>; otherwise empty.</param>
/// <remarks>
/// <para><b>Invariant.</b> <see cref="Kind"/> == <see cref="ValueKind.Inline"/> ⇒
/// <see cref="InlineValue"/> is populated and <see cref="PointerBytes"/> is empty.
/// <see cref="Kind"/> == <see cref="ValueKind.ValueLogPointer"/> ⇒
/// <see cref="PointerBytes"/> has length <see cref="ValueLogPointer.SerializedSize"/> (12)
/// and <see cref="InlineValue"/> is <c>default</c>. Use the <see cref="Inline"/> /
/// <see cref="Pointer"/> factory methods to construct entries that satisfy the invariant;
/// constructions failing it are rejected at the writer boundary.</para>
/// <para><b>Why a single discriminated record.</b> Keeping the
/// <c>(key, seqno, kind, value-or-pointer-bytes)</c> bundle single-valued lets the SSTable
/// writer skip <c>TValue.Serialize</c> for pointer entries and preserves the MemTable-side
/// precedent set by <c>MemTableValue&lt;TValue&gt;</c>. Parallel lists were rejected because
/// they would require two synchronized enumerations and risk index-off-by-one errors.</para>
/// </remarks>
public readonly record struct SegmentWriteEntry<TKey, TValue>(
    TKey Key,
    ulong SequenceNumber,
    ValueKind Kind,
    TValue? InlineValue,
    ReadOnlyMemory<byte> PointerBytes)
{
    /// <summary>Builds an inline entry carrying <paramref name="value"/> directly.</summary>
    public static SegmentWriteEntry<TKey, TValue> Inline(TKey key, TValue value, ulong sequenceNumber)
        => new(key, sequenceNumber, ValueKind.Inline, value, ReadOnlyMemory<byte>.Empty);

    /// <summary>Builds a pointer entry carrying the 12-byte <paramref name="pointerBytes"/>.
    /// <paramref name="pointerBytes"/> must have length <see cref="ValueLogPointer.SerializedSize"/>.</summary>
    public static SegmentWriteEntry<TKey, TValue> Pointer(
        TKey key, ReadOnlyMemory<byte> pointerBytes, ulong sequenceNumber)
    {
        if (pointerBytes.Length != ValueLogPointer.SerializedSize)
        {
            throw new ArgumentException(
                $"ValueLogPointer entries require exactly {ValueLogPointer.SerializedSize} bytes; got {pointerBytes.Length}.",
                nameof(pointerBytes));
        }
        return new SegmentWriteEntry<TKey, TValue>(
            key, sequenceNumber, ValueKind.ValueLogPointer, default, pointerBytes);
    }
}
