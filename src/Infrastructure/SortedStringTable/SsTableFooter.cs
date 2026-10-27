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
using Boutquin.Storage.Domain.ValueObjects.SortedStringTable;

namespace Boutquin.Storage.Infrastructure.SortedStringTable;

/// <summary>
/// SSTable footer — anchors the file's reverse-parse entry point and carries the
/// codec identity, min/max key bounds, the maximum sequence number, the per-block
/// index (for O(log B) block-level binary search), and the persisted bloom-filter
/// bits that survive process restart.
/// </summary>
/// <remarks>
/// <para><b>On-disk layout</b> (head-to-tail once the reader has seeked to the footer start via
/// the trailing <c>footer_length</c> word):
/// <code>
/// [magic = 'B','S','S','1']                    (4 bytes)
/// [format_version = 1]                         (1 byte)
/// [codec_id]                                   (1 byte)
/// [entry_count]                                (4 bytes uint32 LE)
/// [varint min_key_length][min_key bytes]
/// [varint max_key_length][max_key bytes]
/// [varint max_sequence_number]
/// [varint block_index_count]
///    per entry: [varint first_key_length][first_key bytes][varint offset]
/// [varint bloom_bits_count][varint bloom_hash_count][varint bloom_bytes_length][bloom bytes]
/// [varint tombstone_count]
///    per entry: [varint key_length][key bytes][varint deletion_seqno]
/// [optional BLM1 custom Bloom descriptor: kind 1, codec identity/version and state]
/// [footer_crc32c]                              (4 bytes uint32 LE; Castagnoli over every body byte above)
/// [footer_length]                              (4 bytes uint32 LE; body + crc, excludes this trailer)
/// </code>
/// The format version starts at 1 at GA — there is no pre-1 on-disk footer (greenfield premise),
/// so the native fields and tombstone count are always present. The custom Bloom descriptor
/// is optional and covered by the same footer CRC; when present, native Bloom counts are zero.
/// See <c>docs/segment-bloom-filters.md</c> for the descriptor layout and codec lifecycle.
/// </para>
/// <para><b>Magic vs format_version decision record.</b> The <c>'BSS1'</c> magic and
/// <c>format_version</c> byte are distinct identifiers. <c>'BSS1'</c> is the four-byte
/// file-family signature ("Boutquin Storage SSTable, family 1"), distinguishing this
/// block-structured layout from any future reworked family (<c>'BSS2'</c>, etc).
/// <c>format_version</c> is the iteration number within the current family, and starts at 1
/// at GA. The v1 footer carries the full block-structured layout including
/// <c>max_sequence_number</c> and the tombstone list (deleted keys paired with their deletion
/// seqnos) — the latter closes the compaction tombstone-GC gap DDIA Ch. 4 p. 124 names as a
/// load-bearing LSM property. (No pre-GA footer family or earlier format_version ever persisted
/// to disk under the greenfield v2.0.0 premise, so the reader does not carry v_old branches.)</para>
/// </remarks>
public sealed record SsTableFooter(
    byte FormatVersion,
    byte CodecId,
    uint EntryCount,
    ReadOnlyMemory<byte> MinKey,
    ReadOnlyMemory<byte> MaxKey,
    ulong MaxSequenceNumber,
    IReadOnlyList<SsTableBlockIndexEntry> BlockIndex,
    int BloomBitsCount,
    int BloomHashCount,
    ReadOnlyMemory<byte> BloomBits,
    IReadOnlyList<SsTableTombstoneEntry> Tombstones)
{
    /// <summary>Optional custom Bloom state envelope, covered by the footer CRC; empty selects native Bloom fields.</summary>
    public ReadOnlyMemory<byte> BloomState { get; init; }

    /// <summary>Four-byte file-family signature at the head of every footer: ASCII 'B','S','S','1'.</summary>
    public static readonly byte[] Magic = [(byte)'B', (byte)'S', (byte)'S', (byte)'1'];

    /// <summary>Current footer format version — iteration number within the 'BSS1' family. Starts
    /// at 1 at GA (no earlier on-disk version exists under the greenfield premise).</summary>
    public const byte CurrentFormatVersion = 1;

    public byte[] Serialize()
    {
        using var ms = new MemoryStream();
        ms.Write(Magic);
        ms.WriteByte(FormatVersion);
        ms.WriteByte(CodecId);

        Span<byte> u32 = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(u32, EntryCount);
        ms.Write(u32);

        WriteLengthPrefixed(ms, MinKey.Span);
        WriteLengthPrefixed(ms, MaxKey.Span);

        // max_sequence_number field follows max_key.
        VarintHelpers.WriteVarUInt64(ms, MaxSequenceNumber);

        VarintHelpers.WriteVarUInt64(ms, (ulong)BlockIndex.Count);
        foreach (var entry in BlockIndex)
        {
            WriteLengthPrefixed(ms, entry.FirstKey.Span);
            VarintHelpers.WriteVarUInt64(ms, (ulong)entry.Offset);
        }

        VarintHelpers.WriteVarUInt64(ms, (ulong)BloomBitsCount);
        VarintHelpers.WriteVarUInt64(ms, (ulong)BloomHashCount);
        VarintHelpers.WriteVarUInt64(ms, (ulong)BloomBits.Length);
        ms.Write(BloomBits.Span);

        // Persisted tombstone list (deleted keys + deletion seqnos). The compactor's tombstone-GC
        // step needs the seqno to prove that no live entry older than the tombstone survives
        // elsewhere before reclaiming the marker (DDIA Ch. 4 p. 124).
        VarintHelpers.WriteVarUInt64(ms, (ulong)Tombstones.Count);
        foreach (var tombstone in Tombstones)
        {
            WriteLengthPrefixed(ms, tombstone.Key.Span);
            VarintHelpers.WriteVarUInt64(ms, tombstone.SequenceNumber);
        }

        if (!BloomState.IsEmpty)
        {
            ms.Write(BloomState.Span);
        }

        // CRC32C over the footer body. The footer carries the bloom bits, block index,
        // key range, and tombstone list — the inputs to "we can safely skip this segment"
        // decisions — so a bit flip here must fail the open loudly instead of silently
        // producing bloom false negatives or mis-skipped segments.
        var bodyLength = (int)ms.Length;
        if (!ms.TryGetBuffer(out var bodyBuffer))
        {
            bodyBuffer = new ArraySegment<byte>(ms.ToArray());
        }
        var bodyCrc = Algorithms.Crc32C.HashToUInt32(bodyBuffer.AsSpan(0, bodyLength));
        BinaryPrimitives.WriteUInt32LittleEndian(u32, bodyCrc);
        ms.Write(u32);

        // Trailing footer_length = body + CRC bytes (everything before this trailer).
        BinaryPrimitives.WriteUInt32LittleEndian(u32, (uint)bodyLength + CrcSize);
        ms.Write(u32);

        return ms.ToArray();
    }

    public static SsTableFooter Deserialize(byte[] bytes)
    {
        Guard.AgainstNullOrDefault(() => bytes);

        if (bytes.Length < 4)
        {
            throw new InvalidDataException("SSTable footer buffer too short to hold the trailing footer_length.");
        }

        var trailerLen = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(bytes.Length - 4, 4));

        // Widen to long before adding to prevent uint wrap-around: a forged trailerLen near
        // uint.MaxValue would satisfy `trailerLen + 4 == bytes.Length` as uint while casting
        // negative as int, driving MemoryStream/Span with a negative length.
        if ((long)trailerLen + 4 != bytes.Length)
        {
            throw new InvalidDataException(
                $"SSTable footer trailer length {trailerLen} does not match buffer size {bytes.Length - 4}.");
        }

        // Guard against trailerLen < CrcSize before subtraction (uint underflow) and against
        // trailerLen > int.MaxValue before the (int) cast.
        if (trailerLen < CrcSize || trailerLen > int.MaxValue)
        {
            throw new InvalidDataException(
                $"SSTable footer trailer length {trailerLen} is outside the valid range [{CrcSize}, {int.MaxValue}].");
        }

        VerifyBodyCrc(bytes.AsSpan(0, (int)trailerLen));
        using var ms = new MemoryStream(bytes, 0, (int)(trailerLen - CrcSize), writable: false);
        return ReadBody(ms);
    }

    /// <summary>
    /// Probe the tail of <paramref name="fileBytes"/> for a footer. Returns <c>true</c>
    /// when the trailing <c>footer_length</c> resolves to a buffer starting with the
    /// <c>'B','S','S','1'</c> magic; otherwise returns <c>false</c>. v2.0.0 ships a single
    /// footer format — callers surface a <see cref="InvalidDataException"/> rather than
    /// attempting any fallback reader.
    /// </summary>
    public static bool TryReadFromEnd(byte[] fileBytes, out SsTableFooter? footer)
    {
        footer = null;
        if (fileBytes is null || fileBytes.Length < 8)
        {
            return false;
        }

        var trailer = BinaryPrimitives.ReadUInt32LittleEndian(fileBytes.AsSpan(fileBytes.Length - 4, 4));
        if (trailer == 0 || trailer > fileBytes.Length - 4)
        {
            return false;
        }

        var footerStart = fileBytes.Length - 4 - (int)trailer;
        if (footerStart < 0)
        {
            return false;
        }

        if (fileBytes.Length - footerStart < 4 ||
            fileBytes[footerStart] != Magic[0] ||
            fileBytes[footerStart + 1] != Magic[1] ||
            fileBytes[footerStart + 2] != Magic[2] ||
            fileBytes[footerStart + 3] != Magic[3])
        {
            return false;
        }

        try
        {
            VerifyBodyCrc(fileBytes.AsSpan(footerStart, (int)trailer));
            using var ms = new MemoryStream(fileBytes, footerStart, (int)(trailer - CrcSize), writable: false);
            footer = ReadBody(ms);
            return true;
        }
        catch (Exception)
        {
            footer = null;
            return false;
        }
    }

    /// <summary>Bytes of the CRC32C word between the footer body and the trailing length.</summary>
    private const uint CrcSize = 4;

    /// <summary>
    /// Verifies the CRC32C word at the tail of <paramref name="bodyAndCrc"/> against the
    /// body bytes that precede it. Throws <see cref="InvalidDataException"/> on mismatch —
    /// the footer drives segment-skip decisions, so corruption must surface loudly.
    /// </summary>
    private static void VerifyBodyCrc(ReadOnlySpan<byte> bodyAndCrc)
    {
        if (bodyAndCrc.Length < (int)CrcSize)
        {
            throw new InvalidDataException("SSTable footer too short to carry its CRC32C.");
        }

        var body = bodyAndCrc[..^(int)CrcSize];
        var recorded = BinaryPrimitives.ReadUInt32LittleEndian(bodyAndCrc[^(int)CrcSize..]);
        var computed = Algorithms.Crc32C.HashToUInt32(body);
        if (recorded != computed)
        {
            throw new InvalidDataException(
                $"SSTable footer failed its CRC32C check (recorded=0x{recorded:X8}, computed=0x{computed:X8}). " +
                "The footer carries the bloom bits, block index, key range, and tombstones; " +
                "a corrupt footer must not be trusted.");
        }
    }

    private static SsTableFooter ReadBody(Stream stream)
    {
        Span<byte> magic = stackalloc byte[4];
        ReadExact(stream, magic);
        if (magic[0] != Magic[0] || magic[1] != Magic[1] || magic[2] != Magic[2] || magic[3] != Magic[3])
        {
            throw new InvalidDataException(
                $"SSTable footer magic mismatch: expected 'BSS1', observed 0x{magic[0]:X2}{magic[1]:X2}{magic[2]:X2}{magic[3]:X2}.");
        }

        // format_version and codec_id are fixed single-byte fields. Read them through the
        // EOF-detecting ReadExact (not the raw ReadByte(), whose -1 EOF sentinel casts to a
        // silent 0xFF) so a footer truncated inside its header fails at the exact short byte.
        Span<byte> headerByte = stackalloc byte[1];
        ReadExact(stream, headerByte);
        var formatVersion = headerByte[0];

        // The body CRC32C is a checksum, so a footer carrying any other format_version passes it.
        // This build knows exactly one field layout; parsing another version's bytes with it would
        // silently misread the footer, so an unknown version fails closed instead.
        if (formatVersion != CurrentFormatVersion)
        {
            throw new InvalidDataException(
                $"SSTable footer declares format_version={formatVersion}; this build reads only format_version {CurrentFormatVersion}.");
        }

        ReadExact(stream, headerByte);
        var codecId = headerByte[0];

        Span<byte> u32 = stackalloc byte[4];
        ReadExact(stream, u32);
        var entryCount = BinaryPrimitives.ReadUInt32LittleEndian(u32);

        var minKey = ReadLengthPrefixed(stream);
        var maxKey = ReadLengthPrefixed(stream);

        // The v1 footer always carries max_sequence_number after max_key. No earlier format_version
        // exists on disk under the greenfield premise, so there is no field-absent branch to read.
        var maxSeqno = VarintHelpers.ReadVarUInt64(stream);

        var rawBlockCount = VarintHelpers.ReadVarUInt64(stream);
        EnsureWithinRemaining(stream, rawBlockCount, "block-index count");
        var blockCount = (int)rawBlockCount;
        var blockIndex = new List<SsTableBlockIndexEntry>(blockCount);
        for (var i = 0; i < blockCount; i++)
        {
            var firstKey = ReadLengthPrefixed(stream);
            var offset = (long)VarintHelpers.ReadVarUInt64(stream);
            blockIndex.Add(new SsTableBlockIndexEntry(firstKey, offset));
        }

        // bloom_bits_count / bloom_hash_count are counts, not byte lengths, so EnsureWithinRemaining
        // (a bytes-remaining bound) does not apply directly — but they need the same fail-closed
        // discipline its sibling length-prefixed fields get. A forged footer (body-only CRC) can
        // declare a varint that wraps NEGATIVE under the int cast or an absurdly large count; either
        // smuggles a structurally-impossible bloom descriptor past open. Validate the raw varints
        // before the cast and reject out-of-range values with the footer's typed corruption exception.
        var rawBloomBitsCount = VarintHelpers.ReadVarUInt64(stream);
        var rawBloomHashCount = VarintHelpers.ReadVarUInt64(stream);
        EnsureBloomCountsInRange(rawBloomBitsCount, rawBloomHashCount);
        var bloomBitsCount = (int)rawBloomBitsCount;
        var bloomHashCount = (int)rawBloomHashCount;
        var rawBloomBytesLen = VarintHelpers.ReadVarUInt64(stream);
        EnsureWithinRemaining(stream, rawBloomBytesLen, "bloom-bits length");

        // Consistency check: bloom_bytes_length must equal ceil(bloom_bits_count / 8).
        // An independently range-checked but mutually inconsistent pair throws a raw arithmetic
        // exception on bloom construction rather than the fail-closed InvalidDataException.
        var expectedBloomBytes = ((long)bloomBitsCount + 7) >> 3;
        if ((long)rawBloomBytesLen != expectedBloomBytes)
        {
            throw new InvalidDataException(
                $"SSTable footer bloom_bytes_length={rawBloomBytesLen} is inconsistent with " +
                $"bloom_bits_count={bloomBitsCount} (expected {expectedBloomBytes} bytes).");
        }

        var bloomBits = new byte[(int)rawBloomBytesLen];
        ReadExact(stream, bloomBits);

        // The v1 footer always carries the tombstone list (deleted keys + deletion seqnos). No
        // earlier format_version exists on disk under the greenfield premise.
        var rawTombstoneCount = VarintHelpers.ReadVarUInt64(stream);
        EnsureWithinRemaining(stream, rawTombstoneCount, "tombstone count");
        var tombstoneCount = (int)rawTombstoneCount;
        var tombstoneList = new List<SsTableTombstoneEntry>(tombstoneCount);
        for (var i = 0; i < tombstoneCount; i++)
        {
            var key = ReadLengthPrefixed(stream);
            var seqno = VarintHelpers.ReadVarUInt64(stream);
            tombstoneList.Add(new SsTableTombstoneEntry(key, seqno));
        }
        IReadOnlyList<SsTableTombstoneEntry> tombstones = tombstoneList;

        return new SsTableFooter(
            formatVersion,
            codecId,
            entryCount,
            minKey,
            maxKey,
            maxSeqno,
            blockIndex,
            bloomBitsCount,
            bloomHashCount,
            bloomBits,
            tombstones)
        {
            BloomState = ReadBloomState(stream)
        };
    }

    private static byte[] ReadBloomState(Stream stream)
    {
        var remaining = stream.Length - stream.Position;
        if (remaining > 16 * 1024 * 1024 + 512)
        {
            throw new InvalidDataException("Custom Bloom envelope exceeds the supported size.");
        }
        var bytes = new byte[(int)remaining];
        ReadExact(stream, bytes);
        if (bytes.Length > 0 && (bytes.Length < 5 || !bytes.AsSpan(0, 4).SequenceEqual("BLM1"u8) || bytes[4] != 1))
        {
            throw new InvalidDataException("Unknown SSTable Bloom footer extension.");
        }
        return bytes;
    }

    private static void WriteLengthPrefixed(Stream stream, ReadOnlySpan<byte> bytes)
    {
        VarintHelpers.WriteVarUInt64(stream, (ulong)bytes.Length);
        stream.Write(bytes);
    }

    private static byte[] ReadLengthPrefixed(Stream stream)
    {
        var rawLen = VarintHelpers.ReadVarUInt64(stream);
        EnsureWithinRemaining(stream, rawLen, "length-prefixed field");
        var buf = new byte[(int)rawLen];
        ReadExact(stream, buf);
        return buf;
    }

    /// <summary>
    /// Rejects a length/count read from the footer body that exceeds the bytes actually remaining,
    /// BEFORE it is used to size an allocation. The footer body is read from a fixed-size,
    /// seekable <see cref="MemoryStream"/>, so a declared length larger than what remains is
    /// corruption — and a raw <c>(int)</c> cast of a hostile <see cref="ulong"/> varint can wrap
    /// negative. This bound rejects corrupt lengths cheaply before the allocation; the footer CRC32C catches everything else.
    /// </summary>
    private static void EnsureWithinRemaining(Stream stream, ulong declared, string what)
    {
        if (!stream.CanSeek)
        {
            return;
        }

        var remaining = (ulong)(stream.Length - stream.Position);
        if (declared > remaining)
        {
            throw new InvalidDataException(
                $"SSTable footer {what} declares {declared} bytes/entries exceeding the {remaining} remaining; footer is corrupt.");
        }
    }

    /// <summary>
    /// Fail-closed range check for the footer's bloom <c>bits_count</c> / <c>hash_count</c>, read as
    /// raw varints BEFORE the <c>int</c> cast that would silently wrap a hostile value negative.
    /// Mirrors the <see cref="EnsureWithinRemaining"/> discipline applied to the footer's
    /// length-prefixed fields: a forged footer passes the body-only CRC, so the counts themselves
    /// must be bounded. Both counts must fit a non-negative <see cref="int"/>; an empty filter is
    /// the single legitimate <c>(0, 0)</c> case, and any non-empty filter (<c>bits_count &gt; 0</c>)
    /// needs at least one hash function to be usable.
    /// </summary>
    private static void EnsureBloomCountsInRange(ulong bitsCount, ulong hashCount)
    {
        if (bitsCount > int.MaxValue || hashCount > int.MaxValue)
        {
            throw new InvalidDataException(
                $"SSTable footer bloom descriptor is corrupt: bits_count={bitsCount}, hash_count={hashCount} " +
                $"exceed the {int.MaxValue} maximum (a forged varint wraps negative under the int cast); footer is corrupt.");
        }

        if (bitsCount > 0 && hashCount < 1)
        {
            throw new InvalidDataException(
                $"SSTable footer bloom descriptor is corrupt: a non-empty filter (bits_count={bitsCount}) " +
                "declares hash_count=0, which can never have been written by a valid filter; footer is corrupt.");
        }
    }

    private static void ReadExact(Stream stream, Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer[total..]);
            if (read == 0)
            {
                throw new EndOfStreamException(
                    $"Unexpected end of SSTable footer stream (got {total} of {buffer.Length} bytes).");
            }
            total += read;
        }
    }
}
