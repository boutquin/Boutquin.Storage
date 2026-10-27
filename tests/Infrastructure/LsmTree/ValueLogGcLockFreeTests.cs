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
/// P4.8 — TDD tests for the lock-free segment-scan refactor of
/// <c>CollectLiveValueLogPointersUnderLock</c>.
/// Verifies: (a) GC still collects correct live pointers after the snapshot-then-scan
/// refactor; (b) a concurrent write can proceed while GC is scanning segments (no
/// engine-lock held during the scan phase).
/// </summary>
public sealed class ValueLogGcLockFreeTests : IDisposable
{
    private readonly string _testDirectory =
        Path.Combine(Path.GetTempPath(), $"ValueLogGcLockFree_{Guid.NewGuid():N}");

    public ValueLogGcLockFreeTests() => Directory.CreateDirectory(_testDirectory);

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            try { Directory.Delete(_testDirectory, recursive: true); }
            catch (IOException) { /* test-runner cleanup — ignore */ }
        }
    }

    private static LsmStorageEngineOptions<SerializableWrapper<int>, SerializableWrapper<string>> Options(
        string folder, int threshold = 128, int memTableCapacity = 4)
        => new(
            MemTableCapacity: memTableCapacity,
            SegmentFolder: folder,
            SegmentPrefix: "seg",
            SegmentFormat: new SstSegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>>(),
            CompactionStrategy: null,
            ValueLog: new ValueLogOptions(ValueLogThresholdBytes: threshold));

    private static string Payload(int size) => new('x', size);

    // ── Correctness: refactored path still finds live pointers ────────────────

    [Fact]
    public async Task Gc_AfterRefactor_SkipsCurrentWriterFile()
    {
        // All writes go to the single current append-target file; GC must not reclaim it.
        using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            Options(_testDirectory, threshold: 128, memTableCapacity: 10));

        for (var i = 0; i < 5; i++)
        {
            await engine.SetAsync(new SerializableWrapper<int>(i), new SerializableWrapper<string>(Payload(200)));
        }
        await engine.FlushAsync();

        var result = await engine.CompactValueLogAsync();

        result.FilesReclaimed.Should().Be(0, "current append-target file is exempt from GC");
    }

    [Fact]
    public async Task Gc_AfterRefactor_CorrectlyCollectsPointers_FromFlushedSegments()
    {
        // Write + flush two MemTable batches to generate two segment files.
        // GC must find live pointers across both segments.
        using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            Options(_testDirectory, threshold: 64, memTableCapacity: 4));

        // Batch 1 — all keys 0-3 with large values (go to value log)
        for (var i = 0; i < 4; i++)
        {
            await engine.SetAsync(new SerializableWrapper<int>(i), new SerializableWrapper<string>(Payload(128)));
        }
        await engine.FlushAsync();

        // Batch 2 — all keys 4-7
        for (var i = 4; i < 8; i++)
        {
            await engine.SetAsync(new SerializableWrapper<int>(i), new SerializableWrapper<string>(Payload(128)));
        }
        await engine.FlushAsync();

        var result = await engine.CompactValueLogAsync();

        // The value-log file used by both batches is still the current append-target
        // (or a referenced file). No pointers should be stale yet — all keys live.
        result.FilesReclaimed.Should().Be(0, "all keys are live; no file is fully shadowed");

        // All keys still read back correctly after GC.
        for (var i = 0; i < 8; i++)
        {
            var (val, found) = await engine.TryGetValueAsync(new SerializableWrapper<int>(i));
            found.Should().BeTrue($"key {i} should still be readable after GC");
            val.Value.Length.Should().Be(128);
        }
    }

    [Fact]
    public async Task Gc_AfterRefactor_RespectsSnapshotPins()
    {
        // Snapshots pin segments; GC must not reclaim files that snapshot-visible entries
        // reference — even after the snapshot-then-scan lock-free refactor.
        using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            Options(_testDirectory, threshold: 64, memTableCapacity: 4));

        for (var i = 0; i < 4; i++)
        {
            await engine.SetAsync(new SerializableWrapper<int>(i), new SerializableWrapper<string>(Payload(128)));
        }
        await engine.FlushAsync();

        var snapshot = await engine.AcquireSnapshotAsync();
        try
        {
            // Overwrite same keys — old value-log file stays referenced by the snapshot.
            for (var i = 0; i < 4; i++)
            {
                await engine.SetAsync(new SerializableWrapper<int>(i), new SerializableWrapper<string>(Payload(200)));
            }
            await engine.FlushAsync();

            var result = await engine.CompactValueLogAsync();

            result.FilesReclaimed.Should().Be(0, "snapshot pins the pre-overwrite value-log file");

            var (v0, f0) = await snapshot.TryGetValueAsync(new SerializableWrapper<int>(0));
            f0.Should().BeTrue();
            v0.Value.Length.Should().Be(128, "snapshot still sees the original value");
        }
        finally
        {
            await snapshot.DisposeAsync();
        }
    }

    // ── Skipped vs reclaimed file coverage ───────────────────────────────────

    private static LsmStorageEngineOptions<SerializableWrapper<int>, SerializableWrapper<string>> SmallRotatingOptions(
        string folder, int memTableCapacity = 4)
        => new(
            MemTableCapacity: memTableCapacity,
            SegmentFolder: folder,
            SegmentPrefix: "seg",
            SegmentFormat: new SstSegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>>(),
            CompactionStrategy: null,
            // ValueLogThresholdBytes: 64 → values of ≥64 bytes go to value log.
            // SegmentMaxBytes: 200 → each entry is ~140 bytes, so the log rotates after 1 entry,
            // creating distinct closed files that the GC can process.
            ValueLog: new ValueLogOptions(ValueLogThresholdBytes: 64, SegmentMaxBytes: 200));

    [Fact]
    public async Task Gc_FilesWithLivePointers_AreCountedAsSkipped()
    {
        // 2 large-value keys → go to file 1 (rotates after 1 entry).
        // Keys overwritten → new pointers go to a later file.
        // File 1 still referenced by key 1 in segment S1, so GC must skip it.
        using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            SmallRotatingOptions(_testDirectory, memTableCapacity: 4));

        // Write keys 0 and 1 — each goes to its own log file due to tiny SegmentMaxBytes.
        await engine.SetAsync(new SerializableWrapper<int>(0), new SerializableWrapper<string>(Payload(128)));
        await engine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>(Payload(128)));
        await engine.FlushAsync(); // S1: keys 0 (file 1) and 1 (file 2)

        // Overwrite only key 0 → new pointer in file 3; key 1 still in file 2.
        await engine.SetAsync(new SerializableWrapper<int>(0), new SerializableWrapper<string>(Payload(128)));
        await engine.FlushAsync(); // S2: key 0 now in file 3

        // Advance the writer to close file 3 by writing one more value.
        await engine.SetAsync(new SerializableWrapper<int>(99), new SerializableWrapper<string>(Payload(128)));
        await engine.FlushAsync();

        var result = await engine.CompactValueLogAsync();

        result.FilesSkipped.Should().BeGreaterThanOrEqualTo(1,
            "file 2 has a live pointer for key 1 and must not be reclaimed");
    }

    [Fact]
    public async Task Gc_FullyShadowedFile_IsReclaimed()
    {
        // Key 0 written to file 1 (closes after one entry due to tiny SegmentMaxBytes).
        // Key 0 overwritten → new pointer in file 2.
        // After SSTable compaction merges S1 into S2, S1 is deleted. File 1 then has zero
        // live pointers in any remaining segment → GC reclaims it.
        using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            SmallRotatingOptions(_testDirectory, memTableCapacity: 4));

        // Key 0 → file 1. Rotation fires before the NEXT write because file 1 size + next
        // entry length > SegmentMaxBytes.
        await engine.SetAsync(new SerializableWrapper<int>(0), new SerializableWrapper<string>(Payload(128)));
        await engine.FlushAsync(); // S1: key 0 → file 1

        // Overwrite key 0 → rotation fires → key 0 lands in file 2.
        await engine.SetAsync(new SerializableWrapper<int>(0), new SerializableWrapper<string>(Payload(128)));
        await engine.FlushAsync(); // S2: key 0 → file 2

        // Advance the writer past file 2 to close it, so GC can see file 1 as closed.
        await engine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>(Payload(128)));
        await engine.FlushAsync(); // S3: key 1 → file 3

        // LSM compaction merges S1+S2+S3 into one merged segment, deleting S1.
        // After compaction, no live segment references file 1 (key 0 now points at file 2
        // in the merged segment), so GC can reclaim file 1.
        await engine.CompactAsync();

        // Lock the compaction precondition: a single merged segment must exist before GC
        // runs. If a future strategy regression left segments un-merged, every value-log
        // file would still be referenced and the GC reclaim would silently degrade.
        engine.SegmentCount.Should().Be(1, "force-full compaction should produce exactly one segment");

        // Capture the on-disk file paths BEFORE GC runs so we can verify *which* file was
        // reclaimed — not just that a count went up. A regression where GC reclaimed the
        // wrong file (e.g. F1 instead of F0) would still satisfy a count-only assertion.
        var valueLogDir = Path.Combine(_testDirectory, "valuelog");
        var f0 = Path.Combine(valueLogDir, "valuelog_000000.log");
        var f1 = Path.Combine(valueLogDir, "valuelog_000001.log");
        var f2 = Path.Combine(valueLogDir, "valuelog_000002.log");
        File.Exists(f0).Should().BeTrue("precondition: F0 exists pre-GC");
        File.Exists(f1).Should().BeTrue("precondition: F1 exists pre-GC");
        File.Exists(f2).Should().BeTrue("precondition: F2 exists pre-GC");

        var result = await engine.CompactValueLogAsync();

        result.FilesReclaimed.Should().Be(1,
            "exactly one fully-shadowed file (F0) is reclaimable; F1 carries the live key-0 "
            + "pointer, F2 is the writer's current append-target");
        result.FilesSkipped.Should().Be(1, "F1 has a live pointer and must be skipped");
        result.BytesReclaimed.Should().BeGreaterThan(0);

        // Lock down *which* file was reclaimed. A KWayMerge regression that picked the older
        // duplicate (S1 over S2 for key 0) would leave F1 unreferenced and reclaim it instead
        // of F0 — the count-only assertions above would still pass.
        File.Exists(f0).Should().BeFalse("F0 was fully shadowed → reclaimed");
        File.Exists(f1).Should().BeTrue("F1 carries the live key-0 pointer → must be retained");
        File.Exists(f2).Should().BeTrue("F2 is the current append-target → never a candidate");
    }

    [Fact]
    public async Task Gc_FullyShadowedFile_KWayMergeKeepsNewerDuplicate()
    {
        // Sibling to Gc_FullyShadowedFile_IsReclaimed. Locks down the KWayMerge tie-break
        // rule the GC test silently relies on: when the same key appears in multiple picked
        // segments, the entry from the highest source index (the newest segment) wins. A
        // regression that flipped the comparison would leave F0 referenced by the merged
        // segment, F1 unreferenced, and the GC count would still equal 1 — but the wrong
        // file. Distinct payloads make the version observable through TryGetValueAsync.
        using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            SmallRotatingOptions(_testDirectory, memTableCapacity: 4));

        var olderValue = new string('a', 128);
        var newerValue = new string('b', 128);

        await engine.SetAsync(new SerializableWrapper<int>(0), new SerializableWrapper<string>(olderValue));
        await engine.FlushAsync(); // S1: key 0 → F0 (old payload 'a')

        await engine.SetAsync(new SerializableWrapper<int>(0), new SerializableWrapper<string>(newerValue));
        await engine.FlushAsync(); // S2: key 0 → F1 (new payload 'b')

        await engine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>(Payload(128)));
        await engine.FlushAsync(); // S3: key 1 → F2 (closes F1 as a non-current file)

        await engine.CompactAsync();
        engine.SegmentCount.Should().Be(1, "force-full compaction should produce exactly one segment");

        // After the merge, key 0 must resolve to the *newer* value. If KWayMerge inverted,
        // 'a' would survive — which would also flip which file is reclaimable.
        var (val, found) = await engine.TryGetValueAsync(new SerializableWrapper<int>(0));
        found.Should().BeTrue();
        val.Value.Should().Be(newerValue, "KWayMerge must keep the entry from the higher source index (newer segment)");

        // The reclamation outcome is the dual of the tie-break: F0 (older) reclaimable, F1
        // (newer) retained. Run GC to make the contract end-to-end visible.
        var result = await engine.CompactValueLogAsync();
        result.FilesReclaimed.Should().Be(1);
        var valueLogDir = Path.Combine(_testDirectory, "valuelog");
        File.Exists(Path.Combine(valueLogDir, "valuelog_000000.log")).Should().BeFalse(
            "F0 holds the older 'a' payload that lost the merge → reclaimable");
        File.Exists(Path.Combine(valueLogDir, "valuelog_000001.log")).Should().BeTrue(
            "F1 holds the newer 'b' payload that won the merge → retained");
    }

    // ── Concurrency: write must not block while GC scans ─────────────────────

    [Fact]
    public async Task Gc_DoesNotBlockConcurrentWrite()
    {
        // Start GC and a concurrent write simultaneously. Both must complete within a
        // reasonable wall-clock budget — a deadlock would cause the test to time out.
        using var engine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            Options(_testDirectory, threshold: 64, memTableCapacity: 4));

        for (var i = 0; i < 4; i++)
        {
            await engine.SetAsync(new SerializableWrapper<int>(i), new SerializableWrapper<string>(Payload(128)));
        }
        await engine.FlushAsync();

        // Fire both operations concurrently.
        var gcTask = engine.CompactValueLogAsync();
        var writeTask = engine.SetAsync(new SerializableWrapper<int>(99), new SerializableWrapper<string>(Payload(128)));

        // 5-second timeout — a deadlock would cause the test runner to time out cleanly.
        await Task.WhenAll(gcTask, writeTask).WaitAsync(TimeSpan.FromSeconds(5));

        var (val, found) = await engine.TryGetValueAsync(new SerializableWrapper<int>(99));
        found.Should().BeTrue("concurrent write should have completed");
        val.Value.Length.Should().Be(128);
    }
}
