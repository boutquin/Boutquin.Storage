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
/// Phase D tests for the v1 <see cref="AppendOnlyEventLog{TEvent}"/> frame format: magic + version
/// + CRC-over-header, resync-aware reader, persistent writer stream, and v0-log rejection.
/// </summary>
public sealed class AppendOnlyEventLogV1Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"eventlog-v1-{Guid.NewGuid():N}");

    public AppendOnlyEventLogV1Tests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best-effort */ }
        }
    }

    private string LogPath() => Path.Combine(_dir, "events.log");

    public sealed class Evt
    {
        public string? Name { get; set; }
        public int Number { get; set; }
    }

    [Fact]
    public async Task AppendAndRead_RoundTripsSingleEvent()
    {
        var path = LogPath();
        using (var log = await AppendOnlyEventLog<Evt>.CreateAsync(path).ConfigureAwait(true))
        {
            var pos = await log.AppendAsync(new Evt { Name = "one", Number = 1 });
            pos.Should().Be(0);
        }

        using var reopen = await AppendOnlyEventLog<Evt>.CreateAsync(path).ConfigureAwait(true);
        var results = new List<(long, Evt)>();
        await foreach (var item in reopen.ReadFromAsync(0))
        {
            results.Add(item);
        }

        results.Should().ContainSingle();
        results[0].Item1.Should().Be(0);
        results[0].Item2.Name.Should().Be("one");
        results[0].Item2.Number.Should().Be(1);
    }

    [Fact]
    public async Task AdversarialFrameWithHugeLength_DoesNotThrow_AndStillReturnsValidEvents()
    {
        // A checksum-valid header can declare an arbitrary payload length. The reader must bound
        // that length by the bytes actually remaining in the file before allocating — on both the
        // streaming read path and the recovery path — and classify the frame as a torn tail.
        var path = LogPath();
        using (var log = await AppendOnlyEventLog<Evt>.CreateAsync(path).ConfigureAwait(true))
        {
            await log.AppendAsync(new Evt { Name = "a", Number = 1 });
            await log.AppendAsync(new Evt { Name = "b", Number = 2 });
        }

        // Append a checksum-valid header (position 2) whose payload length is int.MaxValue, with
        // no payload bytes behind it: a valid header whose payload runs past end-of-file.
        var adversarial = EventLogFrames.Encode(2, [], int.MaxValue);
        await File.AppendAllBytesAsync(path, adversarial);

        var results = new List<(long, Evt)>();
        var read = async () =>
        {
            using var reopen = await AppendOnlyEventLog<Evt>.CreateAsync(path).ConfigureAwait(true); // CreateAsync runs recovery
            await foreach (var item in reopen.ReadFromAsync(0))
            {
                results.Add(item);
            }
        };

        await read.Should().NotThrowAsync(
            "the reader and recovery must bound frame length by file size, not allocate blindly");
        results.Should().HaveCount(2);
        results.Select(r => r.Item2.Name).Should().Equal("a", "b");
    }

    [Fact]
    public async Task FileOnDiskBeginsWithFrameMagic()
    {
        var path = LogPath();
        using (var log = await AppendOnlyEventLog<Evt>.CreateAsync(path).ConfigureAwait(true))
        {
            await log.AppendAsync(new Evt { Name = "first" });
        }

        var bytes = File.ReadAllBytes(path);
        bytes.Length.Should().BeGreaterThan(4);
        // Magic 'BSEL' in little-endian read order.
        bytes[0].Should().Be((byte)'B');
        bytes[1].Should().Be((byte)'S');
        bytes[2].Should().Be((byte)'E');
        bytes[3].Should().Be((byte)'L');
        bytes[4].Should().Be(1, "frame version is 1");
    }

    [Fact]
    public async Task PersistentWriter_ManyAppends_RoundTripInOrder()
    {
        // Second append must go through the same long-lived writer stream opened on first append.
        // If the writer reopened per append (old behaviour), throughput would be orders of
        // magnitude worse; functionally, a mis-tracked position would also scramble the data.
        var path = LogPath();
        using (var log = await AppendOnlyEventLog<Evt>.CreateAsync(path).ConfigureAwait(true))
        {
            for (var i = 0; i < 100; i++)
            {
                await log.AppendAsync(new Evt { Name = $"e-{i}", Number = i });
            }
        }

        using var reopen = await AppendOnlyEventLog<Evt>.CreateAsync(path).ConfigureAwait(true);
        var collected = new List<Evt>();
        await foreach (var (_, evt) in reopen.ReadFromAsync(0))
        {
            collected.Add(evt);
        }

        collected.Should().HaveCount(100);
        for (var i = 0; i < 100; i++)
        {
            collected[i].Number.Should().Be(i);
        }
    }

    [Fact]
    public async Task SingleByteHeaderCorruption_FailsClosed_MidLogLossIsNotServed()
    {
        // Write 3 frames. Corrupt a single byte in the middle of the second frame's header.
        // The v0 CRC-over-payload-only reader would seek to a wrong offset after reading a
        // bit-flipped payloadLength and desynchronise the entire tail. The v1 CRC-over-header
        // reader detects the mismatch; because a VALID frame follows the corrupt one, this is
        // mid-log data loss (not a torn tail) and the log refuses to serve a gapped history.
        var path = LogPath();
        using (var log = await AppendOnlyEventLog<Evt>.CreateAsync(path).ConfigureAwait(true))
        {
            await log.AppendAsync(new Evt { Name = "a", Number = 1 });
            await log.AppendAsync(new Evt { Name = "b", Number = 2 });
            await log.AppendAsync(new Evt { Name = "c", Number = 3 });
        }

        // Read bytes, corrupt 1 byte inside the second frame's header region, re-write.
        var bytes = File.ReadAllBytes(path);
        // Find the start of the second frame by locating the second occurrence of 'B'S'E'L.
        var firstMagic = 0;
        var secondMagic = -1;
        for (var i = firstMagic + 4; i + 4 <= bytes.Length; i++)
        {
            if (bytes[i] == (byte)'B' && bytes[i + 1] == (byte)'S' &&
                bytes[i + 2] == (byte)'E' && bytes[i + 3] == (byte)'L')
            {
                secondMagic = i;
                break;
            }
        }
        secondMagic.Should().BeGreaterThan(0);
        // Flip a bit inside the second frame's payloadLength field to exercise header-CRC coverage.
        bytes[secondMagic + 4 + 1 + 8 + 2] ^= 0x40;
        File.WriteAllBytes(path, bytes);

        // Recovery itself fails closed: the corrupt frame is followed by an intact one, so the
        // history has a hole that silently skipping would hide from every consumer.
        var act = async () => await AppendOnlyEventLog<Evt>.CreateAsync(path).ConfigureAwait(true);
        await act.Should().ThrowAsync<InvalidDataException>().WithMessage("*mid-log*");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task V0LogOnDisk_OpeningThrowsWithMigrationHint()
    {
        // A foreign file: non-zero bytes that don't begin with the "BSEL" magic. Opening must
        // surface this loudly rather than silently starting at position 0 and clobbering it. (An
        // all-zero file holds no frame and is a torn first append, which opening trims instead.)
        var path = LogPath();
        File.WriteAllBytes(path, [0x4E, 0x4F, 0x54, 0x41, 0x4C, 0x4F, 0x47, 0x21]);

        var act = async () => await AppendOnlyEventLog<Evt>.CreateAsync(path).ConfigureAwait(true);

        await act.Should().ThrowAsync<InvalidDataException>().WithMessage("*BSEL*").ConfigureAwait(true);
    }

    [Fact]
    public async Task ReadFromPosition_SkipsEarlierEvents()
    {
        var path = LogPath();
        using (var log = await AppendOnlyEventLog<Evt>.CreateAsync(path).ConfigureAwait(true))
        {
            for (var i = 0; i < 5; i++)
            {
                await log.AppendAsync(new Evt { Number = i });
            }
        }

        using var reopen = await AppendOnlyEventLog<Evt>.CreateAsync(path).ConfigureAwait(true);
        var collected = new List<int>();
        await foreach (var (pos, evt) in reopen.ReadFromAsync(position: 3))
        {
            collected.Add(evt.Number);
            pos.Should().BeGreaterThanOrEqualTo(3);
        }

        collected.Should().Equal(3, 4);
    }

    [Fact]
    public async Task NullPayloadField_SurvivesRoundTrip()
    {
        // The v1 frame itself doesn't store schema-level null info; this just verifies the
        // encoded payload (JSON with "Name": null) round-trips through the new frame code.
        var path = LogPath();
        using (var log = await AppendOnlyEventLog<Evt>.CreateAsync(path).ConfigureAwait(true))
        {
            await log.AppendAsync(new Evt { Name = null, Number = 42 });
        }

        using var reopen = await AppendOnlyEventLog<Evt>.CreateAsync(path).ConfigureAwait(true);
        await foreach (var (_, evt) in reopen.ReadFromAsync(0))
        {
            evt.Name.Should().BeNull();
            evt.Number.Should().Be(42);
        }
    }
}
