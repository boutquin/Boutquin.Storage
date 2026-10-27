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
using Boutquin.Storage.Infrastructure.Events;

namespace Boutquin.Storage.Infrastructure.Tests.Events;

/// <summary>
/// First-append directory-fsync durability for <see cref="AppendOnlyEventLog{TEvent}"/>. When the
/// first append CREATES the log file, the new directory entry is not durable until the containing
/// directory's inode is fsync'd. Without it, a crash after the frame flush but before the directory
/// write lands can lose the whole file — dropping an already-acknowledged append. Mirrors
/// <c>WriteAheadLog</c>'s first-segment-create directory fsync.
/// </summary>
public sealed class EventLogDirectoryFsyncTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("eventlog-dirfsync-").FullName;

    private string NewPath() => Path.Combine(_dir, $"{Guid.NewGuid():N}.log");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task FirstAppend_OnNewFile_FsyncsDirectoryExactlyOnce()
    {
        // The first-creation directory fsync is a POSIX-only durability step (NTFS rename/create is
        // durable on its own), exactly as the WAL guards it.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var path = NewPath();
        using var log = await AppendOnlyEventLog<string>.CreateAsync(path).ConfigureAwait(true);

        // No append yet → the file has not been created by the writer → no directory fsync.
        log._directoryFsyncCount.Should().Be(0, "the log file is not created until the first append");

        // First append creates the file: the containing directory entry must be fsync'd once.
        await log.AppendAsync("event-0").ConfigureAwait(true);
        log._directoryFsyncCount.Should().Be(1,
            "the append that creates the log file must fsync the containing directory so the new " +
            "directory entry is durable — otherwise an acknowledged append can vanish after a crash");

        // Subsequent appends reuse the open stream and must NOT re-fsync the directory.
        await log.AppendAsync("event-1").ConfigureAwait(true);
        log._directoryFsyncCount.Should().Be(1, "only the file-creating append fsyncs the directory");
    }
    /// <summary>
    /// The first append's directory fsync fails after the open created the file. A retried append —
    /// on the same instance or a fresh one — must still make the directory entry durable before it
    /// acknowledges anything; keying the fsync on "the file did not exist" skips it forever after.
    /// </summary>
    [Fact]
    public async Task RetriedAppend_AfterTheFirstAppendsDirectoryFsyncFailed_FsyncsDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // Write + search but no read: creating the file succeeds, opening the directory to fsync it fails.
        var noRead = Directory.CreateDirectory(Path.Combine(_dir, "noread")).FullName;
        var path = Path.Combine(noRead, "events.log");
        using var log = await AppendOnlyEventLog<string>.CreateAsync(path).ConfigureAwait(true);
        File.SetUnixFileMode(noRead, UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var first = async () => await log.AppendAsync("event-0").ConfigureAwait(true);
            await first.Should().ThrowAsync<IOException>("the directory cannot be opened for fsync").ConfigureAwait(true);
            File.Exists(path).Should().BeTrue("the failed append created the file before the fsync ran");
        }
        finally
        {
            File.SetUnixFileMode(noRead, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        (await log.AppendAsync("event-0").ConfigureAwait(true)).Should().Be(0);
        log._directoryFsyncCount.Should().Be(1,
            "the retried append finds an empty log whose directory entry no append has made durable yet");
    }

    /// <summary>
    /// A crash between the file's creation and the directory fsync leaves an empty file. The first
    /// append through a fresh instance must fsync the directory, as the write-ahead log does for an
    /// empty segment.
    /// </summary>
    [Fact]
    public async Task FirstAppend_OnExistingEmptyFile_FsyncsDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var path = NewPath();
        await File.WriteAllBytesAsync(path, []).ConfigureAwait(true);

        using var log = await AppendOnlyEventLog<string>.CreateAsync(path).ConfigureAwait(true);
        await log.AppendAsync("event-0").ConfigureAwait(true);
        log._directoryFsyncCount.Should().Be(1,
            "an empty log file may be one whose creation never reached a directory fsync");
    }
}
