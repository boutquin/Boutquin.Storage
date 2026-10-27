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
namespace Boutquin.Storage.Infrastructure.Tests.Serialization;

// M-1 (storage review-remediation F-11/F-12/F-13): the shared wire helpers must surface a
// TYPED corruption exception (InvalidDataException) — not a raw OverflowException or an
// unbounded drain — when a forged length prefix declares more than the payload can supply.
// These pin the forged-length surfaces that the seekable-bound guards in ReadString /
// SerializableWrapper.Deserialize already cover for SEEKABLE streams, extended to the
// >int.MaxValue cast boundary and the non-seekable path that the seekable bound can't reach.
public sealed class WireHelperForgedLengthTests
{
    // ── AC-1.1: ReadString length-prefix > int.MaxValue ──────────────────────────────────

    [Fact]
    public void ReadString_DeclaredLengthBeyondInt32_ThrowsInvalidData_NotOverflow()
    {
        // A forged varint declaring a length above int.MaxValue must be rejected as corruption
        // with the repo's typed corruption exception. The un-fixed code does `checked((int)length)`
        // which surfaces a raw OverflowException — a caller catching the documented
        // InvalidDataException corruption contract would miss it.
        using var ms = new MemoryStream();
        VarintHelpers.WriteVarUInt64(ms, (ulong)int.MaxValue + 1); // 2^31, just past int.MaxValue
        ms.Position = 0;

        var act = () => VarintHelpers.ReadString(ms);

        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void ReadString_NonSeekableForgedHugeLength_ThrowsTyped_WithoutHugeAllocation()
    {
        // On a NON-seekable stream the seekable remaining-bytes bound cannot fire, so a forged
        // huge length must still be rejected without renting/allocating the full declared size.
        // Declared length int.MaxValue over a 3-byte body: the typed corruption exception fires
        // and total allocation stays small (no ~2 GB rent/array).
        var forged = BuildForgedLengthPrefixedPayload((ulong)int.MaxValue, payloadBytes: 3);
        using var stream = new NonSeekableStream(forged);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var act = () => VarintHelpers.ReadString(stream);
        act.Should().Throw<InvalidDataException>();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.Should().BeLessThan(10_000_000,
            "a forged length on a non-seekable stream must be bounded by the bytes actually read, never pre-allocated");
    }

    // ── AC-1.2: SkipField LengthPrefixed forged length ───────────────────────────────────

    [Fact]
    public void SkipField_LengthPrefixed_NonSeekableForgedHugeLength_ThrowsInvalidData_Bounded()
    {
        // SkipField(LengthPrefixed) reads the declared length then advances past it. On a
        // non-seekable stream the un-fixed AdvanceExact drains the declared length chunk-by-chunk
        // until the short stream ends and surfaces a raw EndOfStreamException — and for an
        // unbounded declared length it would keep reading until the real EOF (an unbounded drain,
        // not a fail-fast). The fix bounds the skip by the bytes the stream supplies and surfaces
        // the typed corruption exception.
        var forged = BuildForgedLengthPrefixedPayload((ulong)int.MaxValue, payloadBytes: 4);
        using var stream = new NonSeekableStream(forged);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var act = () => VarintHelpers.SkipField(stream, WireType.LengthPrefixed);
        act.Should().Throw<InvalidDataException>();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.Should().BeLessThan(10_000_000,
            "skipping a forged length on a non-seekable stream must be bounded, never drain/allocate the full declared length");
    }

    [Fact]
    public void SkipField_LengthPrefixed_SeekableForgedLength_ThrowsInvalidData()
    {
        // Seekable path: a declared length beyond the remaining stream bytes is corruption; the
        // un-fixed AdvanceExact surfaces a raw EndOfStreamException. Fail-closed with the typed
        // corruption exception so the skip path matches the documented contract.
        using var ms = new MemoryStream();
        VarintHelpers.WriteVarUInt64(ms, 1_000_000UL); // declared >> actual remaining
        ms.WriteByte(0x01); // one real body byte
        ms.Position = 0;

        var act = () => VarintHelpers.SkipField(ms, WireType.LengthPrefixed);

        act.Should().Throw<InvalidDataException>();
    }

    // ── AC-1.3: SerializableWrapper.Deserialize non-seekable forged oversized prefix ──────

    [Fact]
    public void Deserialize_NonSeekableForgedOversizedPrefix_ThrowsTyped_WithoutFullAllocation()
    {
        // F-11: on a NON-seekable stream the seekable bound cannot fire, so Deserialize's
        // `new byte[payloadLength]` allocates the full declared size before ReadExact ever
        // observes the short stream — a forged int.MaxValue prefix rents ~2 GB. The fix reads
        // the payload in bounded chunks (or bounds the allocation against bytes supplied) and
        // surfaces a typed corruption exception, not a raw OverflowException/OOM.
        var forged = BuildForgedLengthPrefixedPayload((ulong)int.MaxValue, payloadBytes: 4);
        using var stream = new NonSeekableStream(forged);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var act = () => SerializableWrapper<int>.Deserialize(stream);
        act.Should().Throw<InvalidDataException>();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.Should().BeLessThan(10_000_000,
            "a forged oversized payload prefix on a non-seekable stream must not pre-allocate the full declared size");
    }

    [Fact]
    public void Deserialize_PayloadLengthBeyondInt32_ThrowsInvalidData_NotOverflow()
    {
        // A forged frame whose length prefix exceeds int.MaxValue must be rejected as corruption
        // with the typed exception, not a raw OverflowException from `checked((int)...)`.
        using var ms = new MemoryStream();
        VarintHelpers.WriteVarUInt64(ms, (ulong)int.MaxValue + 1);
        ms.Position = 0;

        var act = () => SerializableWrapper<int>.Deserialize(ms);

        act.Should().Throw<InvalidDataException>();
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────

    // A ULEB128 length prefix declaring `declaredLength`, followed by `payloadBytes` real bytes.
    private static byte[] BuildForgedLengthPrefixedPayload(ulong declaredLength, int payloadBytes)
    {
        using var ms = new MemoryStream();
        VarintHelpers.WriteVarUInt64(ms, declaredLength);
        ms.Write(new byte[payloadBytes]);
        return ms.ToArray();
    }

    private sealed class NonSeekableStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data, writable: false);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => _inner.Read(buffer);
        public override int ReadByte() => _inner.ReadByte();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
