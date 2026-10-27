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
using Boutquin.Storage.Domain.Enums;
using Boutquin.Storage.Domain.Interfaces.Streaming;
using Boutquin.Storage.Domain.ValueObjects;
using Boutquin.Storage.Domain.ValueObjects.Streaming;
using Boutquin.Storage.Infrastructure.Cdc;
using Boutquin.Storage.Infrastructure.Events;
using Boutquin.Storage.Infrastructure.Streaming;
using Boutquin.Storage.Infrastructure.Streaming.Checkpointing;
using Boutquin.Storage.Infrastructure.Streaming.Joins;
using Boutquin.Storage.Infrastructure.Streaming.Sinks;
using Boutquin.Storage.Infrastructure.Streaming.State;
using Boutquin.Storage.Infrastructure.Streaming.Watermarks;
using Boutquin.Storage.Infrastructure.Streaming.Windows;

namespace Boutquin.Storage.Samples;

/// <summary>Runs event-time windows, a CDC-backed join, and checkpointed replay.</summary>
internal static class StreamingDemo
{
    public static async Task RunAsync()
    {
        var epoch = DateTimeOffset.UnixEpoch;
        var customerLog = new InMemoryEventLog<ChangeRecord<int, string>>();
        await customerLog.AppendAsync(new ChangeRecord<int, string>(
            0, ChangeOperation.Snapshot, 7, "Ada", epoch)).ConfigureAwait(false);
        var customers = new InMemoryStateStore<int, string>();
        var updater = new CdcStateTableUpdater<int, string>(
            new EventLogCdcSource<int, string>(customerLog), customers);
        _ = await updater.ApplyAsync().ConfigureAwait(false);

        var orderLog = new InMemoryEventLog<Order>();
        await orderLog.AppendAsync(new Order(7, 12m, epoch.AddMinutes(1))).ConfigureAwait(false);
        await orderLog.AppendAsync(new Order(7, 8m, epoch.AddMinutes(3))).ConfigureAwait(false);
        var orders = new EventLogStream<Order>(orderLog, order => order.Timestamp);
        var enrich = new StreamTableJoin<Order, int, string, EnrichedOrder>(
            customers,
            order => order.CustomerId,
            (order, customer) => new EnrichedOrder(customer, order.Amount, order.Timestamp));
        var enriched = await enrich.ProcessAsync(orders.ReadAsync()).ToListAsync().ConfigureAwait(false);
        var windows = new WindowedAggregator<EnrichedOrder, string, decimal>(
            new TumblingWindow(TimeSpan.FromMinutes(5)),
            new BoundedOutOfOrdernessWatermarkPolicy(TimeSpan.FromMinutes(1)),
            LateEventPolicy.Include,
            order => order.Customer,
            () => 0m,
            (total, order) => total + order.Amount);
        var totals = await windows.ProcessAsync(ToAsync(enriched)).ToListAsync().ConfigureAwait(false);

        var checkpoints = new InMemoryCheckpointStore();
        var sink = new InMemoryIdempotentStreamSink<decimal>();
        var joinedLog = new InMemoryEventLog<EnrichedOrder>();
        foreach (var record in enriched)
        {
            await joinedLog.AppendAsync(record.Value).ConfigureAwait(false);
        }

        var processor = Checkpointed(joinedLog, checkpoints, sink);
        _ = await processor.RunAsync().ConfigureAwait(false);
        await joinedLog.AppendAsync(new EnrichedOrder("Ada", 5m, epoch.AddMinutes(6))).ConfigureAwait(false);
        var recovered = Checkpointed(joinedLog, checkpoints, sink);
        var offset = await recovered.RunAsync().ConfigureAwait(false);

        if (totals.Count != 1 || totals[0].Value != 20m
            || recovered.CurrentState != 25m || offset != 2 || sink.Outputs.Count != 3)
        {
            throw new InvalidOperationException("Streaming demo did not converge after checkpoint replay.");
        }

        Console.WriteLine($"Event-time window: {totals[0].Key} total={totals[0].Value}");
        Console.WriteLine($"Processing-time catch-up: offset={offset}, total={recovered.CurrentState}");
        Console.WriteLine("Streaming demo passed: CDC join, watermark window, and replay agree.");
    }

    private static EffectivelyOnceProcessor<EnrichedOrder, decimal, decimal> Checkpointed(
        InMemoryEventLog<EnrichedOrder> log,
        ICheckpointStore checkpoints,
        IIdempotentStreamSink<decimal> sink)
        => new(
            "order-total",
            new EventLogStream<EnrichedOrder>(log, order => order.Timestamp),
            checkpoints,
            sink,
            0m,
            (state, order) => (state + order.Amount, state + order.Amount),
            state => JsonSerializer.SerializeToUtf8Bytes(state),
            bytes => JsonSerializer.Deserialize<decimal>(bytes));

    private sealed record Order(int CustomerId, decimal Amount, DateTimeOffset Timestamp);

    private sealed record EnrichedOrder(string Customer, decimal Amount, DateTimeOffset Timestamp);

    private static async IAsyncEnumerable<StreamRecord<EnrichedOrder>> ToAsync(
        IEnumerable<StreamRecord<EnrichedOrder>> records)
    {
        foreach (var record in records)
        {
            yield return record;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }
}
