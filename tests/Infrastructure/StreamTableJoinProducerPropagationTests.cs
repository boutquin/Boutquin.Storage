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
using Boutquin.Storage.Domain.Interfaces.Streaming;
using Boutquin.Storage.Domain.ValueObjects.Streaming;
using Boutquin.Storage.Infrastructure.Streaming;
using Boutquin.Storage.Infrastructure.Streaming.Joins;
using Boutquin.Storage.Infrastructure.Streaming.State;
using Boutquin.Storage.Infrastructure.Streaming.Watermarks;
using Boutquin.Storage.Infrastructure.Streaming.Windows;

namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// A stream-table join transforms values but not provenance: each enriched record keeps the
/// producer identity of the record it enriches, so per-producer event-time consumers downstream
/// of the join still track each producer's completeness threshold individually.
/// </summary>
public sealed class StreamTableJoinProducerPropagationTests
{
    private static readonly DateTimeOffset s_epoch = DateTimeOffset.UnixEpoch;

    [Theory]
    [InlineData(null)]
    [InlineData("A")]
    [InlineData("")]
    public async Task ProcessAsync_CarriesInputProducerOntoEveryOutputRecord(string? producer)
    {
        var join = await JoinAsync().ConfigureAwait(true);

        var output = await join.ProcessAsync(ToAsync(
            new StreamRecord<int>(0, s_epoch, 1) { Producer = producer },
            new StreamRecord<int>(1, s_epoch.AddMinutes(1), 2) { Producer = producer }))
            .ToListAsync().ConfigureAwait(true);

        output.Should().HaveCount(2);
        output.Should().OnlyContain(record => record.Producer == producer);
    }

    [Fact]
    public async Task ProcessAsync_KeepsEachInterleavedRecordsOwnProducer()
    {
        var join = await JoinAsync().ConfigureAwait(true);

        var output = await join.ProcessAsync(ToAsync(
            new StreamRecord<int>(0, s_epoch, 1) { Producer = "A" },
            new StreamRecord<int>(1, s_epoch.AddMinutes(100), 10) { Producer = "A" },
            new StreamRecord<int>(2, s_epoch.AddMinutes(5), 2) { Producer = "B" }))
            .ToListAsync().ConfigureAwait(true);

        output.Select(record => record.Producer).Should().Equal("A", "A", "B");
    }

    // The enrich-then-window pipeline over two in-order producers: B@5 is on time for producer B,
    // so the [0,10) window must wait for B's threshold and total 3 with nothing late.
    [Theory]
    [InlineData(LateEventPolicy.Drop)]
    [InlineData(LateEventPolicy.Include)]
    [InlineData(LateEventPolicy.Throw)]
    public async Task JoinOutput_FeedsPerProducerWindowing_WithoutLosingOnTimeEvents(LateEventPolicy policy)
    {
        var join = await JoinAsync().ConfigureAwait(true);
        var windows = WindowedAggregator<int, string, int>.ForProducers(
            new TumblingWindow(TimeSpan.FromMinutes(10)),
            new Dictionary<string, IWatermarkPolicy>
            {
                ["A"] = new BoundedOutOfOrdernessWatermarkPolicy(TimeSpan.Zero),
                ["B"] = new BoundedOutOfOrdernessWatermarkPolicy(TimeSpan.Zero),
            },
            policy,
            _ => "all",
            () => 0,
            (sum, value) => sum + value,
            (left, right) => left + right);

        var joined = await join.ProcessAsync(ToAsync(
            new StreamRecord<int>(0, s_epoch, 1) { Producer = "A" },
            new StreamRecord<int>(1, s_epoch.AddMinutes(100), 10) { Producer = "A" },
            new StreamRecord<int>(2, s_epoch.AddMinutes(5), 2) { Producer = "B" },
            new StreamRecord<int>(3, s_epoch.AddMinutes(105), 20) { Producer = "B" }))
            .ToListAsync().ConfigureAwait(true);
        var output = await windows.ProcessAsync(ToAsync(joined.ToArray())).ToListAsync().ConfigureAwait(true);

        windows.LateEventCount.Should().Be(0, "each producer's events arrive in order");
        output.Should().Equal(
            new WindowedAggregate<string, int>(
                new TimeWindow(s_epoch, s_epoch.AddMinutes(10)), "all", 3, WindowEmissionKind.Final),
            new WindowedAggregate<string, int>(
                new TimeWindow(s_epoch.AddMinutes(100), s_epoch.AddMinutes(110)), "all", 30, WindowEmissionKind.Final));
    }

    private static async Task<StreamTableJoin<int, int, int, int>> JoinAsync()
    {
        var table = new InMemoryStateStore<int, int>();
        foreach (var key in new[] { 1, 2, 10, 20 })
        {
            await table.SetAsync(key, key).ConfigureAwait(true);
        }

        return new StreamTableJoin<int, int, int, int>(table, value => value, (value, row) => row);
    }

    private static async IAsyncEnumerable<T> ToAsync<T>(params T[] records)
    {
        foreach (var record in records)
        {
            yield return record;
            await Task.Yield();
        }
    }
}
