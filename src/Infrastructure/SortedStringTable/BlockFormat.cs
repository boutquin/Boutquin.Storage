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
using Boutquin.Storage.Domain.Helpers;
using Boutquin.Storage.Domain.Interfaces.Compression;
using Boutquin.Storage.Domain.ValueObjects.SortedStringTable;

namespace Boutquin.Storage.Infrastructure.SortedStringTable;

/// <summary>
/// v2 SSTable block writer + reader. A block is a framed, CRC-protected, codec-compressed
/// envelope over a prefix-compressed entry stream with periodic restart points for
/// within-block binary search (DDIA 2e p. 119, Fig 4-2; LevelDB block layout).
/// </summary>
/// <remarks>
/// <para><b>Frame layout (on disk):</b>
/// <c>[4B uncompressed_length][4B compressed_length][compressed payload][4B CRC32C]</c>.
/// CRC32C uses the Castagnoli polynomial and covers the first 8 header bytes plus the
/// compressed payload.</para>
/// <para><b>Uncompressed payload layout:</b>
/// <c>[entry_0][entry_1]...[entry_N-1][4B restart_offset_0][4B restart_offset_1]...[4B restart_offset_R-1][4B restart_count]</c>.
/// The tail <c>restart_count</c> lets the reader reverse-parse without a separate index.</para>
/// <para><b>Entry layout:</b>
/// <c>[varint shared_prefix_length][varint suffix_length][suffix_bytes][8B seqno LE][1B value_kind][varint value_length][value_bytes]</c>.
/// Restart points store the full key — <c>shared_prefix_length = 0, suffix_length = full</c>.</para>
/// <para><b>Reference:</b> DDIA 2e p. 119, 128.</para>
/// </remarks>
public static class BlockFormat
{
    public const int DefaultRestartInterval = 16;

    /// <summary>
    /// Absolute ceiling on a block's declared uncompressed_length, enforced before allocating
    /// the decompression buffer. The block frame's CRC32C is a checksum, not a MAC, so a forged
    /// frame with a recomputed CRC reaches the decode path; an over-declared uncompressed_length
    /// must not drive a multi-GB allocation (OOM/DoS) ahead of the codec ever observing the
    /// size mismatch. There is no remaining-bytes basis to bound uncompressed_length against on
    /// the wire (it is, by construction, larger than the compressed body), so the bound is an
    /// absolute cap. 256 MiB matches the WAL's MaxPayloadSize ceiling for a single decoded
    /// payload and is far above any legitimate block: the SSTable writer targets a 16 KiB block
    /// size. The cap binds the writer too — <see cref="WriteBlock"/> refuses a payload above it,
    /// and the SSTable writer splits blocks below it and refuses an entry that cannot fit — so
    /// every frame the writer emits is one both decoders accept.
    /// </summary>
    internal const int MaxUncompressedBlockBytes = 256 * 1024 * 1024;

    /// <summary>
    /// Worst-case bytes one entry adds to a block's uncompressed payload beyond its key and value
    /// bytes: three 5-byte varints (shared-prefix, suffix and value lengths), the 8-byte seqno, the
    /// value-kind byte, and a 4-byte restart offset (any entry may start a restart run).
    /// </summary>
    internal const int MaxEntryOverheadBytes = (3 * 5) + 8 + 1 + 4;

    /// <summary>The 4-byte <c>restart_count</c> trailer every block payload ends with.</summary>
    internal const int BlockTrailerBytes = 4;

    /// <summary>Encodes and frames a block. Returns the full on-disk bytes.</summary>
    public static byte[] WriteBlock(
        IReadOnlyList<BlockEntry> entries,
        ICompressionCodec codec,
        int restartInterval = DefaultRestartInterval)
    {
        Guard.AgainstNullOrDefault(() => entries);
        Guard.AgainstNullOrDefault(() => codec);
        Guard.AgainstNegativeOrZero(() => restartInterval);

        var uncompressed = EncodeUncompressedPayload(entries, restartInterval);

        // Both decoders refuse a frame declaring more than the cap, so emitting one would turn an
        // acknowledged write into a segment that reads back as corrupt.
        if (uncompressed.Length > MaxUncompressedBlockBytes)
        {
            throw new ArgumentException(
                $"SSTable block payload of {uncompressed.Length} bytes exceeds the {MaxUncompressedBlockBytes}-byte " +
                "block ceiling the reader enforces; split the entries across blocks.",
                nameof(entries));
        }

        var maxCompressed = codec.MaxCompressedLength(uncompressed.Length);
        var compressedBuffer = new byte[maxCompressed];
        var compressedLen = codec.Compress(uncompressed, compressedBuffer);

        var framed = new byte[4 + 4 + compressedLen + 4];
        BinaryPrimitives.WriteInt32LittleEndian(framed.AsSpan(0, 4), uncompressed.Length);
        BinaryPrimitives.WriteInt32LittleEndian(framed.AsSpan(4, 4), compressedLen);
        compressedBuffer.AsSpan(0, compressedLen).CopyTo(framed.AsSpan(8));

        var crc = Crc32C.HashToUInt32(framed.AsSpan(0, 8 + compressedLen));
        BinaryPrimitives.WriteUInt32LittleEndian(framed.AsSpan(8 + compressedLen, 4), crc);

        return framed;
    }

    /// <summary>
    /// Reads a framed block from <paramref name="stream"/>, validates the CRC32C trailer,
    /// decompresses, and decodes the entry list. Throws
    /// <see cref="CorruptBlockException"/> on CRC mismatch or a header whose declared lengths the
    /// frame bounds reject, and <see cref="UnsupportedValueKindException"/> for reserved value-kind bytes (0x02-0xFF).
    /// Both <see cref="ValueKind.Inline"/> and <see cref="ValueKind.ValueLogPointer"/> are
    /// accepted; a ValueLogPointer carries its 12-byte <c>(fileIndex, offset)</c> tuple in
    /// the value-bytes slot as a 12-byte <c>(fileIndex:u32, offset:u64)</c> tuple.
    /// </summary>
    public static IReadOnlyList<BlockEntry> ReadBlock(Stream stream, ICompressionCodec codec)
    {
        Guard.AgainstNullOrDefault(() => stream);
        Guard.AgainstNullOrDefault(() => codec);

        Span<byte> header = stackalloc byte[8];
        ReadExact(stream, header);
        var uncompressedLen = BinaryPrimitives.ReadInt32LittleEndian(header[..4]);
        var compressedLen = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(4, 4));

        if (uncompressedLen < 0 || compressedLen < 0)
        {
            throw new CorruptBlockException(
                $"SSTable block frame declares negative length (uncompressed={uncompressedLen}, compressed={compressedLen}); bytes are likely corrupt.");
        }

        // Bound the declared compressed length by the bytes actually remaining (compressed body +
        // 4-byte CRC trailer) BEFORE allocating, so a corrupt/hostile header cannot drive a huge
        // allocation (OOM / DoS) ahead of the CRC check. Long arithmetic avoids any int overflow.
        // uncompressedLen needs no pre-bound: it is CRC-covered (the CRC over the 8-byte header is
        // verified before the uncompressed buffer is allocated below), so a corrupt value fails CRC.
        if (stream.CanSeek)
        {
            var remaining = stream.Length - stream.Position;
            if ((long)compressedLen + 4 > remaining)
            {
                throw new CorruptBlockException(
                    $"SSTable block frame declares compressed length {compressedLen} (+4 CRC) exceeding the {remaining} bytes remaining; bytes are likely corrupt.");
            }
        }

        // Ceiling on compressed_length — applied unconditionally, including on non-seekable
        // streams where the remaining-bytes guard above is unavailable. A forged-and-re-CRC'd frame
        // reaches this path; an over-declared compressed_length must not drive a multi-GB allocation
        // (OOM / DoS) ahead of the codec.
        EnsureCompressedLenWithinCodecBound(compressedLen, uncompressedLen, codec);

        var compressed = new byte[compressedLen];
        ReadExact(stream, compressed);

        Span<byte> trailer = stackalloc byte[4];
        ReadExact(stream, trailer);
        var recordedCrc = BinaryPrimitives.ReadUInt32LittleEndian(trailer);

        var computedCrc = Crc32C.HashToUInt32(header, compressed);

        if (recordedCrc != computedCrc)
        {
            throw new CorruptBlockException(recordedCrc, computedCrc);
        }

        EnsureUncompressedLenWithinCap(uncompressedLen);
        var uncompressed = new byte[uncompressedLen];
        DecompressOrThrowCorrupt(codec, compressed, uncompressed, uncompressedLen);

        return DecodeUncompressedPayload(uncompressed);
    }

    /// <summary>
    /// Exposed for the SSTable writer that streams entries into target-sized blocks.
    /// Returns the uncompressed block payload including the restart-point tail.
    /// </summary>
    internal static byte[] EncodeUncompressedPayload(
        IReadOnlyList<BlockEntry> entries,
        int restartInterval)
    {
        using var buffer = new MemoryStream();
        var restartOffsets = new List<int>();
        ReadOnlyMemory<byte> previousKey = ReadOnlyMemory<byte>.Empty;
        var seqnoScratch = new byte[8];

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var isRestart = i % restartInterval == 0;
            if (isRestart)
            {
                restartOffsets.Add((int)buffer.Position);
            }

            var sharedPrefix = isRestart ? 0 : CommonPrefixLength(previousKey.Span, entry.Key.Span);
            var suffix = entry.Key.Span[sharedPrefix..];

            VarintHelpers.WriteVarUInt64(buffer, (ulong)sharedPrefix);
            VarintHelpers.WriteVarUInt64(buffer, (ulong)suffix.Length);
            buffer.Write(suffix);

            BinaryPrimitives.WriteUInt64LittleEndian(seqnoScratch, entry.SequenceNumber);
            buffer.Write(seqnoScratch);

            buffer.WriteByte((byte)entry.Kind);

            VarintHelpers.WriteVarUInt64(buffer, (ulong)entry.Value.Length);
            buffer.Write(entry.Value.Span);

            previousKey = entry.Key;
        }

        // Tail: restart offsets then restart count.
        Span<byte> restartBuffer = stackalloc byte[4];
        for (var r = 0; r < restartOffsets.Count; r++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(restartBuffer, restartOffsets[r]);
            buffer.Write(restartBuffer);
        }
        BinaryPrimitives.WriteInt32LittleEndian(restartBuffer, restartOffsets.Count);
        buffer.Write(restartBuffer);

        return buffer.ToArray();
    }

    internal static IReadOnlyList<BlockEntry> DecodeUncompressedPayload(byte[] payload)
    {
        if (payload.Length < 4)
        {
            throw new CorruptBlockException(
                $"SSTable block payload too short ({payload.Length} bytes) to contain a restart-count tail.");
        }

        var restartCount = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(payload.Length - 4, 4));
        // Widen the tail-size arithmetic to long before multiplying: a forged restart_count such
        // as 0x20000001 overflows `restartCount * 4` in int to a large negative value, which would
        // make the bound FALSE (bypassed) and then `entriesEnd = payload.Length - 4 - (negative)`
        // wrap to a wild value handed to the MemoryStream slice ctor — surfacing
        // ArgumentOutOfRangeException instead of the fail-closed CorruptBlockException the block
        // corruption contract requires. Mirrors ParseRestartOffsets's (long)count * 4 widening.
        if (restartCount < 0 || (long)restartCount * 4 + 4 > payload.Length)
        {
            throw new CorruptBlockException(
                $"SSTable block payload declares restart_count={restartCount} but tail cannot hold that many offsets in {payload.Length} bytes.");
        }

        var entriesEnd = payload.Length - 4 - (restartCount * 4);
        using var stream = new MemoryStream(payload, 0, entriesEnd, writable: false);

        var result = new List<BlockEntry>();
        var previousKey = Array.Empty<byte>();
        var seqnoScratch = new byte[8];

        while (stream.Position < entriesEnd)
        {
            var sharedPrefix = ReadEntryLength(stream, "shared_prefix_length");
            var suffixLen = ReadEntryLength(stream, "suffix_length");

            if (sharedPrefix > previousKey.Length)
            {
                throw new CorruptBlockException(
                    $"SSTable block entry declares shared_prefix_length={sharedPrefix} vs previous key length {previousKey.Length}.");
            }

            // Bound the decoded suffix length by the bytes actually remaining in the bounded
            // entries region BEFORE allocating. The block CRC32C is a checksum, not a MAC, so a
            // forged block with a recomputed CRC passes integrity; an over-declared suffix_length
            // must not drive a multi-GB allocation ahead of the read-bound (same guard as
            // DecompressPayload). suffixLen is the part read from the stream; the shared prefix is
            // copied from the already-validated previous key.
            EnsureWithinRemaining(stream, suffixLen, "suffix_length");

            // Guard against int overflow: sharedPrefix + suffixLen must not exceed int.MaxValue.
            if ((long)sharedPrefix + suffixLen > int.MaxValue)
            {
                throw new CorruptBlockException(
                    $"SSTable block entry sharedPrefix={sharedPrefix} + suffixLen={suffixLen} exceeds int.MaxValue.");
            }

            var key = new byte[sharedPrefix + suffixLen];
            Array.Copy(previousKey, 0, key, 0, sharedPrefix);
            ReadExact(stream, key.AsSpan(sharedPrefix, suffixLen));

            var seqnoBytes = seqnoScratch.AsSpan();
            ReadExact(stream, seqnoBytes);
            var seqno = BinaryPrimitives.ReadUInt64LittleEndian(seqnoBytes);

            var kindByte = stream.ReadByte();
            if (kindByte < 0)
            {
                throw new CorruptBlockException("Unexpected end of block payload while reading value_kind byte.");
            }

            var kind = (ValueKind)(byte)kindByte;
            switch (kindByte)
            {
                case (int)ValueKind.Inline:
                case (int)ValueKind.ValueLogPointer:
                    break;
                default:
                    throw new UnsupportedValueKindException((byte)kindByte);
            }

            var valueLen = ReadEntryLength(stream, "value_length");
            EnsureWithinRemaining(stream, valueLen, "value_length");
            var value = new byte[valueLen];
            ReadExact(stream, value);

            result.Add(new BlockEntry(key, seqno, kind, value));
            previousKey = key;
        }

        return result;
    }

    // ── P4.6 binary-search helpers ───────────────────────────────────────────

    /// <summary>
    /// Decompresses a framed block (CRC validated) and returns the raw uncompressed payload.
    /// Callers use <see cref="ParseRestartOffsets"/> and <see cref="DecodeFromOffset"/> to
    /// implement within-block binary search without decoding all entries.
    /// </summary>
    internal static byte[] DecompressPayload(byte[] framedBytes, ICompressionCodec codec)
    {
        Guard.AgainstNullOrDefault(() => framedBytes);
        Guard.AgainstNullOrDefault(() => codec);

        using var stream = new MemoryStream(framedBytes, writable: false);

        Span<byte> header = stackalloc byte[8];
        ReadExact(stream, header);
        var uncompressedLen = BinaryPrimitives.ReadInt32LittleEndian(header[..4]);
        var compressedLen = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(4, 4));

        if (uncompressedLen < 0 || compressedLen < 0)
        {
            throw new CorruptBlockException(
                $"SSTable block frame declares negative length (uncompressed={uncompressedLen}, compressed={compressedLen}).");
        }

        // Bound the declared length by the bytes actually present before allocating —
        // a corrupt 4-byte compressed_length read from disk must not drive an allocation
        // of up to ~2 GB on the point-lookup path (same guard as ReadBlock).
        var remaining = framedBytes.Length - 8 - 4;
        if (compressedLen > remaining)
        {
            throw new CorruptBlockException(
                $"SSTable block frame declares compressed length {compressedLen} exceeding the " +
                $"{remaining} bytes remaining in the frame.");
        }

        // Same codec-derived ceiling as ReadBlock, so the point-lookup and range/compaction decode
        // paths accept and refuse exactly the same frames.
        EnsureCompressedLenWithinCodecBound(compressedLen, uncompressedLen, codec);

        var compressed = new byte[compressedLen];
        ReadExact(stream, compressed);

        Span<byte> trailer = stackalloc byte[4];
        ReadExact(stream, trailer);
        var recordedCrc = BinaryPrimitives.ReadUInt32LittleEndian(trailer);
        var computedCrc = Crc32C.HashToUInt32(header, compressed);
        if (recordedCrc != computedCrc)
        {
            throw new CorruptBlockException(recordedCrc, computedCrc);
        }

        EnsureUncompressedLenWithinCap(uncompressedLen);
        var uncompressed = new byte[uncompressedLen];
        DecompressOrThrowCorrupt(codec, compressed, uncompressed, uncompressedLen);
        return uncompressed;
    }

    /// <summary>
    /// Parses the restart-point byte offsets from the tail of an uncompressed block payload.
    /// Restart offsets are positions relative to the start of the payload where an entry
    /// stores its full key (shared_prefix_length = 0). Used for within-block binary search.
    /// </summary>
    internal static int[] ParseRestartOffsets(byte[] payload)
    {
        Guard.AgainstNullOrDefault(() => payload);

        if (payload.Length < 4)
        {
            return [];
        }

        var count = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(payload.Length - 4, 4));
        if (count < 0 || (long)count * 4 + 4 > payload.Length)
        {
            throw new CorruptBlockException(
                $"SSTable block payload declares restart_count={count} but the tail cannot hold that many offsets in {payload.Length} bytes; bytes are likely corrupt.");
        }

        if (count == 0)
        {
            return [];
        }

        var offsets = new int[count];
        var tailStart = payload.Length - 4 - count * 4;
        for (var i = 0; i < count; i++)
        {
            var offset = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(tailStart + i * 4, 4));

            // Each offset is decoded from CRC-covered bytes (a checksum, not a MAC) and is later
            // handed to ReadKeyAtOffset / DecodeFromOffset as a slice start, so it must name an
            // entry inside the entries region [0, tailStart). The writer emits them strictly
            // increasing; a repeated or descending offset is equally corrupt.
            var lowerBound = i == 0 ? 0 : offsets[i - 1] + 1;
            if (offset < lowerBound || offset >= tailStart)
            {
                throw new CorruptBlockException(
                    $"SSTable block payload declares restart offset {i} = {offset}, outside [{lowerBound}, {tailStart}) " +
                    "(restart offsets must be strictly increasing and inside the entries region); bytes are likely corrupt.");
            }

            offsets[i] = offset;
        }
        return offsets;
    }

    /// <summary>
    /// Returns the byte offset at which the entry-data region ends (where the restart tail begins).
    /// Used as the upper bound for <see cref="DecodeFromOffset"/>.
    /// </summary>
    internal static int GetEntriesEnd(byte[] payload)
    {
        if (payload.Length < 4)
        {
            return 0;
        }

        var count = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(payload.Length - 4, 4));
        if (count < 0)
        {
            return 0;
        }

        // Widen to long before multiplying: a forged count like 0x20000001 overflows `4 + count *
        // 4` in int to a large negative tail, making `tail > payload.Length` false and returning an
        // out-of-[0, payload.Length] entriesEnd. Mirrors ParseRestartOffsets's (long)count * 4.
        var tail = 4L + (long)count * 4;
        return tail > payload.Length ? 0 : payload.Length - (int)tail;
    }

    /// <summary>
    /// Reads the key bytes at a restart-point position in the uncompressed payload.
    /// At a restart point, shared_prefix_length = 0, so the full key is in the suffix field.
    /// </summary>
    internal static byte[] ReadKeyAtOffset(byte[] payload, int offset)
    {
        Guard.AgainstNullOrDefault(() => payload);

        // Bound the read to the entries region: a restart key that ends early must fail as block
        // corruption, not run on into the restart tail and decode the offsets as key bytes.
        var entriesEnd = GetEntriesEnd(payload);
        if (offset < 0 || offset >= entriesEnd)
        {
            throw new CorruptBlockException(
                $"SSTable block restart offset {offset} lies outside the entries region [0, {entriesEnd}); bytes are likely corrupt.");
        }

        using var ms = new MemoryStream(payload, offset, entriesEnd - offset, writable: false);
        _ = ReadEntryLength(ms, "shared_prefix_length"); // always 0 at restart points
        var suffixLen = ReadEntryLength(ms, "suffix_length");
        // Bound the decoded key length by the bytes actually remaining in the bounded payload
        // slice BEFORE allocating — a forged (CRC-recomputed) restart offset must not drive a
        // multi-GB allocation ahead of the read-bound. EnsureWithinRemaining also rejects a
        // negative suffixLen (the only length guard at this site).
        EnsureWithinRemaining(ms, suffixLen, "suffix_length");
        var keyBytes = new byte[suffixLen];
        ReadExact(ms, keyBytes);
        return keyBytes;
    }

    /// <summary>
    /// Decodes block entries starting from <paramref name="startOffset"/> through
    /// <paramref name="entriesEnd"/>. The previous key is initialised to empty (correct
    /// because restart points always store the full key with shared_prefix_length = 0).
    /// </summary>
    internal static IReadOnlyList<BlockEntry> DecodeFromOffset(byte[] payload, int startOffset, int entriesEnd)
    {
        Guard.AgainstNullOrDefault(() => payload);

        var decodeLen = entriesEnd - startOffset;
        if (decodeLen <= 0)
        {
            return [];
        }

        using var stream = new MemoryStream(payload, startOffset, decodeLen, writable: false);
        var result = new List<BlockEntry>();
        var previousKey = Array.Empty<byte>();
        var seqnoScratch = new byte[8];

        while (stream.Position < decodeLen)
        {
            var sharedPrefix = ReadEntryLength(stream, "shared_prefix_length");
            var suffixLen = ReadEntryLength(stream, "suffix_length");

            if (sharedPrefix > previousKey.Length)
            {
                throw new CorruptBlockException(
                    $"SSTable block entry declares shared_prefix_length={sharedPrefix} vs previous key length {previousKey.Length}.");
            }

            // Bound the decoded suffix length by the bytes remaining in the bounded decode window
            // BEFORE allocating — a forged (CRC-recomputed) entry must not drive a multi-GB
            // allocation ahead of the read-bound (same guard as DecompressPayload).
            EnsureWithinRemaining(stream, suffixLen, "suffix_length");

            // Guard against int overflow: sharedPrefix + suffixLen must not exceed int.MaxValue.
            if ((long)sharedPrefix + suffixLen > int.MaxValue)
            {
                throw new CorruptBlockException(
                    $"SSTable block entry sharedPrefix={sharedPrefix} + suffixLen={suffixLen} exceeds int.MaxValue.");
            }

            var key = new byte[sharedPrefix + suffixLen];
            Array.Copy(previousKey, 0, key, 0, sharedPrefix);
            ReadExact(stream, key.AsSpan(sharedPrefix, suffixLen));

            var seqnoBytes = seqnoScratch.AsSpan();
            ReadExact(stream, seqnoBytes);
            var seqno = BinaryPrimitives.ReadUInt64LittleEndian(seqnoBytes);

            var kindByte = stream.ReadByte();
            if (kindByte < 0)
            {
                throw new CorruptBlockException("Unexpected end of block payload while reading value_kind byte.");
            }

            switch (kindByte)
            {
                case (int)ValueKind.Inline:
                case (int)ValueKind.ValueLogPointer:
                    break;
                default:
                    throw new UnsupportedValueKindException((byte)kindByte);
            }

            var valueLen = ReadEntryLength(stream, "value_length");

            EnsureWithinRemaining(stream, valueLen, "value_length");
            var value = new byte[valueLen];
            ReadExact(stream, value);

            result.Add(new BlockEntry(key, seqno, (ValueKind)(byte)kindByte, value));
            previousKey = key;
        }

        return result;
    }

    private static int CommonPrefixLength(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        var min = Math.Min(a.Length, b.Length);
        var i = 0;
        while (i < min && a[i] == b[i])
        {
            i++;
        }
        return i;
    }

    /// <summary>
    /// Rejects a declared <c>compressed_length</c> larger than <paramref name="codec"/> can produce
    /// for the declared <c>uncompressed_length</c> (clamped to <see cref="MaxUncompressedBlockBytes"/>),
    /// BEFORE the caller allocates the compressed-payload buffer. This is the bound
    /// <see cref="WriteBlock"/> itself compresses into — it sizes its output buffer with
    /// <see cref="ICompressionCodec.MaxCompressedLength"/> — so every frame the writer emits passes
    /// and both decode paths accept the same frames: a fixed cap below that bound made a block
    /// holding one large incompressible value readable by point lookup but unreadable by range
    /// scans and compaction. Clamping to the uncompressed cap keeps the allocation bounded by the
    /// same absolute ceiling the decompression buffer already obeys, even on a non-seekable stream
    /// with no remaining-bytes basis. Shared by <see cref="ReadBlock"/> and
    /// <see cref="DecompressPayload"/>.
    /// </summary>
    // A frame whose CRC verifies can still carry a payload the codec cannot decode: the CRC is a
    // checksum, not a MAC, so a forged-and-re-CRC'd frame reaches the codec. Every shipped codec
    // reports that as InvalidDataException (and a codec may surface a malformed stream as an
    // argument or index error); the block family's contract is CorruptBlockException, so the
    // failure is translated here, once, for both ReadBlock and DecompressPayload. A decoded length
    // other than the declared one is the same corruption.
    private static void DecompressOrThrowCorrupt(
        ICompressionCodec codec, byte[] compressed, byte[] uncompressed, int uncompressedLen)
    {
        int decoded;
        try
        {
            decoded = codec.Decompress(compressed, uncompressed, uncompressedLen);
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException)
        {
            throw new CorruptBlockException(
                $"SSTable block payload ({compressed.Length} bytes) passed its CRC32C check but codec " +
                $"'{codec.Name}' could not decode it to the declared {uncompressedLen} bytes: " +
                $"{ex.GetType().Name}: {ex.Message}");
        }

        if (decoded != uncompressedLen)
        {
            throw new CorruptBlockException(
                $"SSTable block payload decoded by codec '{codec.Name}' to {decoded} bytes, not the " +
                $"declared {uncompressedLen}.");
        }
    }

    private static void EnsureCompressedLenWithinCodecBound(int compressedLen, int uncompressedLen, ICompressionCodec codec)
    {
        var bound = codec.MaxCompressedLength(Math.Min(uncompressedLen, MaxUncompressedBlockBytes));
        if (compressedLen > bound)
        {
            throw new CorruptBlockException(
                $"SSTable block frame declares compressed length {compressedLen} exceeding the {bound}-byte " +
                $"maximum codec '{codec.Name}' can produce for the declared uncompressed length {uncompressedLen}; " +
                "bytes are likely corrupt or forged.");
        }
    }

    /// <summary>
    /// Rejects a declared <c>uncompressed_length</c> that exceeds the absolute
    /// <see cref="MaxUncompressedBlockBytes"/> ceiling BEFORE the caller allocates the
    /// decompression buffer. Unlike <c>compressed_length</c>, <c>uncompressed_length</c> has no
    /// remaining-bytes basis to bound against on the wire (it is larger than the compressed body
    /// by construction), so an absolute cap is the only available guard. The block CRC32C is a
    /// checksum, not a MAC, so a forged-and-re-CRC'd frame reaches this path; an over-declared
    /// uncompressed_length must not drive a multi-GB OOM allocation ahead of the codec observing
    /// the size mismatch. Shared by <see cref="ReadBlock"/> and <see cref="DecompressPayload"/>.
    /// </summary>
    private static void EnsureUncompressedLenWithinCap(int uncompressedLen)
    {
        if (uncompressedLen > MaxUncompressedBlockBytes)
        {
            throw new CorruptBlockException(
                $"SSTable block frame declares uncompressed length {uncompressedLen} exceeding the " +
                $"{MaxUncompressedBlockBytes}-byte cap; bytes are likely corrupt or forged.");
        }
    }

    /// <summary>
    /// Rejects a decoded length that is negative or exceeds the bytes remaining in the bounded
    /// <paramref name="stream"/> BEFORE the caller allocates a buffer of that length. The block
    /// CRC32C is a checksum, not a MAC, so a forged-and-re-CRC'd block reaches the decode path;
    /// a hostile suffix_length / value_length must not drive a multi-GB allocation ahead of the
    /// read-bound. Mirrors the <see cref="DecompressPayload"/> compressed-length guard.
    /// </summary>
    private static void EnsureWithinRemaining(Stream stream, int declaredLength, string fieldName)
    {
        var remaining = stream.Length - stream.Position;
        if (declaredLength < 0 || declaredLength > remaining)
        {
            throw new CorruptBlockException(
                $"SSTable block entry declares {fieldName}={declaredLength} exceeding the {remaining} bytes remaining in the block payload; bytes are likely corrupt.");
        }
    }

    /// <summary>
    /// Reads one entry length varint (shared_prefix_length, suffix_length, value_length) from the
    /// bounded block stream as a non-negative <see cref="int"/>. The varint comes from CRC-covered
    /// bytes, which a forger can recompute, so every malformed shape is block corruption: a value
    /// above <see cref="int.MaxValue"/> is rejected before the cast (0xFFFFFFFF would wrap to -1,
    /// 2^32 + 1 would alias to 1), and the varint reader's end-of-payload and 64-bit-overflow
    /// failures are re-raised as <see cref="CorruptBlockException"/>.
    /// </summary>
    private static int ReadEntryLength(Stream stream, string fieldName)
    {
        ulong raw;
        try
        {
            raw = VarintHelpers.ReadVarUInt64(stream);
        }
        catch (Exception ex) when (ex is EndOfStreamException or InvalidDataException)
        {
            throw new CorruptBlockException(
                $"SSTable block entry {fieldName} varint is truncated or malformed ({ex.Message}); bytes are likely corrupt.");
        }

        if (raw > int.MaxValue)
        {
            throw new CorruptBlockException(
                $"SSTable block entry declares {fieldName}={raw}, exceeding {int.MaxValue}; bytes are likely corrupt.");
        }

        return (int)raw;
    }

    private static void ReadExact(Stream stream, Span<byte> destination)
    {
        var total = 0;
        while (total < destination.Length)
        {
            var read = stream.Read(destination[total..]);
            if (read == 0)
            {
                throw new CorruptBlockException(
                    $"Unexpected end of stream while reading SSTable block (got {total} of {destination.Length} bytes).");
            }
            total += read;
        }
    }
}
