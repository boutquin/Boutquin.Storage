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

// AC-11.2 — forged/edge codec coverage gaps. Each test feeds a hand-forged payload that the codec's
// existing guard must reject with a TYPED corruption exception (InvalidDataException), never a raw
// IndexOutOfRange / overflow / out-of-band exception. These pin behavior the round-trip and
// allocation-ceiling suites do not isolate.
public sealed class CodecForgedInputTypedThrowTests
{
    // ───── DictionaryCodec — forged string id out of dictionary range ─────

    [Fact]
    public void DictionaryCodec_DecodeString_ForgedIdAboveDictSize_ThrowsTypedInvalidData()
    {
        // Layout: magic 0xFF, version 1, VLQ dictSize=1, one dict entry ("a" = VLQ len 1 + 'a'),
        // then one row id whose value (2) exceeds the [0, dictSize=1] range. The id-stream bound
        // guard permits the byte count; the per-row range check is what must fire.
        byte[] forged =
        [
            DictionaryCodec.StringDictMagic, // magic
            0x01,                            // version
            0x01,                            // VLQ dictSize = 1
            0x01,                            // VLQ string length = 1
            (byte)'a',                       // the single dict string
            0x02,                            // VLQ row id = 2 — out of range [0, 1]
        ];

        var act = () => DictionaryCodec.DecodeString(forged, count: 1);

        act.Should().Throw<InvalidDataException>(
            "a forged dictionary id beyond the dictionary range must surface a typed corruption "
            + "exception, not a raw IndexOutOfRangeException from dict[id - 1]")
            .WithMessage("*out of range*");
    }

    [Fact]
    public void DictionaryCodec_DecodeString_ForgedIdAboveIntMax_ThrowsTypedInvalidData()
    {
        // Layout: magic 0xFF, version 1, VLQ dictSize=1, one dict entry ("a"), then one row id
        // whose VLQ value is 2^35 (34,359,738,368) — far beyond int.MaxValue. The id-stream byte
        // bound permits the 6-byte VLQ; the codec must range-validate the id VALUE against the
        // dictionary BEFORE narrowing to int, so a forged > int.MaxValue id surfaces the same typed
        // InvalidDataException as every other out-of-range id — not a raw OverflowException from
        // `checked((int)id64)`.
        byte[] forged =
        [
            DictionaryCodec.StringDictMagic,            // magic
            0x01,                                       // version
            0x01,                                       // VLQ dictSize = 1
            0x01,                                       // VLQ string length = 1
            (byte)'a',                                  // the single dict string
            0x80, 0x80, 0x80, 0x80, 0x80, 0x01,         // VLQ row id = 2^35 > int.MaxValue
        ];

        var act = () => DictionaryCodec.DecodeString(forged, count: 1);

        act.Should().Throw<InvalidDataException>(
            "a forged dictionary id above int.MaxValue must surface the codec's typed corruption "
            + "exception, not a raw OverflowException from the checked int narrowing")
            .WithMessage("*out of range*");
    }

    [Fact]
    public void DictionaryCodec_DecodeDecimal_ForgedIdAboveIntMax_ThrowsTypedInvalidData()
    {
        // Layout: VLQ dictSize=1, one 16-byte decimal entry (all-zero bits = 0m, valid flags word),
        // then one row id whose VLQ value is 2^35 > int.MaxValue. As with the string path, the id
        // VALUE must be range-validated against the dictionary BEFORE the checked int narrowing, so
        // the forged id surfaces a typed InvalidDataException, not a raw OverflowException.
        byte[] forged =
        [
            0x01,                                       // VLQ dictSize = 1
            0x00, 0x00, 0x00, 0x00,                     // decimal bits[0]
            0x00, 0x00, 0x00, 0x00,                     // decimal bits[1]
            0x00, 0x00, 0x00, 0x00,                     // decimal bits[2]
            0x00, 0x00, 0x00, 0x00,                     // decimal bits[3] (flags = 0 → scale 0, positive)
            0x80, 0x80, 0x80, 0x80, 0x80, 0x01,         // VLQ row id = 2^35 > int.MaxValue
        ];

        var act = () => DictionaryCodec.DecodeDecimal(forged, count: 1);

        act.Should().Throw<InvalidDataException>(
            "a forged decimal dictionary id above int.MaxValue must surface the codec's typed "
            + "corruption exception, not a raw OverflowException from the checked int narrowing")
            .WithMessage("*out of range*");
    }

    // ───── GorillaXor — forged reuse-previous-block before any block declared ─────

    [Fact]
    public void GorillaXor_ForgedReuseBlockBeforeAnyBlock_ThrowsTypedInvalidData()
    {
        // 8-byte first value, then for value #2 the control bits '1' (xor != 0) followed by '0'
        // (reuse previous block) — but no '1' new-block header has ever set prevMeaningful. The
        // decoder must reject this forged reuse-before-block control word with a typed exception
        // rather than read a 0-width meaningful span and silently desync.
        var forged = new byte[9];
        // forged[0..8) — first raw IEEE-754 value (all-zero double = 0.0); content is irrelevant.
        // forged[8]: control byte. The BitWriter emits MSB-first; bits "1" then "0" occupy the two
        // high bits → 0b1000_0000 = 0x80.
        forged[8] = 0x80;

        var act = () => GorillaXorCodec.Decode(forged, count: 2, long.MaxValue);

        act.Should().Throw<InvalidDataException>(
            "a forged reuse-previous-block control word before any block has been declared must "
            + "surface a typed corruption exception")
            .WithMessage("*reuse-previous-block*");
    }

    // ───── DeltaOfDeltaVlq — forged dod drives the running day-number out of DateOnly range ─────

    [Fact]
    public void DeltaOfDeltaVlq_ForgedDodOutOfDateOnlyRange_StaysRangeValidated()
    {
        // Forge: first day = 0 (valid), first delta = 0, then a dod so large the running day-number
        // leaves [DateOnly.MinValue, DateOnly.MaxValue]. The decoder's ValidateDayNumber must catch
        // it with a typed exception before `DateOnly.FromDayNumber((int)previous)` throws raw.
        using var ms = new MemoryStream();
        Vlq.WriteInt64(ms, 0);                          // first day number = 0
        Vlq.WriteInt64(ms, 0);                          // first delta = 0
        Vlq.WriteInt64(ms, DateOnly.MaxValue.DayNumber + 1_000_000L); // forged dod → out of range
        var forged = ms.ToArray();

        var act = () => DeltaOfDeltaVlqCodec.Decode(forged, count: 3);

        act.Should().Throw<InvalidDataException>(
            "a forged delta-of-delta that drives the running day-number outside DateOnly range must "
            + "stay range-validated and surface a typed corruption exception")
            .WithMessage("*outside DateOnly range*");
    }

    // ───── Vlq — 10-byte continuation overflow ─────

    [Fact]
    public void Vlq_ReadUInt64Span_ElevenContinuationBytes_ThrowsTypedInvalidData()
    {
        // 11 bytes all with the continuation bit (0x80) set: a well-formed ulong VLQ is at most 10
        // bytes, so the 11th byte means the shift counter has exceeded 70 bits — corrupt input that
        // must throw a typed exception rather than silently mask high bits.
        var forged = new byte[11];
        for (var i = 0; i < forged.Length; i++)
        {
            forged[i] = 0x80;
        }

        var act = () => Vlq.ReadUInt64(forged.AsSpan());

        act.Should().Throw<InvalidDataException>(
            "a VLQ continuation run exceeding 10 bytes must surface a typed corruption exception")
            .WithMessage("*exceeds 10 bytes*");
    }

    [Fact]
    public void Vlq_ReadInt64Span_ElevenContinuationBytes_ThrowsTypedInvalidData()
    {
        var forged = new byte[11];
        for (var i = 0; i < forged.Length; i++)
        {
            forged[i] = 0x80;
        }

        var act = () => Vlq.ReadInt64(forged.AsSpan());

        act.Should().Throw<InvalidDataException>(
            "a signed VLQ continuation run exceeding 10 bytes must surface a typed corruption exception")
            .WithMessage("*exceeds 10 bytes*");
    }

    // ───── run-framing forgeries: truncation between runLen and delta; runLen past Int32 ─────

    [Fact]
    public void DeltaVlq_TruncatedBetweenRunLengthAndDelta_ThrowsTypedInvalidData()
    {
        // [first=0][runLen=2] then STOP — the payload ends between a run's length and its
        // delta. The exhaustion guard fires before the runLen read but not before the delta
        // read, so the raw EndOfStreamException from the VLQ reader leaks.
        byte[] forged = [0x00, 0x02];

        var act = () => DeltaVlqCodec.DecodeInt64(forged, count: 3, maxDecodableRows: long.MaxValue);

        act.Should().Throw<InvalidDataException>(
            "a payload truncated inside a run frame is the same corruption class as one truncated between frames");
    }

    [Fact]
    public void DeltaVlq_RunLengthBeyondInt32_ThrowsTypedInvalidData()
    {
        // [first=0][runLen=2^32][delta=0] — checked((int)runLen) would leak a raw
        // OverflowException; a run length that cannot fit the declared row count is corruption.
        byte[] forged = [0x00, 0x80, 0x80, 0x80, 0x80, 0x10, 0x00];

        var act = () => DeltaVlqCodec.DecodeInt64(forged, count: 5, maxDecodableRows: long.MaxValue);

        act.Should().Throw<InvalidDataException>(
            "a forged run length past Int32.MaxValue must surface the typed corruption exception");
    }

    [Fact]
    public void BitmapRle_RunLengthBeyondInt32_ThrowsTypedInvalidData()
    {
        // [start=0][runLen=2^32] — same checked((int)…) leak in the bitmap run reader.
        byte[] forged = [0x00, 0x80, 0x80, 0x80, 0x80, 0x10];

        var act = () => BitmapRleCodec.Decode(forged, count: 5, maxDecodableRows: long.MaxValue);

        act.Should().Throw<InvalidDataException>(
            "a forged bitmap run length past Int32.MaxValue must surface the typed corruption exception");
    }

    [Fact]
    public void BitmapRleSlice_RunLengthBeyondInt32_DecodesWithoutLeakingOverflow()
    {
        // The slice decoder reads the WHOLE-COLUMN payload, where a single run can
        // legitimately span more rows than any per-group bound (that is the premise of
        // run-length encoding), so a >Int32 run is not provably corrupt here. The contract is
        // that it must never leak a raw OverflowException from checked((int)…) — the run is
        // clamped and walked only as far as the slice needs.
        byte[] payload = [0x00, 0x80, 0x80, 0x80, 0x80, 0x10]; // one false-run of 2^32 rows

        var decoded = BitmapRleCodec.DecodeSlice(payload, firstRowIndex: 0, count: 2, maxDecodableRows: long.MaxValue);

        decoded.Should().Equal(false, false);
    }
}
