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
/// Pins the ordering contract of <see cref="LsmStorageEngine{TKey, TValue}.ClearAsync"/> against a
/// failing directory fsync: every segment deletion and the emptied <c>CURRENT</c> is followed by a
/// segment-folder fsync before the clear reports success, and a fsync failure on one deletion
/// neither stops the remaining deletions nor leaves the engine's in-memory segment set pointing
/// at files the clear already removed.
/// </summary>
/// <remarks>
/// Each test removes the folder's read bit (mode 0300): entries can still be deleted, but the
/// folder itself can no longer be opened, so every directory fsync — and only that step — fails.
/// POSIX only; NTFS directory operations are durable without a directory fsync.
/// </remarks>
public sealed class LsmStorageEngineClearDurabilityOrderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"LsmClearDurabilityOrder_{Guid.NewGuid():N}");

    public LsmStorageEngineClearDurabilityOrderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best-effort */ }
        }
    }

    private static SerializableWrapper<int> K(int v) => new(v);
    private static SerializableWrapper<int> V(int v) => new(v);

    private LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<int>> NewEngine()
        => LsmEngineFactory.Build<SerializableWrapper<int>, SerializableWrapper<int>>(
            memTableCapacity: 2, segmentFolder: _dir, segmentPrefix: "seg");

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private async Task ClearWithUnopenableFolderAsync(
        LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<int>> engine,
        string because)
    {
        var originalMode = File.GetUnixFileMode(_dir);
        File.SetUnixFileMode(_dir, UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var clear = () => engine.ClearAsync();
            await clear.Should().ThrowAsync<IOException>(because).WithMessage("*fsync*");
        }
        finally
        {
            File.SetUnixFileMode(_dir, originalMode);
        }
    }

    [Fact]
    public async Task ClearAsync_SegmentDeleteFsyncFails_StillClearsEverySegment_AndStoreReopensEmpty()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var engine = NewEngine();
        try
        {
            for (var i = 1; i <= 6; i++)
            {
                await engine.SetAsync(K(i), V(i * 10));
            }

            await engine.FlushAsync();
            Directory.GetFiles(_dir, "seg_*.sst").Length.Should().BeGreaterThan(1, "the workload spans several segments");

            await ClearWithUnopenableFolderAsync(
                engine, "a clear whose deletions could not be made durable must not report success");

            // The engine already acknowledged the in-memory clear; a fsync failure on one segment's
            // delete must not leave the segment list holding readers whose files are gone.
            var foundInMemory = true;
            var readFailure = await Record.ExceptionAsync(async () => (_, foundInMemory) = await engine.TryGetValueAsync(K(1)));
            readFailure.Should().BeNull("the failed clear must still empty the in-memory segment set, not keep "
                + "readers whose files it already deleted");
            foundInMemory.Should().BeFalse("the clear removed every key");

            Directory.GetFiles(_dir, "seg_*.sst").Should().BeEmpty(
                "one segment's fsync failure must not stop the deletion of the remaining segments");
            (SegmentManifest.ReadEntries(_dir) ?? []).Should().BeEmpty(
                "CURRENT must not survive naming segments the clear already deleted");
        }
        finally
        {
            engine.Dispose();
        }

        // The store the failed clear left behind must reopen; a CURRENT naming a deleted segment
        // fails the next open closed.
        using var reopened = await LsmEngineFactory.BuildAsync<SerializableWrapper<int>, SerializableWrapper<int>>(
            memTableCapacity: 2, segmentFolder: _dir, segmentPrefix: "seg");
        var (_, foundAfterReopen) = await reopened.TryGetValueAsync(K(1));
        foundAfterReopen.Should().BeFalse("the clear removed every key");
    }

    [Fact]
    public async Task ClearAsync_WithEverySegmentPinned_FsyncsFolderAfterEmptyingCurrent()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var engine = NewEngine();
        await engine.SetAsync(K(1), V(10));
        await engine.FlushAsync();
        SegmentManifest.Exists(_dir).Should().BeTrue();

        // A live snapshot pins every segment, so the clear deletes no segment file: emptying CURRENT
        // is the only entry change, and its folder fsync is the only step that can make it durable
        // (the engine has no WAL).
        await using (await engine.AcquireSnapshotAsync())
        {
            await ClearWithUnopenableFolderAsync(
                engine, "the CURRENT change must be made durable by a folder fsync before ClearAsync succeeds");

            (SegmentManifest.ReadEntries(_dir) ?? []).Should().BeEmpty(
                "the clear empties CURRENT before its folder fsync, so the pinned file is an unlisted orphan");
            Directory.GetFiles(_dir, "seg_*.sst").Should().NotBeEmpty("the snapshot still pins the segment");
        }

        Directory.GetFiles(_dir, "seg_*.sst").Should().BeEmpty("releasing the last pin reclaims the retired segment");
    }

    /// <summary>
    /// A clear whose manifest write fails before replacing <c>CURRENT</c> has changed nothing
    /// durable, so it must not go on to discard anything: <c>CURRENT</c> still names every segment,
    /// so unlinking one leaves a manifest naming a missing file, which the next open refuses.
    /// </summary>
    [Fact]
    public async Task ClearAsync_ManifestWriteFails_DiscardsNothing_AndStoreReopensIntact()
    {
        var engine = NewEngine();
        try
        {
            for (var i = 1; i <= 4; i++)
            {
                await engine.SetAsync(K(i), V(i * 10));
            }

            await engine.FlushAsync();
            await engine.SetAsync(K(5), V(50));

            engine.ClearManifestWriteHook = () => throw new IOException("injected: no space left on device");
            var clear = () => engine.ClearAsync();
            await clear.Should().ThrowAsync<IOException>("the clear could not empty CURRENT").WithMessage("*injected*");
            engine.ClearManifestWriteHook = null;

            var listed = (SegmentManifest.ReadEntries(_dir) ?? []).Select(e => e.FileName).ToList();
            listed.Should().NotBeEmpty("the failed write left the pre-clear CURRENT in place");
            listed.Should().OnlyContain(f => File.Exists(Path.Combine(_dir, f)),
                "a clear that could not empty CURRENT must not unlink a segment CURRENT still names");

            var (_, flushedFound) = await engine.TryGetValueAsync(K(1));
            var (_, unflushedFound) = await engine.TryGetValueAsync(K(5));
            flushedFound.Should().BeTrue("the failed clear must leave the flushed data served");
            unflushedFound.Should().BeTrue("the failed clear must leave the memtable in place");
        }
        finally
        {
            engine.Dispose();
        }

        using var reopened = await LsmEngineFactory.BuildAsync<SerializableWrapper<int>, SerializableWrapper<int>>(
            memTableCapacity: 2, segmentFolder: _dir, segmentPrefix: "seg");
        var (_, foundAfterReopen) = await reopened.TryGetValueAsync(K(1));
        foundAfterReopen.Should().BeTrue("the store the failed clear left behind reopens with its flushed data");
    }

    private static string ClearMarkerPath(string segmentFolder)
        => Path.Combine(segmentFolder, LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<int>>.ClearPendingMarkerFileName);

    /// <summary>
    /// Makes the clear's marker write fail AFTER the marker's directory entry exists — the state a
    /// failing fsync or close of the freshly created marker leaves. Another handle holds the marker
    /// under an exclusive lock, so the clear's own open reaches the file and then throws. The
    /// returned handle must be disposed by the caller.
    /// </summary>
    private static Func<FileStream?> HoldMarkerDuringClear(
        LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<int>> engine, string markerPath, Action? afterHold = null)
    {
        FileStream? holder = null;
        engine.ClearDrainToLockHook = () =>
        {
            holder = new FileStream(markerPath, FileMode.Create, FileAccess.Write, FileShare.None);
            afterHold?.Invoke();
            return Task.CompletedTask;
        };
        return () => holder;
    }

    /// <summary>
    /// A WAL-backed clear whose marker write fails has discarded nothing. If it leaves the marker
    /// on disk, the next open completes that clear — discarding the pre-clear data and every write
    /// the engine acknowledged after the failure. The failed clear must therefore remove the marker
    /// and leave a store that reopens with everything it acknowledged.
    /// </summary>
    [Fact]
    public async Task ClearAsync_MarkerWriteFailsAfterCreatingIt_LeavesNoMarker_AndStoreReopensIntact()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var segmentFolder = Path.Combine(_dir, "seg");
        var walFolder = Path.Combine(_dir, "wal");
        var markerPath = ClearMarkerPath(segmentFolder);

        using (var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<int>>(walFolder))
        {
            var engine = LsmEngineFactory.Build(
                memTableCapacity: 2, segmentFolder: segmentFolder, segmentPrefix: "seg", writeAheadLog: wal);
            try
            {
                for (var i = 1; i <= 4; i++)
                {
                    await engine.SetAsync(K(i), V(i * 10));
                }

                await engine.FlushAsync();
                await engine.SetAsync(K(5), V(50));

                var holder = HoldMarkerDuringClear(engine, markerPath);
                try
                {
                    var clear = () => engine.ClearAsync();
                    await clear.Should().ThrowAsync<IOException>("the clear could not write its marker");
                }
                finally
                {
                    engine.ClearDrainToLockHook = null;
                    holder()?.Dispose();
                }

                File.Exists(markerPath).Should().BeFalse(
                    "a clear that failed before discarding anything must not leave the marker that makes the next "
                    + "open discard the store");

                await engine.SetAsync(K(6), V(60));
                await engine.FlushAsync();
                await engine.SetAsync(K(7), V(70));
            }
            finally
            {
                engine.Dispose();
            }
        }

        using var reopenedWal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<int>>(walFolder);
        using var reopened = await LsmEngineFactory.BuildAsync(
            memTableCapacity: 2, segmentFolder: segmentFolder, segmentPrefix: "seg", writeAheadLog: reopenedWal);
        var present = new List<int>();
        for (var i = 1; i <= 7; i++)
        {
            var (_, found) = await reopened.TryGetValueAsync(K(i));
            if (found)
            {
                present.Add(i);
            }
        }

        present.Should().Equal([1, 2, 3, 4, 5, 6, 7],
            "the failed clear discarded nothing, so every write acknowledged before and after it survives a reopen");
    }

    /// <summary>
    /// When the failed clear cannot remove its marker either, the next open will complete the clear,
    /// so any write accepted now would be discarded then. The engine must fail closed instead.
    /// </summary>
    [Fact]
    public async Task ClearAsync_MarkerWriteFails_AndMarkerCannotBeRemoved_FaultsTheEngine()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var segmentFolder = Path.Combine(_dir, "seg");
        var walFolder = Path.Combine(_dir, "wal");
        var markerPath = ClearMarkerPath(segmentFolder);

        using var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<int>>(walFolder);
        using var engine = LsmEngineFactory.Build(
            memTableCapacity: 2, segmentFolder: segmentFolder, segmentPrefix: "seg", writeAheadLog: wal);
        await engine.SetAsync(K(1), V(10));
        await engine.FlushAsync();

        var originalMode = File.GetUnixFileMode(segmentFolder);

        // Read and search only: the marker can be neither created nor unlinked by anyone after the
        // holder made it.
        var holder = HoldMarkerDuringClear(engine, markerPath,
            () =>
            {
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(segmentFolder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
                }
            });
        try
        {
            var clear = () => engine.ClearAsync();
            await clear.Should().ThrowAsync<IOException>("the clear could not write its marker");
        }
        finally
        {
            File.SetUnixFileMode(segmentFolder, originalMode);
            engine.ClearDrainToLockHook = null;
            holder()?.Dispose();
        }

        File.Exists(markerPath).Should().BeTrue("the folder refused the marker's removal");
        var write = () => engine.SetAsync(K(2), V(20));
        await write.Should().ThrowAsync<IOException>(
            "the surviving marker makes the next open discard the store, so a write accepted now would be lost")
            .WithMessage("*faulted*");
    }
}
