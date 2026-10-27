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
using Wal = global::Boutquin.Storage.Infrastructure.WriteAheadLog.WriteAheadLog<
    Boutquin.Storage.Domain.Helpers.SerializableWrapper<int>,
    Boutquin.Storage.Domain.Helpers.SerializableWrapper<string>>;
using WalOptions = global::Boutquin.Storage.Infrastructure.WriteAheadLog.WriteAheadLogOptions;

namespace Boutquin.Storage.Infrastructure.Tests.WriteAheadLog;

/// <summary>
/// F-33 — WAL <c>TruncateCompletedSegmentsAsync</c> watermark semantics. After the LSM flushes a
/// memtable to a durable SSTable, it reclaims the WAL frames that flush covered by passing the
/// flushed watermark seqno. Reclamation is conservative: a segment is deleted only when its
/// highest appended seqno is fully covered; a segment carrying any frame above the watermark still
/// backs unflushed state and must be retained; a segment whose max seqno was never recorded (a
/// pre-existing file this instance neither appended to nor recovered) is conservatively retained;
/// the current append segment, when fully covered, is deleted and the counter resets to a fresh
/// segment. These four behaviours are the data-safety contract of WAL truncation — losing a
/// retained segment is acked-write loss.
/// </summary>
public sealed class TruncateCompletedSegmentsTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), $"WalTruncateCompleted_{Guid.NewGuid():N}");

    // 1 MiB is the smallest the options validator allows; with ~500-byte payloads a few thousand
    // frames rotate into several segments.
    private static readonly WalOptions s_smallSegments = new() { SegmentMaxBytes = 1L * 1024L * 1024L };

    public TruncateCompletedSegmentsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    private static SerializableWrapper<int> K(int i) => new(i);
    private static SerializableWrapper<string> V(string s) => new(s);

    private List<string> SegmentFiles() =>
        Directory.GetFiles(_dir, "wal_*.log").OrderBy(p => p, StringComparer.Ordinal).ToList();

    /// <summary>
    /// Raw frame scanner: returns the highest seqno across all readable frames in one segment file.
    /// Layout per frame after the 8-byte file header: [4B len LE][1B kind][1B vkind][8B seqno LE]
    /// [4B header crc][payload][4B payload crc]. Lets a test pick a watermark relative to a real segment's max.
    /// </summary>
    private static ulong MaxSeqnoInSegment(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var max = 0UL;
        var pos = 8; // skip file header
        while (pos + 4 <= bytes.Length)
        {
            var payloadLen = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(
                bytes.AsSpan(pos, 4));
            if (payloadLen <= 0)
            {
                break;
            }
            var frameLen = 4 + 1 + 1 + 8 + 4 + payloadLen + 4;
            if (pos + frameLen > bytes.Length)
            {
                break;
            }
            var seqno = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(
                bytes.AsSpan(pos + 4 + 1 + 1, 8));
            if (seqno > max)
            {
                max = seqno;
            }
            pos += frameLen;
        }
        return max;
    }

    private async Task WriteSpanningSegmentsAsync(int count, int payloadSize)
    {
        using var wal = new Wal(_dir, s_smallSegments);
        var payload = new string('x', payloadSize);
        var tasks = Enumerable.Range(0, count)
            .Select(i => wal.AppendAsync(K(i), V(payload), (ulong)(i + 1)))
            .ToArray();
        await Task.WhenAll(tasks).ConfigureAwait(true);
    }

    // ── (a) partial-watermark deletes only fully-covered segments ──────────────

    [Fact]
    public async Task TruncateCompleted_PartialWatermark_DeletesOnlyFullyCoveredSegments()
    {
        await WriteSpanningSegmentsAsync(4_000, 500).ConfigureAwait(true);
        var before = SegmentFiles();
        before.Count.Should().BeGreaterThan(1, "the arrange step must span multiple segments");

        // Watermark = max seqno of the FIRST segment exactly. The first segment is fully covered
        // (every frame <= watermark); the last segment holding frames carries frames above it and
        // must survive. (A rotation publishes the next segment as soon as one fills, so the highest
        // file may be an empty successor.)
        var firstSegmentMax = MaxSeqnoInSegment(before[0]);
        var lastWithFrames = before.Last(p => MaxSeqnoInSegment(p) > 0);
        var lastSegmentMax = MaxSeqnoInSegment(lastWithFrames);
        lastSegmentMax.Should().BeGreaterThan(firstSegmentMax, "later segments carry higher seqnos");

        using var wal = new Wal(_dir, s_smallSegments);
        await wal.RecoverAsync().ConfigureAwait(true); // record every segment's max seqno
        await wal.TruncateCompletedSegmentsAsync(firstSegmentMax).ConfigureAwait(true);

        var after = SegmentFiles();
        after.Should().NotContain(before[0],
            "the first segment is fully covered by the watermark and must be deleted");
        after.Should().Contain(lastWithFrames,
            "the last segment holding frames carries frames above the watermark and must be retained");
        after.Count.Should().BeLessThan(before.Count, "at least the fully-covered first segment is reclaimed");
    }

    // ── (b) retention of a segment with any frame above the watermark ──────────

    [Fact]
    public async Task TruncateCompleted_WatermarkBelowSegmentMax_RetainsThatSegment()
    {
        await WriteSpanningSegmentsAsync(4_000, 500).ConfigureAwait(true);
        var before = SegmentFiles();
        before.Count.Should().BeGreaterThan(1);

        // Watermark one below the FIRST segment's max — the first segment now carries exactly one
        // frame above the watermark, so even the first segment must be retained.
        var firstSegmentMax = MaxSeqnoInSegment(before[0]);
        var watermark = firstSegmentMax - 1;

        using var wal = new Wal(_dir, s_smallSegments);
        await wal.RecoverAsync().ConfigureAwait(true);
        await wal.TruncateCompletedSegmentsAsync(watermark).ConfigureAwait(true);

        SegmentFiles().Should().Contain(before[0],
            "a segment with any frame above the watermark still backs unflushed state and must be retained");
    }

    // ── (c) truncation of the current append segment with counter reset ────────

    [Fact]
    public async Task TruncateCompleted_CoversCurrentSegment_DeletesItAndResetsCounter()
    {
        // A single segment (default large SegmentMaxBytes keeps everything in wal_000001.log).
        ulong maxSeqno;
        using (var wal = new Wal(_dir))
        {
            await wal.AppendAsync(K(1), V("a"), 1UL).ConfigureAwait(true);
            await wal.AppendAsync(K(2), V("b"), 2UL).ConfigureAwait(true);
            await wal.AppendAsync(K(3), V("c"), 3UL).ConfigureAwait(true);
            maxSeqno = 3UL;

            SegmentFiles().Should().ContainSingle("the writes stay in the single current append segment");

            // Watermark covers everything in the current segment → it is closed, deleted, and the
            // counter moves to a fresh segment (resetting since the directory emptied).
            await wal.TruncateCompletedSegmentsAsync(maxSeqno).ConfigureAwait(true);

            SegmentFiles().Should().BeEmpty(
                "the fully-covered current append segment must be deleted, leaving no segment files");

            // A subsequent append must land cleanly in a fresh segment named wal_000001.log
            // (the counter reset to 1 when the directory emptied, mirroring TruncateAsync).
            await wal.AppendAsync(K(4), V("d"), 4UL).ConfigureAwait(true);
            File.Exists(Path.Combine(_dir, "wal_000001.log")).Should().BeTrue(
                "after the current segment is reclaimed the counter resets and the next append opens wal_000001.log");
        }

        using var reader = new Wal(_dir);
        var entries = await reader.RecoverAsync().ConfigureAwait(true);
        entries.Select(e => e.Key.Value).Should().Equal([4],
            "only the post-truncation append survives; the covered entries 1-3 were reclaimed");
    }

    // ── (d) retention of a segment with no recorded max seqno ──────────────────

    [Fact]
    public async Task TruncateCompleted_SegmentWithNoRecordedMaxSeqno_IsRetained()
    {
        // First WAL writes a segment, then closes. The bytes are on disk.
        using (var writer = new Wal(_dir))
        {
            await writer.AppendAsync(K(1), V("a"), 1UL).ConfigureAwait(true);
            await writer.AppendAsync(K(2), V("b"), 2UL).ConfigureAwait(true);
        }
        var existing = SegmentFiles();
        existing.Should().ContainSingle();

        // A FRESH WAL instance that never appended to nor recovered this segment has no recorded
        // max seqno for it. Truncation must conservatively RETAIN it — deleting a segment whose
        // coverage is unknown would be acked-write loss.
        using (var fresh = new Wal(_dir))
        {
            await fresh.TruncateCompletedSegmentsAsync(ulong.MaxValue).ConfigureAwait(true);
        }

        SegmentFiles().Should().Contain(existing[0],
            "a segment whose max seqno was never recorded (no appends observed, no recovery scan) " +
            "must be conservatively retained even under a maximal watermark");

        // Its entries are still recoverable — nothing was lost.
        using var reader = new Wal(_dir);
        var entries = await reader.RecoverAsync().ConfigureAwait(true);
        entries.Select(e => e.Key.Value).Should().Equal([1, 2]);
    }

    // ── (e) unlink durability: deletions must fsync the containing directory ───

    /// <summary>
    /// On POSIX filesystems an unlink is not durable until the directory inode lands (Pillai
    /// et al. 2014 §3 — the same crash model segment CREATION already guards with a directory
    /// fsync). A crash after truncation can otherwise resurrect a deleted segment, and WAL
    /// replay applies its frames unconditionally into the memtable — an old value then shadows
    /// the newer state that the covering SSTable flush made durable. Filesystem reordering is
    /// not unit-testable, so these tests pin the code contract via the internal fsync counter
    /// (mirroring <see cref="FileRaftLogDirectoryFsyncTests"/>).
    /// </summary>
    [Fact]
    public async Task TruncateCompleted_DeletingSegments_FsyncsDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await WriteSpanningSegmentsAsync(4_000, 500).ConfigureAwait(true);
        var before = SegmentFiles();
        before.Count.Should().BeGreaterThan(1, "the arrange step must span multiple segments");
        var firstSegmentMax = MaxSeqnoInSegment(before[0]);

        using var wal = new Wal(_dir, s_smallSegments);
        await wal.RecoverAsync().ConfigureAwait(true);
        wal._deletionDirectoryFsyncCount.Should().Be(0, "no deletion has happened yet");

        await wal.TruncateCompletedSegmentsAsync(firstSegmentMax).ConfigureAwait(true);

        SegmentFiles().Count.Should().BeLessThan(before.Count, "the truncation must have deleted at least one segment");
        wal._deletionDirectoryFsyncCount.Should().BeGreaterThanOrEqualTo(1,
            "deleting WAL segments must fsync the containing directory so the unlinks are durable — " +
            "a resurrected segment's unconditional replay would shadow newer flushed state");
    }

    // ── (f) reclamation keeps the surviving segments contiguous ────────────────

    /// <summary>
    /// Recovery treats a missing segment between the first and the last as lost history, so
    /// reclamation may only remove a contiguous prefix: a segment whose frames are all covered but
    /// which follows a retained segment (seqnos need not grow with the segment index) stays.
    /// </summary>
    [Fact]
    public async Task TruncateCompleted_CoveredSegmentAfterARetainedOne_IsRetainedSoNoGapForms()
    {
        // Segment 1 carries seqnos 100..103, segment 2 carries 1..4, segment 3 carries 5.
        await WriteFourFramesPerSegmentAsync([100UL, 101UL, 102UL, 103UL, 1UL, 2UL, 3UL, 4UL, 5UL]).ConfigureAwait(true);
        var before = SegmentFiles();
        before.Should().HaveCount(3, "precondition: four 300 KB frames fill each 1 MiB segment");

        using (var wal = new Wal(_dir, s_smallSegments))
        {
            await wal.RecoverAsync().ConfigureAwait(true);
            await wal.TruncateCompletedSegmentsAsync(50UL).ConfigureAwait(true);
        }

        SegmentFiles().Should().Equal(before,
            "segment 2 is fully covered but follows the retained segment 1; deleting it would leave a gap");

        using var reader = new Wal(_dir, s_smallSegments);
        (await reader.RecoverAsync().ConfigureAwait(true)).Should().HaveCount(9);
    }

    /// <summary>
    /// Each reclaimed segment's unlink is made durable before the next one is attempted, so a crash
    /// part-way through a reclamation leaves a contiguous run of segments rather than a gap.
    /// </summary>
    [Fact]
    public async Task TruncateCompleted_DeletingSeveralSegments_FsyncsTheDirectoryAfterEachDeletion()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await WriteFourFramesPerSegmentAsync([1UL, 2UL, 3UL, 4UL, 5UL, 6UL, 7UL, 8UL, 9UL]).ConfigureAwait(true);
        SegmentFiles().Should().HaveCount(3);

        using var wal = new Wal(_dir, s_smallSegments);
        await wal.RecoverAsync().ConfigureAwait(true);
        await wal.TruncateCompletedSegmentsAsync(8UL).ConfigureAwait(true);

        SegmentFiles().Should().HaveCount(1, "segments 1 and 2 are fully covered");
        wal._deletionDirectoryFsyncCount.Should().Be(2,
            "one directory fsync per deleted segment keeps every crash state a contiguous run of segments");
    }

    /// <summary>
    /// Appends one 300 KB frame per seqno (keys 1..n) in order; four such frames fill a 1 MiB segment.
    /// </summary>
    private async Task WriteFourFramesPerSegmentAsync(ulong[] seqnos)
    {
        using var wal = new Wal(_dir, s_smallSegments);
        var value = new string('w', 300_000);
        for (var i = 0; i < seqnos.Length; i++)
        {
            await wal.AppendAsync(K(i + 1), V(value), seqnos[i]).ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task TruncateAll_DeletingSegments_FsyncsDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var wal = new Wal(_dir);
        await wal.AppendAsync(K(1), V("v1"), 1UL).ConfigureAwait(true);

        await wal.TruncateAsync().ConfigureAwait(true);

        wal._deletionDirectoryFsyncCount.Should().BeGreaterThanOrEqualTo(1,
            "a full WAL truncation deletes every segment and must make the unlinks durable with a directory fsync");
    }
}
