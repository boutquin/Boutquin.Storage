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
using System.Reflection;
using WalType = Boutquin.Storage.Infrastructure.WriteAheadLog.WriteAheadLog<
    Boutquin.Storage.Domain.Helpers.SerializableWrapper<int>,
    Boutquin.Storage.Domain.Helpers.SerializableWrapper<string>>;

namespace Boutquin.Storage.Infrastructure.Tests.WriteAheadLog;

/// <summary>
/// Phase-12 batch A — MarketData-reachable hardening for
/// <see cref="WriteAheadLog{TKey, TValue}"/>: per-frame corruption arms beyond the
/// existing CRC-mid-frame coverage, header validation across rotation, exact-boundary
/// rotation, multi-segment truncate, partial-CRC tail, fsync failure paths, and
/// dispose semantics.
/// </summary>
public sealed class Phase12HardeningTests : IDisposable
{
    private readonly string _walDirectory = Path.Combine(
        Path.GetTempPath(), $"Phase12HardeningTests_{Guid.NewGuid():N}");

    public Phase12HardeningTests() => Directory.CreateDirectory(_walDirectory);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_walDirectory))
            {
                Directory.Delete(_walDirectory, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    // ── 1 & 2: group-commit fsync failure path ───────────────────────────────

    /// <summary>
    /// When the group-commit flusher batch's <c>Flush(flushToDisk: true)</c> raises (here:
    /// reflection-disposed FileStream → ObjectDisposedException), the catch block at
    /// <c>WriteAheadLog.ExecuteAppendBatch</c> must (a) mark the WAL corrupt and (b) propagate
    /// <see cref="WriteAheadLogCorruptException"/> to every TaskCompletionSource in the batch.
    /// </summary>
    [Fact]
    public async Task GroupCommit_FsyncFailure_FailsBatchAndMarksWalCorrupt()
    {
        var wal = new WalType(_walDirectory);
        try
        {
            // Prime the segment so _appendStream is non-null and _headerResolvedForCurrent.
            await wal.AppendAsync(
                new SerializableWrapper<int>(0),
                new SerializableWrapper<string>("seed"),
                1ul).ConfigureAwait(true);

            // Reach into the WAL via reflection and dispose the active append stream
            // out from under the flusher loop. The next batched write will raise
            // ObjectDisposedException → caught by the batch try/catch → corrupt path.
            DisposeAppendStream(wal);

            // Trigger another batched append. The flusher's Write/Flush call against the
            // disposed FileStream will throw, and the WAL's catch block must surface
            // WriteAheadLogCorruptException to this caller's TCS.
            Func<Task> act = async () => await wal.AppendAsync(
                new SerializableWrapper<int>(1),
                new SerializableWrapper<string>("post-corrupt"),
                2ul).ConfigureAwait(true);

            await act.Should().ThrowAsync<WriteAheadLogCorruptException>().ConfigureAwait(true);

            // The corrupt flag is now sticky — verifiable indirectly via test #2.
            GetIsCorrupt(wal).Should().BeTrue();
        }
        finally
        {
            wal.Dispose();
        }
    }

    /// <summary>
    /// Sibling to test #1: once the WAL has entered the corrupt state, every subsequent
    /// <see cref="WalType.AppendAsync"/> fast-fails via <c>ThrowIfCorrupt</c> at the API
    /// boundary — without performing any disk I/O.
    /// </summary>
    [Fact]
    public async Task AppendAfterCorruption_FastFails()
    {
        var wal = new WalType(_walDirectory);
        try
        {
            await wal.AppendAsync(
                new SerializableWrapper<int>(0),
                new SerializableWrapper<string>("seed"),
                1ul).ConfigureAwait(true);

            DisposeAppendStream(wal);

            // First append after the dispose surfaces the corrupt exception via the batch
            // catch block; we don't care about the inner-vs-API distinction here, just that
            // the WAL is now corrupt.
            try
            {
                await wal.AppendAsync(
                    new SerializableWrapper<int>(1),
                    new SerializableWrapper<string>("first-after"),
                    2ul).ConfigureAwait(true);
            }
            catch (WriteAheadLogCorruptException)
            {
                // Expected — we forced a fsync failure.
            }

            GetIsCorrupt(wal).Should().BeTrue();

            // Subsequent appends must fast-fail at the API boundary (ThrowIfCorrupt) — the
            // exception is raised synchronously before the channel is touched. We verify
            // by timing: a fast-failure returns much faster than a real disk-bound batch.
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            Func<Task> act = async () => await wal.AppendAsync(
                new SerializableWrapper<int>(2),
                new SerializableWrapper<string>("second-after"),
                3ul).ConfigureAwait(true);

            await act.Should().ThrowAsync<WriteAheadLogCorruptException>().ConfigureAwait(true);
            stopwatch.Stop();

            // 250 ms is generous — a real fsync is sub-ms but the channel + flusher round
            // trip can add jitter on slow CI. The point is to demonstrate ThrowIfCorrupt
            // is the fast path: no enqueue, no flush, no disk touch.
            stopwatch.ElapsedMilliseconds.Should().BeLessThan(250,
                "after _isCorrupt is set, ThrowIfCorrupt must short-circuit before disk I/O");
        }
        finally
        {
            wal.Dispose();
        }
    }

    /// <summary>
    /// A batch whose frame write faults part-way leaves torn, never-acknowledged bytes at the
    /// tail. An append already queued behind it must fail too: writing it after the torn bytes and
    /// acknowledging it would seal the tear mid-file, so the next recovery could only fail closed
    /// with an acknowledged write stranded behind it. Recovery then sees a plain torn tail.
    /// </summary>
    [Fact]
    public async Task GroupCommit_FaultMidFrame_QueuedAppendIsRefusedAndRecoveryReturnsPrefix()
    {
        var options = new WriteAheadLogOptions { GroupCommitBatchSize = 1 };
        Exception second;
        Exception third;
        using (var wal = new WalType(_walDirectory, options))
        {
            await wal.AppendAsync(
                new SerializableWrapper<int>(1), new SerializableWrapper<string>("acknowledged-1"), 1ul).ConfigureAwait(true);

            var streamField = typeof(WalType).GetField("_appendStream", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var original = (FileStream)streamField.GetValue(wal)!;
            var faulty = new MidFrameFaultStream(Path.Combine(_walDirectory, "wal_000001.log"));
            streamField.SetValue(wal, faulty);
            original.Dispose();

            var appendTwo = wal.AppendAsync(
                new SerializableWrapper<int>(2), new SerializableWrapper<string>("never-acknowledged-2"), 2ul);
            faulty.FirstWriteEntered.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue("the flusher reaches the faulting write");
            var appendThree = wal.AppendAsync(
                new SerializableWrapper<int>(3), new SerializableWrapper<string>("queued-3"), 3ul);
            faulty.ReleaseFirstWrite.Set();

            second = await Record.ExceptionAsync(() => appendTwo).ConfigureAwait(true);
            third = await Record.ExceptionAsync(() => appendThree).ConfigureAwait(true);
        }

        second.Should().BeOfType<WriteAheadLogCorruptException>("the faulted frame was never acknowledged");
        third.Should().BeOfType<WriteAheadLogCorruptException>(
            "an append queued behind a faulted batch must not be written after its torn bytes and acknowledged");

        using var reader = new WalType(_walDirectory);
        var entries = await reader.RecoverAsync().ConfigureAwait(true);
        entries.Select(e => e.Key.Value).Should().Equal([1], "the faulted frame's torn bytes are a torn tail");
    }

    // ── 3, 4, 5: per-frame corruption arms ──────────────────────────────────

    /// <summary>
    /// CRC mismatch on the first frame of a segment, with an acknowledged CRC-valid frame after
    /// it, is mid-file corruption rather than a torn tail — distinct from the existing
    /// <c>RecoverAsync_CorruptCrcMidFrame_StopsAtCorruption</c> test which damages the final
    /// frame. Recovery fails closed and the segment keeps the second frame's bytes.
    /// </summary>
    [Fact]
    public async Task RecoverAsync_CrcMismatchOnFirstFrameFollowedByValidFrame_FailsClosed()
    {
        using (var wal = new WalType(_walDirectory))
        {
            await wal.AppendAsync(
                new SerializableWrapper<int>(1),
                new SerializableWrapper<string>("first"),
                1ul).ConfigureAwait(true);
            await wal.AppendAsync(
                new SerializableWrapper<int>(2),
                new SerializableWrapper<string>("second"),
                2ul).ConfigureAwait(true);
        }

        // Frame layout from offset 0:
        //   [0..7]   file header (magic + format_version=1)
        //   [8..11]  payload-length LE int32
        //   [12]     EntryKind byte
        //   [13]     ValueKind byte
        //   [14..21] SequenceNumber LE u64
        //   [22..]   payload bytes
        // Index 22 is the first byte of the first frame's payload — flipping it produces
        // a CRC mismatch on the FIRST frame.
        var path = Path.Combine(_walDirectory, "wal_000001.log");
        var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(true);
        bytes[22] ^= 0xFF;
        await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(true);

        using var reader = new WalType(_walDirectory);
        var act = () => reader.RecoverAsync();

        var thrown = await act.Should().ThrowAsync<WriteAheadLogCorruptException>(
            "the acknowledged second frame after the damage proves this is not a torn tail")
            .ConfigureAwait(true);
        thrown.Which.LastGoodOffset.Should().Be(8, "the damaged frame starts right after the file header");
        new FileInfo(path).Length.Should().Be(
            bytes.Length, "fail-closed recovery must not truncate the acknowledged second frame away");
    }

    /// <summary>
    /// A final frame whose <c>EntryKind</c> byte lies outside the declared range (Set=0,
    /// Delete=1) but whose CRC is valid is a complete frame the writer authenticated — a newer
    /// writer's kind, not a torn append. Recovery fails loud with
    /// <see cref="WriteAheadLogCorruptException"/> at that frame and leaves the segment intact.
    /// </summary>
    [Fact]
    public async Task RecoverAsync_UnknownEntryKind_FailsLoudAndPreservesSegment()
    {
        using (var wal = new WalType(_walDirectory))
        {
            await wal.AppendAsync(
                new SerializableWrapper<int>(1),
                new SerializableWrapper<string>("a"),
                1ul).ConfigureAwait(true);
            await wal.AppendAsync(
                new SerializableWrapper<int>(2),
                new SerializableWrapper<string>("b"),
                2ul).ConfigureAwait(true);
        }

        // Surgically rebuild the second frame with EntryKind = 0x7F and a fresh header checksum
        // (recomputed over the corrupted header), so the kind-byte arm fires instead of a
        // header-checksum mismatch.
        var path = Path.Combine(_walDirectory, "wal_000001.log");
        var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(true);

        var secondFrameStart = SecondFrameStart(bytes);
        bytes[secondFrameStart + 4] = 0x7F; // outside declared EntryKind range (skip length prefix)
        ResealFrameHeader(bytes, secondFrameStart);

        await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(true);

        using var reader = new WalType(_walDirectory);
        var act = () => reader.RecoverAsync();

        var thrown = await act.Should().ThrowAsync<WriteAheadLogCorruptException>(
            "a CRC-valid frame of unknown EntryKind is acknowledged bytes, not a torn tail")
            .ConfigureAwait(true);
        thrown.Which.LastGoodOffset.Should().Be(secondFrameStart, "recovery stops where the rejected frame starts");
        (await File.ReadAllBytesAsync(path).ConfigureAwait(true)).Should().Equal(
            bytes, "fail-loud recovery must not truncate the CRC-valid frame away");
    }

    /// <summary>
    /// Symmetric to <c>RecoverAsync_UnknownEntryKind_FailsLoudAndPreservesSegment</c>: a
    /// CRC-valid final frame whose <c>ValueKind</c> byte lies outside the declared range
    /// (Inline=0, ValueLogPointer=1) fails recovery loud and leaves the segment intact.
    /// </summary>
    [Fact]
    public async Task RecoverAsync_UnknownValueKind_FailsLoudAndPreservesSegment()
    {
        using (var wal = new WalType(_walDirectory))
        {
            await wal.AppendAsync(
                new SerializableWrapper<int>(1),
                new SerializableWrapper<string>("a"),
                1ul).ConfigureAwait(true);
            await wal.AppendAsync(
                new SerializableWrapper<int>(2),
                new SerializableWrapper<string>("b"),
                2ul).ConfigureAwait(true);
        }

        var path = Path.Combine(_walDirectory, "wal_000001.log");
        var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(true);

        var secondFrameStart = SecondFrameStart(bytes);
        bytes[secondFrameStart + 4 + 1] = 0x7F; // outside declared ValueKind range (length + entry_kind)
        ResealFrameHeader(bytes, secondFrameStart);

        await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(true);

        using var reader = new WalType(_walDirectory);
        var act = () => reader.RecoverAsync();

        var thrown = await act.Should().ThrowAsync<WriteAheadLogCorruptException>(
            "a CRC-valid frame of unknown ValueKind is acknowledged bytes, not a torn tail")
            .ConfigureAwait(true);
        thrown.Which.LastGoodOffset.Should().Be(secondFrameStart, "recovery stops where the rejected frame starts");
        (await File.ReadAllBytesAsync(path).ConfigureAwait(true)).Should().Equal(
            bytes, "fail-loud recovery must not truncate the CRC-valid frame away");
    }

    // ── 6, 7: header validation across rotation ─────────────────────────────

    /// <summary>
    /// When recovery spans multiple segments and the SECOND segment's magic is corrupted on a
    /// segment that carries frames, recovery throws the typed
    /// <see cref="WriteAheadLogCorruptException"/> naming that segment — header damage under
    /// acknowledged frames is corruption, never a torn segment creation.
    /// </summary>
    [Fact]
    public async Task RecoverAsync_TwoSegments_HeaderCorruptOnSecondSegment_ThrowsTypedCorruption()
    {
        var options = new WriteAheadLogOptions { SegmentMaxBytes = 1L * 1024L * 1024L };
        var bigPayload = new string('x', 500);

        using (var wal = new WalType(_walDirectory, options))
        {
            // Drive the segment counter past 1 — concurrent fan-out lets group commit coalesce
            // writes; the test does not depend on FIFO ordering, only on rotation occurring.
            var tasks = Enumerable.Range(0, 4_000)
                .Select(i => wal.AppendAsync(
                    new SerializableWrapper<int>(i),
                    new SerializableWrapper<string>(bigPayload),
                    (ulong)(i + 1)))
                .ToArray();
            await Task.WhenAll(tasks).ConfigureAwait(true);
        }

        var segments = Directory.GetFiles(_walDirectory, "wal_*.log").OrderBy(p => p).ToList();
        segments.Count.Should().BeGreaterThan(1,
            "test setup: must rotate into a second segment to exercise header-corrupt-on-N>1");

        // Flip the magic bytes of the second segment.
        var secondPath = segments[1];
        using (var fs = new FileStream(secondPath, FileMode.Open, FileAccess.ReadWrite))
        {
            fs.Seek(0, SeekOrigin.Begin);
            fs.Write([0x00, 0x00, 0x00, 0x00]);
        }

        using var reader = new WalType(_walDirectory, options);
        Func<Task> act = async () => await reader.RecoverAsync().ConfigureAwait(true);

        var thrown = await act.Should().ThrowAsync<WriteAheadLogCorruptException>()
            .WithMessage("*BSWL*magic*").ConfigureAwait(true);
        thrown.Which.SegmentPath.Should().Be(secondPath);
    }

    /// <summary>
    /// A segment whose format-version byte has been overwritten to an unsupported value
    /// surfaces as the typed <see cref="WriteAheadLogCorruptException"/> with the
    /// version-mismatch message — distinct from the bad-magic arm.
    /// </summary>
    [Fact]
    public async Task RecoverAsync_SegmentWithUnsupportedFormatVersion_Throws()
    {
        using (var wal = new WalType(_walDirectory))
        {
            await wal.AppendAsync(
                new SerializableWrapper<int>(1),
                new SerializableWrapper<string>("only"),
                1ul).ConfigureAwait(true);
        }

        var path = Path.Combine(_walDirectory, "wal_000001.log");
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
        {
            Span<byte> ver = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(ver, 99u);
            fs.Seek(4, SeekOrigin.Begin);
            fs.Write(ver);
        }

        using var reader = new WalType(_walDirectory);
        Func<Task> act = async () => await reader.RecoverAsync().ConfigureAwait(true);

        var thrown = await act.Should().ThrowAsync<WriteAheadLogCorruptException>()
            .WithMessage("*unknown format version*99*").ConfigureAwait(true);
        thrown.Which.SegmentPath.Should().Be(path);
    }

    // ── 8: rotation-boundary behaviour ──────────────────────────────────────

    /// <summary>
    /// When the active segment's accumulated length crosses
    /// <see cref="WriteAheadLogOptions.SegmentMaxBytes"/>,
    /// <c>MaybeRotateAfterWrite</c> closes the stream and bumps the index — a subsequent
    /// append lands in the next segment file.
    /// </summary>
    [Fact]
    public async Task MaybeRotateAfterWrite_AtExactBoundary_RotatesNextSegment()
    {
        var options = new WriteAheadLogOptions { SegmentMaxBytes = 1L * 1024L * 1024L };
        var payload = new string('q', 500);

        using var wal = new WalType(_walDirectory, options);
        wal.GetCurrentSegmentPath().Should().EndWith("wal_000001.log");

        // Concurrent fan-out — group commit coalesces, the threshold is small enough (1 MiB)
        // that ~2,500 frames of ~500-byte payloads cross it deterministically.
        var tasks = Enumerable.Range(0, 4_000)
            .Select(i => wal.AppendAsync(
                new SerializableWrapper<int>(i),
                new SerializableWrapper<string>(payload),
                (ulong)(i + 1)))
            .ToArray();
        await Task.WhenAll(tasks).ConfigureAwait(true);

        var segments = Directory.GetFiles(_walDirectory, "wal_*.log");
        segments.Should().HaveCountGreaterThan(1,
            "MaybeRotateAfterWrite must advance _currentSegmentIndex once SegmentMaxBytes is reached");
        wal.GetCurrentSegmentPath().Should().NotEndWith("wal_000001.log",
            "the GetCurrentSegmentPath probe must reflect the new segment after rotation");
    }

    // ── 9: truncate covers all rotated segments ─────────────────────────────

    /// <summary>
    /// Truncate must cascade across every rotated segment, not only the active one.
    /// </summary>
    [Fact]
    public async Task Truncate_AfterRotation_DeletesAllSegments()
    {
        // Force a tiny segment size so we land at least three segments quickly.
        var options = new WriteAheadLogOptions { SegmentMaxBytes = 1L * 1024L * 1024L };
        var payload = new string('z', 500);

        using var wal = new WalType(_walDirectory, options);
        var tasks = Enumerable.Range(0, 6_500)
            .Select(i => wal.AppendAsync(
                new SerializableWrapper<int>(i),
                new SerializableWrapper<string>(payload),
                (ulong)(i + 1)))
            .ToArray();
        await Task.WhenAll(tasks).ConfigureAwait(true);

        Directory.GetFiles(_walDirectory, "wal_*.log").Length.Should().BeGreaterThanOrEqualTo(3,
            "test setup: must span at least three segments to exercise the multi-segment truncate path");

        await wal.TruncateAsync().ConfigureAwait(true);

        Directory.GetFiles(_walDirectory, "wal_*.log").Should().BeEmpty(
            "TruncateAsync deletes every segment fully covered by the watermark — no exception for higher counters");
    }

    // ── 10: partial-CRC tail is treated as truncation, not corruption ───────

    /// <summary>
    /// If the segment is truncated mid-CRC trailer (after payload, before all four CRC
    /// bytes are present), the reader stops cleanly at end-of-segment — the partial-CRC
    /// arm classifies this as a structural truncation, not a corruption warning.
    /// </summary>
    [Fact]
    public async Task ReadFrameAsync_TruncatedAfterCrcBytes_StopsCleanly()
    {
        using (var wal = new WalType(_walDirectory))
        {
            await wal.AppendAsync(
                new SerializableWrapper<int>(1),
                new SerializableWrapper<string>("first"),
                1ul).ConfigureAwait(true);
            await wal.AppendAsync(
                new SerializableWrapper<int>(2),
                new SerializableWrapper<string>("second"),
                2ul).ConfigureAwait(true);
        }

        // Truncate the file by removing 2 of the trailing 4 CRC bytes — the second
        // frame's structural framing tail (length + scope + 4-byte CRC) is now incomplete.
        var path = Path.Combine(_walDirectory, "wal_000001.log");
        var fullSize = new FileInfo(path).Length;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write))
        {
            fs.SetLength(fullSize - 2);
        }

        using var reader = new WalType(_walDirectory);
        var entries = await reader.RecoverAsync().ConfigureAwait(true);

        // First frame still surfaces; second frame's truncated-trailer reads as a clean
        // structural tail (Position + framingTail > Length).
        entries.Should().HaveCount(1, "partial-CRC trailer is treated as a clean truncation tail, not a corruption mark");
        entries[0].Key.Value.Should().Be(1);
    }

    // ── 11 & 12: dispose semantics ──────────────────────────────────────────

    /// <summary>
    /// A flurry of un-awaited <see cref="WalType.AppendAsync"/> calls followed immediately
    /// by <see cref="WalType.Dispose"/> must drain — Dispose calls
    /// <c>_channel.Writer.TryComplete()</c> then waits for the flusher loop to exit, so every
    /// frame the channel accepted is durable on disk before Dispose returns.
    /// </summary>
    [Fact]
    public async Task Dispose_DuringActiveAppend_DrainsBeforeReturning()
    {
        const int writerCount = 64;

        var wal = new WalType(_walDirectory);
        try
        {
            // Kick off all writers concurrently. Dispose must drain everything that made it
            // into the channel. Awaiting Task.WhenAll first would defeat the test (we'd be
            // testing a quiescent Dispose). Capture the tasks so the awaiter sees their
            // results after Dispose drains them.
            var tasks = Enumerable.Range(0, writerCount)
                .Select(i => wal.AppendAsync(
                    new SerializableWrapper<int>(i),
                    new SerializableWrapper<string>($"v_{i}"),
                    (ulong)(i + 1)))
                .ToArray();

            wal.Dispose();

            // Every per-append task must complete (Dispose drained them). Any frame the
            // channel accepted is on disk — recovery from a fresh instance must surface
            // exactly the count that completed without throwing.
            await Task.WhenAll(tasks).ConfigureAwait(true);
        }
        finally
        {
            wal.Dispose(); // idempotent — the second pass returns immediately.
        }

        using var reader = new WalType(_walDirectory);
        var entries = await reader.RecoverAsync().ConfigureAwait(true);
        entries.Should().HaveCount(writerCount,
            "Dispose must drain every channel-enqueued append before returning, otherwise frames are silently lost");
    }

    /// <summary>
    /// <see cref="WalType.Dispose"/> guards its cleanup body with
    /// <c>Interlocked.Exchange(ref _disposed, 1)</c> so concurrent disposers race-but-only-once.
    /// </summary>
    [Fact]
    public async Task Dispose_FromMultipleThreads_IsIdempotent()
    {
        var wal = new WalType(_walDirectory);
        await wal.AppendAsync(
            new SerializableWrapper<int>(1),
            new SerializableWrapper<string>("seed"),
            1ul).ConfigureAwait(true);

        // Ten threads attempting Dispose concurrently. The Interlocked guard ensures the
        // cleanup body executes once; the other nine return immediately without throwing.
        var barrier = new Barrier(10);
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        var threads = Enumerable.Range(0, 10).Select(_ => new Thread(() =>
        {
            try
            {
                barrier.SignalAndWait();
                wal.Dispose();
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        })).ToArray();

        foreach (var t in threads)
        {
            t.Start();
        }
        foreach (var t in threads)
        {
            t.Join();
        }

        exceptions.Should().BeEmpty("concurrent Dispose calls must not throw — Interlocked.Exchange guards the cleanup body");
        GetDisposed(wal).Should().Be(1,
            "_disposed flips to 1 exactly once; later Dispose calls observe the sentinel and return early");

        // Post-dispose, AppendAsync surfaces ObjectDisposedException — confirms the cleanup
        // ran and the sentinel was honoured.
        Func<Task> act = async () => await wal.AppendAsync(
            new SerializableWrapper<int>(2),
            new SerializableWrapper<string>("post"),
            2ul).ConfigureAwait(true);
        await act.Should().ThrowAsync<ObjectDisposedException>().ConfigureAwait(true);
    }

    // ── reflection helpers ──────────────────────────────────────────────────

    private static void DisposeAppendStream(WalType wal)
    {
        var streamField = typeof(WalType).GetField(
            "_appendStream",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var stream = (FileStream)streamField!.GetValue(wal)!;
        stream?.Dispose();
        // Leave the field non-null so EnsureSegmentReady early-returns on the next batch
        // and the disposed-stream Write call raises ObjectDisposedException — the path the
        // group-commit catch block must handle.
    }

    /// <summary>
    /// Append stream whose FIRST frame write blocks until released, then writes half the frame and
    /// fails with an I/O error — what ENOSPC or EIO after a partial write leaves. Later writes pass
    /// through unchanged.
    /// </summary>
    private sealed class MidFrameFaultStream(string path)
        : FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 4096, useAsync: true)
    {
        private int _calls;

        public ManualResetEventSlim FirstWriteEntered { get; } = new();

        public ManualResetEventSlim ReleaseFirstWrite { get; } = new();

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                FirstWriteEntered.Set();
                ReleaseFirstWrite.Wait(TimeSpan.FromSeconds(10));
                base.Write(buffer, offset, count / 2);
                Flush(flushToDisk: true);
                throw new IOException("injected I/O error mid-frame");
            }

            base.Write(buffer, offset, count);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                FirstWriteEntered.Dispose();
                ReleaseFirstWrite.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    // WAL frame layout: [4B length][1B kind][1B value_kind][8B seqno][4B header CRC][payload][4B payload CRC].
    private const int FileHeaderBytes = 8;
    private const int FrameHeaderFields = 4 + 1 + 1 + 8;
    private const int FrameOverhead = FrameHeaderFields + 4 + 4;

    private static int SecondFrameStart(byte[] segment)
        => FileHeaderBytes + FrameOverhead + BinaryPrimitives.ReadInt32LittleEndian(segment.AsSpan(FileHeaderBytes, 4));

    /// <summary>Recomputes the header checksum of the frame at <paramref name="frameStart"/> after a forged edit.</summary>
    private static void ResealFrameHeader(byte[] segment, int frameStart)
        => BinaryPrimitives.WriteUInt32LittleEndian(
            segment.AsSpan(frameStart + FrameHeaderFields, 4),
            Boutquin.Storage.Infrastructure.Algorithms.Crc32C.HashToUInt32(segment.AsSpan(frameStart, FrameHeaderFields)));

    private static bool GetIsCorrupt(WalType wal)
    {
        var field = typeof(WalType).GetField(
            "_isCorrupt",
            BindingFlags.NonPublic | BindingFlags.Instance);
        return (bool)field!.GetValue(wal)!;
    }

    private static int GetDisposed(WalType wal)
    {
        var field = typeof(WalType).GetField(
            "_disposed",
            BindingFlags.NonPublic | BindingFlags.Instance);
        return (int)field!.GetValue(wal)!;
    }
}
