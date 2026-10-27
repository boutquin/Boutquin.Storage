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
/// AC-5.5 characterization tests for <see cref="LogSegmentedStorageEngine{TKey, TValue}"/>'s bulk
/// path: a bulk write of N items must serialize each item EXACTLY ONCE (not once to measure size and
/// a second time to write it), and the small-segment merge must partition segments in O(n) via a
/// <see cref="HashSet{T}"/> rather than an O(n²) <c>Contains</c>-over-a-list scan — while still
/// producing the correct last-writer-wins result.
/// </summary>
public sealed class LogSegmentedStorageEngineBulkSerializeTests : IDisposable
{
    private readonly string _testFolderPath = Path.Combine(Path.GetTempPath(), $"BulkSerialize_{Guid.NewGuid():N}");
    private readonly string _testFilePrefix = "BulkSeg";

    private LogSegmentedStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>> NewEngine(
        CountingEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>> serializer,
        long maxSegmentSize)
    {
        static IFileBasedStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>> Factory(
            string fileLocation, string fileName,
            IEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>> s, long maxSize)
            => new AppendOnlyFileStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
                new StorageFile(fileLocation, fileName), s);

        return new LogSegmentedStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            serializer, _testFolderPath, _testFilePrefix, maxSegmentSize, Factory);
    }

    /// <summary>
    /// AC-5.5 — N bulk items ⇒ N serializations. The pre-fix path called <c>CalculateSizeAsync</c>
    /// (serialize #1, throwaway) and then <c>SetAsync</c> (serialize #2, the write) for every item, so
    /// the count was 2N. Eliminating <c>CalculateSizeAsync</c> in favour of the segment's
    /// bytes-written drops it to N.
    /// </summary>
    [Fact]
    public async Task SetBulkAsync_SerializesEachItemExactlyOnce()
    {
        var serializer = new CountingEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>(
            new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>());
        // Large segment so no rollover splits the batch (rollover correctness is covered elsewhere).
        var engine = NewEngine(serializer, maxSegmentSize: 1024 * 1024);

        const int n = 50;
        var batch = new List<KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>>(n);
        for (var i = 0; i < n; i++)
        {
            batch.Add(new(new SerializableWrapper<int>(i), new SerializableWrapper<string>($"v{i}")));
        }

        await engine.SetBulkAsync(batch);

        // Exactly one serialize per item — no double-serialize. (2N == 100 would be the pre-fix count.)
        Assert.Equal(n, serializer.WriteCount);
    }

    /// <summary>
    /// AC-5.5 — the small-segment merge produces the correct last-writer-wins result and collapses the
    /// segment count, exercising the <see cref="HashSet{T}"/>-based retained/merged partition. Many
    /// small segments (tiny max size forces frequent rollover) followed by a compaction must leave one
    /// value per key, equal to the latest written.
    /// </summary>
    [Fact]
    public async Task CompactAsync_MergesSmallSegments_KeepsLatestValuePerKey()
    {
        var serializer = new CountingEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>(
            new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>());
        // Tiny segments so many small segments accumulate, driving the merge partition.
        var engine = NewEngine(serializer, maxSegmentSize: 32);

        // Write each of 10 keys twice; the second write is the surviving value.
        for (var pass = 0; pass < 2; pass++)
        {
            for (var k = 0; k < 10; k++)
            {
                await engine.SetAsync(new SerializableWrapper<int>(k), new SerializableWrapper<string>($"p{pass}-k{k}"));
            }
        }

        await engine.CompactAsync();

        for (var k = 0; k < 10; k++)
        {
            var (value, found) = await engine.TryGetValueAsync(new SerializableWrapper<int>(k));
            Assert.True(found, $"key {k} must survive compaction");
            Assert.Equal($"p1-k{k}", value.Value);
        }
    }

    /// <summary>
    /// AC-5.4 (characterization) — a bulk write of N items must not stat the segment file once per
    /// item. The segment engine reads <c>_currentSegment.FileSize</c> while tracking the running
    /// segment size; with the cached size on both <see cref="StorageFile"/> and the append-only engine,
    /// those reads are served from memory, so the number of underlying-file <c>FileSize</c> stats stays
    /// O(1) in the batch (bounded by the segment count, not the item count). The pre-fix code's
    /// per-item <c>FileInfo().Length</c> getter made it O(N).
    /// <para>
    /// Single-writer note (DR-9): this exercises the engine's documented single-mutator path — the
    /// caller serialises its own bulk writes. The counter is read after the awaited call completes.
    /// </para>
    /// </summary>
    [Fact]
    public async Task SetBulkAsync_DoesNotStatPerItem()
    {
        var statCount = 0;
        IFileBasedStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>> CountingFactory(
            string fileLocation, string fileName,
            IEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>> s, long maxSize)
            => new AppendOnlyFileStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
                new StatCountingStorageFile(new StorageFile(fileLocation, fileName), () => Interlocked.Increment(ref statCount)),
                s);

        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        // Large segment so the whole batch lands in ONE segment (segment count == 1).
        var engine = new LogSegmentedStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            serializer, _testFolderPath, _testFilePrefix, maxSegmentSize: 1024 * 1024, CountingFactory);

        const int n = 100;
        var batch = new List<KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>>(n);
        for (var i = 0; i < n; i++)
        {
            batch.Add(new(new SerializableWrapper<int>(i), new SerializableWrapper<string>($"v{i}")));
        }

        statCount = 0; // ignore any construction-time stat
        await engine.SetBulkAsync(batch);

        // Far fewer than N=100 underlying stats — the cached counter serves the per-item size reads.
        // A small constant (the lazy seed plus the cache's stay-stale resync after the Append-mode
        // Open) is acceptable; the pre-fix path stat'd per item (≈N).
        Assert.True(statCount <= 4, $"expected O(1) stats for a one-segment bulk write, got {statCount}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_testFolderPath))
        {
            Directory.Delete(_testFolderPath, recursive: true);
        }
    }

    /// <summary>
    /// Wraps a real <see cref="StorageFile"/> and invokes a callback every time <c>FileSize</c> is
    /// read, so the bulk path's per-item stat behaviour is observable. Every member delegates.
    /// </summary>
    private sealed class StatCountingStorageFile(IStorageFile inner, Action onFileSizeRead) : IStorageFile
    {
        public long FileSize
        {
            get
            {
                onFileSizeRead();
                return inner.FileSize;
            }
        }

        public string FileName => inner.FileName;
        public string FileLocation => inner.FileLocation;
        public string FilePath => inner.FilePath;
        public void Create(FileExistenceHandling existenceHandling) => inner.Create(existenceHandling);
        public bool Exists() => inner.Exists();
        public Stream Open(FileMode mode) => inner.Open(mode);
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

    /// <summary>
    /// Counts serialization calls so the "exactly once per bulk item" property is observable.
    /// Delegates all real work to an inner serializer.
    /// </summary>
    private sealed class CountingEntrySerializer<TKey, TValue>(IEntrySerializer<TKey, TValue> inner)
        : IEntrySerializer<TKey, TValue>
        where TKey : IStorageKey<TKey>, new()
        where TValue : ISerializable<TValue>, new()
    {
        private int _writeCount;
        public int WriteCount => Volatile.Read(ref _writeCount);

        public void WriteEntry(Stream stream, TKey key, TValue value)
        {
            Interlocked.Increment(ref _writeCount);
            inner.WriteEntry(stream, key, value);
        }

        public Task WriteEntryAsync(Stream stream, TKey key, TValue value, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _writeCount);
            return inner.WriteEntryAsync(stream, key, value, cancellationToken);
        }

        public (TKey Key, TValue Value)? ReadEntry(Stream stream) => inner.ReadEntry(stream);

        public bool CanRead(Stream stream) => inner.CanRead(stream);
    }
}
