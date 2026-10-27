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
using Boutquin.Storage.Infrastructure.Compression;

namespace Boutquin.Storage.Infrastructure.Tests.Compression;

/// <summary>
/// Phase C5 verification: the four shipped codecs (LZ4, GZip, Brotli, None) round-trip
/// arbitrary byte payloads unchanged; each codec advertises the spec-mandated CodecId;
/// the default registry resolves by CodecId and returns null for unregistered codes.
/// </summary>
public sealed class CompressionCodecRoundTripTests
{
    public static IEnumerable<object[]> AllCodecs()
    {
        yield return new object[] { (ICompressionCodec)new LZ4Codec() };
        yield return new object[] { (ICompressionCodec)new GZipCodec() };
        yield return new object[] { (ICompressionCodec)new BrotliCodec() };
        yield return new object[] { (ICompressionCodec)new NoCompressionCodec() };
    }

    [Fact]
    public void CodecId_MatchesSpec()
    {
        new LZ4Codec().CodecId.Should().Be(0x01);
        new GZipCodec().CodecId.Should().Be(0x02);
        new BrotliCodec().CodecId.Should().Be(0x03);
        new NoCompressionCodec().CodecId.Should().Be(0x00);

        new LZ4Codec().Name.Should().Be("LZ4");
        new GZipCodec().Name.Should().Be("GZip");
        new BrotliCodec().Name.Should().Be("Brotli");
        new NoCompressionCodec().Name.Should().Be("None");
    }

    [Theory]
    [MemberData(nameof(AllCodecs))]
    public void RoundTrip_RecoversRandomPayload(ICompressionCodec codec)
    {
        // 16 KB payload — matches the default SSTable block size. Random bytes are
        // worst-case for compression ratio; all four codecs must still round-trip.
        var source = new byte[16 * 1024];
        new Random(42).NextBytes(source);

        var maxLen = codec.MaxCompressedLength(source.Length);
        var scratch = new byte[maxLen];
        var compressedLen = codec.Compress(source, scratch);
        compressedLen.Should().BeGreaterThan(0);

        var restored = new byte[source.Length];
        var restoredLen = codec.Decompress(scratch.AsSpan(0, compressedLen), restored, source.Length);

        restoredLen.Should().Be(source.Length);
        restored.Should().Equal(source);
    }

    [Theory]
    [MemberData(nameof(AllCodecs))]
    public void RoundTrip_RecoversHighlyCompressiblePayload(ICompressionCodec codec)
    {
        // All-zero payload — best-case for compression ratio, verifies round-trip at the
        // extreme where the compressed representation is dramatically smaller than input.
        var source = new byte[16 * 1024];

        var scratch = new byte[codec.MaxCompressedLength(source.Length)];
        var compressedLen = codec.Compress(source, scratch);

        var restored = new byte[source.Length];
        var restoredLen = codec.Decompress(scratch.AsSpan(0, compressedLen), restored, source.Length);

        restoredLen.Should().Be(source.Length);
        restored.Should().Equal(source);
    }

    [Theory]
    [MemberData(nameof(AllCodecs))]
    public void RoundTrip_HandlesEmptyPayload(ICompressionCodec codec)
    {
        var source = Array.Empty<byte>();
        var scratch = new byte[codec.MaxCompressedLength(0)];
        var compressedLen = codec.Compress(source, scratch);

        var restored = Array.Empty<byte>();
        var restoredLen = codec.Decompress(scratch.AsSpan(0, compressedLen), restored, 0);
        restoredLen.Should().Be(0);
    }

    [Theory]
    [MemberData(nameof(AllCodecs))]
    public void Decompress_ClaimedLengthShorterThanPayload_ThrowsInvalidData(ICompressionCodec codec)
    {
        // A frame whose recorded uncompressed length is smaller than the payload it carries
        // (writer bug, codec-id mismatch, forged-and-re-checksummed frame) must fail closed.
        // Returning the first N bytes would hand the caller a silently truncated block.
        var source = new byte[4 * 1024];
        new Random(7).NextBytes(source);

        var scratch = new byte[codec.MaxCompressedLength(source.Length)];
        var compressedLen = codec.Compress(source, scratch);

        var claimed = source.Length / 2;
        var restored = new byte[source.Length];
        var act = () => codec.Decompress(scratch.AsSpan(0, compressedLen), restored, claimed);

        act.Should().Throw<InvalidDataException>(
            "every codec must reject a payload longer than the recorded uncompressed length");
    }

    [Fact]
    public void GZip_RoundTrip_LargePayload_PooledDecompress()
    {
        // 1 MB of random bytes exercises the ArrayPool rent/return path in GZipCodec.Decompress.
        // Previous implementation called source.ToArray() (unpooled full-copy per call); the new
        // implementation rents from ArrayPool<byte>.Shared instead.
        var codec = new GZipCodec();
        var source = new byte[1024 * 1024];
        new Random(99).NextBytes(source);

        var maxLen = codec.MaxCompressedLength(source.Length);
        var scratch = new byte[maxLen];
        var compressedLen = codec.Compress(source, scratch);

        var restored = new byte[source.Length];
        var restoredLen = codec.Decompress(scratch.AsSpan(0, compressedLen), restored, source.Length);

        restoredLen.Should().Be(source.Length);
        restored.Should().Equal(source);
    }

    [Fact]
    public void DefaultRegistry_ResolvesAllShippedCodecs()
    {
        var registry = new DefaultCompressionCodecRegistry();

        registry.Resolve(0x00).Should().BeOfType<NoCompressionCodec>();
        registry.Resolve(0x01).Should().BeOfType<LZ4Codec>();
        registry.Resolve(0x02).Should().BeOfType<GZipCodec>();
        registry.Resolve(0x03).Should().BeOfType<BrotliCodec>();
    }

    [Fact]
    public void DefaultRegistry_ReturnsNull_ForUnregisteredCodecId()
    {
        var registry = new DefaultCompressionCodecRegistry();
        registry.Resolve(0xFF).Should().BeNull();
    }

    [Fact]
    public void Registry_Register_ThrowsOnCodecIdCollision()
    {
        var registry = new DefaultCompressionCodecRegistry();
        var collision = new LZ4Codec();

        var act = () => registry.Register(collision);
        act.Should().Throw<ArgumentException>()
            .WithMessage("*CodecId*0x01*already*");
    }

    [Fact]
    public void Registry_Registered_ListsAllCodecs()
    {
        var registry = new DefaultCompressionCodecRegistry();
        registry.Registered.Should().HaveCount(4);
        registry.Registered.Select(c => c.CodecId).Should().BeEquivalentTo(new byte[] { 0x00, 0x01, 0x02, 0x03 });
    }

    // AC-8.3 (F-19): NoCompressionCodec.Compress with a destination smaller than the source
    // performs a silent partial copy (CopyTo uses available space). It must instead throw
    // InvalidOperationException with a message naming the deficiency.
    [Fact]
    public void NoCompressionCodec_Compress_UndersizedDestination_ThrowsInvalidOperationException()
    {
        var codec = new NoCompressionCodec();
        var source = new byte[] { 1, 2, 3, 4, 5 };  // 5 bytes
        var destination = new byte[3];               // too small — only 3 bytes

        // NoCompressionCodec.MaxCompressedLength(5) == 5. Destination.Length(3) < source.Length(5).
        // Must throw before copying, not silently produce a truncated output.
        var act = () => codec.Compress(source, destination);
        act.Should().Throw<InvalidOperationException>();
    }

    // The decoder must prove it reached the gzip trailer: GZipStream reports a clean end of
    // input when the 8-byte CRC32/ISIZE trailer is missing or cut short, which would turn a
    // detected body corruption into a silent one.
    public static IEnumerable<object[]> TrailerCuts()
    {
        for (var cut = 1; cut <= 8; cut++)
        {
            yield return new object[] { cut, false };
            yield return new object[] { cut, true };
        }
    }

    [Theory]
    [MemberData(nameof(TrailerCuts))]
    public void GZip_Decompress_TruncatedTrailer_ThrowsInvalidDataException(int cut, bool corruptBody)
    {
        var codec = new GZipCodec();
        var source = new byte[4096];
        new Random(7).NextBytes(source);
        var scratch = new byte[codec.MaxCompressedLength(source.Length)];
        var compressedLen = codec.Compress(source, scratch);

        var frame = scratch.AsSpan(0, compressedLen - cut).ToArray();
        if (corruptBody)
        {
            frame[frame.Length / 2] ^= 0x40;
        }

        var act = () => codec.Decompress(frame, new byte[source.Length], source.Length);
        act.Should().Throw<InvalidDataException>(
            "a gzip frame whose CRC32/ISIZE trailer is missing or short must not decode successfully");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4096)]
    public void GZip_Decompress_IntactFrame_RoundTripsAndRejectsForgedTrailerValues(int length)
    {
        var codec = new GZipCodec();
        var source = new byte[length];
        new Random(length).NextBytes(source);
        var scratch = new byte[codec.MaxCompressedLength(length)];
        var compressedLen = codec.Compress(source, scratch);

        var restored = new byte[length];
        codec.Decompress(scratch.AsSpan(0, compressedLen), restored, length).Should().Be(length);
        restored.Should().Equal(source);

        if (compressedLen == 0)
        {
            // The encoder emits no frame at all for an empty payload; nothing to forge.
            return;
        }

        // A flipped CRC32 byte is still rejected (integrity of the trailer values themselves).
        var forged = scratch.AsSpan(0, compressedLen).ToArray();
        forged[compressedLen - 8] ^= 0x01;
        var act = () => codec.Decompress(forged, new byte[length], length);
        act.Should().Throw<InvalidDataException>();
    }
}
