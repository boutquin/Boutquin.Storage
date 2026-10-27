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
/// Allocation-before-bound DoS guard. A forged footer RowCount must not drive a multi-gigabyte
/// <c>new T[count]</c> before the decoder has read any payload byte. Each scalar decoder bounds
/// the declared <c>count</c> against what the (CRC-validated but possibly forged) payload could
/// possibly encode BEFORE allocating, throwing the typed format-corruption exception
/// (<see cref="InvalidDataException"/>) rather than allocating first.
///
/// Each test uses a small declared count whose required minimum payload exceeds the supplied
/// payload, so the guard fires the same way the int.MaxValue case would — but without risking an
/// actual OOM in the test run.
/// </summary>
public sealed class CodecUnboundedRowCountAllocTests
{
    [Fact]
    public void GorillaXor_HugeCount_ShortPayload_ThrowsBeforeAllocating()
    {
        // 8-byte payload carries exactly the first raw value and zero meaningful bits afterward, so
        // at most one value is decodable. A declared count of 1,000,000 is impossible.
        var payload = new byte[8];

        var act = () => GorillaXorCodec.Decode(payload, 1_000_000, long.MaxValue);
        act.Should().Throw<InvalidDataException>()
            .WithMessage("*exceeds*");
    }

    [Fact]
    public void DeltaVlqInt64_HugeCount_ShortPayload_ThrowsBeforeAllocating()
    {
        // first value (1 VLQ byte) + 1 run-length byte cannot encode a run total of 100,000
        // (which needs a >= 3-byte VLQ). The guard rejects it before allocating new long[100000].
        using var ms = new MemoryStream();
        Vlq.WriteUInt64(ms, 0);   // first value = 0 (1 byte)
        ms.WriteByte(0x01);       // 1 leftover byte — not enough to encode the declared count
        var payload = ms.ToArray();

        var act = () => DeltaVlqCodec.DecodeInt64(payload, 100_000);
        act.Should().Throw<InvalidDataException>()
            .WithMessage("*exceeds*");
    }

    [Fact]
    public void DeltaOfDeltaVlq_HugeCount_ShortPayload_ThrowsBeforeAllocating()
    {
        using var ms = new MemoryStream();
        Vlq.WriteInt64(ms, 0);  // first value = day number 0 (1 VLQ byte)
        ms.WriteByte(0x01);     // 1 leftover byte → 2-byte payload, max ~16k rows
        var payload = ms.ToArray();

        var act = () => DeltaOfDeltaVlqCodec.Decode(payload, 100_000);
        act.Should().Throw<InvalidDataException>()
            .WithMessage("*exceeds*");
    }

    [Fact]
    public void BitmapRle_HugeCount_ShortPayload_ThrowsBeforeAllocating()
    {
        // start byte + 1 run byte cannot encode a run total of 100,000.
        var payload = new byte[] { 1, 0x05 };

        var act = () => BitmapRleCodec.Decode(payload, 100_000);
        act.Should().Throw<InvalidDataException>()
            .WithMessage("*exceeds*");
    }

    [Fact]
    public void DeltaVlqInt64_SaturatedPayload_CountExceedsSegmentCeiling_ThrowsBeforeAllocating()
    {
        // Regression for the saturated-bound regime the short-payload tests above MISS. With a
        // run-length VLQ stream of >= 9 bytes, Vlq.MaxCountForRunLengthBytes saturates to
        // long.MaxValue, so the per-payload guard is a no-op against any int count — a forged
        // footer RowCount up to int.MaxValue would drive a multi-gigabyte new long[count]. The
        // segment-byte-length ceiling (maxDecodableRows) is the bound that must fire. Counts and
        // ceiling are kept small so the test verifies the BOUND without allocating gigabytes.
        using var ms = new MemoryStream();
        Vlq.WriteInt64(ms, 0);          // first value (1 byte)
        for (var i = 0; i < 9; i++)     // 9 further bytes → run-length stream is >= 9 bytes
        {
            ms.WriteByte(0x01);
        }

        var payload = ms.ToArray();
        Vlq.MaxCountForRunLengthBytes(payload.Length).Should().Be(long.MaxValue,
            "the payload is in the saturated regime where the per-payload guard cannot discriminate");

        // count (100) exceeds the segment ceiling (10) but is tiny in absolute terms.
        var act = () => DeltaVlqCodec.DecodeInt64(payload, count: 100, maxDecodableRows: 10);
        act.Should().Throw<CorruptSegmentException>()
            .WithMessage("*per-row-group ceiling*");
    }

    [Fact]
    public void BitmapRle_SaturatedPayload_CountExceedsSegmentCeiling_ThrowsBeforeAllocating()
    {
        // Same saturated-regime regression for the bool codec. Start byte + >= 9 run-length bytes
        // saturates MaxCountForRunLengthBytes; the segment ceiling is the bound that fires.
        var payload = new byte[11];     // start byte + 10 run-length bytes (>= 9)
        payload[0] = 1;
        for (var i = 1; i < payload.Length; i++)
        {
            payload[i] = 0x01;
        }

        Vlq.MaxCountForRunLengthBytes(payload.Length - 1).Should().Be(long.MaxValue,
            "the run-length stream is in the saturated regime");

        var act = () => BitmapRleCodec.Decode(payload, count: 100, maxDecodableRows: 10);
        act.Should().Throw<CorruptSegmentException>()
            .WithMessage("*per-row-group ceiling*");
    }

    [Fact]
    public void DictionaryString_HugeCount_ShortPayload_ThrowsBeforeAllocating()
    {
        // Magic + version + dictSize 0 + no id bytes. The id stream needs >= 1 VLQ byte per row,
        // so a declared count of 100,000 with zero id bytes is impossible.
        using var ms = new MemoryStream();
        ms.WriteByte(0xFF); // StringDictMagic
        ms.WriteByte(1);    // version
        Vlq.WriteUInt64(ms, 0); // dictSize = 0
        var payload = ms.ToArray();

        var act = () => DictionaryCodec.DecodeString(payload, 100_000);
        act.Should().Throw<InvalidDataException>()
            .WithMessage("*exceeds*");
    }

    [Fact]
    public void ScaledIntegerDecodeDecimal_PayloadBoundWrappingInt32_ThrowsBeforeAllocating()
    {
        // Header-only payload: scale 0, min 0, bitWidth 64, no body. count = 2^28 rows at 64 bits
        // each needs exactly 2^31 payload bytes — one past int.MaxValue. The payload bound must be
        // compared in 64-bit arithmetic: narrowed to int it wraps to int.MinValue, the "payload too
        // short" comparison passes, and a 4 GiB decimal[] is allocated before anything throws.
        var payload = new byte[10];
        payload[9] = 64;
        const int count = 1 << 28;

        var before = GC.GetAllocatedBytesForCurrentThread();
        var act = () => ScaledIntegerBitPackedCodec.DecodeDecimal(payload, count);
        act.Should().Throw<InvalidDataException>()
            .WithMessage("*truncated*2147483648*");
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.Should().BeLessThan(1 << 20,
            "the forged count must be rejected before the decimal[count] allocation");
    }
}
