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
/// Multi-writer coordination tests for <see cref="AppendOnlyEventLog{TEvent}"/>: two live
/// instances over the same log file (the shape a process with more than one DI container
/// produces) must interleave appends without overwriting each other's committed frames.
///
/// <para>Regression anchor (2026-07-14/16, MarketData ingestion log): before the sidecar
/// append lock and per-append offset re-derivation, each writer stream tracked its file
/// offset independently. A second writer's frame landed at a stale offset, burying the first
/// writer's committed frame and shearing the leading magic bytes off the next one — readers
/// then failed with "Event log frame at byte offset N is corrupt but valid frames follow
/// it".</para>
/// </summary>
public sealed class EventLogMultiWriterCoordinationTests : IDisposable
{
    private readonly string _dir;
    private readonly string _filePath;

    public EventLogMultiWriterCoordinationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "eventlog-multiwriter-tests", Guid.NewGuid().ToString("N"));
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
    public async Task InterleavedAppends_FromTwoInstances_PreserveEveryFrame()
    {
        // Writer A pins a stream offset by appending, then writer B appends at the live
        // end, then A appends again. Before the fix, A's second write landed at its stale
        // tracked offset, overwriting B's committed frame.
        using var writerA = await AppendOnlyEventLog<string>.CreateAsync(_filePath).ConfigureAwait(true);
        var p0 = await writerA.AppendAsync("a-0").ConfigureAwait(true);
        var p1 = await writerA.AppendAsync("a-1").ConfigureAwait(true);

        using var writerB = await AppendOnlyEventLog<string>.CreateAsync(_filePath).ConfigureAwait(true);
        var p2 = await writerB.AppendAsync("b-2-with-a-longer-payload-than-the-others").ConfigureAwait(true);

        var p3 = await writerA.AppendAsync("a-3").ConfigureAwait(true);
        var p4 = await writerB.AppendAsync("b-4").ConfigureAwait(true);
        var p5 = await writerA.AppendAsync("a-5").ConfigureAwait(true);

        // Positions must be gap-free and monotone across both writers.
        new[] { p0, p1, p2, p3, p4, p5 }.Should().Equal(0, 1, 2, 3, 4, 5);

        // A same-process reader must replay every acknowledged event exactly once.
        var replayed = new List<(long Position, string Event)>();
        await foreach (var item in writerA.ReadFromAsync(0).ConfigureAwait(true))
        {
            replayed.Add(item);
        }

        replayed.Select(x => x.Position).Should().Equal(0L, 1L, 2L, 3L, 4L, 5L);
        replayed.Select(x => x.Event).Should().Equal(
            "a-0", "a-1", "b-2-with-a-longer-payload-than-the-others", "a-3", "b-4", "a-5");
    }

    [Fact]
    public async Task FreshInstance_AfterInterleavedAppends_RecoversWithoutCorruption()
    {
        using (var writerA = await AppendOnlyEventLog<string>.CreateAsync(_filePath).ConfigureAwait(true))
        {
            await writerA.AppendAsync("a-0").ConfigureAwait(true);
            await writerA.AppendAsync("a-1").ConfigureAwait(true);

            using var writerB = await AppendOnlyEventLog<string>.CreateAsync(_filePath).ConfigureAwait(true);
            await writerB.AppendAsync("b-2-with-a-longer-payload-than-the-others").ConfigureAwait(true);
            await writerA.AppendAsync("a-3").ConfigureAwait(true);
        }

        // A fresh instance (the next consumer in the same or a later process) must recover
        // cleanly. Before the fix this threw InvalidDataException: mid-log corruption.
        using var reader = await AppendOnlyEventLog<string>.CreateAsync(_filePath).ConfigureAwait(true);
        var replayed = new List<string>();
        await foreach (var (_, evt) in reader.ReadFromAsync(0).ConfigureAwait(true))
        {
            replayed.Add(evt);
        }

        replayed.Should().Equal("a-0", "a-1", "b-2-with-a-longer-payload-than-the-others", "a-3");

        // And its next append continues the gap-free position sequence.
        var next = await reader.AppendAsync("c-4").ConfigureAwait(true);
        next.Should().Be(4);
    }

    [Fact]
    public async Task ConcurrentAppends_FromTwoInstances_LoseNothingAndStayGapFree()
    {
        // Hammer the same log from two instances on parallel tasks. Every acknowledged
        // (position, payload) pair must survive to replay — no overwrites, no duplicate
        // positions, no gaps.
        using var writerA = await AppendOnlyEventLog<string>.CreateAsync(_filePath).ConfigureAwait(true);
        using var writerB = await AppendOnlyEventLog<string>.CreateAsync(_filePath).ConfigureAwait(true);

        const int PerWriter = 50;
        var taskA = Task.Run(async () =>
        {
            var acked = new List<(long, string)>();
            for (var i = 0; i < PerWriter; i++)
            {
                var payload = $"a-{i}-{new string('x', i % 17)}";
                acked.Add((await writerA.AppendAsync(payload).ConfigureAwait(false), payload));
            }
            return acked;
        });
        var taskB = Task.Run(async () =>
        {
            var acked = new List<(long, string)>();
            for (var i = 0; i < PerWriter; i++)
            {
                var payload = $"b-{i}-{new string('y', (i * 3) % 23)}";
                acked.Add((await writerB.AppendAsync(payload).ConfigureAwait(false), payload));
            }
            return acked;
        });

        var acknowledged = (await Task.WhenAll(taskA, taskB).ConfigureAwait(true))
            .SelectMany(x => x)
            .ToList();

        var replayed = new List<(long Position, string Event)>();
        await foreach (var item in writerA.ReadFromAsync(0).ConfigureAwait(true))
        {
            replayed.Add(item);
        }

        replayed.Should().HaveCount(2 * PerWriter);
        replayed.Select(x => x.Position).Should().Equal(
            Enumerable.Range(0, 2 * PerWriter).Select(i => (long)i));
        replayed.Select(x => (x.Position, x.Event)).Should().BeEquivalentTo(acknowledged);
    }
}
