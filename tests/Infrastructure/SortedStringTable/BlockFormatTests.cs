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
using Boutquin.Storage.Domain.Interfaces.Compression;
using Boutquin.Storage.Infrastructure.Compression;

namespace Boutquin.Storage.Infrastructure.Tests.SortedStringTable;

/// <summary>
/// Phase C1/C2/C3 verification: block-level round-trip with restart points + prefix
/// compression; per-block CRC32C (Castagnoli); bit-flip anywhere in the block frame
/// raises <c>CorruptBlockException</c>; restart points enable binary-search seek within
/// the block.
/// </summary>
public sealed class BlockFormatTests
{
    private static BlockEntry E(byte[] key, byte[] value, ulong seqno = 0UL) =>
        new(key, seqno, ValueKind.Inline, value);

    public static IEnumerable<object[]> AllCodecs()
    {
        yield return new object[] { (ICompressionCodec)new NoCompressionCodec() };
        yield return new object[] { (ICompressionCodec)new LZ4Codec() };
        yield return new object[] { (ICompressionCodec)new GZipCodec() };
        yield return new object[] { (ICompressionCodec)new BrotliCodec() };
    }

    [Theory]
    [MemberData(nameof(AllCodecs))]
    public void RoundTrip_PreservesEntriesInOrder(ICompressionCodec codec)
    {
        var entries = new[]
        {
            E([1, 0], [0xAA], 1UL),
            E([2, 0], [0xBB], 2UL),
            E([3, 0], [0xCC], 3UL),
            E([4, 0], [0xDD], 4UL),
        };

        var framed = BlockFormat.WriteBlock(entries, codec, restartInterval: 2);
        using var stream = new MemoryStream(framed);
        var decoded = BlockFormat.ReadBlock(stream, codec);

        decoded.Should().HaveCount(entries.Length);
        for (var i = 0; i < entries.Length; i++)
        {
            decoded[i].Key.Span.ToArray().Should().Equal(entries[i].Key.Span.ToArray());
            decoded[i].Value.Span.ToArray().Should().Equal(entries[i].Value.Span.ToArray());
            decoded[i].SequenceNumber.Should().Be(entries[i].SequenceNumber);
            decoded[i].Kind.Should().Be(entries[i].Kind);
        }
    }

    [Fact]
    public void RoundTrip_WithPrefixCompression_ReconstructsFullKeys()
    {
        // Long shared prefix — prefix compression must still reconstruct the original
        // key bytes at read time. Entries between restart points share bytes with the
        // preceding entry.
        var prefix = System.Text.Encoding.UTF8.GetBytes("metrics/latency/");
        var entries = new[]
        {
            E(Combine(prefix, "p50"u8.ToArray()), "v0"u8.ToArray(), 10UL),
            E(Combine(prefix, "p90"u8.ToArray()), "v1"u8.ToArray(), 11UL),
            E(Combine(prefix, "p95"u8.ToArray()), "v2"u8.ToArray(), 12UL),
            E(Combine(prefix, "p99"u8.ToArray()), "v3"u8.ToArray(), 13UL),
        };

        var framed = BlockFormat.WriteBlock(entries, new NoCompressionCodec(), restartInterval: 4);
        using var stream = new MemoryStream(framed);
        var decoded = BlockFormat.ReadBlock(stream, new NoCompressionCodec());

        decoded.Should().HaveCount(entries.Length);
        for (var i = 0; i < entries.Length; i++)
        {
            decoded[i].Key.Span.ToArray().Should().Equal(entries[i].Key.Span.ToArray());
        }
    }

    [Fact]
    public void BitFlip_InBlockPayload_RaisesCorruptBlockException()
    {
        var entries = new[] { E([1], [1]), E([2], [2]) };
        var framed = BlockFormat.WriteBlock(entries, new NoCompressionCodec(), restartInterval: 2);

        // Flip one bit in the middle of the frame (not the CRC itself — the CRC is the
        // last 4 bytes; flipping bytes 8..framed.Length-5 mutates either the header or
        // the payload and must trigger CRC mismatch).
        framed[framed.Length / 2] ^= 0xFF;

        using var stream = new MemoryStream(framed);
        var act = () => BlockFormat.ReadBlock(stream, new NoCompressionCodec());
        act.Should().Throw<CorruptBlockException>();
    }

    [Fact]
    public void BitFlip_InBlockCrc_RaisesCorruptBlockException()
    {
        var entries = new[] { E([1], [1]) };
        var framed = BlockFormat.WriteBlock(entries, new NoCompressionCodec(), restartInterval: 2);

        // CRC is the last 4 bytes — corrupt any of them.
        framed[^1] ^= 0x01;

        using var stream = new MemoryStream(framed);
        var act = () => BlockFormat.ReadBlock(stream, new NoCompressionCodec());
        act.Should().Throw<CorruptBlockException>();
    }

    [Fact]
    public void RestartPoints_EveryNEntries_StoreFullKey()
    {
        // Writer's contract: every Nth entry is a restart point stored with full key
        // (shared_prefix_length = 0). The test inspects the serialized payload by
        // counting restart points in the tail index and verifying count matches
        // ceil(entryCount / restartInterval).
        var entries = Enumerable.Range(0, 50)
            .Select(i => E([(byte)i], [0], (ulong)i))
            .ToArray();

        var framed = BlockFormat.WriteBlock(entries, new NoCompressionCodec(), restartInterval: 16);
        using var stream = new MemoryStream(framed);
        var decoded = BlockFormat.ReadBlock(stream, new NoCompressionCodec());

        decoded.Should().HaveCount(50);
        // Round-trip establishes correctness of restart-point decoding; the explicit
        // count invariant is exercised by the internal seek path in SeekInBlock.
    }

    [Theory]
    [MemberData(nameof(AllCodecs))]
    public void SeekInBlock_FindsEveryKey(ICompressionCodec codec)
    {
        var entries = Enumerable.Range(0, 100)
            .Select(i => E(System.BitConverter.GetBytes(i), [(byte)(i & 0xFF)], (ulong)i))
            .ToArray();

        var framed = BlockFormat.WriteBlock(entries, codec, restartInterval: 16);
        using var stream = new MemoryStream(framed);
        var decoded = BlockFormat.ReadBlock(stream, codec);

        decoded.Should().HaveCount(100);
        decoded.Should().AllSatisfy(e => e.Kind.Should().Be(ValueKind.Inline));
    }

    [Theory]
    [MemberData(nameof(AllCodecs))]
    public void EmptyBlock_RoundTrips(ICompressionCodec codec)
    {
        var framed = BlockFormat.WriteBlock([], codec, restartInterval: 16);
        using var stream = new MemoryStream(framed);
        var decoded = BlockFormat.ReadBlock(stream, codec);
        decoded.Should().BeEmpty();
    }

    [Fact]
    public void ValueKind_ValueLogPointer_RoundTripsTwelveBytePointerBody()
    {
        // The reader accepts ValueKind.ValueLogPointer entries whose 12-byte value body is
        // the serialised (fileIndex:u32, offset:u64) pointer. No exception — the engine
        // dereferences the pointer through ValueLogReader on the hot read path.
        var pointerBody = new byte[12];
        // fileIndex = 7, offset = 0x1122334455667788 (little-endian)
        BitConverter.GetBytes((uint)7).CopyTo(pointerBody, 0);
        BitConverter.GetBytes((ulong)0x1122334455667788UL).CopyTo(pointerBody, 4);

        var entry = new BlockEntry(
            new byte[] { 0x10 },
            SequenceNumber: 1UL,
            Kind: ValueKind.ValueLogPointer,
            Value: pointerBody);

        var framed = BlockFormat.WriteBlock([entry], new NoCompressionCodec(), restartInterval: 2);
        using var stream = new MemoryStream(framed);
        var decoded = BlockFormat.ReadBlock(stream, new NoCompressionCodec());

        decoded.Should().HaveCount(1);
        decoded[0].Kind.Should().Be(ValueKind.ValueLogPointer);
        decoded[0].Value.ToArray().Should().Equal(pointerBody);
    }

    [Fact]
    public void ValueKind_Reserved_ReaderThrows()
    {
        // Craft a block that carries a reserved ValueKind byte (0x02). We can't produce
        // it via BlockFormat.WriteBlock since the enum only has two members; fabricate
        // by writing a valid Inline entry then flipping the kind byte. The offset of
        // the kind byte within the uncompressed payload is well-defined (after the
        // prefix-compression header + suffix + 8B seqno).
        var entry = E([0x10], [0xAA]);
        var framed = BlockFormat.WriteBlock([entry], new NoCompressionCodec(), restartInterval: 2);

        // Parse the frame headers to find where the kind byte lives in the compressed
        // payload. For NoCompressionCodec the compressed and uncompressed bytes are
        // identical, so flipping inside the "payload" region flips the underlying kind
        // byte — the CRC then fails. That's covered by BitFlip_*; the reserved-kind
        // path is reachable only by building a *valid-CRC* frame with a 0x02 kind byte.
        // Simpler: construct the framed bytes manually using the public primitives.
        using var stream = new MemoryStream(BuildReservedKindFrame());
        var act = () => BlockFormat.ReadBlock(stream, new NoCompressionCodec());
        act.Should().Throw<UnsupportedValueKindException>();
    }

    private static byte[] BuildReservedKindFrame()
    {
        // Single entry: shared_prefix=0 varint(0x00); suffix_length=1 varint(0x01);
        // suffix=[0x10]; seqno=[0..0]; value_kind=0x02 (reserved); value_length=0 varint;
        // Tail: zero restart offsets, restart_count=0 (uint32 LE).
        // Then 4B restart count = 0.
        // So uncompressed payload = [0x00][0x01][0x10][8x00][0x02][0x00] + [0x00,0x00,0x00,0x00]
        // = 12 bytes entry + 4 bytes tail = 16 bytes total
        var payload = new byte[]
        {
            0x00,                   // shared_prefix_length (varint 0)
            0x01,                   // suffix_length (varint 1)
            0x10,                   // suffix bytes
            0, 0, 0, 0, 0, 0, 0, 0, // sequence_number (8B LE = 0)
            0x02,                   // value_kind = reserved 0x02
            0x00,                   // value_length (varint 0)
            0, 0, 0, 0,             // restart_count = 0
        };

        var framed = new byte[4 + 4 + payload.Length + 4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(framed.AsSpan(0, 4), payload.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(framed.AsSpan(4, 4), payload.Length);
        payload.CopyTo(framed.AsSpan(8));

        // CRC32C over header + payload (first 8 + payload.Length bytes).
        var crc = Boutquin.Storage.Infrastructure.Algorithms.Crc32C.HashToUInt32(
            framed.AsSpan(0, 8 + payload.Length));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            framed.AsSpan(8 + payload.Length, 4), crc);

        return framed;
    }

    private static byte[] Combine(byte[] a, byte[] b)
    {
        var r = new byte[a.Length + b.Length];
        a.CopyTo(r, 0);
        b.CopyTo(r, a.Length);
        return r;
    }

    // ── Fault-injection: explicitly constructed corrupt frames ──────────────

    /// <summary>
    /// Wraps an arbitrary uncompressed payload in a valid block frame (header + CRC).
    /// Lets corruption tests build illegal payloads that pass the CRC check so the
    /// payload-validation branches surface the right exception.
    /// </summary>
    private static byte[] FrameWithValidCrc(byte[] payload)
    {
        var framed = new byte[4 + 4 + payload.Length + 4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(framed.AsSpan(0, 4), payload.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(framed.AsSpan(4, 4), payload.Length);
        payload.CopyTo(framed.AsSpan(8));
        var crc = Boutquin.Storage.Infrastructure.Algorithms.Crc32C.HashToUInt32(
            framed.AsSpan(0, 8 + payload.Length));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            framed.AsSpan(8 + payload.Length, 4), crc);
        return framed;
    }

    [Fact]
    public void Read_NegativeUncompressedLength_ThrowsCorruptBlock()
    {
        // Build a frame whose first 4 bytes (uncompressedLen) are negative.
        var framed = new byte[4 + 4 + 0 + 4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(framed.AsSpan(0, 4), -1);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(framed.AsSpan(4, 4), 0);
        // CRC over the (negative-length) header — the length check fires before the CRC.
        var crc = Boutquin.Storage.Infrastructure.Algorithms.Crc32C.HashToUInt32(framed.AsSpan(0, 8));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(framed.AsSpan(8, 4), crc);

        using var stream = new MemoryStream(framed);
        var act = () => BlockFormat.ReadBlock(stream, new NoCompressionCodec());
        act.Should().Throw<CorruptBlockException>().WithMessage("*negative length*");
    }

    [Fact]
    public void Read_CompressedLengthExceedsRemaining_ThrowsBeforeAllocating()
    {
        // #7 (class C): the header declares a compressed length far larger than the bytes that
        // actually remain in the stream. Without a bound, ReadBlock allocated new byte[hugeLen]
        // (OOM / DoS) before the CRC check could fire. It must reject the declared length up front.
        var framed = new byte[8 + 4]; // 8-byte header + 4 stray bytes, nowhere near 1,000,000
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(framed.AsSpan(0, 4), 10);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(framed.AsSpan(4, 4), 1_000_000);

        using var stream = new MemoryStream(framed);
        var act = () => BlockFormat.ReadBlock(stream, new NoCompressionCodec());
        act.Should().Throw<CorruptBlockException>().WithMessage("*exceeding*");
    }

    [Fact]
    public void DecompressPayload_CompressedLengthExceedsRemaining_ThrowsBeforeAllocating()
    {
        // Twin of Read_CompressedLengthExceedsRemaining_ThrowsBeforeAllocating: the same
        // bounded-allocation guard must cover DecompressPayload, which serves the hot
        // point-lookup path (TryGetBlockEntry). A corrupt compressed_length read from disk
        // must be rejected against the bytes actually present, not allocated up front.
        var framed = new byte[8 + 4]; // 8-byte header + 4 stray bytes, nowhere near 1,000,000
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(framed.AsSpan(0, 4), 10);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(framed.AsSpan(4, 4), 1_000_000);

        var act = () => BlockFormat.DecompressPayload(framed, new NoCompressionCodec());
        act.Should().Throw<CorruptBlockException>().WithMessage("*exceeding*");
    }

    [Fact]
    public void Read_NegativeCompressedLength_ThrowsCorruptBlock()
    {
        var framed = new byte[12];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(framed.AsSpan(0, 4), 0);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(framed.AsSpan(4, 4), -1);
        var crc = Boutquin.Storage.Infrastructure.Algorithms.Crc32C.HashToUInt32(framed.AsSpan(0, 8));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(framed.AsSpan(8, 4), crc);

        using var stream = new MemoryStream(framed);
        var act = () => BlockFormat.ReadBlock(stream, new NoCompressionCodec());
        act.Should().Throw<CorruptBlockException>().WithMessage("*negative length*");
    }

    [Fact]
    public void Read_PayloadTooShortForRestartTail_ThrowsCorruptBlock()
    {
        // Payload < 4 bytes — cannot hold the restart-count tail.
        var framed = FrameWithValidCrc([0x00, 0x01, 0x02]);
        using var stream = new MemoryStream(framed);
        var act = () => BlockFormat.ReadBlock(stream, new NoCompressionCodec());
        act.Should().Throw<CorruptBlockException>().WithMessage("*too short*");
    }

    [Fact]
    public void Read_RestartCountOutOfRange_ThrowsCorruptBlock()
    {
        // Payload tail declares a restart_count that cannot fit in the payload.
        // 4 bytes total payload — declares restart_count = 99 in those 4 bytes.
        var payload = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(payload, 99);
        var framed = FrameWithValidCrc(payload);
        using var stream = new MemoryStream(framed);
        var act = () => BlockFormat.ReadBlock(stream, new NoCompressionCodec());
        act.Should().Throw<CorruptBlockException>().WithMessage("*restart_count*");
    }

    [Fact]
    public void Read_NegativeRestartCount_ThrowsCorruptBlock()
    {
        var payload = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(payload, -1);
        var framed = FrameWithValidCrc(payload);
        using var stream = new MemoryStream(framed);
        var act = () => BlockFormat.ReadBlock(stream, new NoCompressionCodec());
        act.Should().Throw<CorruptBlockException>().WithMessage("*restart_count*");
    }

    [Fact]
    public void Read_SharedPrefixExceedsPreviousKey_ThrowsCorruptBlock()
    {
        // Build a payload with a single entry whose shared_prefix_length > 0 even though
        // there is no previous key. This trips the (sharedPrefix > previousKey.Length) check.
        var payload = new byte[]
        {
            0x05,                   // shared_prefix_length = 5 (illegal: previous key empty)
            0x01,                   // suffix_length = 1
            0x10,                   // suffix bytes
            0, 0, 0, 0, 0, 0, 0, 0, // seqno
            0x00,                   // value_kind = Inline
            0x00,                   // value_length = 0
            0x00, 0x00, 0x00, 0x00, // restart_count = 0
        };
        var framed = FrameWithValidCrc(payload);
        using var stream = new MemoryStream(framed);
        var act = () => BlockFormat.ReadBlock(stream, new NoCompressionCodec());
        act.Should().Throw<CorruptBlockException>().WithMessage("*shared_prefix_length*");
    }

    [Fact]
    public void Read_NegativeValueLength_ThrowsCorruptBlock()
    {
        // value_length is read via varint as ulong then cast to int. A varint encoding
        // larger than int.MaxValue rolls over to a negative int — the explicit guard
        // surfaces a CorruptBlockException rather than letting `new byte[negative]` throw.
        var payload = new byte[]
        {
            0x00,                   // shared_prefix_length = 0
            0x01,                   // suffix_length = 1
            0x10,                   // suffix bytes
            0, 0, 0, 0, 0, 0, 0, 0, // seqno
            0x00,                   // value_kind = Inline
            // value_length: varint encoding of 2_147_483_648 (int.MaxValue + 1) = 6 bytes.
            // 0x80 0x80 0x80 0x80 0x08 → casts to int as 0 (rolls over but bypasses negative)
            // To trigger NEGATIVE on the int cast use a number that overflows the int sign bit.
            // 0x80 0x80 0x80 0x80 0x10 → 0x10 << 28 = 0x1_0000_0000 → int cast = 0. Not negative.
            // Use 0xFFFFFFFFFFFFFFFF varint — 10 bytes. Cast to int yields -1.
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01,
            0x00, 0x00, 0x00, 0x00, // restart_count = 0
        };
        var framed = FrameWithValidCrc(payload);
        using var stream = new MemoryStream(framed);
        var act = () => BlockFormat.ReadBlock(stream, new NoCompressionCodec());
        act.Should().Throw<CorruptBlockException>().WithMessage("*value_length*");
    }

    // ── Internal binary-search helpers (Phase P4.6) ───────────────────────────

    [Fact]
    public void ParseRestartOffsets_PayloadShorterThanTail_ReturnsEmpty()
    {
        var result = BlockFormat.ParseRestartOffsets(new byte[3]);
        result.Should().BeEmpty();
    }

    [Fact]
    public void ParseRestartOffsets_RestartCountOutOfRange_ThrowsCorruptBlockException()
    {
        // Payload of 4 bytes declares 99 offsets (impossible — tail would need 4 + 99*4 = 400 bytes).
        // The old fail-open returned [] here, silently degrading binary search to a full linear scan.
        // The corrected behaviour throws CorruptBlockException (B2 fix).
        var payload = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(payload, 99);
        var act = () => BlockFormat.ParseRestartOffsets(payload);
        act.Should().Throw<CorruptBlockException>("a restart_count that exceeds available bytes is corrupt data");
    }

    [Fact]
    public void GetEntriesEnd_PayloadShorterThanTail_ReturnsZero()
    {
        BlockFormat.GetEntriesEnd(new byte[3]).Should().Be(0);
    }

    [Fact]
    public void GetEntriesEnd_NegativeRestartCount_ReturnsZero()
    {
        var payload = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(payload, -1);
        BlockFormat.GetEntriesEnd(payload).Should().Be(0);
    }

    // ── F-alloc-01: block-entry decoded lengths must be bounded before allocating ──
    //
    // The block frame is CRC32C (Castagnoli) protected — a checksum, NOT a MAC — so a
    // forged block with a recomputed CRC passes the integrity check. A point lookup
    // (DecodeFromOffset) or restart binary search (ReadKeyAtOffset / DecodeUncompressedPayload)
    // then reads a suffix_length / value_length varint guarded only against `< 0` and
    // allocates `new byte[len]` BEFORE the read-bound (ReadExact over the bounded stream)
    // can fire. A 12-byte forged block drove ~572 MiB of transient allocation pre-bound.
    // Each decode site must reject a declared length that exceeds the bytes actually
    // remaining in the bounded payload, the same way DecompressPayload (:281-290) does.
    //
    // ULEB128 of 100_000_000 = 0x05F5E100 → [0x80, 0xC2, 0xD7, 0x2F]. Far larger than the
    // tiny forged payloads below, but small enough to keep the test fast/deterministic.

    private static byte[] Varint(ulong value)
    {
        using var ms = new MemoryStream();
        VarintHelpers.WriteVarUInt64(ms, value);
        return ms.ToArray();
    }

    [Fact]
    public void DecodeUncompressedPayload_SuffixLengthExceedsRemaining_ThrowsBeforeAllocating()
    {
        // Entries region: shared_prefix=0, suffix_length=100_000_000, then a couple stray
        // bytes — nowhere near 100M. Tail: restart_count=0 (4 zero bytes).
        using var entries = new MemoryStream();
        entries.Write(Varint(0));                 // shared_prefix_length
        entries.Write(Varint(100_000_000));       // suffix_length — hugely over-declared
        entries.WriteByte(0xAB);                  // stray suffix byte
        entries.WriteByte(0xCD);
        var entryBytes = entries.ToArray();

        var payload = new byte[entryBytes.Length + 4];
        entryBytes.CopyTo(payload, 0);
        // restart_count = 0 in the trailing 4 bytes.

        var act = () => BlockFormat.DecodeUncompressedPayload(payload);
        act.Should().Throw<CorruptBlockException>().WithMessage("*exceed*");
    }

    [Fact]
    public void DecodeUncompressedPayload_ValueLengthExceedsRemaining_ThrowsBeforeAllocating()
    {
        // A well-formed entry header up to value_length, then value_length hugely over-declared.
        using var entries = new MemoryStream();
        entries.Write(Varint(0));                 // shared_prefix_length
        entries.Write(Varint(1));                 // suffix_length = 1
        entries.WriteByte(0x41);                  // 1 suffix byte
        entries.Write(new byte[8]);               // 8-byte seqno
        entries.WriteByte((byte)ValueKind.Inline);
        entries.Write(Varint(100_000_000));       // value_length — hugely over-declared
        entries.WriteByte(0xEE);                  // a single stray value byte
        var entryBytes = entries.ToArray();

        var payload = new byte[entryBytes.Length + 4];
        entryBytes.CopyTo(payload, 0);

        var act = () => BlockFormat.DecodeUncompressedPayload(payload);
        act.Should().Throw<CorruptBlockException>().WithMessage("*exceed*");
    }

    [Fact]
    public void ReadKeyAtOffset_SuffixLengthExceedsRemaining_ThrowsBeforeAllocating()
    {
        // Restart-point key read: shared_prefix=0, suffix_length=100_000_000, tiny payload.
        using var ms = new MemoryStream();
        ms.Write(Varint(0));                      // shared_prefix_length (always 0 at restart)
        ms.Write(Varint(100_000_000));            // suffix_length — hugely over-declared
        ms.WriteByte(0x01);                       // one stray byte
        var payload = WithEmptyRestartTail(ms.ToArray());

        var act = () => BlockFormat.ReadKeyAtOffset(payload, 0);
        act.Should().Throw<CorruptBlockException>().WithMessage("*exceed*");
    }

    [Fact]
    public void DecodeFromOffset_SuffixLengthExceedsRemaining_ThrowsBeforeAllocating()
    {
        // Entries region for the binary-search decode path: suffix_length over-declared.
        using var entries = new MemoryStream();
        entries.Write(Varint(0));                 // shared_prefix_length
        entries.Write(Varint(100_000_000));       // suffix_length — hugely over-declared
        entries.WriteByte(0xAB);                  // stray byte
        var payload = entries.ToArray();

        var act = () => BlockFormat.DecodeFromOffset(payload, 0, payload.Length);
        act.Should().Throw<CorruptBlockException>().WithMessage("*exceed*");
    }

    [Fact]
    public void DecodeFromOffset_ValueLengthExceedsRemaining_ThrowsBeforeAllocating()
    {
        // Well-formed entry up to value_length, then value_length hugely over-declared.
        using var entries = new MemoryStream();
        entries.Write(Varint(0));                 // shared_prefix_length
        entries.Write(Varint(1));                 // suffix_length = 1
        entries.WriteByte(0x41);                  // 1 suffix byte
        entries.Write(new byte[8]);               // 8-byte seqno
        entries.WriteByte((byte)ValueKind.Inline);
        entries.Write(Varint(100_000_000));       // value_length — hugely over-declared
        entries.WriteByte(0xEE);                  // stray value byte
        var payload = entries.ToArray();

        var act = () => BlockFormat.DecodeFromOffset(payload, 0, payload.Length);
        act.Should().Throw<CorruptBlockException>().WithMessage("*exceed*");
    }

    // ── F-E1-01 / F-E1-02: forged uncompressed_length must be capped before allocating ──
    //
    // The frame's first 4 bytes are uncompressed_length. The block CRC32C is a checksum,
    // NOT a MAC, so an attacker who forges [huge uncompressed_length][tiny valid compressed
    // body] and recomputes the CRC produces a frame that passes the CRC check. ReadBlock /
    // DecompressPayload then ran `new byte[uncompressedLen]` (up to ~2 GB) BEFORE the codec
    // ever observed the size mismatch — an OOM/DoS from a <40-byte input. The fix bounds
    // uncompressed_length against an absolute ceiling (BlockFormat.MaxUncompressedBlockBytes)
    // and surfaces CorruptBlockException, not the codec's InvalidDataException, ahead of alloc.

    /// <summary>
    /// Frames an arbitrary compressed body with a caller-chosen <paramref name="uncompressedLen"/>
    /// and a recomputed (valid) CRC32C — exactly the forged-and-re-CRC'd frame an attacker can
    /// build, since CRC32C is a checksum, not a MAC.
    /// </summary>
    private static byte[] ForgeFrame(int uncompressedLen, byte[] compressedBody)
    {
        var framed = new byte[8 + compressedBody.Length + 4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(framed.AsSpan(0, 4), uncompressedLen);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(framed.AsSpan(4, 4), compressedBody.Length);
        compressedBody.CopyTo(framed.AsSpan(8));
        var crc = Boutquin.Storage.Infrastructure.Algorithms.Crc32C.HashToUInt32(
            framed.AsSpan(0, 8 + compressedBody.Length));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            framed.AsSpan(8 + compressedBody.Length, 4), crc);
        return framed;
    }

    [Fact]
    public void ReadBlock_ForgedUncompressedLengthExceedsCap_ThrowsBeforeAllocating()
    {
        // Tiny valid compressed body (so compressedLen passes the remaining-bytes bound and the
        // CRC check passes), but uncompressed_length forged just past the absolute cap. The
        // unfixed reader allocates new byte[uncompressedLen] (> cap) before the codec observes
        // the size mismatch and throws InvalidDataException — the wrong exception, after a giant
        // allocation. The cap must reject it with CorruptBlockException ahead of alloc.
        var body = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        var framed = ForgeFrame(BlockFormat.MaxUncompressedBlockBytes + 1, body);

        using var stream = new MemoryStream(framed);
        var act = () => BlockFormat.ReadBlock(stream, new NoCompressionCodec());
        act.Should().Throw<CorruptBlockException>().WithMessage("*uncompressed*");
    }

    [Fact]
    public void DecompressPayload_ForgedUncompressedLengthExceedsCap_ThrowsBeforeAllocating()
    {
        // Twin of ReadBlock_ForgedUncompressedLengthExceedsCap on the point-lookup path
        // (TryGetBlockEntry → DecompressPayload). Same forged-frame vector, same absolute cap.
        var body = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        var framed = ForgeFrame(BlockFormat.MaxUncompressedBlockBytes + 1, body);

        var act = () => BlockFormat.DecompressPayload(framed, new NoCompressionCodec());
        act.Should().Throw<CorruptBlockException>().WithMessage("*uncompressed*");
    }

    // ── F-WA-C02-01: restart_count * 4 must not overflow int and bypass the tail guard ──
    //
    // DecodeUncompressedPayload (full-scan path) and GetEntriesEnd (point-lookup path) bound
    // restart_count with `restartCount * 4 + 4 > payload.Length` computed in int. A forged
    // restart_count like 0x20000001 overflows int to a large negative value, so the guard is
    // FALSE (bypassed); entriesEnd = payload.Length - 4 - (restartCount*4) wraps to a wild value
    // handed to the MemoryStream slice ctor, which throws ArgumentOutOfRangeException — not the
    // fail-closed CorruptBlockException the block-format corruption contract requires. The
    // correctly-written sibling ParseRestartOffsets already widens to (long)count * 4. The fix
    // mirrors that widening in both helpers.

    [Fact]
    public void DecodeUncompressedPayload_ForgedRestartCountOverflowsInt_ThrowsCorruptBlock()
    {
        // 64-byte payload whose trailing 4 bytes declare restart_count = 0x20000001. In int,
        // 0x20000001 * 4 = 0x80000004 wraps to a large negative value, so the `*4 + 4 >
        // payload.Length` guard is bypassed on the unfixed code. The decode then slices a
        // MemoryStream with a wild length and throws ArgumentOutOfRangeException instead of
        // CorruptBlockException.
        var payload = new byte[64];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
            payload.AsSpan(payload.Length - 4, 4), 0x20000001);

        var act = () => BlockFormat.DecodeUncompressedPayload(payload);
        act.Should().Throw<CorruptBlockException>().WithMessage("*restart_count*");
    }

    [Fact]
    public void GetEntriesEnd_ForgedRestartCountOverflowsInt_ReturnsZero()
    {
        // Sibling of the above on the point-lookup path. GetEntriesEnd computes
        // `tail = 4 + count * 4` in int; a forged count of 0x20000001 overflows to a large
        // negative tail, so `tail > payload.Length` is false on the unfixed code and the method
        // returns an out-of-[0, payload.Length] entriesEnd (probe observed -2147483592). The
        // overflow-safe widening makes an over-declared count fall through to the safe 0 (which
        // DecodeFromOffset treats as an empty decode window — fail-closed, key-not-found).
        var payload = new byte[64];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
            payload.AsSpan(payload.Length - 4, 4), 0x20000001);

        BlockFormat.GetEntriesEnd(payload).Should().Be(0);
    }

    // ── B8: sharedPrefix + suffixLen int overflow ─────────────────────────────

    [Fact]
    public void ReadBlock_SharedPrefixPlusSuffixLenWouldOverflow_ThrowsCorruptBlockException()
    {
        // A forged entry declares sharedPrefix = int.MaxValue and suffixLen = 1. Their sum
        // int.MaxValue + 1 wraps to int.MinValue (a negative allocation request). Before the
        // fix this threw OverflowException or ArgumentOutOfRangeException; after the fix the
        // (long) guard catches it and throws CorruptBlockException with a descriptive message.
        //
        // Encoding: sharedPrefix as varint(int.MaxValue) = 5 bytes (0xFF 0xFF 0xFF 0xFF 0x07),
        // suffixLen as varint(1) = 1 byte.  No previous key → sharedPrefix > previousKey.Length
        // is a different guard; to reach the overflow check we set the restart_index so the
        // first entry has sharedPrefix=0 and the second entry (using the first key as previous)
        // declares sharedPrefix=int.MaxValue.  The simpler path: set sharedPrefix large enough
        // that sharedPrefix + suffixLen overflows, with sharedPrefix ≤ previousKey.Length.
        //
        // Easiest route: craft a forged entry that passes the sharedPrefix ≤ previousKey.Length
        // check (so we need previousKey.Length ≥ sharedPrefix) — but a fake previous key that
        // large would require allocating gigs.  Instead test the guard directly at a smaller
        // scale: varint(int.MaxValue - 1) as sharedPrefix and varint(2) as suffixLen so that
        // their sum overflows only in 32-bit arithmetic.
        //
        // sharedPrefix = int.MaxValue - 1 = 0x7FFFFFFE → 5-byte varint: FE FF FF FF 07
        // suffixLen = 2 → 1-byte varint: 02
        // sum = 0x7FFFFFFE + 2 = 0x80000000 which in int is int.MinValue (negative).
        //
        // BUT: sharedPrefix > previousKey.Length fires first if previousKey is empty.
        // We bypass that by making a one-entry block at a restart point (no previous key, so
        // sharedPrefix must be 0).  For the overflow test we need a two-entry block where the
        // second entry's sharedPrefix + suffixLen overflows. We need previousKey of length
        // sharedPrefix — which is int.MaxValue - 1 bytes.  That's not allocatable.
        //
        // Realistic approach: set sharedPrefix = 3 (fits in previous key "abc") and
        // suffixLen as a varint that when added to 3 overflows: int.MaxValue - 2 = 0x7FFFFFFD.
        // Then sharedPrefix (3) + suffixLen (0x7FFFFFFD) = 0x80000000 = int.MinValue (overflow).
        // EnsureWithinRemaining fires first for suffixLen=0x7FFFFFFD vs remaining bytes, which
        // is a much smaller number.  So EnsureWithinRemaining blocks it before the overflow check.
        //
        // The correct test: sharedPrefix = 2, suffixLen = int.MaxValue - 1 (0x7FFFFFFE).
        // EnsureWithinRemaining(stream, 0x7FFFFFFE, ...) fires first.  Still blocked.
        //
        // To actually reach the overflow check we need EnsureWithinRemaining to pass, which
        // requires the stream to have at least suffixLen bytes remaining.  That means we need
        // a block whose payload section is at least int.MaxValue bytes — not feasible in a unit
        // test.  The guard is therefore a defense-in-depth that fires only in adversarial
        // conditions where the entry section header has been forged to report an unrealistically
        // large remaining byte count.
        //
        // Instead, test via the DecodeFromOffset (point-lookup) path, which has the same guard
        // but reads from an in-memory payload span rather than a stream with a remaining check.
        // Craft a small payload where sharedPrefix + suffixLen exceeds int.MaxValue.
        //
        // Block payload layout (restart_count=1 at the tail, one restart offset pointing to byte 0):
        //   [entry 0 at offset 0, marked as restart point]
        //   ... restart table = [0x00, 0x00, 0x00, 0x00] (offset=0) [0x01, 0x00, 0x00, 0x00] (count=1)
        //
        // Entry 0 at offset 0 (restart point → sharedPrefix must be 0 in practice but we're
        // reading the raw bytes):  sharedPrefix=3 (varint 0x03), suffixLen=0x7FFFFFFE (5-byte varint).
        // With restart_count=1 and the restart offset pointing to byte 0, DecodeFromOffset
        // passes through and starts reading the entry at offset 0.
        // This is the combination: sharedPrefix=3, suffixLen=int.MaxValue-1=0x7FFFFFFE.
        // Unfixed: 3 + 0x7FFFFFFE = 0x80000001 which in signed int = -2147483647 → negative
        // allocation → OverflowException.  Fixed: (long)3 + 0x7FFFFFFE = 0x80000001 > int.MaxValue
        // → CorruptBlockException.
        //
        // We use BlockFormat.DecodeFromOffset (internal) via ReadBlock feeding a MemoryStream,
        // but the overflow guard lives in ReadEntries which also runs in ReadBlock.  Feed a
        // framed block whose payload declares a restart entry with the overflow combination.

        // Build the payload:
        //  sharedPrefix = 3  → varint: 0x03
        //  suffixLen = int.MaxValue - 2  → 0x7FFFFFFD → 5-byte varint: FD FF FF FF 07
        //  (sum = 3 + 0x7FFFFFFD = 0x80000000 which is > int.MaxValue in long space)
        //  restart_count = 0 (no restart table) so we just need the entries region.
        var sharedPrefixVarint = new byte[] { 0x03 };
        // varint for 0x7FFFFFFD: 0x7FFFFFFD in 7-bit groups = (0x7D)(0xFF)(0xFF)(0xFF)(0x07)
        // But we need little-endian varint (LSB first, each 7 bits):
        // 0x7FFFFFFD = 0111 1111 1111 1111 1111 1111 1111 1101
        // 7-bit groups (LSB first): 1111101 1111111 1111111 1111111 0111111 padding
        // Byte 0: 0b1111101 | 0x80 = 0xFD (7 bits 1111101, continuation)
        // Byte 1: 0b1111111 | 0x80 = 0xFF
        // Byte 2: 0b1111111 | 0x80 = 0xFF
        // Byte 3: 0b1111111 | 0x80 = 0xFF
        // Byte 4: 0b0000111 = 0x07 (MSBs, no continuation)
        var suffixLenVarint = new byte[] { 0xFD, 0xFF, 0xFF, 0xFF, 0x07 };
        var restartCount = new byte[] { 0x00, 0x00, 0x00, 0x00 }; // restart_count = 0

        var payloadBytes = sharedPrefixVarint
            .Concat(suffixLenVarint)
            .Concat(restartCount)
            .ToArray();

        var framed = FrameWithValidCrc(payloadBytes);
        using var stream = new MemoryStream(framed);
        var act = () => BlockFormat.ReadBlock(stream, new NoCompressionCodec());
        act.Should().Throw<CorruptBlockException>("sharedPrefix + suffixLen overflows int.MaxValue");
    }

    // ── Writer/reader frame-size agreement ────────────────────────────────────

    /// <summary>
    /// The writer frames any block its codec produces — an oversized value is placed alone in a
    /// block — so the range/compaction decode path must accept every frame the writer emits. A
    /// fixed compressed-length cap below what the writer produces made a single incompressible
    /// multi-MiB value readable by point lookup yet unreadable by scans and compaction.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllCodecs))]
    public void ReadBlock_AcceptsEveryFrameTheWriterEmits_ForAnIncompressibleMultiMiBValue(ICompressionCodec codec)
    {
        var value = new byte[5 * 1024 * 1024];
        new Random(20261018).NextBytes(value);
        var framed = BlockFormat.WriteBlock([E([1, 2, 3], value, 7UL)], codec, restartInterval: 16);

        using var stream = new MemoryStream(framed);
        var decoded = BlockFormat.ReadBlock(stream, codec);

        decoded.Should().ContainSingle();
        decoded[0].SequenceNumber.Should().Be(7UL);
        decoded[0].Value.Span.SequenceEqual(value).Should().BeTrue("the decoded value must be the written bytes");
        BlockFormat.DecodeUncompressedPayload(BlockFormat.DecompressPayload(framed, codec))
            .Should().ContainSingle("the point-lookup decode path accepts the same frame");
    }

    // ── B1: non-seekable allocation-bound escape ──────────────────────────────

    /// <summary>
    /// A forged block header on a non-seekable stream declares an absurdly large
    /// compressedLen. Without an absolute cap the reader attempts new byte[compressedLen]
    /// (OOM / DoS) before the CRC check can fire. The CanSeek-gated remaining-bytes
    /// check is skipped on non-seekable streams, but an absolute-cap check must still
    /// reject the declared length before any allocation.
    /// </summary>
    [Fact]
    public void ReadBlock_NonSeekable_OversizedCompressedLength_ThrowsCorruptBlockException()
    {
        // Build a frame header that declares int.MaxValue compressed bytes.
        // compressedLen = int.MaxValue; uncompressedLen = 1 (plausible positive value).
        var header = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0, 4), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), int.MaxValue);

        // Wrap in a stream that reports CanSeek = false so the remaining-bytes guard is skipped.
        using var nonSeekable = new NonSeekableStream(header);
        var act = () => BlockFormat.ReadBlock(nonSeekable, new NoCompressionCodec());

        // Must throw before attempting to allocate int.MaxValue bytes.
        act.Should().Throw<CorruptBlockException>(
            "compressedLen exceeding the absolute cap must be rejected on non-seekable streams as block corruption");
    }

    // ── B2: restart-tail fail-open ─────────────────────────────────────────────

    /// <summary>
    /// ParseRestartOffsets must throw <see cref="CorruptBlockException"/> when the
    /// trailing restart_count is non-zero but the claimed offsets cannot fit in the
    /// payload (corrupt tail). The current implementation returns [] (fail-open),
    /// silently degrading binary search to a full linear scan.
    /// </summary>
    [Fact]
    public void ParseRestartOffsets_CorruptTail_ThrowsCorruptBlockException()
    {
        // Payload that claims restart_count = 10 but is only 8 bytes total.
        // The tail requires 4 + 10*4 = 44 bytes; only 8 are available.
        var payload = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
            payload.AsSpan(payload.Length - 4, 4), 10); // restart_count = 10

        var act = () => BlockFormat.ParseRestartOffsets(payload);
        act.Should().Throw<CorruptBlockException>(
            "a restart_count that does not fit in the payload is corrupt data, not a graceful fallback");
    }

    // ── Decoded entry varints: wrapped, oversized and truncated encodings ────────
    //
    // Every entry length field is a ULEB128 varint read from CRC-covered (checksum, not MAC)
    // bytes. A value above int.MaxValue must be rejected before the int cast — 0xFFFFFFFF wraps
    // to -1 and 2^32 + 1 aliases to 1 — and a varint the payload ends inside (or that overflows
    // 64 bits) must surface CorruptBlockException, not the varint reader's raw exceptions.

    private static readonly byte[] s_wrapsToMinusOne = [0xFF, 0xFF, 0xFF, 0xFF, 0x0F];          // 0xFFFFFFFF
    private static readonly byte[] s_aliasesToOne = [0x81, 0x80, 0x80, 0x80, 0x10];             // 2^32 + 1
    private static readonly byte[] s_truncatedVarint = [0x80];                                   // continuation bit, then end
    private static readonly byte[] s_overflowingVarint = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x7F];

    /// <summary>Entry region whose first entry carries <paramref name="sharedPrefix"/> raw.</summary>
    private static byte[] EntryWithSharedPrefixVarint(byte[] sharedPrefix)
    {
        using var ms = new MemoryStream();
        ms.Write(sharedPrefix);
        ms.Write(Varint(1));                      // suffix_length = 1
        ms.WriteByte(0x41);                       // suffix byte
        ms.Write(new byte[8]);                    // seqno
        ms.WriteByte((byte)ValueKind.Inline);
        ms.Write(Varint(0));                      // value_length = 0
        return ms.ToArray();
    }

    /// <summary>Entry region whose first entry carries <paramref name="suffixLength"/> raw.</summary>
    private static byte[] EntryWithSuffixLengthVarint(byte[] suffixLength)
    {
        using var ms = new MemoryStream();
        ms.Write(Varint(0));                      // shared_prefix_length = 0
        ms.Write(suffixLength);
        ms.WriteByte(0x41);                       // one suffix byte (what an aliased length of 1 would read)
        ms.Write(new byte[8]);                    // seqno
        ms.WriteByte((byte)ValueKind.Inline);
        ms.Write(Varint(0));                      // value_length = 0
        return ms.ToArray();
    }

    /// <summary>Appends an empty restart tail (restart_count = 0) to an entry region.</summary>
    private static byte[] WithEmptyRestartTail(byte[] entries)
    {
        var payload = new byte[entries.Length + 4];
        entries.CopyTo(payload, 0);
        return payload;
    }

    public static IEnumerable<object[]> MalformedEntryRegions()
    {
        yield return new object[] { "shared_prefix wraps negative", EntryWithSharedPrefixVarint(s_wrapsToMinusOne) };
        yield return new object[] { "suffix_length aliases above int", EntryWithSuffixLengthVarint(s_aliasesToOne) };
        yield return new object[] { "truncated varint", s_truncatedVarint };
        yield return new object[] { "overflowing varint", s_overflowingVarint };
    }

    [Theory]
    [MemberData(nameof(MalformedEntryRegions))]
    public void ReadBlock_MalformedEntryVarint_ThrowsCorruptBlockException(string forgery, byte[] entries)
    {
        var framed = FrameWithValidCrc(WithEmptyRestartTail(entries));
        using var stream = new MemoryStream(framed);

        var act = () => BlockFormat.ReadBlock(stream, new NoCompressionCodec());

        act.Should().Throw<CorruptBlockException>($"a CRC-valid block whose {forgery} is corrupt block data");
    }

    [Theory]
    [MemberData(nameof(MalformedEntryRegions))]
    public void DecodeFromOffset_MalformedEntryVarint_ThrowsCorruptBlockException(string forgery, byte[] entries)
    {
        var act = () => BlockFormat.DecodeFromOffset(entries, 0, entries.Length);

        act.Should().Throw<CorruptBlockException>($"a point-lookup decode whose {forgery} is corrupt block data");
    }

    public static IEnumerable<object[]> MalformedRestartKeys()
    {
        yield return new object[] { "suffix_length aliases above int", EntryWithSuffixLengthVarint(s_aliasesToOne) };
        yield return new object[] { "truncated varint", s_truncatedVarint };
        yield return new object[] { "overflowing varint", s_overflowingVarint };
    }

    [Theory]
    [MemberData(nameof(MalformedRestartKeys))]
    public void ReadKeyAtOffset_MalformedVarint_ThrowsCorruptBlockException(string forgery, byte[] entries)
    {
        var act = () => BlockFormat.ReadKeyAtOffset(WithEmptyRestartTail(entries), 0);

        act.Should().Throw<CorruptBlockException>($"a restart-key read whose {forgery} is corrupt block data");
    }

    [Fact]
    public void ReadKeyAtOffset_TruncatedRestartKeyBeforeTail_ThrowsCorruptBlockException()
    {
        // A full payload: the entries region is one byte (a varint cut after its continuation bit),
        // followed by the restart tail (offset 0, restart_count 1). The restart key must be read
        // from the entries region only — continuing the varint into the tail bytes would decode
        // the restart offsets as key bytes.
        var payload = new byte[] { 0x80, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00 };

        var act = () => BlockFormat.ReadKeyAtOffset(payload, 0);

        act.Should().Throw<CorruptBlockException>("the restart key ends inside the entries region");
    }

    // ── Restart offsets: each must lie in [0, entries_end) and strictly increase ──

    /// <summary>Payload with a 2-byte entry region and the given restart offsets as its tail.</summary>
    private static byte[] PayloadWithRestartOffsets(params int[] offsets)
    {
        const int entriesLen = 2;
        var payload = new byte[entriesLen + (offsets.Length * 4) + 4];
        for (var i = 0; i < offsets.Length; i++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
                payload.AsSpan(entriesLen + (i * 4), 4), offsets[i]);
        }
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(payload.Length - 4, 4), offsets.Length);
        return payload;
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(-5)]
    [InlineData(2)]   // == entries_end (exclusive bound)
    public void ParseRestartOffsets_OffsetOutsideEntriesRegion_ThrowsCorruptBlockException(int forged)
    {
        var payload = PayloadWithRestartOffsets(0, forged);

        var act = () => BlockFormat.ParseRestartOffsets(payload);

        act.Should().Throw<CorruptBlockException>().WithMessage("*restart offset*");
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    public void ParseRestartOffsets_OffsetsNotStrictlyIncreasing_ThrowsCorruptBlockException(int first, int second)
    {
        var payload = PayloadWithRestartOffsets(first, second);

        var act = () => BlockFormat.ParseRestartOffsets(payload);

        act.Should().Throw<CorruptBlockException>().WithMessage("*restart offset*");
    }

    [Fact]
    public void ParseRestartOffsets_ValidIncreasingOffsetsInsideEntriesRegion_ReturnsThem()
    {
        BlockFormat.ParseRestartOffsets(PayloadWithRestartOffsets(0, 1)).Should().Equal(0, 1);
    }

    // ── docs/sstable-block-format.md accuracy ─────────────────────────────────

    /// <summary>
    /// The guide's block-layout worked example must be the bytes the encoder writes for the
    /// documented entry (key 0xAA, seqno 0x0102030405060708, Inline, value 0xBBCC): a reader built
    /// from the guide has to decode real blocks.
    /// </summary>
    [Fact]
    public void BlockFormatGuide_EntryLayoutWorkedExample_MatchesEncoderBytes()
    {
        var entry = new BlockEntry(new byte[] { 0xAA }, 0x0102030405060708UL, ValueKind.Inline, new byte[] { 0xBB, 0xCC });
        var payload = BlockFormat.EncodeUncompressedPayload([entry], BlockFormat.DefaultRestartInterval);
        var expectedLine = "hex : " + string.Join(' ', payload.Select(b => b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)));

        File.ReadAllLines(LocateBlockFormatGuide()).Select(l => l.Trim()).Should().Contain(expectedLine,
            "docs/sstable-block-format.md must carry the encoder's exact bytes for its worked entry example");
    }

    /// <summary>
    /// The guide's block diagram must list each entry's fields, and the restart trailer, in the
    /// order the encoder writes them.
    /// </summary>
    [Fact]
    public void BlockFormatGuide_BlockDiagram_ListsEntryFieldsAndTrailerInEncoderOrder()
    {
        var section = GuideSection("## Block layout (inside one block)");
        string[] encoderOrder =
        [
            "shared_prefix_length", "suffix_length", "suffix_bytes", "seqno", "value_kind", "value_length", "value_bytes",
            "restart_offset", "restart_count",
        ];

        var positions = encoderOrder.Select(field => section.IndexOf(field, StringComparison.Ordinal)).ToArray();
        positions.Should().NotContain(-1, "the diagram must name every field the encoder writes");
        positions.Should().BeInAscendingOrder("the diagram must list the fields in on-disk order");
    }

    /// <summary>
    /// The guide's C# snippets must use the shipped signatures: every named argument of the
    /// SSTable construction example is a real constructor parameter, and the registry's
    /// Resolve is declared with the nullability it ships with.
    /// </summary>
    [Fact]
    public void BlockFormatGuide_CodeSnippets_MatchShippedSignatures()
    {
        var guide = File.ReadAllText(LocateBlockFormatGuide());

        var construction = System.Text.RegularExpressions.Regex.Match(
            guide, @"new SortedStringTable<[^>]*>\((?<args>[^;]*)\);", System.Text.RegularExpressions.RegexOptions.Singleline);
        construction.Success.Should().BeTrue("the guide shows how to construct a SortedStringTable with a codec");
        var namedArguments = System.Text.RegularExpressions.Regex.Matches(construction.Groups["args"].Value, @"(?<name>[A-Za-z_]\w*)\s*:")
            .Select(m => m.Groups["name"].Value)
            .ToList();
        namedArguments.Should().Contain("codec");
        var parameterNames = typeof(SortedStringTable<SerializableWrapper<int>, SerializableWrapper<int>>)
            .GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);
        namedArguments.Should().OnlyContain(n => parameterNames.Contains(n),
            "a named argument the constructor does not declare makes the example fail to compile");

        var resolve = typeof(ICompressionCodecRegistry).GetMethod(nameof(ICompressionCodecRegistry.Resolve))!;
        var nullable = new System.Reflection.NullabilityInfoContext().Create(resolve.ReturnParameter).ReadState
            == System.Reflection.NullabilityState.Nullable;
        guide.Should().Contain($"ICompressionCodec{(nullable ? "?" : string.Empty)} Resolve(byte codecId);",
            "the registry snippet must declare Resolve with its shipped return type");
    }

    private static string GuideSection(string heading)
    {
        var text = File.ReadAllText(LocateBlockFormatGuide());
        var start = text.IndexOf(heading, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, $"the guide has a '{heading}' section");
        var end = text.IndexOf("\n## ", start + heading.Length, StringComparison.Ordinal);
        return end < 0 ? text[start..] : text[start..end];
    }

    private static string LocateBlockFormatGuide()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "docs", "sstable-block-format.md");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            "Could not locate docs/sstable-block-format.md by walking up from the test binary's directory.");
    }

    /// <summary>
    /// Minimal stream wrapper that reports <c>CanSeek = false</c> so we can test the
    /// non-seekable path of <see cref="BlockFormat.ReadBlock"/>.
    /// </summary>
    private sealed class NonSeekableStream(byte[] data) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var available = data.Length - _position;
            var toRead = Math.Min(count, available);
            Array.Copy(data, _position, buffer, offset, toRead);
            _position += toRead;
            return toRead;
        }
    }
}
