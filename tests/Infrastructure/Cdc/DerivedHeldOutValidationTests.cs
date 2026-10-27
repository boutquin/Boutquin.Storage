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

using System.Text.Json;
using Boutquin.Storage.Distributed.Cdc;
using Boutquin.Storage.Domain.Interfaces.Cdc;
using Boutquin.Storage.Domain.Interfaces.Events;
using Boutquin.Storage.Domain.Interfaces.Streaming;
using Boutquin.Storage.Infrastructure.Analytics;
using Boutquin.Storage.Infrastructure.Cdc;
using Boutquin.Storage.Infrastructure.Events;
using Boutquin.Storage.Infrastructure.Search.Text;
using Boutquin.Storage.Infrastructure.Streaming.Checkpointing;

namespace Boutquin.Storage.Infrastructure.Tests.Cdc;

public sealed class DerivedHeldOutValidationTests
{
    [Fact]
    public async Task Bootstrap_ActualLeaderWritesBetweenSnapshotApplicationAndTailAreRetained()
    {
        using var leader = new SingleLeaderReplication<string, string>();
        await leader.WriteAsync("a", "before-a").ConfigureAwait(true);
        await leader.WriteAsync("b", "before-b").ConfigureAwait(true);
        var source = new SingleLeaderCdcSource<string, string>(leader);
        var model = new InMemoryReadModelStore<string, string>();
        var sink = new HookSink(new ReadModelProjectionSink<string, string>(model), async () =>
        {
            await leader.WriteAsync("a", "after-a").ConfigureAwait(true);
            await leader.WriteAsync("c", "after-c").ConfigureAwait(true);
            leader.AddFollower("f");
            await leader.SyncFollowerAsync("f").ConfigureAwait(true);
        });
        var cursor = await CdcProjectionPump.BootstrapAsync(source, sink).ConfigureAwait(true);
        cursor.Should().Be(4);
        (await model.SnapshotAsync().ConfigureAwait(true)).Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["a"] = "after-a",
            ["b"] = "before-b",
            ["c"] = "after-c",
        });
        (await CdcProjectionPump.CatchUpAsync(source, 0, sink).ConfigureAwait(true)).Should().Be(4);
        (await model.TryGetAsync("a").ConfigureAwait(true)).Value.Should().Be("after-a");
    }

    [Fact]
    public async Task CompactedDelete_SaveAfterCommitFailureRestoresTombstoneAndBothSinksRejectStaleReplacement()
    {
        var directory = TempDirectory();
        try
        {
            var durable = new FileCheckpointStore(directory);
            var topic = new CheckpointedLogCompactedTopic<string, string>(new CommitThenThrow(durable, 3), "topic");
            await topic.RestoreAsync().ConfigureAwait(true);
            await topic.AppendAsync("doc", "original token", ChangeOperation.Insert).ConfigureAwait(true);
            var model = new InMemoryReadModelStore<string, string>();
            var readSink = new ReadModelProjectionSink<string, string>(model);
            var index = new InvertedIndex<string>(new TextAnalyzer(new WhitespaceTokenizer()));
            var textSink = new InvertedIndexProjectionSink<string, string>(index, value => value.AsMemory());
            await CdcProjectionPump.CatchUpAsync(topic, 0, readSink).ConfigureAwait(true);
            await CdcProjectionPump.CatchUpAsync(topic, 0, textSink).ConfigureAwait(true);
            await topic.AppendAsync("doc", "replacement token", ChangeOperation.Update).ConfigureAwait(true);
            await CdcProjectionPump.CatchUpAsync(topic, 1, readSink).ConfigureAwait(true);
            await CdcProjectionPump.CatchUpAsync(topic, 1, textSink).ConfigureAwait(true);
            index.Lookup("original").Should().BeEmpty();
            index.Lookup("replacement").Should().Equal("doc");
            await Assert.ThrowsAsync<IOException>(() => topic.AppendAsync("doc", null, ChangeOperation.Delete).AsTask()).ConfigureAwait(true);
            await Assert.ThrowsAsync<InvalidOperationException>(() => topic.CaptureSnapshotAsync().AsTask()).ConfigureAwait(true);
            var reopened = new CheckpointedLogCompactedTopic<string, string>(new FileCheckpointStore(directory), "topic");
            await reopened.RestoreAsync().ConfigureAwait(true);
            (await reopened.AppendAsync("other", "kept", ChangeOperation.Insert).ConfigureAwait(true)).Should().Be(3);
            (await CdcProjectionPump.CatchUpAsync(reopened, 2, readSink).ConfigureAwait(true)).Should().Be(4);
            (await CdcProjectionPump.CatchUpAsync(reopened, 2, textSink).ConfigureAwait(true)).Should().Be(4);
            var stale = new ChangeRecord<string, string>(1, ChangeOperation.Update, "doc", "replacement token", DateTimeOffset.UnixEpoch);
            await readSink.ApplyAsync(stale).ConfigureAwait(true);
            await textSink.ApplyAsync(stale).ConfigureAwait(true);
            (await model.TryGetAsync("doc").ConfigureAwait(true)).Found.Should().BeFalse();
            index.Lookup("replacement").Should().BeEmpty();
            (await model.TryGetAsync("other").ConfigureAwait(true)).Value.Should().Be("kept");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task MaterializedAggregate_DurableSaveThenThrowDoesNotDoubleApplyOnRetryOrRebuild()
    {
        var directory = TempDirectory();
        try
        {
            using var log = await AppendOnlyEventLog<KeyValuePair<string, decimal>>.CreateAsync(Path.Combine(directory, "input.log")).ConfigureAwait(true);
            await log.AppendAsync(new("Québec", 12.50m)).ConfigureAwait(true);
            await log.AppendAsync(new("US", 4.25m)).ConfigureAwait(true);
            await log.AppendAsync(new("Québec", -2.25m)).ConfigureAwait(true);
            var durable = new FileCheckpointStore(Path.Combine(directory, "checkpoints"));
            var view = View(log, new CommitThenThrow(durable, 1));
            await Assert.ThrowsAsync<IOException>(() => view.RefreshAsync().AsTask()).ConfigureAwait(true);
            (await durable.LoadAsync("aggregate").ConfigureAwait(true))!.Value.Offset.Should().Be(1);
            var retry = await view.RefreshAsync().ConfigureAwait(true);
            retry.State.Should().BeEquivalentTo(new Dictionary<string, decimal> { ["Québec"] = 10.25m, ["US"] = 4.25m });
            retry.Position.Should().Be(2);
            var reopened = View(log, new FileCheckpointStore(Path.Combine(directory, "checkpoints")));
            (await reopened.RefreshAsync().ConfigureAwait(true)).Should().BeEquivalentTo(retry);
            (await reopened.RebuildAsync().ConfigureAwait(true)).Should().BeEquivalentTo(retry);
            (await reopened.RefreshAsync().ConfigureAwait(true)).Should().BeEquivalentTo(retry);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task MaterializedAggregate_ReturnedStateCannotMutateCurrentWithoutAnEvent()
    {
        var log = new InMemoryEventLog<KeyValuePair<string, decimal>>();
        await log.AppendAsync(new("A", 6m)).ConfigureAwait(true);
        var store = new InMemoryCheckpointStore();
        var view = View(log, store);
        var published = await view.RefreshAsync().ConfigureAwait(true);
        if (published.State is IDictionary<string, decimal> dictionary)
        {
            try
            {
                dictionary["A"] = 999m;
            }
            catch (NotSupportedException)
            {
                // A read-only wrapper is a valid implementation of the immutable state contract.
            }
        }
        view.Current.Position.Should().Be(0);
        view.Current.State["A"].Should().Be(6m, "published materialized state must remain immutable between refreshes");
    }

    private static MaterializedAggregateView<string, decimal> View(IEventLog<KeyValuePair<string, decimal>> log, ICheckpointStore checkpoints)
        => new("aggregate", log, checkpoints, (sum, value) => sum + value,
            state => JsonSerializer.SerializeToUtf8Bytes(state),
            bytes => JsonSerializer.Deserialize<Dictionary<string, decimal>>(bytes) ?? throw new InvalidDataException());

    private static string TempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "derived-heldout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed class CommitThenThrow(ICheckpointStore inner, long offsetToFail) : ICheckpointStore
    {
        private bool _failed;
        public ValueTask<(long Offset, byte[] State)?> LoadAsync(string processorId, CancellationToken ct = default)
            => inner.LoadAsync(processorId, ct);
        public async ValueTask SaveAsync(string processorId, long offset, byte[] state, CancellationToken ct = default)
        {
            await inner.SaveAsync(processorId, offset, state, ct).ConfigureAwait(true);
            if (offset == offsetToFail && !_failed)
            {
                _failed = true;
                throw new IOException("Checkpoint committed, acknowledgment lost.");
            }
        }
    }

    private sealed class HookSink(IProjectionSink<ChangeRecord<string, string>> inner, Func<ValueTask> hook)
        : IProjectionSink<ChangeRecord<string, string>>
    {
        private bool _called;
        public async ValueTask ApplyAsync(ChangeRecord<string, string> change, CancellationToken ct = default)
        {
            await inner.ApplyAsync(change, ct).ConfigureAwait(true);
            if (!_called)
            {
                _called = true;
                await hook().ConfigureAwait(true);
            }
        }
    }
}
