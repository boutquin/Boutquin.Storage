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
/// Delta + zigzag-VLQ codec for <see cref="int"/> and <see cref="long"/> columns, with
/// run-length encoding of equal-delta runs to compress monotone or near-monotone sequences.
/// </summary>
/// <remarks>
/// <para><b>Authority:</b> DDIA 2nd ed Ch. 4 p 141 (RLE the highest-value compression for
/// monotone sorted columns); Abadi et al. 2008 §3 on per-column integer compression.</para>
/// <para><b>Layout:</b> first value written as a zigzag-VLQ int64; subsequent values encoded
/// as deltas from the previous value. Deltas are grouped into runs of (runLength, delta) where
/// runLength is a VLQ unsigned and delta is a zigzag-VLQ int64. An equal-delta run of 4 or
/// more elements thus collapses to 1 VLQ + 1 VLQ rather than N VLQs — the "RLE fallback" the
/// type-default encoding table prescribes.</para>
/// <para><b>Why int64 internally:</b> both the int32 and int64 entry points promote to int64
/// for delta arithmetic to avoid signed-overflow corner cases (e.g. delta of
/// <c>int.MaxValue - int.MinValue</c>). Int32 decode down-casts with a bounds check to catch
/// corruption that would otherwise silently round.</para>
/// </remarks>
internal static class DeltaVlqCodec
{
    public const ColumnEncodingTag Tag = ColumnEncodingTag.DeltaVlq;
    public const byte Version = 0;

    // ───── Int64 (canonical) ─────────────────────────────────────────

    public static byte[] EncodeInt64(ReadOnlySpan<long> values)
    {
        if (values.Length == 0)
        {
            return [];
        }

        using var output = new MemoryStream();
        Vlq.WriteInt64(output, values[0]);

        if (values.Length == 1)
        {
            return output.ToArray();
        }

        var previous = values[0];
        var currentDelta = values[1] - previous;
        var runLength = 1;
        previous = values[1];

        for (var i = 2; i <= values.Length; i++)
        {
            long nextDelta = 0;
            var atEnd = i == values.Length;
            if (!atEnd)
            {
                nextDelta = values[i] - previous;
            }

            if (atEnd || nextDelta != currentDelta)
            {
                // Flush the current run.
                Vlq.WriteUInt64(output, (ulong)runLength);
                Vlq.WriteInt64(output, currentDelta);
                if (atEnd)
                {
                    break;
                }

                currentDelta = nextDelta;
                runLength = 1;
            }
            else
            {
                runLength++;
            }

            previous = values[i];
        }

        return output.ToArray();
    }

    public static long[] DecodeInt64(ReadOnlySpan<byte> input, int count, long maxDecodableRows = long.MaxValue)
    {
        if (count == 0)
        {
            return [];
        }

        if (input.Length == 0)
        {
            throw new InvalidDataException("DeltaVlq payload is empty but decode count > 0.");
        }

        BoundCountAgainstCeiling(count, maxDecodableRows);
        BoundCountAgainstPayload(count, input.Length);

        var result = new long[count];
        var cursor = 0;
        var (first, read) = Vlq.ReadInt64(input);
        cursor += read;
        result[0] = first;
        var previous = first;
        var written = 1;

        while (written < count)
        {
            if (cursor >= input.Length)
            {
                throw new InvalidDataException("DeltaVlq payload exhausted before row count was reached.");
            }

            var (runLen, r1) = Vlq.ReadUInt64(input[cursor..]);
            cursor += r1;
            if (cursor >= input.Length)
            {
                // Truncation INSIDE a run frame (between length and delta) is the same
                // corruption class as truncation between frames — typed, not a leaked
                // EndOfStreamException from the VLQ reader.
                throw new InvalidDataException("DeltaVlq payload truncated between a run length and its delta.");
            }

            var (delta, r2) = Vlq.ReadInt64(input[cursor..]);
            cursor += r2;

            // Bound the run BEFORE narrowing: a forged run length past Int32.MaxValue must not
            // leak a raw OverflowException from checked((int)…), and any run larger than the
            // rows remaining is corrupt regardless of magnitude.
            if (runLen == 0)
            {
                throw new InvalidDataException("DeltaVlq run length must be >= 1 (saw 0).");
            }

            if (runLen > (ulong)(count - written))
            {
                throw new InvalidDataException(
                    $"DeltaVlq run overflows declared row count (run {runLen}, remaining {count - written}).");
            }

            var run = (int)runLen;

            for (var i = 0; i < run; i++)
            {
                previous += delta;
                result[written + i] = previous;
            }

            written += run;
        }

        return result;
    }

    /// <summary>
    /// Decodes <paramref name="count"/> values starting at row <paramref name="firstRowIndex"/>
    /// from the whole-column payload. Scans through RLE runs, skipping until
    /// <paramref name="firstRowIndex"/> is reached, then stores exactly <paramref name="count"/>
    /// values. Handles runs that straddle the start boundary correctly.
    /// </summary>
    public static long[] DecodeSliceInt64(ReadOnlySpan<byte> input, int firstRowIndex, int count, long maxDecodableRows = long.MaxValue)
    {
        if (count == 0)
        {
            return [];
        }

        if (input.Length == 0)
        {
            throw new InvalidDataException("DeltaVlq payload is empty but decode count > 0.");
        }

        // Same allocation-before-bound guard as the full decoder: a forged footer RowCount feeds
        // this slice's `count`, so reject an impossibly-large slice before the new long[count].
        // The bound caps the ALLOCATION SIZE (`count`, the number of rows actually materialised),
        // NOT the absolute extent `firstRowIndex + count` — a slice only ever allocates new
        // long[count], and a run-length codec legitimately produces a large `firstRowIndex` (the
        // last row group of a many-row column) whose extent far exceeds any single row group's
        // sane maximum. Cap `count` against the caller's per-row-group ceiling.
        BoundCountAgainstCeiling(count, maxDecodableRows);
        BoundCountAgainstPayload(count, input.Length);

        var result = new long[count];
        var cursor = 0;
        var (first, r0) = Vlq.ReadInt64(input);
        cursor += r0;

        var previous = first;
        var encountered = 0;
        var resultWritten = 0;

        // Handle the absolute first element (index 0).
        if (encountered >= firstRowIndex && resultWritten < count)
        {
            result[resultWritten++] = first;
        }

        encountered = 1;

        while (resultWritten < count && cursor < input.Length)
        {
            var (runLen, r1) = Vlq.ReadUInt64(input[cursor..]);
            cursor += r1;
            if (cursor >= input.Length)
            {
                throw new InvalidDataException("DeltaVlq payload truncated between a run length and its delta.");
            }

            var (delta, r2) = Vlq.ReadInt64(input[cursor..]);
            cursor += r2;
            if (runLen == 0)
            {
                throw new InvalidDataException("DeltaVlq run length must be >= 1 (saw 0).");
            }

            // Clamp instead of checked((int)…): the slice loop self-limits on resultWritten and
            // encountered, so a huge (possibly forged) run length only needs to be walkable —
            // int.MaxValue steps is the ceiling the loop can consume before its own exits fire.
            var run = runLen > int.MaxValue ? int.MaxValue : (int)runLen;

            for (var j = 0; j < run; j++)
            {
                previous += delta;
                if (encountered >= firstRowIndex && resultWritten < count)
                {
                    result[resultWritten++] = previous;
                }

                encountered++;
                if (resultWritten == count)
                {
                    break;
                }
            }
        }

        // Fail closed like the full-column decoder: a payload exhausted before `count`
        // values were produced previously returned a silently zero-filled tail — and the
        // production read path has no per-row-group CRC backstop to catch it.
        if (resultWritten < count)
        {
            throw new InvalidDataException("DeltaVlq payload exhausted before row count was reached.");
        }

        return result;
    }

    /// <summary>
    /// Rejects a forged row count against the caller's per-row-group ceiling BEFORE the
    /// <c>new long[count]</c> allocation — the allocation-before-bound DoS guard that actually
    /// holds in the saturated regime. A run-length codec can legitimately encode a huge count from
    /// a tiny payload (one VLQ run length expresses up to <c>2^(7 × bytes) − 1</c> rows), so the
    /// per-payload ceiling <see cref="Vlq.MaxCountForRunLengthBytes"/> saturates to
    /// <see cref="long.MaxValue"/> for payloads of >= 9 run-length bytes and stops discriminating.
    /// The non-saturating bound the caller threads in is a sane PER-ROW-GROUP maximum on the
    /// allocation size: the writer caps each row group at <c>rowGroupMaxRows</c>, so a legitimate
    /// decode count never exceeds that ceiling, while a forged count near <see cref="int.MaxValue"/>
    /// does and is rejected. This is deliberately NOT the segment byte length — a run-length codec
    /// decodes many rows from few bytes, so "N bytes ⇒ ≤ N rows" is false and would false-reject a
    /// valid compressible column. <paramref name="requestedRows"/> is the allocation size
    /// (<c>count</c>), never the absolute extent <c>firstRowIndex + count</c>.
    /// </summary>
    private static void BoundCountAgainstCeiling(long requestedRows, long maxDecodableRows)
    {
        if (requestedRows > maxDecodableRows)
        {
            throw new CorruptSegmentException(
                $"DeltaVlq decode declares {requestedRows} rows, but the per-row-group ceiling permits "
                + $"at most {maxDecodableRows}; segment is corrupt.",
                offset: 0);
        }
    }

    /// <summary>
    /// Rejects a forged footer row count that the payload could not possibly encode in its
    /// <i>non-saturated</i> regime, BEFORE the <c>new long[count]</c> allocation. The run-length
    /// VLQ stream occupies at most <paramref name="payloadLength"/> bytes, so no more than
    /// <see cref="Vlq.MaxCountForRunLengthBytes"/> rows are decodable. This ceiling saturates to
    /// <see cref="long.MaxValue"/> for payloads of >= 9 bytes and is then vacuous against an
    /// <see cref="int"/> count; the segment-level <see cref="BoundCountAgainstCeiling"/> guard is
    /// the one that closes the saturated regime. This guard remains as the tighter bound for short
    /// payloads.
    /// </summary>
    private static void BoundCountAgainstPayload(int count, int payloadLength)
    {
        var maxDecodable = Vlq.MaxCountForRunLengthBytes(payloadLength);
        if (count > maxDecodable)
        {
            throw new InvalidDataException(
                $"DeltaVlq declared row count {count} exceeds the {maxDecodable} rows the "
                + $"{payloadLength}-byte payload could encode. Segment is corrupt.");
        }
    }

    // ───── Int32 (delegates to int64 with overflow-safe casts) ───────

    public static byte[] EncodeInt32(ReadOnlySpan<int> values)
    {
        if (values.Length == 0)
        {
            return [];
        }

        var promoted = new long[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            promoted[i] = values[i];
        }

        return EncodeInt64(promoted);
    }

    /// <summary>
    /// Decodes <paramref name="count"/> int32 values starting at row <paramref name="firstRowIndex"/>.
    /// </summary>
    public static int[] DecodeSliceInt32(ReadOnlySpan<byte> input, int firstRowIndex, int count, long maxDecodableRows = long.MaxValue)
    {
        if (count == 0)
        {
            return [];
        }

        var int64 = DecodeSliceInt64(input, firstRowIndex, count, maxDecodableRows);
        var result = new int[count];
        for (var i = 0; i < count; i++)
        {
            if (int64[i] < int.MinValue || int64[i] > int.MaxValue)
            {
                throw new InvalidDataException(
                    $"DeltaVlq decoded value {int64[i]} at index {firstRowIndex + i} overflows Int32 bounds.");
            }

            result[i] = (int)int64[i];
        }

        return result;
    }

    public static int[] DecodeInt32(ReadOnlySpan<byte> input, int count, long maxDecodableRows = long.MaxValue)
    {
        var int64 = DecodeInt64(input, count, maxDecodableRows);
        var result = new int[count];
        for (var i = 0; i < count; i++)
        {
            if (int64[i] < int.MinValue || int64[i] > int.MaxValue)
            {
                throw new InvalidDataException(
                    $"DeltaVlq decoded value {int64[i]} at index {i} overflows Int32 bounds.");
            }

            result[i] = (int)int64[i];
        }

        return result;
    }
}
