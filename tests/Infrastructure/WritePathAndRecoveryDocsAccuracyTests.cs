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

namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// Pins what the write-path and recovery documentation says by executing it. The LSM flusher
/// reclaims the WAL by a flushed watermark (<c>TruncateCompletedSegmentsAsync</c>), never by
/// emptying the whole log when the immutable queue drains; the whole-log <c>TruncateAsync</c> runs
/// only on <c>ClearAsync</c>. The durable logs' recovery threat model is: crash tears and
/// mid-file damage are detected (a torn tail is truncated, damage inside acknowledged bytes fails
/// loud, sealed segments are verified, a missing segment fails loud), while truncating the active
/// tail or a single-file log exactly at a frame boundary is indistinguishable from "those frames
/// were never written" and is outside the threat model. Each statement is exercised on the real
/// log, and the prose that states it is checked against the same vocabulary.
/// </summary>
public sealed class WritePathAndRecoveryDocsAccuracyTests : IDisposable
{
    private const int WalFileHeaderSize = 8;
    private const int RaftHeaderSize = 25;

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), $"WritePathRecoveryDocs_{Guid.NewGuid():N}");

    public WritePathAndRecoveryDocsAccuracyTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best-effort */ }
        }
    }

    // ── documentation text ─────────────────────────────────────────────────────

    [Fact]
    public void ArchitectureWritePath_DescribesWatermarkTruncation_NotAWholeLogTruncate()
    {
        var writePath = Section(ReadRepoFile("ARCHITECTURE.md"), "### Write Path", "### Read Path");

        writePath.Should().NotContain("WAL.TruncateAsync()",
            "the flusher never empties the whole WAL when the queue drains");
        writePath.Should().NotContain("If queue empty",
            "WAL reclamation is not conditioned on the immutable queue draining");
        writePath.Should().Contain("TruncateCompletedSegmentsAsync",
            "the flusher reclaims the WAL by the flushed watermark");
        writePath.Should().Contain("ClearAsync",
            "the whole-log truncate is named as the ClearAsync-only operation");
    }

    [Theory]
    [InlineData("README.md", "## Design Decisions")]
    [InlineData("docs/PITFALLS.md", "## 4. Corruption Recovery Semantics")]
    public void RecoveryThreatModel_IsStatedInTheDocumentThatOwnsIt(string path, string anchor)
    {
        var text = ReadRepoFile(path);
        var start = text.IndexOf(anchor, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, $"{path} must keep the section that hosts the threat model");

        var model = Section(text[start..], "Recovery threat model", "\n## ", allowMissingEnd: true).ToLowerInvariant();
        model.Should().NotBeEmpty($"{path} must state the durable logs' recovery threat model");
        model.Should().Contain("torn", "crash tears are detected and repaired");
        model.Should().Contain("sealed", "sealed segments are verified against their seals");
        model.Should().Contain("manifest", "a lost value-log manifest fails loud");
        model.Should().Contain("missing", "a missing segment fails loud");
        model.Should().Contain("frame boundary", "frame-boundary truncation is the declared blind spot");
        model.Should().Contain("outside", "the blind spot is stated as outside the threat model");
    }

    [Fact]
    public void ReadmeEngineSummary_DoesNotClaimCorruptionToleranceTheRecoveryRefuses()
    {
        ReadRepoFile("README.md").Should().NotContain("corruption-tolerant recovery",
            "recovery repairs a torn tail and fails loud on every other damage; it never tolerates corruption");
    }

    // ── write path, executed ───────────────────────────────────────────────────

    [Fact]
    public async Task FlushedMemtable_WithTheQueueDrained_KeepsTheWalFramesAboveTheWatermark()
    {
        var walDir = Path.Combine(_dir, "wal");
        var segDir = Path.Combine(_dir, "seg");
        Directory.CreateDirectory(segDir);
        using var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(walDir);
        var options = new LsmStorageEngineOptions<SerializableWrapper<int>, SerializableWrapper<string>>(
            MemTableCapacity: 4,
            SegmentFolder: segDir,
            SegmentPrefix: "seg",
            SegmentFormat: new SstSegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>>(),
            WriteAheadLog: wal);
        await using var engine = await LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>
            .OpenAsync(options).ConfigureAwait(true);

        for (var i = 1; i <= 6; i++)
        {
            await engine.SetAsync(new SerializableWrapper<int>(i), new SerializableWrapper<string>("v" + i))
                .ConfigureAwait(true);
        }

        // The sealed memtable (seqnos 1-4) flushes in the background; the queue is then empty.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (engine.SegmentCount == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20).ConfigureAwait(true);
        }

        engine.SegmentCount.Should().BeGreaterThan(0, "the sealed memtable must have flushed");
        await Task.Delay(250).ConfigureAwait(true); // let the post-commit WAL reclamation run

        var recovered = RecoverCopyOf(walDir);

        recovered.Select(e => e.SequenceNumber).Should().Equal([1UL, 2UL, 3UL, 4UL, 5UL, 6UL],
            "the flusher reclaims only segments wholly at or below the flushed watermark; the segment that also holds unflushed frames stays whole");
    }

    [Fact]
    public async Task ClearAsync_IsTheEngineOperationThatEmptiesTheWholeWal()
    {
        var walDir = Path.Combine(_dir, "wal-clear");
        var segDir = Path.Combine(_dir, "seg-clear");
        Directory.CreateDirectory(segDir);
        using var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(walDir);
        var options = new LsmStorageEngineOptions<SerializableWrapper<int>, SerializableWrapper<string>>(
            MemTableCapacity: 64,
            SegmentFolder: segDir,
            SegmentPrefix: "seg",
            SegmentFormat: new SstSegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>>(),
            WriteAheadLog: wal);
        await using var engine = await LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>
            .OpenAsync(options).ConfigureAwait(true);
        for (var i = 1; i <= 3; i++)
        {
            await engine.SetAsync(new SerializableWrapper<int>(i), new SerializableWrapper<string>("v" + i))
                .ConfigureAwait(true);
        }

        RecoverCopyOf(walDir).Should().HaveCount(3, "nothing has flushed, so every frame is still needed");

        await engine.ClearAsync().ConfigureAwait(true);

        RecoverCopyOf(walDir).Should().BeEmpty("ClearAsync is the one engine path that truncates the whole log");
    }

    // ── WAL threat model, executed ─────────────────────────────────────────────

    [Fact]
    public async Task Wal_TornActiveTail_IsTruncatedAndEveryEarlierFrameRecovered()
    {
        var segment = await WriteThreeFramesAsync().ConfigureAwait(true);
        var boundaries = WalFrameEnds(File.ReadAllBytes(segment));
        var bytes = File.ReadAllBytes(segment);
        // Frame 3 cut inside its payload: a valid header whose payload is short at end of file.
        File.WriteAllBytes(segment, bytes[..(boundaries[1] + 30)]);

        var recovered = await RecoverAsync().ConfigureAwait(true);

        recovered.Select(e => e.SequenceNumber).Should().Equal([1UL, 2UL]);
    }

    [Fact]
    public async Task Wal_MidFileDamage_FailsLoudAndLeavesTheSegmentUntouched()
    {
        var segment = await WriteThreeFramesAsync().ConfigureAwait(true);
        var bytes = File.ReadAllBytes(segment);
        var firstFrameEnd = WalFrameEnds(bytes)[0];
        bytes[firstFrameEnd - 6] ^= 0x01; // a payload byte of frame 1 of 3
        File.WriteAllBytes(segment, bytes);

        var act = () => RecoverAsync();

        await act.Should().ThrowAsync<WriteAheadLogCorruptException>();
        File.ReadAllBytes(segment).Should().Equal(bytes, "fail-loud recovery must not erase the frames after the damage");
    }

    [Fact]
    public async Task Wal_ActiveTailCutExactlyAtAFrameBoundary_RecoversWhatRemainsWithoutError()
    {
        var segment = await WriteThreeFramesAsync().ConfigureAwait(true);
        var bytes = File.ReadAllBytes(segment);
        File.WriteAllBytes(segment, bytes[..WalFrameEnds(bytes)[1]]);

        var recovered = await RecoverAsync().ConfigureAwait(true);

        recovered.Select(e => e.SequenceNumber).Should().Equal([1UL, 2UL],
            "a cut at a frame boundary is indistinguishable from frames never written: outside the threat model");
    }

    [Fact]
    public async Task Wal_SealedSegmentCutAtAFrameBoundary_FailsLoud()
    {
        var segments = await WriteRotatedSegmentsAsync(minimumSegments: 2).ConfigureAwait(true);
        var bytes = File.ReadAllBytes(segments[0]);
        var frameEnds = WalFrameEnds(bytes);
        File.WriteAllBytes(segments[0], bytes[..frameEnds[^3]]);

        var act = () => RecoverAsync();

        await act.Should().ThrowAsync<WriteAheadLogCorruptException>(
            "a sealed segment must end in a seal that matches it, so a cut that loses the seal is detected");
    }

    [Fact]
    public async Task Wal_MissingMiddleSegment_FailsLoud()
    {
        var segments = await WriteRotatedSegmentsAsync(minimumSegments: 3).ConfigureAwait(true);
        File.Delete(segments[1]);

        var act = () => RecoverAsync();

        await act.Should().ThrowAsync<WriteAheadLogCorruptException>(
            "segment indices from the first to the last must be contiguous");
    }

    // ── Raft log threat model, executed ────────────────────────────────────────

    [Fact]
    public void RaftLog_TornFinalFrame_IsTruncatedAndEarlierEntriesSurvive()
    {
        var ends = WriteRaftEntries(3);
        var bytes = File.ReadAllBytes(RaftPath);
        File.WriteAllBytes(RaftPath, bytes[..(int)(ends[1] + RaftHeaderSize + 1)]); // header landed, command short

        using var log = new FileRaftLog<byte[]>(RaftPath, b => b, b => b);

        log.Count.Should().Be(2);
        new FileInfo(RaftPath).Length.Should().Be(ends[1], "the torn tail is physically trimmed back to the last good frame");
    }

    [Fact]
    public void RaftLog_MidFileDamage_FailsLoud()
    {
        WriteRaftEntries(3);
        var bytes = File.ReadAllBytes(RaftPath);
        bytes[RaftHeaderSize] ^= 0x01; // first entry's command byte; two whole frames follow
        File.WriteAllBytes(RaftPath, bytes);

        var act = () => new FileRaftLog<byte[]>(RaftPath, b => b, b => b);

        act.Should().Throw<InvalidDataException>();
        File.ReadAllBytes(RaftPath).Should().Equal(bytes);
    }

    [Fact]
    public void RaftLog_FileCutExactlyAtAFrameBoundary_OpensWithFewerEntries()
    {
        var ends = WriteRaftEntries(3);
        var bytes = File.ReadAllBytes(RaftPath);
        File.WriteAllBytes(RaftPath, bytes[..(int)ends[1]]);

        using var log = new FileRaftLog<byte[]>(RaftPath, b => b, b => b);

        log.Count.Should().Be(2, "single-file logs cannot tell a boundary cut from entries never written: outside the threat model");
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private string RaftPath => Path.Combine(_dir, "raft.bin");

    /// <summary>Appends <paramref name="count"/> entries and returns the file length after each.</summary>
    private long[] WriteRaftEntries(int count)
    {
        var ends = new long[count];
        using var log = new FileRaftLog<byte[]>(RaftPath, b => b, b => b);
        for (var i = 0; i < count; i++)
        {
            log.Append((i + 1L, System.Text.Encoding.UTF8.GetBytes("command-" + i)));
            ends[i] = new FileInfo(RaftPath).Length;
        }

        return ends;
    }

    private string WalDirectory => Path.Combine(_dir, "durable-wal");

    private static WriteAheadLogOptions SmallSegments => new() { SegmentMaxBytes = 1L * 1024L * 1024L };

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

    private static IReadOnlyList<WalEntry<SerializableWrapper<int>, SerializableWrapper<string>>> RecoverCopyOf(string walDirectory)
    {
        var copy = walDirectory + "-copy-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(copy);
        foreach (var file in Directory.GetFiles(walDirectory).Where(f => !f.EndsWith("wal.lock", StringComparison.Ordinal)))
        {
            File.Copy(file, Path.Combine(copy, Path.GetFileName(file)));
        }

        using var reader = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(copy);
        return reader.RecoverAsync().GetAwaiter().GetResult();
    }

    /// <summary>End offset of every whole frame in a WAL segment, in file order.</summary>
    private static List<int> WalFrameEnds(byte[] bytes)
    {
        var ends = new List<int>();
        var position = WalFileHeaderSize;
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

    private static string Section(string text, string startMarker, string endMarker, bool allowMissingEnd = false)
    {
        var start = text.IndexOf(startMarker, StringComparison.Ordinal);
        if (start < 0)
        {
            return string.Empty;
        }

        var end = text.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        if (end < 0)
        {
            end = allowMissingEnd ? text.Length : throw new InvalidOperationException($"No '{endMarker}' after '{startMarker}'.");
        }

        return text[start..end];
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
