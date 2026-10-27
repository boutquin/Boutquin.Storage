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
/// C09 — RAFTLOG-DIR-FSYNC-001. When <see cref="FileRaftLog{TCommand}"/>'s constructor CREATES
/// a new log file, the new directory entry is not durable until the containing directory's inode
/// is fsync'd. Without it, a crash after a frame flush but before the directory write lands can
/// lose the whole log file — dropping entries the cluster already replicated and acknowledged,
/// contradicting Raft persist-before-respond durability. Mirrors <c>WriteAheadLog</c>'s
/// first-segment-create directory fsync and <c>AppendOnlyEventLog</c>'s first-append fsync.
/// </summary>
public sealed class FileRaftLogDirectoryFsyncTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("raftlog-dirfsync-").FullName;

    private string NewPath() => Path.Combine(_dir, $"{Guid.NewGuid():N}.bin");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static byte[] Ser(string s) => Encoding.UTF8.GetBytes(s);

    private static string Deser(byte[] b) => Encoding.UTF8.GetString(b);

    [Fact]
    public void Constructor_OnNewFile_FsyncsDirectoryExactlyOnce()
    {
        // The file-creating directory fsync is a POSIX-only durability step (NTFS create is durable
        // on its own), exactly as the WAL and event log guard it.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var path = NewPath();

        // Constructing on a path with no existing file CREATES the directory entry: the containing
        // directory must be fsync'd once so the new file survives a crash.
        using var log = new FileRaftLog<string>(path, Ser, Deser);

        log._directoryFsyncCount.Should().Be(1,
            "the constructor that creates the log file must fsync the containing directory so the " +
            "new directory entry is durable — otherwise an acknowledged entry can vanish after a crash");
    }

    [Fact]
    public void Constructor_OnExistingFile_DoesNotFsyncDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var path = NewPath();

        // First construction creates the file and fsyncs the directory once.
        using (var first = new FileRaftLog<string>(path, Ser, Deser))
        {
            first.Append((1L, "entry-0"));
        }

        // Reopening an existing file does NOT create a new directory entry → no directory fsync.
        using var reopened = new FileRaftLog<string>(path, Ser, Deser);
        reopened._directoryFsyncCount.Should().Be(0,
            "reopening an existing log file does not create a directory entry, so no directory fsync is owed");
    }
    /// <summary>
    /// A first open whose directory fsync failed leaves the created, empty file behind. The retried
    /// open must still make that directory entry durable before the log can acknowledge an entry —
    /// keying the fsync on "the file did not exist" alone skips it forever after.
    /// </summary>
    [Fact]
    public void Constructor_AfterAFirstOpenWhoseDirectoryFsyncFailed_FsyncsDirectoryOnRetry()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // Write + search but no read: creating a file succeeds, opening the directory to fsync it fails.
        var noRead = Directory.CreateDirectory(Path.Combine(_dir, "noread")).FullName;
        File.SetUnixFileMode(noRead, UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var path = Path.Combine(noRead, "raft.log");
        try
        {
            Action firstOpen = () => new FileRaftLog<string>(path, Ser, Deser).Dispose();
            firstOpen.Should().Throw<IOException>("the directory cannot be opened for fsync");
            File.Exists(path).Should().BeTrue("the failed open created the file before the fsync ran");
        }
        finally
        {
            File.SetUnixFileMode(noRead, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        using var retried = new FileRaftLog<string>(path, Ser, Deser);
        retried._directoryFsyncCount.Should().Be(1,
            "the retried open finds an empty log whose directory entry no open has made durable yet");
        retried.Append((1L, "entry-0"));
    }

    /// <summary>
    /// A crash between the file's creation and the directory fsync leaves an empty file. Reopening
    /// it must fsync the directory, as the write-ahead log does for an empty segment.
    /// </summary>
    [Fact]
    public void Constructor_OnExistingEmptyFile_FsyncsDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var path = NewPath();
        File.WriteAllBytes(path, []);

        using var log = new FileRaftLog<string>(path, Ser, Deser);
        log._directoryFsyncCount.Should().Be(1,
            "an empty log file may be one whose creation never reached a directory fsync");
    }
}
