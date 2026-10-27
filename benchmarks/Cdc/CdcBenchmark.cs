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
using BenchmarkDotNet.Attributes;
using Boutquin.Storage.Domain.Enums;
using Boutquin.Storage.Domain.ValueObjects;
using Boutquin.Storage.Infrastructure.Cdc;
using Boutquin.Storage.Infrastructure.Events;
using Boutquin.Storage.Infrastructure.Streaming.Checkpointing;

namespace Boutquin.Storage.Cdc.Benchmarks;

/// <summary>Contrasts complete compacted-state checkpointing with position-deduplicated in-memory projection.</summary>
[MemoryDiagnoser]
public class CdcBenchmark
{
    /// <summary>Gets or sets the input count, with one hundred distinct keys.</summary>
    [Params(100, 1000)]
    public int Count { get; set; }

    /// <summary>Writes all changes through the compacted checkpoint boundary.</summary>
    /// <returns>The exclusive next cursor.</returns>
    [Benchmark]
    public async Task<long> CompactRepeatedKeys()
    {
        var topic = new CheckpointedLogCompactedTopic<int, string>(new InMemoryCheckpointStore(), "benchmark");
        await topic.RestoreAsync().ConfigureAwait(false);
        for (var i = 0; i < Count; i++)
        {
            await topic.AppendAsync(i % 100, "value", ChangeOperation.Update).ConfigureAwait(false);
        }
        return (await topic.CaptureSnapshotAsync().ConfigureAwait(false)).NextPosition;
    }

    /// <summary>Projects the same updates and replays each once to exercise deduplication.</summary>
    /// <returns>The retained live-key count.</returns>
    [Benchmark]
    public async Task<int> ProjectReadModel()
    {
        var model = new InMemoryReadModelStore<int, string>();
        var sink = new ReadModelProjectionSink<int, string>(model);
        for (var i = 0; i < Count; i++)
        {
            var record = new ChangeRecord<int, string>(i, ChangeOperation.Update, i % 100, "value", DateTimeOffset.UnixEpoch);
            await sink.ApplyAsync(record).ConfigureAwait(false);
            await sink.ApplyAsync(record).ConfigureAwait(false);
        }
        return (await model.SnapshotAsync().ConfigureAwait(false)).Count;
    }
}
