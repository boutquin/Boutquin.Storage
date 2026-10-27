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
using Boutquin.Storage.Domain.Interfaces.Cdc;
using Boutquin.Storage.Domain.ValueObjects.Streaming;
using Boutquin.Storage.Infrastructure.Cdc;
using Boutquin.Storage.Infrastructure.Events;
using Boutquin.Storage.Infrastructure.Streaming.Joins;
using Boutquin.Storage.Infrastructure.Streaming.State;

namespace Boutquin.Storage.Infrastructure.Tests.Streaming;

public sealed class StreamingJoinTests
{
    [Fact]
    public async Task StreamTableJoin_EnrichesFromCdcDerivedLatestState()
    {
        var customers = new InMemoryStateStore<int, string>();
        await customers.SetAsync(7, "Ada").ConfigureAwait(true);
        var join = new StreamTableJoin<Order, int, string, string>(
            customers,
            order => order.CustomerId,
            (order, customer) => $"{customer}:{order.Amount}");

        var output = await join.ProcessAsync(ToAsync(
            new StreamRecord<Order>(0, DateTimeOffset.UnixEpoch, new Order(7, 12m)),
            new StreamRecord<Order>(1, DateTimeOffset.UnixEpoch, new Order(9, 20m))))
            .ToListAsync().ConfigureAwait(true);

        output.Should().ContainSingle().Which.Value.Should().Be("Ada:12");
    }

    [Fact]
    public async Task CdcStateTableUpdater_AppliesSnapshotTailUpdateAndDeleteBeforeJoin()
    {
        var log = new InMemoryEventLog<ChangeRecord<int, string>>();
        var now = DateTimeOffset.UnixEpoch;
        await log.AppendAsync(new ChangeRecord<int, string>(99, ChangeOperation.Snapshot, 7, "Ada", now))
            .ConfigureAwait(true);
        await log.AppendAsync(new ChangeRecord<int, string>(99, ChangeOperation.Update, 7, "Grace", now))
            .ConfigureAwait(true);
        await log.AppendAsync(new ChangeRecord<int, string>(99, ChangeOperation.Insert, 8, "Linus", now))
            .ConfigureAwait(true);
        await log.AppendAsync(new ChangeRecord<int, string>(99, ChangeOperation.Delete, 8, null, now))
            .ConfigureAwait(true);
        var state = new InMemoryStateStore<int, string>();
        var updater = new CdcStateTableUpdater<int, string>(new EventLogCdcSource<int, string>(log), state);

        (await updater.ApplyAsync().ConfigureAwait(true)).Should().Be(3);
        (await state.TryGetAsync(7).ConfigureAwait(true)).Should().Be(("Grace", true));
        (await state.TryGetAsync(8).ConfigureAwait(true)).Found.Should().BeFalse();

        var join = new StreamTableJoin<Order, int, string, string>(
            state,
            order => order.CustomerId,
            (order, customer) => $"{customer}:{order.Amount}");
        var output = await join.ProcessAsync(ToAsync(
            new StreamRecord<Order>(0, now, new Order(7, 12m))))
            .ToListAsync().ConfigureAwait(true);

        output.Should().ContainSingle().Which.Value.Should().Be("Grace:12");
    }

    [Fact]
    public async Task CdcStateTableUpdater_AcceptsOrderedSparsePositionsFromCompactedSource()
    {
        var state = new InMemoryStateStore<int, string>();
        var updater = new CdcStateTableUpdater<int, string>(new SparseCdcSource(), state);

        (await updater.ApplyAsync(2).ConfigureAwait(true)).Should().Be(7);
        (await state.TryGetAsync(1).ConfigureAwait(true)).Value.Should().Be("latest");
    }

    [Fact]
    public async Task CdcStateTableUpdater_RejectsUnknownOperationBeforeOverwritingState()
    {
        var state = new InMemoryStateStore<int, string>();
        var updater = new CdcStateTableUpdater<int, string>(new UnknownOperationCdcSource(), state);

        var act = async () => await updater.ApplyAsync().ConfigureAwait(true);

        await act.Should().ThrowAsync<InvalidDataException>().ConfigureAwait(true);
        (await state.TryGetAsync(1).ConfigureAwait(true)).Value.Should().Be("valid");
    }

    [Fact]
    public async Task TableTableJoin_UsesConsistentPointInTimeSnapshots()
    {
        var customers = new InMemoryStateStore<int, string>();
        var balances = new InMemoryStateStore<int, decimal>();
        await customers.SetAsync(1, "Ada").ConfigureAwait(true);
        await customers.SetAsync(2, "Linus").ConfigureAwait(true);
        await balances.SetAsync(1, 12.5m).ConfigureAwait(true);

        var join = new TableTableJoin<int, string, decimal, string>(
            customers, balances, (key, name, balance) => $"{key}:{name}:{balance}");
        var output = await join.JoinAsync().ConfigureAwait(true);

        output.Should().Equal("1:Ada:12.5");
    }

    [Fact]
    public async Task StreamStreamJoin_MatchesKeysInsideEventTimeTolerance()
    {
        var join = new StreamStreamJoin<Order, Payment, int, string>(
            TimeSpan.FromMinutes(2),
            order => order.CustomerId,
            payment => payment.CustomerId,
            (order, payment) => $"{order.Amount}:{payment.Amount}");
        var left = ToAsync(
            new StreamRecord<Order>(0, DateTimeOffset.UnixEpoch, new Order(7, 12m)),
            new StreamRecord<Order>(1, DateTimeOffset.UnixEpoch.AddMinutes(10), new Order(8, 5m)));
        var right = ToAsync(
            new StreamRecord<Payment>(0, DateTimeOffset.UnixEpoch.AddMinutes(1), new Payment(7, 12m)),
            new StreamRecord<Payment>(1, DateTimeOffset.UnixEpoch.AddMinutes(20), new Payment(8, 5m)));

        var output = await join.JoinAsync(left, right).ToListAsync().ConfigureAwait(true);

        output.Should().ContainSingle().Which.Value.Should().Be("12:12");
    }

    [Fact]
    public async Task StateStore_SnapshotIsDetachedFromLaterWrites()
    {
        var store = new InMemoryStateStore<int, string>();
        await store.SetAsync(1, "first").ConfigureAwait(true);
        var snapshot = await store.SnapshotAsync().ConfigureAwait(true);
        await store.SetAsync(1, "second").ConfigureAwait(true);

        snapshot[1].Should().Be("first");
        (await store.TryGetAsync(1).ConfigureAwait(true)).Value.Should().Be("second");
        (await store.RemoveAsync(1).ConfigureAwait(true)).Should().BeTrue();
    }

    private static async IAsyncEnumerable<T> ToAsync<T>(params T[] records)
    {
        foreach (var record in records)
        {
            yield return record;
        }
    }

    private sealed record Order(int CustomerId, decimal Amount);

    private sealed record Payment(int CustomerId, decimal Amount);

    private sealed class SparseCdcSource : IChangeDataCaptureSource<int, string>
    {
        public async IAsyncEnumerable<ChangeRecord<int, string>> ReadChangesAsync(
            long position,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var timestamp = DateTimeOffset.UnixEpoch;
            yield return new ChangeRecord<int, string>(2, ChangeOperation.Insert, 1, "first", timestamp);
            yield return new ChangeRecord<int, string>(7, ChangeOperation.Update, 1, "latest", timestamp);
            await Task.CompletedTask.ConfigureAwait(true);
        }
    }

    private sealed class UnknownOperationCdcSource : IChangeDataCaptureSource<int, string>
    {
        public async IAsyncEnumerable<ChangeRecord<int, string>> ReadChangesAsync(
            long position,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var timestamp = DateTimeOffset.UnixEpoch;
            yield return new ChangeRecord<int, string>(0, ChangeOperation.Insert, 1, "valid", timestamp);
            yield return new ChangeRecord<int, string>(1, (ChangeOperation)999, 1, "invalid", timestamp);
            await Task.CompletedTask.ConfigureAwait(true);
        }
    }
}
