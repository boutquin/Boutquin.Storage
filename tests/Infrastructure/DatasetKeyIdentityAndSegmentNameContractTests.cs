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
using Boutquin.Storage.Infrastructure.TimeSeries;

namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// Pins one identity for <see cref="TimeSeriesKey{TDatasetKey}"/>: <c>CompareTo</c>, <c>Equals</c>,
/// <c>GetHashCode</c> and the serialized bytes agree for every dataset key that meets the declared
/// constraints, so the engine's hash-keyed tombstone containers and its comparer-ordered memtable and
/// segments name the same keys, and a segment prefix is admitted only when the manifest reads it back
/// verbatim.
/// </summary>
public sealed class DatasetKeyIdentityAndSegmentNameContractTests : IDisposable
{
    private static readonly DateOnly s_d0 = new(2024, 1, 1);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ds-key-identity-" + Guid.NewGuid().ToString("N"));

    public DatasetKeyIdentityAndSegmentNameContractTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // best-effort cleanup of a temp directory
        }
    }

    // A dataset key that meets every declared constraint but keeps reference equality.
    public sealed class RefKey : ISerializable<RefKey>, IComparable<RefKey>
    {
        public RefKey()
        {
        }

        public RefKey(string name) => Name = name;

        public string Name { get; set; } = string.Empty;

        public int CompareTo(RefKey? other) => string.CompareOrdinal(Name, other?.Name);

        public void Serialize(Stream stream)
        {
            var bytes = Encoding.UTF8.GetBytes(Name);
            stream.WriteByte((byte)bytes.Length);
            stream.Write(bytes);
        }

        public static RefKey Deserialize(Stream stream)
        {
            var length = stream.ReadByte();
            var bytes = new byte[length];
            stream.ReadExactly(bytes);
            return new RefKey(Encoding.UTF8.GetString(bytes));
        }
    }

    // A dataset key whose CompareTo and Equals ignore case while Serialize keeps the caller's spelling.
    public sealed class CiKey : ISerializable<CiKey>, IComparable<CiKey>, IEquatable<CiKey>
    {
        public CiKey()
        {
        }

        public CiKey(string name) => Name = name;

        public string Name { get; set; } = string.Empty;

        public int CompareTo(CiKey? other) => string.Compare(Name, other?.Name, StringComparison.OrdinalIgnoreCase);

        public bool Equals(CiKey? other) => other is not null && string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase);

        public override bool Equals(object? obj) => Equals(obj as CiKey);

        public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Name);

        public void Serialize(Stream stream)
        {
            var bytes = Encoding.UTF8.GetBytes(Name);
            stream.WriteByte((byte)bytes.Length);
            stream.Write(bytes);
        }

        public static CiKey Deserialize(Stream stream)
        {
            var length = stream.ReadByte();
            var bytes = new byte[length];
            stream.ReadExactly(bytes);
            return new CiKey(Encoding.UTF8.GetString(bytes));
        }
    }

    private static byte[] Bytes<T>(T key)
        where T : ISerializable<T>
    {
        using var ms = new MemoryStream();
        key.Serialize(ms);
        return ms.ToArray();
    }

    private static Task<LsmStorageEngine<TimeSeriesKey<TDs>, SerializableWrapper<int>>> OpenSstAsync<TDs>(string dir)
        where TDs : IComparable<TDs>, ISerializable<TDs>, new()
        => LsmStorageEngine<TimeSeriesKey<TDs>, SerializableWrapper<int>>.OpenAsync(
            new LsmStorageEngineOptions<TimeSeriesKey<TDs>, SerializableWrapper<int>>(
                100, dir, "seg", new SstSegmentFormat<TimeSeriesKey<TDs>, SerializableWrapper<int>>()));

    private static MarketDataBar Bar(DateOnly date, decimal close) => new() { Date = date, Close = close };

    private static async Task<List<T>> ToListAsync<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source)
        {
            list.Add(item);
        }

        return list;
    }

    // ---- TimeSeriesKey identity ----

    [Fact]
    public void TimeSeriesKey_CompareEqualReferenceDatasetKeys_AreEqualWithEqualHashes()
    {
        var a = new TimeSeriesKey<RefKey>(new RefKey("x"), s_d0);
        var b = new TimeSeriesKey<RefKey>(new RefKey("x"), s_d0);

        a.CompareTo(b).Should().Be(0);
        a.Equals(b).Should().BeTrue("IStorageKey requires Equals to agree with CompareTo; instance identity is not a key identity");
        (a == b).Should().BeTrue();
        a.GetHashCode().Should().Be(b.GetHashCode(), "equal keys must hash alike or every hash-keyed tombstone container misses them");
    }

    [Fact]
    public void TimeSeriesKey_CompareEqualKeysThatSerializeDifferently_AreDistinctUnderEveryRelation()
    {
        var alpha = new TimeSeriesKey<CiKey>(new CiKey("Alpha"), s_d0);
        var upper = new TimeSeriesKey<CiKey>(new CiKey("ALPHA"), s_d0);

        Bytes(alpha).Should().NotEqual(Bytes(upper), "the dataset key keeps the caller's spelling");
        (alpha.CompareTo(upper) == 0).Should().BeFalse(
            "keys whose serialized bytes differ must not compare equal: the bloom filter and the on-disk bytes see two keys");
        alpha.Equals(upper).Should().BeFalse();
        Math.Sign(alpha.CompareTo(upper)).Should().Be(-Math.Sign(upper.CompareTo(alpha)), "the tie-break must be antisymmetric");
    }

    // ---- Generic LSM engine over TimeSeriesKey ----

    [Fact]
    public async Task Engine_RemoveThroughAnEqualKeyInstance_HidesTheRecordInMemtableAndAfterFlush()
    {
        await using var engine = await OpenSstAsync<RefKey>(_dir);
        await engine.SetAsync(new TimeSeriesKey<RefKey>(new RefKey("x"), s_d0), new SerializableWrapper<int>(42));
        await engine.FlushAsync();

        await engine.RemoveAsync(new TimeSeriesKey<RefKey>(new RefKey("x"), s_d0));
        var probe = new TimeSeriesKey<RefKey>(new RefKey("x"), s_d0);

        (await engine.TryGetValueAsync(probe)).Found.Should().BeFalse("the tombstone sits in the memtable for an equal key");
        (await engine.ContainsKeyAsync(probe)).Should().BeFalse();
        (await engine.GetAllItemsAsync()).Should().BeEmpty();

        await engine.FlushAsync();

        (await engine.TryGetValueAsync(probe)).Found.Should().BeFalse("the flushed segment tombstone covers an equal key");
        (await ToListAsync(engine.GetRangeAsync(probe, probe))).Should().BeEmpty();
    }

    [Fact]
    public async Task Engine_LookupOfACompareEqualSpellingVariant_AnswersTheSameBeforeAndAfterFlush()
    {
        await using var engine = await OpenSstAsync<CiKey>(_dir);
        await engine.SetAsync(new TimeSeriesKey<CiKey>(new CiKey("Alpha"), s_d0), new SerializableWrapper<int>(42));
        var variant = new TimeSeriesKey<CiKey>(new CiKey("ALPHA"), s_d0);

        var beforeFlush = (await engine.TryGetValueAsync(variant)).Found;
        await engine.FlushAsync();
        var afterFlush = (await engine.TryGetValueAsync(variant)).Found;

        afterFlush.Should().Be(beforeFlush,
            "a flush must not change which key a lookup names; the bloom filter is keyed on the serialized bytes");
        (await engine.TryGetValueAsync(new TimeSeriesKey<CiKey>(new CiKey("Alpha"), s_d0))).Value.Value.Should().Be(42);
    }

    // ---- Public time-series store across a reopen ----

    [Fact]
    public async Task Store_DeleteRangeThenReopen_DoesNotReturnTheDeletedRows()
    {
        var folder = Path.Combine(_dir, "store");
        await using (var stack = await ColumnarTimeSeriesEngineBuilder.BuildAsync<RefKey, MarketDataBar>(folder, memTableCapacity: 100))
        {
            await stack.Store.AppendAsync(new RefKey("x"), [.. Enumerable.Range(0, 5).Select(i => Bar(s_d0.AddDays(i), i))]);
            await stack.InnerEngine.FlushAsync();
            await stack.Store.DeleteRangeAsync(new RefKey("x"), s_d0, s_d0.AddDays(2));
            await stack.InnerEngine.FlushAsync();
        }

        await using var reopened = await ColumnarTimeSeriesEngineBuilder.BuildAsync<RefKey, MarketDataBar>(folder, memTableCapacity: 100);
        var rows = await ToListAsync(reopened.Store.GetRangeAsync(new RefKey("x"), DateOnly.MinValue, DateOnly.MaxValue));

        rows.Select(r => r.Date).Should().Equal([s_d0.AddDays(3), s_d0.AddDays(4)],
            "the recovered segment tombstones are keyed by deserialized instances and must still hide the deleted rows");
    }
}
