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

using Boutquin.Storage.Domain.Interfaces.TimeSeries;
using Boutquin.Storage.Domain.ValueObjects.TimeSeries;
using Boutquin.Storage.Infrastructure.Tests.TimeSeries.Columnar;
using Boutquin.Storage.Infrastructure.TimeSeries;
using CiKey = Boutquin.Storage.Infrastructure.Tests.DatasetKeyIdentityAndSegmentNameContractTests.CiKey;

namespace Boutquin.Storage.Infrastructure.Tests;

public sealed class DatasetKeyRoundTripIdentityContractTests : IDisposable
{
    private static readonly DateOnly s_date = new(2024, 1, 1);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"dataset-roundtrip-{Guid.NewGuid():N}");

    public DatasetKeyRoundTripIdentityContractTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static MarketDataBar Bar(DateOnly date, decimal close) => new() { Date = date, Close = close };

    private static async Task<List<T>> RowsAsync<T>(IAsyncEnumerable<T> source)
    {
        var rows = new List<T>();
        await foreach (var row in source.ConfigureAwait(true))
        {
            rows.Add(row);
        }
        return rows;
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("row")]
    [InlineData("columnar")]
    public async Task LossyDataset_IsRejectedWithoutAliasingNeighborOrChangingCoverage(string kind)
    {
        var (store, engine) = await OpenAsync(kind).ConfigureAwait(true);
        await using (engine)
        {
            var valid = new CiKey("\uFFFD");
            var invalid = new CiKey("\uD800");
            await store.AppendAsync(valid, [Bar(s_date.AddDays(30), 2m)]).ConfigureAwait(true);
            await store.AppendAsync(new CiKey("Other"), [Bar(s_date, 3m)]).ConfigureAwait(true);

            Func<Task> append = () => store.AppendAsync(invalid, [Bar(s_date, 1m)]);
            await append.Should().ThrowAsync<ArgumentException>().ConfigureAwait(true);
            Func<Task> delete = () => store.DeleteRangeAsync(invalid, s_date, s_date.AddDays(30));
            await delete.Should().ThrowAsync<ArgumentException>().ConfigureAwait(true);
            Func<Task> lookup = async () => _ = await RowsAsync(store.GetRangeAsync(invalid, s_date, s_date.AddDays(30))).ConfigureAwait(true);
            await lookup.Should().ThrowAsync<ArgumentException>().ConfigureAwait(true);
            (await RowsAsync(store.GetRangeAsync(valid, s_date, s_date.AddDays(30))).ConfigureAwait(true))
                .Select(r => r.Close).Should().Equal(2m);
            (await store.GetCoverageAsync(valid).ConfigureAwait(true)).Should().Be(new DateRange(s_date.AddDays(30), s_date.AddDays(30)));
            (await store.GetGapsAsync(valid, new DateRange(s_date, s_date.AddDays(30))).ConfigureAwait(true))
                .Should().Equal(new DateRange(s_date, s_date.AddDays(29)));
            if (engine is not null)
            {
                await engine.FlushAsync().ConfigureAwait(true);
                await engine.CompactAsync().ConfigureAwait(true);
            }
        }

        if (kind == "memory")
        {
            return;
        }
        var (reopened, reopenedEngine) = await OpenAsync(kind).ConfigureAwait(true);
        await using (reopenedEngine)
        {
            (await RowsAsync(reopened.GetRangeAsync(new CiKey("\uFFFD"), s_date, s_date.AddDays(30))).ConfigureAwait(true))
                .Select(r => r.Close).Should().Equal(2m);
            (await RowsAsync(reopened.GetRangeAsync(new CiKey("Other"), s_date, s_date)).ConfigureAwait(true))
                .Select(r => r.Close).Should().Equal(3m);
        }
    }

    [Theory]
    [InlineData("row")]
    [InlineData("columnar")]
    public async Task EngineAdmission_RejectsLossyDatasetInBulkAndTombstones(string kind)
    {
        var (_, openedEngine) = await OpenAsync(kind).ConfigureAwait(true);
        await using var engine = openedEngine!;
        var valid = new TimeSeriesKey<CiKey>(new CiKey("\uFFFD"), s_date);
        var invalid = new TimeSeriesKey<CiKey>(new CiKey("\uD800"), s_date);
        var untouched = new TimeSeriesKey<CiKey>(new CiKey("untouched"), s_date);
        await engine.SetAsync(valid, Bar(s_date, 2m)).ConfigureAwait(true);
        Func<Task> bulk = () => engine.SetBulkAsync([
            new KeyValuePair<TimeSeriesKey<CiKey>, MarketDataBar>(untouched, Bar(s_date, 3m)),
            new KeyValuePair<TimeSeriesKey<CiKey>, MarketDataBar>(invalid, Bar(s_date, 1m)),
        ]);
        await bulk.Should().ThrowAsync<ArgumentException>().ConfigureAwait(true);
        (await engine.TryGetValueAsync(untouched).ConfigureAwait(true)).Found.Should().BeFalse();
        Func<Task> delete = () => engine.RemoveAsync(invalid);
        await delete.Should().ThrowAsync<ArgumentException>().ConfigureAwait(true);
        await engine.FlushAsync().ConfigureAwait(true);
        var (value, found) = await engine.TryGetValueAsync(valid).ConfigureAwait(true);
        found.Should().BeTrue();
        value.Close.Should().Be(2m);
    }

    private async Task<(ITimeSeriesStore<CiKey, MarketDataBar> Store, LsmStorageEngine<TimeSeriesKey<CiKey>, MarketDataBar>? Engine)> OpenAsync(string kind)
    {
        if (kind == "memory")
        {
            return (new InMemoryTimeSeriesStore<CiKey, MarketDataBar>(), null);
        }
        if (kind == "columnar")
        {
            var stack = await ColumnarTimeSeriesEngineBuilder.BuildAsync<CiKey, MarketDataBar>(
                _dir, memTableCapacity: 100).ConfigureAwait(true);
            return (stack.Store, stack.InnerEngine);
        }
        var engine = LsmEngineFactory.Build<TimeSeriesKey<CiKey>, MarketDataBar>(
            memTableCapacity: 100, segmentFolder: _dir, segmentPrefix: "seg");
        return (new LsmTimeSeriesStore<CiKey, MarketDataBar>(new LsmTimeSeriesStorageEngine<CiKey, MarketDataBar>(engine),
            new InMemoryDatasetCoverageIndex<CiKey>()), engine);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CoverageIndexes_RejectLossyDatasetAndKeepValidCoverage(bool fileBacked)
    {
        IDatasetCoverageIndex<CiKey> index = fileBacked
            ? new FileDatasetCoverageIndex<CiKey>(Path.Combine(_dir, "coverage.json"))
            : new InMemoryDatasetCoverageIndex<CiKey>();
        var valid = new CiKey("\uFFFD");
        var invalid = new CiKey("\uD800");
        await index.RegisterAsync(valid, s_date, s_date).ConfigureAwait(true);
        Func<Task> register = () => index.RegisterAsync(invalid, s_date.AddDays(30), s_date.AddDays(30));
        await register.Should().ThrowAsync<ArgumentException>().ConfigureAwait(true);
        Func<Task> replace = () => index.SetAsync(invalid, null);
        await replace.Should().ThrowAsync<ArgumentException>().ConfigureAwait(true);
        Func<Task> lookup = async () => _ = await index.GetAsync(invalid).ConfigureAwait(true);
        await lookup.Should().ThrowAsync<ArgumentException>().ConfigureAwait(true);
        (await index.GetAsync(valid).ConfigureAwait(true)).Should().Be(new DateRange(s_date, s_date));
        if (fileBacked)
        {
            var reopened = new FileDatasetCoverageIndex<CiKey>(Path.Combine(_dir, "coverage.json"));
            (await reopened.GetAsync(valid).ConfigureAwait(true)).Should().Be(new DateRange(s_date, s_date));
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void CompositeKey_RejectsComparisonLoss_ByteDrift_AndPartialConsumption(int failure)
    {
        var key = new TimeSeriesKey<MalformedDataset>(new MalformedDataset((byte)failure, 1, 0), s_date);
        using var bytes = new MemoryStream();
        Action encode = () => key.Serialize(bytes);
        encode.Should().Throw<ArgumentException>();
        bytes.Length.Should().Be(0, "validation must finish before emitting any composite-key bytes");
    }

    [Fact]
    public void CompositeKey_DatasetDecoderResourceFailure_IsNotReportedAsInvalidInput()
    {
        var key = new TimeSeriesKey<MalformedDataset>(new MalformedDataset(4, 1, 0), s_date);
        using var bytes = new MemoryStream();
        Action encode = () => key.Serialize(bytes);
        encode.Should().Throw<OutOfMemoryException>();
        bytes.Length.Should().Be(0);
    }

    private sealed class MalformedDataset(byte mode, byte identity, byte variant) : ISerializable<MalformedDataset>, IComparable<MalformedDataset>
    {
        public MalformedDataset() : this(0, 0, 0) { }
        public int CompareTo(MalformedDataset? other) => other is null ? 1 : identity.CompareTo(other.Identity);
        private byte Identity => identity;
        public void Serialize(Stream stream)
        {
            stream.WriteByte(mode);
            stream.WriteByte(mode == 1 ? (byte)0 : identity);
            stream.WriteByte(variant);
        }
        public static MalformedDataset Deserialize(Stream stream)
        {
            var mode = (byte)stream.ReadByte();
            if (mode == 4)
            {
                throw new SimulatedDecoderMemoryException();
            }
            var identity = (byte)stream.ReadByte();
            var variant = mode == 3 ? (byte)0 : (byte)stream.ReadByte();
            return new MalformedDataset(mode, identity, mode == 2 ? (byte)(variant + 1) : variant);
        }
    }

    private sealed class SimulatedDecoderMemoryException : OutOfMemoryException
    {
    }
}
