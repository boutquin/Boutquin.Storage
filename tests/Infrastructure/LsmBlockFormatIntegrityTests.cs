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
using System.Buffers.Binary;
using Boutquin.Storage.Domain.ValueObjects.ValueLog;
using Boutquin.Storage.Infrastructure.Compression;

namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// Pins writer/reader agreement on the SSTable block ceiling: both block decoders refuse a frame
/// whose uncompressed payload exceeds <see cref="BlockFormat.MaxUncompressedBlockBytes"/>, so no
/// writer may emit one. The block encoder refuses such a block, the SSTable writer splits blocks
/// below the ceiling whatever its configured block size and refuses an entry that cannot fit any
/// block before creating a file, and the engine's inline-value threshold is bounded so no legal
/// configuration routes an unflushable value into the SSTable.
/// </summary>
public sealed class LsmBlockFormatIntegrityTests : IDisposable
{
    private const int Cap = BlockFormat.MaxUncompressedBlockBytes;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"LsmBlockIntegrity_{Guid.NewGuid():N}");

    public LsmBlockFormatIntegrityTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best-effort */ }
        }
    }

    [Fact]
    public void WriteBlock_PayloadAboveReaderCeiling_IsRefused()
    {
        // Two entries sharing one value array keep the test's own allocation near half the cap
        // while the encoded payload lands just above it.
        var value = new byte[(Cap / 2) + 1];
        BlockEntry[] entries =
        [
            new(new byte[] { 1 }, 1UL, ValueKind.Inline, value),
            new(new byte[] { 2 }, 2UL, ValueKind.Inline, value),
        ];

        var act = () => BlockFormat.WriteBlock(entries, new NoCompressionCodec());

        act.Should().Throw<ArgumentException>(
            "a block the reader refuses as corrupt must never be emitted").WithMessage($"*{Cap}*");
    }

    [Fact]
    public void SortedStringTable_UnboundedBlockSize_SplitsBlocksBelowReaderCeiling_AndReadsBack()
    {
        const string fileName = "unbounded-blocks.sst";
        var sst = new SortedStringTable<SerializableWrapper<int>, RawBlob>(
            _dir, fileName, codec: new NoCompressionCodec(), blockSizeBytes: int.MaxValue);
        var payload = new byte[100 * 1024 * 1024];
        var writeFailure = Record.Exception(() => sst.Write(Enumerable.Range(1, 3)
            .Select(i => SegmentWriteEntry<SerializableWrapper<int>, RawBlob>.Inline(
                new SerializableWrapper<int>(i), new RawBlob(payload, (byte)i), (ulong)i))
            .ToList()));
        writeFailure.Should().BeNull("each entry fits a block on its own, so the writer must split rather than refuse");

        for (var i = 1; i <= 3; i++)
        {
            RawBlob value = null!;
            var found = false;
            var readFailure = Record.Exception(() => found = sst.TryGetValue(new SerializableWrapper<int>(i), out value));
            readFailure.Should().BeNull($"key {i} was written and acknowledged, so its block must decode");
            found.Should().BeTrue($"key {i} was written and acknowledged");
            value.Length.Should().Be(payload.Length);
            value.Tag.Should().Be((byte)i);
        }
    }

    [Fact]
    public void SortedStringTable_SingleEntryAboveReaderCeiling_IsRefusedBeforeAnyFileIsCreated()
    {
        const string fileName = "oversized-entry.sst";
        var sst = new SortedStringTable<SerializableWrapper<int>, RawBlob>(
            _dir, fileName, codec: new NoCompressionCodec());
        var payload = new byte[Cap];
        var before = DirectoryImage();

        var act = () => sst.Write(
            [SegmentWriteEntry<SerializableWrapper<int>, RawBlob>.Inline(new SerializableWrapper<int>(1), new RawBlob(payload, 1), 1UL)]);

        act.Should().Throw<ArgumentException>("an entry no block can hold must be refused before the write")
            .WithMessage($"*{Cap}*");
        DirectoryImage().Should().BeEquivalentTo(before,
            "the refusal must happen before the first output byte, leaving no partial or temp file");
    }

    private Dictionary<string, long> DirectoryImage()
        => Directory.GetFiles(_dir).ToDictionary(f => Path.GetFileName(f), f => new FileInfo(f).Length, StringComparer.Ordinal);

    [Fact]
    public void ValueLogOptions_ThresholdAboveInlineCeiling_IsRejected()
    {
        var act = () => new ValueLogOptions(ValueLogThresholdBytes: int.MaxValue);

        act.Should().Throw<ArgumentOutOfRangeException>(
                "an inline value that large cannot fit an SSTable block the reader accepts")
            .WithParameterName("ValueLogThresholdBytes");
    }

    /// <summary>A length-prefixed byte payload plus a one-byte tag, so large values stay cheap to build.</summary>
    private sealed class RawBlob : ISerializable<RawBlob>, IComparable<RawBlob>
    {
        private readonly byte[] _payload;

        public RawBlob()
            : this([], 0)
        {
        }

        public RawBlob(byte[] payload, byte tag)
        {
            _payload = payload;
            Tag = tag;
        }

        public int Length => _payload.Length;

        public byte Tag { get; }

        public void Serialize(Stream stream)
        {
            Span<byte> header = stackalloc byte[5];
            BinaryPrimitives.WriteInt32LittleEndian(header, _payload.Length);
            header[4] = Tag;
            stream.Write(header);
            stream.Write(_payload);
        }

        public static RawBlob Deserialize(Stream stream)
        {
            Span<byte> header = stackalloc byte[5];
            stream.ReadExactly(header);
            var payload = new byte[BinaryPrimitives.ReadInt32LittleEndian(header)];
            stream.ReadExactly(payload);
            return new RawBlob(payload, header[4]);
        }

        public int CompareTo(RawBlob? other) => other is null ? 1 : Tag.CompareTo(other.Tag);
    }
}
