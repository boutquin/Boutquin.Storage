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
#nullable disable

namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// Torn-tail-vs-mid-file discrimination tests for
/// <see cref="AppendOnlyFileStorageEngine{TKey, TValue}"/>. A crash mid-append leaves an
/// incomplete LAST frame (torn tail) that recovery discards cleanly; on-disk corruption of an
/// INTERIOR record — with intact, durably-acknowledged records still ahead of it — must fail
/// loud, never silently swallow the post-corruption records as if they were a torn tail.
/// </summary>
public sealed class AppendOnlyFileStorageEngineTornTailTests : IDisposable
{
    private readonly string _testDirectory = Path.Combine(Path.GetTempPath(), $"TornTailTests_{Guid.NewGuid():N}");
    private readonly string _testFileName = "torntail.dat";
    private string TestFilePath => Path.Combine(_testDirectory, _testFileName);

    private AppendOnlyFileStorageEngine<SerializableWrapper<int>, SerializableWrapper<int>> NewEngine()
    {
        var storageFile = new StorageFile(_testDirectory, _testFileName);
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<int>>();
        return new AppendOnlyFileStorageEngine<SerializableWrapper<int>, SerializableWrapper<int>>(storageFile, serializer);
    }

    private async Task WriteThreeRecordsAsync()
    {
        var engine = NewEngine();
        await engine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<int>(100));
        await engine.SetAsync(new SerializableWrapper<int>(2), new SerializableWrapper<int>(200));
        await engine.SetAsync(new SerializableWrapper<int>(3), new SerializableWrapper<int>(300));
    }

    /// <summary>
    /// Computes the byte offset of each record boundary by walking the clean file's frame headers,
    /// so the corruption probe targets the right record.
    /// </summary>
    private List<long> RecordBoundaries() => AppendOnlyFrames.Boundaries(File.ReadAllBytes(TestFilePath));

    /// <summary>
    /// Mid-file corruption: overwrite the leading bytes of record 2's frame header, while record 3
    /// remains an intact, durably-written frame AFTER the damage. Replay must FAIL LOUD — records 2
    /// and 3 must never be silently dropped, because a subsequent compaction would then permanently
    /// erase them.
    /// </summary>
    [Fact]
    public async Task GetAllItemsAsync_MidFileLengthPrefixCorruption_FailsLoud()
    {
        await WriteThreeRecordsAsync();
        var boundaries = RecordBoundaries();
        Assert.Equal(4, boundaries.Count); // 3 records => boundaries [0, b1, b2, end]

        // Overwrite the start of record 2's header with FF FF FF FF 07; its header checksum fails.
        var data = File.ReadAllBytes(TestFilePath);
        var record2Start = (int)boundaries[1];
        byte[] corruptPrefix = { 0xFF, 0xFF, 0xFF, 0xFF, 0x07 };
        Array.Copy(corruptPrefix, 0, data, record2Start, corruptPrefix.Length);
        File.WriteAllBytes(TestFilePath, data);

        var engine = NewEngine();

        // Record 3 (key=3) is intact AFTER the damage. The engine must NOT silently swallow it.
        await Assert.ThrowsAsync<InvalidDataException>(() => engine.GetAllItemsAsync());
    }

    /// <summary>
    /// Genuine torn tail: truncate the file mid-way through the LAST record (crash mid-append).
    /// The two complete records before it are durable; recovery must return them cleanly with no
    /// exception.
    /// </summary>
    [Fact]
    public async Task GetAllItemsAsync_TruncatedLastFrame_RecoversCleanly()
    {
        await WriteThreeRecordsAsync();
        var boundaries = RecordBoundaries();
        Assert.Equal(4, boundaries.Count);

        // Truncate one byte into record 3 — a partial final frame, the torn-tail shape.
        var data = File.ReadAllBytes(TestFilePath);
        var record3Start = (int)boundaries[2];
        var truncated = data[..(record3Start + 1)];
        File.WriteAllBytes(TestFilePath, truncated);

        var engine = NewEngine();
        var items = (await engine.GetAllItemsAsync()).ToList();

        // The two complete records survive; the torn final frame is discarded, no throw.
        Assert.Equal(2, items.Count);
        Assert.Contains(items, e => e.Key.Value == 1 && e.Value.Value == 100);
        Assert.Contains(items, e => e.Key.Value == 2 && e.Value.Value == 200);
    }

    /// <summary>The append entry points that must repair a torn tail before writing.</summary>
    public enum AppendPath
    {
        BaseSet,
        BaseSetBulk,
        IndexedSet,
        IndexedSetBulk,
    }

    private IFileBasedStorageEngine<SerializableWrapper<int>, SerializableWrapper<int>> NewEngine(AppendPath path)
    {
        var storageFile = new StorageFile(_testDirectory, _testFileName);
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<int>>();
        return path is AppendPath.IndexedSet or AppendPath.IndexedSetBulk
            ? new AppendOnlyFileStorageEngineWithIndex<SerializableWrapper<int>, SerializableWrapper<int>>(
                storageFile, serializer, new InMemoryFileIndex<SerializableWrapper<int>>())
            : new AppendOnlyFileStorageEngine<SerializableWrapper<int>, SerializableWrapper<int>>(storageFile, serializer);
    }

    private static Task AppendAsync(
        IFileBasedStorageEngine<SerializableWrapper<int>, SerializableWrapper<int>> engine, AppendPath path, int key, int value)
        => path is AppendPath.BaseSetBulk or AppendPath.IndexedSetBulk
            ? engine.SetBulkAsync([new KeyValuePair<SerializableWrapper<int>, SerializableWrapper<int>>(
                new SerializableWrapper<int>(key), new SerializableWrapper<int>(value))])
            : engine.SetAsync(new SerializableWrapper<int>(key), new SerializableWrapper<int>(value));

    /// <summary>
    /// A crash mid-append leaves a torn final frame. The first append after restart must repair it
    /// (truncate to the last clean record boundary) BEFORE writing; otherwise the new, acknowledged
    /// record lands after the garbage, the torn frame is reclassified as mid-file corruption and
    /// every read of the store — including of the new key — throws.
    /// </summary>
    [Theory]
    [InlineData(AppendPath.BaseSet)]
    [InlineData(AppendPath.BaseSetBulk)]
    [InlineData(AppendPath.IndexedSet)]
    [InlineData(AppendPath.IndexedSetBulk)]
    public async Task AppendAfterTornTail_RepairsTailFirst_AllRecordsReadable(AppendPath path)
    {
        await WriteThreeRecordsAsync();
        var boundaries = RecordBoundaries();
        var data = File.ReadAllBytes(TestFilePath);
        File.WriteAllBytes(TestFilePath, data[..((int)boundaries[2] + 1)]);

        var engine = NewEngine(path);
        await AppendAsync(engine, path, 4, 400);

        var readFault = await Record.ExceptionAsync(() => engine.GetAllItemsAsync());
        Assert.Null(readFault);
        var items = (await engine.GetAllItemsAsync()).ToList();
        Assert.Equal(3, items.Count);
        Assert.Contains(items, e => e.Key.Value == 1 && e.Value.Value == 100);
        Assert.Contains(items, e => e.Key.Value == 2 && e.Value.Value == 200);
        Assert.Contains(items, e => e.Key.Value == 4 && e.Value.Value == 400);

        var (v4, found4) = await engine.TryGetValueAsync(new SerializableWrapper<int>(4));
        Assert.True(found4);
        Assert.Equal(400, v4.Value);
        Assert.Equal(new FileInfo(TestFilePath).Length, engine.FileSize);
    }

    /// <summary>
    /// Delegating storage file whose next append stream, once armed, writes the first half of the
    /// frame it is given and then throws, leaving a torn frame at EOF exactly as an I/O error
    /// mid-append does. Every other member delegates to a real <see cref="StorageFile"/>.
    /// </summary>
    private sealed class TearingStorageFile(IStorageFile inner) : IStorageFile
    {
        public bool TearNextAppend { get; set; }

        public Stream Open(FileMode mode)
        {
            var stream = inner.Open(mode);
            if (mode != FileMode.Append || !TearNextAppend)
            {
                return stream;
            }

            TearNextAppend = false;
            return new TearingStream(stream);
        }

        public long FileSize => inner.FileSize;
        public string FileName => inner.FileName;
        public string FileLocation => inner.FileLocation;
        public string FilePath => inner.FilePath;
        public void Create(FileExistenceHandling existenceHandling) => inner.Create(existenceHandling);
        public bool Exists() => inner.Exists();
        public void Close() => inner.Close();
        public void Delete(FileDeletionHandling deletionHandling) => inner.Delete(deletionHandling);
        public byte[] ReadBytes(long offset, long count) => inner.ReadBytes(offset, count);
        public Task<byte[]> ReadBytesAsync(long offset, long count, CancellationToken cancellationToken = default)
            => inner.ReadBytesAsync(offset, count, cancellationToken);
        public byte[] ReadAllBytes() => inner.ReadAllBytes();
        public Task<byte[]> ReadAllBytesAsync(CancellationToken cancellationToken = default)
            => inner.ReadAllBytesAsync(cancellationToken);
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

    /// <summary>Writes half of the first buffer it receives to the real stream, then throws.</summary>
    private sealed class TearingStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count / 2);
            inner.Flush();
            throw new IOException("simulated device error mid-append");
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await inner.WriteAsync(buffer[..(buffer.Length / 2)], cancellationToken).ConfigureAwait(true);
            await inner.FlushAsync(cancellationToken).ConfigureAwait(true);
            throw new IOException("simulated device error mid-append");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// An append that fails part-way leaves its partial bytes at EOF. The next append through the
    /// SAME instance must repair that tail before writing — not only the first append after open —
    /// or the retried write buries the torn frame mid-file and the store becomes unreadable.
    /// </summary>
    [Theory]
    [InlineData(AppendPath.BaseSet)]
    [InlineData(AppendPath.BaseSetBulk)]
    [InlineData(AppendPath.IndexedSet)]
    [InlineData(AppendPath.IndexedSetBulk)]
    public async Task AppendAfterFailedAppend_RepairsPartialRecord_AllRecordsReadable(AppendPath path)
    {
        var storageFile = new TearingStorageFile(new StorageFile(_testDirectory, _testFileName));
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<int>>();
        IFileBasedStorageEngine<SerializableWrapper<int>, SerializableWrapper<int>> engine =
            path is AppendPath.IndexedSet or AppendPath.IndexedSetBulk
                ? new AppendOnlyFileStorageEngineWithIndex<SerializableWrapper<int>, SerializableWrapper<int>>(
                    storageFile, serializer, new InMemoryFileIndex<SerializableWrapper<int>>())
                : new AppendOnlyFileStorageEngine<SerializableWrapper<int>, SerializableWrapper<int>>(storageFile, serializer);

        await AppendAsync(engine, path, 1, 100);
        storageFile.TearNextAppend = true;
        await Assert.ThrowsAsync<IOException>(() => AppendAsync(engine, path, 2, 200));
        await AppendAsync(engine, path, 3, 300);

        var readFault = await Record.ExceptionAsync(() => engine.GetAllItemsAsync());
        Assert.Null(readFault);
        var items = (await engine.GetAllItemsAsync()).ToList();
        Assert.Equal(2, items.Count);
        Assert.Contains(items, e => e.Key.Value == 1 && e.Value.Value == 100);
        Assert.Contains(items, e => e.Key.Value == 3 && e.Value.Value == 300);

        var (v3, found3) = await engine.TryGetValueAsync(new SerializableWrapper<int>(3));
        Assert.True(found3);
        Assert.Equal(300, v3.Value);
        Assert.Equal(new FileInfo(TestFilePath).Length, engine.FileSize);
    }

    /// <summary>
    /// A torn final record whose payload bytes happen to contain a complete, checksum-valid frame
    /// (a stored blob carrying a serialised log image) is still a torn tail: its own header
    /// validates and its payload is short at end-of-file. Recovery must return the durable prefix
    /// and the next append must succeed, rather than reading the embedded frame as a following
    /// record.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TornFinalRecordEmbeddingAParseableRecord_RecoversPrefixAndAcceptsAppends(bool indexed)
    {
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        var embedded = AppendOnlyFrames.Encode(serializer, new SerializableWrapper<int>(7), new SerializableWrapper<string>("hi"));
        var victimPayload = "AAAA"u8.ToArray().Concat(embedded).Concat(Enumerable.Repeat((byte)'B', 64)).ToArray();
        var victim = new MemoryStream(AppendOnlyFrames.Encode(victimPayload));

        IFileBasedStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>> NewStringEngine()
        {
            var storageFile = new StorageFile(_testDirectory, _testFileName);
            return indexed
                ? new AppendOnlyFileStorageEngineWithIndex<SerializableWrapper<int>, SerializableWrapper<string>>(
                    storageFile, serializer, new InMemoryFileIndex<SerializableWrapper<int>>())
                : new AppendOnlyFileStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(storageFile, serializer);
        }

        await NewStringEngine().SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("value-one"));
        var durableLength = new FileInfo(TestFilePath).Length;

        // Crash mid-append: the victim frame reached disk only up to 32 bytes before its end,
        // a cut inside the trailing 'B' run, after the embedded frame.
        await using (var fs = new FileStream(TestFilePath, FileMode.Append, FileAccess.Write))
        {
            fs.Write(victim.ToArray().AsSpan(0, (int)victim.Length - 32));
        }

        var engine = NewStringEngine();
        var readFault = await Record.ExceptionAsync(() => engine.GetAllItemsAsync());
        Assert.Null(readFault);
        var recovered = (await engine.GetAllItemsAsync()).Select(e => e.Key.Value).ToList();
        Assert.Equal([1], recovered);

        await engine.SetAsync(new SerializableWrapper<int>(3), new SerializableWrapper<string>("value-three"));
        var after = (await engine.GetAllItemsAsync()).Select(e => e.Key.Value).ToList();
        Assert.Equal([1, 3], after);
        Assert.True(new FileInfo(TestFilePath).Length > durableLength);
    }

    /// <summary>
    /// Fixed-width little-endian entry serializer: key 0 with value 0 encodes as eight zero bytes,
    /// the payload an entry serializer is free to emit, so nothing but zeros follows its header.
    /// </summary>
    private sealed class FixedWidthIntSerializer : IEntrySerializer<SerializableWrapper<int>, SerializableWrapper<int>>
    {
        public void WriteEntry(Stream stream, SerializableWrapper<int> key, SerializableWrapper<int> value)
        {
            Span<byte> bytes = stackalloc byte[8];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes, key.Value);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes[4..], value.Value);
            stream.Write(bytes);
        }

        public Task WriteEntryAsync(
            Stream stream, SerializableWrapper<int> key, SerializableWrapper<int> value, CancellationToken cancellationToken = default)
        {
            WriteEntry(stream, key, value);
            return Task.CompletedTask;
        }

        public (SerializableWrapper<int> Key, SerializableWrapper<int> Value)? ReadEntry(Stream stream)
        {
            Span<byte> bytes = stackalloc byte[8];
            stream.ReadExactly(bytes);
            return (new SerializableWrapper<int>(System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes)),
                new SerializableWrapper<int>(System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes[4..])));
        }

        public bool CanRead(Stream stream) => stream.Length - stream.Position >= 8;
    }

    private IFileBasedStorageEngine<SerializableWrapper<int>, SerializableWrapper<int>> NewFixedWidthEngine(bool indexed)
    {
        var storageFile = new StorageFile(_testDirectory, _testFileName);
        var serializer = new FixedWidthIntSerializer();
        return indexed
            ? new AppendOnlyFileStorageEngineWithIndex<SerializableWrapper<int>, SerializableWrapper<int>>(
                storageFile, serializer, new InMemoryFileIndex<SerializableWrapper<int>>())
            : new AppendOnlyFileStorageEngine<SerializableWrapper<int>, SerializableWrapper<int>>(storageFile, serializer);
    }

    /// <summary>Writes record (1, 100) then a final record whose payload is all zeros; returns the final frame's start.</summary>
    private async Task<int> WriteRecordThenZeroPayloadRecordAsync(bool indexed)
    {
        var engine = NewFixedWidthEngine(indexed);
        await engine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<int>(100));
        var finalStart = (int)new FileInfo(TestFilePath).Length;
        await engine.SetAsync(new SerializableWrapper<int>(0), new SerializableWrapper<int>(0));
        return finalStart;
    }

    /// <summary>
    /// The final record's header fully landed — its last byte, the header checksum's last byte, is
    /// non-zero — yet fails its checksum. An interrupted append leaves only a strict header prefix,
    /// so this is damage to an fsync'd, acknowledged record: every read and the next append must
    /// fail loud and the file must not be truncated, even though only zeros follow the header.
    /// </summary>
    [Theory]
    [InlineData(false, 5)]
    [InlineData(false, AppendOnlyFrames.HeaderSize - 1)]
    [InlineData(true, 9)]
    [InlineData(true, AppendOnlyFrames.HeaderSize - 1)]
    public async Task CompleteFinalHeaderFailingItsChecksum_BeforeAZeroPayload_FailsLoudAndNeverTruncates(bool indexed, int damagedByte)
    {
        var finalStart = await WriteRecordThenZeroPayloadRecordAsync(indexed);
        var data = File.ReadAllBytes(TestFilePath);
        Assert.NotEqual(0, data[finalStart + AppendOnlyFrames.HeaderSize - 1]);

        // The frame ends in its non-zero end marker; zero it so only zeros follow the header, the
        // image this test pins (a complete header is damage even when only zeros follow it).
        Assert.Equal(AppendOnlyFrames.EndMarker, data[^1]);
        data[^1] = 0;
        Assert.True(data.AsSpan(finalStart + AppendOnlyFrames.HeaderSize).IndexOfAnyExcept((byte)0) < 0);
        data[finalStart + damagedByte] ^= 0x01;
        File.WriteAllBytes(TestFilePath, data);

        var engine = NewFixedWidthEngine(indexed);
        await Assert.ThrowsAsync<InvalidDataException>(() => engine.GetAllItemsAsync());
        await Assert.ThrowsAsync<InvalidDataException>(
            () => engine.SetAsync(new SerializableWrapper<int>(2), new SerializableWrapper<int>(200)));
        Assert.Equal(data, File.ReadAllBytes(TestFilePath));
    }

    /// <summary>
    /// Must-recover counterpart: the same zero-payload record torn inside its header (a strict
    /// prefix, so the last header byte is zero) is an interrupted append and is dropped.
    /// </summary>
    [Theory]
    [InlineData(false, 9)]
    [InlineData(false, AppendOnlyFrames.HeaderSize - 1)]
    [InlineData(true, AppendOnlyFrames.HeaderSize - 1)]
    public async Task HeaderPrefixOfAZeroPayloadRecord_IsATornTail(bool indexed, int landedBytes)
    {
        var finalStart = await WriteRecordThenZeroPayloadRecordAsync(indexed);
        var data = File.ReadAllBytes(TestFilePath);
        // An interrupted append leaves nothing after the landed prefix: the rest of the header, the
        // zero payload and the end marker are all zeros.
        Array.Clear(data, finalStart + landedBytes, data.Length - finalStart - landedBytes);
        File.WriteAllBytes(TestFilePath, data);

        var engine = NewFixedWidthEngine(indexed);
        var items = (await engine.GetAllItemsAsync()).ToList();
        Assert.Equal([1], items.Select(e => e.Key.Value));
        await engine.SetAsync(new SerializableWrapper<int>(2), new SerializableWrapper<int>(200));
        Assert.Equal([1, 2], (await engine.GetAllItemsAsync()).Select(e => e.Key.Value));
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }
}
