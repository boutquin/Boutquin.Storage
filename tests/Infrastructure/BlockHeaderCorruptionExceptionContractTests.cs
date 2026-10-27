// Copyright (c) 2024-2026 Pierre G. Boutquin. All rights reserved.
//
//   Licensed under the Apache License, Version 2.0 (the "License").
//   You may not use this file except in compliance with the License.
//   You may obtain a copy of the License at
//
//       http://www.apache.org/licenses/LICENSE-2.0
//
//   Unless required by applicable law or agreed to in writing, software
//   distributed under the License is distributed on an "AS IS" BASIS,
//   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//
//   See the License for the specific language governing permissions and
//   limitations under the License.
//
using System.Buffers.Binary;
using Boutquin.Storage.Domain.Interfaces.Compression;
using Boutquin.Storage.Infrastructure.Compression;

namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// A corrupt or forged SSTable block header surfaces the block family's typed corruption
/// exception, <see cref="CorruptBlockException"/>, on every decode path — including when the
/// header is rejected by the codec-derived compressed-length bound before its CRC is checked.
/// <see cref="CorruptBlockException"/> does not derive from <see cref="InvalidDataException"/>,
/// so a handler written against the documented type would miss any other exception.
/// </summary>
public sealed class BlockHeaderCorruptionExceptionContractTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"block-header-corruption-{Guid.NewGuid():N}");

    public BlockHeaderCorruptionExceptionContractTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void BitRotInBlockUncompressedLength_SurfacesCorruptBlockException_OnLookupAndScan()
    {
        const string fileName = "bitrot.sst";
        var writer = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _dir, fileName, codec: new NoCompressionCodec());
        writer.Write(Enumerable.Range(0, 8)
            .Select(i => (new SerializableWrapper<int>(i), new SerializableWrapper<string>($"v{i}"), 0UL))
            .ToList());

        // Lower the CRC-covered uncompressed_length of block 0 without re-CRCing: bit rot, not
        // forgery. With no compression the codec bound equals the uncompressed length, so the
        // stored compressed_length now exceeds it and the bound check fires before the CRC check.
        var path = Path.Combine(_dir, fileName);
        var file = File.ReadAllBytes(path);
        var uncompressed = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(0, 4));
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(0, 4), uncompressed - 1);
        File.WriteAllBytes(path, file);

        var table = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _dir, fileName, codec: new NoCompressionCodec());
        var lookup = () => table.TryGetValue(new SerializableWrapper<int>(3), out _);
        var scan = () => table.EnumerateInline().ToList();

        lookup.Should().Throw<CorruptBlockException>("a block header that fails its CRC is block corruption");
        scan.Should().Throw<CorruptBlockException>("the scan decode path shares the same corruption contract");
    }

    [Fact]
    public void ReadBlockAndDecompressPayload_CompressedLengthAboveCodecBound_ThrowCorruptBlockException()
    {
        // Header inside the remaining-bytes bound but above what the codec can produce for the
        // declared uncompressed length (10 bytes uncompressed cannot compress to 1000).
        var frame = ForgedFrame(uncompressedLen: 10, compressedLen: 1000);

        var read = () => BlockFormat.ReadBlock(new MemoryStream(frame, writable: false), new NoCompressionCodec());
        var decompress = () => BlockFormat.DecompressPayload(frame, new NoCompressionCodec());

        read.Should().Throw<CorruptBlockException>();
        decompress.Should().Throw<CorruptBlockException>();
    }

    [Theory]
    [InlineData("none")]
    [InlineData("lz4")]
    [InlineData("brotli")]
    [InlineData("gzip")]
    public void ReadBlockAndDecompressPayload_CrcValidPayloadTheCodecCannotDecode_ThrowCorruptBlockException(string codecName)
    {
        ICompressionCodec codec = codecName switch
        {
            "none" => new NoCompressionCodec(),
            "lz4" => new LZ4Codec(),
            "brotli" => new BrotliCodec(),
            _ => new GZipCodec(),
        };
        // A body the codec cannot turn into 64 bytes: too short for "none", garbage for the others.
        var body = codecName == "none" ? new byte[16] : Enumerable.Repeat((byte)0xA5, 16).ToArray();
        var frame = CrcValidFrame(uncompressedLen: 64, body);

        var read = () => BlockFormat.ReadBlock(new MemoryStream(frame, writable: false), codec);
        var decompress = () => BlockFormat.DecompressPayload(frame, codec);

        read.Should().Throw<CorruptBlockException>("a CRC-valid frame whose payload cannot be decoded is a corrupt block");
        decompress.Should().Throw<CorruptBlockException>("the scan decode path shares the same corruption contract");
    }

    private static byte[] CrcValidFrame(int uncompressedLen, byte[] body)
    {
        var frame = new byte[8 + body.Length + 4];
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, 4), uncompressedLen);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4, 4), body.Length);
        body.CopyTo(frame.AsSpan(8));
        var crc = Boutquin.Storage.Infrastructure.Algorithms.Crc32C.HashToUInt32(frame.AsSpan(0, 8), body);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(8 + body.Length, 4), crc);
        return frame;
    }

    private static byte[] ForgedFrame(int uncompressedLen, int compressedLen)
    {
        var frame = new byte[8 + compressedLen + 4];
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, 4), uncompressedLen);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4, 4), compressedLen);
        return frame;
    }
}
