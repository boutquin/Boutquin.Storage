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
/// Passthrough codec: the on-disk bytes equal the in-memory bytes. Used when the workload
/// already holds incompressible payloads (encrypted blobs, already-compressed media) and
/// wants to pay zero CPU on the read path.
/// </summary>
public sealed class NoCompressionCodec : ICompressionCodec
{
    public byte CodecId => 0x00;
    public string Name => "None";

    public int MaxCompressedLength(int uncompressedLength) => uncompressedLength;

    public int Compress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        // AC-8.3 (F-19): guard undersized destination explicitly with the documented contract
        // exception. The runtime's CopyTo throws ArgumentException with a generic message;
        // an upfront check lets callers distinguish a contract violation from an I/O failure.
        if (destination.Length < source.Length)
        {
            throw new InvalidOperationException(
                $"NoCompressionCodec: destination ({destination.Length} bytes) is smaller than source ({source.Length} bytes). " +
                $"Allocate at least MaxCompressedLength({source.Length}) = {source.Length} bytes.");
        }

        source.CopyTo(destination);
        return source.Length;
    }

    public int Decompress(ReadOnlySpan<byte> source, Span<byte> destination, int expectedUncompressedLength)
    {
        if (source.Length != expectedUncompressedLength)
        {
            throw new InvalidDataException(
                $"NoCompressionCodec: expected {expectedUncompressedLength} bytes, received {source.Length}.");
        }
        source.CopyTo(destination);
        return source.Length;
    }
}
