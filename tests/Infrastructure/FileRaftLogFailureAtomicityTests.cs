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
using System.Reflection;

namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// Pins failure atomicity of <see cref="FileRaftLog{TCommand}"/>'s mutations. The type promises
/// that its in-memory mirror is a faithful mirror of disk, so an I/O failure inside
/// <see cref="FileRaftLog{TCommand}.Append"/> or <see cref="FileRaftLog{TCommand}.TruncateFrom"/>
/// must either leave the file at the mirror's last good boundary (a retry is then safe) or fault
/// the log so it never serves or extends a state that disagrees with disk. The faults are injected
/// by swapping the log's private stream for one that throws on demand — the only deterministic way
/// to make a real file write, fsync or truncate fail.
/// </summary>
public sealed class FileRaftLogFailureAtomicityTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("raftlog-atomicity-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string NewPath() => Path.Combine(_dir, $"{Guid.NewGuid():N}.bin");

    private static byte[] Ser(string s) => Encoding.UTF8.GetBytes(s);

    private static string Deser(byte[] b) => Encoding.UTF8.GetString(b);

    private static List<(long Term, string Command)> Entries(FileRaftLog<string> log)
        => [.. log.GetRange(0, log.Count)];

    private static FaultInjectingStream InjectFaults(FileRaftLog<string> log, string path)
    {
        var field = typeof(FileRaftLog<string>).GetField("_stream", BindingFlags.NonPublic | BindingFlags.Instance)!;
        ((FileStream)field.GetValue(log)!).Dispose();
        var faulty = new FaultInjectingStream(path);
        field.SetValue(log, faulty);
        return faulty;
    }

    [Fact]
    public void Append_FsyncFailure_LeavesDiskMatchingMirror_SoARetryIsNotDuplicated()
    {
        var path = NewPath();
        List<(long, string)> mirror;
        using (var log = new FileRaftLog<string>(path, Ser, Deser))
        {
            log.Append((1, "e0"));
            var faults = InjectFaults(log, path);

            // The frame reaches the file, then its fsync fails: the append is not acknowledged.
            faults.FailFlushes = 1;
            var failed = () => log.Append((1, "e1"));
            failed.Should().Throw<IOException>();
            log.Count.Should().Be(1);

            // A leader resends the entry; the host retries the append.
            log.Append((1, "e1"));
            log.Append((1, "e2"));
            mirror = Entries(log);
        }

        using var reopened = new FileRaftLog<string>(path, Ser, Deser);
        Entries(reopened).Should().Equal(mirror,
            "the durable log must be exactly the log the node served and acknowledged — a failed append must not leave its frame behind");
    }

    [Fact]
    public void Append_PartialWriteFailure_LeavesNoTornBytes_SoTheLogStillReopens()
    {
        var path = NewPath();
        List<(long, string)> mirror;
        using (var log = new FileRaftLog<string>(path, Ser, Deser))
        {
            log.Append((1, "e0"));
            var faults = InjectFaults(log, path);

            // Only part of the frame is written before the write fails.
            faults.PartialWriteBytes = 5;
            var failed = () => log.Append((1, "e1"));
            failed.Should().Throw<IOException>();

            log.Append((1, "e2"));
            mirror = Entries(log);
        }

        var reopen = () => new FileRaftLog<string>(path, Ser, Deser);
        using var reopened = reopen.Should().NotThrow(
            "a failed append must not seal torn bytes into the middle of the file").Subject;
        Entries(reopened).Should().Equal(mirror);
    }

    [Fact]
    public void Append_FailureWhoseRepairAlsoFails_FaultsTheLog()
    {
        var path = NewPath();
        using var log = new FileRaftLog<string>(path, Ser, Deser);
        log.Append((1, "e0"));
        var faults = InjectFaults(log, path);

        // The fsync fails and so does the truncation that would restore the last good boundary:
        // the file tail is now unknown, so the log must refuse further use until reopened.
        faults.FailFlushes = 1;
        faults.FailSetLengths = 1;
        var failed = () => log.Append((1, "e1"));
        failed.Should().Throw<IOException>();

        var next = () => log.Append((1, "e2"));
        next.Should().Throw<InvalidOperationException>(
            "appending after an unrepaired failure would seal unknown tail bytes into the middle of the log");
        var read = () => log.GetEntry(0);
        read.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void TruncateFrom_FsyncFailure_FaultsTheLog_SoAStaleMirrorIsNeverServed()
    {
        var path = NewPath();
        using var log = new FileRaftLog<string>(path, Ser, Deser);
        log.Append((1, "e0"));
        log.Append((1, "e1"));
        log.Append((1, "e2"));
        var faults = InjectFaults(log, path);

        // The truncation lands in the file, then its fsync fails: how much of it is durable is
        // unknown, and the mirror still holds the entries the file no longer has.
        faults.FailFlushes = 1;
        var failed = () => log.TruncateFrom(1);
        failed.Should().Throw<IOException>();

        var read = () => log.GetEntry(1);
        read.Should().Throw<InvalidOperationException>(
            "the mirror must not serve an entry whose on-disk state is uncertain");
        var append = () => log.Append((2, "x3"));
        append.Should().Throw<InvalidOperationException>();
    }

    /// <summary>A real file stream that fails selected operations on demand.</summary>
    private sealed class FaultInjectingStream(string path)
        : FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
    {
        public int FailFlushes { get; set; }

        public int FailSetLengths { get; set; }

        public int? PartialWriteBytes { get; set; }

        public override void Flush(bool flushToDisk)
        {
            if (FailFlushes > 0)
            {
                FailFlushes--;
                throw new IOException("Injected fsync failure.");
            }
            base.Flush(flushToDisk);
        }

        public override void SetLength(long value)
        {
            if (FailSetLengths > 0)
            {
                FailSetLengths--;
                throw new IOException("Injected truncate failure.");
            }
            base.SetLength(value);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (PartialWriteBytes is { } partial)
            {
                PartialWriteBytes = null;
                base.Write(buffer, offset, Math.Min(partial, count));
                base.Flush(flushToDisk: false);
                throw new IOException("Injected partial write failure.");
            }
            base.Write(buffer, offset, count);
        }
    }
}
