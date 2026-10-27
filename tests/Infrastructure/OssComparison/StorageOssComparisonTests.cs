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

// LevelDB scenario adaptations: Copyright (c) 2011 The LevelDB Authors.
// Badger scenario adaptations: Copyright (c) 2017-2025 Istari Digital, Inc.
// Modified for Storage's public APIs and deterministic payloads; see README.md and
// LevelDB-LICENSE.txt / Badger-LICENSE.txt for provenance, licenses and limitations.

using System.Text.Json;
using Boutquin.Storage.Domain.ValueObjects.ValueLog;
using IntKey = Boutquin.Storage.Domain.Helpers.SerializableWrapper<int>;
using Text = Boutquin.Storage.Domain.Helpers.SerializableWrapper<string>;

namespace Boutquin.Storage.Infrastructure.Tests.OssComparison;

public sealed class StorageOssComparisonTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"storage-oss-{Guid.NewGuid():N}");

    public static IEnumerable<object[]> Configurations
    {
        get
        {
            for (var strategy = 0; strategy < 3; strategy++)
            {
                yield return new object[] { strategy, false };
                yield return new object[] { strategy, true };
            }
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private Task<LsmStorageEngine<TKey, Text>> OpenAsync<TKey>(int strategy, bool separated, string? folder = null)
        where TKey : IStorageKey<TKey>, new()
        => LsmEngineFactory.BuildAsync<TKey, Text>(
            memTableCapacity: 8,
            segmentFolder: folder ?? _folder,
            segmentPrefix: "oss",
            compactionStrategy: strategy switch
            {
                0 => new FullCompactionStrategy<TKey>(4),
                1 => new SizeTieredCompactionStrategy<TKey>(minSegments: 4),
                2 => new LeveledCompactionStrategy<TKey>(level0Threshold: 4),
                _ => throw new ArgumentOutOfRangeException(nameof(strategy))
            },
            valueLog: separated ? new ValueLogOptions(ValueLogThresholdBytes: 64, SegmentMaxBytes: 480) : null);

    private static async Task AssertTextAsync(Task<(Text Value, bool Found)> lookup, string? expected)
    {
        var (value, found) = await lookup.ConfigureAwait(true);
        found.Should().Be(expected is not null);
        if (found)
        {
            value.Value.Should().Be(expected);
        }
    }

    [Theory]
    [MemberData(nameof(Configurations))]
    public async Task LevelDb_PutDeleteGet(int strategy, bool separated)
    {
        await using var engine = await OpenAsync<Text>(strategy, separated).ConfigureAwait(true);
        var key = new Text("foo");
        await engine.SetAsync(key, new Text("v1")).ConfigureAwait(true);
        await AssertTextAsync(engine.TryGetValueAsync(key), "v1").ConfigureAwait(true);
        await engine.SetAsync(key, new Text("v2")).ConfigureAwait(true);
        await AssertTextAsync(engine.TryGetValueAsync(key), "v2").ConfigureAwait(true);
        await engine.RemoveAsync(key).ConfigureAwait(true);
        await AssertTextAsync(engine.TryGetValueAsync(key), null).ConfigureAwait(true);
    }

    [Theory]
    [MemberData(nameof(Configurations))]
    public async Task LevelDb_GetSnapshot(int strategy, bool separated)
    {
        await using var engine = await OpenAsync<Text>(strategy, separated).ConfigureAwait(true);
        foreach (var text in new[] { "foo", new string('x', 200) })
        {
            var key = new Text(text);
            await engine.SetAsync(key, new Text("v1")).ConfigureAwait(true);
            await using var snapshot = await engine.AcquireSnapshotAsync().ConfigureAwait(true);
            await engine.SetAsync(key, new Text("v2")).ConfigureAwait(true);
            await AssertTextAsync(engine.TryGetValueAsync(key), "v2").ConfigureAwait(true);
            await AssertTextAsync(snapshot.TryGetValueAsync(key), "v1").ConfigureAwait(true);
            await engine.FlushAsync().ConfigureAwait(true);
            await AssertTextAsync(engine.TryGetValueAsync(key), "v2").ConfigureAwait(true);
            await AssertTextAsync(snapshot.TryGetValueAsync(key), "v1").ConfigureAwait(true);
        }
    }

    [Theory]
    [MemberData(nameof(Configurations))]
    public async Task LevelDb_GetIdenticalSnapshots(int strategy, bool separated)
    {
        await using var engine = await OpenAsync<Text>(strategy, separated).ConfigureAwait(true);
        foreach (var text in new[] { "foo", new string('x', 200) })
        {
            var key = new Text(text);
            await engine.SetAsync(key, new Text("v1")).ConfigureAwait(true);
            await using var first = await engine.AcquireSnapshotAsync().ConfigureAwait(true);
            await using var second = await engine.AcquireSnapshotAsync().ConfigureAwait(true);
            await using var third = await engine.AcquireSnapshotAsync().ConfigureAwait(true);
            await engine.SetAsync(key, new Text("v2")).ConfigureAwait(true);
            await AssertTextAsync(engine.TryGetValueAsync(key), "v2").ConfigureAwait(true);
            await AssertTextAsync(first.TryGetValueAsync(key), "v1").ConfigureAwait(true);
            await AssertTextAsync(second.TryGetValueAsync(key), "v1").ConfigureAwait(true);
            await AssertTextAsync(third.TryGetValueAsync(key), "v1").ConfigureAwait(true);
            await first.DisposeAsync().ConfigureAwait(true);
            await engine.FlushAsync().ConfigureAwait(true);
            await AssertTextAsync(engine.TryGetValueAsync(key), "v2").ConfigureAwait(true);
            await AssertTextAsync(second.TryGetValueAsync(key), "v1").ConfigureAwait(true);
            await second.DisposeAsync().ConfigureAwait(true);
            await AssertTextAsync(third.TryGetValueAsync(key), "v1").ConfigureAwait(true);
        }
    }

    [Theory]
    [MemberData(nameof(Configurations))]
    public async Task LevelDb_IterateOverEmptySnapshot(int strategy, bool separated)
    {
        await using var engine = await OpenAsync<Text>(strategy, separated).ConfigureAwait(true);
        await using var snapshot = await engine.AcquireSnapshotAsync().ConfigureAwait(true);
        await engine.SetAsync(new Text("foo"), new Text("v1")).ConfigureAwait(true);
        await engine.SetAsync(new Text("foo"), new Text("v2")).ConfigureAwait(true);
        for (var phase = 0; phase < 2; phase++)
        {
            var count = 0;
            await foreach (var unused in snapshot.GetRangeAsync(new Text("a"), new Text("z")).ConfigureAwait(true))
            {
                count++;
            }
            count.Should().Be(0);
            await engine.FlushAsync().ConfigureAwait(true);
        }
    }

    [Theory]
    [MemberData(nameof(Configurations))]
    public async Task LevelDb_GetLevel0Ordering(int strategy, bool separated)
    {
        await using var engine = await OpenAsync<Text>(strategy, separated).ConfigureAwait(true);
        await engine.SetAsync(new Text("bar"), new Text("b")).ConfigureAwait(true);
        await engine.SetAsync(new Text("foo"), new Text("v1")).ConfigureAwait(true);
        await engine.FlushAsync().ConfigureAwait(true);
        await engine.SetAsync(new Text("foo"), new Text("v2")).ConfigureAwait(true);
        await engine.FlushAsync().ConfigureAwait(true);
        await AssertTextAsync(engine.TryGetValueAsync(new Text("foo")), "v2").ConfigureAwait(true);
    }

    [Theory]
    [MemberData(nameof(Configurations))]
    public async Task LevelDb_GetOrderedByLevels(int strategy, bool separated)
    {
        await using var engine = await OpenAsync<Text>(strategy, separated).ConfigureAwait(true);
        await engine.SetAsync(new Text("foo"), new Text("v1")).ConfigureAwait(true);
        await engine.FlushAsync().ConfigureAwait(true);
        await engine.CompactAsync().ConfigureAwait(true);
        await AssertTextAsync(engine.TryGetValueAsync(new Text("foo")), "v1").ConfigureAwait(true);
        await engine.SetAsync(new Text("foo"), new Text("v2")).ConfigureAwait(true);
        await AssertTextAsync(engine.TryGetValueAsync(new Text("foo")), "v2").ConfigureAwait(true);
        await engine.FlushAsync().ConfigureAwait(true);
        await AssertTextAsync(engine.TryGetValueAsync(new Text("foo")), "v2").ConfigureAwait(true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Badger_ValueGc_DeletionAndSmallReplacement(bool replaceWithSmallValues)
    {
        var engine = await OpenAsync<IntKey>(0, true).ConfigureAwait(true);
        try
        {
            for (var key = 1; key <= 100; key++)
            {
                await engine.SetAsync(new IntKey(key), new Text(new string((char)('a' + key % 26), 140))).ConfigureAwait(true);
            }
            var deleted = replaceWithSmallValues ? 5 : 45;
            for (var key = 1; key <= deleted; key++)
            {
                await engine.RemoveAsync(new IntKey(key)).ConfigureAwait(true);
            }
            if (replaceWithSmallValues)
            {
                for (var key = 6; key <= 10; key++)
                {
                    await engine.SetAsync(new IntKey(key), new Text($"value{key}")).ConfigureAwait(true);
                }
            }
            await engine.FlushAsync().ConfigureAwait(true);
            await engine.CompactAsync().ConfigureAwait(true);
            var result = await engine.CompactValueLogAsync().ConfigureAwait(true);
            result.FilesReclaimed.Should().BeGreaterThan(0, "the scenario must actually reclaim stale files");
            for (var phase = 0; phase < 2; phase++)
            {
                for (var key = 1; key <= 100; key++)
                {
                    var expected = key <= deleted ? null
                        : replaceWithSmallValues && key <= 10 ? $"value{key}"
                        : new string((char)('a' + key % 26), 140);
                    await AssertTextAsync(engine.TryGetValueAsync(new IntKey(key)), expected).ConfigureAwait(true);
                }
                if (phase == 0)
                {
                    await engine.DisposeAsync().ConfigureAwait(true);
                    engine = await OpenAsync<IntKey>(0, true).ConfigureAwait(true);
                }
            }
        }
        finally
        {
            await engine.DisposeAsync().ConfigureAwait(true);
        }
    }

    [Theory]
    [MemberData(nameof(Configurations))]
    public async Task Sqlite_SeededHistories_MatchAtEveryBoundary(int strategy, bool separated)
    {
        using var resource = typeof(StorageOssComparisonTests).Assembly.GetManifestResourceStream("StorageOracle.sqlite-kv-histories.json")!;
        using var corpus = await JsonDocument.ParseAsync(resource).ConfigureAwait(true);
        foreach (var history in corpus.RootElement.GetProperty("histories").EnumerateArray())
        {
            var folder = Path.Combine(_folder, history.GetProperty("seed").GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture));
            var engine = await OpenAsync<IntKey>(strategy, separated, folder).ConfigureAwait(true);
            ISnapshot<IntKey, Text>? snapshot = null;
            (int Key, string Value)[] snapshotState = [];
            try
            {
                var index = 0;
                foreach (var step in history.GetProperty("steps").EnumerateArray())
                {
                    var key = new IntKey(step.GetProperty("key").GetInt32());
                    if (step.GetProperty("operation").GetString() == "put")
                    {
                        await engine.SetAsync(key, new Text(step.GetProperty("value").GetString()!)).ConfigureAwait(true);
                    }
                    else
                    {
                        await engine.RemoveAsync(key).ConfigureAwait(true);
                    }

                    var expected = step.GetProperty("expected").EnumerateArray()
                        .Select(row => (Key: row.GetProperty("key").GetInt32(), Value: row.GetProperty("value").GetString()!)).ToArray();
                    await AssertStateAsync(engine, expected).ConfigureAwait(true);
                    if (index % 16 == 0)
                    {
                        snapshot = await engine.AcquireSnapshotAsync().ConfigureAwait(true);
                        snapshotState = expected;
                    }
                    if (index % 7 == 6)
                    {
                        await engine.FlushAsync().ConfigureAwait(true);
                        await AssertStateAsync(engine, expected).ConfigureAwait(true);
                    }
                    if (index % 11 == 10)
                    {
                        await engine.CompactAsync().ConfigureAwait(true);
                        if (separated)
                        {
                            await engine.CompactValueLogAsync().ConfigureAwait(true);
                        }
                        await AssertStateAsync(engine, expected).ConfigureAwait(true);
                    }
                    if (snapshot is not null)
                    {
                        var rows = new List<(int Key, string Value)>();
                        await foreach (var pair in snapshot.GetRangeAsync(new IntKey(1), new IntKey(24)).ConfigureAwait(true))
                        {
                            rows.Add((pair.Key.Value, pair.Value.Value));
                        }
                        rows.Should().Equal(snapshotState);
                    }
                    if (index % 16 == 15)
                    {
                        await snapshot!.DisposeAsync().ConfigureAwait(true);
                        snapshot = null;
                    }
                    if (index % 32 == 31)
                    {
                        await engine.FlushAsync().ConfigureAwait(true);
                        await engine.DisposeAsync().ConfigureAwait(true);
                        engine = await OpenAsync<IntKey>(strategy, separated, folder).ConfigureAwait(true);
                        await AssertStateAsync(engine, expected).ConfigureAwait(true);
                    }
                    index++;
                }
            }
            finally
            {
                if (snapshot is not null)
                {
                    await snapshot.DisposeAsync().ConfigureAwait(true);
                }
                await engine.DisposeAsync().ConfigureAwait(true);
            }
        }
    }

    private static async Task AssertStateAsync(LsmStorageEngine<IntKey, Text> engine, (int Key, string Value)[] expected)
    {
        for (var key = 1; key <= 24; key++)
        {
            await AssertTextAsync(engine.TryGetValueAsync(new IntKey(key)),
                expected.FirstOrDefault(pair => pair.Key == key).Value).ConfigureAwait(true);
        }
        var rows = new List<(int Key, string Value)>();
        await foreach (var pair in engine.GetRangeAsync(new IntKey(1), new IntKey(24)).ConfigureAwait(true))
        {
            rows.Add((pair.Key.Value, pair.Value.Value));
        }
        rows.Should().Equal(expected);

        var middle = new List<(int Key, string Value)>();
        await foreach (var pair in engine.GetRangeAsync(new IntKey(7), new IntKey(19)).ConfigureAwait(true))
        {
            middle.Add((pair.Key.Value, pair.Value.Value));
        }
        middle.Should().Equal(expected.Where(pair => pair.Key is >= 7 and <= 19));
    }
}
