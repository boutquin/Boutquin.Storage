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

// LevelDB scenario adaptations: Copyright (c) 2011 The LevelDB Authors.
// Badger scenario adaptations: Copyright (c) 2017-2025 Istari Digital, Inc.
// Adapted to Storage API contracts; see Io-provenance.md and retained upstream licenses.
using Boutquin.Storage.Domain.ValueObjects.LsmTree;
using Boutquin.Storage.Domain.ValueObjects.ValueLog;
using Boutquin.Storage.Infrastructure.ValueLog;
using Key = Boutquin.Storage.Domain.Helpers.SerializableWrapper<int>;
using Text = Boutquin.Storage.Domain.Helpers.SerializableWrapper<string>;

namespace Boutquin.Storage.Infrastructure.Tests.OssComparison;

public sealed class IoOssComparisonTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"io-oss-{Guid.NewGuid():N}");

    public IoOssComparisonTests() => Directory.CreateDirectory(_folder);
    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LevelDb_LogReopenForAppend_VariedRecordLengths(bool indexed)
    {
        AppendOnlyFileStorageEngine<Key, Text> Open() => indexed
            ? new AppendOnlyFileStorageEngineWithIndex<Key, Text>(new StorageFile(_folder, "append.log"),
                new BinaryEntrySerializer<Key, Text>(), new InMemoryFileIndex<Key>())
            : new AppendOnlyFileStorageEngine<Key, Text>(new StorageFile(_folder, "append.log"), new BinaryEntrySerializer<Key, Text>());
        var expected = new Dictionary<int, string>();
        for (var generation = 0; generation < 3; generation++)
        {
            var engine = Open();
            for (var key = 1; key <= 18; key++)
            {
                var value = new string((char)('a' + key), key % 3 == 0 ? 32768 : key * 7) + generation;
                expected[key] = value;
                await engine.SetAsync(new Key(key), new Text(value));
            }
            await engine.CompactAsync();
            var reopened = Open();
            foreach (var (key, value) in expected)
            {
                Assert.Equal(value, (await reopened.TryGetValueAsync(new Key(key))).Value.Value);
            }
        }
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task LevelDb_ConcurrentDisjointWriters_SnapshotAndCompactionKeepEveryVersion(int strategy, bool separated)
    {
        var options = new LsmStorageEngineOptions<Key, Text>(
            MemTableCapacity: 8, SegmentFolder: _folder, SegmentPrefix: "seg",
            SegmentFormat: new SstSegmentFormat<Key, Text>(),
            CompactionStrategy: strategy switch
            {
                0 => new FullCompactionStrategy<Key>(4),
                1 => new SizeTieredCompactionStrategy<Key>(minSegments: 4),
                _ => new LeveledCompactionStrategy<Key>(level0Threshold: 4)
            },
            ValueLog: separated ? new ValueLogOptions(ValueLogThresholdBytes: 64, SegmentMaxBytes: 480) : null);
        await using var engine = await LsmStorageEngine<Key, Text>.OpenAsync(options);
        for (var key = 1; key <= 32; key++)
        {
            await engine.SetAsync(new Key(key), new Text(Payload(key, 0)));
        }
        await engine.FlushAsync();
        await using var snapshot = await engine.AcquireSnapshotAsync();
        var writers = Enumerable.Range(0, 4).Select(async worker =>
        {
            for (var generation = 1; generation <= 3; generation++)
            {
                for (var key = worker + 1; key <= 32; key += 4)
                {
                    await engine.SetAsync(new Key(key), new Text(Payload(key, generation)));
                    Assert.Equal(Payload(key, generation), (await engine.TryGetValueAsync(new Key(key))).Value.Value);
                    Assert.Equal(Payload(key, 0), (await snapshot.TryGetValueAsync(new Key(key))).Value.Value);
                }
            }
        });
        await Task.WhenAll(writers);
        await engine.FlushAsync();
        await engine.CompactAsync();
        if (separated)
        {
            await engine.CompactValueLogAsync();
        }
        for (var key = 1; key <= 32; key++)
        {
            Assert.Equal(Payload(key, 3), (await engine.TryGetValueAsync(new Key(key))).Value.Value);
            Assert.Equal(Payload(key, 0), (await snapshot.TryGetValueAsync(new Key(key))).Value.Value);
        }
        var rows = new List<int>();
        await foreach (var row in snapshot.GetRangeAsync(new Key(1), new Key(32)))
        {
            rows.Add(row.Key.Value);
            Assert.Equal(Payload(row.Key.Value, 0), row.Value.Value);
        }
        Assert.Equal(Enumerable.Range(1, 32), rows);
    }

    [Fact]
    public async Task Badger_ValueRead_ConcurrentEvictionRetainsExactPayloads()
    {
        var options = new ValueLogOptions(SegmentMaxBytes: 200, ReadHandleCacheSize: 1);
        await using var manifest = new ValueLogManifest(Path.Combine(_folder, "values.manifest"));
        await using var writer = await ValueLogWriter.OpenAsync(_folder, manifest, options);
        var pointers = new List<ValueLogPointer>();
        for (var i = 1; i <= 24; i++)
        {
            pointers.Add(await writer.AppendAsync(Encoding.UTF8.GetBytes(Payload(i, 0)), (ulong)i));
        }
        Assert.True(writer.FileSizes.Count > 1, "cache eviction needs multiple files");
        using var reader = new ValueLogReader(_folder, options);
        await Task.WhenAll(Enumerable.Range(0, 6).Select(worker => Task.Run(() =>
        {
            for (var turn = 0; turn < 96; turn++)
            {
                var i = (turn + worker * 7) % pointers.Count;
                Assert.Equal(Payload(i + 1, 0), Encoding.UTF8.GetString(reader.Read(pointers[i])));
            }
        })));
    }

    [Fact]
    public async Task Wal_ConcurrentGroupCommit_RecoversEveryAcknowledgedFrameOnce()
    {
        using (var wal = new WriteAheadLog<Key, Text>(_folder))
        {
            await Task.WhenAll(Enumerable.Range(1, 96).Select(i =>
                wal.AppendAsync(new Key(i), new Text(Payload(i, 0)), (ulong)i)));
        }
        using var recovered = new WriteAheadLog<Key, Text>(_folder);
        var entries = await recovered.RecoverAsync();
        Assert.Equal(96, entries.Count);
        Assert.Equal(96, entries.Select(entry => entry.Key.Value).Distinct().Count());
        foreach (var entry in entries)
        {
            Assert.Equal(Payload(entry.Key.Value, 0), entry.InlineValue!.Value);
        }
    }

    private static string Payload(int key, int generation) => $"{key}:{generation}:" + new string((char)('a' + key % 26), 140);
}
