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
/// Run-length-encoded bitmap codec for <see cref="bool"/> columns. Encodes the column as a
/// starting value plus a sequence of VLQ-encoded run lengths; alternating boolean runs are
/// implicit (consecutive runs always toggle).
/// </summary>
/// <remarks>
/// <para><b>Authority:</b> DDIA 2nd ed Ch. 4 pp 140–141 — bitmap encoding with run-length
/// compression is the primary recommended compression for low-cardinality columns. For a
/// two-valued column (a <see cref="bool"/>), the bitmap + RLE composition degenerates to this
/// simple scheme.</para>
/// <para><b>Layout:</b></para>
/// <list type="number">
/// <item>1 byte: starting value (<c>0</c> = <see langword="false"/>, <c>1</c> = <see langword="true"/>).
///   Omitted when the column is empty (see below).</item>
/// <item>Repeated, one per run: VLQ-encoded unsigned run length.</item>
/// </list>
/// <para>Empty input encodes to zero bytes. Single-run inputs encode to 1 + 1 (or more) bytes.</para>
/// <para><b>Worst case:</b> every row toggles value (e.g. <c>[T, F, T, F, …]</c>) — each run
/// length is 1, encoded as 1 VLQ byte. Output size = N+1 bytes for N rows, compared with N
/// bytes plain. Marginally worse than plain in the pathological case; we accept that for the
/// much more common case of long same-value runs (isAdjusted flags, regime indicators).</para>
/// </remarks>
internal static class BitmapRleCodec
{
    public const ColumnEncodingTag Tag = ColumnEncodingTag.BitmapRle;
    public const byte Version = 0;

    /// <summary>Encodes the bool column. Caller supplies the full row-group span.</summary>
    public static byte[] Encode(ReadOnlySpan<bool> values)
    {
        if (values.Length == 0)
        {
            return [];
        }

        using var output = new MemoryStream();
        output.WriteByte(values[0] ? (byte)1 : (byte)0);

        var runStart = 0;
        for (var i = 1; i <= values.Length; i++)
        {
            if (i == values.Length || values[i] != values[i - 1])
            {
                var runLength = (ulong)(i - runStart);
                Vlq.WriteUInt64(output, runLength);
                runStart = i;
            }
        }

        return output.ToArray();
    }

    /// <summary>
    /// Decodes <paramref name="count"/> bool values starting at row <paramref name="firstRowIndex"/>.
    /// Scans through RLE runs, skipping rows before the start offset.
    /// </summary>
    public static bool[] DecodeSlice(ReadOnlySpan<byte> input, int firstRowIndex, int count, long maxDecodableRows = long.MaxValue)
    {
        if (count == 0)
        {
            return [];
        }

        if (input.Length == 0)
        {
            throw new InvalidDataException("BitmapRle payload is empty but decode count > 0.");
        }

        // Same allocation-before-bound guard as the full decoder: a forged footer RowCount feeds
        // this slice's `count`. The bound caps the ALLOCATION SIZE (`count`, the number of rows
        // materialised into new bool[count]) against the caller's per-row-group ceiling — NOT the
        // absolute extent firstRowIndex + count (a later row group legitimately has a large
        // firstRowIndex), and NOT the segment byte length (a run-length codec decodes many rows
        // from few bytes). Then the per-payload ceiling for short payloads.
        BoundCountAgainstCeiling(count, maxDecodableRows);
        BoundCountAgainstPayload(count, input.Length);

        var result = new bool[count];
        var current = input[0] != 0;
        var cursor = 1;
        var encountered = 0;
        var resultWritten = 0;

        while (resultWritten < count && cursor < input.Length)
        {
            var (runLength, read) = Vlq.ReadUInt64(input[cursor..]);
            cursor += read;
            if (runLength == 0)
            {
                throw new InvalidDataException("BitmapRle run length must be >= 1 (saw 0).");
            }

            // Clamp instead of checked((int)…): a forged run length past Int32.MaxValue must not
            // leak a raw OverflowException, and the loop below self-limits on resultWritten (it
            // breaks the moment the slice is full), so a clamped huge run is walked only as far
            // as the slice actually needs.
            var runLen = runLength > int.MaxValue ? int.MaxValue : (int)runLength;

            for (var j = 0; j < runLen; j++)
            {
                if (encountered >= firstRowIndex && resultWritten < count)
                {
                    result[resultWritten++] = current;
                }

                encountered++;
                if (resultWritten == count)
                {
                    break;
                }
            }

            current = !current;
        }

        // Fail closed like the full decoder: a payload exhausted before `count` values were
        // produced previously returned a silently zero-filled (false) tail — and the
        // production read path has no per-row-group CRC backstop to catch it.
        if (resultWritten < count)
        {
            throw new InvalidDataException("BitmapRle payload exhausted before row count was reached.");
        }

        return result;
    }

    /// <summary>Decodes <paramref name="count"/> bool values from <paramref name="input"/>.</summary>
    public static bool[] Decode(ReadOnlySpan<byte> input, int count, long maxDecodableRows = long.MaxValue)
    {
        if (count == 0)
        {
            return [];
        }

        if (input.Length == 0)
        {
            throw new InvalidDataException("BitmapRle payload is empty but decode count > 0.");
        }

        // Bound the declared count against what the payload could possibly encode BEFORE allocating
        // — a forged footer RowCount must not drive a multi-gigabyte array. The per-row-group
        // ceiling is the non-saturating bound (a single RLE run can express an arbitrary count from
        // ~1 payload byte, so the per-payload ceiling saturates); apply it first, then the tighter
        // per-payload ceiling for short payloads.
        BoundCountAgainstCeiling(count, maxDecodableRows);
        BoundCountAgainstPayload(count, input.Length);

        var result = new bool[count];
        var current = input[0] != 0;
        var cursor = 1;
        var written = 0;
        while (written < count)
        {
            if (cursor >= input.Length)
            {
                throw new InvalidDataException("BitmapRle payload exhausted before row count was reached.");
            }

            var (runLength, read) = Vlq.ReadUInt64(input[cursor..]);
            cursor += read;
            if (runLength == 0)
            {
                throw new InvalidDataException("BitmapRle run length must be >= 1 (saw 0).");
            }

            // Bound BEFORE narrowing: a forged run length past Int32.MaxValue must surface the
            // typed corruption exception, not a raw OverflowException from checked((int)…).
            if (runLength > (ulong)(count - written))
            {
                throw new InvalidDataException(
                    $"BitmapRle run overflows declared row count (run {runLength}, remaining {count - written}).");
            }

            var runLen = (int)runLength;

            for (var i = 0; i < runLen; i++)
            {
                result[written + i] = current;
            }

            written += runLen;
            current = !current;
        }

        return result;
    }

    /// <summary>
    /// Rejects a forged row count against the caller's per-row-group ceiling BEFORE the
    /// <c>new bool[count]</c> allocation — the allocation-before-bound DoS guard that holds in the
    /// saturated regime. A single RLE run length expresses up to <c>2^(7 × bytes) − 1</c> rows, so
    /// the per-payload ceiling <see cref="Vlq.MaxCountForRunLengthBytes"/> saturates to
    /// <see cref="long.MaxValue"/> for run-length streams of >= 9 bytes and stops discriminating
    /// against an <see cref="int"/> count. The non-saturating bound the caller threads in is a sane
    /// PER-ROW-GROUP maximum on the allocation size <c>count</c>: the writer caps each row group at
    /// <c>rowGroupMaxRows</c>, so a legitimate decode count stays below the ceiling while a forged
    /// count near <see cref="int.MaxValue"/> is rejected. Deliberately NOT the segment byte length
    /// — a run-length codec decodes many rows from few bytes, so "N bytes ⇒ ≤ N rows" is false and
    /// would false-reject a valid compressible column.
    /// </summary>
    private static void BoundCountAgainstCeiling(long requestedRows, long maxDecodableRows)
    {
        if (requestedRows > maxDecodableRows)
        {
            throw new CorruptSegmentException(
                $"BitmapRle decode declares {requestedRows} rows, but the per-row-group ceiling permits "
                + $"at most {maxDecodableRows}; segment is corrupt.",
                offset: 0);
        }
    }

    /// <summary>
    /// Rejects a forged footer row count the payload could not possibly encode in its
    /// <i>non-saturated</i> regime, BEFORE the <c>new bool[count]</c> allocation. Byte 0 is the
    /// start value; the remaining bytes are run-length VLQs. This ceiling saturates to
    /// <see cref="long.MaxValue"/> for >= 9 run-length bytes and is then vacuous against an
    /// <see cref="int"/> count; <see cref="BoundCountAgainstCeiling"/> closes the saturated regime.
    /// </summary>
    private static void BoundCountAgainstPayload(int count, int payloadLength)
    {
        var maxDecodable = Vlq.MaxCountForRunLengthBytes(payloadLength - 1);
        if (count > maxDecodable)
        {
            throw new InvalidDataException(
                $"BitmapRle declared row count {count} exceeds the {maxDecodable} rows the "
                + $"{payloadLength}-byte payload could encode. Segment is corrupt.");
        }
    }
}
