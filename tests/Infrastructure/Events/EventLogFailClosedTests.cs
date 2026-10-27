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
/// Fail-closed corruption semantics for <see cref="AppendOnlyEventLog{TEvent}"/>. The
/// IEventLog contract assigns gap-free positions (0, 1, 2, ...), so a reader that silently
/// skips a corrupt MID-LOG frame hands stream/CDC consumers a non-prefix history — a lost
/// change they can never detect. Mid-log corruption (a corrupt frame with valid frames after
/// it) must therefore throw; only a torn TAIL (crash mid-append, nothing valid after) is
/// recoverable, and recovery trims it so appends never bury garbage inside the log.
/// </summary>
public sealed class EventLogFailClosedTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("eventlog-failclosed-").FullName;

    private string NewPath() => Path.Combine(_dir, $"{Guid.NewGuid():N}.log");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static int SecondFrameOffset(byte[] bytes)
    {
        // Frames start with the ASCII magic "BSEL"; find the second occurrence.
        ReadOnlySpan<byte> magic = "BSEL"u8;
        var first = bytes.AsSpan().IndexOf(magic);
        first.Should().Be(0, "the log must start with a frame magic");
        var second = bytes.AsSpan(first + 1).IndexOf(magic);
        second.Should().BeGreaterThan(0, "the log must contain at least two frames");
        return first + 1 + second;
    }

    // The live appender owns a read/write handle. Windows checks sharing in both directions,
    // so raw corruption fixtures must permit that existing writer, just as the log's readers do.
    private static async Task<byte[]> ReadSharedBytesAsync(string path)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var bytes = new MemoryStream();
        await file.CopyToAsync(bytes).ConfigureAwait(true);
        return bytes.ToArray();
    }

    private static async Task WriteSharedBytesAsync(string path, byte[] bytes)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        file.SetLength(bytes.Length);
        await file.WriteAsync(bytes).ConfigureAwait(true);
        file.Flush(flushToDisk: true);
    }

    [Fact]
    public async Task ReadFromAsync_MidLogCorruption_ThrowsInsteadOfSkipping()
    {
        var path = NewPath();
        using (var log = await AppendOnlyEventLog<string>.CreateAsync(path).ConfigureAwait(true))
        {
            await log.AppendAsync("event-0");
            await log.AppendAsync("event-1");
            await log.AppendAsync("event-2");
        }

        // Corrupt one payload byte of the MIDDLE frame — frames 0 and 2 stay intact.
        var bytes = await File.ReadAllBytesAsync(path);
        var secondFrame = SecondFrameOffset(bytes);
        bytes[secondFrame + 25 + 1] ^= 0xFF; // header is 25 bytes; flip a payload byte
        await File.WriteAllBytesAsync(path, bytes);

        // Recovery scans the log at CreateAsync time and fails closed on the mid-log hole.
        var act = async () => await AppendOnlyEventLog<string>.CreateAsync(path).ConfigureAwait(true);
        await act.Should().ThrowAsync<InvalidDataException>(
            "a corrupt frame followed by a valid frame is mid-log data loss, not a torn tail — " +
            "silently skipping it would hand consumers a non-prefix history").ConfigureAwait(true);
    }

    /// <summary>
    /// A reader tailing a log while a writer appends can observe frame N partially visible
    /// (full header, payload short of the declared length) with NO corruption anywhere. The
    /// classification must be scoped to the byte length snapshotted at enumeration start: if
    /// frames appended AFTER the snapshot are allowed to participate, the writer completing
    /// frame N and appending N+1 turns a benign in-flight append into a false
    /// "mid-log data loss … restore from backup" verdict on a healthy log.
    /// </summary>
    [Fact]
    public async Task ReadFromAsync_TailRacingActiveAppender_EndsBenignly_NoFalseMidLogVerdict()
    {
        var path = NewPath();
        using var log = await AppendOnlyEventLog<string>.CreateAsync(path).ConfigureAwait(true);
        await log.AppendAsync("event-0");
        await log.AppendAsync("event-1");

        // Simulate the reader's racy observation window: truncate the file mid-frame-1 (the
        // partially-visible in-flight append), start enumerating, then restore the completed
        // bytes and append frame 2 — exactly what the writer does while the reader is mid-scan.
        var fullBytes = await ReadSharedBytesAsync(path).ConfigureAwait(true);
        var secondFrame = SecondFrameOffset(fullBytes);
        await log.AppendAsync("event-2");
        var withThirdFrame = await ReadSharedBytesAsync(path).ConfigureAwait(true);
        await WriteSharedBytesAsync(path, fullBytes[..(secondFrame + 20)]).ConfigureAwait(true); // torn frame 1

        var seen = new List<string>();
        var enumerator = log.ReadFromAsync(0).GetAsyncEnumerator();
        try
        {
            (await enumerator.MoveNextAsync()).Should().BeTrue("frame 0 is fully visible");
            seen.Add(enumerator.Current.Event);

            // The writer completes frame 1 and appends frame 2 while the reader is parked.
            await WriteSharedBytesAsync(path, withThirdFrame).ConfigureAwait(true);

            // The reader's next step hits the torn-at-snapshot-boundary frame. It must end
            // benignly (a fresh read sees everything) — not throw the unrecoverable
            // mid-log-data-loss verdict on a healthy log.
            var more = await enumerator.MoveNextAsync();
            more.Should().BeFalse(
                "a torn frame at the snapshot boundary is an in-flight append, not corruption");
        }
        finally
        {
            await enumerator.DisposeAsync();
        }

        seen.Should().Equal("event-0");
    }

    [Fact]
    public async Task TornTail_IsEndOfLog_AndReopenTruncatesIt()
    {
        var path = NewPath();
        using (var log = await AppendOnlyEventLog<string>.CreateAsync(path).ConfigureAwait(true))
        {
            await log.AppendAsync("event-0");
            await log.AppendAsync("event-1");
            await log.AppendAsync("event-2");
        }

        // Tear the tail: chop 5 bytes off the final frame (a crash mid-append).
        var bytes = await File.ReadAllBytesAsync(path);
        var thirdFrame = bytes.AsSpan(SecondFrameOffset(bytes) + 1).IndexOf("BSEL"u8) + SecondFrameOffset(bytes) + 1;
        await File.WriteAllBytesAsync(path, bytes[..^5]);

        // Reopen: the torn tail is trimmed so the next append never buries garbage mid-log.
        using var reopened = await AppendOnlyEventLog<string>.CreateAsync(path).ConfigureAwait(true);
        new FileInfo(path).Length.Should().Be(thirdFrame,
            "recovery truncates the torn final frame back to the end of the last valid frame");

        // The two intact events survive; the torn one is gone; the next append reuses its position.
        var positions = new List<long>();
        await foreach (var (pos, _) in reopened.ReadFromAsync(0))
        {
            positions.Add(pos);
        }
        positions.Should().Equal(0, 1);

        (await reopened.AppendAsync("event-2-rewritten")).Should().Be(2);
    }

    /// <summary>
    /// Recovery truncation is idempotent: a second open of an already-trimmed (clean) log
    /// must not re-truncate — in particular it must never trim a VALID trailing frame.
    /// </summary>
    [Fact]
    public async Task TornTailTruncation_SecondOpen_IsANoOp()
    {
        var path = NewPath();
        using (var log = await AppendOnlyEventLog<string>.CreateAsync(path).ConfigureAwait(true))
        {
            await log.AppendAsync("event-0");
            await log.AppendAsync("event-1");
        }

        // Tear the tail, then open once to trim it.
        var bytes = await File.ReadAllBytesAsync(path);
        await File.WriteAllBytesAsync(path, bytes[..^3]);
        using (var first = await AppendOnlyEventLog<string>.CreateAsync(path).ConfigureAwait(true))
        {
        }
        var lengthAfterFirstOpen = new FileInfo(path).Length;

        // Second open of the now-clean file: length unchanged, sole intact event readable.
        using var second = await AppendOnlyEventLog<string>.CreateAsync(path).ConfigureAwait(true);
        new FileInfo(path).Length.Should().Be(lengthAfterFirstOpen,
            "recovery must never re-trim a clean log — a valid trailing frame is not a torn tail");

        var events = new List<string>();
        await foreach (var (_, e) in second.ReadFromAsync(0))
        {
            events.Add(e);
        }
        events.Should().Equal("event-0");
    }

    /// <summary>
    /// Fail-closed propagation through CDC: when the backing file log detects mid-log
    /// corruption (a valid frame after a corrupt one), the CDC source must surface the
    /// InvalidDataException to the consumer — never a silently shortened change stream.
    /// </summary>
    [Fact]
    public async Task CdcSource_OverMidLogCorruptFileLog_PropagatesFailClosed()
    {
        var path = NewPath();
        using var log = await AppendOnlyEventLog<
            Boutquin.Storage.Domain.ValueObjects.ChangeRecord<string, string>>.CreateAsync(path).ConfigureAwait(true);
        await log.AppendAsync(new(0, Boutquin.Storage.Domain.Enums.ChangeOperation.Insert, "k0", "v0", DateTimeOffset.UnixEpoch));
        await log.AppendAsync(new(1, Boutquin.Storage.Domain.Enums.ChangeOperation.Insert, "k1", "v1", DateTimeOffset.UnixEpoch));
        await log.AppendAsync(new(2, Boutquin.Storage.Domain.Enums.ChangeOperation.Insert, "k2", "v2", DateTimeOffset.UnixEpoch));

        // Corrupt the SECOND frame's payload region (mid-log) AFTER open — reads open a fresh
        // stream per call, so the corruption is observed by the next enumeration.
        var bytes = await ReadSharedBytesAsync(path).ConfigureAwait(true);
        var secondFrame = SecondFrameOffset(bytes);
        bytes[secondFrame + 25 + 1] ^= 0xFF; // header is 25 bytes; flip a payload byte
        await WriteSharedBytesAsync(path, bytes).ConfigureAwait(true);

        var cdc = new Boutquin.Storage.Infrastructure.Cdc.EventLogCdcSource<string, string>(log);

        var act = async () =>
        {
            await foreach (var _ in cdc.ReadChangesAsync(0))
            {
            }
        };

        await act.Should().ThrowAsync<InvalidDataException>(
            "mid-log corruption is a hole in the gap-free positions contract and must fail closed through CDC")
            .ConfigureAwait(true);
    }

    [Fact]
    public async Task ReadFromAsync_NegativePosition_Throws_OnBothSiblings()
    {
        var path = NewPath();
        using var fileLog = await AppendOnlyEventLog<string>.CreateAsync(path).ConfigureAwait(true);
        await fileLog.AppendAsync("e");
        var inMemoryLog = new InMemoryEventLog<string>();
        await inMemoryLog.AppendAsync("e");

        var fileAct = async () => { await foreach (var _ in fileLog.ReadFromAsync(-1)) { } };
        var memAct = async () => { await foreach (var _ in inMemoryLog.ReadFromAsync(-1)) { } };

        // Sibling contract: a negative position is a caller bug, not "empty" (InMemory's old
        // behavior) nor "everything" (the file log's old behavior).
        await fileAct.Should().ThrowAsync<ArgumentOutOfRangeException>();
        await memAct.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }
    /// <summary>
    /// AppendAsync refuses any event its own decode cannot read back, so a checksum-valid frame
    /// that fails to deserialize is foreign or forged bytes — the same fail-closed corruption class
    /// as a frame that decodes to null. Reads must surface it as the log's
    /// <see cref="InvalidDataException"/>, not as a raw deserializer exception.
    /// </summary>
    [Theory]
    [InlineData("{")]
    [InlineData("123")]
    [InlineData("[\"a\"]")]
    public async Task ReadFromAsync_ChecksumValidUndecodableFrame_ThrowsInvalidDataException(string payload)
    {
        var path = NewPath();
        using (var log = await AppendOnlyEventLog<string>.CreateAsync(path).ConfigureAwait(true))
        {
            await log.AppendAsync("event-0");
        }

        await using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write))
        {
            await fs.WriteAsync(AppendOnlyEventLog<string>.EncodeFrame(1, System.Text.Encoding.UTF8.GetBytes(payload)));
        }

        using var reopened = await AppendOnlyEventLog<string>.CreateAsync(path).ConfigureAwait(true);
        var read = new List<string>();
        var act = async () =>
        {
            await foreach (var (_, e) in reopened.ReadFromAsync(0))
            {
                read.Add(e);
            }
        };

        (await act.Should().ThrowExactlyAsync<InvalidDataException>(
            "a checksum-valid frame this log could never have written is corrupt or foreign data").ConfigureAwait(true))
            .Which.InnerException.Should().NotBeNull("the deserializer failure is kept as the cause");
        read.Should().Equal(["event-0"], "the valid prefix is still served before the damaged frame");
    }
}
