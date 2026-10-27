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

using Boutquin.Storage.Domain.ValueObjects.LsmTree;
using Boutquin.Storage.Domain.ValueObjects.ValueLog;

namespace Boutquin.Storage.Infrastructure.Tests.LsmTree;

/// <summary>
/// Arena round-1 cluster C01/C03 regression tests:
/// <list type="bullet">
/// <item><description>F-WA-C03-01 — the value-log GC's lock-free segment scan must not fault
/// (ObjectDisposedException) when a concurrent compaction disposes a captured reader mid-scan;
/// it must fail-closed (skip reclaim, no data loss) instead.</description></item>
/// <item><description>F-WA-C01-01 — <c>Dispose</c>/<c>DisposeAsync</c> must release retired-but-pinned
/// segment readers rather than leaking their file handles.</description></item>
/// <item><description>F-WB1-04 — the engine implements <see cref="IAsyncDisposable"/>.</description></item>
/// <item><description>F-WB1-05 — value-log GC serialisation is per-engine, not process-global.</description></item>
/// </list>
/// </summary>
public sealed class LsmEngineArenaR1Tests : IDisposable
{
    private readonly string _testDirectory =
        Path.Combine(Path.GetTempPath(), $"LsmEngineArenaR1_{Guid.NewGuid():N}");

    public LsmEngineArenaR1Tests() => Directory.CreateDirectory(_testDirectory);

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            try { Directory.Delete(_testDirectory, recursive: true); }
            catch (IOException) { /* test-runner cleanup — ignore */ }
        }
    }

    private static LsmStorageEngineOptions<SerializableWrapper<int>, SerializableWrapper<string>> Options(
        string folder, int threshold = 64, int memTableCapacity = 4, long? segmentMaxBytes = null)
        => new(
            MemTableCapacity: memTableCapacity,
            SegmentFolder: folder,
            SegmentPrefix: "seg",
            SegmentFormat: new SstSegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>>(),
            CompactionStrategy: null,
            ValueLog: segmentMaxBytes is { } cap
                ? new ValueLogOptions(ValueLogThresholdBytes: threshold, SegmentMaxBytes: cap)
                : new ValueLogOptions(ValueLogThresholdBytes: threshold));

    private static string Payload(int size) => new('x', size);

    // ── F-WA-C03-01: GC scan vs concurrent compaction race (deterministic seam) ──

    [Fact]
    public async Task CompactValueLog_WhenCompactionDisposesCapturedReaderMidScan_DoesNotFault_AndKeepsData()
    {
        // Two flushed SSTable segments → the GC's Phase-A snapshot captures both readers.
        // The deterministic GcScanPhaseBHook fires in the post-lock-release window (Phase B)
        // and runs a full compaction, which disposes the captured (non-pinned) readers via
        // ISegmentReader.ClearAsync — exactly what a concurrent CompactAsync would do. The
        // lock-free scan then reads a disposed reader.
        using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            Options(_testDirectory, threshold: 64, memTableCapacity: 4, segmentMaxBytes: 200));

        for (var i = 0; i < 4; i++)
        {
            await engine.SetAsync(new SerializableWrapper<int>(i), new SerializableWrapper<string>(Payload(128)));
        }
        await engine.FlushAsync();
        for (var i = 4; i < 8; i++)
        {
            await engine.SetAsync(new SerializableWrapper<int>(i), new SerializableWrapper<string>(Payload(128)));
        }
        await engine.FlushAsync();
        engine.SegmentCount.Should().Be(2, "two flushed batches produce two segments for the GC to scan");
        Directory.GetFiles(engine.ValueLogDirectory, "*.log").Length.Should().BeGreaterThan(1,
            "the race must expose closed value-log files that an incomplete scan could wrongly reclaim");

        // One-shot: dispose the captured readers exactly once, inside Phase B.
        engine.GcScanPhaseBHook = () =>
        {
            engine.GcScanPhaseBHook = null;
            engine.CompactAsync().GetAwaiter().GetResult();
        };

        // RED (pre-fix): this faults out of CompactValueLogAsync because the lock-free scan
        // touches a reader the in-hook compaction retired (the SortedStringTable disposed + its
        // backing file deleted) — surfacing as ObjectDisposedException ("Cannot access a closed
        // Stream") in the live race, or IOException/FileNotFoundException in this deterministic
        // seam. GREEN (post-fix): the scan tolerates the retirement, reports the round
        // incomplete, and CompactValueLogAsync returns normally without reclaiming.
        var result = await engine.CompactValueLogAsync();
        result.FilesReclaimed.Should().Be(0, "retirement makes the captured liveness set incomplete");

        // No data loss: every key still reads back at full length (the fail-closed property —
        // no value-log file was unlinked on the incomplete-scan round).
        for (var i = 0; i < 8; i++)
        {
            var (val, found) = await engine.TryGetValueAsync(new SerializableWrapper<int>(i));
            found.Should().BeTrue($"key {i} must survive the GC-vs-compaction race");
            val.Value.Length.Should().Be(128);
        }
    }

    [Fact]
    public async Task CompactValueLog_NormalRun_StillReclaims_WhenNoConcurrentDisposal()
    {
        // Mutation guard for the fail-closed branch: with NO hook (no mid-scan disposal) the GC
        // must still reclaim a fully-shadowed file. Proves the skip-reclaim path is gated on the
        // disposal, not unconditionally short-circuiting every round.
        using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            Options(_testDirectory, threshold: 64, memTableCapacity: 4, segmentMaxBytes: 200));

        await engine.SetAsync(new SerializableWrapper<int>(0), new SerializableWrapper<string>(Payload(128)));
        await engine.FlushAsync();
        await engine.SetAsync(new SerializableWrapper<int>(0), new SerializableWrapper<string>(Payload(128)));
        await engine.FlushAsync();
        await engine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>(Payload(128)));
        await engine.FlushAsync();
        await engine.CompactAsync();
        engine.SegmentCount.Should().Be(1);

        var result = await engine.CompactValueLogAsync();

        result.FilesReclaimed.Should().Be(1,
            "with no concurrent disposal the scan completes and the fully-shadowed file is reclaimed");
    }

    // ── F-WA-C01-01: Dispose releases retired-but-pinned readers ──

    [Fact]
    public async Task Dispose_WithRetiredPinnedReader_ReleasesIt_DeletingTheRetiredSegmentFile()
    {
        // Build a retired-but-pinned reader: flush a segment, acquire a snapshot that pins it,
        // overwrite + flush (new segment), compact (retires the pinned old segment → parked in
        // _retiredPinnedReaders), then dispose the ENGINE while the snapshot is still live.
        // Pre-fix: Dispose never disposes the parked reader, so the retired segment file leaks
        // on disk (and its file handle leaks until finalization). Post-fix: Dispose clears the
        // parked reader (delete + dispose), so the retired segment file is gone.
        var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            Options(_testDirectory, threshold: 1_000_000, memTableCapacity: 2));

        await engine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("a"));
        await engine.SetAsync(new SerializableWrapper<int>(2), new SerializableWrapper<string>("b"));
        await engine.FlushAsync();

        var preSnapFiles = Directory.GetFiles(_testDirectory, "seg_*.sst").Select(Path.GetFileName).ToHashSet();

        var snapshot = await engine.AcquireSnapshotAsync();

        await engine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("c"));
        await engine.SetAsync(new SerializableWrapper<int>(2), new SerializableWrapper<string>("d"));
        await engine.FlushAsync();
        await engine.CompactAsync();

        // The pinned pre-compaction segment(s) must still be on disk (snapshot keeps them).
        var afterCompaction = Directory.GetFiles(_testDirectory, "seg_*.sst").Select(Path.GetFileName).ToHashSet();
        preSnapFiles.IsSubsetOf(afterCompaction).Should().BeTrue(
            "the live snapshot pins the pre-compaction segment file(s)");

        // Dispose the engine (synchronous SUT) WITHOUT disposing the snapshot → retired-pinned
        // readers are still parked at teardown time.
        engine.Dispose();

        var afterDispose = Directory.GetFiles(_testDirectory, "seg_*.sst").Select(Path.GetFileName).ToHashSet();
        preSnapFiles.Overlaps(afterDispose).Should().BeFalse(
            "Dispose must release every retired-but-pinned reader (ClearAsync deletes its file + "
            + "closes its handle); leaving the retired segment file on disk means the reader leaked");

        var pointRead = async () => await snapshot.TryGetValueAsync(new SerializableWrapper<int>(1));
        await pointRead.Should().ThrowAsync<ObjectDisposedException>(
            "a retired reader must fail loudly instead of reporting a snapshot-visible key absent");
        var rangeRead = async () =>
        {
            await foreach (var _ in snapshot.GetRangeAsync(new SerializableWrapper<int>(0), new SerializableWrapper<int>(3))) { }
        };
        await rangeRead.Should().ThrowAsync<ObjectDisposedException>();

        await snapshot.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_WithRetiredPinnedReader_ReleasesIt_DeletingTheRetiredSegmentFile()
    {
        // F-WB1-04 + F-WA-C01-01: the async path must perform the same retired-pinned-reader
        // release, awaiting the reader clears rather than blocking.
        var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            Options(_testDirectory, threshold: 1_000_000, memTableCapacity: 2));

        await engine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("a"));
        await engine.SetAsync(new SerializableWrapper<int>(2), new SerializableWrapper<string>("b"));
        await engine.FlushAsync();

        var preSnapFiles = Directory.GetFiles(_testDirectory, "seg_*.sst").Select(Path.GetFileName).ToHashSet();

        var snapshot = await engine.AcquireSnapshotAsync();

        await engine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("c"));
        await engine.SetAsync(new SerializableWrapper<int>(2), new SerializableWrapper<string>("d"));
        await engine.FlushAsync();
        await engine.CompactAsync();

        // IAsyncDisposable surface (F-WB1-04): the concrete engine is consumable via await using.
        (engine as IAsyncDisposable).Should().NotBeNull("LsmStorageEngine implements IAsyncDisposable");

        await engine.DisposeAsync();

        var afterDispose = Directory.GetFiles(_testDirectory, "seg_*.sst").Select(Path.GetFileName).ToHashSet();
        preSnapFiles.Overlaps(afterDispose).Should().BeFalse(
            "DisposeAsync must release every retired-but-pinned reader");

        await snapshot.DisposeAsync();
    }

    // ── F-WB1-05: per-engine (not process-global) GC serialisation ──

    [Fact]
    public async Task CompactValueLog_OnTwoEngines_RunsConcurrently_NotProcessGlobalSerialised()
    {
        // Two engines pointing at SEPARATE directories must be able to run a GC round in
        // parallel. Pre-fix, the static s_gcGate serialised GC across ALL instances. We prove
        // parallelism deterministically by the PEAK number of GC bodies simultaneously inside
        // Phase B across both engines:
        //   • instance gate → both rounds enter Phase B at once → peak occupancy == 2;
        //   • static gate   → the gate serialises them → peak occupancy == 1.
        // This replaces an earlier wall-clock threshold (Elapsed < 1.5× a fixed delay), which was
        // load-fragile: under the parallel test execution of a full-suite run the two rounds could
        // overshoot the threshold even when genuinely concurrent. A barrier (not a timed bound) is
        // what proves concurrency here, so scheduler jitter and machine load cannot turn a real
        // parallel run into a false failure.
        var dirA = Path.Combine(_testDirectory, "engineA");
        var dirB = Path.Combine(_testDirectory, "engineB");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);

        using var engineA = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            Options(dirA, threshold: 64, memTableCapacity: 4));
        using var engineB = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            Options(dirB, threshold: 64, memTableCapacity: 4));

        foreach (var engine in new[] { engineA, engineB })
        {
            for (var i = 0; i < 4; i++)
            {
                await engine.SetAsync(new SerializableWrapper<int>(i), new SerializableWrapper<string>(Payload(128)));
            }
            await engine.FlushAsync();
        }

        var inPhaseB = 0;
        var peakConcurrency = 0;
        var occupancyLock = new object();
        using var bothEntered = new CountdownEvent(2);

        // Each engine's Phase-B hook bumps shared occupancy, then waits at the barrier until BOTH
        // engines have entered Phase B (or a generous timeout fires) before dropping occupancy.
        // With a per-engine gate both rounds reach the barrier → peak == 2. With a process-global
        // gate the second round cannot enter while the first holds the gate, so the first falls
        // through the timeout with peak still 1 — and only then releases the gate for the second.
        // The hook fires once per GC round, so exactly two Signal() calls bring the count to zero.
        void ObservePhaseB()
        {
            lock (occupancyLock)
            {
                inPhaseB++;
                if (inPhaseB > peakConcurrency)
                {
                    peakConcurrency = inPhaseB;
                }
            }

            bothEntered.Signal();
            // 30 s, not 5: under a full-suite parallel run, thread-pool starvation can delay
            // the second engine's Phase-B entry past a short barrier window, turning a
            // genuinely concurrent run into a false peak==1 (observed as a rare full-suite-only
            // flake). The margin does not weaken the discrimination: with a process-global
            // gate the second round can NEVER enter while the first holds the gate, so peak
            // stays 1 no matter how long the barrier waits.
            bothEntered.Wait(TimeSpan.FromSeconds(30));

            lock (occupancyLock)
            {
                inPhaseB--;
            }
        }

        engineA.GcScanPhaseBHook = ObservePhaseB;
        engineB.GcScanPhaseBHook = ObservePhaseB;

        var gcA = Task.Run(async () => await engineA.CompactValueLogAsync());
        var gcB = Task.Run(async () => await engineB.CompactValueLogAsync());
        await Task.WhenAll(gcA, gcB);

        peakConcurrency.Should().Be(2,
            "two engines with separate value-log directories must GC in parallel (per-engine gate); "
            + "a process-global static gate would serialise them so only one GC round is ever in "
            + "Phase B at a time (peak == 1)");
    }

    // ── F-WB1-01 / F-WB1-04: two concurrent CompactValueLogAsync on the SAME engine ──

    [Fact]
    public async Task CompactValueLog_TwoConcurrentCallsOnSameEngine_AreSerialised_NoOverlap_NoThrow()
    {
        // F-WB1-01: the per-engine GC gate was illusory — CompactValueLogAsync constructed a
        // fresh ValueLogGarbageCollector (and thus a fresh per-instance SemaphoreSlim) per call,
        // so two concurrent calls each acquired their OWN gate and both entered the GC body.
        // F-WB1-04: this is the missing concurrent-same-engine GC test.
        //
        // The Phase-B hook fires once per GC round, INSIDE the GC body (after the engine lock is
        // released, before the liveness scan reads any reader). We use it to measure how many GC
        // bodies are simultaneously in flight on this one engine:
        //   • illusory gate → both rounds enter Phase B at once → observed peak concurrency == 2 (RED);
        //   • real per-engine gate → the second call blocks on the engine gate until the first
        //     round finishes → observed peak concurrency == 1 (GREEN).
        using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            Options(_testDirectory, threshold: 64, memTableCapacity: 4, segmentMaxBytes: 200));

        // One fully-shadowed value-log file so a normal round has real reclaim work to do.
        await engine.SetAsync(new SerializableWrapper<int>(0), new SerializableWrapper<string>(Payload(128)));
        await engine.FlushAsync();
        await engine.SetAsync(new SerializableWrapper<int>(0), new SerializableWrapper<string>(Payload(128)));
        await engine.FlushAsync();
        await engine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>(Payload(128)));
        await engine.FlushAsync();
        await engine.CompactAsync();

        var inBody = 0;
        var peakConcurrency = 0;
        var concurrencyLock = new object();

        // Inside the GC body: bump occupancy, hold briefly so a second concurrent round (if the
        // gate is illusory) has time to also enter and be observed, then drop occupancy. With a
        // real gate the second round never reaches this hook until the first has left it.
        engine.GcScanPhaseBHook = () =>
        {
            lock (concurrencyLock)
            {
                inBody++;
                if (inBody > peakConcurrency)
                {
                    peakConcurrency = inBody;
                }
            }

            Thread.Sleep(TimeSpan.FromMilliseconds(300));

            lock (concurrencyLock)
            {
                inBody--;
            }
        };

        var c1 = Task.Run(async () => await engine.CompactValueLogAsync());
        var c2 = Task.Run(async () => await engine.CompactValueLogAsync());

        // No InvalidOperationException ("no live entry for fileIndex=…") from a double-reclaim,
        // and no NotSupportedException from unserialised manifest-stream appends.
        var act = async () => await Task.WhenAll(c1, c2);
        await act.Should().NotThrowAsync(
            "two concurrent GC rounds on the same engine must be serialised, not race the manifest");

        peakConcurrency.Should().Be(1,
            "a real per-engine GC gate must let only one GC round occupy the engine at a time; "
            + "an illusory per-instance gate lets both rounds enter the GC body concurrently (peak == 2)");
    }
}
