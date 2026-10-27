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
#nullable disable

using System.Buffers.Binary;
using Boutquin.Storage.Infrastructure.Events;

namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// The final frame of a single-file log (Raft log, append-only engine, event log) whose complete
/// header carries a header checksum ending in a zero byte — about one frame in 256. Each log's
/// header ends with its CRC32C, so such a header looks, by its last byte alone, like a strict
/// prefix an interrupted append left behind. It is not one when the checksum bytes that did land
/// disagree with the checksum of the header fields before them: a real prefix carries the real
/// checksum's leading bytes. Damage anywhere in such a header must therefore fail loud and leave
/// the acknowledged frame on disk, while a genuine prefix of the same frame followed by zeros is
/// still a torn tail that recovery drops.
/// </summary>
public sealed class SingleFileLogZeroChecksumByteTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("single-file-log-zero-crc-byte-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; a stray temp dir is harmless.
        }
    }

    /// <summary>
    /// Asserts the fixture: the final frame's header ends in a zero checksum byte, and after the
    /// damage its last non-zero byte is still the checksum byte before it, so the damaged header
    /// looks like a 24-of-25 (or 16-of-17) byte prefix by its zero bytes alone.
    /// </summary>
    private static void AssertLooksLikeAPrefixByItsZeroBytes(byte[] bytes, int frameStart, int headerSize)
    {
        bytes[frameStart + headerSize - 1].Should().Be(0, "the fixture needs a complete header whose checksum ends in a zero byte");
        bytes.AsSpan(frameStart, headerSize).LastIndexOfAnyExcept((byte)0).Should().Be(headerSize - 2,
            "the damaged header's last non-zero byte must be the checksum byte before the zero one");
    }

    // ---------------------------------------------------------------- Raft log

    private const int RaftHeaderSize = 25;
    private const int RaftTermOffset = 5;
    private const int RaftCommandLengthOffset = 13;
    private const int RaftCommandCrcOffset = 17;
    private const int RaftHeaderCrcOffset = 21;
    private const int RaftEndMarkerSize = 1;

    private string RaftPath => Path.Combine(_dir, "raft.log");

    private static byte[] RaftCommand(string command) => command == "empty" ? [] : new byte[4];

    /// <summary>
    /// The first term from 2 upward whose frame header, built by an independent encoder of the
    /// documented layout, ends in a zero checksum byte with a non-zero, non-one byte before it.
    /// </summary>
    private static long RaftTermWhoseHeaderChecksumEndsInZero(byte[] command)
    {
        var header = new byte[RaftHeaderSize];
        "BSRL"u8.CopyTo(header);
        header[4] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(RaftCommandLengthOffset), command.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(RaftCommandCrcOffset), Crc32C.HashToUInt32(command));
        for (long term = 2; ; term++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(RaftTermOffset), term);
            var crc = Crc32C.HashToUInt32(header.AsSpan(0, RaftHeaderCrcOffset));
            if (crc >> 24 == 0 && (crc >> 16 & 0xFF) > 1)
            {
                return term;
            }
        }
    }

    private int WriteRaftEntries(byte[] finalCommand)
    {
        using var log = new FileRaftLog<byte[]>(RaftPath, b => b, b => b);
        log.Append((1, "op-1"u8.ToArray()));
        var finalStart = (int)new FileInfo(RaftPath).Length;
        log.Append((RaftTermWhoseHeaderChecksumEndsInZero(finalCommand), finalCommand));
        return finalStart;
    }

    public static TheoryData<string, int> RaftDamage => new()
    {
        { "empty", RaftTermOffset },
        { "empty", RaftCommandLengthOffset },
        { "empty", RaftCommandCrcOffset },
        { "empty", RaftHeaderCrcOffset },
        { "empty", RaftHeaderCrcOffset + 1 },
        { "four-zero-bytes", RaftTermOffset + 1 },
        { "four-zero-bytes", RaftHeaderCrcOffset },
    };

    [Theory]
    [MemberData(nameof(RaftDamage))]
    public void Raft_CompleteFinalHeaderWhoseChecksumEndsInZero_FailsLoudAndIsNotTruncated(string command, int damagedByte)
    {
        var finalStart = WriteRaftEntries(RaftCommand(command));
        var bytes = File.ReadAllBytes(RaftPath);
        bytes[finalStart + damagedByte] ^= 0x01;
        AssertLooksLikeAPrefixByItsZeroBytes(bytes, finalStart, RaftHeaderSize);
        File.WriteAllBytes(RaftPath, bytes);

        var act = () => new FileRaftLog<byte[]>(RaftPath, b => b, b => b);

        act.Should().Throw<InvalidDataException>(
            "the landed checksum bytes disagree with the header fields, so this is damage to an acknowledged entry, not a torn prefix");
        File.ReadAllBytes(RaftPath).Should().Equal(bytes, "a fail-loud recovery must not truncate the acknowledged final entry away");
    }

    [Theory]
    [InlineData("empty", RaftHeaderCrcOffset)]
    [InlineData("empty", RaftHeaderCrcOffset + 1)]
    [InlineData("empty", RaftHeaderCrcOffset + 2)]
    [InlineData("four-zero-bytes", RaftHeaderCrcOffset + 2)]
    public void Raft_HeaderPrefixOfAFrameWhoseChecksumEndsInZero_IsATornTail(string command, int landedBytes)
    {
        var finalStart = WriteRaftEntries(RaftCommand(command));
        var bytes = File.ReadAllBytes(RaftPath);
        // An interrupted append lands a prefix of the frame: everything after the landed header
        // bytes — the rest of the header, the command and the frame's end marker — is still zero.
        Array.Clear(bytes, finalStart + landedBytes, bytes.Length - finalStart - landedBytes);
        File.WriteAllBytes(RaftPath, bytes);

        using (var reopened = new FileRaftLog<byte[]>(RaftPath, b => b, b => b))
        {
            reopened.Count.Should().Be(1, "a header prefix followed by zeros is an interrupted append and is dropped");
            reopened.Append((3, "op-3"u8.ToArray()));
        }

        new FileInfo(RaftPath).Length.Should().Be(finalStart + RaftHeaderSize + "op-3".Length + RaftEndMarkerSize,
            "recovery truncates the torn header back to the last good frame before the next append");
    }

    // ---------------------------------------------------------------- Append-only engine

    private const int AofHeaderSize = 17;
    private const int AofPayloadLengthOffset = 5;
    private const int AofPayloadCrcOffset = 9;
    private const int AofHeaderCrcOffset = 13;
    private const string AofFileName = "aof.dat";

    private string AofPath => Path.Combine(_dir, AofFileName);

    /// <summary>
    /// An entry serializer whose payload is a run of zero bytes: key <c>n</c> encodes as <c>n</c>
    /// zeros (the value is not stored), the shape an entry serializer is free to emit.
    /// </summary>
    private sealed class ZeroRunSerializer : IEntrySerializer<SerializableWrapper<int>, SerializableWrapper<int>>
    {
        public void WriteEntry(Stream stream, SerializableWrapper<int> key, SerializableWrapper<int> value)
            => stream.Write(new byte[key.Value]);

        public Task WriteEntryAsync(
            Stream stream, SerializableWrapper<int> key, SerializableWrapper<int> value, CancellationToken cancellationToken = default)
        {
            WriteEntry(stream, key, value);
            return Task.CompletedTask;
        }

        public (SerializableWrapper<int> Key, SerializableWrapper<int> Value)? ReadEntry(Stream stream)
        {
            var count = (int)(stream.Length - stream.Position);
            stream.ReadExactly(new byte[count]);
            return (new SerializableWrapper<int>(count), new SerializableWrapper<int>(0));
        }

        public bool CanRead(Stream stream) => stream.Length > stream.Position;
    }

    private IFileBasedStorageEngine<SerializableWrapper<int>, SerializableWrapper<int>> NewAof(bool indexed)
    {
        var storageFile = new StorageFile(_dir, AofFileName);
        var serializer = new ZeroRunSerializer();
        return indexed
            ? new AppendOnlyFileStorageEngineWithIndex<SerializableWrapper<int>, SerializableWrapper<int>>(
                storageFile, serializer, new InMemoryFileIndex<SerializableWrapper<int>>())
            : new AppendOnlyFileStorageEngine<SerializableWrapper<int>, SerializableWrapper<int>>(storageFile, serializer);
    }

    /// <summary>
    /// The first payload length from 2 upward whose all-zero payload's frame header, built by an
    /// independent encoder of the documented layout, ends in a zero checksum byte with a non-zero,
    /// non-one byte before it.
    /// </summary>
    private static int AofZeroPayloadLengthWhoseHeaderChecksumEndsInZero()
    {
        for (var length = 2; ; length++)
        {
            var crc = BinaryPrimitives.ReadUInt32LittleEndian(AppendOnlyFrames.Encode(new byte[length]).AsSpan(AofHeaderCrcOffset));
            if (crc >> 24 == 0 && (crc >> 16 & 0xFF) > 1)
            {
                return length;
            }
        }
    }

    private async Task<int> WriteAofRecordsAsync(bool indexed)
    {
        var engine = NewAof(indexed);
        await engine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<int>(0));
        var finalStart = (int)new FileInfo(AofPath).Length;
        await engine.SetAsync(new SerializableWrapper<int>(AofZeroPayloadLengthWhoseHeaderChecksumEndsInZero()), new SerializableWrapper<int>(0));
        return finalStart;
    }

    [Theory]
    [InlineData(false, AofPayloadLengthOffset)]
    [InlineData(false, AofPayloadCrcOffset)]
    [InlineData(false, AofHeaderCrcOffset)]
    [InlineData(false, AofHeaderCrcOffset + 1)]
    [InlineData(true, AofPayloadLengthOffset)]
    [InlineData(true, AofHeaderCrcOffset)]
    public async Task Aof_CompleteFinalHeaderWhoseChecksumEndsInZero_FailsLoudAndNeverTruncates(bool indexed, int damagedByte)
    {
        var finalStart = await WriteAofRecordsAsync(indexed);
        var bytes = File.ReadAllBytes(AofPath);
        bytes[finalStart + damagedByte] ^= 0x01;

        // The frame ends in its non-zero end marker; zero it so only zeros follow the damaged header,
        // the image this test pins (a complete header is damage even when only zeros follow it).
        bytes[^1] = 0;
        AssertLooksLikeAPrefixByItsZeroBytes(bytes, finalStart, AofHeaderSize);
        bytes.AsSpan(finalStart + AofHeaderSize).IndexOfAnyExcept((byte)0).Should().Be(-1, "only zeros follow the damaged header");
        File.WriteAllBytes(AofPath, bytes);

        var engine = NewAof(indexed);
        await FluentActions.Awaiting(() => engine.GetAllItemsAsync()).Should().ThrowAsync<InvalidDataException>(
            "the landed checksum bytes disagree with the header fields, so this is damage to an acknowledged record");
        await FluentActions.Awaiting(() => engine.SetAsync(new SerializableWrapper<int>(3), new SerializableWrapper<int>(0)))
            .Should().ThrowAsync<InvalidDataException>("an append must not truncate the acknowledged record away first");
        File.ReadAllBytes(AofPath).Should().Equal(bytes, "a fail-loud recovery must leave the file untouched");
    }

    [Theory]
    [InlineData(false, AofHeaderCrcOffset)]
    [InlineData(false, AofHeaderCrcOffset + 2)]
    [InlineData(true, AofHeaderCrcOffset + 1)]
    [InlineData(true, AofHeaderCrcOffset + 2)]
    public async Task Aof_HeaderPrefixOfARecordWhoseChecksumEndsInZero_IsATornTail(bool indexed, int landedBytes)
    {
        var finalStart = await WriteAofRecordsAsync(indexed);
        var bytes = File.ReadAllBytes(AofPath);
        // An interrupted append leaves nothing after the landed prefix: the rest of the header, the
        // payload and the end marker are all zeros.
        Array.Clear(bytes, finalStart + landedBytes, bytes.Length - finalStart - landedBytes);
        File.WriteAllBytes(AofPath, bytes);

        var engine = NewAof(indexed);
        (await engine.GetAllItemsAsync()).Select(e => e.Key.Value).Should().Equal([1],
            "a header prefix followed by zeros is an interrupted append and is dropped");
        await engine.SetAsync(new SerializableWrapper<int>(3), new SerializableWrapper<int>(0));
        (await engine.GetAllItemsAsync()).Select(e => e.Key.Value).Should().Equal([1, 3]);
    }

    // ---------------------------------------------------------------- Event log

    private const int EventHeaderSize = 25;
    private const int EventPositionOffset = 5;
    private const int EventPayloadLengthOffset = 13;
    private const int EventPayloadCrcOffset = 17;
    private const int EventHeaderCrcOffset = 21;

    private string EventPath => Path.Combine(_dir, "events.log");

    /// <summary>
    /// Writes one event through the log, then appends a frame — built by an independent encoder of
    /// the documented layout — whose all-zero payload makes its header checksum end in a zero byte.
    /// The tear rule is a function of the bytes on disk alone, so this image pins it for whatever
    /// writer produced the frame.
    /// </summary>
    private async Task<int> WriteEventFramesAsync()
    {
        using (var log = await AppendOnlyEventLog<string>.CreateAsync(EventPath).ConfigureAwait(true))
        {
            await log.AppendAsync("event-0").ConfigureAwait(true);
        }

        var finalStart = (int)new FileInfo(EventPath).Length;
        for (var length = 2; ; length++)
        {
            var frame = EncodeEventFrame(1, new byte[length]);
            if (frame[EventHeaderSize - 1] == 0 && frame[EventHeaderSize - 2] > 1)
            {
                using var stream = new FileStream(EventPath, FileMode.Append, FileAccess.Write);
                stream.Write(frame);
                return finalStart;
            }
        }
    }

    private static byte[] EncodeEventFrame(long position, byte[] payload)
    {
        var frame = new byte[EventHeaderSize + payload.Length];
        "BSEL"u8.CopyTo(frame);
        frame[4] = 1;
        BinaryPrimitives.WriteInt64LittleEndian(frame.AsSpan(EventPositionOffset), position);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(EventPayloadLengthOffset), payload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(EventPayloadCrcOffset), Crc32C.HashToUInt32(payload));
        BinaryPrimitives.WriteUInt32LittleEndian(
            frame.AsSpan(EventHeaderCrcOffset), Crc32C.HashToUInt32(frame.AsSpan(0, EventHeaderCrcOffset)));
        payload.CopyTo(frame, EventHeaderSize);
        return frame;
    }

    [Theory]
    [InlineData(EventPositionOffset)]
    [InlineData(EventPayloadLengthOffset)]
    [InlineData(EventPayloadCrcOffset)]
    [InlineData(EventHeaderCrcOffset)]
    [InlineData(EventHeaderCrcOffset + 1)]
    public async Task EventLog_CompleteFinalHeaderWhoseChecksumEndsInZero_FailsLoudAndIsNotTruncated(int damagedByte)
    {
        var finalStart = await WriteEventFramesAsync().ConfigureAwait(true);
        var bytes = File.ReadAllBytes(EventPath);
        bytes[finalStart + damagedByte] ^= 0x01;
        AssertLooksLikeAPrefixByItsZeroBytes(bytes, finalStart, EventHeaderSize);
        File.WriteAllBytes(EventPath, bytes);

        await FluentActions.Awaiting(() => AppendOnlyEventLog<string>.CreateAsync(EventPath))
            .Should().ThrowAsync<InvalidDataException>(
                "the landed checksum bytes disagree with the header fields, so this is damage, not a torn prefix")
            .ConfigureAwait(true);
        File.ReadAllBytes(EventPath).Should().Equal(bytes, "a fail-loud recovery must not trim the final frame away");
    }

    [Theory]
    [InlineData(EventHeaderCrcOffset)]
    [InlineData(EventHeaderCrcOffset + 1)]
    [InlineData(EventHeaderCrcOffset + 2)]
    public async Task EventLog_HeaderPrefixOfAFrameWhoseChecksumEndsInZero_IsATornTail(int landedBytes)
    {
        var finalStart = await WriteEventFramesAsync().ConfigureAwait(true);
        var bytes = File.ReadAllBytes(EventPath);
        Array.Clear(bytes, finalStart + landedBytes, EventHeaderSize - landedBytes);
        File.WriteAllBytes(EventPath, bytes);

        var events = new List<string>();
        using (var log = await AppendOnlyEventLog<string>.CreateAsync(EventPath).ConfigureAwait(true))
        {
            await log.AppendAsync("event-1").ConfigureAwait(true);
            await foreach (var (_, e) in log.ReadFromAsync(0).ConfigureAwait(true))
            {
                events.Add(e);
            }
        }

        events.Should().Equal(["event-0", "event-1"], "a header prefix followed by zeros is an interrupted append and is trimmed");
    }
}
