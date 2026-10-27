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
using Boutquin.Storage.Domain.Helpers;

namespace Boutquin.Storage.Infrastructure.TimeSeries.Columnar.Codecs;

/// <summary>
/// Dictionary encoding for low-cardinality columns — distinct values are stored once in a
/// header dictionary, the payload stores small integer IDs referencing the dictionary. The
/// source generator routes <see cref="string"/> columns here unconditionally; <see cref="decimal"/>
/// columns default here as well, with <c>[ColumnEncoding(ColumnEncoding.ScaledIntegerBitPacked)]</c>
/// available as the explicit override for high-cardinality decimal workloads.
/// </summary>
/// <remarks>
/// <para><b>Authority:</b> Abadi, Madden &amp; Ferreira 2008 §3 (dictionary encoding for
/// low-cardinality columns) and the Apache Parquet dictionary-page format. DDIA 2e Ch. 4's
/// column-compression discussion covers bitmap encoding; dictionary encoding comes from the
/// column-store literature it cites.</para>
/// <para><b>Layout (common to all value types):</b></para>
/// <list type="number">
/// <item>VLQ uint: dictionary size <c>D</c>.</item>
/// <item><c>D</c> distinct values, encoded type-specifically (see type-specific entry points).</item>
/// <item>Payload: <c>N</c> IDs, encoded as VLQ unsigned integers (most IDs are small).</item>
/// </list>
/// <para><b>Why VLQ for the ID stream rather than fixed-width bit-packing:</b> the current
/// implementation prioritises simplicity. A future optimisation could swap the ID stream for
/// bit-packed fixed-width integers when <c>ceil(log2(D))</c> aligns to a whole byte
/// (e.g. <c>D ≤ 256</c> → 8 bits / 1 byte per ID; <c>D ≤ 65,536</c> → 16 bits / 2 bytes).
/// Left to future integration measurement.</para>
/// </remarks>
internal static class DictionaryCodec
{
    public const ColumnEncodingTag Tag = ColumnEncodingTag.Dictionary;
    public const byte Version = 1;

    /// <summary>
    /// Magic byte that identifies the string-dictionary layout (null-aware). The leading
    /// <c>0xFF</c> locks the payload shape against future additive changes.
    /// </summary>
    internal const byte StringDictMagic = 0xFF;

    // ───── String ─────────────────────────────────────────────────────

    /// <summary>
    /// Encodes a span of strings (possibly containing <see langword="null"/>) in the v1
    /// null-aware layout. Dictionary id <c>0</c> is reserved for <see langword="null"/>;
    /// non-null distinct values are assigned ids <c>1..N</c>. The payload's VLQ id stream
    /// therefore distinguishes <c>null</c> from the empty string (DDIA 2e Ch. 5 —
    /// sentinel reservation guarantees future codec revisions can decode today's output).
    /// </summary>
    public static byte[] EncodeString(ReadOnlySpan<string?> values)
    {
        if (values.Length == 0)
        {
            return [];
        }

        var dict = new Dictionary<string, int>(StringComparer.Ordinal);
        var distinct = new List<string>();
        var ids = new int[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            var v = values[i];
            if (v is null)
            {
                ids[i] = 0; // Reserved null id.
                continue;
            }

            if (!dict.TryGetValue(v, out var id))
            {
                id = distinct.Count + 1; // non-null ids start at 1
                dict[v] = id;
                distinct.Add(v);
            }

            ids[i] = id;
        }

        using var output = new MemoryStream();

        // Magic + version byte. v2.0.0 ships a single string-dictionary format; readers
        // require both bytes at offset 0.
        output.WriteByte(StringDictMagic);
        output.WriteByte(Version);

        Vlq.WriteUInt64(output, (ulong)distinct.Count);
        foreach (var v in distinct)
        {
            WriteString(output, v);
        }

        for (var i = 0; i < ids.Length; i++)
        {
            Vlq.WriteUInt64(output, (ulong)ids[i]);
        }

        return output.ToArray();
    }

    /// <summary>
    /// Decodes <paramref name="count"/> strings starting at row <paramref name="firstRowIndex"/>.
    /// </summary>
    /// <remarks>
    /// Not a seek: the id stream is variable-width VLQ with the dictionary at the front of the
    /// payload, so this decodes the full prefix — O(dictionary + firstRowIndex + count) per
    /// call, not O(count). Callers iterating many row groups over the same column pay the
    /// prefix scan once per group. (Values are dictionary references, so the prefix cost is
    /// VLQ id reads plus one prefix-sized array — no per-row string materialisation.)
    /// </remarks>
    public static string?[] DecodeStringSlice(ReadOnlySpan<byte> input, int firstRowIndex, int count)
    {
        if (count == 0)
        {
            return [];
        }

        // Compute the full-prefix length in 64-bit arithmetic: a forged firstRowIndex near
        // int.MaxValue (from a corrupt slice descriptor) would otherwise overflow `firstRowIndex +
        // count` to a negative int and leak a raw OverflowException out of this public decode API
        // instead of the typed corruption signal the rest of the codec uses.
        var prefix = (long)firstRowIndex + count;
        if (firstRowIndex < 0 || prefix > int.MaxValue)
        {
            throw new InvalidDataException(
                $"DictionaryCodec (string): forged slice descriptor firstRowIndex={firstRowIndex}, "
                + $"count={count} overflows the decodable prefix. Segment is corrupt.");
        }

        var full = DecodeString(input, (int)prefix);
        var result = new string?[count];
        Array.Copy(full, firstRowIndex, result, 0, count);
        return result;
    }

    /// <summary>
    /// Decodes a string column. The input must start with <see cref="StringDictMagic"/>
    /// followed by the <see cref="Version"/> byte — v2.0.0 ships a single string-dictionary
    /// format. The return array holds <see langword="null"/> entries when the source
    /// payload encoded them via the reserved id 0.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why <c>0xFF</c> is unambiguous.</b> <see cref="StringDictMagic"/> = <c>0xFF</c>
    /// is a byte that cannot appear as the first byte of a raw VLQ-encoded dictionary
    /// size (VLQ continuation bit would only reach <c>0xFF</c> as part of a multi-byte
    /// sequence whose high bit is set, but as a first byte it would encode a value with
    /// its continuation bit set, not a terminal byte — the layout locks against future
    /// additive changes).
    /// </para>
    /// </remarks>
    public static string?[] DecodeString(ReadOnlySpan<byte> input, int count)
    {
        if (count == 0)
        {
            return [];
        }

        if (input.Length == 0 || input[0] != StringDictMagic)
        {
            throw new InvalidDataException(
                $"DictionaryCodec (string): expected dictionary magic 0x{StringDictMagic:X2} at offset 0, " +
                $"got 0x{(input.IsEmpty ? 0 : input[0]):X2}. Segment is corrupt.");
        }

        return DecodeStringPayload(input, count);
    }

    private static string?[] DecodeStringPayload(ReadOnlySpan<byte> input, int count)
    {
        // [0]: magic (already checked by caller), [1]: version — a mismatched version
        // byte is a future-forward incompatibility and surfaces as a typed exception.
        if (input.Length < 2)
        {
            throw new InvalidDataException("DictionaryCodec (string): truncated header.");
        }
        var version = input[1];
        if (version != Version)
        {
            throw new UnsupportedFormatVersionException(version, Version);
        }

        var cursor = 2;
        var (dictSize64, r0) = Vlq.ReadUInt64(input[cursor..]);
        cursor += r0;
        var dictSize = checked((int)dictSize64);

        // Bound the declared dictionary size by the bytes actually available BEFORE
        // allocating: every entry occupies at least one byte (its VLQ length prefix), so a
        // well-formed payload can never declare more entries than it has remaining bytes.
        // A corrupt size near int.MaxValue otherwise forces a multi-gigabyte allocation
        // from a few corrupt bytes.
        if (dictSize > input.Length - cursor)
        {
            throw new InvalidDataException(
                $"DictionaryCodec (string): declared dictionary size {dictSize} exceeds the "
                + $"{input.Length - cursor} payload bytes remaining. Segment is corrupt.");
        }

        // Non-null ids range from 1..dictSize; id 0 is reserved for null.
        var dict = new string[dictSize];
        for (var i = 0; i < dictSize; i++)
        {
            var (s, read) = ReadString(input[cursor..]);
            cursor += read;
            dict[i] = s;
        }

        // Bound the declared row count against the id-stream bytes that remain BEFORE allocating
        // the result — a forged footer RowCount must not drive a multi-gigabyte array. Every row id
        // occupies at least one VLQ byte, so the payload can never encode more rows than it has
        // bytes left after the dictionary.
        if (count > input.Length - cursor)
        {
            throw new InvalidDataException(
                $"DictionaryCodec (string): declared row count {count} exceeds the "
                + $"{input.Length - cursor} id-stream bytes remaining. Segment is corrupt.");
        }

        var result = new string?[count];
        for (var i = 0; i < count; i++)
        {
            var (id64, r) = Vlq.ReadUInt64(input[cursor..]);
            cursor += r;
            // Validate the id VALUE against the dictionary BEFORE narrowing to int. Vlq.ReadUInt64
            // accepts the full 64-bit range, so a forged id > int.MaxValue would throw a raw
            // OverflowException at `checked((int)id64)` ahead of the typed range guard. Bound it as
            // ulong first so a forged id surfaces this codec's typed corruption exception.
            if (id64 > (ulong)dictSize)
            {
                throw new InvalidDataException(
                    $"DictionaryCodec (string): ID {id64} at row {i} is out of range [0, {dictSize}].");
            }
            var id = (int)id64;
            if (id == 0)
            {
                result[i] = null;
                continue;
            }

            result[i] = dict[id - 1];
        }

        return result;
    }

    // Strict: the default Encoding.UTF8 silently rewrites an unpaired surrogate to U+FFFD, so the
    // decoded value would differ from the written one and two distinct inputs could share one
    // dictionary entry. Mirrors the tagged- and ordered-wire string writers.
    private static readonly System.Text.UTF8Encoding s_strictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static void WriteString(Stream output, string value)
    {
        byte[] bytes;
        try
        {
            bytes = s_strictUtf8.GetBytes(value);
        }
        catch (System.Text.EncoderFallbackException ex)
        {
            throw new ArgumentException(
                "Columnar string values must be well-formed UTF-16: an unpaired surrogate cannot be "
                + "encoded in UTF-8, and replacement substitution (U+FFFD) would silently change the "
                + "stored value.", nameof(value), ex);
        }

        Vlq.WriteUInt64(output, (ulong)bytes.Length);
        output.Write(bytes, 0, bytes.Length);
    }

    private static (string Value, int BytesRead) ReadString(ReadOnlySpan<byte> input)
    {
        var (len64, r0) = Vlq.ReadUInt64(input);
        var len = checked((int)len64);
        if (r0 + len > input.Length)
        {
            throw new InvalidDataException(
                $"DictionaryCodec (string): UTF-8 payload of {len} bytes overruns buffer (have {input.Length - r0}).");
        }

        var s = System.Text.Encoding.UTF8.GetString(input.Slice(r0, len));
        return (s, r0 + len);
    }

    // ───── Decimal ────────────────────────────────────────────────────

    /// <summary>
    /// Decodes <paramref name="count"/> decimal values starting at row <paramref name="firstRowIndex"/>.
    /// </summary>
    /// <remarks>
    /// Not a seek: the id stream is variable-width VLQ with the dictionary at the front of the
    /// payload, so this decodes the full prefix — O(dictionary + firstRowIndex + count) per
    /// call, not O(count). Callers iterating many row groups over the same column pay the
    /// prefix scan once per group.
    /// </remarks>
    public static decimal[] DecodeDecimalSlice(ReadOnlySpan<byte> input, int firstRowIndex, int count)
    {
        if (count == 0)
        {
            return [];
        }

        // Compute the full-prefix length in 64-bit arithmetic: a forged firstRowIndex near
        // int.MaxValue (from a corrupt slice descriptor) would otherwise overflow `firstRowIndex +
        // count` to a negative int and leak a raw OverflowException out of this public decode API
        // instead of the typed corruption signal the rest of the codec uses. (Mirrors the
        // string-column guard.)
        var prefix = (long)firstRowIndex + count;
        if (firstRowIndex < 0 || prefix > int.MaxValue)
        {
            throw new InvalidDataException(
                $"DictionaryCodec (decimal): forged slice descriptor firstRowIndex={firstRowIndex}, "
                + $"count={count} overflows the decodable prefix. Segment is corrupt.");
        }

        var full = DecodeDecimal(input, (int)prefix);
        var result = new decimal[count];
        Array.Copy(full, firstRowIndex, result, 0, count);
        return result;
    }

    public static byte[] EncodeDecimal(ReadOnlySpan<decimal> values)
    {
        if (values.Length == 0)
        {
            return [];
        }

        var dict = new Dictionary<decimal, int>();
        var distinct = new List<decimal>();
        var ids = new int[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            var v = values[i];
            if (!dict.TryGetValue(v, out var id))
            {
                id = distinct.Count;
                dict[v] = id;
                distinct.Add(v);
            }

            ids[i] = id;
        }

        using var output = new MemoryStream();
        Vlq.WriteUInt64(output, (ulong)distinct.Count);
        Span<byte> decimalBuf = stackalloc byte[16];
        foreach (var v in distinct)
        {
            WriteDecimal(decimalBuf, v);
            output.Write(decimalBuf);
        }

        for (var i = 0; i < ids.Length; i++)
        {
            Vlq.WriteUInt64(output, (ulong)ids[i]);
        }

        return output.ToArray();
    }

    public static decimal[] DecodeDecimal(ReadOnlySpan<byte> input, int count)
    {
        if (count == 0)
        {
            return [];
        }

        var cursor = 0;
        var (dictSize64, r0) = Vlq.ReadUInt64(input);
        cursor += r0;
        var dictSize = checked((int)dictSize64);

        // Bound the declared dictionary size by the bytes actually available BEFORE
        // allocating: every decimal entry is exactly 16 bytes, so a well-formed payload can
        // never declare more entries than (remaining bytes / 16). A corrupt size near
        // int.MaxValue otherwise forces a ~32 GB allocation from a few corrupt bytes.
        if (dictSize > (input.Length - cursor) / 16)
        {
            throw new InvalidDataException(
                $"DictionaryCodec (decimal): declared dictionary size {dictSize} exceeds the "
                + $"{(input.Length - cursor) / 16} entries the remaining payload can hold. Segment is corrupt.");
        }

        var dict = new decimal[dictSize];
        Span<int> bits = stackalloc int[4];
        for (var i = 0; i < dictSize; i++)
        {
            if (cursor + 16 > input.Length)
            {
                throw new InvalidDataException("DictionaryCodec (decimal): dictionary overflows payload.");
            }

            for (var j = 0; j < 4; j++)
            {
                bits[j] = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(input.Slice(cursor + j * 4, 4));
            }

            // Validate the decoded flags word before construction — a forged scale > 28 or reserved
            // bit otherwise leaks a raw ArgumentException out of this public columnar decode API
            // (matches OrderedByteHelpers.ReadDecimal's typed-corruption convention).
            dict[i] = CorruptionGuards.ToDecimal(bits);
            cursor += 16;
        }

        // Bound the declared row count against the id-stream bytes that remain BEFORE allocating
        // the result — a forged footer RowCount must not drive a multi-gigabyte array. Every row id
        // occupies at least one VLQ byte, so the payload can never encode more rows than it has
        // bytes left after the dictionary. (Mirrors the string-column guard.)
        if (count > input.Length - cursor)
        {
            throw new InvalidDataException(
                $"DictionaryCodec (decimal): declared row count {count} exceeds the "
                + $"{input.Length - cursor} id-stream bytes remaining. Segment is corrupt.");
        }

        var result = new decimal[count];
        for (var i = 0; i < count; i++)
        {
            var (id64, r) = Vlq.ReadUInt64(input[cursor..]);
            cursor += r;
            // Validate the id VALUE against the dictionary BEFORE narrowing to int. Vlq.ReadUInt64
            // accepts the full 64-bit range, so a forged id > int.MaxValue would throw a raw
            // OverflowException at `checked((int)id64)` ahead of the typed range guard. Bound it as
            // ulong first so a forged id surfaces this codec's typed corruption exception.
            if (id64 >= (ulong)dictSize)
            {
                throw new InvalidDataException(
                    $"DictionaryCodec (decimal): ID {id64} at row {i} is out of range [0, {dictSize}).");
            }
            var id = (int)id64;

            result[i] = dict[id];
        }

        return result;
    }

    private static void WriteDecimal(Span<byte> buf16, decimal d)
    {
        Span<int> bits = stackalloc int[4];
        _ = decimal.GetBits(d, bits);
        for (var i = 0; i < 4; i++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buf16.Slice(i * 4, 4), bits[i]);
        }
    }
}
