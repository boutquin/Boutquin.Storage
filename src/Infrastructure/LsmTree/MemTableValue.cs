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

namespace Boutquin.Storage.Infrastructure.LsmTree;

/// <summary>
/// Discriminated MemTable slot that allows the engine to carry either an inline value or a
/// <see cref="ValueLogPointer"/> without two parallel in-memory data structures. The readonly
/// value-type shape keeps the discriminator, payload, pointer, and sequence number together
/// without allocating a separate metadata object for each write.
/// </summary>
/// <typeparam name="TValue">Underlying value type carried inline when
/// <see cref="Kind"/> is <see cref="ValueKind.Inline"/>.</typeparam>
/// <remarks>
/// <para><b>Invariant.</b> <c>Kind == ValueKind.Inline</c> ⇒ <see cref="InlineValue"/> carries
/// the caller's <typeparamref name="TValue"/>; <see cref="Pointer"/> is the default.
/// <c>Kind == ValueKind.ValueLogPointer</c> ⇒ <see cref="Pointer"/> carries the value-log
/// reference; <see cref="InlineValue"/> is <c>default!</c>.</para>
/// <para><b>Seqno field.</b> <see cref="Seqno"/> carries the engine-assigned monotonic
/// sequence number for this write, eliminating the parallel <c>_memTableSeqnos</c>
/// dictionary that previously shadowed every MemTable entry. The seqno is needed at flush
/// time (forwarded into the SSTable write path so recovery can reseed the counter), at
/// snapshot acquisition time (to decide whether a MemTable entry is visible to a given
/// snapshot), and at compaction time. Carrying it inside the slot keeps the three pieces
/// of per-entry metadata — payload kind, payload, seqno — co-located in one struct value.</para>
/// <para><b>Allocation shape.</b> Four fields: the 1-byte discriminator, the
/// <typeparamref name="TValue"/> slot, the 12-byte <see cref="ValueLogPointer"/>, and the
/// 8-byte seqno. The shape avoids a separate metadata object for each entry.</para>
/// </remarks>
internal readonly struct MemTableValue<TValue>
{
    /// <summary>Discriminator selecting which slot (<see cref="InlineValue"/> or
    /// <see cref="Pointer"/>) carries the payload.</summary>
    public ValueKind Kind { get; }

    /// <summary>The caller-supplied value when <see cref="Kind"/> is
    /// <see cref="ValueKind.Inline"/>; otherwise the default for <typeparamref name="TValue"/>.</summary>
    public TValue InlineValue { get; }

    /// <summary>The value-log reference when <see cref="Kind"/> is
    /// <see cref="ValueKind.ValueLogPointer"/>; otherwise the default pointer (all zeros).</summary>
    public ValueLogPointer Pointer { get; }

    /// <summary>Engine-assigned monotonic sequence number for this write. Used at flush time
    /// to forward seqnos into the SSTable write path and at snapshot acquisition to determine
    /// visibility.</summary>
    public ulong Seqno { get; }

    private MemTableValue(ValueKind kind, TValue inlineValue, ValueLogPointer pointer, ulong seqno)
    {
        Kind = kind;
        InlineValue = inlineValue;
        Pointer = pointer;
        Seqno = seqno;
    }

    /// <summary>Inline slot carrying <paramref name="value"/> directly.</summary>
    public static MemTableValue<TValue> Inline(TValue value, ulong seqno)
        => new(ValueKind.Inline, value, default, seqno);

    /// <summary>Pointer slot carrying the value-log reference <paramref name="pointer"/>.</summary>
    public static MemTableValue<TValue> ValueLogPointer(ValueLogPointer pointer, ulong seqno)
        => new(Domain.Enums.ValueKind.ValueLogPointer, default!, pointer, seqno);
}
