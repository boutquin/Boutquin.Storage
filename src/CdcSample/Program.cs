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
using System.Runtime.CompilerServices;
using System.Text.Json;
using Boutquin.Storage.Domain.Enums;
using Boutquin.Storage.Domain.Helpers;
using Boutquin.Storage.Domain.Interfaces.Cdc;
using Boutquin.Storage.Domain.Interfaces.Events;
using Boutquin.Storage.Domain.Interfaces.TimeSeries;
using Boutquin.Storage.Domain.ValueObjects;
using Boutquin.Storage.Domain.ValueObjects.LsmTree;
using Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar;
using Boutquin.Storage.Infrastructure.Analytics;
using Boutquin.Storage.Infrastructure.Cdc;
using Boutquin.Storage.Infrastructure.Events;
using Boutquin.Storage.Infrastructure.LsmTree;
using Boutquin.Storage.Infrastructure.Search.Text;
using Boutquin.Storage.Infrastructure.Streaming.Checkpointing;

var directory = Path.Combine(Path.GetTempPath(), "storage-cdc-demo-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    using var journal = await AppendOnlyEventLog<ChangeRecord<SerializableWrapper<int>, SerializableWrapper<string>>>.CreateAsync(Path.Combine(directory, "changes.log")).ConfigureAwait(false);
    var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
        new LsmStorageEngineOptions<SerializableWrapper<int>, SerializableWrapper<string>>(32, Path.Combine(directory, "engine"), "cdc", new SstSegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>>()));
    await using var engineLifetime = engine.ConfigureAwait(false);
    var source = new LsmCdcAdapter<SerializableWrapper<int>, SerializableWrapper<string>>(engine, journal);
    await source.RecoverAsync().ConfigureAwait(false);
    await source.SetAsync(new(1), new("durable storage")).ConfigureAwait(false);
    await source.SetAsync(new(2), new("old document")).ConfigureAwait(false);
    var snapshot = await source.CaptureSnapshotAsync().ConfigureAwait(false);
    await source.SetAsync(new(1), new("searchable storage")).ConfigureAwait(false);
    await source.RemoveAsync(new(2)).ConfigureAwait(false);
    await source.SetAsync(new(3), new("stream processing")).ConfigureAwait(false);

    var model = new InMemoryReadModelStore<SerializableWrapper<int>, SerializableWrapper<string>>();
    var index = new InvertedIndex<SerializableWrapper<int>>(new TextAnalyzer(new WhitespaceTokenizer(), new LowercaseNormalizer()));
    var sink = new CombinedSink(
        new ReadModelProjectionSink<SerializableWrapper<int>, SerializableWrapper<string>>(model),
        new InvertedIndexProjectionSink<SerializableWrapper<int>, SerializableWrapper<string>>(index, value => value.Value.AsMemory()));
    foreach (var change in snapshot.Records) { await sink.ApplyAsync(change).ConfigureAwait(false); }
    var next = await CdcProjectionPump.CatchUpAsync(source, snapshot.NextPosition, sink).ConfigureAwait(false);
    await CdcProjectionPump.CatchUpAsync(source, 0, sink).ConfigureAwait(false);
    var rows = await model.SnapshotAsync().ConfigureAwait(false);
    if (rows.Count != 2 || index.Lookup("old").Count != 0 || index.Lookup("storage").Single().Value != 1 || next != 5)
    {
        throw new InvalidOperationException("Snapshot/tail/replay produced the wrong derived state.");
    }
    var analytics = new MaterializedAggregateView<int, int>("word-count", new WordCountLog(source),
        new FileCheckpointStore(Path.Combine(directory, "analytics")), (_, current) => current,
        state => JsonSerializer.SerializeToUtf8Bytes(state),
        bytes => JsonSerializer.Deserialize<Dictionary<int, int>>(bytes) ?? throw new InvalidDataException("Missing analytics state."));
    var aggregate = await analytics.RefreshAsync().ConfigureAwait(false);
    var rebuilt = await analytics.RebuildAsync().ConfigureAwait(false);
    var date = new DateOnly(2026, 9, 17);
    var batch = new ColumnBatch<WordCountRow>(rows.Count,
        new Dictionary<string, Array> { [nameof(WordCountRow.Words)] = rows.Values.Select(value => value.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length).ToArray() }, date, date);
    var batchTotal = new AggregateOperator<WordCountRow, int, int>(nameof(WordCountRow.Words), 0, (sum, count) => sum + count).Execute(batch);
    if (aggregate.State.Values.Sum() != 4 || rebuilt.State.Values.Sum() != batchTotal || batchTotal != 4)
    {
        throw new InvalidOperationException("CDC-derived analytics replay disagrees with current column-batch state.");
    }
    Console.WriteLine($"Analytics words={batchTotal}; checkpoint replay/rebuild and current column batch agree.");

    var topic = new CheckpointedLogCompactedTopic<SerializableWrapper<int>, SerializableWrapper<string>>(new FileCheckpointStore(Path.Combine(directory, "topic")), "documents");
    await topic.RestoreAsync().ConfigureAwait(false);
    await foreach (var change in source.ReadChangesAsync(0).ConfigureAwait(false))
    {
        await topic.AppendAsync(change.Key, change.Value, change.Operation).ConfigureAwait(false);
    }
    var compacted = await topic.CaptureSnapshotAsync().ConfigureAwait(false);
    Console.WriteLine($"Snapshot cursor={snapshot.NextPosition}; caught-up cursor={next}; live documents={rows.Count}.");
    Console.WriteLine($"Search storage -> {index.Lookup("storage").Single().Value}; deleted document remains absent after replay.");
    Console.WriteLine($"Compacted records={compacted.Records.Count}, retained deletion watermarks={compacted.Records.Count(r => r.Operation == ChangeOperation.Delete)}.");
}
finally { Directory.Delete(directory, true); }

internal sealed class CombinedSink(params IProjectionSink<ChangeRecord<SerializableWrapper<int>, SerializableWrapper<string>>>[] sinks)
    : IProjectionSink<ChangeRecord<SerializableWrapper<int>, SerializableWrapper<string>>>
{
    public async ValueTask ApplyAsync(ChangeRecord<SerializableWrapper<int>, SerializableWrapper<string>> change, CancellationToken ct = default)
    {
        foreach (var sink in sinks) { await sink.ApplyAsync(change, ct).ConfigureAwait(false); }
    }
}

internal readonly record struct WordCountRow(DateOnly Date, int Words) : IHasDate;

// The projection runner requires every source offset: this adapter is for the uncompacted
// LSM change journal. Replacement counts (zero for deletes) avoid double-counting upserts.
internal sealed class WordCountLog(IChangeDataCaptureSource<SerializableWrapper<int>, SerializableWrapper<string>> source)
    : IEventLog<KeyValuePair<int, int>>
{
    public ValueTask<long> AppendAsync(KeyValuePair<int, int> value, CancellationToken ct = default) =>
        throw new NotSupportedException("This is a read-only projection of the source journal.");

    public async IAsyncEnumerable<(long Position, KeyValuePair<int, int> Event)> ReadFromAsync(long position, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var change in source.ReadChangesAsync(position, ct).ConfigureAwait(false))
        {
            var count = change.Operation == ChangeOperation.Delete ? 0 : change.Value!.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            yield return (change.Position, new(change.Key.Value, count));
        }
    }
}
