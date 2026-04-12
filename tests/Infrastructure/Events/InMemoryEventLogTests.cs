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
using Boutquin.Storage.Infrastructure.Events;

namespace Boutquin.Storage.Infrastructure.Tests.Events;

/// <summary>
/// Tests for <see cref="InMemoryEventLog{TEvent}"/>.
/// </summary>
public sealed class InMemoryEventLogTests
{
    [Fact]
    public async Task Append_ReturnsMonotonicallyIncreasingPositions()
    {
        var log = new InMemoryEventLog<string>();

        var p0 = await log.AppendAsync("event-0").ConfigureAwait(true);
        var p1 = await log.AppendAsync("event-1").ConfigureAwait(true);
        var p2 = await log.AppendAsync("event-2").ConfigureAwait(true);

        p0.Should().Be(0);
        p1.Should().Be(1);
        p2.Should().Be(2);
    }

    [Fact]
    public async Task ReadFromZero_ReturnsAllEvents()
    {
        var log = new InMemoryEventLog<string>();
        await log.AppendAsync("a").ConfigureAwait(true);
        await log.AppendAsync("b").ConfigureAwait(true);
        await log.AppendAsync("c").ConfigureAwait(true);

        var events = new List<(long, string)>();
        await foreach (var item in log.ReadFromAsync(0).ConfigureAwait(true))
        {
            events.Add(item);
        }

        events.Should().HaveCount(3);
        events[0].Should().Be((0, "a"));
        events[1].Should().Be((1, "b"));
        events[2].Should().Be((2, "c"));
    }

    [Fact]
    public async Task ReadFromN_SkipsFirstNEvents()
    {
        var log = new InMemoryEventLog<string>();
        await log.AppendAsync("a").ConfigureAwait(true);
        await log.AppendAsync("b").ConfigureAwait(true);
        await log.AppendAsync("c").ConfigureAwait(true);

        var events = new List<(long, string)>();
        await foreach (var item in log.ReadFromAsync(2).ConfigureAwait(true))
        {
            events.Add(item);
        }

        events.Should().ContainSingle();
        events[0].Should().Be((2, "c"));
    }

    [Fact]
    public async Task ReadFromBeyondEnd_YieldsEmpty()
    {
        var log = new InMemoryEventLog<string>();
        await log.AppendAsync("only").ConfigureAwait(true);

        var events = new List<(long, string)>();
        await foreach (var item in log.ReadFromAsync(99).ConfigureAwait(true))
        {
            events.Add(item);
        }

        events.Should().BeEmpty();
    }

    [Fact]
    public async Task EmptyLog_ReadFromZero_YieldsEmpty()
    {
        var log = new InMemoryEventLog<string>();

        var events = new List<(long, string)>();
        await foreach (var item in log.ReadFromAsync(0).ConfigureAwait(true))
        {
            events.Add(item);
        }

        events.Should().BeEmpty();
    }

    [Fact]
    public async Task AppendAfterRead_NewEventsVisibleOnNextRead()
    {
        var log = new InMemoryEventLog<string>();
        await log.AppendAsync("first").ConfigureAwait(true);

        // First read
        var events1 = new List<(long, string)>();
        await foreach (var item in log.ReadFromAsync(0).ConfigureAwait(true))
        {
            events1.Add(item);
        }

        events1.Should().ContainSingle();

        // Append more
        await log.AppendAsync("second").ConfigureAwait(true);

        // Second read
        var events2 = new List<(long, string)>();
        await foreach (var item in log.ReadFromAsync(0).ConfigureAwait(true))
        {
            events2.Add(item);
        }

        events2.Should().HaveCount(2);
    }

    [Fact]
    public async Task ConcurrentAppends_NoLostEvents_UniquePositions()
    {
        var log = new InMemoryEventLog<int>();
        var tasks = new Task<long>[100];

        for (var i = 0; i < tasks.Length; i++)
        {
            var val = i;
            tasks[i] = log.AppendAsync(val).AsTask();
        }

        var positions = await Task.WhenAll(tasks).ConfigureAwait(true);

        // All positions should be unique
        positions.Distinct().Should().HaveCount(100);

        // All events should be present
        var events = new List<(long, int)>();
        await foreach (var item in log.ReadFromAsync(0).ConfigureAwait(true))
        {
            events.Add(item);
        }

        events.Should().HaveCount(100);
    }
}
