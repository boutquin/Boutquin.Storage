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
using System.Buffers.Binary;
using Boutquin.Storage.Domain.ValueObjects.LsmTree;
using Boutquin.Storage.Domain.ValueObjects.ValueLog;
using Boutquin.Storage.Infrastructure.Events;
using Boutquin.Storage.Infrastructure.ValueLog;

namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// Pins, by executing the real logs, the parts of the recovery threat model that name a specific
/// log: deleting the oldest WAL segments is undetected (a lost head is indistinguishable from
/// reclamation) and takes acknowledged writes with it, an emptied last WAL segment numbered above
/// 1 fails loud, and a last-frame payload checksum failure whose trailing bytes never landed,
/// followed by zeros, is a repaired tear in the WAL, the value log and the append-only engine but
/// is refused by the event log and the Raft log unless the damaged frame ends exactly at
/// end-of-file. The README, PITFALLS and WiscKey-guide prose is then checked against the same
/// vocabulary, so a document that drifts from the behaviour fails here.
/// </summary>
public sealed class RecoveryThreatModelDocsAccuracyTests : IDisposable
{
    private const int RaftHeaderSize = 25;
    private const int EventHeaderSize = 25;
    private const int AofHeaderSize = 17;

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), $"RecoveryThreatModelDocs_{Guid.NewGuid():N}");

    public RecoveryThreatModelDocsAccuracyTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best-effort */ }
        }
    }

    // ── documentation text ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("README.md", "## Design Decisions")]
    [InlineData("docs/PITFALLS.md", "## 4. Corruption Recovery Semantics")]
    public void ThreatModelProse_DeclaresTheDeletedOldestWalSegmentsAsOutsideTheModel(string path, string anchor)
    {
        var model = ThreatModel(path, anchor);

        model.Should().Contain("oldest wal segment", "deleting the head of the WAL is a silent-loss shape the model must name");
        model.Should().Contain("reclamation", "a lost head is undetectable because reclamation legitimately removes an oldest-first prefix");
        model.Should().Contain("low-water mark", "closing the lost-head case needs a durable reclamation low-water mark the WAL does not keep");
    }

    [Theory]
    [InlineData("README.md", "## Design Decisions")]
    [InlineData("docs/PITFALLS.md", "## 4. Corruption Recovery Semantics")]
    public void ThreatModelProse_NamesTheLogsThatRepairALastFramePayloadChecksumFailureFollowedByZeros(string path, string anchor)
    {
        var model = ThreatModel(path, anchor);

        model.Should().Contain("event log", "the event log refuses the shape unless the frame ends at end-of-file");
        model.Should().Contain("raft log", "the Raft log refuses the shape unless the frame ends at end-of-file");
        model.Should().Contain("exactly at end-of-file", "the carve-out is stated as an end-of-file condition");
        model.Should().Contain("append-only engine", "the log that repairs the shape is named, not implied");
    }

    [Theory]
    [InlineData("README.md", "## Design Decisions")]
    [InlineData("docs/PITFALLS.md", "## 4. Corruption Recovery Semantics")]
    public void ThreatModelProse_StatesThatAnEmptiedLaterWalSegmentFailsLoud(string path, string anchor)
        => ThreatModel(path, anchor).Should().Contain("numbered above 1",
            "an emptied, header-cut or zero-filled last WAL segment other than the first fails loud");

    [Theory]
    [InlineData("README.md", "## Design Decisions")]
    [InlineData("docs/PITFALLS.md", "## 4. Corruption Recovery Semantics")]
    public void ThreatModelProse_StatesThatAFinalFrameWhoseLastByteLandedFailsLoudInEveryLog(string path, string anchor)
    {
        var model = ThreatModel(path, anchor);

        model.Should().NotContain("qualifies in the wal, the value log and the append-only engine",
            "no log repairs a final frame whose last byte landed: it was written completely and acknowledged");
        model.Should().Contain("whose last byte landed",
            "the rule that separates a tear from damage is whether the frame's last byte landed");
        model.Should().Contain("checksum bytes that landed",
            "the WAL and the value log end each frame with its checksum, whose high byte is genuinely zero in one frame in 256, so the landed checksum bytes decide");
    }

    [Theory]
    [InlineData("README.md", "## Design Decisions")]
    [InlineData("docs/PITFALLS.md", "## 4. Corruption Recovery Semantics")]
    public void ThreatModelProse_HoldsTheWalAndTheRaftLogToTheSingleLandedLastByteRule(string path, string anchor)
    {
        var model = ThreatModel(path, anchor);

        model.Should().NotContain("qualifies in the wal",
            "the WAL repairs a final frame only when its checksum trailer is an unwritten suffix, like every other log");
        model.Should().NotContain("in the wal any such frame",
            "the WAL refuses a final frame whose last byte landed, like every other log");
        model.Should().Contain("end marker",
            "the Raft log ends every frame with a fixed non-zero end marker, so whether its last byte landed never depends on the command encoding");
        model.Should().NotContain("append-only engine and the raft log store caller-encoded bytes",
            "the Raft log no longer carries the zero-ending command limit");
        model.Should().NotContain("append-only engine or raft log: damage to a final payload",
            "the Raft log no longer carries the zero-ending command limit");
    }

    [Theory]
    [InlineData("README.md", "## Design Decisions")]
    [InlineData("docs/PITFALLS.md", "## 4. Corruption Recovery Semantics")]
    public void ThreatModelProse_NamesTheZeroEndingPayloadLimitOfTheLogsWhoseFramesEndWithTheirPayload(string path, string anchor)
    {
        var model = ThreatModel(path, anchor);

        model.Should().Contain("genuine last byte is zero",
            "damage to a final payload whose genuine last byte is zero reads as a payload prefix followed by zeros and is truncated");
        model.Should().Contain("small little-endian integer",
            "the limit reaches whole encodings, so the prose names one");
    }

    [Fact]
    public void WiscKeyGuide_FrameTearRule_RefusesAFinalFrameWhoseLastByteLanded()
        => GuideParagraph("**Recovery.** On open").Should().Contain("whose last byte landed",
            "the value log fails loud on a final frame that was written completely and fails its checksum");

    [Fact]
    public void WiscKeyGuide_ManifestTearRule_RequiresTheLandedChecksumBytesToMatch()
        => GuideParagraph("Recovery replays the manifest").Should().Contain("landed crc bytes",
            "a final manifest record whose landed CRC bytes disagree with its fields is damage to a complete record, not a torn append");

    // ── WAL: the oldest segments ───────────────────────────────────────────────

    [Fact]
    public async Task Wal_OldestSegmentDeleted_RecoversTheSurvivorsWithoutError()
    {
        var segments = await WriteRotatedSegmentsAsync(minimumSegments: 3).ConfigureAwait(true);
        var all = await RecoverAsync().ConfigureAwait(true);

        File.Delete(segments[0]);
        var survivors = await RecoverAsync().ConfigureAwait(true);

        survivors.Should().NotBeEmpty();
        survivors.Count.Should().BeLessThan(all.Count, "the writes of the deleted head segment are gone");
        survivors.Select(e => e.SequenceNumber).Should().Equal(
            all.Skip(all.Count - survivors.Count).Select(e => e.SequenceNumber),
            "recovery returns exactly the suffix that remains, with no error: a lost head is indistinguishable from reclamation");
    }

    [Fact]
    public async Task Wal_OldestTwoSegmentsDeleted_RecoversTheSurvivorsWithoutError()
    {
        var segments = await WriteRotatedSegmentsAsync(minimumSegments: 4).ConfigureAwait(true);
        var all = await RecoverAsync().ConfigureAwait(true);

        File.Delete(segments[0]);
        File.Delete(segments[1]);
        var survivors = await RecoverAsync().ConfigureAwait(true);

        survivors.Should().NotBeEmpty();
        survivors.Select(e => e.SequenceNumber).Should().Equal(
            all.Skip(all.Count - survivors.Count).Select(e => e.SequenceNumber));
    }

    [Fact]
    public async Task LsmEngine_OverAWalWhoseOldestSegmentWasDeleted_OpensWithoutTheLostWrites()
    {
        const int Writes = 3_500;
        var walDir = Path.Combine(_dir, "engine-wal");
        var segDir = Path.Combine(_dir, "engine-seg");
        Directory.CreateDirectory(segDir);
        var payload = new string('x', 1_000);
        using var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(walDir, SmallSegments);
        var options = new LsmStorageEngineOptions<SerializableWrapper<int>, SerializableWrapper<string>>(
            MemTableCapacity: 1_000_000,
            SegmentFolder: segDir,
            SegmentPrefix: "seg",
            SegmentFormat: new SstSegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>>(),
            WriteAheadLog: wal);
        await using var engine = await LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>
            .OpenAsync(options).ConfigureAwait(true);
        for (var i = 0; i < Writes; i++)
        {
            await engine.SetAsync(new SerializableWrapper<int>(i), new SerializableWrapper<string>(payload))
                .ConfigureAwait(true);
        }

        // Copy the live WAL (nothing has flushed) so the damage never races the engine's disposal.
        var copy = Path.Combine(_dir, "engine-wal-copy");
        Directory.CreateDirectory(copy);
        foreach (var file in Directory.GetFiles(walDir).Where(f => !f.EndsWith("wal.lock", StringComparison.Ordinal)))
        {
            File.Copy(file, Path.Combine(copy, Path.GetFileName(file)));
        }

        var copiedSegments = Directory.GetFiles(copy, "wal_*.log").OrderBy(p => p, StringComparer.Ordinal).ToList();
        copiedSegments.Count.Should().BeGreaterThanOrEqualTo(3, "the arrange step must rotate");
        File.Delete(copiedSegments[0]);

        var reopenedSegDir = Path.Combine(_dir, "engine-seg-reopened");
        Directory.CreateDirectory(reopenedSegDir);
        using var reopenedWal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(copy, SmallSegments);
        var reopenedOptions = options with { SegmentFolder = reopenedSegDir, WriteAheadLog = reopenedWal };
        await using var reopened = await LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>
            .OpenAsync(reopenedOptions).ConfigureAwait(true);

        (await reopened.TryGetValueAsync(new SerializableWrapper<int>(0)).ConfigureAwait(true)).Found
            .Should().BeFalse("the acknowledged first write lived in the deleted head segment and nothing flushed it");
        (await reopened.TryGetValueAsync(new SerializableWrapper<int>(Writes - 1)).ConfigureAwait(true)).Found
            .Should().BeTrue("the tail of the log survived");
    }

    // ── WAL: an emptied later segment ──────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public async Task Wal_LastSegmentNumberedAboveOne_EmptiedOrCutInsideItsHeader_FailsLoud(int keptBytes)
    {
        var segments = await WriteRotatedSegmentsAsync(minimumSegments: 2).ConfigureAwait(true);
        var last = segments[^1];
        Path.GetFileName(last).Should().NotBe("wal_000001.log");
        var bytes = File.ReadAllBytes(last);
        File.WriteAllBytes(last, bytes[..keptBytes]);

        var act = () => RecoverAsync();

        await act.Should().ThrowAsync<WriteAheadLogCorruptException>(
            "a later segment is published only after its header is durable, so an emptied or header-cut one is damage");
        File.ReadAllBytes(last).Should().Equal(bytes[..keptBytes], "fail-loud recovery leaves the file untouched");
    }

    // ── a last-frame payload checksum failure followed by zeros ────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    public async Task Wal_LastFramePayloadChecksumFailureFollowedByZeros_IsRepairedAsATornTail(int extraZeros)
    {
        var segment = await WriteThreeFramesAsync().ConfigureAwait(true);
        var bytes = File.ReadAllBytes(segment);
        var ends = WalFrameEnds(bytes);
        // Frame 3 keeps its header and first payload bytes; the rest of it, and anything after, is zero.
        var damaged = ZeroFrom(bytes, ends[1] + 4 + 1 + 1 + 8 + 4 + 6, extraZeros);
        File.WriteAllBytes(segment, damaged);

        var recovered = await RecoverAsync().ConfigureAwait(true);

        recovered.Select(e => e.SequenceNumber).Should().Equal([1UL, 2UL]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    public async Task AppendOnlyEngine_LastRecordPayloadChecksumFailureFollowedByZeros_IsRepairedAsATornTail(int extraZeros)
    {
        var storageFile = new StorageFile(_dir, "aof.dat");
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<int>>();
        var engine = new AppendOnlyFileStorageEngine<SerializableWrapper<int>, SerializableWrapper<int>>(storageFile, serializer);
        for (var i = 1; i <= 3; i++)
        {
            await engine.SetAsync(new SerializableWrapper<int>(i), new SerializableWrapper<int>(i * 100)).ConfigureAwait(true);
        }

        var path = Path.Combine(_dir, "aof.dat");
        var bytes = File.ReadAllBytes(path);
        var boundaries = AppendOnlyFrames.Boundaries(bytes);
        File.WriteAllBytes(path, ZeroFrom(bytes, (int)boundaries[2] + AofHeaderSize + 2, extraZeros));

        var reopened = new AppendOnlyFileStorageEngine<SerializableWrapper<int>, SerializableWrapper<int>>(storageFile, serializer);

        (await reopened.GetAllItemsAsync().ConfigureAwait(true)).Select(e => e.Key.Value).Should().Equal([1, 2]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    public async Task ValueLog_LastFramePayloadChecksumFailureFollowedByZeros_IsRepairedAsATornTail(int extraZeros)
    {
        var valueLogDir = Path.Combine(_dir, "vlog");
        var manifestPath = Path.Combine(valueLogDir, "valuelog.manifest");
        ValueLogPointer first;
        ValueLogPointer second;
        await using (var manifest = new ValueLogManifest(manifestPath))
        await using (var writer = await ValueLogWriter.OpenAsync(
            valueLogDir, manifest, ValueLogOptions.Default).ConfigureAwait(true))
        {
            first = await writer.AppendAsync(Enumerable.Repeat((byte)7, 40).ToArray(), 1UL).ConfigureAwait(true);
            second = await writer.AppendAsync(Enumerable.Repeat((byte)8, 40).ToArray(), 2UL).ConfigureAwait(true);
            await writer.AppendAsync(Enumerable.Repeat((byte)9, 40).ToArray(), 3UL).ConfigureAwait(true);
        }

        var segment = ValueLogWriter.ResolveSegmentPath(valueLogDir, 0);
        var bytes = File.ReadAllBytes(segment);
        var finalFrameStart = bytes.Length - (int)ValueLogFrame.FrameSize(40);
        File.WriteAllBytes(segment, ZeroFrom(bytes, finalFrameStart + ValueLogFrame.EntryHeaderSize + 10, extraZeros));

        await using (var manifest = new ValueLogManifest(manifestPath))
        await using (await ValueLogWriter.OpenAsync(
            valueLogDir, manifest, ValueLogOptions.Default).ConfigureAwait(true))
        {
        }

        new FileInfo(segment).Length.Should().Be(finalFrameStart, "the torn final frame is truncated back to the last good frame");
        using var reader = new ValueLogReader(valueLogDir, ValueLogOptions.Default);
        reader.Read(first).Should().Equal(Enumerable.Repeat((byte)7, 40));
        reader.Read(second).Should().Equal(Enumerable.Repeat((byte)8, 40));
    }

    [Fact]
    public void RaftLog_LastCommandChecksumFailureEndingExactlyAtEndOfFile_IsRepairedAsATornTail()
    {
        var finalStart = WriteRaftEntries(3);
        var bytes = File.ReadAllBytes(RaftPath);
        File.WriteAllBytes(RaftPath, ZeroFrom(bytes, finalStart + RaftHeaderSize + 4, extraZeros: 0));

        using var log = new FileRaftLog<byte[]>(RaftPath, b => b, b => b);

        log.Count.Should().Be(2);
        new FileInfo(RaftPath).Length.Should().Be(finalStart);
    }

    [Fact]
    public void RaftLog_LastCommandChecksumFailureFollowedByZerosBeyondTheFrame_FailsLoud()
    {
        var finalStart = WriteRaftEntries(3);
        var bytes = File.ReadAllBytes(RaftPath);
        var damaged = ZeroFrom(bytes, finalStart + RaftHeaderSize + 4, extraZeros: 16);
        File.WriteAllBytes(RaftPath, damaged);

        var act = () => new FileRaftLog<byte[]>(RaftPath, b => b, b => b);

        act.Should().Throw<InvalidDataException>(
            "one frame is fsync'd per append, so bytes after the damaged frame cannot be its interrupted write");
        File.ReadAllBytes(RaftPath).Should().Equal(damaged, "fail-loud recovery leaves the file untouched");
    }

    [Fact]
    public async Task EventLog_LastPayloadChecksumFailureEndingExactlyAtEndOfFile_IsRepairedAsATornTail()
    {
        var finalStart = await WriteEventsAsync().ConfigureAwait(true);
        var bytes = File.ReadAllBytes(EventPath);
        File.WriteAllBytes(EventPath, ZeroFrom(bytes, finalStart + EventHeaderSize + 3, extraZeros: 0));

        var events = await ReadEventsAsync().ConfigureAwait(true);

        events.Should().Equal(["event-0", "event-1"]);
    }

    [Fact]
    public async Task EventLog_LastPayloadChecksumFailureFollowedByZerosBeyondTheFrame_FailsLoud()
    {
        var finalStart = await WriteEventsAsync().ConfigureAwait(true);
        var bytes = File.ReadAllBytes(EventPath);
        var damaged = ZeroFrom(bytes, finalStart + EventHeaderSize + 3, extraZeros: 16);
        File.WriteAllBytes(EventPath, damaged);

        await FluentActions.Awaiting(() => AppendOnlyEventLog<string>.CreateAsync(EventPath))
            .Should().ThrowAsync<InvalidDataException>(
                "one frame is fsync'd per append, so bytes after the damaged frame cannot be its interrupted write")
            .ConfigureAwait(true);
        File.ReadAllBytes(EventPath).Should().Equal(damaged, "fail-loud recovery leaves the file untouched");
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private string RaftPath => Path.Combine(_dir, "raft.bin");

    private string EventPath => Path.Combine(_dir, "events.log");

    private string WalDirectory => Path.Combine(_dir, "durable-wal");

    private static WriteAheadLogOptions SmallSegments => new() { SegmentMaxBytes = 1L * 1024L * 1024L };

    /// <summary>Keeps the first <paramref name="keep"/> bytes, zeroes the rest, then appends zeros.</summary>
    private static byte[] ZeroFrom(byte[] bytes, int keep, int extraZeros)
    {
        var damaged = new byte[bytes.Length + extraZeros];
        Array.Copy(bytes, damaged, keep);
        return damaged;
    }

    /// <summary>Appends <paramref name="count"/> entries and returns the file length before the last one.</summary>
    private int WriteRaftEntries(int count)
    {
        var finalStart = 0;
        using var log = new FileRaftLog<byte[]>(RaftPath, b => b, b => b);
        for (var i = 0; i < count; i++)
        {
            finalStart = (int)(File.Exists(RaftPath) ? new FileInfo(RaftPath).Length : 0);
            log.Append((i + 1L, System.Text.Encoding.UTF8.GetBytes("command-" + i)));
        }

        return finalStart;
    }

    private async Task<int> WriteEventsAsync()
    {
        var finalStart = 0;
        using var log = await AppendOnlyEventLog<string>.CreateAsync(EventPath).ConfigureAwait(true);
        for (var i = 0; i < 3; i++)
        {
            finalStart = File.Exists(EventPath) ? (int)new FileInfo(EventPath).Length : 0;
            await log.AppendAsync("event-" + i).ConfigureAwait(true);
        }

        return finalStart;
    }

    private async Task<List<string>> ReadEventsAsync()
    {
        var events = new List<string>();
        using var log = await AppendOnlyEventLog<string>.CreateAsync(EventPath).ConfigureAwait(true);
        await foreach (var (_, e) in log.ReadFromAsync(0).ConfigureAwait(true))
        {
            events.Add(e);
        }

        return events;
    }

    private async Task<string> WriteThreeFramesAsync()
    {
        using (var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(WalDirectory))
        {
            for (var i = 1; i <= 3; i++)
            {
                await wal.AppendAsync(new SerializableWrapper<int>(i), new SerializableWrapper<string>("value-" + i), (ulong)i)
                    .ConfigureAwait(true);
            }
        }

        return Path.Combine(WalDirectory, "wal_000001.log");
    }

    private async Task<IReadOnlyList<string>> WriteRotatedSegmentsAsync(int minimumSegments)
    {
        using (var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(WalDirectory, SmallSegments))
        {
            var payload = new string('x', 500);
            var frames = 2_200 * minimumSegments;
            await Task.WhenAll(Enumerable.Range(0, frames)
                .Select(i => wal.AppendAsync(new SerializableWrapper<int>(i), new SerializableWrapper<string>(payload), (ulong)(i + 1))))
                .ConfigureAwait(true);
        }

        var segments = Directory.GetFiles(WalDirectory, "wal_*.log").OrderBy(p => p, StringComparer.Ordinal).ToList();
        segments.Count.Should().BeGreaterThanOrEqualTo(minimumSegments, "the arrange step must rotate");
        return segments;
    }

    private async Task<IReadOnlyList<WalEntry<SerializableWrapper<int>, SerializableWrapper<string>>>> RecoverAsync()
    {
        using var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(WalDirectory, SmallSegments);
        return await wal.RecoverAsync().ConfigureAwait(true);
    }

    /// <summary>End offset of every whole frame in a WAL segment, in file order.</summary>
    private static List<int> WalFrameEnds(byte[] bytes)
    {
        var ends = new List<int>();
        var position = 8;
        while (position + 4 <= bytes.Length)
        {
            var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(position, 4));
            var frameLength = 4 + 1 + 1 + 8 + 4 + payloadLength + 4;
            if (payloadLength <= 0 || position + frameLength > bytes.Length)
            {
                break;
            }

            position += frameLength;
            ends.Add(position);
        }

        return ends;
    }

    private static string ThreatModel(string path, string anchor)
    {
        var text = ReadRepoFile(path);
        var start = text.IndexOf(anchor, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, $"{path} must keep the section that hosts the threat model");
        var from = text[start..];
        var modelStart = from.IndexOf("Recovery threat model", StringComparison.Ordinal);
        modelStart.Should().BeGreaterThanOrEqualTo(0, $"{path} must state the durable logs' recovery threat model");
        var model = from[modelStart..];
        var end = model.IndexOf("\n## ", StringComparison.Ordinal);
        return (end < 0 ? model : model[..end]).Replace("\r\n", "\n").Replace('\n', ' ').ToLowerInvariant();
    }

    /// <summary>The paragraph of the WiscKey guide that starts with <paramref name="opening"/>, flattened and lower-cased.</summary>
    private static string GuideParagraph(string opening)
    {
        var text = ReadRepoFile("docs/wisckey-value-log.md").Replace("\r\n", "\n");
        var start = text.IndexOf(opening, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, $"the WiscKey guide must keep the paragraph opening '{opening}'");
        var end = text.IndexOf("\n\n", start, StringComparison.Ordinal);
        return (end < 0 ? text[start..] : text[start..end]).Replace('\n', ' ').ToLowerInvariant();
    }

    private static string ReadRepoFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Boutquin.Storage.slnx")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the test runs inside the repository checkout");
        return File.ReadAllText(Path.Combine(directory!.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar)));
    }
}
