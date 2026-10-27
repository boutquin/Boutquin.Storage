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

using Boutquin.Storage.Distributed.Cdc;
using Boutquin.Storage.Infrastructure.Cdc;
using Boutquin.Storage.Infrastructure.Events;
using Boutquin.Storage.Infrastructure.Streaming.Checkpointing;

namespace Boutquin.Storage.Infrastructure.Tests.Cdc;

public sealed class CdcPipelineTests
{
    [Fact]
    public async Task CompactionRetainsLatestOffsetsAndDeleteWatermarksAcrossRecovery()
    {
        var checkpoints = new InMemoryCheckpointStore();
        var topic = new CheckpointedLogCompactedTopic<string, string>(checkpoints, "topic");
        await topic.RestoreAsync();
        Assert.Equal(0, await topic.AppendAsync("a", "old", ChangeOperation.Insert));
        await topic.AppendAsync("b", "stay", ChangeOperation.Insert);
        await topic.AppendAsync("a", "new", ChangeOperation.Update);
        await topic.AppendAsync("a", null, ChangeOperation.Delete);
        var recovered = new CheckpointedLogCompactedTopic<string, string>(checkpoints, "topic");
        await recovered.RestoreAsync();
        var records = await Collect(recovered.ReadChangesAsync(0));
        Assert.Equal(new long[] { 1, 3 }, records.Select(r => r.Position));
        Assert.Equal(ChangeOperation.Delete, records[1].Operation);
        var snapshot = await recovered.CaptureSnapshotAsync();
        Assert.Equal(4, snapshot.NextPosition);
        var model = new InMemoryReadModelStore<string, string>();
        var sink = new ReadModelProjectionSink<string, string>(model);
        foreach (var record in snapshot.Records)
        {
            await sink.ApplyAsync(record);
        }

        await sink.ApplyAsync(new(0, ChangeOperation.Insert, "a", "resurrect", DateTimeOffset.UnixEpoch));
        Assert.False((await model.TryGetAsync("a")).Found);
        Assert.Equal("stay", (await model.TryGetAsync("b")).Value);
    }

    [Fact]
    public async Task SnapshotThenTailDoesNotLoseConcurrentUpdateAndReplayIsIdempotent()
    {
        var topic = new CheckpointedLogCompactedTopic<string, string>(new InMemoryCheckpointStore(), "snapshot");
        await topic.RestoreAsync();
        await topic.AppendAsync("a", "before", ChangeOperation.Insert);
        var snapshot = await topic.CaptureSnapshotAsync();
        await topic.AppendAsync("a", "after", ChangeOperation.Update);
        await topic.AppendAsync("b", "new", ChangeOperation.Insert);
        var model = new InMemoryReadModelStore<string, string>();
        var sink = new ReadModelProjectionSink<string, string>(model);
        foreach (var record in snapshot.Records)
        {
            await sink.ApplyAsync(record);
        }

        Assert.Equal(3, await CdcProjectionPump.CatchUpAsync(topic, snapshot.NextPosition, sink));
        await CdcProjectionPump.CatchUpAsync(topic, 0, sink);
        Assert.Equal("after", (await model.TryGetAsync("a")).Value);
        Assert.Equal("new", (await model.TryGetAsync("b")).Value);
    }

    [Fact]
    public async Task LsmAdapterCapturesOrderedWritesDeletesAndRebuildsFromDurableJournal()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cdc_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "changes.log");
            using (var log = await AppendOnlyEventLog<ChangeRecord<SerializableWrapper<int>, SerializableWrapper<string>>>.CreateAsync(path))
            await using (var engine = LsmEngineFactory.Build<SerializableWrapper<int>, SerializableWrapper<string>>(memTableCapacity: 32, segmentPrefix: "cdc", segmentFolder: Path.Combine(dir, "first")))
            {
                var source = new LsmCdcAdapter<SerializableWrapper<int>, SerializableWrapper<string>>(engine, log);
                await source.RecoverAsync();
                await source.SetAsync(new(1), new("one"));
                await source.SetAsync(new(1), new("changed"));
                await source.SetAsync(new(2), new("two"));
                await source.RemoveAsync(new(1));
                var changes = await Collect(source.ReadChangesAsync(0));
                Assert.Equal(new[] { ChangeOperation.Insert, ChangeOperation.Update, ChangeOperation.Insert, ChangeOperation.Delete }, changes.Select(r => r.Operation));
                Assert.Equal(new long[] { 0, 1, 2, 3 }, changes.Select(r => r.Position));
            }
            using var reopened = await AppendOnlyEventLog<ChangeRecord<SerializableWrapper<int>, SerializableWrapper<string>>>.CreateAsync(path);
            await using var rebuilt = LsmEngineFactory.Build<SerializableWrapper<int>, SerializableWrapper<string>>(memTableCapacity: 32, segmentPrefix: "cdc", segmentFolder: Path.Combine(dir, "rebuilt"));
            var recovered = new LsmCdcAdapter<SerializableWrapper<int>, SerializableWrapper<string>>(rebuilt, reopened);
            await recovered.RecoverAsync();
            Assert.False((await rebuilt.TryGetValueAsync(new(1))).Found);
            Assert.Equal(new SerializableWrapper<string>("two"), (await rebuilt.TryGetValueAsync(new(2))).Value);
            Assert.Equal(4, (await recovered.CaptureSnapshotAsync()).NextPosition);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task SingleLeaderAdapterCapturesDirectWritesAndSnapshotTailDespiteFollowerTruncation()
    {
        using var leader = new SingleLeaderReplication<string, string>();
        var source = new SingleLeaderCdcSource<string, string>(leader);
        await leader.WriteAsync("a", "before");
        var snapshot = await source.CaptureSnapshotAsync();
        leader.AddFollower("follower");
        await leader.WriteAsync("a", "after");
        await leader.SyncFollowerAsync("follower");
        var records = await Collect(source.ReadChangesAsync(snapshot.NextPosition));
        Assert.Single(records);
        Assert.Equal(1, records[0].Position);
        Assert.Equal("after", records[0].Value);
        Assert.Equal(2, (await source.CaptureSnapshotAsync()).NextPosition);
    }

    [Fact]
    public async Task TextSinkKeepsOnlyCurrentTermsAndRejectsReplayedPreDeleteState()
    {
        var index = new Boutquin.Storage.Infrastructure.Search.Text.InvertedIndex<string>(
            new Boutquin.Storage.Infrastructure.Search.Text.TextAnalyzer(new Boutquin.Storage.Infrastructure.Search.Text.WhitespaceTokenizer()));
        var sink = new InvertedIndexProjectionSink<string, string>(index, text => text.AsMemory());
        await sink.ApplyAsync(new(0, ChangeOperation.Insert, "a", "old term", DateTimeOffset.UnixEpoch));
        await sink.ApplyAsync(new(1, ChangeOperation.Update, "a", "new term", DateTimeOffset.UnixEpoch));
        Assert.Empty(index.Lookup("old"));
        Assert.Equal(new[] { "a" }, index.Lookup("new"));
        await sink.ApplyAsync(new(2, ChangeOperation.Delete, "a", null, DateTimeOffset.UnixEpoch));
        await sink.ApplyAsync(new(1, ChangeOperation.Update, "a", "new term", DateTimeOffset.UnixEpoch));
        Assert.Empty(index.Lookup("new"));
    }

    [Fact]
    public async Task SeededSnapshotTailReplayMatchesCurrentStateAcrossCompactedGaps()
    {
        for (var seed = 1; seed <= 20; seed++)
        {
            var random = new Random(seed);
            var topic = new CheckpointedLogCompactedTopic<string, string>(new InMemoryCheckpointStore(), "topic");
            await topic.RestoreAsync();
            var expected = new Dictionary<string, string>();
            CdcSnapshot<string, string>? snapshot = null;
            for (var step = 0; step < 100; step++)
            {
                var key = random.Next(8).ToString(System.Globalization.CultureInfo.InvariantCulture);
                var deleted = random.Next(3) == 0;
                var value = "value-" + step;
                await topic.AppendAsync(key, deleted ? null : value, deleted ? ChangeOperation.Delete : ChangeOperation.Update);
                if (deleted) { expected.Remove(key); } else { expected[key] = value; }
                if (step == 49) { snapshot = await topic.CaptureSnapshotAsync(); }
            }
            var model = new InMemoryReadModelStore<string, string>();
            var sink = new ReadModelProjectionSink<string, string>(model);
            foreach (var change in snapshot!.Records) { await sink.ApplyAsync(change); }
            Assert.Equal(100, await CdcProjectionPump.CatchUpAsync(topic, snapshot.NextPosition, sink));
            await CdcProjectionPump.CatchUpAsync(topic, 0, sink);
            var actual = await model.SnapshotAsync();
            Assert.Equal(expected.OrderBy(pair => pair.Key), actual.OrderBy(pair => pair.Key));
        }
    }

    private static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> source)
    {
        var result = new List<T>();
        await foreach (var item in source)
        {
            result.Add(item);
        }

        return result;
    }
}
