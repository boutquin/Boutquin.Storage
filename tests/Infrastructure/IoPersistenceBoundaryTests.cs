// Copyright (c) 2024-2026 Pierre G. Boutquin. All rights reserved.
//
//   Licensed under the Apache License, Version 2.0 (the "License").
//   You may not use this file except in compliance with the License.
//   You may obtain a copy of the License at
//
//       http://www.apache.org/licenses/LICENSE-2.0
//
//   Unless required by applicable law or agreed to in writing, software
//   distributed under the License is distributed on an "AS IS" BASIS,
//   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//
//   See the License for the specific language governing permissions and
//   limitations under the License.
//

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Reflection;
using Boutquin.Storage.Domain.ValueObjects.LsmTree;
using Boutquin.Storage.Domain.ValueObjects.ValueLog;
using Boutquin.Storage.Infrastructure.ValueLog;
using Key = Boutquin.Storage.Domain.Helpers.SerializableWrapper<int>;
using Text = Boutquin.Storage.Domain.Helpers.SerializableWrapper<string>;

namespace Boutquin.Storage.Infrastructure.Tests;

public sealed class IoPersistenceBoundaryTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"io-boundaries-{Guid.NewGuid():N}");

    public IoPersistenceBoundaryTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private LsmStorageEngineOptions<Key, Text> Options() => new(
        MemTableCapacity: 16, SegmentFolder: _folder, SegmentPrefix: "seg",
        SegmentFormat: new SstSegmentFormat<Key, Text>());

    [Fact]
    public async Task CompactionPick_EqualMetadata_ConsumesSelectedMultiplicityOnly()
    {
        var strategy = new Mock<ICompactionStrategy<Key>>();
        var entry = new SegmentMetadata<Key>(0, new Key(1), new Key(2), 100, 42);
        strategy.Setup(x => x.SelectSegments(It.IsAny<IReadOnlyList<SegmentMetadata<Key>>>()))
            .Returns(new[] { entry });
        await using var engine = await LsmStorageEngine<Key, Text>.OpenAsync(Options() with { CompactionStrategy = strategy.Object });
        var method = typeof(LsmStorageEngine<Key, Text>).GetMethod("PickCompactionIndexes", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var selected = (List<int>)method.Invoke(engine, new object[] { false, new[] { entry, entry } })!;
        Assert.Single(selected);
    }

    [Fact]
    public void FreshTable_ReopensAsValidEmptyBeforeFirstWrite()
    {
        var first = new SortedStringTable<Key, Text>(_folder, "fresh.sst");
        Assert.True(first.FileSize > 0);
        var reopened = new SortedStringTable<Key, Text>(_folder, "fresh.sst");
        Assert.False(reopened.TryGetValue(new Key(1), out _));
    }

    [Fact]
    public void ExistingEmptySstable_IsRejectedWithoutOverwritingIt()
    {
        var path = Path.Combine(_folder, "empty.sst");
        File.WriteAllBytes(path, []);
        Assert.Throws<InvalidDataException>(() => new SortedStringTable<Key, Text>(_folder, "empty.sst"));
        Assert.Empty(File.ReadAllBytes(path));
    }

    [Fact]
    public async Task Compaction_CancelAfterManifestCommit_CompletesAndRetiresEveryInput()
    {
        await using var engine = await LsmStorageEngine<Key, Text>.OpenAsync(Options());
        await engine.SetAsync(new Key(1), new Text("first"));
        await engine.FlushAsync();
        await engine.SetAsync(new Key(2), new Text("second"));
        await engine.FlushAsync();
        using var cancellation = new CancellationTokenSource();
        engine.CompactionManifestCommittedHook = cancellation.Cancel;
        await engine.CompactAsync(cancellation.Token);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Single(Directory.GetFiles(_folder, "seg_*.sst"));
        Assert.Equal("first", (await engine.TryGetValueAsync(new Key(1))).Value.Value);
        Assert.Equal("second", (await engine.TryGetValueAsync(new Key(2))).Value.Value);
    }

    [Fact]
    public async Task Recovery_RemovesOnlyRecognizedInterruptedWriteTemps()
    {
        await using (var engine = await LsmStorageEngine<Key, Text>.OpenAsync(Options()))
        {
            await engine.SetAsync(new Key(1), new Text("survivor"));
            await engine.FlushAsync();
        }
        var temps = new[] { "seg_000041.sst.tmp", $"CURRENT.tmp.{Guid.NewGuid():N}" };
        var foreign = new[] { "other_000041.sst.tmp", "seg_notes.sst.tmp", "CURRENT.tmp.notes" };
        foreach (var name in temps.Concat(foreign))
        {
            File.WriteAllText(Path.Combine(_folder, name), "interrupted");
        }
        await using var reopened = await LsmStorageEngine<Key, Text>.OpenAsync(Options());
        Assert.Equal("survivor", (await reopened.TryGetValueAsync(new Key(1))).Value.Value);
        foreach (var name in temps)
        {
            Assert.False(File.Exists(Path.Combine(_folder, name)), name);
        }
        foreach (var name in foreign)
        {
            Assert.True(File.Exists(Path.Combine(_folder, name)), name);
        }
    }

    [Fact]
    public async Task Wal_ChecksumValidPayloadWithTrailingBytes_IsRejectedAndPreserved()
    {
        using (var writer = new WriteAheadLog<Key, Text>(_folder))
        {
            await writer.AppendAsync(new Key(1), new Text("value"), 1);
        }
        var path = Path.Combine(_folder, "wal_000001.log");
        var bytes = File.ReadAllBytes(path);
        const int frameStart = 8, fields = 14, header = 18;
        var length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(frameStart, 4));
        var changed = new byte[bytes.Length + 1];
        bytes.AsSpan(0, frameStart + header + length).CopyTo(changed);
        changed[frameStart + header + length] = 0x42;
        BinaryPrimitives.WriteInt32LittleEndian(changed.AsSpan(frameStart, 4), length + 1);
        BinaryPrimitives.WriteUInt32LittleEndian(changed.AsSpan(frameStart + fields, 4), Crc32C.HashToUInt32(changed.AsSpan(frameStart, fields)));
        BinaryPrimitives.WriteUInt32LittleEndian(changed.AsSpan(frameStart + header + length + 1, 4), Crc32C.HashToUInt32(changed.AsSpan(frameStart + header, length + 1)));
        File.WriteAllBytes(path, changed);
        using var reader = new WriteAheadLog<Key, Text>(_folder);
        await Assert.ThrowsAsync<WriteAheadLogCorruptException>(() => reader.RecoverAsync());
        Assert.Equal(changed, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task ValueLog_ReservedFrameCannotBeDereferencedAsPayload()
    {
        var options = ValueLogOptions.Default;
        await using var manifest = new ValueLogManifest(Path.Combine(_folder, "values.manifest"));
        ValueLogPointer pointer;
        await using (var writer = await ValueLogWriter.OpenAsync(_folder, manifest, options))
        {
            pointer = await writer.AppendAsync(new byte[] { 1, 2, 3 }, 7);
        }
        var path = ValueLogWriter.ResolveSegmentPath(_folder, pointer.FileIndex);
        var bytes = File.ReadAllBytes(path);
        var header = bytes.AsSpan((int)pointer.Offset, ValueLogFrame.EntryHeaderSize);
        header[ValueLogFrame.KindOffset] = ValueLogFrame.KindReserved;
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(ValueLogFrame.HeaderCrcOffset, 4), ValueLogFrame.ComputeHeaderCrc(header));
        File.WriteAllBytes(path, bytes);
        using var reader = new ValueLogReader(_folder, options);
        Assert.Throws<CorruptValueLogPointerException>(() => reader.Read(pointer));
    }

    [Theory]
    [InlineData(0x7FFF_FFFF_FFFF_FFFEUL)]
    [InlineData(0x7FFF_FFFF_FFFF_FFFFUL)]
    [InlineData(0xFFFF_FFFF_FFFF_FFFFUL)]
    public async Task ValueLog_ForgedOffsetNearSignedLimit_ReportsTypedCorruptPointer(ulong offset)
    {
        await using var manifest = new ValueLogManifest(Path.Combine(_folder, "values.manifest"));
        await using var writer = await ValueLogWriter.OpenAsync(_folder, manifest, ValueLogOptions.Default);
        var pointer = await writer.AppendAsync(new byte[] { 1, 2, 3 }, 7);
        using var reader = new ValueLogReader(_folder, ValueLogOptions.Default);
        Assert.Throws<CorruptValueLogPointerException>(() => reader.Read(new ValueLogPointer(pointer.FileIndex, offset, 7)));
    }

    [Fact]
    public async Task ValueLog_DisposeRacingColdRead_DoesNotReopenOrLeakHandles()
    {
        await using var manifest = new ValueLogManifest(Path.Combine(_folder, "values.manifest"));
        await using var writer = await ValueLogWriter.OpenAsync(_folder, manifest, ValueLogOptions.Default);
        var pointer = await writer.AppendAsync(new byte[] { 1, 2, 3 }, 7);
        using var reader = new ValueLogReader(_folder, ValueLogOptions.Default);
        var gate = (Lock)typeof(ValueLogReader).GetField("_openLock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(reader)!;
        Exception? error = null;
        var thread = new Thread(() => { try { reader.Read(pointer); } catch (Exception ex) { error = ex; } });
        lock (gate)
        {
            thread.Start();
            Assert.True(SpinWait.SpinUntil(() => (thread.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(10)));
            reader.Dispose();
        }
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        Assert.IsType<ObjectDisposedException>(error);
        var handles = (ConcurrentDictionary<uint, Microsoft.Win32.SafeHandles.SafeFileHandle>)typeof(ValueLogReader)
            .GetField("_handles", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(reader)!;
        Assert.Empty(handles);
        Assert.Throws<ObjectDisposedException>(() => reader.Read(pointer));
    }

    [Fact]
    public async Task IndexedRead_DuringPendingAppend_WaitsForCommittedIndex()
    {
        var storage = new StorageFile(_folder, "indexed.log");
        var engine = new AppendOnlyFileStorageEngineWithIndex<Key, Text>(storage,
            new BinaryEntrySerializer<Key, Text>(), new InMemoryFileIndex<Key>());
        await engine.SetAsync(new Key(1), new Text("old"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.BeforeAppendHook = () => { entered.SetResult(); return release.Task; };
        var write = engine.SetAsync(new Key(1), new Text("new"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var read = engine.TryGetValueAsync(new Key(1));
        try
        {
            Assert.False(read.IsCompleted, "a read must not run a stale index rebuild while the append is pending");
        }
        finally
        {
            release.SetResult();
            await write;
        }
        Assert.Equal("new", (await read).Value.Value);
    }

    [Fact]
    public async Task IndexedRebuild_OldSnapshotCannotOverwriteAcknowledgedAppend()
    {
        var backing = new InMemoryFileIndex<Key>();
        var index = new Mock<IFileStorageIndex<Key>>();
        var staleWriteEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStaleWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arm = false;
        index.Setup(i => i.SetAsync(It.IsAny<Key>(), It.IsAny<FileLocation>(), It.IsAny<CancellationToken>()))
            .Returns(async (Key key, FileLocation location, CancellationToken token) =>
            {
                if (arm && location.Offset == 0)
                {
                    staleWriteEntered.TrySetResult();
                    await releaseStaleWrite.Task;
                }
                await backing.SetAsync(key, location, token);
            });
        index.Setup(i => i.TryGetValueAsync(It.IsAny<Key>(), It.IsAny<CancellationToken>()))
            .Returns((Key key, CancellationToken token) => backing.TryGetValueAsync(key, token));
        var engine = new AppendOnlyFileStorageEngineWithIndex<Key, Text>(new StorageFile(_folder, "overlap.log"),
            new BinaryEntrySerializer<Key, Text>(), index.Object);
        await engine.SetAsync(new Key(1), new Text("old"));
        arm = true;
        var appendEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAppend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.BeforeAppendHook = () => { appendEntered.SetResult(); return releaseAppend.Task; };
        var write = engine.SetAsync(new Key(1), new Text("new"));
        await appendEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var read = engine.TryGetValueAsync(new Key(1));
        // The unfixed reader starts recovery from old log bytes. The fixed reader waits at
        // the publication gate; the short timeout merely chooses which schedule to release.
        await Task.WhenAny(staleWriteEntered.Task, Task.Delay(250));
        releaseAppend.SetResult();
        await write.WaitAsync(TimeSpan.FromSeconds(10));
        releaseStaleWrite.SetResult();
        await read.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("new", (await engine.TryGetValueAsync(new Key(1))).Value.Value);
    }

    [Fact]
    public async Task IndexedCompaction_CancelAfterSwap_CompletesIndexPublication()
    {
        using var source = new CancellationTokenSource();
        var backing = new InMemoryFileIndex<Key>();
        var index = new Mock<IFileStorageIndex<Key>>();
        index.Setup(i => i.SetAsync(It.IsAny<Key>(), It.IsAny<FileLocation>(), It.IsAny<CancellationToken>()))
            .Returns((Key key, FileLocation location, CancellationToken token) => backing.SetAsync(key, location, token));
        index.Setup(i => i.TryGetValueAsync(It.IsAny<Key>(), It.IsAny<CancellationToken>()))
            .Returns((Key key, CancellationToken token) => backing.TryGetValueAsync(key, token));
        index.Setup(i => i.ClearAsync(It.IsAny<CancellationToken>())).Returns((CancellationToken token) =>
        {
            source.Cancel();
            return backing.ClearAsync(token);
        });
        var engine = new AppendOnlyFileStorageEngineWithIndex<Key, Text>(new StorageFile(_folder, "indexed.log"),
            new BinaryEntrySerializer<Key, Text>(), index.Object);
        await engine.SetAsync(new Key(1), new Text("old"));
        await engine.SetAsync(new Key(2), new Text("other"));
        await engine.SetAsync(new Key(1), new Text("new"));
        await engine.CompactAsync(source.Token);
        Assert.True(source.IsCancellationRequested);
        Assert.Equal("new", (await engine.TryGetValueAsync(new Key(1))).Value.Value);
        Assert.Equal("other", (await engine.TryGetValueAsync(new Key(2))).Value.Value);
    }
}
