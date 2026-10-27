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
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar.Codecs;

namespace Boutquin.Storage.Infrastructure.Tests.TimeSeries.Columnar.Codecs;

/// <summary>
/// F-W3-regress-01 — the round-5 allocation guard bounded a run-length codec's declared row count
/// against the SEGMENT BYTE LENGTH, and the slice decoders bounded the ABSOLUTE EXTENT
/// <c>firstRowIndex + count</c> rather than the allocation size <c>count</c>. Both dimensions are
/// wrong: a run-length codec legitimately decodes many rows from few bytes, and a slice only ever
/// allocates <c>new T[count]</c> — so the ceiling that prevents a forged-count DoS is a sane
/// PER-ROW-GROUP maximum on <c>count</c>, not the segment byte length and not the absolute extent.
///
/// These tests pin the corrected dimension directly at the codec boundary: a valid slice whose
/// <c>count</c> is within the per-row-group ceiling but whose <c>firstRowIndex + count</c> exceeds
/// it must DECODE (pre-fix: false-rejected with CorruptSegmentException), while a forged
/// <c>count</c> above the per-row-group ceiling must still throw.
/// </summary>
public sealed class CodecPerRowGroupCeilingTests
{
    // A per-row-group ceiling a reader would thread: the default writer cap is 131_072.
    private const long PerRowGroupCeiling = 131_072;

    [Fact]
    public void DeltaVlqInt64Slice_ValidSlice_CountWithinCeiling_ExtentBeyondCeiling_Decodes()
    {
        // A constant-delta run encoding 131_172 rows in ~6 bytes: first value + (runLength, delta).
        // The slice [131_072, 131_172) decodes 100 rows. count (100) is well within the per-row-
        // group ceiling (131_072); only the ABSOLUTE EXTENT firstRowIndex + count (131_172) exceeds
        // it. The corrected bound caps `count`, so this is valid data that must decode — pre-fix the
        // `firstRowIndex + count` bound throws CorruptSegmentException ("...ceiling...").
        var payload = BuildConstantDeltaInt64Payload(firstValue: 1_000, delta: 1, totalRows: 131_172);

        var decoded = DeltaVlqCodec.DecodeSliceInt64(
            payload, firstRowIndex: 131_072, count: 100, maxDecodableRows: PerRowGroupCeiling);

        decoded.Should().HaveCount(100);
        // values[i] = firstValue + i (delta = 1), so the slice [131_072, 131_172) holds
        // values[131_072 .. 131_171] = 1000 + 131_072 .. 1000 + 131_171.
        decoded[0].Should().Be(1_000 + 131_072);
        decoded[99].Should().Be(1_000 + 131_171);
    }

    [Fact]
    public void DeltaVlqInt64Slice_ForgedCount_AboveCeiling_StillThrows()
    {
        // The same saturating regime, but now `count` itself exceeds the per-row-group ceiling.
        // The corrected bound (on `count`) must still reject it before the new long[count].
        var payload = BuildConstantDeltaInt64Payload(firstValue: 0, delta: 1, totalRows: 4);

        var act = () => DeltaVlqCodec.DecodeSliceInt64(
            payload, firstRowIndex: 0, count: (int)PerRowGroupCeiling + 1, maxDecodableRows: PerRowGroupCeiling);

        act.Should().Throw<CorruptSegmentException>().WithMessage("*ceiling*");
    }

    [Fact]
    public void BitmapRleSlice_ValidSlice_CountWithinCeiling_ExtentBeyondCeiling_Decodes()
    {
        // A single false-run of 131_172 rows encoded in ~4 bytes (start byte + run-length VLQ).
        // Slice [131_072, 131_172) decodes 100 rows, all false. count (100) within ceiling; only
        // the extent (131_172) exceeds it. Must decode — pre-fix the extent bound false-rejects.
        var payload = BuildConstantFalseBitmapPayload(totalRows: 131_172);

        var decoded = BitmapRleCodec.DecodeSlice(
            payload, firstRowIndex: 131_072, count: 100, maxDecodableRows: PerRowGroupCeiling);

        decoded.Should().HaveCount(100);
        decoded.Should().OnlyContain(b => b == false);
    }

    [Fact]
    public void BitmapRleSlice_ForgedCount_AboveCeiling_StillThrows()
    {
        var payload = BuildConstantFalseBitmapPayload(totalRows: 4);

        var act = () => BitmapRleCodec.DecodeSlice(
            payload, firstRowIndex: 0, count: (int)PerRowGroupCeiling + 1, maxDecodableRows: PerRowGroupCeiling);

        act.Should().Throw<CorruptSegmentException>().WithMessage("*ceiling*");
    }

    [Fact]
    public void DeltaVlqInt64Full_ForgedCount_AboveCeiling_StillThrows()
    {
        // Full (non-slice) decoder still bounds the declared count against the ceiling.
        var payload = BuildConstantDeltaInt64Payload(firstValue: 0, delta: 1, totalRows: 4);

        var act = () => DeltaVlqCodec.DecodeInt64(
            payload, count: (int)PerRowGroupCeiling + 1, maxDecodableRows: PerRowGroupCeiling);

        act.Should().Throw<CorruptSegmentException>().WithMessage("*ceiling*");
    }

    [Fact]
    public void BitmapRleFull_ValidConstantColumn_DecodesManyRowsFromFewBytes()
    {
        // The core premise: a run-length codec decodes a great many rows from a few bytes. A
        // constant false column of 131_072 rows encodes in ~4 bytes; with the per-row-group ceiling
        // it must decode all rows — the segment-byte-length premise ("N bytes => <= N rows") is false.
        var payload = BuildConstantFalseBitmapPayload(totalRows: 131_072);
        payload.Length.Should().BeLessThan(16, "a constant run-length column compresses to a few bytes");

        var decoded = BitmapRleCodec.Decode(payload, count: 131_072, maxDecodableRows: PerRowGroupCeiling);

        decoded.Should().HaveCount(131_072);
        decoded.Should().OnlyContain(b => b == false);
    }

    [Fact]
    public void GorillaXorFull_ForgedCount_AboveCeiling_StillThrows()
    {
        // A ~16 KiB all-zero payload passes Gorilla's ≥1-bit-per-value self-bound for a forged
        // count just above the ceiling (zeros decode as endless reuse-previous control bits, so
        // the decode even SUCCEEDS); without the per-row-group ceiling the decoder allocates
        // count doubles from garbage — a 64× allocation amplification over every sibling codec,
        // which all thread MaxDecodableRowsPerRowGroup.
        var payload = new byte[((131_073 - 1) / 8) + 9];

        var act = () => GorillaXorCodec.Decode(payload, count: 131_073, maxDecodableRows: PerRowGroupCeiling);

        act.Should().Throw<CorruptSegmentException>().WithMessage("*ceiling*");
    }

    // ───── payload builders (use the real encoders) ──────────────────────

    private static byte[] BuildConstantDeltaInt64Payload(long firstValue, long delta, int totalRows)
    {
        var values = new long[totalRows];
        var v = firstValue;
        for (var i = 0; i < totalRows; i++)
        {
            values[i] = v;
            v += delta;
        }

        return DeltaVlqCodec.EncodeInt64(values);
    }

    private static byte[] BuildConstantFalseBitmapPayload(int totalRows)
    {
        var values = new bool[totalRows]; // all false
        return BitmapRleCodec.Encode(values);
    }
}
