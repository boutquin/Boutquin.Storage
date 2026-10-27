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
namespace Boutquin.Storage.Infrastructure.TimeSeries.Columnar.Codecs;

/// <summary>
/// Shared variable-length quantity (VLQ) + zigzag-encoding primitives used by the
/// delta-based scalar codecs (<c>DeltaVlqCodec</c>, <c>DeltaOfDeltaVlqCodec</c>).
/// </summary>
/// <remarks>
/// <para><b>VLQ (7-bit, little-endian continuation):</b> each byte carries 7 payload bits;
/// bit 7 is set on every non-final byte. A <see cref="ulong"/> therefore occupies 1–10 bytes
/// (10 bytes is the worst case for 64 bits of payload). Values near zero encode in 1 byte —
/// the whole point of delta coding is to keep values near zero.</para>
/// <para><b>Zigzag:</b> maps signed integers to unsigned such that small magnitudes — positive
/// or negative — land in small unsigned numbers (<c>0,-1,1,-2,2,… → 0,1,2,3,4,…</c>). Required
/// because VLQ is most compact for small unsigned inputs; a signed <c>-1</c> naively serialised
/// as <c>0xFFFFFFFFFFFFFFFF</c> would take 10 VLQ bytes.</para>
/// </remarks>
internal static class Vlq
{
    // ───── Zigzag mapping ─────────────────────────────────────────────

    public static ulong ZigzagEncode(long value)
        => (ulong)((value << 1) ^ (value >> 63));

    public static long ZigzagDecode(ulong value)
        => (long)(value >> 1) ^ -(long)(value & 1);

    // ───── VLQ over Stream ────────────────────────────────────────────

    public static void WriteUInt64(Stream output, ulong value)
    {
        while (value >= 0x80)
        {
            output.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }

        output.WriteByte((byte)value);
    }

    public static ulong ReadUInt64(Stream input)
    {
        ulong result = 0;
        var shift = 0;
        while (true)
        {
            var b = input.ReadByte();
            if (b < 0)
            {
                throw new EndOfStreamException("Unexpected end of stream in VLQ read.");
            }

            result |= ((ulong)b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return result;
            }

            shift += 7;
            if (shift >= 70)
            {
                throw new InvalidDataException("VLQ value exceeds 10 bytes (corrupt input).");
            }
        }
    }

    public static void WriteInt64(Stream output, long value) => WriteUInt64(output, ZigzagEncode(value));

    public static long ReadInt64(Stream input) => ZigzagDecode(ReadUInt64(input));

    /// <summary>
    /// The largest run-total a run-length codec could legitimately decode from
    /// <paramref name="runByteCount"/> bytes of run-length VLQ stream. Each VLQ byte carries 7
    /// magnitude bits, so a single run length spanning all the bytes encodes up to
    /// <c>2^(7 × runByteCount) − 1</c>; that bounds the sum of all run lengths (= the row count)
    /// because additional runs only add framing overhead. Saturates at <see cref="long.MaxValue"/>
    /// once 7 × <paramref name="runByteCount"/> reaches 63 bits (i.e. <paramref name="runByteCount"/>
    /// >= 9).
    /// </summary>
    /// <remarks>
    /// <para><b>This bound is a useful filter only in its non-saturated regime
    /// (<paramref name="runByteCount"/> &lt;= 8).</b> Once it saturates to <see cref="long.MaxValue"/>
    /// it can no longer discriminate against an <see cref="int"/> declared count, so it is NOT a
    /// sufficient allocation-before-bound DoS guard on its own — a forged footer RowCount up to
    /// <see cref="int.MaxValue"/> would pass it for any payload of >= 9 run-length bytes. The
    /// non-saturating bound that closes the saturated regime is the caller-threaded segment byte
    /// length (no segment of N bytes can hold more than N rows); the restartable codecs apply BOTH
    /// (segment ceiling first, then this tighter per-payload ceiling for short payloads).</para>
    /// </remarks>
    public static long MaxCountForRunLengthBytes(int runByteCount)
    {
        if (runByteCount <= 0)
        {
            return 0;
        }

        var bits = 7L * runByteCount;
        return bits >= 63 ? long.MaxValue : (1L << (int)bits) - 1L;
    }

    // ───── VLQ over ReadOnlySpan<byte> (cursor-based reads) ───────────

    /// <summary>Reads one zigzag-VLQ int64 and returns both the decoded value and the number of bytes consumed.</summary>
    public static (long Value, int BytesRead) ReadInt64(ReadOnlySpan<byte> input)
    {
        ulong result = 0;
        var shift = 0;
        for (var i = 0; i < input.Length; i++)
        {
            var b = input[i];
            result |= ((ulong)b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return (ZigzagDecode(result), i + 1);
            }

            shift += 7;
            if (shift >= 70)
            {
                throw new InvalidDataException("VLQ value exceeds 10 bytes (corrupt input).");
            }
        }

        throw new EndOfStreamException("VLQ read ran off the end of the buffer without a terminating byte.");
    }

    /// <summary>Reads one VLQ uint64 and returns both the decoded value and the number of bytes consumed.</summary>
    public static (ulong Value, int BytesRead) ReadUInt64(ReadOnlySpan<byte> input)
    {
        ulong result = 0;
        var shift = 0;
        for (var i = 0; i < input.Length; i++)
        {
            var b = input[i];
            result |= ((ulong)b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return (result, i + 1);
            }

            shift += 7;
            if (shift >= 70)
            {
                throw new InvalidDataException("VLQ value exceeds 10 bytes (corrupt input).");
            }
        }

        throw new EndOfStreamException("VLQ read ran off the end of the buffer without a terminating byte.");
    }
}
