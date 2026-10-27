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
namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// Pins the segment-identity contract of <see cref="LsmStorageEngine{TKey, TValue}.ClearAsync"/>:
/// a clear never makes a segment filename available for reuse while anything that captured the
/// pre-clear name — a live snapshot's pin, a retired-pinned reader, an in-flight compaction
/// plan — can still act on it. Each test flushes AFTER the clear, the step that would hand a
/// post-clear segment the same pathname as a pre-clear one.
/// </summary>
public sealed class LsmStorageEngineClearSafetyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"LsmClearSafety_{Guid.NewGuid():N}");

    public LsmStorageEngineClearSafetyTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best-effort */ }
        }
    }

    private static SerializableWrapper<int> K(int v) => new(v);
    private static SerializableWrapper<int> V(int v) => new(v);

    private LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<int>> NewEngine(int memTableCapacity)
        => LsmEngineFactory.Build<SerializableWrapper<int>, SerializableWrapper<int>>(
            memTableCapacity: memTableCapacity, segmentFolder: _dir, segmentPrefix: "seg");

    private List<string> ManifestFileNames()
        => (SegmentManifest.ReadEntries(_dir) ?? []).Select(e => e.FileName).ToList();

    [Fact]
    public async Task ReleasingPreClearSnapshot_DoesNotDeletePostClearSegment()
    {
        using var engine = NewEngine(memTableCapacity: 2);
        await engine.SetAsync(K(1), V(1));
        await engine.FlushAsync();

        var snapshot = await engine.AcquireSnapshotAsync();
        await engine.ClearAsync();

        // Acknowledged, flushed post-clear writes.
        await engine.SetAsync(K(10), V(10));
        await engine.SetAsync(K(11), V(11));
        await engine.FlushAsync();

        // Releasing the pre-clear pin reclaims the pre-clear file. It must never reclaim a
        // file the post-clear manifest lists.
        await snapshot.DisposeAsync();

        var listed = ManifestFileNames();
        listed.Should().NotBeEmpty("the post-clear flush wrote a manifest");
        foreach (var fileName in listed)
        {
            File.Exists(Path.Combine(_dir, fileName)).Should().BeTrue(
                $"'{fileName}' holds acknowledged post-clear writes and is listed in CURRENT; releasing a "
                + "snapshot taken before the clear must not delete it");
        }

        var (value, found) = await engine.TryGetValueAsync(K(10));
        found.Should().BeTrue("key 10 was acknowledged and flushed after the clear");
        value.Value.Should().Be(10);
    }

    [Fact]
    public async Task PreClearSnapshot_ReadsOwnValue_AfterPostClearFlush()
    {
        using var engine = NewEngine(memTableCapacity: 2);
        await engine.SetAsync(K(1), V(1));
        await engine.FlushAsync();

        await using var snapshot = await engine.AcquireSnapshotAsync();
        await engine.ClearAsync();

        await engine.SetAsync(K(1), V(999));
        await engine.SetAsync(K(2), V(2));
        await engine.FlushAsync();

        // The snapshot pinned the pre-clear segment; the post-clear segment must not be served
        // through that pin.
        (SerializableWrapper<int> Value, bool Found) read = default;
        var act = async () => read = await snapshot.TryGetValueAsync(K(1));
        await act.Should().NotThrowAsync(
            "the snapshot's pinned pre-clear segment must still be the bytes it pinned, not a post-clear "
            + "segment written under the same name");
        read.Found.Should().BeTrue();
        read.Value!.Value.Should().Be(1, "a snapshot returns its as-of value for its whole lifetime");

        var (_, k2Found) = await snapshot.TryGetValueAsync(K(2));
        k2Found.Should().BeFalse("key 2 was written after the snapshot was acquired");
    }

    [Fact]
    public async Task ClearDuringCompactionMerge_ThenReflush_CommitDoesNotReplaceNewSegments()
    {
        using var engine = NewEngine(memTableCapacity: 100);
        for (var i = 1; i <= 4; i++)
        {
            await engine.SetAsync(K(i), V(i));
        }

        await engine.FlushAsync();
        for (var i = 5; i <= 8; i++)
        {
            await engine.SetAsync(K(i), V(i));
        }

        await engine.FlushAsync();

        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fired = 0;
        engine.CompactionMergeHook = async () =>
        {
            if (Interlocked.Exchange(ref fired, 1) == 0)
            {
                parked.TrySetResult();
                await release.Task.ConfigureAwait(true);
            }
        };

        var compaction = engine.CompactAsync();
        await parked.Task.WaitAsync(TimeSpan.FromSeconds(20));

        await engine.ClearAsync();
        await engine.SetAsync(K(100), V(100));
        await engine.FlushAsync();
        await engine.SetAsync(K(101), V(101));
        await engine.FlushAsync();

        release.TrySetResult();
        await compaction.WaitAsync(TimeSpan.FromSeconds(20));

        var keys = (await engine.GetAllItemsAsync()).Select(kv => kv.Key.Value).OrderBy(k => k).ToList();
        keys.Should().Equal([100, 101],
            "the compaction merged segments the clear removed; its commit must abort rather than swap the "
            + "cleared data in over the acknowledged post-clear segments");
    }

    // Copies the segment folder's current directory entries into a sibling folder — the durable
    // image a process kill would leave at this instant (ClearAsync fsyncs the folder before it
    // returns, so the copied entry set is what survives a crash).
    private string CopyCrashImage()
    {
        var image = Path.Combine(_dir, "crash-image");
        Directory.CreateDirectory(image);
        foreach (var file in Directory.GetFiles(_dir))
        {
            File.Copy(file, Path.Combine(image, Path.GetFileName(file)));
        }

        return image;
    }

    [Fact]
    public async Task ClearAsync_WithLiveSnapshot_CrashImageReopensEmpty()
    {
        using var engine = NewEngine(memTableCapacity: 2);
        await engine.SetAsync(K(1), V(1));
        await engine.FlushAsync();

        await using var snapshot = await engine.AcquireSnapshotAsync();
        await engine.ClearAsync();
        Directory.GetFiles(_dir, "seg_*.sst").Should().NotBeEmpty("the live snapshot still pins the cleared segment");

        // A kill while the pin is live leaves this directory behind; the clear already reported
        // success, so the next open must serve the cleared (empty) store.
        var image = CopyCrashImage();
        LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<int>>? reopened = null;
        var open = async () => reopened = await LsmEngineFactory.BuildAsync<SerializableWrapper<int>, SerializableWrapper<int>>(
            memTableCapacity: 2, segmentFolder: image, segmentPrefix: "seg");
        await open.Should().NotThrowAsync(
            "a successful clear must leave a directory the next open accepts, even while a snapshot pins retired files");
        using (reopened)
        {
            var (_, found) = await reopened!.TryGetValueAsync(K(1));
            found.Should().BeFalse("the clear removed every key before the crash");
        }
    }

    [Fact]
    public async Task ClearAsync_WithLiveSnapshot_CrashImageOpenSweepsPinnedRetirees()
    {
        using var engine = NewEngine(memTableCapacity: 2);
        await engine.SetAsync(K(1), V(1));
        await engine.SetAsync(K(2), V(2));
        await engine.FlushAsync();

        await using var snapshot = await engine.AcquireSnapshotAsync();
        await engine.ClearAsync();

        var image = CopyCrashImage();
        Directory.GetFiles(image, "seg_*.sst").Should().NotBeEmpty("the pinned retiree is part of the crash image");
        try
        {
            using var reopened = await LsmEngineFactory.BuildAsync<SerializableWrapper<int>, SerializableWrapper<int>>(
                memTableCapacity: 2, segmentFolder: image, segmentPrefix: "seg");
        }
        catch (InvalidDataException ex)
        {
            throw new Xunit.Sdk.XunitException(
                "recovery must treat a cleared store's pinned retirees as unlisted orphans, not refuse to open: " + ex.Message);
        }

        (SegmentManifest.ReadEntries(image) ?? []).Should().BeEmpty("the clear left CURRENT naming no segment");
        Directory.GetFiles(image, "seg_*.sst").Should().BeEmpty(
            "a segment file the manifest does not name is never live and is reclaimed by the open");
    }

    [Fact]
    public async Task ClearAsync_CancelledMidSegmentLoop_StillClearsEverySegment_AndStoreReopensEmpty()
    {
        using var cts = new CancellationTokenSource();
        var format = new CancelAfterFirstSegmentClearFormat(cts);
        var engine = LsmEngineFactory.Build<SerializableWrapper<int>, SerializableWrapper<int>>(
            memTableCapacity: 2, segmentFolder: _dir, segmentPrefix: "seg", segmentFormat: format,
            compactionStrategy: new FullCompactionStrategy<SerializableWrapper<int>>(threshold: 100_000));
        try
        {
            for (var i = 1; i <= 8; i++)
            {
                await engine.SetAsync(K(i), V(i));
                await engine.FlushAsync();
            }

            engine.SegmentCount.Should().BeGreaterThan(1, "the workload spans several segments");

            // The first segment delete cancels the caller's token, so every later per-segment
            // delete observes a cancelled token while the clear is already discarding state.
            Exception? clearFailure = null;
            try
            {
                await engine.ClearAsync(cts.Token);
            }
            catch (Exception ex)
            {
                clearFailure = ex;
            }

            format.CancelFired.Should().BeTrue("the cancellation must land inside the segment loop");
            var readFailure = await Record.ExceptionAsync(async () => await engine.TryGetValueAsync(K(1)));
            readFailure.Should().BeNull(
                "a clear that started discarding state must not leave readers of deleted files in the segment set"
                + (clearFailure is null ? string.Empty : $" (clear threw {clearFailure.GetType().Name})"));
            Directory.GetFiles(_dir, "seg_*.sst").Should().BeEmpty("the clear must finish every segment delete");
        }
        finally
        {
            engine.Dispose();
        }

        using var reopened = await LsmEngineFactory.BuildAsync<SerializableWrapper<int>, SerializableWrapper<int>>(
            memTableCapacity: 2, segmentFolder: _dir, segmentPrefix: "seg");
        var (_, found) = await reopened.TryGetValueAsync(K(1));
        found.Should().BeFalse("the clear removed every key");
    }

    /// <summary>
    /// SSTable segment format whose readers cancel <see cref="CancellationTokenSource"/> right
    /// after the first segment file is deleted — a caller cancellation landing deterministically
    /// between two iterations of the clear's per-segment delete loop.
    /// </summary>
    private sealed class CancelAfterFirstSegmentClearFormat(CancellationTokenSource cts)
        : ISegmentFormat<SerializableWrapper<int>, SerializableWrapper<int>>
    {
        private readonly SstSegmentFormat<SerializableWrapper<int>, SerializableWrapper<int>> _inner = new();
        private int _cleared;

        public bool CancelFired => Volatile.Read(ref _cleared) > 0;

        public string FileExtension => _inner.FileExtension;

        public bool SupportsSnapshots => _inner.SupportsSnapshots;

        public async Task<ISegmentReader<SerializableWrapper<int>, SerializableWrapper<int>>> WriteAsync(
            string segmentFolder,
            string segmentFileName,
            IReadOnlyList<SegmentWriteEntry<SerializableWrapper<int>, SerializableWrapper<int>>> sortedEntries,
            IReadOnlyDictionary<SerializableWrapper<int>, ulong>? tombstones = null,
            CancellationToken cancellationToken = default)
            => new Reader(this, await _inner.WriteAsync(segmentFolder, segmentFileName, sortedEntries, tombstones, cancellationToken)
                .ConfigureAwait(true));

        public ISegmentReader<SerializableWrapper<int>, SerializableWrapper<int>> Open(
            string segmentFolder, string segmentFileName)
            => new Reader(this, _inner.Open(segmentFolder, segmentFileName));

        private sealed class Reader(
            CancelAfterFirstSegmentClearFormat owner,
            ISegmentReader<SerializableWrapper<int>, SerializableWrapper<int>> inner)
            : ISegmentReader<SerializableWrapper<int>, SerializableWrapper<int>>
        {
            public Task<(BlockEntry Entry, bool Found)> TryGetBlockEntryAsync(
                SerializableWrapper<int> key, ulong maxSequenceNumber = ulong.MaxValue, CancellationToken cancellationToken = default)
                => inner.TryGetBlockEntryAsync(key, maxSequenceNumber, cancellationToken);

            public IAsyncEnumerable<SegmentWriteEntry<SerializableWrapper<int>, SerializableWrapper<int>>> StreamBlockEntriesAsync(
                ulong maxSequenceNumber = ulong.MaxValue,
                ScanDirection direction = ScanDirection.Ascending,
                CancellationToken cancellationToken = default)
                => inner.StreamBlockEntriesAsync(maxSequenceNumber, direction, cancellationToken);

            public async Task ClearAsync(CancellationToken cancellationToken = default)
            {
                await inner.ClearAsync(cancellationToken).ConfigureAwait(true);
                if (Interlocked.Increment(ref owner._cleared) == 1)
                {
                    await owner.CancelAsync().ConfigureAwait(true);
                }
            }

            public ulong MaxSequenceNumber => inner.MaxSequenceNumber;

            public bool TryGetRange(out SerializableWrapper<int> min, out SerializableWrapper<int> max)
                => inner.TryGetRange(out min, out max);

            public IReadOnlyDictionary<SerializableWrapper<int>, ulong> GetTombstoneSeqnos() => inner.GetTombstoneSeqnos();

            public Task<(SerializableWrapper<int> Value, ulong SequenceNumber, bool Found)> TryGetValueWithSeqnoAsync(
                SerializableWrapper<int> key, ulong maxSequenceNumber, CancellationToken cancellationToken = default)
                => inner.TryGetValueWithSeqnoAsync(key, maxSequenceNumber, cancellationToken);
        }

        private Task CancelAsync() => cts.CancelAsync();
    }

    /// <summary>
    /// A clear on a WAL-backed engine empties CURRENT and truncates the WAL — two files, so two
    /// durable steps. A crash between them must reopen to the pre-clear or the post-clear state,
    /// never the flushed data gone while the WAL's unflushed writes replay back.
    /// </summary>
    [Fact]
    public async Task CrashBetweenEmptyingCurrentAndTruncatingWal_ReopensFullyClearedOrFullyIntact()
    {
        var live = Path.Combine(_dir, "live");
        var image = Path.Combine(_dir, "image");
        Directory.CreateDirectory(live);

        // 300 KiB values against the 1 MiB minimum WAL segment rotate the WAL every few frames, so
        // the flushes' watermark truncation discards the flushed frames and the WAL ends up holding
        // only the unflushed write.
        var walOptions = new WriteAheadLogOptions { SegmentMaxBytes = 1024 * 1024 };
        var padding = new string('v', 300 * 1024);
        var wal = new CrashImageAtTruncateWal(
            new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(Path.Combine(live, "wal"), walOptions),
            live,
            image);
        var engine = LsmEngineFactory.Build(
            memTableCapacity: 2, segmentFolder: Path.Combine(live, "seg"), segmentPrefix: "seg", writeAheadLog: wal);
        try
        {
            for (var i = 1; i <= 8; i++)
            {
                await engine.SetAsync(K(i), new SerializableWrapper<string>(padding));
            }

            await engine.FlushAsync();
            await engine.SetAsync(K(9), new SerializableWrapper<string>("unflushed"));
            await engine.ClearAsync();
            wal.Captured.Should().BeTrue("the clear truncates the WAL, the instant the crash image records");
        }
        finally
        {
            engine.Dispose();
        }

        using var reopenedWal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(
            Path.Combine(image, "wal"), walOptions);
        using var reopened = await LsmEngineFactory.BuildAsync(
            memTableCapacity: 2, segmentFolder: Path.Combine(image, "seg"), segmentPrefix: "seg", writeAheadLog: reopenedWal);
        var present = new List<int>();
        for (var i = 1; i <= 9; i++)
        {
            var (_, found) = await reopened.TryGetValueAsync(K(i));
            if (found)
            {
                present.Add(i);
            }
        }

        present.Should().Match<List<int>>(
            p => p.Count == 0 || p.Count == 9,
            "a clear interrupted by a crash must reopen to the pre-clear or the post-clear state, not a mix");
    }

    /// <summary>Copies the store's files the instant the engine truncates its WAL — a crash image.</summary>
    private sealed class CrashImageAtTruncateWal(
        IWriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>> inner, string liveRoot, string imageRoot)
        : IWriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>
    {
        public bool Captured { get; private set; }

        public Task AppendAsync(SerializableWrapper<int> key, SerializableWrapper<string> value, ulong sequenceNumber, CancellationToken cancellationToken = default)
            => inner.AppendAsync(key, value, sequenceNumber, cancellationToken);

        public Task AppendPointerAsync(SerializableWrapper<int> key, Boutquin.Storage.Domain.ValueObjects.ValueLog.ValueLogPointer pointer, ulong sequenceNumber, CancellationToken cancellationToken = default)
            => inner.AppendPointerAsync(key, pointer, sequenceNumber, cancellationToken);

        public Task AppendDeleteAsync(SerializableWrapper<int> key, ulong sequenceNumber, CancellationToken cancellationToken = default)
            => inner.AppendDeleteAsync(key, sequenceNumber, cancellationToken);

        public Task<IReadOnlyList<WalEntry<SerializableWrapper<int>, SerializableWrapper<string>>>> RecoverAsync(CancellationToken cancellationToken = default)
            => inner.RecoverAsync(cancellationToken);

        public Task TruncateCompletedSegmentsAsync(ulong upToSeqnoInclusive, CancellationToken cancellationToken = default)
            => inner.TruncateCompletedSegmentsAsync(upToSeqnoInclusive, cancellationToken);

        public Task TruncateAsync(CancellationToken cancellationToken = default)
        {
            CopyTree(liveRoot, imageRoot);
            Captured = true;
            return inner.TruncateAsync(cancellationToken);
        }

        public void Dispose() => inner.Dispose();

        private static void CopyTree(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (var file in Directory.GetFiles(source))
            {
                if (!file.EndsWith(".lock", StringComparison.Ordinal))
                {
                    File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
                }
            }

            foreach (var directory in Directory.GetDirectories(source))
            {
                CopyTree(directory, Path.Combine(destination, Path.GetFileName(directory)));
            }
        }
    }
}
