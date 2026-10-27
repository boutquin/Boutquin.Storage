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
/// Allocation-before-bound DoS guard on the <c>*Slice</c> decode paths — the sibling of
/// <see cref="CodecUnboundedRowCountAllocTests"/>, which covers the full-column decoders. A slice
/// decoder must not drive a multi-gigabyte <c>new T[count]</c> from a forged slice <c>count</c>
/// before reading any payload byte. Each slice decoder bounds the declared <c>count</c> against
/// what the (CRC-validated but possibly forged) payload could possibly encode BEFORE allocating,
/// throwing the typed format-corruption exception (<see cref="InvalidDataException"/>).
///
/// Each test uses a small declared count (1,000,000) whose required minimum payload exceeds the
/// supplied payload, so the guard fires the same way the int.MaxValue case would — without risking
/// an actual OOM in the test run.
/// </summary>
public sealed class CodecSliceUnboundedRowCountAllocTests
{
    [Fact]
    public void DeltaVlqInt64Slice_HugeCount_ShortPayload_ThrowsBeforeAllocating()
    {
        // first value (1 VLQ byte) + 1 run-length byte cannot encode a slice total of 1,000,000.
        // The guard must reject it before allocating new long[1000000].
        using var ms = new MemoryStream();
        Vlq.WriteInt64(ms, 0);   // first value = 0 (1 byte)
        ms.WriteByte(0x01);      // 1 leftover byte — not enough to encode the declared count
        var payload = ms.ToArray();

        var act = () => DeltaVlqCodec.DecodeSliceInt64(payload, firstRowIndex: 0, count: 1_000_000);
        act.Should().Throw<InvalidDataException>()
            .WithMessage("*exceeds*");
    }

    [Fact]
    public void DeltaVlqInt32Slice_HugeCount_ShortPayload_ThrowsBeforeAllocating()
    {
        // Int32 slice delegates to the int64 slice; the same guard must fire before the
        // new int[1000000] copy.
        using var ms = new MemoryStream();
        Vlq.WriteInt64(ms, 0);
        ms.WriteByte(0x01);
        var payload = ms.ToArray();

        var act = () => DeltaVlqCodec.DecodeSliceInt32(payload, firstRowIndex: 0, count: 1_000_000);
        act.Should().Throw<InvalidDataException>()
            .WithMessage("*exceeds*");
    }

    [Fact]
    public void BitmapRleSlice_HugeCount_ShortPayload_ThrowsBeforeAllocating()
    {
        // start byte + 1 run byte cannot encode a slice total of 1,000,000.
        var payload = new byte[] { 1, 0x05 };

        var act = () => BitmapRleCodec.DecodeSlice(payload, firstRowIndex: 0, count: 1_000_000);
        act.Should().Throw<InvalidDataException>()
            .WithMessage("*exceeds*");
    }

    [Fact]
    public void DeltaVlqInt64Slice_SaturatedPayload_ExtentExceedsSegmentCeiling_ThrowsBeforeAllocating()
    {
        // Saturated-regime regression for the slice path: a >= 9-byte run-length stream makes the
        // per-payload guard a no-op, so the segment-byte-length ceiling must bound the slice extent
        // [firstRowIndex, firstRowIndex + count) before the new long[count]. Counts/ceiling small.
        using var ms = new MemoryStream();
        Vlq.WriteInt64(ms, 0);
        for (var i = 0; i < 9; i++)
        {
            ms.WriteByte(0x01);
        }

        var payload = ms.ToArray();
        var act = () => DeltaVlqCodec.DecodeSliceInt64(payload, firstRowIndex: 5, count: 100, maxDecodableRows: 10);
        act.Should().Throw<CorruptSegmentException>()
            .WithMessage("*per-row-group ceiling*");
    }

    [Fact]
    public void BitmapRleSlice_SaturatedPayload_ExtentExceedsSegmentCeiling_ThrowsBeforeAllocating()
    {
        var payload = new byte[11];
        payload[0] = 1;
        for (var i = 1; i < payload.Length; i++)
        {
            payload[i] = 0x01;
        }

        var act = () => BitmapRleCodec.DecodeSlice(payload, firstRowIndex: 5, count: 100, maxDecodableRows: 10);
        act.Should().Throw<CorruptSegmentException>()
            .WithMessage("*per-row-group ceiling*");
    }

    [Fact]
    public void DeltaOfDeltaVlqSlice_SaturatedPayload_CountExceedsSegmentCeiling_ThrowsBeforeAllocating()
    {
        // DeltaOfDeltaVlq is non-restartable: it is routed through the per-row-group slice path
        // (DecodeColumnSlice -> DeltaOfDeltaVlqCodec.Decode), where the segment ceiling is in scope.
        // With a >= 9-byte payload, Vlq.MaxCountForRunLengthBytes saturates to long.MaxValue, so the
        // codec's per-payload guard is a no-op against any int count — a forged footer RowCount would
        // drive new DateOnly[count] before any payload read. The segment-byte-length ceiling
        // (maxDecodableRows) is the bound that must fire. Counts/ceiling are small so the test
        // verifies the BOUND without allocating gigabytes.
        using var ms = new MemoryStream();
        Vlq.WriteInt64(ms, 0);          // first value = day number 0 (1 byte)
        for (var i = 0; i < 9; i++)     // 9 further bytes → run-length stream is >= 9 bytes
        {
            ms.WriteByte(0x00);
        }

        var payload = ms.ToArray();
        Vlq.MaxCountForRunLengthBytes(payload.Length).Should().Be(long.MaxValue,
            "the payload is in the saturated regime where the per-payload guard cannot discriminate");

        // count (100) exceeds the segment ceiling (10) but is tiny in absolute terms. Only the new
        // segment-ceiling guard throws the typed CorruptSegmentException with this message; before
        // the fix the saturated per-payload guard is vacuous and the giant new DateOnly[count] is
        // allocated, so the call instead reaches the payload-exhaustion path (InvalidDataException).
        var act = () => DeltaOfDeltaVlqCodec.Decode(payload, count: 100, maxDecodableRows: 10);
        act.Should().Throw<CorruptSegmentException>()
            .WithMessage("*per-row-group ceiling*");
    }

    [Fact]
    public void DictionaryStringSlice_HugeCount_ShortPayload_ThrowsBeforeAllocating()
    {
        // Magic + version + dictSize 0 + no id bytes. The id stream needs >= 1 VLQ byte per row,
        // so a declared slice count of 1,000,000 with zero id bytes is impossible.
        using var ms = new MemoryStream();
        ms.WriteByte(0xFF); // StringDictMagic
        ms.WriteByte(1);    // version
        Vlq.WriteUInt64(ms, 0); // dictSize = 0
        var payload = ms.ToArray();

        var act = () => DictionaryCodec.DecodeStringSlice(payload, firstRowIndex: 0, count: 1_000_000);
        act.Should().Throw<InvalidDataException>()
            .WithMessage("*exceeds*");
    }

    [Fact]
    public void DictionaryDecimalSlice_HugeCount_ShortPayload_ThrowsBeforeAllocating()
    {
        // dictSize 0 + no id bytes. The id stream needs >= 1 VLQ byte per row, so a declared
        // slice count of 1,000,000 over an empty id stream is impossible.
        using var ms = new MemoryStream();
        Vlq.WriteUInt64(ms, 0); // dictSize = 0
        var payload = ms.ToArray();

        var act = () => DictionaryCodec.DecodeDecimalSlice(payload, firstRowIndex: 0, count: 1_000_000);
        act.Should().Throw<InvalidDataException>()
            .WithMessage("*exceeds*");
    }

    [Fact]
    public void ScaledIntegerDecimalSlice_HugeCount_ShortPayload_ThrowsBeforeAllocating()
    {
        // bitWidth > 0: header (scale=2, min=0, width=8) + 1 payload byte holds 1 row of 8 bits,
        // so a declared slice count of 1,000,000 is impossible. The bitWidth>0 truncation guard
        // (mirrored from the full decoder) bounds rows [firstRowIndex, firstRowIndex+count) against
        // the payload BEFORE the new decimal[count] is reached. Characterization + bound coverage.
        var payload = new byte[ScaledIntegerHeaderSize + 1];
        payload[0] = 2;  // scale
        payload[9] = 8;  // bitWidth = 8

        var act = () => ScaledIntegerBitPackedCodec.DecodeDecimalSlice(payload, firstRowIndex: 0, count: 1_000_000);
        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void ScaledIntegerDecimalSlice_IntMaxCount_TinyPayload_ThrowsInvalidDataNotOom()
    {
        // bitWidth > 0 with a forged int.MaxValue row count over a 1-byte payload. The bitWidth>0
        // truncation bound MUST be evaluated BEFORE the new decimal[count] allocation: a decimal[]
        // of int.MaxValue elements is ~32 GiB and throws OutOfMemoryException on the allocation
        // attempt. If the bound runs first (correct), it throws the typed InvalidDataException
        // cheaply without touching the heap. This pins ordering — the plain "throws" test above
        // passes even when the alloc precedes the bound, because count=1,000,000 does not OOM.
        var payload = new byte[ScaledIntegerHeaderSize + 1];
        payload[0] = 2;  // scale
        payload[9] = 8;  // bitWidth = 8

        var act = () => ScaledIntegerBitPackedCodec.DecodeDecimalSlice(payload, firstRowIndex: 0, count: int.MaxValue);
        act.Should().Throw<InvalidDataException>();
    }

    // The ScaledInteger header is 10 bytes: scale (1) + min (8) + bitWidth (1).
    private const int ScaledIntegerHeaderSize = 10;
}
