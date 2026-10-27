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
namespace Boutquin.Storage.Infrastructure.ValueLog;

/// <summary>
/// Shared byte-level constants for the value-log entry-frame and file-header encoding.
/// The writer emits these layouts; the reader parses
/// them; a doc-level byte diagram in <c>docs/wisckey-value-log.md</c> mirrors the numbers
/// declared here.
/// </summary>
/// <remarks>
/// <para><b>Header:</b>
/// <c>[4B magic 'B','S','V','L'][4B format_version LE u32 = 1]</c> = 8 bytes.</para>
/// <para><b>Entry frame:</b>
/// <c>[4B length LE u32][4B header_crc32c LE u32][1B entry_kind][8B seqno LE u64][N payload][4B payload_crc32c LE u32]</c>
/// = 17 + <c>length</c> + 4 bytes. The header checksum covers every other header field — the
/// length (bytes 0..3), the kind byte and the sequence number (bytes 8..16) — so a damaged length
/// prefix is detected as corruption rather than inferred from where later frames might start. The
/// payload checksum covers the N payload bytes only.</para>
/// <para><b>Tear rule.</b> Recovery reads frames in order and decides on authenticated bytes only:
/// a partial header at end of file; a header whose landed bytes (a prefix a writer could produce —
/// a non-zero kind byte must be a defined kind) are followed only by zeros to end of file, the
/// all-zero run from a frame start being the empty prefix; a valid header whose payload is short at
/// end of file; or a payload checksum failure on a frame followed by nothing but zeros to end of
/// file is a torn tail and is truncated. A header checksum failure with any non-zero byte after the
/// header, or a payload checksum failure on a frame with a non-zero byte after it, is corruption and
/// fails loud. The writer refuses an empty payload, so every complete frame holds a non-zero byte
/// after its header — even an all-zero payload, because CRC32C of a run of zero bytes is zero only
/// for runs whose length is a multiple of 2^31 − 1, longer than any frame. A complete header that
/// fails its checksum is therefore never read as an unwritten tail.</para>
/// <para><b>Sealed segments.</b> The tear rule applies to the active (highest) segment only.
/// Rotation seals a segment in the <see cref="ValueLogManifest"/> — final byte length, frame count
/// and a CRC32C over the segment's bytes — before its successor exists, and nothing is appended to
/// it again. Recovery verifies each sealed segment against its seal; any difference, including
/// every shape the tear rule would repair, fails loud.</para>
/// </remarks>
public static class ValueLogFrame
{
    /// <summary>Magic bytes that open every value-log segment file.</summary>
    public static readonly byte[] Magic = [(byte)'B', (byte)'S', (byte)'V', (byte)'L'];

    /// <summary>The only format version the v2.0.0 GA accepts.</summary>
    public const uint CurrentFormatVersion = 1;

    /// <summary>Byte length of the segment file header (magic + version).</summary>
    public const int HeaderSize = 8;

    /// <summary>Byte length of an entry frame's fixed prefix (length + header checksum + kind + seqno).</summary>
    public const int EntryHeaderSize = 17;

    /// <summary>Byte length of the payload CRC32C trailer of an entry frame.</summary>
    public const int EntryCrcSize = 4;

    // Field offsets inside the 17-byte entry header. The kind byte stays at +8 because the engine's
    // WAL-replay pointer check reads it there.
    internal const int HeaderCrcOffset = 4;
    internal const int KindOffset = 8;
    internal const int SeqnoOffset = 9;

    /// <summary>Payload <c>entry_kind</c> byte: a normal user-payload frame.</summary>
    public const byte KindPayload = 0x00;

    /// <summary>Reserved <c>entry_kind</c> byte 0x01 — wire-reserved; readers accept-skip.</summary>
    public const byte KindReserved = 0x01;

    /// <summary>Total on-disk size of an entry frame carrying a payload of
    /// <paramref name="payloadLength"/> bytes: 17-byte header + payload + 4-byte payload CRC.</summary>
    public static long FrameSize(long payloadLength) => EntryHeaderSize + payloadLength + EntryCrcSize;

    /// <summary>CRC32C over the header fields the header checksum authenticates: the length
    /// (bytes 0..3) and the kind byte plus sequence number (bytes 8..16) of <paramref name="header"/>.</summary>
    internal static uint ComputeHeaderCrc(ReadOnlySpan<byte> header)
        => Crc32C.HashToUInt32(header[..HeaderCrcOffset], header[KindOffset..EntryHeaderSize]);

    /// <summary>The header checksum recorded in <paramref name="header"/>.</summary>
    internal static uint ReadHeaderCrc(ReadOnlySpan<byte> header)
        => BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(HeaderCrcOffset, 4));
}
