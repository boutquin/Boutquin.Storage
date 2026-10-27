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
namespace Boutquin.Storage.Domain.Helpers;

/// <summary>
/// Low-level varint / zigzag / tag-and-wire-type primitives shared across every consumer of
/// the Boutquin.Storage tagged-wire format. The Phase 2 source generator emits code that
/// calls these helpers directly; the shipped <c>SerializableWrapper&lt;T&gt;</c> and every
/// hand-rolled <c>ISerializable&lt;T&gt;</c> path in Infrastructure does the same.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why shared in Domain rather than generator-emitted per assembly?</b> An earlier design
/// contemplated emitting <c>__BoutquinStorageVarintHelpers</c> once per consuming assembly.
/// Implementation reality is that Boutquin.Storage.Domain itself needs varint I/O for
/// <c>SerializableWrapper&lt;T&gt;</c> to match the consumer wire format, and Domain has no
/// <c>[StorageSerializable]</c> type to trigger generator emission. Shipping the helpers as
/// public API in Domain gives every consumer (the generator-emitted code, the wrapper,
/// hand-rolled serializers) a single implementation to depend on. Helper emission strategy
/// was an open design question; this file resolves it in favour of a shared public helper class.
/// </para>
/// <para>
/// <b>ULEB128 format.</b> Each byte carries 7 bits of payload in the low bits; the high bit
/// is a continuation flag (1 = more bytes follow, 0 = final byte). The encoding is little-
/// endian at the 7-bit-group level. The same format underlies Protobuf, Thrift, and WASM
/// value encoding — DDIA 2nd ed Ch. 5 documents its provenance.
/// </para>
/// <para>
/// <b>ZigZag.</b> Signed integers map to unsigned via <c>(n &lt;&lt; 1) ^ (n &gt;&gt; 63)</c>
/// (for Int64) so that small negatives (−1, −2) compact to one varint byte instead of the full
/// 10 bytes a naive two's-complement varint would require for any negative — the Protobuf
/// <c>sint*</c> encoding (DDIA 2e Ch. 5 describes the underlying varint compaction, e.g.
/// "numbers from −64 to 63 are encoded in one byte").
/// </para>
/// <para>
/// <b>Thread safety.</b> All members are pure static methods operating on stream parameters.
/// The caller controls stream synchronization; this class holds no state.
/// </para>
/// </remarks>
public static class VarintHelpers
{
    private const int MaxVarInt64Bytes = 10;

    /// <summary>
    /// Writes an unsigned 64-bit value as ULEB128. Returns the number of bytes written
    /// (range 1–10). Small values produce short encodings: 0–127 in 1 byte, 128–16 383 in
    /// 2 bytes, and so on.
    /// </summary>
    public static int WriteVarUInt64(Stream stream, ulong value)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var count = 0;
        while (value >= 0x80)
        {
            stream.WriteByte((byte)(value | 0x80));
            value >>= 7;
            count++;
        }
        stream.WriteByte((byte)value);
        return count + 1;
    }

    /// <summary>
    /// Writes <paramref name="value"/> as a fixed-width <paramref name="width"/>-byte ULEB128
    /// into <paramref name="destination"/>. The first <c>width − 1</c> bytes carry the
    /// continuation bit set so the encoding remains a valid ULEB128 sequence regardless of
    /// the value's natural canonical width; the final byte clears the continuation bit.
    /// Used by the Phase-2 tagged-wire emitter to backpatch length and count prefixes whose
    /// width is reserved before the body bytes are known
    /// (see <see cref="PooledMemoryStream.Reserve(int)"/>).
    /// </summary>
    /// <param name="destination">
    /// Span receiving the encoded bytes. Must be at least <paramref name="width"/> long; only
    /// the first <paramref name="width"/> bytes are written.
    /// </param>
    /// <param name="value">The unsigned value to encode.</param>
    /// <param name="width">
    /// Number of bytes the encoding must occupy. Must be in <c>[1, 10]</c>.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="width"/> is outside <c>[1, 10]</c>, or when
    /// <paramref name="value"/> exceeds the bit-capacity of <paramref name="width"/>
    /// continuation-padded bytes (the value's effective domain is
    /// <c>[0, 2^(7 × width) − 1]</c>, capped at <see cref="ulong.MaxValue"/> for
    /// <paramref name="width"/> = 10).
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="destination"/> has fewer than <paramref name="width"/>
    /// bytes available.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <b>Why the encoding stays valid.</b> A reader of canonical ULEB128 walks bytes left to
    /// right, accumulating the low 7 bits of each byte at increasing 7-bit shifts, and stops
    /// at the first byte whose continuation bit (0x80) is clear. Setting the continuation bit
    /// on every byte except the last and zero-padding the high payload bits produces a valid
    /// — but non-canonical — encoding that decodes to the same value. The reader at
    /// <see cref="ReadVarUInt64(Stream)"/> reads up to 10 bytes and only checks for overflow
    /// on the 10th byte, so any width in <c>[1, 10]</c> is accepted.
    /// </para>
    /// <para>
    /// <b>Wire-size cost.</b> Padding inflates the encoding by <c>(width − canonical_width)</c>
    /// bytes per prefix. For the Phase-2 count-prefixed and annotated collection paths the
    /// emitter pads both LEN and count to the maximum 10 bytes, costing up to 18 extra bytes
    /// per collection field versus a canonical encoding. The cost buys back one
    /// <see cref="PooledMemoryStream"/> rental per collection on the write hot path and a
    /// further buffer-to-buffer copy.
    /// </para>
    /// </remarks>
    public static void WriteVarUInt64Padded(Span<byte> destination, ulong value, int width)
    {
        if (width < 1 || width > MaxVarInt64Bytes)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width,
                $"Padded varint width must be in [1, {MaxVarInt64Bytes}].");
        }
        if (destination.Length < width)
        {
            throw new ArgumentException(
                $"Destination span has {destination.Length} bytes; padded varint requires {width}.",
                nameof(destination));
        }

        var original = value;
        for (var i = 0; i < width - 1; i++)
        {
            destination[i] = (byte)((value & 0x7F) | 0x80);
            value >>= 7;
        }
        destination[width - 1] = (byte)(value & 0x7F);
        value >>= 7;
        if (value != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), original,
                $"Value exceeds the bit-capacity of a {width}-byte padded varint.");
        }
    }

    /// <summary>
    /// Reads a ULEB128-encoded unsigned 64-bit integer.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// Varint is malformed (more than 10 bytes or overflow on the 10th byte).
    /// </exception>
    /// <exception cref="EndOfStreamException">Stream ends mid-varint.</exception>
    public static ulong ReadVarUInt64(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ulong result = 0;
        var shift = 0;
        for (var i = 0; i < MaxVarInt64Bytes; i++)
        {
            var b = stream.ReadByte();
            if (b < 0)
            {
                throw new EndOfStreamException("Stream ended while reading varint.");
            }

            // Final byte on a 10-byte encoding must only set bit 0 of the high block,
            // otherwise the resulting value overflows UInt64.
            if (i == MaxVarInt64Bytes - 1 && (b & 0xFE) != 0)
            {
                throw new InvalidDataException("Varint overflow on 10th byte.");
            }

            result |= ((ulong)(b & 0x7F)) << shift;
            if ((b & 0x80) == 0)
            {
                return result;
            }
            shift += 7;
        }
        throw new InvalidDataException("Varint exceeds maximum length of 10 bytes.");
    }

    /// <summary>ZigZag-encodes a signed 64-bit integer.</summary>
    public static ulong ZigZagEncode(long value) => (ulong)((value << 1) ^ (value >> 63));

    /// <summary>Reverses <see cref="ZigZagEncode"/>.</summary>
    public static long ZigZagDecode(ulong value) => (long)(value >> 1) ^ -(long)(value & 1);

    /// <summary>Writes a zigzag-encoded signed 64-bit integer.</summary>
    public static int WriteZigZagInt64(Stream stream, long value) => WriteVarUInt64(stream, ZigZagEncode(value));

    /// <summary>Reads a zigzag-encoded signed 64-bit integer.</summary>
    public static long ReadZigZagInt64(Stream stream) => ZigZagDecode(ReadVarUInt64(stream));

    /// <summary>
    /// Writes a combined tag-and-wire-type varint. Low 3 bits of the emitted value carry the
    /// <see cref="WireType"/>; the upper bits carry the tag number. Tag numbers must be
    /// positive; <see cref="WireType"/> must be a defined enum value.
    /// </summary>
    /// <remarks>
    /// The wire-format specification's documented envelope for user tags is 1 .. 2²⁸ − 1
    /// (the generator enforces it as BSSG012). This helper — and the reader, which tolerates
    /// any tag up to <see cref="int.MaxValue"/> — deliberately accepts the wider range: the
    /// reader's tolerance is corruption robustness, not writer license. Hand-rolled
    /// <see cref="Boutquin.Storage.Domain.Interfaces.ISerializable{T}"/> writers should stay
    /// within the documented envelope.
    /// </remarks>
    public static int WriteTag(Stream stream, int tagNumber, WireType wireType)
    {
        if (tagNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tagNumber), tagNumber,
                "Tag numbers must be positive; tag 0 is reserved for the schema-format version.");
        }

        if (!Enum.IsDefined(wireType))
        {
            throw new ArgumentOutOfRangeException(nameof(wireType), wireType,
                "Wire type must be a defined WireType value; an undefined value would corrupt the tag bits of the key.");
        }

        var combined = ((ulong)tagNumber << 3) | (byte)wireType;
        return WriteVarUInt64(stream, combined);
    }

    /// <summary>
    /// Reads a tag-and-wire-type varint. Returns <see langword="false"/> on clean end-of-stream
    /// (no bytes available); the caller treats that as "no more fields". Throws on partial reads.
    /// </summary>
    public static bool TryReadTag(Stream stream, out int tagNumber, out WireType wireType)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var first = stream.ReadByte();
        if (first < 0)
        {
            tagNumber = 0;
            wireType = WireType.Varint;
            return false;
        }

        // Re-stitch the first byte into the ULEB128 loop. One-byte case is the hot path
        // (tag numbers 1..15 all encode in a single byte), so it's worth keeping it direct.
        ulong combined;
        if ((first & 0x80) == 0)
        {
            combined = (ulong)(first & 0x7F);
        }
        else
        {
            combined = (ulong)(first & 0x7F);
            var shift = 7;
            for (var i = 1; i < MaxVarInt64Bytes; i++)
            {
                var b = stream.ReadByte();
                if (b < 0)
                {
                    throw new EndOfStreamException("Stream ended mid-tag-varint.");
                }

                if (i == MaxVarInt64Bytes - 1 && (b & 0xFE) != 0)
                {
                    throw new InvalidDataException("Tag varint overflow on 10th byte.");
                }

                combined |= ((ulong)(b & 0x7F)) << shift;
                if ((b & 0x80) == 0)
                {
                    goto done;
                }
                shift += 7;
            }
            throw new InvalidDataException("Tag varint exceeds maximum length of 10 bytes.");
        }

    done:
        var wt = (byte)(combined & 0x07);
        if (wt > (byte)WireType.Fixed128)
        {
            throw new InvalidDataException($"Unknown wire type {wt}.");
        }

        wireType = (WireType)wt;

        // A valid ≤10-byte varint can carry tag bits up to 2^61; an unchecked (int) cast would
        // keep the low 32 bits, so a corrupt tag of 2^32 + N COLLIDES with real tag N and feeds
        // garbage into that field instead of failing. No legitimate writer produces such a tag
        // (the generator caps tags at 2^28 − 1) — treat it as corruption.
        var rawTag = combined >> 3;
        if (rawTag > int.MaxValue)
        {
            throw new InvalidDataException(
                $"Tag varint decodes to tag number {rawTag}, beyond the supported range — corrupt or foreign stream.");
        }

        tagNumber = (int)rawTag;
        return true;
    }

    /// <summary>
    /// Advances the stream past the payload of a field whose <paramref name="wireType"/> the
    /// reader doesn't recognize. Used by generated <c>Deserialize</c> loops to implement
    /// forward-compatibility via unknown-tag skip.
    /// </summary>
    public static void SkipField(Stream stream, WireType wireType)
    {
        ArgumentNullException.ThrowIfNull(stream);
        switch (wireType)
        {
            case WireType.Varint:
                {
                    // Drain continuation bytes without reconstructing the value — the
                    // MSB of each byte is 1 while more bytes follow, 0 on the final byte.
                    // Bounded to the same 10-byte maximum ReadVarUInt64 enforces: without
                    // the cap, malformed-data acceptance would depend on whether the tag is
                    // known to the reader (known field → throw; unknown field → skipped).
                    var count = 0;
                    int b;
                    do
                    {
                        if (++count > MaxVarInt64Bytes)
                        {
                            throw new InvalidDataException(
                                "Varint being skipped exceeds maximum length of 10 bytes.");
                        }

                        b = stream.ReadByte();
                        if (b < 0)
                        {
                            throw new InvalidDataException("Stream ended while skipping a Varint field.");
                        }

                        // Mirror ReadVarUInt64's 10th-byte overflow rejection: a final byte
                        // setting more than bit 0 encodes a value past UInt64. Without this,
                        // acceptance of the overflow class would depend on whether the tag is
                        // known (read → throw) or unknown (skip → silently accepted) — exactly
                        // the tag-dependence the length cap above exists to eliminate.
                        if (count == MaxVarInt64Bytes && (b & 0xFE) != 0)
                        {
                            throw new InvalidDataException("Varint overflow on 10th byte.");
                        }
                    }
                    while ((b & 0x80) != 0);
                    return;
                }
            case WireType.Fixed16:
                AdvanceExact(stream, 2);
                return;
            case WireType.Fixed32:
                AdvanceExact(stream, 4);
                return;
            case WireType.Fixed64:
                AdvanceExact(stream, 8);
                return;
            case WireType.Fixed128:
                AdvanceExact(stream, 16);
                return;
            case WireType.LengthPrefixed:
                {
                    var length = ReadVarUInt64(stream);
                    // The declared length is corruption-controlled data. On a seekable stream,
                    // reject a length beyond the bytes remaining; on a non-seekable stream, drain
                    // chunk-by-chunk but treat a short stream as corruption — never an unbounded
                    // drain or a raw EndOfStreamException. Both surface the typed corruption
                    // contract so a skipped (unknown-tag) field fails closed exactly like a read field.
                    SkipExactBounded(stream, (long)length);
                    return;
                }
            default:
                throw new InvalidDataException($"Unknown wire type {wireType}.");
        }
    }

    // Strict UTF-8: throws on ill-formed UTF-16 (unpaired surrogates) at write and on invalid
    // UTF-8 byte sequences at read, instead of silently substituting U+FFFD. Replacement
    // substitution breaks Deserialize(Serialize(s)) == s with no error — the same silent-loss
    // class the ordered-wire string writer and the NaN-float key writer reject fail-fast.
    private static readonly UTF8Encoding s_strictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Writes a length-prefixed UTF-8 string: varint byte-count followed by the UTF-8 bytes.
    /// <paramref name="value"/> must be non-null; the generator emits a null-guard upstream
    /// so callers here can rely on that contract. Ill-formed UTF-16 (an unpaired surrogate)
    /// throws <see cref="ArgumentException"/> — it cannot round-trip through UTF-8.
    /// </summary>
    public static void WriteString(Stream stream, string value)
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
                "Tagged-wire strings must be well-formed UTF-16: an unpaired surrogate cannot be " +
                "encoded in UTF-8, and replacement substitution (U+FFFD) would silently break " +
                "round-trip exactness.", nameof(value), ex);
        }

        WriteVarUInt64(stream, (ulong)byteCount);
        if (byteCount == 0)
        {
            return;
        }
        // Stack-alloc threshold of 256 bytes avoids a heap allocation for short keys/strings;
        // larger strings rent from the pool to keep peak allocation bounded.
        if (byteCount <= 256)
        {
            Span<byte> buf = stackalloc byte[byteCount];
            s_strictUtf8.GetBytes(value, buf);
            stream.Write(buf);
        }
        else
        {
            var rented = System.Buffers.ArrayPool<byte>.Shared.Rent(byteCount);
            try
            {
                s_strictUtf8.GetBytes(value, rented.AsSpan(0, byteCount));
                stream.Write(rented, 0, byteCount);
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    /// <summary>
    /// Reads a ULEB128 varint that frames a LENGTH or COUNT and narrows it to <see cref="int"/>.
    /// A value past <see cref="int.MaxValue"/> cannot index a CLR array/span at all, so it is
    /// stream corruption by construction and surfaces as the typed corruption exception — a bare
    /// <c>checked((int)…)</c> at the call site would leak a raw <see cref="OverflowException"/>
    /// across the deserializer boundary (the same convention <see cref="ReadString"/> applies to
    /// its own length prefix). Used by the generated tagged-wire readers for payload lengths,
    /// collection block lengths, element counts, and nested-record lengths.
    /// </summary>
    /// <param name="stream">The source stream positioned at the varint.</param>
    /// <param name="description">What the varint frames — used in the corruption message.</param>
    /// <exception cref="InvalidDataException">The varint exceeds <see cref="int.MaxValue"/>.</exception>
    public static int ReadVarInt32LengthOrCount(Stream stream, string description)
    {
        var raw = ReadVarUInt64(stream);
        if (raw > int.MaxValue)
        {
            throw new InvalidDataException(
                $"{description} varint {raw} exceeds Int32.MaxValue — forged or corrupt stream.");
        }

        return (int)raw;
    }

    /// <summary>Reads a length-prefixed UTF-8 string.</summary>
    /// <exception cref="InvalidDataException">
    /// The declared length is corrupt: it exceeds <see cref="int.MaxValue"/>, exceeds the bytes a
    /// seekable stream can supply, or the (non-seekable) stream ends before the declared length is
    /// read. The length prefix is corruption-controlled data, so an out-of-range or unsatisfiable
    /// declaration is the repo's documented corruption contract — surfaced as a typed exception
    /// rather than a raw <see cref="OverflowException"/> or <see cref="EndOfStreamException"/>.
    /// </exception>
    public static string ReadString(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var length = ReadVarUInt64(stream);
        if (length == 0)
        {
            return string.Empty;
        }

        // The declared length is corruption-controlled data. A length above int.MaxValue cannot
        // index a CLR array/span at all — surface the typed corruption exception rather than the
        // raw OverflowException a `checked((int)length)` would throw (a caller catching the
        // documented InvalidDataException contract would otherwise miss it).
        if (length > int.MaxValue)
        {
            throw new InvalidDataException(
                $"Declared string length {length} exceeds int.MaxValue — corrupt or foreign length prefix.");
        }

        var len = (int)length;

        // Seekable streams: bound the declared length by the bytes actually remaining before
        // allocating, or a corrupt prefix declaring int.MaxValue rents ~2 GB before the read
        // observes the short stream.
        if (stream.CanSeek && len > stream.Length - stream.Position)
        {
            throw new InvalidDataException(
                $"Declared string length {len} exceeds the {stream.Length - stream.Position} bytes remaining in the stream — corrupt length prefix.");
        }

        if (len <= 256)
        {
            Span<byte> buf = stackalloc byte[len];
            ReadExactBounded(stream, buf);
            return DecodeStrictUtf8(buf);
        }

        // Non-seekable streams cannot be pre-bounded above, so never rent the full declared length
        // up front — a forged int.MaxValue would OOM. Read into a bounded, geometrically-growing
        // pooled buffer so peak allocation tracks the bytes the stream actually supplies.
        var (rented, read) = ReadAllBounded(stream, len);
        try
        {
            return DecodeStrictUtf8(rented.AsSpan(0, read));
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(rented);
        }
    }

    // Reads exactly bytes.Length bytes, mapping a short stream to the typed corruption contract
    // (InvalidDataException) instead of the raw EndOfStreamException ReadExact surfaces — used on
    // declared-length payload reads where a short stream means a corrupt/forged length prefix.
    private static void ReadExactBounded(Stream stream, Span<byte> bytes)
    {
        var total = 0;
        while (total < bytes.Length)
        {
            var read = stream.Read(bytes[total..]);
            if (read <= 0)
            {
                throw new InvalidDataException(
                    $"Declared length {bytes.Length} exceeds the {total} bytes the stream supplied — corrupt or forged length prefix.");
            }
            total += read;
        }
    }

    // Reads up to `declaredLength` bytes into a pooled buffer that grows geometrically from a small
    // seed, so a forged huge declaredLength never pre-allocates: peak allocation tracks the bytes
    // the stream actually supplies. A stream that ends before `declaredLength` is corruption (the
    // length prefix over-declared) and surfaces the typed corruption exception. The caller owns
    // returning the rented buffer.
    private static (byte[] Rented, int Read) ReadAllBounded(Stream stream, int declaredLength)
    {
        // Cap the initial rent so an int.MaxValue declaration can't drive a giant up-front rent;
        // the buffer doubles as real bytes arrive.
        var capacity = Math.Min(declaredLength, 4096);
        var rented = System.Buffers.ArrayPool<byte>.Shared.Rent(capacity);
        var read = 0;
        try
        {
            while (read < declaredLength)
            {
                if (read == rented.Length)
                {
                    var grown = System.Buffers.ArrayPool<byte>.Shared.Rent(
                        // Use long arithmetic before clamping to int to avoid int overflow when
                        // rented.Length is close to int.MaxValue/2 (e.g., after several doublings
                        // from the 4096 seed). Without the cast, rented.Length * 2 overflows to a
                        // negative value, causing ArrayPool.Rent to throw ArgumentOutOfRangeException.
                        (int)Math.Min((long)declaredLength, Math.Min((long)rented.Length * 2, (long)int.MaxValue)));
                    Array.Copy(rented, grown, read);
                    System.Buffers.ArrayPool<byte>.Shared.Return(rented);
                    rented = grown;
                }

                var n = stream.Read(rented, read, Math.Min(rented.Length, declaredLength) - read);
                if (n <= 0)
                {
                    throw new InvalidDataException(
                        $"Declared length {declaredLength} exceeds the {read} bytes the stream supplied — corrupt or forged length prefix.");
                }
                read += n;
            }

            return (rented, read);
        }
        catch
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(rented);
            throw;
        }
    }

    // Strict decode: the writer only ever emits well-formed UTF-8, so an invalid sequence is
    // corruption — fail loud instead of silently substituting U+FFFD and returning a string
    // no writer ever wrote.
    private static string DecodeStrictUtf8(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return s_strictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException(
                "Length-prefixed string payload is not valid UTF-8 — the writer only emits " +
                "well-formed UTF-8, so this indicates corruption.", ex);
        }
    }

    /// <summary>
    /// Reads exactly <paramref name="declaredLength"/> bytes of a length-prefixed payload into a
    /// freshly-allocated right-sized array, bounding the work by the bytes the stream actually
    /// supplies so a forged/oversized declared length on a non-seekable stream never pre-allocates
    /// the full declared size. A stream that ends before <paramref name="declaredLength"/> is
    /// corruption — the length prefix over-declared — and surfaces <see cref="InvalidDataException"/>.
    /// </summary>
    /// <param name="stream">The source stream, positioned at the first payload byte.</param>
    /// <param name="declaredLength">
    /// The declared payload length (already validated to be in <c>[0, int.MaxValue]</c> by the
    /// caller's length-prefix guard).
    /// </param>
    /// <returns>A new array of exactly <paramref name="declaredLength"/> bytes.</returns>
    /// <exception cref="InvalidDataException">
    /// The stream ends before <paramref name="declaredLength"/> bytes are read — a corrupt or
    /// forged length prefix.
    /// </exception>
    public static byte[] ReadLengthPrefixedPayloadBounded(Stream stream, int declaredLength)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegative(declaredLength);
        if (declaredLength == 0)
        {
            return [];
        }

        var (rented, read) = ReadAllBounded(stream, declaredLength);
        try
        {
            // read == declaredLength on success (ReadAllBounded throws otherwise).
            return rented.AsSpan(0, read).ToArray();
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>Reads exactly <paramref name="bytes"/>.Length bytes or throws.</summary>
    public static void ReadExact(Stream stream, Span<byte> bytes)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var total = 0;
        while (total < bytes.Length)
        {
            var read = stream.Read(bytes[total..]);
            if (read <= 0)
            {
                throw new EndOfStreamException(
                    $"Expected {bytes.Length} bytes, only read {total} before end of stream.");
            }
            total += read;
        }
    }

    // Skips a declared-length payload, mapping every corruption signal (over-declared length,
    // short stream) to the typed corruption contract rather than a raw EndOfStreamException or an
    // unbounded drain. On a seekable stream the past-end check is O(1); on a non-seekable stream
    // the drain reads into a fixed scratch buffer, so peak allocation is bounded regardless of the
    // declared length.
    private static void SkipExactBounded(Stream stream, long count)
    {
        if (count < 0)
        {
            throw new InvalidDataException(
                $"Declared length-prefixed field length is negative or exceeds long.MaxValue — corrupt or forged length prefix.");
        }

        if (stream.CanSeek)
        {
            // Subtraction form: a declared count near long.MaxValue would wrap Position + count
            // negative, pass the bound and reach a raw Seek. Length - Position cannot overflow.
            if (count > stream.Length - stream.Position)
            {
                throw new InvalidDataException(
                    $"Declared length-prefixed field length {count} exceeds the {stream.Length - stream.Position} bytes remaining — corrupt or forged length prefix.");
            }
            stream.Seek(count, SeekOrigin.Current);
            return;
        }

        const int ScratchSize = 512;
        Span<byte> scratch = stackalloc byte[ScratchSize];
        var remaining = count;
        while (remaining > 0)
        {
            var want = (int)Math.Min(remaining, ScratchSize);
            var slice = scratch[..want];
            var total = 0;
            while (total < want)
            {
                var read = stream.Read(slice[total..]);
                if (read <= 0)
                {
                    throw new InvalidDataException(
                        $"Declared length-prefixed field length {count} exceeds the bytes the stream supplied — corrupt or forged length prefix.");
                }
                total += read;
            }
            remaining -= want;
        }
    }

    private static void AdvanceExact(Stream stream, long count)
    {
        if (stream.CanSeek)
        {
            if (stream.Position + count > stream.Length)
            {
                throw new EndOfStreamException(
                    $"Attempted to skip {count} bytes past end of stream.");
            }
            stream.Seek(count, SeekOrigin.Current);
            return;
        }

        // Non-seekable stream — drain into a scratch buffer. Cap per-iteration allocation.
        const int ScratchSize = 512;
        Span<byte> scratch = stackalloc byte[ScratchSize];
        var remaining = count;
        while (remaining > 0)
        {
            var chunk = (int)Math.Min(remaining, ScratchSize);
            ReadExact(stream, scratch[..chunk]);
            remaining -= chunk;
        }
    }
}
