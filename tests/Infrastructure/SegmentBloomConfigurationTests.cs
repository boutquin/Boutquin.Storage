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
using Boutquin.Storage.Domain.ValueObjects.LsmTree;
using Boutquin.Storage.Domain.ValueObjects.TimeSeries;
using Boutquin.Storage.Infrastructure.Tests.TimeSeries.Columnar;
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar;

namespace Boutquin.Storage.Infrastructure.Tests;

public sealed partial class SegmentBloomConfigurationTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "segment-bloom-" + Guid.NewGuid().ToString("N"));

    public SegmentBloomConfigurationTests() => Directory.CreateDirectory(_folder);

    [Boutquin.Storage.Domain.Attributes.Key]
    public partial record struct DatasetKey([property: Boutquin.Storage.Domain.Attributes.Tag(1)] string Provider);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ColumnarCompaction_GeneratedDatasetKeyWithUnknownBounds_ReopensAllRows(bool leveled)
    {
        var codec = new SpyCodec<TimeSeriesKey<DatasetKey>>();
        var format = new ColumnarSegmentFormat<DatasetKey, MarketDataBar>(codec.Options(), new CompressedColumnarSegmentFormat<MarketDataBar>());
        var options = new LsmStorageEngineOptions<TimeSeriesKey<DatasetKey>, MarketDataBar>(
            MemTableCapacity: 1000, SegmentFolder: _folder, SegmentPrefix: "generated", SegmentFormat: format,
            CompactionStrategy: leveled
                ? new LeveledCompactionStrategy<TimeSeriesKey<DatasetKey>>(level0Threshold: 4)
                : new FullCompactionStrategy<TimeSeriesKey<DatasetKey>>(threshold: 4));
        await using (var engine = new LsmStorageEngine<TimeSeriesKey<DatasetKey>, MarketDataBar>(options))
        {
            for (var day = 1; day <= 4; day++)
            {
                var date = new DateOnly(2026, 10, day);
                await engine.SetAsync(new(new DatasetKey("provider"), date), new MarketDataBar { Date = date, Close = day }).ConfigureAwait(true);
                await engine.FlushAsync().ConfigureAwait(true);
            }
        }
        codec.Creates.Should().Be(5);
        var creates = codec.Creates;
        await using var reopened = await LsmStorageEngine<TimeSeriesKey<DatasetKey>, MarketDataBar>.OpenAsync(options).ConfigureAwait(true);
        for (var day = 1; day <= 4; day++)
        {
            var result = await reopened.TryGetValueAsync(new(new DatasetKey("provider"), new DateOnly(2026, 10, day))).ConfigureAwait(true);
            result.Found.Should().BeTrue();
            result.Value.Close.Should().Be(day);
        }
        codec.Creates.Should().Be(creates);
    }

    [Theory]
    [InlineData(100, 0.01)]
    [InlineData(500, 0.001)]
    public async Task NativeOptions_ControlPhysicalFooterAndSurviveReopen(int expectedItems, double probability)
    {
        var options = new SegmentBloomFilterOptions<SerializableWrapper<int>>(expectedItems: expectedItems, falsePositiveProbability: probability);
        var format = new SstSegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>>(options);
        var reader = await format.WriteAsync(_folder, "a.sst", Entries()).ConfigureAwait(true);
        var footer = ReadFooter("a.sst");
        var expectedBits = (int)Math.Ceiling(-expectedItems * Math.Log(probability) / Math.Pow(Math.Log(2), 2));
        footer.BloomBitsCount.Should().Be(expectedBits);
        footer.BloomHashCount.Should().Be((int)Math.Round(expectedBits / (double)expectedItems * Math.Log(2)));
        (reader as IDisposable)?.Dispose();
        var reopened = format.Open(_folder, "a.sst");
        (await reopened.TryGetBlockEntryAsync(new(10)).ConfigureAwait(true)).Found.Should().BeTrue();
        (await reopened.TryGetBlockEntryAsync(new(20)).ConfigureAwait(true)).Found.Should().BeFalse();
        (reopened as IDisposable)?.Dispose();
    }

    [Fact]
    public async Task DisabledFilter_PersistsNoBitsButStillFindsPresentKeys()
    {
        var format = new SstSegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>>(
            new SegmentBloomFilterOptions<SerializableWrapper<int>>(enabled: false));
        var reader = await format.WriteAsync(_folder, "off.sst", Entries()).ConfigureAwait(true);
        ReadFooter("off.sst").BloomBitsCount.Should().Be(0);
        (await reader.TryGetBlockEntryAsync(new(30)).ConfigureAwait(true)).Found.Should().BeTrue();
        (reader as IDisposable)?.Dispose();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Options_RejectInvalidCapacity(int capacity)
    {
        var create = () => new SegmentBloomFilterOptions<SerializableWrapper<int>>(expectedItems: capacity);
        create.Should().Throw<ArgumentOutOfRangeException>();
    }

    private static SegmentWriteEntry<SerializableWrapper<int>, SerializableWrapper<string>>[] Entries() =>
    [
        SegmentWriteEntry<SerializableWrapper<int>, SerializableWrapper<string>>.Inline(new(10), new("ten"), 1),
        SegmentWriteEntry<SerializableWrapper<int>, SerializableWrapper<string>>.Inline(new(30), new("thirty"), 2)
    ];

    [Fact]
    public async Task CustomRowFilter_ReopensFromStateWithoutFactoryOrAdd_AndPreservesSnapshotsAndTombstones()
    {
        var codec = new SpyCodec<SerializableWrapper<int>>();
        var options = codec.Options();
        var format = new SstSegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>>(options);
        var reader = await format.WriteAsync(_folder, "custom.sst", Entries(), new Dictionary<SerializableWrapper<int>, ulong> { [new(20)] = 7 }).ConfigureAwait(true);
        codec.Creates.Should().Be(1);
        codec.Adds.Should().Be(2);
        ReadFooter("custom.sst").BloomState.IsEmpty.Should().BeFalse();
        (reader as IDisposable)!.Dispose();
        codec.Disposes.Should().Be(2);
        var readsAtSeal = codec.Reads;
        codec.Probes = 0;
        var reopened = format.Open(_folder, "custom.sst");
        codec.Reads.Should().Be(readsAtSeal + 1);
        codec.Creates.Should().Be(1);
        codec.Adds.Should().Be(2);
        (await reopened.TryGetBlockEntryAsync(new(20)).ConfigureAwait(true)).Found.Should().BeFalse();
        codec.Probes.Should().Be(1);
        (await reopened.TryGetBlockEntryAsync(new(30), 1).ConfigureAwait(true)).Found.Should().BeFalse();
        (await reopened.TryGetBlockEntryAsync(new(30), 2).ConfigureAwait(true)).Found.Should().BeTrue();
        reopened.GetTombstoneSeqnos()[new(20)].Should().Be(7);
        var rows = new List<int>();
        await foreach (var entry in reopened.StreamBlockEntriesAsync().ConfigureAwait(true))
        {
            rows.Add(entry.Key.Value);
        }
        rows.Should().Equal(10, 30);
        await reopened.ClearAsync().ConfigureAwait(true);
        codec.Disposes.Should().Be(3);
    }

    [Fact]
    public async Task CustomColumnarFilter_PersistsAndDoesNotPruneRanges()
    {
        var codec = new SpyCodec<TimeSeriesKey<SerializableWrapper<string>>>();
        var options = codec.Options();
        var format = new ColumnarSegmentFormat<SerializableWrapper<string>, MarketDataBar>(options, new CompressedColumnarSegmentFormat<MarketDataBar>());
        var first = new DateOnly(2024, 1, 1);
        TimeSeriesKey<SerializableWrapper<string>> Key(int day) => new(new("A"), first.AddDays(day));
        var entries = new[] { 0, 2 }.Select(day => SegmentWriteEntry<TimeSeriesKey<SerializableWrapper<string>>, MarketDataBar>.Inline(
            Key(day), new MarketDataBar { Date = first.AddDays(day), Close = 100, SplitRatio = 1 }, (ulong)day + 1)).ToArray();
        var reader = await format.WriteAsync(_folder, "custom.bsct", entries).ConfigureAwait(true);
        codec.Creates.Should().Be(1);
        codec.Adds.Should().Be(2);
        codec.Disposes.Should().Be(2);
        var readsAtSeal = codec.Reads;
        codec.Probes = 0;
        (reader as IDisposable)!.Dispose();
        var reopened = format.Open(_folder, "custom.bsct");
        (await reopened.TryGetBlockEntryAsync(Key(1)).ConfigureAwait(true)).Found.Should().BeFalse();
        codec.Probes.Should().Be(1);
        codec.Reads.Should().Be(readsAtSeal + 1);
        codec.Creates.Should().Be(1);
        codec.Adds.Should().Be(2);
        (await reopened.TryGetBlockEntryAsync(Key(2)).ConfigureAwait(true)).Found.Should().BeTrue();
        var rows = new List<MarketDataBar>();
        await foreach (var entry in reopened.StreamBlockEntriesAsync().ConfigureAwait(true))
        {
            rows.Add(entry.InlineValue!);
        }
        rows.Select(x => x.Date).Should().Equal(first, first.AddDays(2));
        (reopened as IDisposable)!.Dispose();
        codec.Disposes.Should().Be(3);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CustomRowFilter_MissingOrWrongCodecFailsClosed(bool wrongVersion)
    {
        var codec = new SpyCodec<SerializableWrapper<int>>();
        var format = new SstSegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>>(codec.Options());
        var reader = await format.WriteAsync(_folder, "codec.sst", Entries()).ConfigureAwait(true);
        (reader as IDisposable)!.Dispose();
        var other = wrongVersion
            ? new SstSegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>>(new SpyCodec<SerializableWrapper<int>>(2).Options())
            : new SstSegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>>();
        var open = () => other.Open(_folder, "codec.sst");
        open.Should().Throw<Boutquin.Storage.Domain.Exceptions.UnsupportedBloomFilterCodecException>();
    }

    [Fact]
    public async Task CodecFailure_DisposesFactoryFilter()
    {
        var codec = new SpyCodec<SerializableWrapper<int>> { FailSerialize = true };
        var format = new SstSegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>>(codec.Options());
        var write = () => format.WriteAsync(_folder, "failed.sst", Entries());
        await write.Should().ThrowAsync<InvalidDataException>().ConfigureAwait(true);
        codec.Disposes.Should().Be(1);
        File.Exists(Path.Combine(_folder, "failed.sst")).Should().BeFalse();
    }

    private sealed class SpyCodec<TKey>(int version = 1) : IBloomFilterStateCodec<TKey> where TKey : ISerializable<TKey>
    {
        public int Creates, Reads, Adds, Disposes, Probes;
        public bool FailSerialize { get; init; }
        public bool LoseKeysOnRestore { get; init; }
        public string CodecId => "test.exact-membership";
        public int Version => version;
        public SegmentBloomFilterOptions<TKey> Options() => new(factory: (_, _) => { Creates++; return new Filter(this, []); }, stateCodec: this);
        public byte[] Serialize(IBloomFilter<TKey> filter)
        {
            if (FailSerialize) { throw new InvalidDataException("Injected codec failure."); }
            return Encoding.UTF8.GetBytes(string.Join('\n', ((Filter)filter).Keys.Order(StringComparer.Ordinal)));
        }
        public IBloomFilter<TKey> Deserialize(ReadOnlyMemory<byte> state)
        {
            Reads++;
            var text = new UTF8Encoding(false, true).GetString(state.Span);
            var keys = text.Length == 0 ? [] : text.Split('\n');
            foreach (var key in keys) { _ = Convert.FromBase64String(key); }
            return new Filter(this, LoseKeysOnRestore ? [] : new HashSet<string>(keys, StringComparer.Ordinal));
        }
        private sealed class Filter(SpyCodec<TKey> owner, HashSet<string> keys) : IBloomFilter<TKey>
        {
            public HashSet<string> Keys { get; } = keys;
            private bool _disposed;
            private static string Encode(TKey key) { using var s = new MemoryStream(); key.Serialize(s); return Convert.ToBase64String(s.ToArray()); }
            public void Add(TKey item) { ObjectDisposedException.ThrowIf(_disposed, this); owner.Adds++; Keys.Add(Encode(item)); }
            public bool Contains(TKey item) { ObjectDisposedException.ThrowIf(_disposed, this); owner.Probes++; return Keys.Contains(Encode(item)); }
            public void Clear() => Keys.Clear();
            public void Dispose() { if (!_disposed) { _disposed = true; owner.Disposes++; } }
        }
    }

    [Fact]
    public async Task NativeSizing_IsAppliedToFlushAndCompaction()
    {
        var options = new SegmentBloomFilterOptions<SerializableWrapper<int>>(expectedItems: 1000, falsePositiveProbability: 0.001);
        var format = new SstSegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>>(options);
        await using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            new LsmStorageEngineOptions<SerializableWrapper<int>, SerializableWrapper<string>>(
                MemTableCapacity: 1000, SegmentFolder: _folder, SegmentPrefix: "seg", SegmentFormat: format, CompactionStrategy: null));
        await engine.SetAsync(new(10), new("ten")).ConfigureAwait(true);
        await engine.FlushAsync().ConfigureAwait(true);
        await engine.SetAsync(new(30), new("thirty")).ConfigureAwait(true);
        await engine.FlushAsync().ConfigureAwait(true);
        var expected = (int)Math.Ceiling(-1000 * Math.Log(0.001) / Math.Pow(Math.Log(2), 2));
        foreach (var path in Directory.GetFiles(_folder, "seg_*.sst"))
        {
            ReadFooter(Path.GetFileName(path)).BloomBitsCount.Should().Be(expected);
        }
        await engine.CompactAsync().ConfigureAwait(true);
        var compacted = Directory.GetFiles(_folder, "seg_*.sst");
        compacted.Should().ContainSingle();
        ReadFooter(Path.GetFileName(compacted[0])).BloomBitsCount.Should().Be(expected);
        (await engine.TryGetValueAsync(new(10)).ConfigureAwait(true)).Found.Should().BeTrue();
        (await engine.TryGetValueAsync(new(30)).ConfigureAwait(true)).Found.Should().BeTrue();
    }

    [Fact]
    public async Task DecoderOnlyDisabledConfiguration_ReopensCustomStateWithoutFactory()
    {
        var codec = new SpyCodec<SerializableWrapper<int>>();
        var writer = new SstSegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>>(codec.Options());
        var reader = await writer.WriteAsync(_folder, "decode.sst", Entries()).ConfigureAwait(true);
        (reader as IDisposable)!.Dispose();
        var decoding = new SegmentBloomFilterOptions<SerializableWrapper<int>>(enabled: false, stateCodec: codec);
        var format = new SstSegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>>(decoding);
        var reopened = format.Open(_folder, "decode.sst");
        (await reopened.TryGetBlockEntryAsync(new(10)).ConfigureAwait(true)).Found.Should().BeTrue();
        codec.Creates.Should().Be(1);
        codec.Adds.Should().Be(2);
        (reopened as IDisposable)!.Dispose();
        var disabled = await format.WriteAsync(_folder, "disabled.sst", Entries()).ConfigureAwait(true);
        ReadFooter("disabled.sst").BloomBitsCount.Should().Be(0);
        codec.Creates.Should().Be(1);
        (disabled as IDisposable)!.Dispose();
    }

    [Fact]
    public async Task ForgedCustomStateLength_IsRejectedEvenWithValidFooterCrc()
    {
        var codec = new SpyCodec<SerializableWrapper<int>>();
        var format = new SstSegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>>(codec.Options());
        var reader = await format.WriteAsync(_folder, "forged.sst", Entries()).ConfigureAwait(true);
        (reader as IDisposable)!.Dispose();
        var footer = ReadFooter("forged.sst");
        var bad = footer.BloomState.ToArray();
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bad.AsSpan(5), int.MaxValue);
        var bytes = File.ReadAllBytes(Path.Combine(_folder, "forged.sst"));
        var oldFooterSize = footer.Serialize().Length;
        File.WriteAllBytes(Path.Combine(_folder, "forged.sst"), [.. bytes.AsSpan(0, bytes.Length - oldFooterSize), .. (footer with { BloomState = bad }).Serialize()]);
        var open = () => format.Open(_folder, "forged.sst");
        open.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public async Task ReusedFactoryInstance_IsRejectedWithoutDisposingFirstSegmentFilter()
    {
        var codec = new SpyCodec<SerializableWrapper<int>>();
        var shared = codec.Options().Factory!(10, 0.01);
        var options = new SegmentBloomFilterOptions<SerializableWrapper<int>>(factory: (_, _) => shared, stateCodec: codec);
        var format = new SstSegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>>(options);
        var first = await format.WriteAsync(_folder, "first.sst", Entries()).ConfigureAwait(true);
        var write = () => format.WriteAsync(_folder, "second.sst", Entries());
        await write.Should().ThrowAsync<InvalidOperationException>().ConfigureAwait(true);
        File.Exists(Path.Combine(_folder, "second.sst")).Should().BeFalse();
        (await first.TryGetBlockEntryAsync(new(10)).ConfigureAwait(true)).Found.Should().BeTrue();
        (first as IDisposable)!.Dispose();
    }

    [Fact]
    public async Task EngineDisposal_ReleasesCustomFilters_AndMissingCodecCannotExcludeValidData()
    {
        var codec = new SpyCodec<SerializableWrapper<int>>();
        var options = new LsmStorageEngineOptions<SerializableWrapper<int>, SerializableWrapper<string>>(
            MemTableCapacity: 1000, SegmentFolder: _folder, SegmentPrefix: "seg",
            SegmentFormat: new SstSegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>>(codec.Options()), CompactionStrategy: null);
        await using (var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(options))
        {
            await engine.SetAsync(new(10), new("ten")).ConfigureAwait(true);
            await engine.FlushAsync().ConfigureAwait(true);
            await engine.SetAsync(new(30), new("thirty")).ConfigureAwait(true);
            await engine.FlushAsync().ConfigureAwait(true);
            await engine.CompactAsync().ConfigureAwait(true);
        }
        codec.Creates.Should().Be(3);
        codec.Disposes.Should().Be(codec.Creates + codec.Reads);
        var exclusions = 0;
        var missing = options with
        {
            SegmentFormat = new SstSegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>>(),
            CorruptSegmentRecovery = CorruptSegmentRecoveryDisposition.ExcludeAndSurface,
            OnCorruptSegmentExcluded = _ => exclusions++
        };
        var open = () => LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>.OpenAsync(missing);
        await open.Should().ThrowAsync<Boutquin.Storage.Domain.Exceptions.UnsupportedBloomFilterCodecException>().ConfigureAwait(true);
        exclusions.Should().Be(0);
        await using var recovered = await LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>.OpenAsync(options).ConfigureAwait(true);
        (await recovered.TryGetValueAsync(new(10)).ConfigureAwait(true)).Found.Should().BeTrue();
        (await recovered.TryGetValueAsync(new(30)).ConfigureAwait(true)).Found.Should().BeTrue();
    }

    [Fact]
    public async Task CodecLosingMembership_CannotPublishAFalseNegativeFilter()
    {
        var codec = new SpyCodec<SerializableWrapper<int>> { LoseKeysOnRestore = true };
        var format = new SstSegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>>(codec.Options());
        var write = () => format.WriteAsync(_folder, "lost.sst", Entries());
        await write.Should().ThrowAsync<InvalidDataException>().ConfigureAwait(true);
        File.Exists(Path.Combine(_folder, "lost.sst")).Should().BeFalse();
        codec.Disposes.Should().Be(2);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-0.01)]
    [InlineData(1e-30)]
    public void NativeOptions_RejectInvalidProbabilityOrExcessiveHashCount(double probability)
    {
        var construct = () => new SegmentBloomFilterOptions<SerializableWrapper<int>>(falsePositiveProbability: probability);
        construct.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void NativeOptions_RejectExcessiveBitCapacity()
    {
        var construct = () => new SegmentBloomFilterOptions<SerializableWrapper<int>>(expectedItems: int.MaxValue);
        construct.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task ColumnarNativeOptions_ControlPersistedDescriptor_AndDisabledModeOmitsIt()
    {
        var first = new DateOnly(2024, 1, 1);
        var entries = new[] { 0, 2 }.Select(day => SegmentWriteEntry<TimeSeriesKey<SerializableWrapper<string>>, MarketDataBar>.Inline(
            new(new("A"), first.AddDays(day)), new MarketDataBar { Date = first.AddDays(day), Close = 100, SplitRatio = 1 }, (ulong)day + 1)).ToArray();
        var options = new SegmentBloomFilterOptions<TimeSeriesKey<SerializableWrapper<string>>>(expectedItems: 100, falsePositiveProbability: 0.001);
        var format = new ColumnarSegmentFormat<SerializableWrapper<string>, MarketDataBar>(options, new CompressedColumnarSegmentFormat<MarketDataBar>());
        var reader = await format.WriteAsync(_folder, "native.bsct", entries).ConfigureAwait(true);
        var bytes = File.ReadAllBytes(Path.Combine(_folder, "native.bsct"));
        var offset = bytes.AsSpan().LastIndexOf("BLM1"u8);
        offset.Should().BeGreaterThan(0);
        var bits = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 5, 4));
        bits.Should().Be((int)Math.Ceiling(-100 * Math.Log(0.001) / Math.Pow(Math.Log(2), 2)));
        (reader as IDisposable)!.Dispose();
        var reopen = format.Open(_folder, "native.bsct");
        (await reopen.TryGetBlockEntryAsync(new(new("A"), first.AddDays(2))).ConfigureAwait(true)).Found.Should().BeTrue();
        (reopen as IDisposable)!.Dispose();
        var disabled = new ColumnarSegmentFormat<SerializableWrapper<string>, MarketDataBar>(
            new SegmentBloomFilterOptions<TimeSeriesKey<SerializableWrapper<string>>>(enabled: false), new CompressedColumnarSegmentFormat<MarketDataBar>());
        var off = await disabled.WriteAsync(_folder, "off.bsct", entries).ConfigureAwait(true);
        File.ReadAllBytes(Path.Combine(_folder, "off.bsct")).AsSpan().LastIndexOf("BLM1"u8).Should().Be(-1);
        (off as IDisposable)!.Dispose();
    }

    [Fact]
    public async Task ColumnarMissingCodec_IsConfigurationFailureDuringMetadataLoad()
    {
        var codec = new SpyCodec<TimeSeriesKey<SerializableWrapper<string>>>();
        var format = new ColumnarSegmentFormat<SerializableWrapper<string>, MarketDataBar>(codec.Options(), new CompressedColumnarSegmentFormat<MarketDataBar>());
        var reader = await format.WriteAsync(_folder, "codec.bsct", []).ConfigureAwait(true);
        (reader as IDisposable)!.Dispose();
        var noCodec = new ColumnarSegmentFormat<SerializableWrapper<string>, MarketDataBar>(new CompressedColumnarSegmentFormat<MarketDataBar>());
        var reopened = noCodec.Open(_folder, "codec.bsct");
        var read = () => reopened.MaxSequenceNumber;
        read.Should().Throw<Boutquin.Storage.Domain.Exceptions.UnsupportedBloomFilterCodecException>();
        (reopened as IDisposable)!.Dispose();
    }

    private SsTableFooter ReadFooter(string name)
    {
        SsTableFooter.TryReadFromEnd(File.ReadAllBytes(Path.Combine(_folder, name)), out var footer).Should().BeTrue();
        return footer!;
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);
}
