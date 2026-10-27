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
using System.Runtime.InteropServices;

namespace Boutquin.Storage.Infrastructure.KeyValueStore;

/// <summary>
/// Helper for durable file replacement — atomic rename plus a containing-directory fsync on
/// POSIX platforms so the rename survives a crash.
/// </summary>
/// <remarks>
/// <para>
/// On Linux ext4/XFS/ext3 and on ZFS/btrfs, <c>rename(2)</c> is atomic but not durable
/// until the containing directory's inode reaches disk. Without an explicit directory
/// fsync, a crash between the rename syscall returning and the directory write landing can
/// leave the old entry visible on restart — exactly the failure mode the checkpoint and
/// LSM-manifest call sites are trying to prevent.
/// </para>
/// <para>
/// On Windows, this helper requests a POSIX-semantics handle rename, allowing a shared old
/// reader to finish while fresh opens see the replacement. It returns without a directory
/// durability barrier. That operation alone does not establish
/// persistence across power loss; the runtime, filesystem and storage stack require separate
/// qualification. No Windows power-loss guarantee is established by this implementation.
/// Publication is serialized for normalized, case-insensitive destination paths within this
/// process; it does not coordinate writers in other processes. The filesystem must support
/// the extended handle-rename operation; unsupported operations fail without a copy/delete fallback.
/// </para>
/// <para>
/// Data-page durability across the storage paths is provided by
/// <see cref="FileStream.Flush(bool)"/> with <c>flushToDisk: true</c> (the call every append /
/// atomic-write site makes before acknowledging a write); this helper adds the complementary
/// <i>directory-entry</i> durability that a rename needs. On macOS (APFS), <c>fsync</c> — the syscall
/// both <c>FileStream.Flush(flushToDisk: true)</c> and the directory fsync below issue — does not
/// flush to stable media; strict durability requires <c>fcntl(F_FULLFSYNC)</c>. The residual risk is
/// acknowledged and documented; full <c>F_FULLFSYNC</c> support is a future hardening item.
/// </para>
/// <para>
/// <b>Reference:</b> Pillai et al. 2014 (OSDI) §3 — "All File Systems Are Not Created Equal:
/// On the Complexity of Crafting Crash-Consistent Applications" — for the full crash-consistency
/// landscape across file systems.
/// </para>
/// </remarks>
internal static partial class DurableFile
{
    // Windows replacement opens the existing destination during publication. Concurrent
    // same-path renames can conflict even when no reader holds it open. Bounded stripes
    // serialize that short commit step across every helper caller without retaining paths.
    private const int WindowsReplacementStripeCount = 64;
    private static readonly Lock[] s_windowsReplacementGates = Enumerable.Range(0, WindowsReplacementStripeCount)
        .Select(static _ => new Lock()).ToArray();

    /// <summary>
    /// Atomically replaces <paramref name="targetPath"/> with <paramref name="sourcePath"/>.
    /// On POSIX platforms, fsyncs the containing directory so the rename itself is durable.
    /// </summary>
    /// <param name="sourcePath">The file to move. Must exist. Consumed by the operation.</param>
    /// <param name="targetPath">The destination path. Overwritten if it exists.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="sourcePath"/> or <paramref name="targetPath"/> is null,
    /// empty, or when both resolve to the same absolute path.
    /// </exception>
    /// <exception cref="FileNotFoundException">Thrown when the source file does not exist.</exception>
    /// <exception cref="IOException">
    /// Thrown when the underlying rename or directory fsync fails.
    /// </exception>
    public static void AtomicReplace(string sourcePath, string targetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        if (string.Equals(
                Path.GetFullPath(sourcePath),
                Path.GetFullPath(targetPath),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "sourcePath and targetPath must resolve to different files; AtomicReplace is not a no-op.",
                nameof(sourcePath));
        }

        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Source file not found.", sourcePath);
        }

        if (OperatingSystem.IsWindows())
        {
            var target = Path.GetFullPath(targetPath);
            var stripe = StringComparer.OrdinalIgnoreCase.GetHashCode(target) & (WindowsReplacementStripeCount - 1);
            lock (s_windowsReplacementGates[stripe])
            {
                AtomicReplaceWindows(sourcePath, target);
            }

            // This implementation has no Windows directory durability barrier. Returning after
            // Handle-based rename does not establish persistence across power loss; see the type remarks.
            return;
        }

        File.Move(sourcePath, targetPath, overwrite: true);
        FsyncDirectory(targetPath);
    }

    /// <summary>
    /// Creates <paramref name="directory"/> (and any missing ancestors), fsyncing each newly
    /// created level's parent on POSIX, top-down.
    /// A bare <c>Directory.CreateDirectory</c> leaves the new entries in the OS page cache —
    /// on ext4-class filesystems (Pillai et al. 2014 §3) a crash after an acknowledged write
    /// into a freshly created subtree can drop the whole subtree, taking the acked file with
    /// it. Returns the number of directory levels created (0 when the path already exists);
    /// Windows skips the directory barrier; its power-loss persistence remains a separate
    /// platform qualification, as described in the type remarks.
    /// </summary>
    internal static int CreateDirectoryDurable(string directory)
    {
        var missing = new Stack<string>();
        var current = Path.GetFullPath(directory);
        while (!string.IsNullOrEmpty(current) && !Directory.Exists(current))
        {
            missing.Push(current);
            current = Path.GetDirectoryName(current)!;
        }

        if (missing.Count == 0)
        {
            return 0;
        }

        Directory.CreateDirectory(directory);

        var created = missing.Count;
        if (!OperatingSystem.IsWindows())
        {
            // Top-down (root-most first): FsyncDirectory(path) fsyncs path's PARENT, making
            // the newly created entry itself durable before its children's entries are.
            foreach (var level in missing)
            {
                FsyncDirectory(level);
            }
        }

        return created;
    }

    /// <summary>
    /// Fsyncs the directory containing <paramref name="filePath"/> so a prior rename
    /// or create operation is durable across a crash. Called only on non-Windows.
    /// </summary>
    /// <remarks>
    /// Uses POSIX <c>open(2)</c> + <c>fsync(2)</c> via P/Invoke. <see cref="File.OpenHandle(string, FileMode, FileAccess, FileShare, FileOptions, long)"/>
    /// rejects directory paths on POSIX with <see cref="UnauthorizedAccessException"/> because
    /// the managed helper validates the path as a file; directory fsync is a regular POSIX
    /// operation that needs the raw syscall pair.
    /// </remarks>
    internal static void FsyncDirectory(string filePath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        const int O_RDONLY = 0;
        var fd = NativeOpen(directory, O_RDONLY);
        if (fd < 0)
        {
            var err = Marshal.GetLastPInvokeError();
            throw new IOException(
                $"Failed to open directory '{directory}' for fsync (errno={err}).");
        }

        try
        {
            if (NativeFsync(fd) < 0)
            {
                var err = Marshal.GetLastPInvokeError();
                throw new IOException(
                    $"Failed to fsync directory '{directory}' (errno={err}).");
            }
        }
        finally
        {
            _ = NativeClose(fd);
        }
    }

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int NativeOpen(string pathname, int flags);

    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static partial int NativeFsync(int fd);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int NativeClose(int fd);
}
