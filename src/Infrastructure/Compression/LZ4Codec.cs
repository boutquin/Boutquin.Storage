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

namespace Boutquin.Storage.Infrastructure.Compression;

/// <summary>
/// Default SSTable block codec — LZ4 frame-free, fast-mode. Industry default across
/// RocksDB, Cassandra, and ScyllaDB for OLTP read latency (decode ~2–4 GB/s/core vs.
/// ~150–400 MB/s/core for GZip).
/// </summary>
/// <remarks>
/// <para>
/// Uses the block API (not the LZ4 frame format) so the SSTable-block framing in
/// <c>BlockFormat</c> owns the CRC32C and length headers directly. This keeps the on-disk
/// codec identity as "raw LZ4 block" — swapping to Zstd-block later is purely a registry
/// change, no enclosing-format rewrite.
/// </para>
/// </remarks>
public sealed class LZ4Codec : ICompressionCodec
{
    public byte CodecId => 0x01;
    public string Name => "LZ4";

    // AC-8.6 (F-82-part): inline the trivial private wrapper — one less indirection on
    // every SSTable block-size probe. The wrapper added no abstraction over the K4os call.
    public int MaxCompressedLength(int uncompressedLength) => K4os.Compression.LZ4.LZ4Codec.MaximumOutputSize(uncompressedLength);

    public int Compress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        var written = K4os.Compression.LZ4.LZ4Codec.Encode(source, destination);
        if (written < 0)
        {
            throw new InvalidOperationException(
                $"LZ4 encoder rejected destination buffer of {destination.Length} bytes for {source.Length}-byte input.");
        }
        return written;
    }

    public int Decompress(ReadOnlySpan<byte> source, Span<byte> destination, int expectedUncompressedLength)
    {
        var written = K4os.Compression.LZ4.LZ4Codec.Decode(source, destination[..expectedUncompressedLength]);
        if (written != expectedUncompressedLength)
        {
            throw new InvalidDataException(
                $"LZ4 decoder produced {written} bytes, expected {expectedUncompressedLength}.");
        }
        return written;
    }

}
