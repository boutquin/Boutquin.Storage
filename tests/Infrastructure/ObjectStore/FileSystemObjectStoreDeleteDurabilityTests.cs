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
using Boutquin.Storage.Infrastructure.ObjectStore;

namespace Boutquin.Storage.Infrastructure.Tests.ObjectStore;

/// <summary>
/// Delete-durability for <see cref="FileSystemObjectStore"/>. A bare <c>File.Delete</c> removes the
/// directory entry but does not make the removal durable; a crash after the unlink returns yet
/// before the directory write lands can resurrect the deleted object on restart. The store must
/// fsync the containing directory after a delete, mirroring the write path's
/// <c>DurableFile.AtomicReplace</c> directory fsync.
/// </summary>
public sealed class FileSystemObjectStoreDeleteDurabilityTests : IDisposable
{
    private readonly string _rootDir;
    private readonly FileSystemObjectStore _store;

    public FileSystemObjectStoreDeleteDurabilityTests()
    {
        _rootDir = Path.Combine(Path.GetTempPath(), "objstore-delete-durability", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_rootDir);
        _store = new FileSystemObjectStore(_rootDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootDir))
        {
            Directory.Delete(_rootDir, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteAsync_OfExistingKey_FsyncsContainingDirectory()
    {
        // The delete-durability fsync is a POSIX-only step (NTFS makes the directory update durable
        // on its own), exactly as DurableFile guards it on the write path.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var key = "to-delete.dat";
        await using (var content = new MemoryStream("payload"u8.ToArray()))
        {
            await _store.WriteAsync(key, content).ConfigureAwait(true);
        }

        // Deleting an existing key must fsync the containing directory so the unlink is durable.
        await _store.DeleteAsync(key).ConfigureAwait(true);
        _store._deleteDirectoryFsyncCount.Should().Be(1,
            "deleting an existing object must fsync the containing directory so the unlink survives " +
            "a crash — otherwise the deleted object can resurrect on restart");

        (await _store.ExistsAsync(key).ConfigureAwait(true)).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteAsync_OfMissingKey_DoesNotFsync()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // No file to unlink → no directory mutation → no fsync.
        await _store.DeleteAsync("never-written.dat").ConfigureAwait(true);
        _store._deleteDirectoryFsyncCount.Should().Be(0,
            "a delete of a missing key removes nothing, so there is no unlink to make durable");
    }

    /// <summary>
    /// A nested key's write creates ancestor directories, and each created ENTRY must be
    /// durable before the write acks: AtomicReplace fsyncs only the immediate containing
    /// directory, so on ext4-class filesystems (the Pillai et al. 2014 crash model the repo's
    /// own DurableFile cites) a crash could drop the freshly created subtree — and the
    /// acknowledged object with it.
    /// </summary>
    [Fact]
    public async Task WriteAsync_NestedKey_MakesEveryCreatedAncestorDirectoryDurable()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var payload = new MemoryStream("x"u8.ToArray());
        await _store.WriteAsync("y2026/m07/part-0001.dat", payload).ConfigureAwait(true);

        _store._ancestorDirectoriesCreatedDurably.Should().Be(2,
            "two directory levels (y2026 and y2026/m07) were created and each entry must be fsync'd");

        // A second write into the SAME subtree creates nothing — the fast path stays clean.
        using var payload2 = new MemoryStream("y"u8.ToArray());
        await _store.WriteAsync("y2026/m07/part-0002.dat", payload2).ConfigureAwait(true);
        _store._ancestorDirectoriesCreatedDurably.Should().Be(2, "no new levels were created");
    }

    /// <summary>
    /// The constructor creates the root (and any missing ancestors). A bare
    /// <c>Directory.CreateDirectory</c> leaves those new entries in the page cache, and every later
    /// write fsyncs only levels BELOW the (by then existing) root — so a crash after an acked write
    /// into a freshly created root could drop the whole tree. Each created root level must be fsync'd.
    /// </summary>
    [Fact]
    public void Constructor_OverMissingNestedRoot_MakesEveryCreatedRootLevelDurable()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var nestedRoot = Path.Combine(_rootDir, "lvl1", "lvl2", "root");
        var store = new FileSystemObjectStore(nestedRoot);

        Directory.Exists(nestedRoot).Should().BeTrue();
        store._rootDirectoriesCreatedDurably.Should().Be(3,
            "three levels (lvl1, lvl1/lvl2, lvl1/lvl2/root) were created by the constructor and each " +
            "entry must be fsync'd — later writes only fsync levels below the existing root");

        // Re-opening the now-existing root creates nothing.
        new FileSystemObjectStore(nestedRoot)._rootDirectoriesCreatedDurably.Should().Be(0,
            "an existing root has no new directory entries to make durable");
    }
}
