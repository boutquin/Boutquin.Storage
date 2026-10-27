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
/// Crash-atomicity of <see cref="AppendOnlyEventLog{TEvent}"/> appends. A cancelled or faulted
/// append must leave the log at exactly its pre-append committed byte length — never a buffered
/// PARTIAL frame. A partial frame followed by the next append's valid frame is classified by the
/// reader as mid-log corruption, which fails closed and renders the WHOLE log permanently
/// unreadable. The invariant pinned here: a faulted append is a no-op on the on-disk log.
/// </summary>
public sealed class EventLogPartialFrameAtomicityTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("eventlog-atomicity-").FullName;

    private string NewPath() => Path.Combine(_dir, $"{Guid.NewGuid():N}.log");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task FaultedAppend_LeavesLogAtPreAppendLength_AndStaysReadable()
    {
        var path = NewPath();
        using var log = await AppendOnlyEventLog<string>.CreateAsync(path).ConfigureAwait(true);

        // Two committed events; capture the committed on-disk length.
        await log.AppendAsync("event-0").ConfigureAwait(true);
        await log.AppendAsync("event-1").ConfigureAwait(true);
        var committedLength = new FileInfo(path).Length;

        // Inject a torn write for the NEXT append: write the first half of the frame's bytes to
        // disk (durably flushed) and then fault — exactly the mid-frame crash the three-write
        // append used to leave behind as a poisoning partial frame.
        log._frameWriteFaultHook = async (stream, frame, ct) =>
        {
            await stream.WriteAsync(frame.AsMemory(0, frame.Length / 2), ct).ConfigureAwait(true);
            ((FileStream)stream).Flush(flushToDisk: true);
            throw new IOException("Injected I/O fault mid-frame.");
        };

        var faulted = async () => await log.AppendAsync("event-2-doomed").ConfigureAwait(true);
        await faulted.Should().ThrowAsync<IOException>().ConfigureAwait(true);

        // Invariant: the faulted append must be a no-op on the on-disk log — no partial frame.
        new FileInfo(path).Length.Should().Be(committedLength,
            "a faulted append must leave the log at exactly its pre-append committed length — " +
            "a buffered partial frame poisons the next append into mid-log corruption");

        // And the previously-committed events must still read back cleanly (no mid-log poison).
        var events = new List<string>();
        using var reopened = await AppendOnlyEventLog<string>.CreateAsync(path).ConfigureAwait(true);
        await foreach (var (_, e) in reopened.ReadFromAsync(0).ConfigureAwait(true))
        {
            events.Add(e);
        }

        events.Should().Equal("event-0", "event-1");
    }
}
