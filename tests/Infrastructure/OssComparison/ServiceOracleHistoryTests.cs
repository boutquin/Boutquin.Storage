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

using System.Globalization;
using System.Text.Json;
using Boutquin.Storage.Domain.Interfaces.Events;
using Boutquin.Storage.Domain.Interfaces.ObjectStore;
using Boutquin.Storage.Domain.Interfaces.Streaming;
using Boutquin.Storage.Infrastructure.Cdc;
using Boutquin.Storage.Infrastructure.Events;
using Boutquin.Storage.Infrastructure.ObjectStore;
using Boutquin.Storage.Infrastructure.Streaming.Checkpointing;

namespace Boutquin.Storage.Infrastructure.Tests.OssComparison;

public sealed class ServiceOracleHistoryTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("service-oracle-");

    public void Dispose() => _directory.Delete(recursive: true);

    [Theory]
    [InlineData(17, false)]
    [InlineData(73, false)]
    [InlineData(2026, false)]
    [InlineData(17, true)]
    [InlineData(73, true)]
    [InlineData(2026, true)]
    public async Task SqliteHistories_ObjectEventCheckpointCdcAndMvcc_KeepTheSameCommittedState(int seed, bool fileBacked)
    {
        using var fixture = JsonDocument.Parse(typeof(ServiceOracleHistoryTests).Assembly
            .GetManifestResourceStream("StorageOracle.sqlite-kv-histories.json")!);
        var steps = fixture.RootElement.GetProperty("histories").EnumerateArray()
            .Single(history => history.GetProperty("seed").GetInt32() == seed).GetProperty("steps").EnumerateArray().ToArray();
        var keys = steps.Select(step => step.GetProperty("key").GetInt32()).Distinct().ToArray();
        var objectsPath = Path.Combine(_directory.FullName, "objects");
        var checkpointsPath = Path.Combine(_directory.FullName, "checkpoints");
        var eventsPath = Path.Combine(_directory.FullName, "events");
        IObjectStore objects = fileBacked ? new FileSystemObjectStore(objectsPath) : new InMemoryObjectStore();
        ICheckpointStore checkpoints = fileBacked ? new FileCheckpointStore(checkpointsPath) : new InMemoryCheckpointStore();
        var uncertain = new SaveThenFail(checkpoints);
        var topic = new CheckpointedLogCompactedTopic<int, string>(uncertain, "topic");
        await topic.RestoreAsync().ConfigureAwait(true);
        IEventLog<HistoryEvent> events = fileBacked
            ? await AppendOnlyEventLog<HistoryEvent>.CreateAsync(eventsPath).ConfigureAwait(true)
            : new InMemoryEventLog<HistoryEvent>();
        using var mvcc = new MvccStore<int, string>();
        var model = new InMemoryReadModelStore<int, string>();
        var sink = new ReadModelProjectionSink<int, string>(model);
        var before = new Dictionary<int, string>();
        long cursor = 0;
        try
        {
            for (var index = 0; index < steps.Length; index++)
            {
                var step = steps[index];
                var key = step.GetProperty("key").GetInt32();
                var textKey = key.ToString(CultureInfo.InvariantCulture);
                var value = step.GetProperty("operation").GetString() == "put" ? step.GetProperty("value").GetString()! : null;
                var expected = step.GetProperty("expected").EnumerateArray()
                    .ToDictionary(row => row.GetProperty("key").GetInt32(), row => row.GetProperty("value").GetString()!);
                var observer = mvcc.BeginTransaction();
                var writer = mvcc.BeginTransaction();
                if (value is null)
                {
                    await objects.DeleteAsync(textKey).ConfigureAwait(true);
                    await mvcc.DeleteAsync(key, writer).ConfigureAwait(true);
                }
                else
                {
                    using var bytes = new MemoryStream(Encoding.UTF8.GetBytes(value));
                    await objects.WriteAsync(textKey, bytes).ConfigureAwait(true);
                    await mvcc.WriteAsync(key, value, writer).ConfigureAwait(true);
                }
                (await mvcc.CommitAsync(writer).ConfigureAwait(true)).Should().BeTrue();
                (await events.AppendAsync(new HistoryEvent(key, value)).ConfigureAwait(true)).Should().Be(index);
                var operation = value is null ? ChangeOperation.Delete :
                    before.ContainsKey(key) ? ChangeOperation.Update : ChangeOperation.Insert;
                uncertain.FailNext = index % 17 == 16;
                if (uncertain.FailNext)
                {
                    await Assert.ThrowsAsync<IOException>(() => topic.AppendAsync(key, value, operation).AsTask()).ConfigureAwait(true);
                    await Assert.ThrowsAsync<InvalidOperationException>(() => topic.CaptureSnapshotAsync().AsTask()).ConfigureAwait(true);
                    await topic.RestoreAsync().ConfigureAwait(true);
                }
                else
                {
                    (await topic.AppendAsync(key, value, operation).ConfigureAwait(true)).Should().Be(index);
                }
                var latest = mvcc.BeginTransaction();
                foreach (var candidate in keys)
                {
                    var old = await mvcc.ReadAsync(candidate, observer).ConfigureAwait(true);
                    old.Found.Should().Be(before.ContainsKey(candidate));
                    if (old.Found) { old.Value.Should().Be(before[candidate]); }
                    var current = await mvcc.ReadAsync(candidate, latest).ConfigureAwait(true);
                    current.Found.Should().Be(expected.ContainsKey(candidate));
                    if (current.Found) { current.Value.Should().Be(expected[candidate]); }
                    var objectKey = candidate.ToString(CultureInfo.InvariantCulture);
                    (await objects.ExistsAsync(objectKey).ConfigureAwait(true)).Should().Be(expected.ContainsKey(candidate));
                    await using var content = await objects.ReadAsync(objectKey).ConfigureAwait(true);
                    if (expected.TryGetValue(candidate, out var wanted))
                    {
                        content.Should().NotBeNull();
                        using var buffer = new MemoryStream();
                        await content!.CopyToAsync(buffer).ConfigureAwait(true);
                        Encoding.UTF8.GetString(buffer.ToArray()).Should().Be(wanted);
                    }
                    else { content.Should().BeNull(); }
                }
                mvcc.AbortTransaction(observer);
                mvcc.AbortTransaction(latest);
                cursor = await CdcProjectionPump.CatchUpAsync(topic, cursor, sink).ConfigureAwait(true);
                cursor.Should().Be(index + 1);
                (await model.SnapshotAsync().ConfigureAwait(true)).Should().BeEquivalentTo(expected);
                // Deliver the entire retained history again in reverse order. Per-key positions must
                // suppress both duplicate effects and stale resurrection after tombstones.
                var changes = new List<ChangeRecord<int, string>>();
                await foreach (var change in topic.ReadChangesAsync(0).ConfigureAwait(true)) { changes.Add(change); }
                foreach (var change in changes.AsEnumerable().Reverse())
                {
                    await sink.ApplyAsync(change).ConfigureAwait(true);
                }
                (await model.SnapshotAsync().ConfigureAwait(true)).Should().BeEquivalentTo(expected);
                await checkpoints.SaveAsync("oracle", index, JsonSerializer.SerializeToUtf8Bytes(expected)).ConfigureAwait(true);
                if ((index + 1) % 16 == 0)
                {
                    if (fileBacked)
                    {
                        (events as IDisposable)!.Dispose();
                        events = await AppendOnlyEventLog<HistoryEvent>.CreateAsync(eventsPath).ConfigureAwait(true);
                        objects = new FileSystemObjectStore(objectsPath);
                        checkpoints = new FileCheckpointStore(checkpointsPath);
                    }
                    var saved = await checkpoints.LoadAsync("oracle").ConfigureAwait(true);
                    saved!.Value.Offset.Should().Be(index);
                    JsonSerializer.Deserialize<Dictionary<int, string>>(saved.Value.State).Should().BeEquivalentTo(expected);
                    topic = new CheckpointedLogCompactedTopic<int, string>(uncertain, "topic");
                    await topic.RestoreAsync().ConfigureAwait(true);
                    var snapshot = await topic.CaptureSnapshotAsync().ConfigureAwait(true);
                    snapshot.NextPosition.Should().Be(index + 1);
                    snapshot.Records.Where(row => row.Operation != ChangeOperation.Delete)
                        .ToDictionary(row => row.Key, row => row.Value!).Should().BeEquivalentTo(expected);
                    var replay = new Dictionary<int, string>();
                    var position = 0;
                    await foreach (var item in events.ReadFromAsync(0).ConfigureAwait(true))
                    {
                        item.Position.Should().Be(position++);
                        if (item.Event.Value is null) { replay.Remove(item.Event.Key); }
                        else { replay[item.Event.Key] = item.Event.Value; }
                    }
                    position.Should().Be(index + 1);
                    replay.Should().BeEquivalentTo(expected);
                }
                before = expected;
            }
        }
        finally { (events as IDisposable)?.Dispose(); }
    }

    public sealed record HistoryEvent(int Key, string? Value);

    private sealed class SaveThenFail(ICheckpointStore inner) : ICheckpointStore
    {
        internal bool FailNext { get; set; }
        public ValueTask<(long Offset, byte[] State)?> LoadAsync(string processorId, CancellationToken ct = default) =>
            inner.LoadAsync(processorId, ct);
        public async ValueTask SaveAsync(string processorId, long offset, byte[] state, CancellationToken ct = default)
        {
            await inner.SaveAsync(processorId, offset, state, ct).ConfigureAwait(true);
            if (FailNext)
            {
                FailNext = false;
                throw new IOException("Injected response loss after the atomic save.");
            }
        }
    }
}
