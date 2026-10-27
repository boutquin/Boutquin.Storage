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
namespace Boutquin.Storage.Domain.Interfaces.Compression;

/// <summary>
/// Pluggable compression algorithm for SSTable blocks and any future per-record codec
/// pipeline. Implementations operate on contiguous byte spans to keep the hot path
/// allocation-free.
/// </summary>
/// <remarks>
/// <para><b>Theory:</b>
/// DDIA 2e pp. 119, 131 prescribe compression of SSTable blocks but are codec-agnostic.
/// The block format persists a one-byte <see cref="CodecId"/> in the footer; readers
/// resolve the matching codec through an <see cref="ICompressionCodecRegistry"/>. This
/// keeps on-disk format decisions reversible — a future Zstd drop-in is one registration
/// call.</para>
/// <para><b>Contract:</b>
/// A well-formed pair <c>(Compress, Decompress)</c> MUST round-trip any byte span up to
/// <c>int.MaxValue</c>. <see cref="MaxCompressedLength"/> returns the writer-side scratch
/// size required for the worst-case expansion at the given uncompressed length.</para>
/// <para><b>Shipped codecs:</b> LZ4 (default, 0x01), GZip (0x02), Brotli (0x03), None (0x00).</para>
/// </remarks>
public interface ICompressionCodec
{
    /// <summary>
    /// Stable single-byte identifier persisted in the SSTable footer. Readers use this to
    /// resolve the matching codec via <see cref="ICompressionCodecRegistry"/>; the byte
    /// must survive every format rev without re-binding to a different algorithm.
    /// </summary>
    byte CodecId { get; }

    /// <summary>
    /// Human-readable codec name (e.g., "LZ4", "GZip", "Brotli", "None") — for logging and
    /// diagnostics only, never persisted.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Compresses <paramref name="source"/> into <paramref name="destination"/> and
    /// returns the number of bytes written. <paramref name="destination"/> MUST be at
    /// least <see cref="MaxCompressedLength"/>(<paramref name="source"/>.Length) bytes.
    /// </summary>
    int Compress(ReadOnlySpan<byte> source, Span<byte> destination);

    /// <summary>
    /// Decompresses <paramref name="source"/> into <paramref name="destination"/> and
    /// returns the number of bytes written — always
    /// <paramref name="expectedUncompressedLength"/> for a well-formed input. The reader
    /// knows the expected length from the v2 block header.
    /// </summary>
    int Decompress(ReadOnlySpan<byte> source, Span<byte> destination, int expectedUncompressedLength);

    /// <summary>
    /// Worst-case number of bytes the compressor can produce for an input of
    /// <paramref name="uncompressedLength"/> bytes. Callers size their scratch buffers by
    /// this value.
    /// </summary>
    int MaxCompressedLength(int uncompressedLength);
}
