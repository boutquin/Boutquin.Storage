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

using Boutquin.Storage.Domain.Interfaces.Streaming;
using Boutquin.Storage.Infrastructure.Events;
using Boutquin.Storage.Infrastructure.Streaming;
using Boutquin.Storage.Infrastructure.Streaming.Checkpointing;
using Boutquin.Storage.Infrastructure.Streaming.Sinks;

namespace Boutquin.Storage.Infrastructure.Tests.Streaming;

public sealed class StreamingDeliveryTests
{
    [Fact]
    public async Task CheckpointReplay_RecoversOffsetAndState()
    {
        var log = new InMemoryEventLog<TimedValue>();
        await log.AppendAsync(new TimedValue(4, DateTimeOffset.UnixEpoch)).ConfigureAwait(true);
        await log.AppendAsync(new TimedValue(7, DateTimeOffset.UnixEpoch.AddMinutes(1))).ConfigureAwait(true);
        var checkpoints = new InMemoryCheckpointStore();
        var sink = new InMemoryStreamSink<int>();

        var first = AtLeastOnce(log, checkpoints, sink);
        (await first.RunAsync().ConfigureAwait(true)).Should().Be(1);
        first.CurrentState.Should().Be(11);

        await log.AppendAsync(new TimedValue(-2, DateTimeOffset.UnixEpoch.AddMinutes(2))).ConfigureAwait(true);
        var restarted = AtLeastOnce(log, checkpoints, sink);
        (await restarted.RunAsync().ConfigureAwait(true)).Should().Be(2);
        restarted.CurrentState.Should().Be(9);
        sink.Outputs.Should().Equal(4, 11, 9);
    }

    [Fact]
    public async Task AtLeastOnce_RetryAfterOutputBeforeCheckpoint_DuplicatesOutput()
    {
        var log = await OneEventLog().ConfigureAwait(true);
        var durable = new InMemoryCheckpointStore();
        var checkpoints = new FailOnceCheckpointStore(durable);
        var sink = new InMemoryStreamSink<int>();
        var processor = AtLeastOnce(log, checkpoints, sink);

        var first = async () => await processor.RunAsync().ConfigureAwait(true);
        await first.Should().ThrowAsync<IOException>().ConfigureAwait(true);
        (await processor.RunAsync().ConfigureAwait(true)).Should().Be(0);

        sink.Outputs.Should().Equal(5, 5);
        processor.CurrentState.Should().Be(5);
    }

    [Fact]
    public async Task EffectivelyOnce_RetryUsesStableOperationIdToSuppressDuplicate()
    {
        var log = await OneEventLog().ConfigureAwait(true);
        var durable = new InMemoryCheckpointStore();
        var checkpoints = new FailOnceCheckpointStore(durable);
        var sink = new InMemoryIdempotentStreamSink<int>();
        var processor = EffectivelyOnce(log, checkpoints, sink);

        var first = async () => await processor.RunAsync().ConfigureAwait(true);
        await first.Should().ThrowAsync<IOException>().ConfigureAwait(true);
        (await processor.RunAsync().ConfigureAwait(true)).Should().Be(0);

        sink.Outputs.Should().Equal(5);
        processor.CurrentState.Should().Be(5);
    }

    [Fact]
    public async Task EventLogStream_ReadsFromInclusiveOffsetAndPreservesSourcePosition()
    {
        var log = new InMemoryEventLog<TimedValue>();
        await log.AppendAsync(new TimedValue(1, DateTimeOffset.UnixEpoch)).ConfigureAwait(true);
        await log.AppendAsync(new TimedValue(2, DateTimeOffset.UnixEpoch.AddMinutes(1))).ConfigureAwait(true);

        var records = await new EventLogStream<TimedValue>(log, value => value.Timestamp)
            .ReadAsync(1).ToListAsync().ConfigureAwait(true);

        records.Should().ContainSingle();
        records[0].Offset.Should().Be(1);
        records[0].Value.Amount.Should().Be(2);
    }

    [Fact]
    public async Task EffectivelyOnce_GappedInputFailsBeforePublishingOutput()
    {
        var sink = new InMemoryIdempotentStreamSink<int>();
        var processor = new EffectivelyOnceProcessor<int, int, int>(
            "gap",
            new GappedStream(),
            new InMemoryCheckpointStore(),
            sink,
            0,
            (state, value) => (state + value, value),
            state => JsonSerializer.SerializeToUtf8Bytes(state),
            bytes => JsonSerializer.Deserialize<int>(bytes));

        var act = async () => await processor.RunAsync().ConfigureAwait(true);

        await act.Should().ThrowAsync<InvalidDataException>().ConfigureAwait(true);
        sink.Outputs.Should().BeEmpty();
    }

    [Fact]
    public async Task EffectivelyOnce_SerializesStateBeforePublishingOutput()
    {
        var log = await OneEventLog().ConfigureAwait(true);
        var sink = new InMemoryIdempotentStreamSink<int>();
        var processor = new EffectivelyOnceProcessor<TimedValue, int, int>(
            "serialization",
            new EventLogStream<TimedValue>(log, value => value.Timestamp),
            new InMemoryCheckpointStore(),
            sink,
            0,
            (state, value) => (state + value.Amount, value.Amount),
            _ => throw new InvalidDataException("Injected state-codec failure."),
            bytes => JsonSerializer.Deserialize<int>(bytes));

        var act = async () => await processor.RunAsync().ConfigureAwait(true);

        await act.Should().ThrowAsync<InvalidDataException>().ConfigureAwait(true);
        sink.Outputs.Should().BeEmpty();
    }

    [Fact]
    public async Task EffectivelyOnce_RejectsUnresumableCheckpointOffset()
    {
        var checkpoints = new InMemoryCheckpointStore();
        await checkpoints.SaveAsync(
            "overflow", long.MaxValue, JsonSerializer.SerializeToUtf8Bytes(0)).ConfigureAwait(true);
        var sink = new InMemoryIdempotentStreamSink<int>();
        var processor = new EffectivelyOnceProcessor<int, int, int>(
            "overflow",
            new GappedStream(),
            checkpoints,
            sink,
            0,
            (state, value) => (state + value, value),
            state => JsonSerializer.SerializeToUtf8Bytes(state),
            bytes => JsonSerializer.Deserialize<int>(bytes));

        var act = async () => await processor.RunAsync().ConfigureAwait(true);

        await act.Should().ThrowAsync<InvalidDataException>().ConfigureAwait(true);
        sink.Outputs.Should().BeEmpty();
    }

    [Fact]
    public async Task EffectivelyOnce_RejectsConcurrentRunsOnSameInstance()
    {
        var log = await OneEventLog().ConfigureAwait(true);
        var sink = new BlockingIdempotentSink();
        var processor = new EffectivelyOnceProcessor<TimedValue, int, int>(
            "concurrent",
            new EventLogStream<TimedValue>(log, value => value.Timestamp),
            new InMemoryCheckpointStore(),
            sink,
            0,
            (state, value) => (state + value.Amount, value.Amount),
            state => JsonSerializer.SerializeToUtf8Bytes(state),
            bytes => JsonSerializer.Deserialize<int>(bytes));

        var first = processor.RunAsync().AsTask();
        await sink.Entered.Task.ConfigureAwait(true);
        var second = async () => await processor.RunAsync().ConfigureAwait(true);

        await second.Should().ThrowAsync<InvalidOperationException>().ConfigureAwait(true);
        sink.Release.TrySetResult();
        (await first.ConfigureAwait(true)).Should().Be(0);
    }

    [Fact]
    public async Task EffectivelyOnce_EveryInjectedCheckpointFailureReplaysToSameStateAndOutput()
    {
        const int eventCount = 12;
        var log = new InMemoryEventLog<TimedValue>();
        for (var value = 1; value <= eventCount; value++)
        {
            await log.AppendAsync(new TimedValue(value, DateTimeOffset.UnixEpoch.AddMinutes(value)))
                .ConfigureAwait(true);
        }

        for (var failAt = 0; failAt < eventCount; failAt++)
        {
            var durable = new InMemoryCheckpointStore();
            var checkpoints = new FailAtOffsetCheckpointStore(durable, failAt);
            var sink = new InMemoryIdempotentStreamSink<int>();
            var processor = EffectivelyOnce(log, checkpoints, sink);

            var interrupted = async () => await processor.RunAsync().ConfigureAwait(true);
            await interrupted.Should().ThrowAsync<IOException>().ConfigureAwait(true);
            (await processor.RunAsync().ConfigureAwait(true)).Should().Be(eventCount - 1);

            processor.CurrentState.Should().Be(eventCount * (eventCount + 1) / 2);
            sink.Outputs.Should().Equal(
                Enumerable.Range(1, eventCount).Select(value => value * (value + 1) / 2));
        }
    }

    [Fact]
    public async Task EffectivelyOnce_RoundTripsInitialStateBeforeRetryingFailedMutableTransition()
    {
        var log = await OneEventLog().ConfigureAwait(true);
        var checkpoints = new FailOnceCheckpointStore(new InMemoryCheckpointStore());
        var sink = new InMemoryIdempotentStreamSink<int>();
        var processor = new EffectivelyOnceProcessor<TimedValue, List<int>, int>(
            "mutable-baseline",
            new EventLogStream<TimedValue>(log, value => value.Timestamp),
            checkpoints,
            sink,
            [],
            (state, value) =>
            {
                state.Add(value.Amount);
                return (state, state.Sum());
            },
            state => JsonSerializer.SerializeToUtf8Bytes(state),
            bytes => JsonSerializer.Deserialize<List<int>>(bytes)
                ?? throw new InvalidDataException("Missing list state."));

        var interrupted = async () => await processor.RunAsync().ConfigureAwait(true);
        await interrupted.Should().ThrowAsync<IOException>().ConfigureAwait(true);
        (await processor.RunAsync().ConfigureAwait(true)).Should().Be(0);

        processor.CurrentState.Should().Equal(5);
        sink.Outputs.Should().Equal(5);
    }

    private static AtLeastOnceProcessor<TimedValue, int, int> AtLeastOnce(
        InMemoryEventLog<TimedValue> log,
        ICheckpointStore checkpoints,
        IStreamSink<int> sink)
        => new(
            "sum",
            new EventLogStream<TimedValue>(log, value => value.Timestamp),
            checkpoints,
            sink,
            0,
            (state, value) => (state + value.Amount, state + value.Amount),
            state => JsonSerializer.SerializeToUtf8Bytes(state),
            bytes => JsonSerializer.Deserialize<int>(bytes));

    private static EffectivelyOnceProcessor<TimedValue, int, int> EffectivelyOnce(
        InMemoryEventLog<TimedValue> log,
        ICheckpointStore checkpoints,
        IIdempotentStreamSink<int> sink)
        => new(
            "sum",
            new EventLogStream<TimedValue>(log, value => value.Timestamp),
            checkpoints,
            sink,
            0,
            (state, value) => (state + value.Amount, state + value.Amount),
            state => JsonSerializer.SerializeToUtf8Bytes(state),
            bytes => JsonSerializer.Deserialize<int>(bytes));

    private static async Task<InMemoryEventLog<TimedValue>> OneEventLog()
    {
        var log = new InMemoryEventLog<TimedValue>();
        await log.AppendAsync(new TimedValue(5, DateTimeOffset.UnixEpoch)).ConfigureAwait(true);
        return log;
    }

    private sealed record TimedValue(int Amount, DateTimeOffset Timestamp);

    private sealed class FailOnceCheckpointStore(ICheckpointStore inner) : ICheckpointStore
    {
        private bool _fail = true;

        public ValueTask<(long Offset, byte[] State)?> LoadAsync(
            string processorId,
            CancellationToken ct = default)
            => inner.LoadAsync(processorId, ct);

        public ValueTask SaveAsync(
            string processorId,
            long offset,
            byte[] state,
            CancellationToken ct = default)
        {
            if (_fail)
            {
                _fail = false;
                throw new IOException("Injected crash after sink write.");
            }

            return inner.SaveAsync(processorId, offset, state, ct);
        }
    }

    private sealed class FailAtOffsetCheckpointStore(ICheckpointStore inner, long failAt) : ICheckpointStore
    {
        private bool _failed;

        public ValueTask<(long Offset, byte[] State)?> LoadAsync(
            string processorId,
            CancellationToken ct = default)
            => inner.LoadAsync(processorId, ct);

        public ValueTask SaveAsync(
            string processorId,
            long offset,
            byte[] state,
            CancellationToken ct = default)
        {
            if (!_failed && offset == failAt)
            {
                _failed = true;
                throw new IOException("Injected checkpoint interruption.");
            }

            return inner.SaveAsync(processorId, offset, state, ct);
        }
    }

    private sealed class GappedStream : IStream<int>
    {
        public async IAsyncEnumerable<Domain.ValueObjects.Streaming.StreamRecord<int>> ReadAsync(
            long offset = 0,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            yield return new Domain.ValueObjects.Streaming.StreamRecord<int>(
                1, DateTimeOffset.UnixEpoch, 5);
            await Task.CompletedTask.ConfigureAwait(true);
        }
    }

    private sealed class BlockingIdempotentSink : IIdempotentStreamSink<int>
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask WriteAsync(
            string operationId,
            int value,
            CancellationToken ct = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(ct).ConfigureAwait(true);
        }
    }
}
