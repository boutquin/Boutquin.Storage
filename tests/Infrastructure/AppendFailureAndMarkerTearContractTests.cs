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
/// Contract tests for the durable logs' end-marker tear rule and for append failure atomicity:
/// a final frame is a torn tail only when its bytes are what one interrupted write leaves (a
/// prefix of the frame followed by zeros), and an append the engine reports failed or cancelled
/// leaves none of its bytes behind as history.
/// </summary>
public sealed class AppendFailureAndMarkerTearContractTests : IDisposable
{
    private const int RaftHeaderSize = 25;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"AppendFailureMarker_{Guid.NewGuid():N}");

    public AppendFailureAndMarkerTearContractTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private string RaftPath => Path.Combine(_dir, "raft.bin");

    // ── Raft log: zero end marker after a landed, non-prefix command ─────────────

    /// <summary>
    /// Must-fail-loud: an fsync'd final entry has a middle command byte rotted and its end marker
    /// zeroed. The command's last byte is non-zero, so the whole command landed; a command that
    /// landed in full yet fails its checksum is not a prefix followed by zeros, so this is not an
    /// image one interrupted append can leave.
    /// </summary>
    [Fact]
    public void Raft_ZeroMarkerAfterLandedCommandThatFailsItsChecksum_FailsLoud()
    {
        var finalStart = WriteRaftEntries("op-1", "op-2-final");
        var bytes = File.ReadAllBytes(RaftPath);
        bytes[finalStart + RaftHeaderSize + 3] ^= 0x10;
        bytes[^1] = 0;
        File.WriteAllBytes(RaftPath, bytes);

        var act = () => OpenRaft().Dispose();

        act.Should().Throw<InvalidDataException>("the landed command fails its checksum, so the frame is not a torn prefix");
        File.ReadAllBytes(RaftPath).Should().Equal(bytes, "fail-loud recovery leaves the file untouched");
    }

    /// <summary>
    /// Must-recover: the interrupted final append landed its whole command but not its end marker.
    /// The command passes its checksum, so the frame is a prefix followed by a zero.
    /// </summary>
    [Fact]
    public void Raft_ZeroMarkerAfterChecksumValidCommand_IsATornTail()
    {
        var finalStart = WriteRaftEntries("op-1", "op-2-final");
        var bytes = File.ReadAllBytes(RaftPath);
        bytes[^1] = 0;
        File.WriteAllBytes(RaftPath, bytes);

        using var log = OpenRaft();

        log.Count.Should().Be(1, "the final frame is a prefix of the interrupted append");
        new FileInfo(RaftPath).Length.Should().Be(finalStart, "the torn tail is truncated at the last whole frame");
    }

    private FileRaftLog<string> OpenRaft() => new(RaftPath, Encoding.UTF8.GetBytes, Encoding.UTF8.GetString);

    private int WriteRaftEntries(params string[] commands)
    {
        var finalStart = 0;
        using var log = OpenRaft();
        foreach (var command in commands)
        {
            finalStart = (int)new FileInfo(RaftPath).Length;
            log.Append((1, command));
        }

        return finalStart;
    }

    // ── Append-only engine: rot in a final payload whose genuine last byte is zero ──

    private const int AppendOnlyHeaderSize = 17;

    private const string AppendOnlyFileName = "aof.dat";

    private string AppendOnlyPath => Path.Combine(_dir, AppendOnlyFileName);

    private AppendOnlyFileStorageEngine<SerializableWrapper<int>, SerializableWrapper<int>> NewAppendOnlyEngine(bool indexed)
    {
        var file = new StorageFile(_dir, AppendOnlyFileName);
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<int>>();
        return indexed
            ? new AppendOnlyFileStorageEngineWithIndex<SerializableWrapper<int>, SerializableWrapper<int>>(
                file, serializer, new InMemoryFileIndex<SerializableWrapper<int>>())
            : new AppendOnlyFileStorageEngine<SerializableWrapper<int>, SerializableWrapper<int>>(file, serializer);
    }

    /// <summary>
    /// Must-fail-loud: the shipped serializer encodes value 0 as a payload ending in a zero byte.
    /// The final record is acknowledged (fsync'd before <c>SetAsync</c> returned); one earlier
    /// payload byte then rots. Every byte of the frame landed, so the reopen must refuse the log and
    /// the next append must not erase the record.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AppendOnly_RotInFinalPayloadWhoseGenuineLastByteIsZero_FailsLoud(bool indexed)
    {
        var writer = NewAppendOnlyEngine(indexed);
        await writer.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<int>(10));
        await writer.SetAsync(new SerializableWrapper<int>(2), new SerializableWrapper<int>(20));
        var finalStart = (int)new FileInfo(AppendOnlyPath).Length;
        await writer.SetAsync(new SerializableWrapper<int>(3), new SerializableWrapper<int>(0));

        var bytes = File.ReadAllBytes(AppendOnlyPath);
        bytes[finalStart + AppendOnlyHeaderSize] ^= 0x04;
        File.WriteAllBytes(AppendOnlyPath, bytes);

        var reopened = NewAppendOnlyEngine(indexed);
        var read = () => reopened.TryGetValueAsync(new SerializableWrapper<int>(1));
        var append = () => reopened.SetAsync(new SerializableWrapper<int>(4), new SerializableWrapper<int>(40));

        await read.Should().ThrowAsync<InvalidDataException>("the acknowledged final record landed in full");
        await append.Should().ThrowAsync<InvalidDataException>("an append must not truncate an acknowledged record");
        File.ReadAllBytes(AppendOnlyPath).Should().Equal(bytes, "fail-loud recovery leaves the file untouched");
    }

    // ── Append-only engines: an append reported failed or cancelled leaves nothing behind ──

    /// <summary>The ways an append can fail after its frame bytes were handed to the file.</summary>
    public enum AppendFault
    {
        /// <summary>The data reaches the file, then the fsync fails (EIO).</summary>
        FsyncFails,

        /// <summary>The caller's token fires while the append flushes.</summary>
        CancelledAtFlush,
    }

    /// <summary>
    /// An append the engine reports failed or cancelled must leave none of its bytes as history:
    /// neither the same instance nor a reopened one may serve it, and the file must end where it
    /// ended before the call — the WAL's rule for a failed batch.
    /// </summary>
    [Theory]
    [InlineData(false, false, AppendFault.FsyncFails)]
    [InlineData(false, false, AppendFault.CancelledAtFlush)]
    [InlineData(false, true, AppendFault.FsyncFails)]
    [InlineData(false, true, AppendFault.CancelledAtFlush)]
    [InlineData(true, false, AppendFault.FsyncFails)]
    [InlineData(true, false, AppendFault.CancelledAtFlush)]
    [InlineData(true, true, AppendFault.FsyncFails)]
    [InlineData(true, true, AppendFault.CancelledAtFlush)]
    public async Task AppendOnly_AppendReportedFailedOrCancelled_LeavesNothingBehind(bool indexed, bool bulk, AppendFault fault)
    {
        var file = new FaultingStorageFile(new StorageFile(_dir, AppendOnlyFileName));
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<int>>();
        AppendOnlyFileStorageEngine<SerializableWrapper<int>, SerializableWrapper<int>> engine = indexed
            ? new AppendOnlyFileStorageEngineWithIndex<SerializableWrapper<int>, SerializableWrapper<int>>(
                file, serializer, new InMemoryFileIndex<SerializableWrapper<int>>())
            : new AppendOnlyFileStorageEngine<SerializableWrapper<int>, SerializableWrapper<int>>(file, serializer);
        await engine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<int>(10));
        var lengthBefore = new FileInfo(AppendOnlyPath).Length;

        using var cts = new CancellationTokenSource();
        file.Fault = fault;
        file.CancelOnFlush = cts;
        var call = bulk
            ? () => engine.SetBulkAsync(
                [new(new SerializableWrapper<int>(2), new SerializableWrapper<int>(20)),
                 new(new SerializableWrapper<int>(3), new SerializableWrapper<int>(30))],
                cts.Token)
            : (Func<Task>)(() => engine.SetAsync(new SerializableWrapper<int>(2), new SerializableWrapper<int>(20), cts.Token));

        if (fault == AppendFault.FsyncFails)
        {
            await call.Should().ThrowAsync<IOException>();
        }
        else
        {
            await call.Should().ThrowAsync<OperationCanceledException>();
        }

        file.Fault = null;
        new FileInfo(AppendOnlyPath).Length.Should().Be(lengthBefore, "a failed append is truncated back to where it started");
        (await engine.TryGetValueAsync(new SerializableWrapper<int>(2))).Found.Should().BeFalse("the same instance must not serve a failed append");

        var reopened = new AppendOnlyFileStorageEngine<SerializableWrapper<int>, SerializableWrapper<int>>(
            new StorageFile(_dir, AppendOnlyFileName), serializer);
        (await reopened.GetAllItemsAsync()).Select(i => i.Key.Value).Should().Equal([1], "a restart must not replay a failed append");

        // The engine stays usable: the next append lands directly after the last acknowledged record.
        await engine.SetAsync(new SerializableWrapper<int>(4), new SerializableWrapper<int>(40));
        (await reopened.GetAllItemsAsync()).Select(i => i.Key.Value).Should().Equal([1, 4]);
    }

    /// <summary>
    /// When the truncation that removes a failed append itself fails, the failed bytes stay on
    /// disk for a moment, but this instance never serves them, and its next append removes them
    /// before writing.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AppendOnly_FailedAppendWhoseRemovalAlsoFails_IsHiddenAndRemovedByTheNextAppend(bool indexed)
    {
        var file = new FaultingStorageFile(new StorageFile(_dir, AppendOnlyFileName));
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<int>>();
        AppendOnlyFileStorageEngine<SerializableWrapper<int>, SerializableWrapper<int>> engine = indexed
            ? new AppendOnlyFileStorageEngineWithIndex<SerializableWrapper<int>, SerializableWrapper<int>>(
                file, serializer, new InMemoryFileIndex<SerializableWrapper<int>>())
            : new AppendOnlyFileStorageEngine<SerializableWrapper<int>, SerializableWrapper<int>>(file, serializer);
        await engine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<int>(10));

        file.Fault = AppendFault.FsyncFails;
        file.FailRepairOpen = true;
        var call = () => engine.SetAsync(new SerializableWrapper<int>(2), new SerializableWrapper<int>(20));
        await call.Should().ThrowAsync<IOException>();
        file.Fault = null;
        file.FailRepairOpen = false;

        (await engine.TryGetValueAsync(new SerializableWrapper<int>(2))).Found.Should().BeFalse("the failed bytes are hidden");
        (await engine.GetAllItemsAsync()).Select(i => i.Key.Value).Should().Equal([1]);

        await engine.SetAsync(new SerializableWrapper<int>(3), new SerializableWrapper<int>(30));

        var reopened = new AppendOnlyFileStorageEngine<SerializableWrapper<int>, SerializableWrapper<int>>(
            new StorageFile(_dir, AppendOnlyFileName), serializer);
        (await reopened.GetAllItemsAsync()).Select(i => i.Key.Value).Should().Equal([1, 3], "the next append removed the failed bytes");
    }

    /// <summary>
    /// Wraps a real <see cref="StorageFile"/>; while <see cref="Fault"/> is set, its append streams
    /// are real file streams that write their data and then fail the fsync, or cancel the caller's
    /// token as they flush.
    /// </summary>
    private sealed class FaultingStorageFile(StorageFile inner) : IStorageFile
    {
        public AppendFault? Fault { get; set; }

        public CancellationTokenSource? CancelOnFlush { get; set; }

        public bool FailRepairOpen { get; set; }

        public long FileSize => inner.FileSize;

        public string FileName => inner.FileName;

        public string FileLocation => inner.FileLocation;

        public void Create(FileExistenceHandling fileExistenceHandling) => inner.Create(fileExistenceHandling);

        public bool Exists() => inner.Exists();

        public Stream Open(FileMode mode)
        {
            if (mode == FileMode.Open && FailRepairOpen)
            {
                throw new IOException("injected failure opening the file for repair");
            }

            if (mode != FileMode.Append || Fault is null)
            {
                return inner.Open(mode);
            }

            inner.Close();
            return new FaultStream(Path.Combine(inner.FileLocation, inner.FileName), this);
        }

        public void Close() => inner.Close();

        public void Delete(FileDeletionHandling fileDeletionHandling) => inner.Delete(fileDeletionHandling);

        public byte[] ReadBytes(long offset, long count) => inner.ReadBytes(offset, count);

        public Task<byte[]> ReadBytesAsync(long offset, long count, CancellationToken cancellationToken = default)
            => inner.ReadBytesAsync(offset, count, cancellationToken);

        public byte[] ReadAllBytes() => inner.ReadAllBytes();

        public Task<byte[]> ReadAllBytesAsync(CancellationToken cancellationToken = default) => inner.ReadAllBytesAsync(cancellationToken);

        public string ReadAllText(Encoding encoding) => inner.ReadAllText(encoding);

        public Task<string> ReadAllTextAsync(Encoding encoding, CancellationToken cancellationToken = default)
            => inner.ReadAllTextAsync(encoding, cancellationToken);

        public void WriteAllBytes(byte[] content) => inner.WriteAllBytes(content);

        public Task WriteAllBytesAsync(byte[] content, CancellationToken cancellationToken = default)
            => inner.WriteAllBytesAsync(content, cancellationToken);

        public void WriteAllText(string content, Encoding encoding) => inner.WriteAllText(content, encoding);

        public Task WriteAllTextAsync(string content, Encoding encoding, CancellationToken cancellationToken = default)
            => inner.WriteAllTextAsync(content, encoding, cancellationToken);

        public void AppendAllBytes(byte[] content) => inner.AppendAllBytes(content);

        public Task AppendAllBytesAsync(byte[] content, CancellationToken cancellationToken = default)
            => inner.AppendAllBytesAsync(content, cancellationToken);

        public void AppendAllText(string content, Encoding encoding) => inner.AppendAllText(content, encoding);

        public Task AppendAllTextAsync(string content, Encoding encoding, CancellationToken cancellationToken = default)
            => inner.AppendAllTextAsync(content, encoding, cancellationToken);

        public void Dispose() => inner.Dispose();
    }

    private sealed class FaultStream(string path, FaultingStorageFile owner)
        : FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, useAsync: true)
    {
        public override void Flush(bool flushToDisk)
        {
            base.Flush(false);
            if (flushToDisk && owner.Fault == AppendFault.FsyncFails)
            {
                throw new IOException("injected fsync failure (EIO)");
            }
        }

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            // The frame bytes reach the file before the fault fires.
            await base.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            if (owner.Fault == AppendFault.CancelledAtFlush && owner.CancelOnFlush is { } source)
            {
                await source.CancelAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
    }

    // ── RaftNode identity: an id the durable vote store cannot persist ───────────

    /// <summary>
    /// A node id that is not well-formed UTF-16 (an unpaired surrogate) cannot be persisted as a
    /// vote by the file-backed term/vote store, so the node must refuse it at construction rather
    /// than fail inside a peer's tick at protocol time.
    /// </summary>
    /// <remarks>
    /// The ids are built at run time: an unpaired surrogate in an attribute argument is replaced
    /// by U+FFFD when the compiler encodes it into metadata, so inline theory data cannot carry it.
    /// </remarks>
    [Theory]
    [InlineData("n", 0xD800, "")]
    [InlineData("", 0xDC00, "node")]
    [InlineData("a", 0xDC00, "\uD83D\uDE00")]
    public void RaftNode_IllFormedUtf16NodeId_IsRefusedAtConstruction(string prefix, int lone, string suffix)
    {
        var nodeId = prefix + (char)lone + suffix;

        var act = () => new RaftNode<string>(nodeId);

        act.Should().Throw<ArgumentException>().Which.ParamName.Should().Be("nodeId");
    }

    /// <summary>
    /// A well-formed id with a surrogate pair is persistable and stays accepted.
    /// </summary>
    [Fact]
    public void RaftNode_WellFormedSurrogatePairNodeId_IsAccepted()
    {
        var node = new RaftNode<string>("node-😀");

        node.NodeId.Should().Be("node-😀");
    }
}
