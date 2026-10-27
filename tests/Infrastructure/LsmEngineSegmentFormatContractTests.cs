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

namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// Pins the contract between the LSM engine and its segment format: an entry the format's writer
/// would refuse at flush must be refused by the engine before it is acknowledged. An entry
/// acknowledged and then refused at flush faults that flush, every later flush, and — through the
/// WAL — every reopen.
/// </summary>
public sealed class LsmEngineSegmentFormatContractTests : IDisposable
{
    private const int Cap = BlockFormat.MaxUncompressedBlockBytes;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"LsmFormatContract_{Guid.NewGuid():N}");

    public LsmEngineSegmentFormatContractTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best-effort */ }
        }
    }

    private LsmStorageEngine<SizedKey, SerializableWrapper<int>> NewEngine()
        => LsmEngineFactory.Build<SizedKey, SerializableWrapper<int>>(
            memTableCapacity: 16, segmentFolder: _dir, segmentPrefix: "seg");

    /// <summary>
    /// Serialized key of <see cref="Cap"/> − 11 bytes plus a 4-byte value: within the WAL frame
    /// ceiling, but no SSTable block can hold it once the per-entry overhead is added.
    /// </summary>
    private static SizedKey OversizedKey() => new(Cap - 16, 9);

    [Fact]
    public async Task SetAsync_EntryNoSstBlockCanHold_IsRefusedBeforeAcknowledgement_AndEngineKeepsFlushing()
    {
        using var engine = NewEngine();

        var set = () => engine.SetAsync(OversizedKey(), new SerializableWrapper<int>(1));

        await set.Should().ThrowAsync<ArgumentException>(
            "the SSTable writer refuses this entry at flush, so the engine must refuse it before acknowledging it");
        var (_, found) = await engine.TryGetValueAsync(OversizedKey());
        found.Should().BeFalse("a refused entry is not acknowledged");

        await engine.SetAsync(new SizedKey(8, 1), new SerializableWrapper<int>(2));
        await engine.FlushAsync();
        var (value, smallFound) = await engine.TryGetValueAsync(new SizedKey(8, 1));
        smallFound.Should().BeTrue("the refusal must not fault the engine's flushes");
        value.Value.Should().Be(2);
    }

    [Fact]
    public async Task SetBulkAsync_BatchWithAnEntryNoSstBlockCanHold_IsRefusedWhole()
    {
        using var engine = NewEngine();

        var bulk = () => engine.SetBulkAsync(
        [
            new(new SizedKey(8, 1), new SerializableWrapper<int>(1)),
            new(OversizedKey(), new SerializableWrapper<int>(2)),
            new(new SizedKey(8, 3), new SerializableWrapper<int>(3)),
        ]);

        await bulk.Should().ThrowAsync<ArgumentException>(
            "the SSTable writer refuses one entry at flush, so the batch must be refused before anything is applied");
        var (_, firstFound) = await engine.TryGetValueAsync(new SizedKey(8, 1));
        var (_, lastFound) = await engine.TryGetValueAsync(new SizedKey(8, 3));
        firstFound.Should().BeFalse("a refused batch applies nothing");
        lastFound.Should().BeFalse("a refused batch applies nothing");

        await engine.FlushAsync();
    }

    /// <summary>
    /// A key whose serialized form is a declared number of zero bytes plus a one-byte tag, written
    /// in chunks so an oversized key costs no allocation of its own size. Keys compare by tag, then
    /// by length.
    /// </summary>
    public sealed class SizedKey : IStorageKey<SizedKey>
    {
        private static readonly byte[] s_zeros = new byte[64 * 1024];

        public SizedKey()
            : this(0, 0)
        {
        }

        public SizedKey(int payloadLength, byte tag)
        {
            PayloadLength = payloadLength;
            Tag = tag;
        }

        public int PayloadLength { get; }

        public byte Tag { get; }

        public void Serialize(Stream stream)
        {
            Span<byte> header = stackalloc byte[5];
            BinaryPrimitives.WriteInt32LittleEndian(header, PayloadLength);
            header[4] = Tag;
            stream.Write(header);
            for (var remaining = PayloadLength; remaining > 0; remaining -= s_zeros.Length)
            {
                stream.Write(s_zeros, 0, Math.Min(remaining, s_zeros.Length));
            }
        }

        public static SizedKey Deserialize(Stream stream)
        {
            Span<byte> header = stackalloc byte[5];
            stream.ReadExactly(header);
            var length = BinaryPrimitives.ReadInt32LittleEndian(header);
            var buffer = new byte[Math.Min(length, s_zeros.Length)];
            for (var remaining = length; remaining > 0; remaining -= buffer.Length)
            {
                stream.ReadExactly(buffer, 0, Math.Min(remaining, buffer.Length));
            }

            return new SizedKey(length, header[4]);
        }

        public int CompareTo(SizedKey? other)
            => other is null ? 1 : (Tag, PayloadLength).CompareTo((other.Tag, other.PayloadLength));

        public bool Equals(SizedKey? other) => other is not null && CompareTo(other) == 0;

        public override bool Equals(object? obj) => obj is SizedKey other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Tag, PayloadLength);
    }
}
