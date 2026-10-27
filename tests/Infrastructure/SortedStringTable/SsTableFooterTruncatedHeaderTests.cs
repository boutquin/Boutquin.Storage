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
namespace Boutquin.Storage.Infrastructure.Tests.SortedStringTable;

/// <summary>
/// F-77 — footer header EOF detection. <c>ReadBody</c> read <c>format_version</c> and
/// <c>codec_id</c> with the raw single-byte <c>Stream.ReadByte()</c>, which returns <c>-1</c> at
/// end-of-stream and was cast straight to <c>(byte)</c> — silently fabricating a <c>0xFF</c>
/// version/codec byte from a truncated footer before any later field signalled the truncation.
/// The fix routes both bytes through <c>ReadExact</c>, the same EOF-detecting helper the rest of
/// the footer body uses, so a footer truncated inside its fixed header surfaces an
/// <see cref="EndOfStreamException"/> at the exact byte that ran short.
/// </summary>
public sealed class SsTableFooterTruncatedHeaderTests
{
    /// <summary>
    /// Builds a footer buffer whose body has been truncated to exactly <paramref name="bodyBytes"/>
    /// bytes, with a correct CRC32C over that truncated body and a correct trailing footer_length,
    /// so the truncation surfaces inside <c>ReadBody</c> (not at the CRC / trailer-length checks).
    /// </summary>
    private static byte[] BuildTruncatedFooter(byte[] bodyBytes)
    {
        var crc = global::Boutquin.Storage.Infrastructure.Algorithms.Crc32C.HashToUInt32(bodyBytes);
        var buffer = new byte[bodyBytes.Length + 4 + 4]; // body + crc + trailer-length
        Array.Copy(bodyBytes, 0, buffer, 0, bodyBytes.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            buffer.AsSpan(bodyBytes.Length, 4), crc);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            buffer.AsSpan(bodyBytes.Length + 4, 4), (uint)(bodyBytes.Length + 4)); // body + crc
        return buffer;
    }

    [Fact]
    public void Deserialize_FooterTruncatedAfterMagic_ThrowsEndOfStreamAtFormatVersionRead()
    {
        // Body = the 4-byte magic only. The very next read is format_version (1 byte) and the
        // stream is already at EOF. With ReadExact, the throw cites a 1-byte shortfall
        // ("got 0 of 1 bytes"); with the raw ReadByte() the version+codec are silently
        // fabricated as 0xFF and the failure is deferred to the 4-byte entry_count read.
        var body = new byte[] { (byte)'B', (byte)'S', (byte)'S', (byte)'1' };
        var bytes = BuildTruncatedFooter(body);

        var act = () => SsTableFooter.Deserialize(bytes);

        act.Should().Throw<EndOfStreamException>(
                "a footer truncated immediately after its magic must fail loudly at the format_version " +
                "read, not silently fabricate a 0xFF version byte from ReadByte()'s -1 EOF sentinel")
            .WithMessage("*got 0 of 1 bytes*",
                "format_version must be read with the EOF-detecting ReadExact (1-byte read), not the " +
                "raw ReadByte() that defers the shortfall to a later multi-byte field");
    }
}
