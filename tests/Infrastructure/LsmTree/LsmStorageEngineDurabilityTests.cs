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
namespace Boutquin.Storage.Infrastructure.Tests.LsmTree;

/// <summary>
/// Phase A durability tests for <see cref="LsmStorageEngine{TKey, TValue}"/>:
/// <c>CURRENT</c> manifest presence after segment-set changes, orphan-file
/// reconciliation on recovery, and three-phase compaction invariants.
/// </summary>
public sealed class LsmStorageEngineDurabilityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"LsmDurability_{Guid.NewGuid():N}");

    public LsmStorageEngineDurabilityTests()
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

    private LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>> NewEngine(int memTableCapacity = 3)
    {
        return LsmEngineFactory.Build<SerializableWrapper<int>, SerializableWrapper<string>>(memTableCapacity: memTableCapacity,
            segmentFolder: _dir,
            segmentPrefix: "seg");
    }

    private static SerializableWrapper<int> K(int i) => new(i);
    private static SerializableWrapper<string> V(string s) => new(s);

    [Fact]
    public async Task AckedWriteInFreshActive_SurvivesReopen_AfterBackgroundFlushTruncatesWal()
    {
        // DDIA 2e Ch. 4: "Every time the memtable gets written out to an SSTable, the
        // corresponding part of the log can be discarded" — the CORRESPONDING part, not
        // the whole log. Keys 1..3 fill the active (capacity 3); key 4's WAL frame is
        // appended BEFORE the seal that its own write triggers, but its data lands in the
        // fresh active. When the background flush of the sealed slot drains the queue, the
        // WAL truncation must not discard key 4's frame — it is the only durable copy.
        var walDir = Path.Combine(_dir, "wal");

        {
            using var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(walDir);
            using var engine = LsmEngineFactory.Build<SerializableWrapper<int>, SerializableWrapper<string>>(
                memTableCapacity: 3, segmentFolder: _dir, segmentPrefix: "seg", writeAheadLog: wal);

            await engine.SetAsync(K(1), V("v1"));
            await engine.SetAsync(K(2), V("v2"));
            await engine.SetAsync(K(3), V("v3"));
            await engine.SetAsync(K(4), V("v4")); // triggers the seal; lands in the fresh active

            // Wait for the background flush of the sealed slot (keys 1..3) to commit.
            // The flush-side WAL truncation completes inside the same commit lock hold,
            // so once SegmentCount is visible the truncation has already run.
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (engine.SegmentCount == 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(25);
            }
            engine.SegmentCount.Should().Be(1, "the sealed slot (keys 1..3) must background-flush to one segment");

            // Dispose WITHOUT FlushAsync — simulates a crash. Key 4 exists only in the
            // fresh active memtable + (it must!) the WAL.
        }

        using var wal2 = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(walDir);
        using var reopened = await LsmEngineFactory.BuildAsync<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 3, segmentFolder: _dir, segmentPrefix: "seg", writeAheadLog: wal2);

        for (var i = 1; i <= 3; i++)
        {
            var (v, found) = await reopened.TryGetValueAsync(K(i));
            found.Should().BeTrue($"key {i} was flushed to a segment before the crash");
            v.Value.Should().Be($"v{i}");
        }

        var (v4, found4) = await reopened.TryGetValueAsync(K(4));
        found4.Should().BeTrue(
            "key 4 was acknowledged with a fsync'd WAL frame; background flush of the sealed slot " +
            "must not truncate the WAL segment that still backs the unflushed fresh active");
        v4.Value.Should().Be("v4");
    }

    [Fact]
    public async Task FlushWritesManifest_ListingTheNewSegment()
    {
        // Writes 1+2 fill the active; write 3 triggers a seal of the original active and
        // lands in a fresh active. The active+immutable rotation drains the sealed slot
        // asynchronously; FlushAsync drains the queue (and seals the now-non-empty active)
        // so the manifest is on disk before the using block disposes.
        using (var engine = NewEngine(memTableCapacity: 2))
        {
            await engine.SetAsync(K(1), V("a"));
            await engine.SetAsync(K(2), V("b"));
            await engine.SetAsync(K(3), V("c"));
            await engine.FlushAsync();
        }

        SegmentManifest.Exists(_dir).Should().BeTrue("flush is the durable commit point and must rewrite CURRENT");
        var manifest = SegmentManifest.Read(_dir);
        manifest.Should().NotBeNull();
        manifest!.Should().NotBeEmpty("at least one segment must exist after FlushAsync drains the queue");
        manifest![0].Should().StartWith("seg_").And.EndWith(".sst");
    }

    [Fact]
    public async Task RecoveryFromManifest_DeletesOrphanSegmentFiles()
    {
        // Arrange: create a segment via a real flush, then fabricate an orphan segment file
        // that is NOT listed in the manifest — simulating a crash between manifest-write and
        // old-segment-delete during compaction.
        using (var engine = NewEngine(memTableCapacity: 2))
        {
            await engine.SetAsync(K(1), V("a"));
            await engine.SetAsync(K(2), V("b"));
            await engine.SetAsync(K(3), V("c"));
            await engine.FlushAsync(); // drain queue + seal active so manifest is on disk before dispose
        }

        var orphanPath = Path.Combine(_dir, "seg_999999.sst");
        File.WriteAllBytes(orphanPath, [0xAB, 0xCD]);
        File.Exists(orphanPath).Should().BeTrue();

        // Act: re-open engine — orphan reconciliation runs in the constructor.
        using (var engine2 = NewEngine(memTableCapacity: 2))
        {
            engine2.SegmentCount.Should().BeGreaterThan(0, "manifest-listed segments should be loaded");
            var (v, found) = await engine2.TryGetValueAsync(K(1));
            found.Should().BeTrue();
            v.Value.Should().Be("a");
        }

        // Assert: the orphan file is gone; the manifest-listed segment remains.
        File.Exists(orphanPath).Should().BeFalse("orphan-reconciliation should have deleted the unlisted segment");
        var manifestEntries = SegmentManifest.Read(_dir)!;
        foreach (var listed in manifestEntries)
        {
            File.Exists(Path.Combine(_dir, listed)).Should().BeTrue("every manifest-listed segment must exist");
        }
    }

    [Fact]
    public async Task RecoveryWithoutManifest_FailsClosed()
    {
        // Arrange: write segments, then delete the manifest to simulate a corrupted state
        // where segment files exist but CURRENT is lost. Under v2.0.0 greenfield the engine
        // fails closed — no directory-scan fallback, because no well-behaved write path can
        // produce this state (the first flush writes the manifest before returning).
        using (var engine = NewEngine(memTableCapacity: 2))
        {
            await engine.SetAsync(K(1), V("a"));
            await engine.SetAsync(K(2), V("b"));
            await engine.SetAsync(K(3), V("c"));
            await engine.FlushAsync(); // produce a real segment file before manifest deletion
        }

        File.Delete(Path.Combine(_dir, SegmentManifest.FileName));
        SegmentManifest.Exists(_dir).Should().BeFalse();

        // Act + Assert: re-opening the engine must throw InvalidDataException so operators
        // notice a corrupted state instead of silently resurrecting orphan segments.
        var reopen = () => NewEngine(memTableCapacity: 2);
        reopen.Should().Throw<InvalidDataException>()
            .WithMessage("*contains*segment file*but no CURRENT manifest*");
    }

    [Fact]
    public async Task CompactionProducesManifestListingOnlyNewSegment()
    {
        // Drive two flushes, then force a compaction via CompactAsync. Three-phase commit
        // requires the manifest to list exactly the new compacted segment after compaction.
        using var engine = NewEngine(memTableCapacity: 2);
        await engine.SetAsync(K(1), V("a"));
        await engine.SetAsync(K(2), V("b"));
        await engine.SetAsync(K(3), V("c"));
        await engine.SetAsync(K(4), V("d"));
        await engine.SetAsync(K(5), V("e"));
        await engine.SetAsync(K(6), V("f"));
        await engine.FlushAsync(); // drain rotation queue → multiple segments on disk

        engine.SegmentCount.Should().BeGreaterThanOrEqualTo(2, "rotation drains every queued + the active into segments");

        await engine.CompactAsync();

        engine.SegmentCount.Should().Be(1, "three-phase commit replaces all old segments with one compacted segment");
        var manifest = SegmentManifest.Read(_dir);
        manifest.Should().NotBeNull();
        manifest!.Should().ContainSingle("manifest is the durable commit point and must list only the compacted segment");
    }

    [Fact]
    public async Task ClearEmptiesManifest()
    {
        using var engine = NewEngine(memTableCapacity: 2);
        await engine.SetAsync(K(1), V("a"));
        await engine.SetAsync(K(2), V("b"));
        await engine.SetAsync(K(3), V("c"));
        await engine.FlushAsync(); // drain queue → manifest written

        SegmentManifest.Exists(_dir).Should().BeTrue();

        await engine.ClearAsync();

        (SegmentManifest.ReadEntries(_dir) ?? []).Should().BeEmpty(
            "Clear empties the segment set; a manifest still naming the cleared segments would mis-point the next start");
    }

    [Fact]
    public async Task ClearAsync_FsyncsSegmentDirectory_AfterDeletingSegmentsAndEmptyingManifest()
    {
        // The segment-folder directory fsync is a POSIX-only durability step (NTFS directory
        // operations are durable on their own), as in every other directory-fsync site.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var engine = NewEngine(memTableCapacity: 2);
        await engine.SetAsync(K(1), V("a"));
        await engine.SetAsync(K(2), V("b"));
        await engine.SetAsync(K(3), V("c"));
        await engine.FlushAsync();
        Directory.GetFiles(_dir, "seg_*.sst").Should().NotBeEmpty();

        // Write + search without read: entries in the folder can still be deleted, but the
        // folder itself can no longer be opened — so a directory fsync is the one step that
        // fails. A clear that never fsyncs the folder completes "successfully" here, leaving
        // its deletions in the page cache where a crash can resurrect any mix of them.
        var originalMode = File.GetUnixFileMode(_dir);
        File.SetUnixFileMode(_dir, UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var clear = () => engine.ClearAsync();
            await clear.Should().ThrowAsync<IOException>(
                    "ClearAsync must make its segment deletions and emptied CURRENT durable with a directory fsync "
                    + "before it reports success")
                .WithMessage("*fsync*");
        }
        finally
        {
            File.SetUnixFileMode(_dir, originalMode);
        }

        (SegmentManifest.ReadEntries(_dir) ?? []).Should().BeEmpty("the fsync follows emptying CURRENT");
        Directory.GetFiles(_dir, "seg_*.sst").Should().BeEmpty("the fsync follows the segment deletes");
    }

    /// <summary>
    /// Segment format that runs a test callback after each segment file is fully written —
    /// i.e. inside the flusher's write-to-commit window, outside the engine lock.
    /// </summary>
    private sealed class AfterWriteHookSegmentFormat : ISegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>>
    {
        private readonly SstSegmentFormat<SerializableWrapper<int>, SerializableWrapper<string>> _inner = new();

        public Func<CancellationToken, Task>? AfterWrite { get; set; }

        public string FileExtension => _inner.FileExtension;

        public bool SupportsSnapshots => _inner.SupportsSnapshots;

        public async Task<ISegmentReader<SerializableWrapper<int>, SerializableWrapper<string>>> WriteAsync(
            string segmentFolder,
            string segmentFileName,
            IReadOnlyList<SegmentWriteEntry<SerializableWrapper<int>, SerializableWrapper<string>>> sortedEntries,
            IReadOnlyDictionary<SerializableWrapper<int>, ulong>? tombstones = null,
            CancellationToken cancellationToken = default)
        {
            var reader = await _inner.WriteAsync(segmentFolder, segmentFileName, sortedEntries, tombstones, cancellationToken)
                .ConfigureAwait(true);
            if (AfterWrite is { } afterWrite)
            {
                await afterWrite(cancellationToken).ConfigureAwait(true);
            }

            return reader;
        }

        public ISegmentReader<SerializableWrapper<int>, SerializableWrapper<string>> Open(
            string segmentFolder, string segmentFileName)
            => _inner.Open(segmentFolder, segmentFileName);
    }

    private void AssertEverySegmentFileIsListed(string because)
    {
        var listed = (SegmentManifest.ReadEntries(_dir) ?? []).Select(e => e.FileName).ToHashSet(StringComparer.Ordinal);
        Directory.GetFiles(_dir, "seg_*.sst").Select(Path.GetFileName).Should().OnlyContain(f => listed.Contains(f!), because);
    }

    [Fact]
    public async Task Dispose_ObservedAtFirstFlushCommit_LeavesDirectoryReopenable()
    {
        var walDir = Path.Combine(_dir, "wal");
        var commitLockAcquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposalObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using (var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(walDir))
        {
            var engine = LsmEngineFactory.Build<SerializableWrapper<int>, SerializableWrapper<string>>(
                memTableCapacity: 1, segmentFolder: _dir, segmentPrefix: "seg", writeAheadLog: wal);

            // Park the flusher's first commit (segment already written, manifest not yet) until
            // disposal has been requested, so the commit observes the disposed engine.
            engine.FlusherCommitLockHook = async cancellationToken =>
            {
                using var registration = cancellationToken.Register(() => disposalObserved.TrySetResult());
                commitLockAcquired.TrySetResult();
                await releaseCommit.Task.ConfigureAwait(true);
            };

            await engine.SetAsync(K(1), V("one"));
            await engine.SetAsync(K(2), V("two"));
            await commitLockAcquired.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var disposeTask = Task.Run(engine.Dispose);
            await disposalObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));
            releaseCommit.TrySetResult();
            await disposeTask.WaitAsync(TimeSpan.FromSeconds(10));
        }

        AssertEverySegmentFileIsListed(
            "a flush abandoned by disposal never reached the manifest; its segment file must not survive as an "
            + "orphan, because segment files with no CURRENT fail the next open closed");

        using var wal2 = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(walDir);
        using var reopened = await LsmEngineFactory.BuildAsync<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 1, segmentFolder: _dir, segmentPrefix: "seg", writeAheadLog: wal2);
        (await reopened.TryGetValueAsync(K(1))).Found.Should().BeTrue("key 1 is durable in the WAL");
        (await reopened.TryGetValueAsync(K(2))).Found.Should().BeTrue("key 2 is durable in the WAL");
    }

    [Fact]
    public async Task Dispose_WhileFirstFlushWaitsForCommitLock_LeavesDirectoryReopenable()
    {
        var walDir = Path.Combine(_dir, "wal");
        var format = new AfterWriteHookSegmentFormat();
        var lockTaken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using (var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(walDir))
        {
            var engine = LsmEngineFactory.Build<SerializableWrapper<int>, SerializableWrapper<string>>(
                memTableCapacity: 1, segmentFolder: _dir, segmentPrefix: "seg", segmentFormat: format, writeAheadLog: wal);
            var host = (Boutquin.Storage.Infrastructure.ValueLog.IValueLogGcHost)engine;

            // Another holder takes the engine lock once the segment file is written, so the
            // flusher parks on its commit-lock wait — where disposal's cancellation lands.
            var fired = 0;
            format.AfterWrite = async _ =>
            {
                if (Interlocked.Exchange(ref fired, 1) == 0)
                {
                    await host.EnterLockForGcAsync(CancellationToken.None).ConfigureAwait(true);
                    lockTaken.TrySetResult();
                }
            };

            await engine.SetAsync(K(1), V("one"));
            await engine.SetAsync(K(2), V("two"));
            await lockTaken.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var flusherLoop = (Task)typeof(LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>)
                .GetField("_flusherLoop", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(engine)!;
            var disposeTask = Task.Run(engine.Dispose);

            // The flusher must exit on the cancelled commit-lock wait while the lock is still
            // held; only then is the lock handed back so disposal can finish.
            await flusherLoop.WaitAsync(TimeSpan.FromSeconds(10));
            host.ExitLockForGc();
            await disposeTask.WaitAsync(TimeSpan.FromSeconds(10));
        }

        AssertEverySegmentFileIsListed(
            "a flush cancelled on its commit-lock wait never reached the manifest; its segment file must not "
            + "survive as an orphan, because segment files with no CURRENT fail the next open closed");

        using var wal2 = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(walDir);
        using var reopened = await LsmEngineFactory.BuildAsync<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 1, segmentFolder: _dir, segmentPrefix: "seg", writeAheadLog: wal2);
        (await reopened.TryGetValueAsync(K(1))).Found.Should().BeTrue("key 1 is durable in the WAL");
        (await reopened.TryGetValueAsync(K(2))).Found.Should().BeTrue("key 2 is durable in the WAL");
    }

    [Fact]
    public async Task RoundTrip_AfterFlushAndRestart_DataReadable()
    {
        using (var engine = NewEngine(memTableCapacity: 2))
        {
            await engine.SetAsync(K(1), V("hello"));
            await engine.SetAsync(K(2), V("world"));
            await engine.SetAsync(K(3), V("!"));
            await engine.FlushAsync(); // ensure all three keys are durable before dispose
        }

        using var reopen = NewEngine(memTableCapacity: 2);
        var (a, foundA) = await reopen.TryGetValueAsync(K(1));
        var (b, foundB) = await reopen.TryGetValueAsync(K(2));
        var (c, foundC) = await reopen.TryGetValueAsync(K(3));

        foundA.Should().BeTrue(); a.Value.Should().Be("hello");
        foundB.Should().BeTrue(); b.Value.Should().Be("world");
        foundC.Should().BeTrue(); c.Value.Should().Be("!");
    }
}
