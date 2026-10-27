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

using Boutquin.Storage.Infrastructure.Events;

namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// Torn-tail versus mid-file damage in the append-only logs whose frames carry a header
/// checksum: the append-only key-value engine (plain and indexed). A header checksum covers each
/// frame's length field, so damage to a length is detected as corruption rather than inferred
/// from whether later bytes happen to parse. The tear rule is deterministic: a partial header at
/// end-of-file, or a valid header whose payload is short at end-of-file, is a torn tail and is
/// truncated; any other damage fails loud and nothing is truncated.
/// </summary>
public sealed class AppendLogHeaderChecksumTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"AppendLogHeaderChecksum_{Guid.NewGuid():N}");

    private string LogPath => Path.Combine(_dir, "log.dat");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private IFileBasedStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>> NewAof(bool indexed)
    {
        var storageFile = new StorageFile(_dir, "log.dat");
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        return indexed
            ? new AppendOnlyFileStorageEngineWithIndex<SerializableWrapper<int>, SerializableWrapper<string>>(
                storageFile, serializer, new InMemoryFileIndex<SerializableWrapper<int>>())
            : new AppendOnlyFileStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(storageFile, serializer);
    }

    /// <summary>
    /// Writes five acknowledged records and, optionally, a long sixth record, returning each
    /// record's start offset read from the file length before its append (format-agnostic).
    /// </summary>
    private async Task<List<long>> WriteAofRecordsAsync(bool sixth)
    {
        var offsets = new List<long>();
        var engine = NewAof(indexed: false);
        for (var k = 1; k <= 5; k++)
        {
            offsets.Add(File.Exists(LogPath) ? new FileInfo(LogPath).Length : 0);
            await engine.SetAsync(new SerializableWrapper<int>(k), new SerializableWrapper<string>($"acknowledged-value-{k}"));
        }

        if (sixth)
        {
            offsets.Add(new FileInfo(LogPath).Length);
            await engine.SetAsync(new SerializableWrapper<int>(6), new SerializableWrapper<string>(new string('t', 300)));
        }

        return offsets;
    }

    /// <summary>
    /// Damage inside record 2 together with a torn final record. Records 3..5 were acknowledged
    /// after the damaged record, so this is mid-file corruption: every read must fail loud, and the
    /// next append must refuse rather than truncate the log back to the damage (which would erase
    /// the acknowledged records 2..5).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Aof_MidFileDamagePlusTornFinalRecord_FailsLoudAndNeverTruncates(bool indexed)
    {
        var offsets = await WriteAofRecordsAsync(sixth: true);
        var bytes = await File.ReadAllBytesAsync(LogPath);
        bytes[(int)offsets[1]] = 0x60; // rot the first byte of record 2
        var damaged = bytes.AsSpan(0, bytes.Length - 150).ToArray(); // tear record 6
        await File.WriteAllBytesAsync(LogPath, damaged);

        var engine = NewAof(indexed);
        await Assert.ThrowsAsync<InvalidDataException>(() => engine.GetAllItemsAsync());
        await Assert.ThrowsAsync<InvalidDataException>(
            () => engine.SetAsync(new SerializableWrapper<int>(7), new SerializableWrapper<string>("after-restart")));
        Assert.Equal(damaged, await File.ReadAllBytesAsync(LogPath));
    }

    private static byte[] RaftCommand(int i) => Encoding.UTF8.GetBytes($"committed-command-{i}-" + new string('c', 40));

    /// <summary>
    /// Writes five committed Raft entries and a long sixth one, returning each frame's start
    /// offset read from the file length before its append (format-agnostic).
    /// </summary>
    private List<long> WriteRaftEntries()
    {
        var offsets = new List<long>();
        using var log = new FileRaftLog<byte[]>(LogPath, b => b, b => b);
        for (var i = 0; i < 6; i++)
        {
            offsets.Add(new FileInfo(LogPath).Length);
            log.Append((1, i == 5 ? new byte[300] : RaftCommand(i)));
        }

        return offsets;
    }

    /// <summary>
    /// Damage to frame 1's length and term bytes together with a torn final append. Frames 2..4
    /// were committed after the damaged frame, so this is mid-file corruption: reopening must fail
    /// loud and must not truncate the committed entries away.
    /// </summary>
    [Fact]
    public void Raft_MidFileLengthAndTermDamagePlusTornFinalAppend_FailsLoudAndNeverTruncates()
    {
        Directory.CreateDirectory(_dir);
        var offsets = WriteRaftEntries();
        var bytes = File.ReadAllBytes(LogPath);
        bytes[(int)offsets[1] + 2] ^= 0x01; // a length / header byte of frame 1
        bytes[(int)offsets[1] + 5] ^= 0x5A; // a term byte of frame 1
        var damaged = bytes.AsSpan(0, bytes.Length - 150).ToArray(); // tear frame 5
        File.WriteAllBytes(LogPath, damaged);

        var reopen = () => new FileRaftLog<byte[]>(LogPath, b => b, b => b);
        Assert.Throws<InvalidDataException>(reopen);
        Assert.Equal(damaged, File.ReadAllBytes(LogPath));
    }

    /// <summary>The logs that share the header-checksum format and its tear rule.</summary>
    public enum LogKind
    {
        Aof,
        AofIndexed,
        EventLog,
        Raft,
    }

    private static int HeaderSize(LogKind kind) => kind is LogKind.Aof or LogKind.AofIndexed ? 17 : 25;

    private static int LengthOffset(LogKind kind) => kind is LogKind.Aof or LogKind.AofIndexed ? 5 : 13;

    /// <summary>Appends one record with the log's own writer to the log at <paramref name="path"/>.</summary>
    private static async Task AppendAsync(LogKind kind, string path, int i)
    {
        switch (kind)
        {
            case LogKind.Aof:
            case LogKind.AofIndexed:
                await NewAofAt(kind, path).SetAsync(new SerializableWrapper<int>(i), new SerializableWrapper<string>($"record-{i}"));
                break;
            case LogKind.EventLog:
                using (var log = await AppendOnlyEventLog<string>.CreateAsync(path))
                {
                    await log.AppendAsync($"record-{i}");
                }

                break;
            default:
                using (var raft = new FileRaftLog<byte[]>(path, b => b, b => b))
                {
                    raft.Append((1, Encoding.UTF8.GetBytes($"record-{i}")));
                }

                break;
        }
    }

    private static IFileBasedStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>> NewAofAt(LogKind kind, string path)
    {
        var storageFile = new StorageFile(Path.GetDirectoryName(path), Path.GetFileName(path));
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        return kind == LogKind.AofIndexed
            ? new AppendOnlyFileStorageEngineWithIndex<SerializableWrapper<int>, SerializableWrapper<string>>(
                storageFile, serializer, new InMemoryFileIndex<SerializableWrapper<int>>())
            : new AppendOnlyFileStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(storageFile, serializer);
    }

    /// <summary>Opens the log through its recovery path and returns how many records it serves.</summary>
    private static async Task<int> RecoverCountAsync(LogKind kind, string path)
    {
        switch (kind)
        {
            case LogKind.Aof:
            case LogKind.AofIndexed:
                return (await NewAofAt(kind, path).GetAllItemsAsync()).Count();
            case LogKind.EventLog:
                using (var log = await AppendOnlyEventLog<string>.CreateAsync(path))
                {
                    var n = 0;
                    await foreach (var _ in log.ReadFromAsync(0))
                    {
                        n++;
                    }

                    return n;
                }

            default:
                using (var raft = new FileRaftLog<byte[]>(path, b => b, b => b))
                {
                    return raft.Count;
                }
        }
    }

    /// <summary>The bytes of one complete frame written by the log's own writer.</summary>
    private async Task<byte[]> OneFrameAsync(LogKind kind, int i)
    {
        var scratch = Path.Combine(_dir, $"frame-{Guid.NewGuid():N}.dat");
        await AppendAsync(kind, scratch, i);
        return await File.ReadAllBytesAsync(scratch);
    }

    /// <summary>Writes <paramref name="count"/> records and returns each frame's start offset plus the end.</summary>
    private async Task<List<long>> WriteRecordsAsync(LogKind kind, int count)
    {
        var offsets = new List<long>();
        for (var i = 0; i < count; i++)
        {
            offsets.Add(File.Exists(LogPath) ? new FileInfo(LogPath).Length : 0);
            await AppendAsync(kind, LogPath, i);
        }

        offsets.Add(new FileInfo(LogPath).Length);
        return offsets;
    }

    /// <summary>
    /// Must-recover: the image is a torn tail under the tear rule. Recovery serves
    /// <paramref name="expected"/> records, and a later append lands cleanly after them.
    /// </summary>
    private async Task AssertRecoversAsync(LogKind kind, int expected)
    {
        Assert.Equal(expected, await RecoverCountAsync(kind, LogPath));
        await AppendAsync(kind, LogPath, 99);
        Assert.Equal(expected + 1, await RecoverCountAsync(kind, LogPath));
    }

    /// <summary>Must-fail-loud: recovery and the next append throw, and the image is never truncated.</summary>
    private async Task AssertFailsLoudAsync(LogKind kind, byte[] image)
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => RecoverCountAsync(kind, LogPath));
        await Assert.ThrowsAsync<InvalidDataException>(() => AppendAsync(kind, LogPath, 99));
        Assert.Equal(image, await File.ReadAllBytesAsync(LogPath));
    }

    [Theory]
    [InlineData(LogKind.Aof)]
    [InlineData(LogKind.AofIndexed)]
    [InlineData(LogKind.EventLog)]
    [InlineData(LogKind.Raft)]
    public async Task ZeroFilledFirstFrame_IsATornTail(LogKind kind)
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllBytesAsync(LogPath, new byte[40]);
        await AssertRecoversAsync(kind, 0);
    }

    [Theory]
    [InlineData(LogKind.Aof, 3)]
    [InlineData(LogKind.Aof, 12)]
    [InlineData(LogKind.AofIndexed, 9)]
    [InlineData(LogKind.EventLog, 3)]
    [InlineData(LogKind.EventLog, 20)]
    [InlineData(LogKind.Raft, 9)]
    [InlineData(LogKind.Raft, 30)]
    public async Task PartiallyWrittenFirstFrame_IsATornTail(LogKind kind, int keptBytes)
    {
        Directory.CreateDirectory(_dir);
        var frame = await OneFrameAsync(kind, 1);
        await File.WriteAllBytesAsync(LogPath, frame.AsSpan(0, keptBytes).ToArray());
        await AssertRecoversAsync(kind, 0);
    }

    [Theory]
    [InlineData(LogKind.Aof)]
    [InlineData(LogKind.EventLog)]
    [InlineData(LogKind.Raft)]
    public async Task HeaderPrefixFollowedByZeros_AfterValidFrames_IsATornTail(LogKind kind)
    {
        Directory.CreateDirectory(_dir);
        await WriteRecordsAsync(kind, 3);
        var frame = await OneFrameAsync(kind, 7);
        var tail = frame.AsSpan(0, 10).ToArray().Concat(new byte[30]).ToArray();
        await File.AppendAllBytesAsync(LogPath, tail);
        await AssertRecoversAsync(kind, 3);
    }

    // Every frame of these logs ends with its payload, so an interrupted append — or the
    // interrupted bulk batch of the append-only engine, whose landed data stops inside one frame —
    // leaves a payload prefix followed by zeros: only a zero last byte can be a tear (see the two
    // theories below).

    /// <summary>
    /// Must-recover: the final payload's trailing bytes never landed (a payload prefix followed
    /// by zeros to end-of-file), the shape an interrupted append leaves.
    /// </summary>
    [Theory]
    [InlineData(LogKind.Aof, 1)]
    [InlineData(LogKind.Aof, 3)]
    [InlineData(LogKind.AofIndexed, 1)]
    [InlineData(LogKind.AofIndexed, 3)]
    [InlineData(LogKind.EventLog, 1)]
    [InlineData(LogKind.EventLog, 3)]
    [InlineData(LogKind.Raft, 1)]
    [InlineData(LogKind.Raft, 3)]
    public async Task FinalPayloadZeroSuffixEndingAtEof_IsATornTail(LogKind kind, int unlandedBytes)
    {
        Directory.CreateDirectory(_dir);
        await WriteRecordsAsync(kind, 3);
        var bytes = await File.ReadAllBytesAsync(LogPath);
        Array.Clear(bytes, bytes.Length - unlandedBytes, unlandedBytes);
        await File.WriteAllBytesAsync(LogPath, bytes);
        await AssertRecoversAsync(kind, 2);
    }

    /// <summary>
    /// Must-fail-loud: the final payload's last byte landed (non-zero) yet the payload fails its
    /// checksum. That is damage to an fsync'd, acknowledged record, not a tear, so recovery and the
    /// next append throw and the image is never truncated.
    /// </summary>
    [Theory]
    [InlineData(LogKind.Aof, 1)]
    [InlineData(LogKind.Aof, 4)]
    [InlineData(LogKind.AofIndexed, 1)]
    [InlineData(LogKind.AofIndexed, 4)]
    [InlineData(LogKind.EventLog, 1)]
    [InlineData(LogKind.EventLog, 4)]
    [InlineData(LogKind.Raft, 1)]
    [InlineData(LogKind.Raft, 4)]
    public async Task FinalPayloadChecksumFailureWithLandedLastByte_FailsLoud(LogKind kind, int damagedFromEnd)
    {
        Directory.CreateDirectory(_dir);
        await WriteRecordsAsync(kind, 3);
        var bytes = await File.ReadAllBytesAsync(LogPath);
        bytes[^damagedFromEnd] ^= 0x20;
        Assert.NotEqual(0, bytes[^1]);
        await File.WriteAllBytesAsync(LogPath, bytes);
        await AssertFailsLoudAsync(kind, bytes);
    }

    [Theory]
    [InlineData(LogKind.Aof)]
    [InlineData(LogKind.AofIndexed)]
    [InlineData(LogKind.EventLog)]
    [InlineData(LogKind.Raft)]
    public async Task InteriorLengthFieldDamage_FailsLoud(LogKind kind)
    {
        Directory.CreateDirectory(_dir);
        var offsets = await WriteRecordsAsync(kind, 4);
        var bytes = await File.ReadAllBytesAsync(LogPath);
        bytes[(int)offsets[1] + LengthOffset(kind)] ^= 0x01; // frame 1 now claims one byte more or less
        await File.WriteAllBytesAsync(LogPath, bytes);
        await AssertFailsLoudAsync(kind, bytes);
    }

    [Theory]
    [InlineData(LogKind.Aof)]
    [InlineData(LogKind.EventLog)]
    [InlineData(LogKind.Raft)]
    public async Task InteriorPayloadDamage_FailsLoud(LogKind kind)
    {
        Directory.CreateDirectory(_dir);
        var offsets = await WriteRecordsAsync(kind, 4);
        var bytes = await File.ReadAllBytesAsync(LogPath);
        bytes[(int)offsets[2] - 1] ^= 0x20; // last payload byte of frame 1
        await File.WriteAllBytesAsync(LogPath, bytes);
        await AssertFailsLoudAsync(kind, bytes);
    }

    [Theory]
    [InlineData(LogKind.Aof)]
    [InlineData(LogKind.EventLog)]
    [InlineData(LogKind.Raft)]
    public async Task InteriorHeaderZeroed_WithFramesAfterIt_FailsLoud(LogKind kind)
    {
        Directory.CreateDirectory(_dir);
        var offsets = await WriteRecordsAsync(kind, 4);
        var bytes = await File.ReadAllBytesAsync(LogPath);
        Array.Clear(bytes, (int)offsets[1], HeaderSize(kind));
        await File.WriteAllBytesAsync(LogPath, bytes);
        await AssertFailsLoudAsync(kind, bytes);
    }

    [Theory]
    [InlineData(LogKind.Aof)]
    [InlineData(LogKind.EventLog)]
    [InlineData(LogKind.Raft)]
    public async Task ForeignPartialHeaderAtEof_FailsLoud(LogKind kind)
    {
        Directory.CreateDirectory(_dir);
        await WriteRecordsAsync(kind, 2);
        await File.AppendAllBytesAsync(LogPath, "XYZ"u8.ToArray());
        await AssertFailsLoudAsync(kind, await File.ReadAllBytesAsync(LogPath));
    }
}

/// <summary>
/// An independent reader and writer for the append-only engine's version-1 frame layout
/// (<c>[magic "BSAO"][version][payload length][payload CRC32C][header CRC32C][payload][end marker 0xA5]</c>),
/// so tests can inspect and build log images without going through the engine under test.
/// </summary>
internal static class AppendOnlyFrames
{
    public const int HeaderSize = 17;
    public const int PayloadLengthOffset = 5;
    public const int EndMarkerSize = 1;
    public const byte EndMarker = 0xA5;

    /// <summary>Encodes <paramref name="key"/>/<paramref name="value"/> as one complete frame.</summary>
    public static byte[] Encode<TKey, TValue>(IEntrySerializer<TKey, TValue> serializer, TKey key, TValue value)
        where TKey : IStorageKey<TKey>, new()
        where TValue : ISerializable<TValue>, new()
    {
        using var payload = new MemoryStream();
        serializer.WriteEntry(payload, key, value);
        return Encode(payload.ToArray());
    }

    /// <summary>Wraps raw payload bytes in a checksum-valid frame.</summary>
    public static byte[] Encode(byte[] payload)
    {
        var frame = new byte[HeaderSize + payload.Length + EndMarkerSize];
        "BSAO"u8.CopyTo(frame);
        frame[4] = 1;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(PayloadLengthOffset), payload.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(9), Crc32C.HashToUInt32(payload));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(13), Crc32C.HashToUInt32(frame.AsSpan(0, 13)));
        payload.CopyTo(frame, HeaderSize);
        frame[^1] = EndMarker;
        return frame;
    }

    /// <summary>Start offsets of every complete frame, plus the end offset of the last one.</summary>
    public static List<long> Boundaries(byte[] file)
    {
        var boundaries = new List<long> { 0 };
        long position = 0;
        while (position + HeaderSize <= file.Length)
        {
            Assert.True(file.AsSpan((int)position, 4).SequenceEqual("BSAO"u8), $"frame magic expected at offset {position}");
            var payloadLength = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan((int)position + PayloadLengthOffset));
            if (position + HeaderSize + payloadLength + EndMarkerSize > file.Length)
            {
                break;
            }

            position += HeaderSize + payloadLength + EndMarkerSize;
            boundaries.Add(position);
        }

        return boundaries;
    }

    /// <summary>Decodes every complete frame's payload in the file at <paramref name="path"/>.</summary>
    public static List<(TKey Key, TValue Value)> ReadEntries<TKey, TValue>(IEntrySerializer<TKey, TValue> serializer, string path)
        where TKey : IStorageKey<TKey>, new()
        where TValue : ISerializable<TValue>, new()
    {
        var file = File.ReadAllBytes(path);
        var boundaries = Boundaries(file);
        var entries = new List<(TKey Key, TValue Value)>();
        for (var i = 0; i + 1 < boundaries.Count; i++)
        {
            var start = (int)boundaries[i] + HeaderSize;
            using var payload = new MemoryStream(file, start, (int)boundaries[i + 1] - EndMarkerSize - start, writable: false);
            entries.Add(serializer.ReadEntry(payload)!.Value);
        }

        return entries;
    }
}

/// <summary>
/// An independent writer for the event log's version-1 frame layout
/// (<c>[magic "BSEL"][version][position][payload length][payload CRC32C][header CRC32C][payload]</c>).
/// </summary>
internal static class EventLogFrames
{
    public const int HeaderSize = 25;
    public const int PayloadLengthOffset = 13;

    /// <summary>Encodes <paramref name="payload"/> at <paramref name="position"/> as one checksum-valid frame.</summary>
    public static byte[] Encode(long position, byte[] payload) => Encode(position, payload, payload.Length);

    /// <summary>
    /// Encodes a frame whose checksum-valid header declares <paramref name="declaredLength"/>
    /// payload bytes; only the header and <paramref name="payload"/> are emitted.
    /// </summary>
    public static byte[] Encode(long position, byte[] payload, int declaredLength)
    {
        var frame = new byte[HeaderSize + payload.Length];
        "BSEL"u8.CopyTo(frame);
        frame[4] = 1;
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(frame.AsSpan(5), position);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(PayloadLengthOffset), declaredLength);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(17), Crc32C.HashToUInt32(payload));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(21), Crc32C.HashToUInt32(frame.AsSpan(0, 21)));
        payload.CopyTo(frame, HeaderSize);
        return frame;
    }
}
