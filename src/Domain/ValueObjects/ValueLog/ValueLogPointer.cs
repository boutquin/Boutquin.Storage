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
using System.Buffers.Binary;

namespace Boutquin.Storage.Domain.ValueObjects.ValueLog;

/// <summary>
/// The 12-byte pointer an SSTable block entry carries when <c>ValueKind.ValueLogPointer</c>
/// is set: a 4-byte value-log file index plus an 8-byte field holding the absolute offset of the
/// entry frame's first byte (the <c>length</c> field) within that file and the
/// <see cref="SequenceCheck"/> identity of the frame written there.
/// </summary>
/// <param name="FileIndex">The integer suffix of <c>valuelog_NNNNNN.log</c>. Monotonically
/// assigned; segment rotation advances the counter without reuse.</param>
/// <param name="Offset">Absolute byte offset of the entry frame's <c>length</c> field within
/// the named value-log file. Points to the 8-byte little-endian <c>length</c>, not to the
/// payload.</param>
/// <remarks>
/// <para><b>Wire layout (little-endian, 12 bytes):</b>
/// <c>[0..4) fileIndex : u32][4..12) offset | sequenceCheck &lt;&lt; 40 : u64</c> — the low 40 bits
/// hold the offset (segments are capped below 1 TiB), the high 24 bits the
/// <see cref="SequenceCheck"/>. This is the value body carried by a
/// block entry when <c>ValueKind = 0x01</c>; the SSTable block reader decodes these bytes
/// as a pointer and the engine dereferences it on the read path. A 4-byte file index lets
/// the garbage collector reclaim closed value-log segments rather than only rewriting in
/// place.</para>
/// <para><b>Reference:</b> Kleppmann &amp; Riccomini, <i>Designing Data-Intensive
/// Applications</i>, 2nd ed. (O'Reilly, 2026), p. 130, "Key-value separation".</para>
/// </remarks>
public readonly record struct ValueLogPointer(uint FileIndex, ulong Offset)
{
    /// <summary>The fixed on-disk size of a <see cref="ValueLogPointer"/> in bytes.</summary>
    public const int SerializedSize = 12;

    /// <summary>The largest value <see cref="SequenceCheck"/> can hold (24 bits).</summary>
    public const uint MaxSequenceCheck = 0xFF_FFFF;

    /// <summary>The largest <see cref="Offset"/> the 40-bit on-disk offset field can encode
    /// (1 TiB − 1); the value-log writer rotates segments before reaching it.</summary>
    public const ulong MaxOffset = (1UL << OffsetBits) - 1;

    private const int OffsetBits = 40;

    /// <summary>
    /// Creates the pointer the value-log writer returns for the frame it appended with
    /// <paramref name="sequenceNumber"/>; <see cref="SequenceCheck"/> records that frame's identity.
    /// </summary>
    /// <param name="fileIndex">The integer suffix of <c>valuelog_NNNNNN.log</c>.</param>
    /// <param name="offset">Absolute byte offset of the entry frame within that file.</param>
    /// <param name="sequenceNumber">The sequence number the frame at that address carries.</param>
    public ValueLogPointer(uint fileIndex, ulong offset, ulong sequenceNumber)
        : this(fileIndex, offset)
    {
        SequenceCheck = ComputeSequenceCheck(sequenceNumber);
    }

    /// <summary>
    /// Identity of the frame this pointer was issued for: <see cref="ComputeSequenceCheck"/> of
    /// the frame's sequence number, in <c>[1, <see cref="MaxSequenceCheck"/>]</c>, or <c>0</c> when
    /// the pointer was built from a bare address and records no identity. A reader compares it
    /// with the frame it finds, so an address that was truncated and then reused by a later write
    /// is reported as lost instead of resolving to another write's value. Two distinct sequence
    /// numbers collide only when they differ by a multiple of <see cref="MaxSequenceCheck"/>.
    /// </summary>
    public uint SequenceCheck { get; private init; }

    /// <summary>Maps a frame sequence number to its <see cref="SequenceCheck"/> value.</summary>
    public static uint ComputeSequenceCheck(ulong sequenceNumber)
        => (uint)(sequenceNumber % MaxSequenceCheck) + 1;

    /// <summary>
    /// Encodes the pointer into <paramref name="destination"/>. Throws if the span is
    /// shorter than <see cref="SerializedSize"/>.
    /// </summary>
    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < SerializedSize)
        {
            throw new ArgumentException(
                $"ValueLogPointer requires {SerializedSize} bytes; span has {destination.Length}.",
                nameof(destination));
        }

        if (Offset > MaxOffset)
        {
            throw new InvalidOperationException(
                $"ValueLogPointer offset {Offset} exceeds the {OffsetBits}-bit on-disk offset field (max {MaxOffset}).");
        }

        BinaryPrimitives.WriteUInt32LittleEndian(destination[..4], FileIndex);
        BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(4, 8), Offset | ((ulong)SequenceCheck << OffsetBits));
    }

    /// <summary>Allocates a fresh 12-byte array carrying the encoded pointer.</summary>
    public byte[] ToArray()
    {
        var bytes = new byte[SerializedSize];
        WriteTo(bytes);
        return bytes;
    }

    /// <summary>Decodes a pointer from <paramref name="source"/>'s first 12 bytes.</summary>
    public static ValueLogPointer ReadFrom(ReadOnlySpan<byte> source)
    {
        if (source.Length < SerializedSize)
        {
            throw new ArgumentException(
                $"ValueLogPointer requires {SerializedSize} bytes; span has {source.Length}.",
                nameof(source));
        }

        var fileIndex = BinaryPrimitives.ReadUInt32LittleEndian(source[..4]);
        var packed = BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(4, 8));
        return new ValueLogPointer(fileIndex, packed & MaxOffset)
        {
            SequenceCheck = (uint)(packed >> OffsetBits),
        };
    }
}
