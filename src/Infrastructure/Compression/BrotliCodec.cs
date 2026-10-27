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
using Boutquin.Storage.Domain.Interfaces.Compression;

namespace Boutquin.Storage.Infrastructure.Compression;

/// <summary>
/// BCL-only Brotli codec — no new package dependency. Higher compression ratio than GZip
/// at moderate decode cost; suited to cold-storage SSTables where read latency is not the
/// bottleneck.
/// </summary>
public sealed class BrotliCodec : ICompressionCodec
{
    public byte CodecId => 0x03;
    public string Name => "Brotli";

    public int MaxCompressedLength(int uncompressedLength)
    {
        // BrotliEncoder exposes the exact bound; wrap it here for registry-driven sizing.
        return BrotliEncoder.GetMaxCompressedLength(uncompressedLength);
    }

    public int Compress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (!BrotliEncoder.TryCompress(source, destination, out var bytesWritten))
        {
            throw new InvalidOperationException(
                $"Brotli encoder rejected destination buffer of {destination.Length} bytes for {source.Length}-byte input.");
        }
        return bytesWritten;
    }

    public int Decompress(ReadOnlySpan<byte> source, Span<byte> destination, int expectedUncompressedLength)
    {
        if (!BrotliDecoder.TryDecompress(source, destination[..expectedUncompressedLength], out var bytesWritten))
        {
            throw new InvalidDataException(
                $"Brotli decoder failed or produced unexpected length (expected {expectedUncompressedLength} bytes).");
        }
        if (bytesWritten != expectedUncompressedLength)
        {
            throw new InvalidDataException(
                $"Brotli decoder produced {bytesWritten} bytes, expected {expectedUncompressedLength}.");
        }
        return bytesWritten;
    }
}
