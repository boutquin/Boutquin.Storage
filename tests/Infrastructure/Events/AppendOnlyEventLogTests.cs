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
/// Tests for <see cref="AppendOnlyEventLog{TEvent}"/>.
/// </summary>
public sealed class AppendOnlyEventLogTests : IDisposable
{
    private readonly string _dir;
    private readonly string _filePath;

    public AppendOnlyEventLogTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "eventlog-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _filePath = Path.Combine(_dir, "events.log");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public async Task Append_ReturnsMonotonicallyIncreasingPositions()
    {
        using var log = new AppendOnlyEventLog<string>(_filePath);

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
        using var log = new AppendOnlyEventLog<string>(_filePath);
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
        using var log = new AppendOnlyEventLog<string>(_filePath);
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
        using var log = new AppendOnlyEventLog<string>(_filePath);
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
        using var log = new AppendOnlyEventLog<string>(_filePath);

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
        using var log = new AppendOnlyEventLog<string>(_filePath);
        await log.AppendAsync("first").ConfigureAwait(true);

        var events1 = new List<(long, string)>();
        await foreach (var item in log.ReadFromAsync(0).ConfigureAwait(true))
        {
            events1.Add(item);
        }

        events1.Should().ContainSingle();

        await log.AppendAsync("second").ConfigureAwait(true);

        var events2 = new List<(long, string)>();
        await foreach (var item in log.ReadFromAsync(0).ConfigureAwait(true))
        {
            events2.Add(item);
        }

        events2.Should().HaveCount(2);
    }

    [Fact]
    public async Task Recovery_CloseAndReopen_AllEventsRecoverable()
    {
        // Write with first instance
        using (var log1 = new AppendOnlyEventLog<string>(_filePath))
        {
            await log1.AppendAsync("alpha").ConfigureAwait(true);
            await log1.AppendAsync("beta").ConfigureAwait(true);
            await log1.AppendAsync("gamma").ConfigureAwait(true);
        }

        // Reopen and read
        using var log2 = new AppendOnlyEventLog<string>(_filePath);
        var events = new List<(long, string)>();
        await foreach (var item in log2.ReadFromAsync(0).ConfigureAwait(true))
        {
            events.Add(item);
        }

        events.Should().HaveCount(3);
        events[0].Should().Be((0, "alpha"));
        events[1].Should().Be((1, "beta"));
        events[2].Should().Be((2, "gamma"));

        // New append should continue from position 3
        var p3 = await log2.AppendAsync("delta").ConfigureAwait(true);
        p3.Should().Be(3);
    }

    [Fact]
    public async Task ConcurrentAppends_NoLostEvents_UniquePositions()
    {
        using var log = new AppendOnlyEventLog<int>(_filePath);
        var tasks = new Task<long>[20];

        for (var i = 0; i < tasks.Length; i++)
        {
            var val = i;
            tasks[i] = log.AppendAsync(val).AsTask();
        }

        var positions = await Task.WhenAll(tasks).ConfigureAwait(true);
        positions.Distinct().Should().HaveCount(20);

        var events = new List<(long, int)>();
        await foreach (var item in log.ReadFromAsync(0).ConfigureAwait(true))
        {
            events.Add(item);
        }

        events.Should().HaveCount(20);
    }
}
