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
using Boutquin.Storage.Infrastructure.Streaming.Watermarks;
using Boutquin.Storage.Infrastructure.Streaming.Windows;

namespace Boutquin.Storage.Infrastructure.Tests.Streaming;

public sealed class WindowedAggregatorTests
{
    private static readonly DateTimeOffset s_epoch = DateTimeOffset.UnixEpoch;

    [Fact]
    public void Assigners_UseHalfOpenUtcWindows()
    {
        var tumbling = new TumblingWindow(TimeSpan.FromMinutes(10));
        tumbling.AssignWindows(s_epoch.AddMinutes(17)).Should().ContainSingle()
            .Which.Should().Be(new TimeWindow(s_epoch.AddMinutes(10), s_epoch.AddMinutes(20)));

        var sliding = new SlidingWindow(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(5));
        sliding.AssignWindows(s_epoch.AddMinutes(12)).Should().Equal(
            new TimeWindow(s_epoch.AddMinutes(5), s_epoch.AddMinutes(15)),
            new TimeWindow(s_epoch.AddMinutes(10), s_epoch.AddMinutes(20)));

        var session = new SessionWindow(TimeSpan.FromMinutes(5));
        session.AssignWindows(s_epoch.AddMinutes(12)).Should().ContainSingle()
            .Which.Should().Be(new TimeWindow(s_epoch.AddMinutes(12), s_epoch.AddMinutes(17)));
    }

    [Fact]
    public void Assigners_RejectInvalidDurations()
    {
        var tumbling = () => new TumblingWindow(TimeSpan.Zero);
        var slidingSize = () => new SlidingWindow(TimeSpan.Zero, TimeSpan.FromSeconds(1));
        var slidingStep = () => new SlidingWindow(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
        var session = () => new SessionWindow(TimeSpan.Zero);

        tumbling.Should().Throw<ArgumentOutOfRangeException>();
        slidingSize.Should().Throw<ArgumentOutOfRangeException>();
        slidingStep.Should().Throw<ArgumentOutOfRangeException>();
        session.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Watermark_IsMonotonicAcrossOutOfOrderEvents_AndResettable()
    {
        var policy = new BoundedOutOfOrdernessWatermarkPolicy(TimeSpan.FromMinutes(2));

        policy.Observe(s_epoch.AddMinutes(10)).Timestamp.Should().Be(s_epoch.AddMinutes(8));
        policy.Observe(s_epoch.AddMinutes(5)).Timestamp.Should().Be(s_epoch.AddMinutes(8));

        policy.Reset();
        policy.Current.Timestamp.Should().Be(DateTimeOffset.MinValue);
    }

    [Fact]
    public async Task TumblingWindow_IncludeLateEvent_EmitsRevisionAfterWatermarkClosedWindow()
    {
        var processor = Aggregator(
            new TumblingWindow(TimeSpan.FromHours(1)),
            LateEventPolicy.Include);
        var records = ToAsync(
            Record(0, 0, 1),
            Record(1, 120, 2),
            Record(2, 30, 3));

        var output = await processor.ProcessAsync(records).ToListAsync().ConfigureAwait(true);

        processor.LateEventCount.Should().Be(1);
        output.Should().Contain(item => item.Window.StartInclusive == s_epoch
            && item.Value == 1 && item.Kind == WindowEmissionKind.Final);
        output.Should().Contain(item => item.Window.StartInclusive == s_epoch
            && item.Value == 4 && item.Kind == WindowEmissionKind.Upsert);
        output.Should().Contain(item => item.Window.StartInclusive == s_epoch.AddHours(2) && item.Value == 2);
    }

    [Fact]
    public async Task LateEventPolicies_DropOrThrowExplicitly()
    {
        var records = new[] { Record(0, 60, 4), Record(1, 10, 7) };
        var drop = Aggregator(new TumblingWindow(TimeSpan.FromHours(1)), LateEventPolicy.Drop);
        var dropped = await drop.ProcessAsync(ToAsync(records)).ToListAsync().ConfigureAwait(true);
        dropped.Sum(item => item.Value).Should().Be(4);
        drop.LateEventCount.Should().Be(1);

        var throwing = Aggregator(new TumblingWindow(TimeSpan.FromHours(1)), LateEventPolicy.Throw);
        var act = async () => await throwing.ProcessAsync(ToAsync(records)).ToListAsync().ConfigureAwait(true);
        await act.Should().ThrowAsync<InvalidOperationException>().ConfigureAwait(true);
    }

    [Fact]
    public async Task SessionWindow_MergesEventsSeparatedByLessThanGap()
    {
        var processor = Aggregator(new SessionWindow(TimeSpan.FromMinutes(5)), LateEventPolicy.Include);
        var output = await processor.ProcessAsync(ToAsync(
            Record(0, 0, 1),
            Record(1, 3, 2),
            Record(2, 10, 4))).ToListAsync().ConfigureAwait(true);

        output.Should().HaveCount(2);
        output.Should().Contain(item => item.Window == new TimeWindow(s_epoch, s_epoch.AddMinutes(8))
            && item.Value == 3);
        output.Should().Contain(item => item.Window == new TimeWindow(
            s_epoch.AddMinutes(10), s_epoch.AddMinutes(15)) && item.Value == 4);
    }

    [Fact]
    public async Task SessionWindow_LateBridgeRetractsPublishedWindowsAndUpsertsMergedReplacement()
    {
        var processor = Aggregator(new SessionWindow(TimeSpan.FromMinutes(5)), LateEventPolicy.Include);

        var output = await processor.ProcessAsync(ToAsync(
            Record(0, 0, 1),
            Record(1, 8, 2),
            Record(2, 20, 4),
            Record(3, 4, 8))).ToListAsync().ConfigureAwait(true);

        output.Should().Contain(item => item.Window == new TimeWindow(s_epoch, s_epoch.AddMinutes(5))
            && item.Kind == WindowEmissionKind.Retraction);
        output.Should().Contain(item => item.Window == new TimeWindow(
            s_epoch.AddMinutes(8), s_epoch.AddMinutes(13))
            && item.Kind == WindowEmissionKind.Retraction);
        output.Should().Contain(item => item.Window == new TimeWindow(s_epoch, s_epoch.AddMinutes(13))
            && item.Value == 11
            && item.Kind == WindowEmissionKind.Upsert);
    }

    [Fact]
    public async Task SessionWindow_SeededShuffledEventsConvergeToOfflineEventTimeOracle()
    {
        const int count = 80;
        var random = new Random(1709);
        var values = Enumerable.Range(0, count)
            .Select(index => (Minute: random.Next(0, 180), Value: index + 1))
            .ToArray();
        var arrival = values.OrderBy(_ => random.Next()).ToArray();
        var records = arrival.Select((item, offset) =>
            new StreamRecord<int>(offset, s_epoch.AddMinutes(item.Minute), item.Value)).ToArray();
        var processor = Aggregator(new SessionWindow(TimeSpan.FromMinutes(5)), LateEventPolicy.Include);

        var changes = await processor.ProcessAsync(ToAsync(records)).ToListAsync().ConfigureAwait(true);
        var materialized = new Dictionary<TimeWindow, int>();
        foreach (var change in changes)
        {
            if (change.Kind == WindowEmissionKind.Retraction)
            {
                materialized.Remove(change.Window).Should().BeTrue();
            }
            else
            {
                materialized[change.Window] = change.Value;
            }
        }

        var expected = OfflineSessions(values, TimeSpan.FromMinutes(5));
        materialized.OrderBy(pair => pair.Key.StartInclusive).Should().Equal(
            expected.OrderBy(pair => pair.Key.StartInclusive));
        processor.LateEventCount.Should().BeGreaterThan(0);
    }

    [Theory]
    [InlineData(LateEventPolicy.Drop)]
    [InlineData(LateEventPolicy.Throw)]
    [InlineData(LateEventPolicy.Include)]
    public async Task SessionWindow_OnTimeEventAtClosedSessionEnd_NeverRevisesAFinalWindow(LateEventPolicy policy)
    {
        // k1@0 opens [0,10); k2@10 advances the watermark to 10; k1@10 is on time (not behind
        // the watermark) and, because sessions merge inclusively at their end, extends k1's
        // session. A session must therefore stay open until the watermark is strictly past its
        // end, or its Final emission is revised by an event no policy classifies as late.
        var processor = new WindowedAggregator<int, string, int>(
            new SessionWindow(TimeSpan.FromMinutes(10)),
            new BoundedOutOfOrdernessWatermarkPolicy(TimeSpan.Zero),
            policy,
            value => value >= 100 ? "k2" : "k1",
            () => 0,
            (sum, value) => sum + value,
            (left, right) => left + right);

        var output = await processor.ProcessAsync(ToAsync(
            Record(0, 0, 1),
            Record(1, 10, 100),
            Record(2, 10, 2))).ToListAsync().ConfigureAwait(true);

        processor.LateEventCount.Should().Be(0);
        output.Should().OnlyContain(item => item.Kind == WindowEmissionKind.Final,
            "no event in this input is late, so no emitted window may be retracted or revised");
        output.Should().ContainSingle(item => item.Key == "k1").Which.Should().Be(
            new WindowedAggregate<string, int>(
                new TimeWindow(s_epoch, s_epoch.AddMinutes(20)), "k1", 3, WindowEmissionKind.Final));
        output.Should().ContainSingle(item => item.Key == "k2").Which.Window.Should().Be(
            new TimeWindow(s_epoch.AddMinutes(10), s_epoch.AddMinutes(20)));
    }

    // DDIA tracks each producer's completeness threshold individually and emits a window only once
    // every participating producer's threshold has passed its end. A@100 must not close [0,10)
    // while producer B, whose events are in order, has not yet passed minute 10: B@5 is on time.
    [Theory]
    [InlineData(LateEventPolicy.Drop)]
    [InlineData(LateEventPolicy.Throw)]
    [InlineData(LateEventPolicy.Include)]
    public async Task TumblingWindow_InOrderLaggingProducer_WindowWaitsForEveryProducerThreshold(LateEventPolicy policy)
    {
        var processor = ProducerAggregator(policy, "A", "B");

        var output = await processor.ProcessAsync(ToAsync(
            Record(0, 0, 1) with { Producer = "A" },
            Record(1, 100, 10) with { Producer = "A" },
            Record(2, 5, 2) with { Producer = "B" },
            Record(3, 105, 20) with { Producer = "B" })).ToListAsync().ConfigureAwait(true);

        processor.LateEventCount.Should().Be(0, "each producer's events arrive in order");
        output.Should().Equal(
            new WindowedAggregate<string, int>(
                new TimeWindow(s_epoch, s_epoch.AddMinutes(10)), "all", 3, WindowEmissionKind.Final),
            new WindowedAggregate<string, int>(
                new TimeWindow(s_epoch.AddMinutes(100), s_epoch.AddMinutes(110)), "all", 30, WindowEmissionKind.Final));
    }

    // A record from a producer outside the declared participating set cannot be attributed to a
    // threshold, so the windows it belongs to may already be closed: refuse it explicitly.
    [Fact]
    public async Task ProducerWatermarks_RecordFromUndeclaredProducer_IsRefused()
    {
        var processor = ProducerAggregator(LateEventPolicy.Include, "A", "B");

        var act = async () => await processor.ProcessAsync(ToAsync(
            Record(0, 0, 1) with { Producer = "A" },
            Record(1, 1, 2) with { Producer = "C" })).ToListAsync().ConfigureAwait(true);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*'C'*").ConfigureAwait(true);
    }

    // A policy instance listed for two producers would fold both producers' timestamps into one
    // threshold again, so the per-producer factory must refuse it.
    [Fact]
    public void ProducerWatermarks_PolicyInstanceSharedByTwoProducers_IsRefused()
    {
        var shared = new BoundedOutOfOrdernessWatermarkPolicy(TimeSpan.Zero);

        var act = () => WindowedAggregator<int, string, int>.ForProducers(
            new TumblingWindow(TimeSpan.FromMinutes(10)),
            new Dictionary<string, IWatermarkPolicy> { ["A"] = shared, ["B"] = shared },
            LateEventPolicy.Include,
            _ => "all",
            () => 0,
            (sum, value) => sum + value);

        act.Should().Throw<ArgumentException>().WithParameterName("producerWatermarkPolicies");
    }

    // One shared watermark policy cannot track several producers' thresholds. Records that name
    // two different producers must be refused rather than merged into a single threshold that
    // drops a lagging producer's on-time events as late.
    [Fact]
    public async Task SingleWatermarkPolicy_RecordsFromTwoProducers_AreRefused()
    {
        var processor = Aggregator(new TumblingWindow(TimeSpan.FromMinutes(10)), LateEventPolicy.Drop);

        var act = async () => await processor.ProcessAsync(ToAsync(
            Record(0, 0, 1) with { Producer = "A" },
            Record(1, 100, 10) with { Producer = "A" },
            Record(2, 5, 2) with { Producer = "B" })).ToListAsync().ConfigureAwait(true);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*'B'*").ConfigureAwait(true);
    }

    private static WindowedAggregator<int, string, int> ProducerAggregator(
        LateEventPolicy policy,
        params string[] producers)
        => WindowedAggregator<int, string, int>.ForProducers(
            new TumblingWindow(TimeSpan.FromMinutes(10)),
            producers.ToDictionary(
                producer => producer,
                IWatermarkPolicy (_) => new BoundedOutOfOrdernessWatermarkPolicy(TimeSpan.Zero)),
            policy,
            _ => "all",
            () => 0,
            (sum, value) => sum + value,
            (left, right) => left + right);

    private static WindowedAggregator<int, string, int> Aggregator(
        IWindowAssigner assigner,
        LateEventPolicy policy)
        => new(
            assigner,
            new BoundedOutOfOrdernessWatermarkPolicy(TimeSpan.Zero),
            policy,
            _ => "all",
            () => 0,
            (sum, value) => sum + value,
            (left, right) => left + right);

    private static StreamRecord<int> Record(long offset, int minute, int value)
        => new(offset, s_epoch.AddMinutes(minute), value);

    private static Dictionary<TimeWindow, int> OfflineSessions(
        IEnumerable<(int Minute, int Value)> values,
        TimeSpan gap)
    {
        var ordered = values.OrderBy(item => item.Minute).ToArray();
        var result = new Dictionary<TimeWindow, int>();
        if (ordered.Length == 0)
        {
            return result;
        }

        var start = s_epoch.AddMinutes(ordered[0].Minute);
        var end = start.Add(gap);
        var sum = ordered[0].Value;
        foreach (var item in ordered.Skip(1))
        {
            var timestamp = s_epoch.AddMinutes(item.Minute);
            if (timestamp <= end)
            {
                end = timestamp.Add(gap);
                sum += item.Value;
                continue;
            }

            result[new TimeWindow(start, end)] = sum;
            start = timestamp;
            end = timestamp.Add(gap);
            sum = item.Value;
        }

        result[new TimeWindow(start, end)] = sum;
        return result;
    }

    private static async IAsyncEnumerable<StreamRecord<int>> ToAsync(
        params StreamRecord<int>[] records)
    {
        foreach (var record in records)
        {
            yield return record;
        }
    }
}
