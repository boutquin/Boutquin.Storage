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

namespace Boutquin.Storage.Domain.Helpers;

/// <summary>
/// Byte-lex-comparable encoding primitives for Phase 3 ordered wire on [Key] types. The
/// invariant: <c>byte-lex(encode(a), encode(b)) == numeric-compare(a, b)</c> for every
/// supported kind. Persisting encoded bytes into an LSM SSTable then lets binary search
/// and range scan run over raw byte spans without deserialising each key on every step.
/// </summary>
/// <remarks>
/// <para>
/// <b>Signed integers — BE + sign-bit flip.</b> Two's-complement signed integers have the
/// sign bit set for negatives; raw big-endian bytes would sort positives before negatives.
/// XORing the MSB flips the ordering: negatives become 0x00..0x7F, positives 0x80..0xFF,
/// so byte-lex matches numeric order. (FoundationDB / CockroachDB composite-key encoding.)
/// </para>
/// <para>
/// <b>Unsigned integers — plain BE.</b> Byte-lex of BE bytes == numeric order directly.
/// </para>
/// <para>
/// <b>Strings — terminator-escaped UTF-8.</b> Each 0x00 byte in the payload is escaped as
/// <c>0x00 0xFF</c>; the field terminator is <c>0x00 0x00</c>. This yields byte-lex == UTF-8
/// ordinal order while still self-delimiting inside a composite key (variable-length fields
/// must self-terminate so the next field in the composite is unambiguously located).
/// </para>
/// <para>
/// <b>Nullable discriminator.</b> One byte: 0x00 for null, 0x01 for present. Null sorts
/// first. The caller then writes the payload only when present.
/// </para>
/// <para>
/// <b>Thread safety.</b> All members are pure static methods over the caller's stream.
/// </para>
/// </remarks>
public static class OrderedByteHelpers
{
    private const byte FieldTerminator = 0x00;
    private const byte NullByteEscape = 0xFF;

    // Strict UTF-8: throws EncoderFallbackException on ill-formed UTF-16 (unpaired surrogates)
    // instead of silently substituting U+FFFD. Replacement substitution would collide distinct
    // keys ("\uD800" and "\uDFFF" both encode to EF BF BD) and break round-trip exactness —
    // the same class of silent corruption the NaN rejection in WriteSingle/WriteDouble guards
    // against, so the same fail-fast policy applies.
    private static readonly UTF8Encoding s_strictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    // ═════════════════════════════════════════════════════════════════════════════════════
    // Signed integers — BE + sign-bit flip.
    // ═════════════════════════════════════════════════════════════════════════════════════

    public static void WriteInt8(Stream stream, sbyte value)
    {
        ArgumentNullException.ThrowIfNull(stream);
        stream.WriteByte((byte)(value ^ 0x80));
    }

    public static sbyte ReadInt8(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var b = stream.ReadByte();
        if (b < 0)
        {
            throw new EndOfStreamException("Stream ended while reading ordered sbyte.");
        }

        return (sbyte)(b ^ 0x80);
    }

    public static void WriteInt16(Stream stream, short value)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> buf = stackalloc byte[2];
        BinaryPrimitives.WriteInt16BigEndian(buf, (short)(value ^ short.MinValue));
        stream.Write(buf);
    }

    public static short ReadInt16(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> buf = stackalloc byte[2];
        VarintHelpers.ReadExact(stream, buf);
        return (short)(BinaryPrimitives.ReadInt16BigEndian(buf) ^ short.MinValue);
    }

    public static void WriteInt32(Stream stream, int value)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buf, value ^ int.MinValue);
        stream.Write(buf);
    }

    public static int ReadInt32(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> buf = stackalloc byte[4];
        VarintHelpers.ReadExact(stream, buf);
        return BinaryPrimitives.ReadInt32BigEndian(buf) ^ int.MinValue;
    }

    public static void WriteInt64(Stream stream, long value)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(buf, value ^ long.MinValue);
        stream.Write(buf);
    }

    public static long ReadInt64(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> buf = stackalloc byte[8];
        VarintHelpers.ReadExact(stream, buf);
        return BinaryPrimitives.ReadInt64BigEndian(buf) ^ long.MinValue;
    }

    // ═════════════════════════════════════════════════════════════════════════════════════
    // Unsigned integers — plain BE.
    // ═════════════════════════════════════════════════════════════════════════════════════

    public static void WriteUInt8(Stream stream, byte value)
    {
        ArgumentNullException.ThrowIfNull(stream);
        stream.WriteByte(value);
    }

    public static byte ReadUInt8(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var b = stream.ReadByte();
        if (b < 0)
        {
            throw new EndOfStreamException("Stream ended while reading ordered byte.");
        }

        return (byte)b;
    }

    public static void WriteUInt16(Stream stream, ushort value)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> buf = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buf, value);
        stream.Write(buf);
    }

    public static ushort ReadUInt16(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> buf = stackalloc byte[2];
        VarintHelpers.ReadExact(stream, buf);
        return BinaryPrimitives.ReadUInt16BigEndian(buf);
    }

    public static void WriteUInt32(Stream stream, uint value)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buf, value);
        stream.Write(buf);
    }

    public static uint ReadUInt32(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> buf = stackalloc byte[4];
        VarintHelpers.ReadExact(stream, buf);
        return BinaryPrimitives.ReadUInt32BigEndian(buf);
    }

    public static void WriteUInt64(Stream stream, ulong value)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(buf, value);
        stream.Write(buf);
    }

    public static ulong ReadUInt64(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> buf = stackalloc byte[8];
        VarintHelpers.ReadExact(stream, buf);
        return BinaryPrimitives.ReadUInt64BigEndian(buf);
    }

    // ═════════════════════════════════════════════════════════════════════════════════════
    // Bool (1 byte) + Char (2 bytes BE).
    // ═════════════════════════════════════════════════════════════════════════════════════

    public static void WriteBool(Stream stream, bool value)
    {
        ArgumentNullException.ThrowIfNull(stream);
        stream.WriteByte(value ? (byte)1 : (byte)0);
    }

    public static bool ReadBool(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var b = stream.ReadByte();
        if (b < 0)
        {
            throw new EndOfStreamException("Stream ended while reading ordered bool.");
        }

        return b != 0;
    }

    public static void WriteChar(Stream stream, char value) => WriteUInt16(stream, value);
    public static char ReadChar(Stream stream) => (char)ReadUInt16(stream);

    // ═════════════════════════════════════════════════════════════════════════════════════
    // String — UTF-8 with terminator escape.
    // ═════════════════════════════════════════════════════════════════════════════════════

    public static void WriteOrderedString(Stream stream, string value)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(value);

        int byteCount;
        try
        {
            byteCount = s_strictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException ex)
        {
            throw new ArgumentException(
                "Ordered-wire key strings must be well-formed UTF-16: an unpaired surrogate cannot " +
                "be encoded in UTF-8, and replacement substitution (U+FFFD) would collide distinct " +
                "keys and break round-trip exactness.", nameof(value), ex);
        }
        byte[]? rented = null;
        Span<byte> utf8 = byteCount <= 256
            ? stackalloc byte[byteCount]
            : (rented = System.Buffers.ArrayPool<byte>.Shared.Rent(byteCount)).AsSpan(0, byteCount);
        try
        {
            if (byteCount > 0)
            {
                s_strictUtf8.GetBytes(value, utf8);
            }

            // Scan byte-by-byte so the 0x00 escape inserts inline. Two-byte terminator
            // closes the field after the last data byte.
            for (var i = 0; i < byteCount; i++)
            {
                var b = utf8[i];
                stream.WriteByte(b);
                if (b == FieldTerminator)
                {
                    stream.WriteByte(NullByteEscape);
                }
            }
            stream.WriteByte(FieldTerminator);
            stream.WriteByte(FieldTerminator);
        }
        finally
        {
            if (rented is not null)
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    public static string ReadOrderedString(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        // Scan for the 0x00 0x00 terminator; decode escapes inline. Writes into a growing
        // pooled buffer so we don't need two passes.
        var rented = System.Buffers.ArrayPool<byte>.Shared.Rent(64);
        var length = 0;
        try
        {
            while (true)
            {
                var b = stream.ReadByte();
                if (b < 0)
                {
                    throw new EndOfStreamException("Stream ended mid ordered-string.");
                }

                if (b == FieldTerminator)
                {
                    var next = stream.ReadByte();
                    if (next < 0)
                    {
                        throw new EndOfStreamException("Stream ended mid ordered-string escape.");
                    }

                    if (next == FieldTerminator)
                    {
                        // End of string. Strict decode: the writer only ever emits well-formed
                        // UTF-8, so an invalid sequence here is corruption — fail loud instead
                        // of silently substituting U+FFFD and returning a string no writer wrote.
                        if (length == 0)
                        {
                            return string.Empty;
                        }

                        try
                        {
                            return s_strictUtf8.GetString(rented, 0, length);
                        }
                        catch (DecoderFallbackException ex)
                        {
                            throw new InvalidDataException(
                                "Ordered-string payload is not valid UTF-8 — the writer only emits " +
                                "well-formed UTF-8, so this indicates corruption.", ex);
                        }
                    }
                    if (next == NullByteEscape)
                    {
                        // Escaped literal 0x00.
                        AppendByte(ref rented, ref length, FieldTerminator);
                        continue;
                    }
                    throw new InvalidDataException(
                        $"Invalid escape byte 0x{next:X2} following 0x00 in ordered string.");
                }

                AppendByte(ref rented, ref length, (byte)b);
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static void AppendByte(ref byte[] rented, ref int length, byte b)
    {
        if (length == rented.Length)
        {
            var bigger = System.Buffers.ArrayPool<byte>.Shared.Rent(rented.Length * 2);
            Buffer.BlockCopy(rented, 0, bigger, 0, length);
            System.Buffers.ArrayPool<byte>.Shared.Return(rented);
            rented = bigger;
        }
        rented[length++] = b;
    }

    // ═════════════════════════════════════════════════════════════════════════════════════
    // Nullable discriminator — 0x00 (null, sorts first) / 0x01 (present).
    // ═════════════════════════════════════════════════════════════════════════════════════

    public static void WriteNullDiscriminator(Stream stream, bool hasValue)
    {
        ArgumentNullException.ThrowIfNull(stream);
        stream.WriteByte(hasValue ? (byte)1 : (byte)0);
    }

    public static bool ReadNullDiscriminator(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var b = stream.ReadByte();
        if (b < 0)
        {
            throw new EndOfStreamException("Stream ended while reading null discriminator.");
        }

        return b != 0;
    }

    // ═════════════════════════════════════════════════════════════════════════════════════
    // Guid — 16-byte canonical byte-comparable (big-endian).
    // ═════════════════════════════════════════════════════════════════════════════════════

    public static void WriteGuid(Stream stream, Guid value)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> buf = stackalloc byte[16];
        value.TryWriteBytes(buf, bigEndian: true, out _);
        stream.Write(buf);
    }

    public static Guid ReadGuid(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> buf = stackalloc byte[16];
        VarintHelpers.ReadExact(stream, buf);
        return new Guid(buf, bigEndian: true);
    }

    /// <summary>
    /// Compares two <see cref="Guid"/>s as their canonical 16-byte big-endian byte-lex
    /// representation — the same representation <see cref="WriteGuid"/> emits. Returns a
    /// negative value if <paramref name="left"/> sorts first, zero if equal, positive if
    /// <paramref name="right"/> sorts first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why not <see cref="Guid.CompareTo(Guid)"/>?</b> .NET's <see cref="Guid"/> lays out
    /// the four leading groups in little-endian fields (<c>data1</c>, <c>data2</c>,
    /// <c>data3</c>), but <see cref="WriteGuid"/> emits the canonical big-endian form. The
    /// two orderings disagree on every <see cref="Guid"/> whose first 8 bytes differ only in
    /// endian-swapped field boundaries — a large fraction of randomly-generated GUIDs. For
    /// ordered-wire <c>[Key]</c> types that contain a <see cref="Guid"/>, the in-memory
    /// <c>CompareTo</c> must track the byte-lex-of-encoded invariant, not <c>Guid</c>'s
    /// field-wise comparator, or SSTable binary search over Guid-keyed datasets silently
    /// misses keys that exist.
    /// </para>
    /// <para>
    /// Implementation: <see cref="Guid.TryWriteBytes(Span{byte}, bool, out int)"/> with
    /// <c>bigEndian: true</c> on both operands, then a <c>ReadOnlySpan&lt;byte&gt;.SequenceCompareTo</c>
    /// comparison. All on-stack, no allocations.
    /// </para>
    /// </remarks>
    public static int CompareGuidByteLex(Guid left, Guid right)
    {
        Span<byte> leftBytes = stackalloc byte[16];
        Span<byte> rightBytes = stackalloc byte[16];
        left.TryWriteBytes(leftBytes, bigEndian: true, out _);
        right.TryWriteBytes(rightBytes, bigEndian: true, out _);
        return leftBytes.SequenceCompareTo(rightBytes);
    }

    /// <summary>
    /// Compares two strings in the byte-lexicographic order of their ordered-wire UTF-8
    /// encodings — i.e., Unicode <i>code-point</i> order — without encoding either operand.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why not <see cref="string.CompareOrdinal(string, string)"/>?</b> Ordinal comparison
    /// orders UTF-16 <i>code units</i>; UTF-8 byte-lex orders <i>code points</i>. The two
    /// disagree exactly where supplementary-plane code points (U+10000 and above, encoded in
    /// UTF-16 as surrogate pairs starting 0xD800–0xDBFF) meet BMP code points in U+E000–U+FFFF:
    /// ordinal sorts the surrogate code units below 0xE000, while the UTF-8 encodings (lead
    /// bytes 0xF0–0xF4 vs 0xEE–0xEF) sort above. A generated <c>[Key]</c> <c>CompareTo</c>
    /// built on ordinal comparison would disagree with SSTable binary search over the encoded
    /// bytes for any key string containing a non-BMP character (every emoji, for instance).
    /// </para>
    /// <para>
    /// Implementation: compare UTF-16 code units, remapping each through the standard
    /// code-point-order transform (used by Lucene's <c>compareUTF16SortedAsUTF8</c>):
    /// units below 0xD800 are unchanged, units in 0xE000–0xFFFF shift down by 0x800, and
    /// surrogate units in 0xD800–0xDFFF shift up by 0x2000 — placing surrogate pairs (and
    /// therefore all supplementary code points) above every BMP code point, exactly matching
    /// UTF-8 byte order. When one string is a prefix of the other, the shorter sorts first,
    /// matching the wire's <c>0x00 0x00</c> terminator (which sorts below every data byte,
    /// including escaped <c>0x00</c> → <c>0x00 0xFF</c>). Ill-formed UTF-16 (unpaired
    /// surrogates) still receives a deterministic total order here — required because a
    /// default-initialized record may flow through in-memory comparison before
    /// <see cref="WriteOrderedString"/> rejects it at encode time.
    /// </para>
    /// </remarks>
    public static int CompareStringByteLex(string? left, string? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        if (left is null)
        {
            return -1;
        }

        if (right is null)
        {
            return 1;
        }

        var min = Math.Min(left.Length, right.Length);
        for (var i = 0; i < min; i++)
        {
            char l = left[i], r = right[i];
            if (l != r)
            {
                return Math.Sign(MapUtf16ToCodePointOrder(l) - MapUtf16ToCodePointOrder(r));
            }
        }

        return left.Length.CompareTo(right.Length);
    }

    // Remaps a UTF-16 code unit so that integer comparison of remapped units matches Unicode
    // code-point order (== UTF-8 byte-lex): [0x0000, 0xD7FF] unchanged; [0xE000, 0xFFFF] shifts
    // down to [0xD800, 0xF7FF]; surrogates [0xD800, 0xDFFF] shift up to [0xF800, 0xFFFF].
    private static int MapUtf16ToCodePointOrder(char c) =>
        c < 0xD800 ? c : c >= 0xE000 ? c - 0x800 : c + 0x2000;

    /// <summary>
    /// Compares two <see cref="DateTimeOffset"/> values in the byte-lexicographic order of
    /// their ordered-wire encodings: UTC instant first, then offset minutes as a tiebreaker.
    /// </summary>
    /// <remarks>
    /// <b>Why not <see cref="DateTimeOffset.CompareTo(DateTimeOffset)"/>?</b> The BCL
    /// comparator orders by UTC instant only, reporting two representations of the same
    /// instant at different offsets as equal — but <see cref="WriteDateTimeOffset"/> encodes
    /// the offset in the trailing two bytes, so their encodings differ. A comparator that
    /// reports 0 where the bytes differ breaks the byte-lex/CompareTo consistency invariant
    /// at the equality boundary: a memtable would dedup the two values as one key while the
    /// SSTable stores two distinct byte keys, so a lookup written at one offset and probed at
    /// the other silently misses. This comparator tracks the encoding: instant-primary,
    /// offset-minutes tiebreaker (lower offset first).
    /// </remarks>
    public static int CompareDateTimeOffsetByteLex(DateTimeOffset left, DateTimeOffset right)
    {
        var byInstant = left.UtcTicks.CompareTo(right.UtcTicks);
        if (byInstant != 0)
        {
            return byInstant;
        }

        // Same cast the writer applies: DateTimeOffset offsets are whole minutes in [-14h, +14h].
        return ((short)left.Offset.TotalMinutes).CompareTo((short)right.Offset.TotalMinutes);
    }

    // ═════════════════════════════════════════════════════════════════════════════════════
    // IEEE 754 floats — BE bytes with sign-bit flip (positives) / bit complement (negatives).
    // NaN rejected at encode time because NaN has no total-order position in the IEEE
    // comparison lattice — ordering would require an arbitrary tiebreaker that would
    // mask buggy callers. Cross-sign ordering under this scheme:
    //   large negative: bits start ~0x00..    (highest magnitude negative sorts first)
    //   small negative: bits start ~0x7F..
    //   +0:             bits start 0x80..
    //   small positive: bits start 0x80..
    //   large positive: bits start 0xFF..
    // -0 is normalized to +0 BEFORE the transform (its bit pattern 0x8000_0000 is replaced with 0)
    // so both encode identically to 0x8000_0000. This is required for byte-lex/CompareTo consistency:
    // the generated [Key] comparator is float.CompareTo, which treats -0 == +0 (-0.0f == 0.0f is
    // true), so a memtable dedups the two as one key — and the on-disk encoding must agree, or the
    // same logical key would occupy two distinct SSTable entries. Without the normalization the raw
    // transform would emit -0 -> 0x7FFF_FFFF (< +0), contradicting CompareTo's equality. NaN is
    // rejected at encode (above): it has no total-order byte position, so a NaN key cannot be persisted.
    // ═════════════════════════════════════════════════════════════════════════════════════

    public static void WriteHalf(Stream stream, Half value)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (Half.IsNaN(value))
        {
            throw new ArgumentException("NaN cannot be encoded as an ordered key — no total-order position in IEEE 754.", nameof(value));
        }

        var bits = BitConverter.HalfToUInt16Bits(value);
        if (bits == 0x8000) { bits = 0; } // normalize -0 to +0 (byte-lex must match CompareTo's -0 == +0)
        bits = (bits & 0x8000) == 0 ? (ushort)(bits ^ 0x8000) : (ushort)~bits;
        Span<byte> buf = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buf, bits);
        stream.Write(buf);
    }

    public static Half ReadHalf(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> buf = stackalloc byte[2];
        VarintHelpers.ReadExact(stream, buf);
        var bits = BinaryPrimitives.ReadUInt16BigEndian(buf);
        // Original sign bit set → encoded as XOR with 0x8000 (top bit becomes 1). Read-side:
        // top bit 1 means original was positive; top bit 0 means original was negative.
        bits = (bits & 0x8000) != 0 ? (ushort)(bits ^ 0x8000) : (ushort)~bits;
        return BitConverter.UInt16BitsToHalf(bits);
    }

    public static void WriteSingle(Stream stream, float value)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (float.IsNaN(value))
        {
            throw new ArgumentException("NaN cannot be encoded as an ordered key — no total-order position in IEEE 754.", nameof(value));
        }

        var bits = BitConverter.SingleToUInt32Bits(value);
        if (bits == 0x80000000u) { bits = 0u; } // normalize -0 to +0 (byte-lex must match CompareTo's -0 == +0)
        bits = (bits & 0x80000000u) == 0 ? bits ^ 0x80000000u : ~bits;
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buf, bits);
        stream.Write(buf);
    }

    public static float ReadSingle(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> buf = stackalloc byte[4];
        VarintHelpers.ReadExact(stream, buf);
        var bits = BinaryPrimitives.ReadUInt32BigEndian(buf);
        bits = (bits & 0x80000000u) != 0 ? bits ^ 0x80000000u : ~bits;
        return BitConverter.UInt32BitsToSingle(bits);
    }

    public static void WriteDouble(Stream stream, double value)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (double.IsNaN(value))
        {
            throw new ArgumentException("NaN cannot be encoded as an ordered key — no total-order position in IEEE 754.", nameof(value));
        }

        var bits = BitConverter.DoubleToUInt64Bits(value);
        if (bits == 0x8000_0000_0000_0000ul) { bits = 0ul; } // normalize -0 to +0 (byte-lex must match CompareTo's -0 == +0)
        bits = (bits & 0x8000_0000_0000_0000ul) == 0 ? bits ^ 0x8000_0000_0000_0000ul : ~bits;
        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(buf, bits);
        stream.Write(buf);
    }

    public static double ReadDouble(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> buf = stackalloc byte[8];
        VarintHelpers.ReadExact(stream, buf);
        var bits = BinaryPrimitives.ReadUInt64BigEndian(buf);
        bits = (bits & 0x8000_0000_0000_0000ul) != 0 ? bits ^ 0x8000_0000_0000_0000ul : ~bits;
        return BitConverter.UInt64BitsToDouble(bits);
    }

    // ═════════════════════════════════════════════════════════════════════════════════════
    // Int128 / UInt128 — 16 BE bytes. Signed: high-byte XOR 0x80 matches the 64-bit recipe
    // (equivalent to XOR with Int128.MinValue, which is 0x80 followed by 15 zero bytes).
    // ═════════════════════════════════════════════════════════════════════════════════════

    public static void WriteInt128(Stream stream, Int128 value)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> buf = stackalloc byte[16];
        BinaryPrimitives.WriteInt128BigEndian(buf, value);
        buf[0] ^= 0x80;
        stream.Write(buf);
    }

    public static Int128 ReadInt128(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> buf = stackalloc byte[16];
        VarintHelpers.ReadExact(stream, buf);
        buf[0] ^= 0x80;
        return BinaryPrimitives.ReadInt128BigEndian(buf);
    }

    public static void WriteUInt128(Stream stream, UInt128 value)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> buf = stackalloc byte[16];
        BinaryPrimitives.WriteUInt128BigEndian(buf, value);
        stream.Write(buf);
    }

    public static UInt128 ReadUInt128(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> buf = stackalloc byte[16];
        VarintHelpers.ReadExact(stream, buf);
        return BinaryPrimitives.ReadUInt128BigEndian(buf);
    }

    // ═════════════════════════════════════════════════════════════════════════════════════
    // DateTimeOffset — 10 bytes (8 BE UtcTicks + 2 BE sign-flipped OffsetMinutes).
    // Ordering semantic: UTC-instant primary, offset tiebreaker. Two DateTimeOffsets
    // representing the same UTC instant at different offsets sort adjacent with the lower
    // offset first, matching the UTC-normalisation convention of Avro's `timestamp-millis`
    // logical type and Protobuf's `google.protobuf.Timestamp` well-known type (both specs
    // define timestamps as instants normalised to UTC).
    // ═════════════════════════════════════════════════════════════════════════════════════

    public static void WriteDateTimeOffset(Stream stream, DateTimeOffset value)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> buf = stackalloc byte[10];
        // UtcTicks is always ≥ 0 for any reachable DateTimeOffset (min is DateTime.MinValue
        // at any valid offset — the UTC-equivalent tick count is non-negative). Plain BE on
        // the unsigned interpretation preserves numeric order.
        BinaryPrimitives.WriteUInt64BigEndian(buf[..8], (ulong)value.UtcTicks);
        // Offset range is [-14h, +14h] in minutes — fits in a short. Sign-flip recipe
        // matches WriteInt16.
        var offsetMinutes = (short)value.Offset.TotalMinutes;
        BinaryPrimitives.WriteInt16BigEndian(buf.Slice(8, 2), (short)(offsetMinutes ^ short.MinValue));
        stream.Write(buf);
    }

    public static DateTimeOffset ReadDateTimeOffset(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> buf = stackalloc byte[10];
        VarintHelpers.ReadExact(stream, buf);
        var utcTicks = (long)BinaryPrimitives.ReadUInt64BigEndian(buf[..8]);
        var flipped = BinaryPrimitives.ReadInt16BigEndian(buf.Slice(8, 2));
        var offsetMinutes = (short)(flipped ^ short.MinValue);

        // Range-guard the decoded parts before construction — a forged out-of-range tick count or an
        // offset beyond the ±14h cap otherwise leaks a raw ArgumentOutOfRangeException out of this
        // public ordered-wire read API (matches the sibling ReadDecimal's typed-corruption convention).
        return CorruptionGuards.ToDateTimeOffsetFromUtc(utcTicks, offsetMinutes);
    }

    // ═════════════════════════════════════════════════════════════════════════════════════
    // decimal — 17 bytes: 1-byte sign discriminator + 16-byte magnitude envelope.
    //
    // Sign discriminator: 0x00 negative, 0x01 zero, 0x02 positive. Placing it first makes
    // byte-lex agree with numeric order across the sign boundary.
    //
    // Magnitude envelope: 12 BE bytes of the 96-bit mantissa, 1 byte encoding (0xFF - scale)
    // — i.e., scale inverted so LARGER scale (smaller absolute value) sorts EARLIER when
    // the mantissa is equal — then 3 zero-pad bytes.
    //
    // Canonicalisation (Phase A2, DDIA 2e Ch. 5). Callers MUST declare the canonical scale
    // via [DecimalScale(N)] on the [Key] property; the generator forwards N to this helper.
    // Canonicalisation is VALUE-PRESERVING: raw scales below N are padded up (1m → 1.0000m)
    // and trailing-zero digits beyond N are trimmed (1.500000m → 1.5000m) so two values that
    // compare equal under decimal.Compare but carry different raw scales produce byte-identical
    // encodings. Values that CANNOT be represented exactly at scale N — excess fractional
    // precision (1.00005m at N=4) or magnitude overflow (|v| × 10^N beyond the 96-bit
    // mantissa) — are rejected with ArgumentException, because silently rounding or
    // under-scaling them breaks the SSTable binary-search invariant
    // byte-lex(encode(a), encode(b)) == sign(decimal.Compare(a, b)) against the
    // full-precision in-memory comparator.
    //
    // For negatives, magnitude bytes are complemented (two's-complement by bitwise NOT) so
    // that within the negative group a larger absolute value sorts earlier, matching
    // decimal.Compare.
    // ═════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Encodes <paramref name="value"/> as a 17-byte byte-lex-comparable decimal after
    /// normalising it to the canonical <paramref name="scale"/> prescribed by the caller's
    /// <c>[DecimalScale(N)]</c> declaration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>17-byte layout:</b>
    /// <list type="table">
    /// <item><term>byte 0</term><description>Discriminator: 0x00 = negative, 0x01 = zero, 0x02 = positive.</description></item>
    /// <item><term>bytes 1–4</term><description>96-bit mantissa high word (decimal bits[2]), big-endian. Complemented for negatives.</description></item>
    /// <item><term>bytes 5–8</term><description>96-bit mantissa mid word (decimal bits[1]), big-endian. Complemented for negatives.</description></item>
    /// <item><term>bytes 9–12</term><description>96-bit mantissa low word (decimal bits[0]), big-endian. Complemented for negatives.</description></item>
    /// <item><term>byte 13</term><description>Inverted scale (0xFF − rawScale) so equal-mantissa values with a larger scale sort before those with a smaller scale. Complemented for negatives.</description></item>
    /// <item><term>bytes 14–16</term><description>Zero padding (reserved for future expansion).</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// The canonicalisation step (trim or pad the raw scale to <paramref name="scale"/>,
    /// never changing the numeric value) ensures that two decimals that compare equal under
    /// <see cref="decimal.Compare"/> but carry different raw scales produce byte-identical
    /// output, preserving the SSTable binary-search invariant. Values not exactly
    /// representable at <paramref name="scale"/> — excess fractional precision or a magnitude
    /// too large to carry the declared scale within decimal's 96-bit mantissa — throw
    /// <see cref="ArgumentException"/>: quantise at the ingestion boundary before persisting.
    /// </para>
    /// </remarks>
    /// <param name="stream">The output stream.</param>
    /// <param name="value">The decimal value to encode.</param>
    /// <param name="scale">Canonical scale in [0, 28]. Values outside this range throw
    /// <see cref="ArgumentOutOfRangeException"/> — defence-in-depth against a missing or
    /// out-of-range generator-time check.</param>
    public static void WriteDecimal(Stream stream, decimal value, int scale)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (scale < 0 || scale > 28)
        {
            throw new ArgumentOutOfRangeException(nameof(scale), scale,
                "Decimal scale must be in [0, 28]. See Boutquin.Storage.Domain.Attributes.DecimalScaleAttribute.");
        }

        // Canonicalise to the declared scale before encoding so cross-scale equal values
        // produce byte-identical output. Canonicalisation must be VALUE-PRESERVING — the
        // generated [Key] comparator is decimal.CompareTo at full precision, so any encode-time
        // value change (silent rounding) would make two unequal keys byte-identical, breaking
        // sign(CompareTo) == sign(byte-lex) at the equality boundary and making
        // Deserialize(Serialize(v)) != v. Excess fractional precision is therefore REJECTED,
        // not rounded away (the same fail-fast policy as NaN floats and unpaired surrogates).
        var rounded = decimal.Round(value, scale, MidpointRounding.ToEven);
        if (rounded != value)
        {
            throw new ArgumentException(
                $"Decimal value {value} has more fractional digits than the declared scale {scale}. " +
                "Ordered-wire decimal keys must be stored at their declared [DecimalScale]; round or " +
                "quantise at the ingestion boundary before persisting.", nameof(value));
        }

        // Value-preserving normalisation: Round never changes the numeric value here (checked
        // above) but trims excess trailing-zero digits (1.500000m raw scale 6 → 1.5000 scale 4),
        // so the padding step below starts from a scale ≤ the declared scale.
        value = rounded;

        // decimal values may carry a scale less than the declared scale (e.g., 1m has scale 0,
        // not 1.0000m's scale 4). Pad the scale up to the declared value by adding a zero at
        // the target scale — decimal addition preserves max(left.Scale, right.Scale), which
        // forces the canonical scale on values that started with fewer fractional digits. This
        // is the load-bearing step that makes cross-scale equivalent values (1m vs 1.0000m)
        // produce byte-identical output.
        //
        // The padding can silently UNDER-shoot: when |value| × 10^scale exceeds the 96-bit
        // mantissa, decimal addition rescales the result down to keep it representable, leaving
        // the stored scale below the declared scale. Differently-scaled encodings compare
        // mantissa bytes that aren't digit-aligned, contradicting decimal.Compare — so a
        // post-padding scale shortfall is rejected (verified below after GetBits).
        if (scale > 0)
        {
            var scalePadding = new decimal(lo: 0, mid: 0, hi: 0, isNegative: false, scale: (byte)scale);
            value += scalePadding;
        }

        Span<int> bits = stackalloc int[4];
        decimal.GetBits(value, bits);

        var isNegative = (bits[3] & unchecked((int)0x8000_0000)) != 0;
        var rawScale = (byte)((bits[3] >> 16) & 0x7F);

        // Scale-padding shortfall check (see the canonicalisation comment above): every encoded
        // value must carry exactly the declared scale, or byte-lex order across values breaks.
        // Zero is exempt — it encodes through the dedicated canonical-zero branch below, which
        // never writes mantissa or scale bytes.
        var isMagnitudeZero = bits[0] == 0 && bits[1] == 0 && bits[2] == 0;
        if (!isMagnitudeZero && rawScale != scale)
        {
            throw new ArgumentException(
                $"Decimal value {value} cannot be represented at the declared scale {scale}: " +
                $"|value| × 10^{scale} exceeds decimal's 96-bit mantissa (canonicalisation reached " +
                $"scale {rawScale} only). Ordered-wire byte-lex ordering requires every key to carry " +
                "the declared scale; use a smaller [DecimalScale] or a narrower value domain.", nameof(value));
        }

        // Deliberately no trailing-zero mantissa normalisation here. The A2 canonicalisation
        // above has already driven every equivalent value to the same (mantissa, scale) pair;
        // stripping trailing zeros now would UNDO that work on values whose mantissa happens
        // to end in zero (e.g., 7.36e19 at scale 4 has a trailing decimal zero; stripping it
        // would give a different scale byte from the co-canonical 2.26e22 pair, flipping
        // byte-lex order within the negatives' complement region).

        Span<byte> buf = stackalloc byte[17];
        if (isMagnitudeZero)
        {
            buf[0] = 0x01;
            // Remaining 16 bytes are 0x00 — zero's magnitude is canonical.
        }
        else
        {
            buf[0] = isNegative ? (byte)0x00 : (byte)0x02;
            // 96-bit mantissa as 12 BE bytes: high word in bits[2] sits at buf[1..5],
            // mid at buf[5..9], low at buf[9..13].
            BinaryPrimitives.WriteUInt32BigEndian(buf.Slice(1, 4), (uint)bits[2]);
            BinaryPrimitives.WriteUInt32BigEndian(buf.Slice(5, 4), (uint)bits[1]);
            BinaryPrimitives.WriteUInt32BigEndian(buf.Slice(9, 4), (uint)bits[0]);
            // Inverted scale so (equal mantissa, larger scale) → smaller byte → sorts first.
            // rawScale reflects the post-normalisation stored scale; under the A2
            // canonicalisation contract it agrees with the declared scale when no trailing
            // zeros were stripped.
            buf[13] = (byte)(0xFF - rawScale);
            // buf[14..17] are zero pad.
            if (isNegative)
            {
                // Complement magnitude (bytes 1..17) so within-negative ordering inverts to
                // match decimal.Compare. The discriminator (byte 0) is already 0x00, which
                // sorts before the complemented-positive magnitudes; no extra work there.
                for (var i = 1; i < 17; i++)
                {
                    buf[i] = (byte)~buf[i];
                }
            }
        }
        stream.Write(buf);
    }

    public static decimal ReadDecimal(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> buf = stackalloc byte[17];
        VarintHelpers.ReadExact(stream, buf);

        var discriminator = buf[0];
        if (discriminator == 0x01)
        {
            return decimal.Zero;
        }

        // Discriminator byte is only valid as 0x00 (negative), 0x01 (zero, handled above),
        // or 0x02 (positive). Any other value is unrecognised on-disk data — fail closed
        // rather than decoding garbage or surfacing a cryptic OverflowException from the
        // decimal(int,int,int,bool,byte) constructor when the scale byte overflows.
        if (discriminator != 0x00 && discriminator != 0x02)
        {
            throw new InvalidDataException(
                $"OrderedByteHelpers.ReadDecimal: unrecognised decimal discriminator byte 0x{discriminator:X2}. " +
                "Expected 0x00 (negative), 0x01 (zero), or 0x02 (positive). " +
                "The stream contains corrupt or foreign data.");
        }

        var isNegative = discriminator == 0x00;
        if (isNegative)
        {
            for (var i = 1; i < 17; i++)
            {
                buf[i] = (byte)~buf[i];
            }
        }

        var high = BinaryPrimitives.ReadUInt32BigEndian(buf.Slice(1, 4));
        var mid = BinaryPrimitives.ReadUInt32BigEndian(buf.Slice(5, 4));
        var low = BinaryPrimitives.ReadUInt32BigEndian(buf.Slice(9, 4));
        var scale = (byte)(0xFF - buf[13]);

        // The scale byte is the same untrusted on-disk surface as the discriminator: a valid
        // writer only emits 0..28 (inverted to 0xE3..0xFF), so anything past the decimal cap
        // is corrupt or foreign data — fail closed instead of letting the decimal constructor
        // surface a cryptic ArgumentOutOfRangeException.
        if (scale > 28)
        {
            throw new InvalidDataException(
                $"OrderedByteHelpers.ReadDecimal: scale byte 0x{buf[13]:X2} decodes to scale {scale}, " +
                "but decimal supports scales 0..28. The stream contains corrupt or foreign data.");
        }

        return new decimal((int)low, (int)mid, (int)high, isNegative, scale);
    }

}
