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
#nullable disable
using Boutquin.Storage.Domain.Interfaces.Compression;
using Boutquin.Storage.Infrastructure.Compression;

namespace Boutquin.Storage.Infrastructure.Tests.SortedStringTable;

/// <summary>
/// v2-format integration tests for <see cref="SortedStringTable{TKey, TValue}"/>:
/// multi-codec round-trip, bloom-from-footer reload, codec-mismatch resolution, large
/// multi-block correctness, and the property-based invariant that every key present at
/// write time resolves at read time (bloom no-false-negatives + binary-search correctness
/// across blocks).
/// </summary>
public sealed class SortedStringTableV2IntegrationTests : IDisposable
{
    private readonly string _testDirectory = Path.Combine(Path.GetTempPath(), $"SSTableV2Tests_{Guid.NewGuid():N}");

    public SortedStringTableV2IntegrationTests()
    {
        Directory.CreateDirectory(_testDirectory);
    }

    public static IEnumerable<object[]> AllCodecs()
    {
        yield return new object[] { (ICompressionCodec)new NoCompressionCodec(), "None" };
        yield return new object[] { (ICompressionCodec)new LZ4Codec(), "LZ4" };
        yield return new object[] { (ICompressionCodec)new GZipCodec(), "GZip" };
        yield return new object[] { (ICompressionCodec)new BrotliCodec(), "Brotli" };
    }

    [Theory]
    [MemberData(nameof(AllCodecs))]
    public void RoundTrip_AcrossAllShippedCodecs(ICompressionCodec codec, string label)
    {
        var fileName = $"codec_{label}.dat";
        var sst = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _testDirectory, fileName, codec: codec, blockSizeBytes: 512);

        var items = Enumerable.Range(0, 200)
            .Select(i => new KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>(
                new SerializableWrapper<int>(i),
                new SerializableWrapper<string>($"value_{i}_padded_with_extra_text_for_block_splits")))
            .ToList();
        sst.Write(items.WithZeroSeqno());

        // Every key present must resolve on read.
        foreach (var kv in items)
        {
            sst.TryGetValue(kv.Key, out var v).Should().BeTrue($"key {kv.Key.Value} must be found");
            v.Value.Should().Be(kv.Value.Value);
        }

        // Absent keys must return false.
        sst.TryGetValue(new SerializableWrapper<int>(-1), out _).Should().BeFalse();
        sst.TryGetValue(new SerializableWrapper<int>(9999), out _).Should().BeFalse();
    }

    [Fact]
    public void Reopen_LoadsBloomAndBlockIndex_FromFooter()
    {
        // After the writer closes the file, reopening with a fresh SST instance must
        // recover the bloom + block index from the footer — no rebuild, no degraded reads
        // (DDIA 2e pp. 122–123).
        var fileName = "reopen.dat";
        {
            var writer = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
                _testDirectory, fileName, codec: new LZ4Codec());
            writer.Write(Enumerable.Range(0, 50)
                .Select(i => (
                    new SerializableWrapper<int>(i),
                    new SerializableWrapper<string>($"v{i}"),
                    (ulong)(i + 1)))
                .ToList());
        }

        var reader = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _testDirectory, fileName);
        reader.GetEntryCount().Should().Be(50);

        // Spot-check several keys to exercise the reloaded footer + bloom path.
        reader.TryGetValue(new SerializableWrapper<int>(0), out var v0).Should().BeTrue();
        v0.Value.Should().Be("v0");
        reader.TryGetValue(new SerializableWrapper<int>(25), out var v25).Should().BeTrue();
        v25.Value.Should().Be("v25");
        reader.TryGetValue(new SerializableWrapper<int>(49), out var v49).Should().BeTrue();
        v49.Value.Should().Be("v49");
        reader.TryGetValue(new SerializableWrapper<int>(100), out _).Should().BeFalse();
    }

    [Fact]
    public void CrossCodec_Reopen_ResolvesViaRegistry()
    {
        // Writer uses Brotli; reader constructs with default codec (LZ4). The registry
        // resolves the persisted Brotli CodecId and reads decompress correctly. Writer's
        // codec choice is preserved in the footer; reader's default is used only when
        // producing new writes against the same instance.
        var fileName = "crosscodec.dat";
        {
            var writer = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
                _testDirectory, fileName, codec: new BrotliCodec());
            writer.Write(
            [
                (new SerializableWrapper<int>(1), new SerializableWrapper<string>("one"), 0UL),
                (new SerializableWrapper<int>(2), new SerializableWrapper<string>("two"), 0UL),
            ]);
        }

        var reader = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _testDirectory, fileName /* default codec = LZ4 */);
        reader.TryGetValue(new SerializableWrapper<int>(1), out var v).Should().BeTrue();
        v.Value.Should().Be("one");
    }

    [Fact]
    public void UnregisteredCodec_Reopen_RaisesUnsupportedCompressionCodecException()
    {
        // Writer uses Brotli; reader's custom registry only knows LZ4. Opening the file
        // must surface UnsupportedCompressionCodecException.
        var fileName = "unregistered.dat";
        {
            var writer = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
                _testDirectory, fileName, codec: new BrotliCodec());
            writer.Write(
            [
                (new SerializableWrapper<int>(1), new SerializableWrapper<string>("one"), 0UL),
            ]);
        }

        // Craft a registry that only knows LZ4 (CodecId 0x01). Reading a Brotli-written
        // file must throw. The registry is used at open time to resolve the footer's
        // codec id — a missing registration is a hard failure, not a silent fallback.
        var partial = new LeanRegistry();
        partial.Register(new LZ4Codec());

        var act = () => new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _testDirectory, fileName, codec: new LZ4Codec(), codecRegistry: partial);
        act.Should().Throw<UnsupportedCompressionCodecException>()
            .Which.CodecId.Should().Be(0x03);
    }

    [Fact]
    public void LargeDataset_MultiBlock_AllKeysResolve()
    {
        // Force many blocks by setting a small block size. Exercises binary search across
        // the block index and the within-block linear scan.
        var fileName = "large.dat";
        var sst = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _testDirectory, fileName, codec: new NoCompressionCodec(), blockSizeBytes: 256);

        var items = Enumerable.Range(0, 2000)
            .Select(i => new KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>(
                new SerializableWrapper<int>(i),
                new SerializableWrapper<string>($"v-{i:D5}")))
            .ToList();
        sst.Write(items.WithZeroSeqno());

        // Verify 100 randomly chosen keys + endpoints.
        var rng = new Random(42);
        for (var probe = 0; probe < 100; probe++)
        {
            var k = rng.Next(0, 2000);
            sst.TryGetValue(new SerializableWrapper<int>(k), out var v).Should().BeTrue(
                $"randomly probed key {k} must be present");
            v.Value.Should().Be($"v-{k:D5}");
        }
        sst.TryGetValue(new SerializableWrapper<int>(0), out _).Should().BeTrue();
        sst.TryGetValue(new SerializableWrapper<int>(1999), out _).Should().BeTrue();
    }

    [Fact]
    public void EmptySst_RoundTrips()
    {
        var sst = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _testDirectory, "empty.dat");
        sst.Write(Array.Empty<(SerializableWrapper<int>, SerializableWrapper<string>, ulong)>());
        sst.GetEntryCount().Should().Be(0);
        sst.TryGetValue(new SerializableWrapper<int>(1), out _).Should().BeFalse();

        // Reopen path.
        var reopened = new SortedStringTable<SerializableWrapper<int>, SerializableWrapper<string>>(
            _testDirectory, "empty.dat");
        reopened.GetEntryCount().Should().Be(0);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDirectory))
            {
                Directory.Delete(_testDirectory, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    /// <summary>
    /// A minimal registry used to prove that an unregistered codec surfaces an exception
    /// at open time (see <see cref="UnregisteredCodec_Reopen_RaisesUnsupportedCompressionCodecException"/>).
    /// </summary>
    private sealed class LeanRegistry : ICompressionCodecRegistry
    {
        private readonly Dictionary<byte, ICompressionCodec> _map = [];
        public void Register(ICompressionCodec codec) => _map[codec.CodecId] = codec;
        public ICompressionCodec Resolve(byte codecId) => _map.TryGetValue(codecId, out var c) ? c : null;
        public IReadOnlyCollection<ICompressionCodec> Registered => _map.Values;
    }
}
