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
/// Regression tests for cancellation at the durable-mutation / index-update boundary in
/// <see cref="AppendOnlyFileStorageEngineWithIndex{TKey,TValue}"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>F-GAP-APPEND-01.</b> <c>CompactAsync</c> durably replaces the data file with the compacted
/// layout, then clears and rebuilds the in-memory index. <see cref="Indexing.InMemoryStorageIndex{TKey,TValue}"/>'s
/// <c>ClearAsync</c> checks the cancellation token <em>before</em> clearing, so a token cancelled in
/// the file-swap → index-clear window throws before the index is touched. The index then retains
/// OLD-layout offsets while the file holds the NEW (shorter, deduplicated) compacted layout. If the
/// recovery sentinel stayed completed, the next point read would consult stale offsets — returning
/// another key's value silently or throwing <see cref="ArgumentOutOfRangeException"/> (stale offset
/// past the new, shorter EOF). The engine must instead self-heal: invalidating the recovery sentinel
/// <em>before</em> the durable swap means any post-swap interrupt makes the next consumer operation
/// rebuild the index from the new compacted file.
/// </para>
/// </remarks>
public sealed class AppendOnlyFileStorageEngineWithIndexCompactCancelTests : IDisposable
{
    private readonly string _testDirectory = Path.Combine(Path.GetTempPath(), $"StorageCancelTests_{Guid.NewGuid():N}");
    private readonly string _testFileName = "CompactCancel.dat";
    private string TestFilePath => Path.Combine(_testDirectory, _testFileName);

    private void CleanupTestFiles()
    {
        if (File.Exists(TestFilePath))
        {
            File.Delete(TestFilePath);
        }
    }

    /// <summary>
    /// A faithful <see cref="IFileStorageIndex{TKey}"/> wrapper that reproduces the
    /// <see cref="Indexing.InMemoryStorageIndex{TKey,TValue}"/> <c>ClearAsync</c> cancellation ordering
    /// (<c>ThrowIfCancellationRequested()</c> BEFORE the dictionary clear). The first <c>ClearAsync</c>
    /// is forced to throw <see cref="OperationCanceledException"/> — modelling a token cancelled in the
    /// exact file-swap → index-clear window — while every other call delegates to a real backing index.
    /// </summary>
    private sealed class CancelOnFirstClearIndex<TKey>(IFileStorageIndex<TKey> inner) : IFileStorageIndex<TKey>
        where TKey : IComparable<TKey>
    {
        private bool _firstClear = true;

        public Task SetAsync(TKey key, FileLocation value, CancellationToken cancellationToken = default)
            => inner.SetAsync(key, value, cancellationToken);

        public Task<(FileLocation Value, bool Found)> TryGetValueAsync(TKey key, CancellationToken cancellationToken = default)
            => inner.TryGetValueAsync(key, cancellationToken);

        public Task<bool> ContainsKeyAsync(TKey key, CancellationToken cancellationToken = default)
            => inner.ContainsKeyAsync(key, cancellationToken);

        public Task RemoveAsync(TKey key, CancellationToken cancellationToken = default)
            => inner.RemoveAsync(key, cancellationToken);

        public Task ClearAsync(CancellationToken cancellationToken = default)
        {
            if (_firstClear)
            {
                _firstClear = false;
                // Reproduce InMemoryStorageIndex.ClearAsync: throw BEFORE clearing. The index keeps
                // its OLD-layout offsets, exactly the state CompactAsync leaves behind on a cancel at
                // the swap → clear boundary.
                throw new OperationCanceledException();
            }

            return inner.ClearAsync(cancellationToken);
        }
    }

    /// <summary>
    /// After a <c>CompactAsync</c> interrupted at the file-swap → index-clear boundary, a subsequent
    /// point read of a durably-written key must return the CORRECT value (the engine self-heals by
    /// rebuilding the index from the new compacted file) — never a stale/cross-key value and never
    /// <see cref="ArgumentOutOfRangeException"/> from a stale offset past the shorter new EOF.
    /// </summary>
    [Fact]
    public async Task CompactAsync_CancelledAtIndexClearBoundary_NextReadSelfHealsToCorrectValue()
    {
        var entrySerializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        var storageFile = new StorageFile(_testDirectory, _testFileName);
        var index = new CancelOnFirstClearIndex<SerializableWrapper<int>>(
            new InMemoryFileIndex<SerializableWrapper<int>>());

        var engine = new AppendOnlyFileStorageEngineWithIndex<SerializableWrapper<int>, SerializableWrapper<string>>(
            storageFile, entrySerializer, index);

        // Duplicate keys (key 1 written twice) so compaction shrinks the file and shifts every
        // surviving entry's offset — the precondition for a stale offset to read the wrong bytes.
        await engine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("stale-1"));
        await engine.SetAsync(new SerializableWrapper<int>(2), new SerializableWrapper<string>("value-2"));
        await engine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("current-1"));
        await engine.SetAsync(new SerializableWrapper<int>(3), new SerializableWrapper<string>("value-3"));

        // CompactAsync swaps the file durably, then ClearAsync throws (cancellation modelled by the
        // wrapper). The compacted file is on disk; the in-memory index still holds old offsets.
        await Assert.ThrowsAsync<OperationCanceledException>(() => engine.CompactAsync());

        // The next reads must self-heal. Pre-fix: the stale index serves old offsets against the new
        // shorter file → wrong value (e.g. key 1 reads another key's bytes) or ArgumentOutOfRangeException.
        var (v1, found1) = await engine.TryGetValueAsync(new SerializableWrapper<int>(1));
        Assert.True(found1, "key 1 was durably written and survives compaction — it must be findable.");
        Assert.Equal("current-1", v1.Value);

        var (v2, found2) = await engine.TryGetValueAsync(new SerializableWrapper<int>(2));
        Assert.True(found2);
        Assert.Equal("value-2", v2.Value);

        var (v3, found3) = await engine.TryGetValueAsync(new SerializableWrapper<int>(3));
        Assert.True(found3);
        Assert.Equal("value-3", v3.Value);
    }

    /// <summary>
    /// A faithful <see cref="IFileStorageIndex{TKey}"/> wrapper whose first <c>SetAsync</c> cancels
    /// the caller's token source and then honours the token it was handed — modelling a token that
    /// fires in the post-fsync → index-update window, where the entry is already durable on disk.
    /// Every other call delegates to a real backing index.
    /// </summary>
    private sealed class CancelOnFirstSetIndex<TKey>(IFileStorageIndex<TKey> inner, CancellationTokenSource source) : IFileStorageIndex<TKey>
        where TKey : IComparable<TKey>
    {
        private bool _firstSet = true;

        public Task SetAsync(TKey key, FileLocation value, CancellationToken cancellationToken = default)
        {
            if (_firstSet)
            {
                _firstSet = false;
                source.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }

            return inner.SetAsync(key, value, cancellationToken);
        }

        public Task<(FileLocation Value, bool Found)> TryGetValueAsync(TKey key, CancellationToken cancellationToken = default)
            => inner.TryGetValueAsync(key, cancellationToken);

        public Task<bool> ContainsKeyAsync(TKey key, CancellationToken cancellationToken = default)
            => inner.ContainsKeyAsync(key, cancellationToken);

        public Task RemoveAsync(TKey key, CancellationToken cancellationToken = default)
            => inner.RemoveAsync(key, cancellationToken);

        public Task ClearAsync(CancellationToken cancellationToken = default)
            => inner.ClearAsync(cancellationToken);
    }

    /// <summary>
    /// F-GAP-APPEND-02. A token that fires in the post-fsync → index-update window arrives after the
    /// entry is durable on disk, so the write is committed: the call completes rather than reporting
    /// a committed write as cancelled, and a subsequent point read finds the key.
    /// </summary>
    [Fact]
    public async Task SetAsync_CancelledAtIndexUpdateBoundary_CompletesAndNextReadFindsDurableEntry()
    {
        var entrySerializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        var storageFile = new StorageFile(_testDirectory, _testFileName);
        using var cts = new CancellationTokenSource();
        var index = new CancelOnFirstSetIndex<SerializableWrapper<int>>(
            new InMemoryFileIndex<SerializableWrapper<int>>(), cts);

        var engine = new AppendOnlyFileStorageEngineWithIndex<SerializableWrapper<int>, SerializableWrapper<string>>(
            storageFile, entrySerializer, index);

        // The write is durable on disk before the token fires at the index update, so it completes.
        await engine.SetAsync(new SerializableWrapper<int>(7), new SerializableWrapper<string>("durable-7"), cts.Token);

        var (v7, found7) = await engine.TryGetValueAsync(new SerializableWrapper<int>(7));
        Assert.True(found7, "a durably-written key must be findable even if the index update was interrupted (rebuild self-heals).");
        Assert.Equal("durable-7", v7.Value);
    }

    /// <summary>
    /// Returns the value the base engine's full log scan resolves for <paramref name="key"/>
    /// (last-writer-wins over every record on disk) — the index-free path the indexed point read
    /// must agree with.
    /// </summary>
    private static async Task<(string? Value, bool Found)> FullScanLookupAsync(
        AppendOnlyFileStorageEngineWithIndex<SerializableWrapper<int>, SerializableWrapper<string>> engine,
        int key)
    {
        var items = await engine.GetAllItemsAsync().ConfigureAwait(true);
        var matches = items.Where(i => i.Key.Value == key).ToList();
        return matches.Count == 0 ? (null, false) : (matches[^1].Value.Value, true);
    }

    /// <summary>
    /// A <c>ClearAsync</c> interrupted at the index-clear step must never leave the index holding
    /// offsets into a log that no longer contains those records. Pre-fix, the log was deleted first
    /// and the index clear threw afterwards, so a cleared key kept its old offset and a later write
    /// of a same-sized record landed at that offset — the point read then served another key's value.
    /// </summary>
    [Fact]
    public async Task ClearAsync_CancelledAtIndexClear_PointReadAgreesWithLogAndNeverServesAnotherKey()
    {
        var entrySerializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        var storageFile = new StorageFile(_testDirectory, _testFileName);
        var index = new CancelOnFirstClearIndex<SerializableWrapper<int>>(
            new InMemoryFileIndex<SerializableWrapper<int>>());
        var engine = new AppendOnlyFileStorageEngineWithIndex<SerializableWrapper<int>, SerializableWrapper<string>>(
            storageFile, entrySerializer, index);

        await engine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("one"));
        await Assert.ThrowsAsync<OperationCanceledException>(() => engine.ClearAsync());

        // Same encoded size as key 1's record, so a stale offset 0 lands exactly on this record.
        await engine.SetAsync(new SerializableWrapper<int>(2), new SerializableWrapper<string>("two"));

        var (v1, found1) = await engine.TryGetValueAsync(new SerializableWrapper<int>(1));
        var (scanValue1, scanFound1) = await FullScanLookupAsync(engine, 1);
        Assert.Equal(scanFound1, found1);
        if (found1)
        {
            Assert.Equal(scanValue1, v1.Value);
        }

        var (v2, found2) = await engine.TryGetValueAsync(new SerializableWrapper<int>(2));
        Assert.True(found2);
        Assert.Equal("two", v2.Value);
    }

    /// <summary>
    /// A faithful <see cref="IFileStorageIndex{TKey}"/> wrapper whose first <c>ClearAsync</c> clears
    /// the backing index and THEN cancels the caller's token source — modelling a token that fires
    /// after the index step and before the log delete. Every other call delegates unchanged.
    /// </summary>
    private sealed class CancelAfterFirstClearIndex<TKey>(IFileStorageIndex<TKey> inner, CancellationTokenSource source)
        : IFileStorageIndex<TKey>
        where TKey : IComparable<TKey>
    {
        private bool _firstClear = true;

        public Task SetAsync(TKey key, FileLocation value, CancellationToken cancellationToken = default)
            => inner.SetAsync(key, value, cancellationToken);

        public Task<(FileLocation Value, bool Found)> TryGetValueAsync(TKey key, CancellationToken cancellationToken = default)
            => inner.TryGetValueAsync(key, cancellationToken);

        public Task<bool> ContainsKeyAsync(TKey key, CancellationToken cancellationToken = default)
            => inner.ContainsKeyAsync(key, cancellationToken);

        public Task RemoveAsync(TKey key, CancellationToken cancellationToken = default)
            => inner.RemoveAsync(key, cancellationToken);

        public async Task ClearAsync(CancellationToken cancellationToken = default)
        {
            await inner.ClearAsync(cancellationToken).ConfigureAwait(true);
            if (_firstClear)
            {
                _firstClear = false;
                await source.CancelAsync().ConfigureAwait(true);
            }
        }
    }

    /// <summary>
    /// A <c>ClearAsync</c> interrupted after the index was cleared but before the log was deleted
    /// leaves the log intact; the next point read must rebuild the index from that log and find the
    /// durable key, never report a silent false negative from a completed recovery sentinel.
    /// </summary>
    [Fact]
    public async Task ClearAsync_CancelledBetweenIndexClearAndLogDelete_NextReadRebuildsFromIntactLog()
    {
        using var cts = new CancellationTokenSource();
        var entrySerializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        var storageFile = new StorageFile(_testDirectory, _testFileName);
        var index = new CancelAfterFirstClearIndex<SerializableWrapper<int>>(
            new InMemoryFileIndex<SerializableWrapper<int>>(), cts);
        var engine = new AppendOnlyFileStorageEngineWithIndex<SerializableWrapper<int>, SerializableWrapper<string>>(
            storageFile, entrySerializer, index);

        await engine.SetAsync(new SerializableWrapper<int>(1), new SerializableWrapper<string>("one"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.ClearAsync(cts.Token));

        var (v1, found1) = await engine.TryGetValueAsync(new SerializableWrapper<int>(1));
        var (scanValue1, scanFound1) = await FullScanLookupAsync(engine, 1);
        Assert.True(scanFound1, "the interrupted clear never deleted the log, so key 1 is still on disk.");
        Assert.True(found1, "the point read must agree with the intact log after the index-only clear.");
        Assert.Equal(scanValue1, v1.Value);
    }

    public void Dispose()
    {
        CleanupTestFiles();
        if (Directory.Exists(_testDirectory))
        {
            try { Directory.Delete(_testDirectory, recursive: true); } catch (IOException) { }
        }
    }
}
