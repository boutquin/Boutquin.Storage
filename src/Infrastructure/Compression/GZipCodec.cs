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
using System.IO.Compression;
using System.IO.Hashing;
using Boutquin.Storage.Domain.Interfaces.Compression;

namespace Boutquin.Storage.Infrastructure.Compression;

/// <summary>
/// BCL-only GZip codec — no new package dependency. Slower on decode than LZ4 by an order
/// of magnitude; suitable for workloads that prefer a smaller on-disk footprint over low
/// decode latency, or that must interoperate with gzip-aware external tools.
/// </summary>
public sealed class GZipCodec : ICompressionCodec
{
    public byte CodecId => 0x02;
    public string Name => "GZip";

    public int MaxCompressedLength(int uncompressedLength)
    {
        // GZip frame = ~18-byte header/trailer + up to 5 bytes per 32 KB store block.
        // 128 bytes of slack + 1% of the input covers every case — DEFLATE expansion is
        // bounded at "input + 11 bytes per 16 KB block" plus the fixed gzip envelope.
        return uncompressedLength + 128 + (uncompressedLength / 100);
    }

    public int Compress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        using var output = new MemoryStream(destination.Length);
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(source);
        }

        var produced = (int)output.Length;
        if (produced > destination.Length)
        {
            throw new InvalidOperationException(
                $"GZip encoder produced {produced} bytes; destination buffer is {destination.Length} bytes.");
        }

        output.GetBuffer().AsSpan(0, produced).CopyTo(destination);
        return produced;
    }

    public int Decompress(ReadOnlySpan<byte> source, Span<byte> destination, int expectedUncompressedLength)
    {
        // Rent a pooled buffer to avoid an unpooled full-copy allocation (source.ToArray()) on
        // every decompress call. The rented buffer is returned in the finally even on exception.
        var rented = ArrayPool<byte>.Shared.Rent(source.Length);
        try
        {
            source.CopyTo(rented);
            var input = new MemoryStream(rented, 0, source.Length, writable: false);
            var written = DecompressCore(input, destination, expectedUncompressedLength);
            VerifyTrailer(source, destination[..written]);
            return written;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static int DecompressCore(Stream input, Span<byte> destination, int expectedUncompressedLength)
    {
        using var gzip = new GZipStream(input, CompressionMode.Decompress);

        var written = 0;
        while (written < expectedUncompressedLength)
        {
            var read = gzip.Read(destination[written..expectedUncompressedLength]);
            if (read == 0)
            {
                break;
            }
            written += read;
        }

        if (written != expectedUncompressedLength)
        {
            throw new InvalidDataException(
                $"GZip decoder produced {written} bytes, expected {expectedUncompressedLength}.");
        }

        // The stream must be exhausted at exactly the expected length. Probing for one more byte
        // rejects a payload longer than the recorded length (the sibling codecs all fail closed
        // on that mismatch) and drives the decoder through a PRESENT gzip trailer, so its
        // CRC32/ISIZE check runs. A missing or short trailer is not an error to GZipStream (it
        // reports a clean end of input), so VerifyTrailer checks the trailer explicitly.
        Span<byte> overflow = stackalloc byte[1];
        if (gzip.Read(overflow) != 0)
        {
            throw new InvalidDataException(
                $"GZip payload decodes to more than the expected {expectedUncompressedLength} bytes.");
        }

        return written;
    }

    /// <summary>
    /// Proves the frame ends in a complete, matching 8-byte gzip trailer (CRC32 then ISIZE, both
    /// little-endian, per the gzip format). <see cref="GZipStream"/> silently accepts a frame whose
    /// trailer is missing or cut short, which would turn a corrupt body into a successful decode.
    /// Frames are single-member, as <see cref="Compress"/> emits. An empty frame is the encoder's
    /// output for an empty payload and carries no trailer.
    /// </summary>
    private static void VerifyTrailer(ReadOnlySpan<byte> source, ReadOnlySpan<byte> decoded)
    {
        if (source.IsEmpty)
        {
            return;
        }

        // 10-byte minimum header + 8-byte trailer.
        if (source.Length < 18)
        {
            throw new InvalidDataException("GZip frame is too short to hold a header and CRC32/ISIZE trailer.");
        }

        var trailer = source[^8..];
        var crc = BinaryPrimitives.ReadUInt32LittleEndian(trailer);
        var isize = BinaryPrimitives.ReadUInt32LittleEndian(trailer[4..]);
        if (isize != (uint)decoded.Length || crc != Crc32.HashToUInt32(decoded))
        {
            throw new InvalidDataException(
                "GZip frame trailer is missing, truncated, or does not match the decoded payload (CRC32/ISIZE).");
        }
    }
}
