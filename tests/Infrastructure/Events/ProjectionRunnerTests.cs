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

using Boutquin.Storage.Domain.Interfaces.Events;
using Boutquin.Storage.Domain.Interfaces.Streaming;
using Boutquin.Storage.Infrastructure.Events;
using Boutquin.Storage.Infrastructure.Streaming.Checkpointing;

namespace Boutquin.Storage.Infrastructure.Tests.Events;

public sealed class ProjectionRunnerTests
{
    [Fact]
    public async Task Restart_RecoversStateAndOffset_ThenRebuildProducesSameResult()
    {
        var directory = Path.Combine(Path.GetTempPath(), "projection-" + Guid.NewGuid().ToString("N"));
        try
        {
            var log = new InMemoryEventLog<int>();
            await log.AppendAsync(4).ConfigureAwait(true);
            await log.AppendAsync(7).ConfigureAwait(true);
            var first = Runner(log, new FileCheckpointStore(directory));
            var initial = await first.RunAsync().ConfigureAwait(true);
            Assert.Equal(11, initial.State);
            Assert.Equal(1, initial.Position);

            await log.AppendAsync(-2).ConfigureAwait(true);
            var restarted = Runner(log, new FileCheckpointStore(directory));
            var recovered = await restarted.RunAsync().ConfigureAwait(true);
            Assert.Equal(9, recovered.State);
            Assert.Equal(2, recovered.Position);
            var repeated = await restarted.RunAsync().ConfigureAwait(true);
            Assert.Equal(recovered, repeated);

            var rebuilt = await new ProjectionRebuilder<int, int>(log, new SumProjection())
                .RebuildAsync().ConfigureAwait(true);
            Assert.Equal(recovered, rebuilt);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task FailedCheckpoint_RetriesIdempotentOutput_WithoutDoubleApplyingState()
    {
        var log = new InMemoryEventLog<int>();
        await log.AppendAsync(5).ConfigureAwait(true);
        var durable = new InMemoryCheckpointStore();
        var checkpoints = new FailOnceCheckpointStore(durable);
        var projector = new CountingProjector();
        var runner = Runner(log, checkpoints, projector);
        await Assert.ThrowsAsync<IOException>(async () =>
            await runner.RunAsync().ConfigureAwait(true)).ConfigureAwait(true);
        Assert.Null(await durable.LoadAsync("sum").ConfigureAwait(true));

        var result = await runner.RunAsync().ConfigureAwait(true);
        Assert.Equal(5, result.State);
        Assert.Equal(2, projector.Attempts);
        Assert.Single(projector.Applied);
    }

    [Fact]
    public async Task EmptyLog_ReturnsInitialState_AndCancellationDoesNotSave()
    {
        var checkpoints = new InMemoryCheckpointStore();
        var runner = Runner(new InMemoryEventLog<int>(), checkpoints);
        var result = await runner.RunAsync().ConfigureAwait(true);
        Assert.Equal(-1, result.Position);
        Assert.Equal(0, result.State);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await runner.RunAsync(cancellation.Token).ConfigureAwait(true)).ConfigureAwait(true);
        Assert.Null(await checkpoints.LoadAsync("sum").ConfigureAwait(true));
    }

    [Fact]
    public async Task CorruptCheckpoint_FailsClosed_InsteadOfRestartingFromZero()
    {
        var checkpoints = new InMemoryCheckpointStore();
        await checkpoints.SaveAsync("sum", -2, JsonSerializer.SerializeToUtf8Bytes(10)).ConfigureAwait(true);
        var runner = Runner(new InMemoryEventLog<int>(), checkpoints);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await runner.RunAsync().ConfigureAwait(true)).ConfigureAwait(true);
    }

    [Fact]
    public async Task ReadModel_RejectsStaleUpdates_AndRetainsDeleteWatermark()
    {
        var store = new InMemoryReadModelStore<string, string>();
        await store.SetAsync("a", "first", 0).ConfigureAwait(true);
        await store.SetAsync("a", "latest", 2).ConfigureAwait(true);
        await store.SetAsync("a", "stale", 1).ConfigureAwait(true);
        Assert.Equal(("latest", true), await store.TryGetAsync("a").ConfigureAwait(true));
        await store.RemoveAsync("a", 3).ConfigureAwait(true);
        await store.SetAsync("a", "replayed", 2).ConfigureAwait(true);
        Assert.False((await store.TryGetAsync("a").ConfigureAwait(true)).Found);
        Assert.Empty(await store.SnapshotAsync().ConfigureAwait(true));
    }

    [Fact]
    public async Task Runner_RejectsGappedInput_BeforePublishingOutput()
    {
        var projector = new CountingProjector();
        var runner = Runner(new GappedLog(), new InMemoryCheckpointStore(), projector);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await runner.RunAsync().ConfigureAwait(true)).ConfigureAwait(true);
        Assert.Empty(projector.Applied);
    }

    [Fact]
    public async Task EveryInterruptedSaveBoundary_ReplaysToTheSameFold()
    {
        var random = new Random(1709);
        var events = Enumerable.Range(0, 20).Select(_ => random.Next(-50, 51)).ToArray();
        var log = new InMemoryEventLog<int>();
        foreach (var value in events)
        {
            await log.AppendAsync(value).ConfigureAwait(true);
        }
        for (var failAt = 0; failAt < events.Length; failAt++)
        {
            var checkpoints = new FailOnceCheckpointStore(new InMemoryCheckpointStore(), failAt);
            var projector = new CountingProjector();
            var runner = Runner(log, checkpoints, projector);
            await Assert.ThrowsAsync<IOException>(async () =>
                await runner.RunAsync().ConfigureAwait(true)).ConfigureAwait(true);
            var resumed = await runner.RunAsync().ConfigureAwait(true);
            Assert.Equal(events.Sum(), resumed.State);
            Assert.Equal(events.Length - 1, resumed.Position);
            Assert.Equal(Enumerable.Range(0, events.Length).Select(i => (long)i), projector.Applied.Order());
            Assert.Equal(events.Length + 1, projector.Attempts);
        }
    }

    private static ProjectionRunner<int, int> Runner(IEventLog<int> log, ICheckpointStore checkpoints,
        IProjector<int>? projector = null) => new("sum", log, new SumProjection(), checkpoints,
            state => JsonSerializer.SerializeToUtf8Bytes(state),
            bytes => JsonSerializer.Deserialize<int>(bytes), projector);

    private sealed class SumProjection : IProjection<int, int>
    {
        public int InitialState => 0;
        public int Apply(int state, int @event) => checked(state + @event);
    }

    private sealed class CountingProjector : IProjector<int>
    {
        public int Attempts { get; private set; }
        public HashSet<long> Applied { get; } = [];
        public ValueTask ApplyAsync(long position, int @event, CancellationToken ct = default)
        {
            Attempts++;
            Applied.Add(position);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailOnceCheckpointStore(ICheckpointStore inner, long failAt = 0) : ICheckpointStore
    {
        private bool _fail = true;
        public ValueTask<(long Offset, byte[] State)?> LoadAsync(string processorId, CancellationToken ct = default)
            => inner.LoadAsync(processorId, ct);
        public ValueTask SaveAsync(string processorId, long offset, byte[] state, CancellationToken ct = default)
        {
            if (_fail && offset == failAt)
            {
                _fail = false;
                throw new IOException("Injected interruption after output and before checkpoint.");
            }
            return inner.SaveAsync(processorId, offset, state, ct);
        }
    }

    private sealed class GappedLog : IEventLog<int>
    {
        public ValueTask<long> AppendAsync(int @event, CancellationToken ct = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<(long Position, int Event)> ReadFromAsync(long position,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            yield return (1, 10);
            await Task.CompletedTask.ConfigureAwait(true);
        }
    }
}
