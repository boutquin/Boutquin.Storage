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
        using var log = await AppendOnlyEventLog<string>.CreateAsync(_filePath).ConfigureAwait(true);

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
        using var log = await AppendOnlyEventLog<string>.CreateAsync(_filePath).ConfigureAwait(true);
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
        using var log = await AppendOnlyEventLog<string>.CreateAsync(_filePath).ConfigureAwait(true);
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
        using var log = await AppendOnlyEventLog<string>.CreateAsync(_filePath).ConfigureAwait(true);
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
        using var log = await AppendOnlyEventLog<string>.CreateAsync(_filePath).ConfigureAwait(true);

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
        using var log = await AppendOnlyEventLog<string>.CreateAsync(_filePath).ConfigureAwait(true);
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
        using (var log1 = await AppendOnlyEventLog<string>.CreateAsync(_filePath).ConfigureAwait(true))
        {
            await log1.AppendAsync("alpha").ConfigureAwait(true);
            await log1.AppendAsync("beta").ConfigureAwait(true);
            await log1.AppendAsync("gamma").ConfigureAwait(true);
        }

        // Reopen and read
        using var log2 = await AppendOnlyEventLog<string>.CreateAsync(_filePath).ConfigureAwait(true);
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
        using var log = await AppendOnlyEventLog<int>.CreateAsync(_filePath).ConfigureAwait(true);
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

    [Fact]
    public async Task CreateAsync_RecoverPosition_AfterReopen()
    {
        // Write with first instance, close it, then CreateAsync a second instance and
        // verify that recovery correctly resumes the position counter from where we left off.
        using (var first = await AppendOnlyEventLog<string>.CreateAsync(_filePath).ConfigureAwait(true))
        {
            await first.AppendAsync("x").ConfigureAwait(true);
            await first.AppendAsync("y").ConfigureAwait(true);
        }

        using var second = await AppendOnlyEventLog<string>.CreateAsync(_filePath).ConfigureAwait(true);
        // Recovery should have set nextPosition = 2 (the next available position).
        var p2 = await second.AppendAsync("z").ConfigureAwait(true);
        p2.Should().Be(2, "CreateAsync must recover position from the existing log asynchronously");
    }
    [Fact]
    public async Task ReadFrom_CrcValidFrameHoldingJsonNull_FailsClosedWithInvalidDataException()
    {
        // AppendAsync never frames a null event, so a CRC-valid frame whose payload is the JSON
        // literal null can only be foreign or forged bytes — the log's fail-closed corruption
        // class, not an InvalidOperationException.
        var payload = "null"u8.ToArray();
        var frame = EventLogFrames.Encode(0, payload);
        await File.WriteAllBytesAsync(_filePath, frame).ConfigureAwait(true);

        using var log = await AppendOnlyEventLog<string>.CreateAsync(_filePath).ConfigureAwait(true);
        var act = async () =>
        {
            await foreach (var _ in log.ReadFromAsync(0).ConfigureAwait(true))
            {
            }
        };

        await act.Should().ThrowAsync<InvalidDataException>(
            "an unreadable CRC-valid frame is corruption and must surface the log's typed fail-closed exception")
            .ConfigureAwait(true);
    }

    [Fact]
    public async Task ReadFrom_SchemaEnvelope_PreservesSchemaVersion()
    {
        // SchemaVersion is a struct whose only state is a get-only property set by a validating
        // constructor. Persisted through the log's default JSON encoding, the version must decode
        // to the value written — not to the struct's default 0, which its own constructor rejects.
        var written = new SchemaEnvelope<string>(new SchemaVersion(7), "payload", DateTimeOffset.UnixEpoch);
        using var log = await AppendOnlyEventLog<SchemaEnvelope<string>>.CreateAsync(_filePath).ConfigureAwait(true);
        await log.AppendAsync(written).ConfigureAwait(true);

        var read = new List<SchemaEnvelope<string>>();
        await foreach (var (_, envelope) in log.ReadFromAsync(0).ConfigureAwait(true))
        {
            read.Add(envelope);
        }

        read.Should().ContainSingle();
        read[0].Version.Value.Should().Be(7, "the persisted schema version must survive the JSON round trip");
        read[0].Should().Be(written);
    }

    [Fact]
    public async Task Recovery_EventEnvelope_PreservesSchemaVersionAcrossReopen()
    {
        var written = new EventEnvelope<string, int>(new SchemaVersion(7), "k", 42, DateTimeOffset.UnixEpoch, 3);
        using (var first = await AppendOnlyEventLog<EventEnvelope<string, int>>.CreateAsync(_filePath).ConfigureAwait(true))
        {
            await first.AppendAsync(written).ConfigureAwait(true);
        }

        using var reopened = await AppendOnlyEventLog<EventEnvelope<string, int>>.CreateAsync(_filePath).ConfigureAwait(true);
        var read = new List<EventEnvelope<string, int>>();
        await foreach (var (_, envelope) in reopened.ReadFromAsync(0).ConfigureAwait(true))
        {
            read.Add(envelope);
        }

        read.Should().ContainSingle();
        read[0].Version.Value.Should().Be(7, "write -> reopen -> read must reconstruct the persisted schema version");
        read[0].Should().Be(written);
    }

    [Fact]
    public async Task Append_EventEnvelopeWithDefaultSchemaVersion_IsRejectedAndLogStaysReadable()
    {
        // default(SchemaVersion) carries Value 0, which the read path refuses. Acknowledging it
        // would leave a CRC-valid frame that makes every replay through its position throw, so
        // the append must refuse it before any position is assigned.
        using (var first = await AppendOnlyEventLog<EventEnvelope<string, int>>.CreateAsync(_filePath).ConfigureAwait(true))
        {
            var poison = new EventEnvelope<string, int>(default, "k0", 0, DateTimeOffset.UnixEpoch, 0);
            var act = async () => await first.AppendAsync(poison).ConfigureAwait(true);
            await act.Should().ThrowAsync<ArgumentException>(
                "an event the log's reader cannot decode must never be acknowledged").ConfigureAwait(true);

            var valid = new EventEnvelope<string, int>(new SchemaVersion(2), "k1", 1, DateTimeOffset.UnixEpoch, 1);
            (await first.AppendAsync(valid).ConfigureAwait(true)).Should().Be(0,
                "the refused append must not consume a position");
        }

        using var reopened = await AppendOnlyEventLog<EventEnvelope<string, int>>.CreateAsync(_filePath).ConfigureAwait(true);
        var read = new List<(long Position, EventEnvelope<string, int> Event)>();
        await foreach (var item in reopened.ReadFromAsync(0).ConfigureAwait(true))
        {
            read.Add(item);
        }

        read.Should().ContainSingle();
        read[0].Position.Should().Be(0);
        read[0].Event.Version.Value.Should().Be(2);
    }

    private const int EventFrameHeaderSize = 25;

    /// <summary>
    /// Appends "a", "b", "c" and rewrites the final frame so only zeros follow its header (the
    /// payload never landed), returning the final frame's start and the rewritten image.
    /// </summary>
    private async Task<(int FinalStart, byte[] Image)> WriteEventsWithZeroedFinalPayloadAsync()
    {
        int finalStart;
        using (var log = await AppendOnlyEventLog<string>.CreateAsync(_filePath).ConfigureAwait(true))
        {
            await log.AppendAsync("a").ConfigureAwait(true);
            await log.AppendAsync("b").ConfigureAwait(true);
            finalStart = (int)new FileInfo(_filePath).Length;
            await log.AppendAsync("c").ConfigureAwait(true);
        }

        var image = await File.ReadAllBytesAsync(_filePath).ConfigureAwait(true);
        Array.Clear(image, finalStart + EventFrameHeaderSize, image.Length - finalStart - EventFrameHeaderSize);
        return (finalStart, image);
    }

    /// <summary>
    /// The final frame's header fully landed — its last byte, the header checksum's last byte, is
    /// non-zero — yet fails its checksum, and only zeros follow it. An interrupted append leaves a
    /// strict header prefix whose last byte is zero, so this is damage to an acknowledged frame:
    /// opening and reading must fail loud and the file must not be trimmed.
    /// </summary>
    [Theory]
    [InlineData(6)]
    [InlineData(EventFrameHeaderSize - 1)]
    public async Task CreateAsync_CompleteFinalHeaderFailingItsChecksum_FailsLoudAndNeverTrims(int damagedByte)
    {
        var (finalStart, image) = await WriteEventsWithZeroedFinalPayloadAsync().ConfigureAwait(true);
        image[finalStart + EventFrameHeaderSize - 1].Should().NotBe(0,
            "the fixture needs a header whose every byte landed, including the last checksum byte");
        image[finalStart + damagedByte] ^= 0x01;
        await File.WriteAllBytesAsync(_filePath, image).ConfigureAwait(true);

        var open = async () => await AppendOnlyEventLog<string>.CreateAsync(_filePath).ConfigureAwait(true);

        await open.Should().ThrowAsync<InvalidDataException>(
            "a header whose every byte landed cannot be an interrupted append").ConfigureAwait(true);
        (await File.ReadAllBytesAsync(_filePath).ConfigureAwait(true)).Should().Equal(image,
            "a fail-loud recovery must not trim the acknowledged final frame away");
    }

    /// <summary>
    /// Must-recover counterpart: the final frame torn inside its header — a strict prefix, so the
    /// last header byte is zero — followed by zeros is an interrupted append and is trimmed.
    /// </summary>
    [Theory]
    [InlineData(13)]
    [InlineData(EventFrameHeaderSize - 1)]
    public async Task CreateAsync_FinalHeaderPrefixFollowedByZeros_IsTrimmedAsATornTail(int landedBytes)
    {
        var (finalStart, image) = await WriteEventsWithZeroedFinalPayloadAsync().ConfigureAwait(true);
        Array.Clear(image, finalStart + landedBytes, EventFrameHeaderSize - landedBytes);
        await File.WriteAllBytesAsync(_filePath, image).ConfigureAwait(true);

        using var log = await AppendOnlyEventLog<string>.CreateAsync(_filePath).ConfigureAwait(true);
        var events = new List<string>();
        await foreach (var (_, e) in log.ReadFromAsync(0).ConfigureAwait(true))
        {
            events.Add(e);
        }

        events.Should().Equal("a", "b");
        (await log.AppendAsync("d").ConfigureAwait(true)).Should().Be(2);
    }

    /// <summary>
    /// A first append whose size reached disk but whose data did not leaves a zero-filled torn
    /// frame as the whole file. No frame can be present in it, so it is a torn tail exactly like
    /// the same zero run after a valid frame: opening trims it and the log starts at position 0.
    /// </summary>
    [Fact]
    public async Task CreateAsync_ZeroFilledTornFirstFrame_TrimsAndStartsAtZero()
    {
        await File.WriteAllBytesAsync(_filePath, new byte[40]).ConfigureAwait(true);

        var open = async () => await AppendOnlyEventLog<string>.CreateAsync(_filePath).ConfigureAwait(true);
        using var log = (await open.Should().NotThrowAsync(
            "an all-zero file holds no frame, so it is a torn first append, not a foreign file").ConfigureAwait(true)).Subject;

        new FileInfo(_filePath).Length.Should().Be(0, "the torn first frame is trimmed");
        (await log.AppendAsync("first").ConfigureAwait(true)).Should().Be(0);
    }
}
