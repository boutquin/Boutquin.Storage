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
using Boutquin.Storage.Infrastructure.Cdc;
using Boutquin.Storage.Infrastructure.Streaming.Checkpointing;
using Boutquin.Storage.Infrastructure.Streaming.State;

namespace Boutquin.Storage.Infrastructure.Tests.Streaming;

public sealed class StreamingCompactedCdcIntegrationTests
{
    [Fact]
    public async Task RestoredCompactedTopic_SparsePositionsAndDeleteReplayResumeThroughTableUpdater()
    {
        var checkpoints = new InMemoryCheckpointStore();
        var topic = new CheckpointedLogCompactedTopic<string, string>(checkpoints, "sparse-stream-table");
        await topic.RestoreAsync().ConfigureAwait(true);
        await topic.AppendAsync("a", "stale", ChangeOperation.Insert).ConfigureAwait(true);
        await topic.AppendAsync("a", "new", ChangeOperation.Update).ConfigureAwait(true);
        await topic.AppendAsync("b", "removed", ChangeOperation.Insert).ConfigureAwait(true);
        await topic.AppendAsync("b", null, ChangeOperation.Delete).ConfigureAwait(true);

        // Reopening retains only the latest key versions, including the deletion watermark.
        topic = new CheckpointedLogCompactedTopic<string, string>(checkpoints, "sparse-stream-table");
        await topic.RestoreAsync().ConfigureAwait(true);
        var positions = new List<long>();
        await foreach (var change in topic.ReadChangesAsync(0).ConfigureAwait(true))
        {
            positions.Add(change.Position);
        }
        positions.Should().Equal(1, 3);

        var table = new InMemoryStateStore<string, string>();
        await table.SetAsync("b", "prior projection").ConfigureAwait(true);
        var updater = new CdcStateTableUpdater<string, string>(topic, table);
        var position = await updater.ApplyAsync().ConfigureAwait(true);

        position.Should().Be(3);
        (await table.TryGetAsync("a").ConfigureAwait(true)).Should().Be(("new", true));
        (await table.TryGetAsync("b").ConfigureAwait(true)).Found.Should().BeFalse();

        await topic.AppendAsync("c", "tail", ChangeOperation.Insert).ConfigureAwait(true);
        position = await updater.ApplyAsync(position + 1).ConfigureAwait(true);

        position.Should().Be(4);
        (await table.TryGetAsync("a").ConfigureAwait(true)).Should().Be(("new", true));
        (await table.TryGetAsync("c").ConfigureAwait(true)).Should().Be(("tail", true));
        (await updater.ApplyAsync(position + 1).ConfigureAwait(true)).Should().Be(4);
        (await table.SnapshotAsync().ConfigureAwait(true)).Should().HaveCount(2);
    }
}
