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
/// Delta-of-delta + zigzag-VLQ codec for <see cref="DateOnly"/> columns in monotone date
/// order. Exploits the regularity of time-series sampling (daily bars, hourly observations,
/// fiscal-quarter reports): consecutive dates share near-constant deltas, so second-order
/// differences (delta-of-delta) cluster tightly around zero and compress to 1 VLQ byte each.
/// </summary>
/// <remarks>
/// <para><b>Authority:</b> Pelkonen et al. 2015 (Gorilla, VLDB) §4.1 — delta-of-delta
/// timestamp encoding. For evenly-spaced time-series data the delta-of-delta is 0 almost
/// always, collapsing to minimum-size VLQ output.</para>
/// <para><b>Layout:</b></para>
/// <list type="number">
/// <item>First value: day number written as a zigzag-VLQ int64.</item>
/// <item>Second value (if present): first delta, zigzag-VLQ int64.</item>
/// <item>Third value onwards: delta-of-delta (i.e. <c>delta_i − delta_{i-1}</c>), zigzag-VLQ
///   int64 each.</item>
/// </list>
/// <para>For N evenly-spaced dates, output is ~N+1 bytes (first date: a few VLQ bytes; first
/// delta: 1 byte; subsequent delta-of-deltas: 1 byte each — almost all zero).</para>
/// <para><b>Monotonicity:</b> this codec does not require the input to be strictly monotone —
/// any DateOnly sequence round-trips correctly — but compression is best on monotone input.
/// Callers should sort before encoding; the plain-format row-group contract guarantees date
/// order, so this is free at the integration boundary.</para>
/// </remarks>
internal static class DeltaOfDeltaVlqCodec
{
    public const ColumnEncodingTag Tag = ColumnEncodingTag.DeltaOfDeltaVlq;
    public const byte Version = 0;

    public static byte[] Encode(ReadOnlySpan<DateOnly> values)
    {
        if (values.Length == 0)
        {
            return [];
        }

        using var output = new MemoryStream();
        long previous = values[0].DayNumber;
        Vlq.WriteInt64(output, previous);

        if (values.Length == 1)
        {
            return output.ToArray();
        }

        var previousDelta = values[1].DayNumber - previous;
        Vlq.WriteInt64(output, previousDelta);
        previous = values[1].DayNumber;

        for (var i = 2; i < values.Length; i++)
        {
            var currentDelta = values[i].DayNumber - previous;
            var deltaOfDelta = currentDelta - previousDelta;
            Vlq.WriteInt64(output, deltaOfDelta);
            previous = values[i].DayNumber;
            previousDelta = currentDelta;
        }

        return output.ToArray();
    }

    public static DateOnly[] Decode(ReadOnlySpan<byte> input, int count, long maxDecodableRows = long.MaxValue)
    {
        if (count == 0)
        {
            return [];
        }

        if (input.Length == 0)
        {
            throw new InvalidDataException("DeltaOfDeltaVlq payload is empty but decode count > 0.");
        }

        // Reject a forged footer RowCount against the caller's per-row-group ceiling BEFORE the
        // `new DateOnly[count]` allocation — the allocation-before-bound DoS guard that actually
        // holds in the saturated regime. DeltaOfDeltaVlq is a run-of-deltas codec: each VLQ value is
        // one row's delta-of-delta, so a tiny payload of >= 9 bytes drives the per-payload ceiling
        // below to saturate (long.MaxValue) and stop discriminating against an `int` count. The
        // non-saturating bound the caller threads in is a sane PER-ROW-GROUP maximum on the
        // allocation size `count`: this codec is decoded one row-group slice at a time, so `count`
        // is a single row group's row count, capped by the writer's rowGroupMaxRows. A `count`
        // exceeding that ceiling cannot have come from a real row group and is a forged-footer DoS
        // attempt. The per-row-group cap is the correct, codec-agnostic ceiling on the allocation.
        if (count > maxDecodableRows)
        {
            throw new CorruptSegmentException(
                $"DeltaOfDeltaVlq decode declares {count} rows, but the per-row-group ceiling permits "
                + $"at most {maxDecodableRows}; segment is corrupt.",
                offset: 0);
        }

        // Tighter per-payload ceiling for the non-saturated regime (short payloads). Each VLQ byte
        // carries 7 magnitude bits; the first value consumes at least one of the input bytes, so the
        // whole payload is a loose-but-sound ceiling on the decodable run total. This saturates to
        // long.MaxValue for payloads of >= 9 bytes and is then vacuous against an `int` count; the
        // segment-level guard above is the one that closes the saturated regime.
        var maxDecodable = Vlq.MaxCountForRunLengthBytes(input.Length);
        if (count > maxDecodable)
        {
            throw new InvalidDataException(
                $"DeltaOfDeltaVlq declared row count {count} exceeds the {maxDecodable} rows the "
                + $"{input.Length}-byte payload could encode. Segment is corrupt.");
        }

        var result = new DateOnly[count];
        var cursor = 0;
        var (firstDayNum, r0) = Vlq.ReadInt64(input);
        cursor += r0;
        if (firstDayNum < DateOnly.MinValue.DayNumber || firstDayNum > DateOnly.MaxValue.DayNumber)
        {
            throw new InvalidDataException(
                $"DeltaOfDeltaVlq first day-number {firstDayNum} is outside DateOnly range.");
        }

        var previous = firstDayNum;
        result[0] = DateOnly.FromDayNumber((int)firstDayNum);

        if (count == 1)
        {
            return result;
        }

        // Guard before reading the second value: a payload truncated after the first value
        // would cause Vlq.ReadInt64 to throw EndOfStreamException (raw, untyped). The codec
        // contract specifies InvalidDataException for malformed input — convert the truncation
        // sentinel here before the read so callers see the right exception type.
        if (cursor >= input.Length)
        {
            throw new InvalidDataException(
                $"DeltaOfDelta payload truncated: expected second value at offset {cursor}, payload length {input.Length}.");
        }

        var (firstDelta, r1) = Vlq.ReadInt64(input[cursor..]);
        cursor += r1;
        var previousDelta = firstDelta;
        previous += firstDelta;
        ValidateDayNumber(previous, 1);
        result[1] = DateOnly.FromDayNumber((int)previous);

        for (var i = 2; i < count; i++)
        {
            if (cursor >= input.Length)
            {
                throw new InvalidDataException("DeltaOfDeltaVlq payload exhausted before row count was reached.");
            }

            var (dod, r2) = Vlq.ReadInt64(input[cursor..]);
            cursor += r2;
            var currentDelta = previousDelta + dod;
            previous += currentDelta;
            ValidateDayNumber(previous, i);
            result[i] = DateOnly.FromDayNumber((int)previous);
            previousDelta = currentDelta;
        }

        return result;
    }

    private static void ValidateDayNumber(long dayNumber, int index)
    {
        if (dayNumber < DateOnly.MinValue.DayNumber || dayNumber > DateOnly.MaxValue.DayNumber)
        {
            throw new InvalidDataException(
                $"DeltaOfDeltaVlq decoded day-number {dayNumber} at index {index} is outside DateOnly range "
                + $"[{DateOnly.MinValue.DayNumber}, {DateOnly.MaxValue.DayNumber}].");
        }
    }
}
