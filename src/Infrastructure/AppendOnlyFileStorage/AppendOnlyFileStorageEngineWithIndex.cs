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
namespace Boutquin.Storage.Infrastructure.AppendOnlyFileStorage;

/// <summary>
/// Provides an append-only file-based storage engine with asynchronous operations,
/// using an index to speed up reads by storing the offset in the file.
/// </summary>
/// <typeparam name="TKey">The type of the keys in the store.</typeparam>
/// <typeparam name="TValue">The type of the values in the store.</typeparam>
/// <remarks>
/// <para>
/// <b>Why an index layer on top of append-only storage?</b> The base
/// <see cref="AppendOnlyFileStorageEngine{TKey, TValue}"/> requires O(n) sequential scan for reads.
/// The index trades memory for read performance by storing each key's file offset, enabling O(1)
/// direct reads via seek. This is the hash-index approach from Kleppmann 2e Ch. 4 — the entire keyspace
/// must fit in memory, but reads become constant-time.
/// </para>
/// <para>
/// <b>Why store FileLocation(offset, length - offset)?</b> The offset is the stream position before
/// writing the entry. The count (length - offset) is the number of bytes the entry occupies. Together
/// they allow reading exactly the right bytes from disk — no scanning, no parsing neighboring entries.
/// </para>
/// <para>
/// <b>Concurrency contract — single mutator.</b> Inherited from the base engine, and stricter
/// here: index recovery, point reads and mutations share a gate so a reader cannot overwrite
/// committed offsets with an older recovery scan. Other inherited scan operations retain the
/// base engine's external serialization requirement. Compose those operations under an
/// external serialisation point. As in the base engine,
/// overlapping writes from a second live instance over the same path fail fast at the file
/// layer (<see cref="IStorageFile.Open"/> hands out append streams under an exclusive
/// <see cref="FileShare.None"/> lock) rather than overwriting committed bytes — but even
/// non-overlapping cross-instance writes leave THIS instance's in-memory index blind to the
/// other writer's entries, so point reads serve stale values. Multi-instance topologies are a
/// contract violation regardless of the file-layer fail-fast.
/// </para>
/// </remarks>
/// <remarks>
/// Initializes a new instance of the <see cref="AppendOnlyFileStorageEngineWithIndex{TKey, TValue}"/> class.
/// </remarks>
/// <param name="storageFile">The storage file to use for storing data.</param>
/// <param name="entrySerializer">The serializer to use for serializing and deserializing entries.</param>
/// <param name="index">The index to use for storing offsets of entries.</param>
/// <exception cref="ArgumentNullException">Thrown if <paramref name="storageFile"/>, <paramref name="entrySerializer"/>, or <paramref name="index"/> is null.</exception>
public sealed class AppendOnlyFileStorageEngineWithIndex<TKey, TValue>(
    IStorageFile storageFile,
    IEntrySerializer<TKey, TValue> entrySerializer,
    IFileStorageIndex<TKey> index) :
    AppendOnlyFileStorageEngine<TKey, TValue>(storageFile, entrySerializer)
    where TKey : IStorageKey<TKey>, new()
    where TValue : ISerializable<TValue>, new()
{
    private readonly IFileStorageIndex<TKey> _index = index ?? throw new ArgumentNullException(nameof(index));

    // Rebuild and mutation publish one derived index state. A reader cannot scan an old log
    // snapshot and overwrite a newer writer's offsets after its durable append completes.
    private readonly SemaphoreSlim _indexGate = new(1, 1);

    // Lazy one-shot rebuild. Lazy<Task> guarantees the factory (which STARTS the rebuild — the
    // whole-log scan runs synchronously up to its first await) is invoked exactly once under
    // ExecutionAndPublication, so concurrent first operations never launch parallel scans.
    // ClearAsync / CompactAsync replace this with an already-completed Lazy (the empty/rebuilt
    // index IS the recovered state), so a later first operation does not re-scan.
    // Nullable rather than `null!`: the recovery sentinel is genuinely unset until the first operation
    // lazily creates it via LazyInitializer.EnsureInitialized (which takes a ref to a nullable slot and
    // is the one-shot allocator). Declaring it `Lazy<Task>?` expresses that "unset" state honestly
    // instead of suppressing the nullable warning with `null!` — the suppression hid a real invariant
    // (the field IS null before first use) behind a forgiving-cast. Every read goes through
    // EnsureIndexRecoveredAsync or a Volatile.Write of a non-null Lazy, so no consumer dereferences null.
    private Lazy<Task>? _indexRecovery;

    internal Func<Task>? BeforeAppendHook { get; set; }

    private Lazy<Task> CreateRecoveryLazy()
        => new(RebuildIndexFromLogAsync, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// One-time index rebuild from the existing log, run lazily before the first operation.
    /// The index is in-memory and not persisted, so a restart must rebuild it by scanning
    /// the whole log and recording the latest byte offset for each key (DDIA 2e Ch. 4) —
    /// otherwise every durable, previously-acknowledged key reads as missing through the
    /// indexed point-read path while the full scan still returns it. Idempotent over a
    /// pre-populated index: the scan's last-writer-wins overwrites agree with any entries
    /// the caller's index already holds for this file. Runs with
    /// <see cref="CancellationToken.None"/> so a caller's cancelled first operation cannot
    /// poison the cached recovery task.
    /// </summary>
    /// <remarks>
    /// The rebuild is published atomically. A plain <c>??=</c> on a bare task field is a
    /// non-atomic check-then-act: two concurrent first operations could both observe the field
    /// null and both launch a full <see cref="RebuildIndexFromLogAsync"/> — a redundant whole-log
    /// scan plus duplicate index writes racing into the same index. A
    /// <see cref="Lazy{T}"/><c>&lt;Task&gt;</c> under
    /// <see cref="LazyThreadSafetyMode.ExecutionAndPublication"/> guarantees the factory that
    /// starts the rebuild runs exactly once; every racer awaits the single shared task.
    /// </remarks>
    private Task EnsureIndexRecoveredAsync()
        => LazyInitializer.EnsureInitialized(ref _indexRecovery, CreateRecoveryLazy).Value;

    private async Task RebuildIndexFromLogAsync()
    {
        if (!StorageFile.Exists() || StorageFile.FileSize == 0)
        {
            return;
        }

        var fileBytes = await StorageFile.ReadAllBytesAsync(CancellationToken.None).ConfigureAwait(false);
        using var stream = OpenLogScan(fileBytes);
        while (stream.Position < stream.Length)
        {
            var offset = stream.Position;
            if (!TryReadNextEntry(stream, out var entry))
            {
                // Torn final record (crash mid-append) — end of recoverable log.
                break;
            }
            if (entry.HasValue)
            {
                await _index.SetAsync(
                    entry.Value.Key,
                    new FileLocation(offset, stream.Position - offset),
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc/>
    public override async Task SetAsync(TKey key, TValue value, CancellationToken cancellationToken = default)
    {
        await _indexGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SetAsyncCore(key, value, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _indexGate.Release();
        }
    }

    private async Task SetAsyncCore(TKey key, TValue value, CancellationToken cancellationToken = default)
    {
        Guard.AgainstNullOrDefault(() => key);
        Guard.AgainstNullOrDefault(() => value);
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureIndexRecoveredAsync().ConfigureAwait(false);
        await RepairTornTailBeforeAppendAsync(cancellationToken).ConfigureAwait(false);

        // Invalidate the recovery sentinel to a REBUILD lazy BEFORE the durable write. If the
        // caller's token fires in the post-fsync → index-update window, the entry is durable on
        // disk but absent from the index; with the sentinel reset to a pending rebuild, the next
        // consumer operation re-scans the log and recovers it, rather than the completed sentinel
        // suppressing the rebuild and leaving a silent TryGetValueAsync false negative.
        Volatile.Write(ref _indexRecovery, CreateRecoveryLazy());
        if (BeforeAppendHook is { } hook)
        {
            await hook().ConfigureAwait(false);
        }

        // Unverified while the append is in flight: a write that fails part-way leaves a torn frame
        // at EOF, and the next append must repair it rather than trust the earlier check.
        TailVerified = false;
        var stream = StorageFile.Open(FileMode.Append);
        var offset = stream.Length;
        long newLength;
        try
        {
            await using (stream.ConfigureAwait(false))
            {
                await WriteEntryAsync(stream, key, value, cancellationToken).ConfigureAwait(false);
                newLength = stream.Length;

                // fsync before close and before the index records the offset — acknowledge only after
                // the entry is durable, matching the base SetBulkAsync path (DDIA 2e —
                // durable-before-ack). An index entry pointing at a page-cache-only write would dangle
                // after a crash.
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (stream is FileStream fs)
                {
                    fs.Flush(flushToDisk: true);
                }

                TailVerified = true;

                // Keep the inherited cached size exact — newLength IS the append stream's post-write end
                // offset, so no stat is needed for a following FileSize read.
                CachedFileSize = newLength;
            }
        }
        catch
        {
            // The append is reported failed or cancelled, so none of its bytes may survive; the
            // pending rebuild sentinel re-indexes the truncated log.
            await RemoveFailedAppendAsync(offset).ConfigureAwait(false);
            throw;
        }

        // The entry is durable, so the call completes: a cancellation arriving now must not report
        // a committed write as cancelled.
        await _index.SetAsync(key, new FileLocation(offset, newLength - offset), CancellationToken.None).ConfigureAwait(false);

        // Index now reflects the durable write. Publish the fast-path completed sentinel only after
        // the index update succeeds; an interrupt before here leaves the pending rebuild in place.
        Volatile.Write(ref _indexRecovery, new Lazy<Task>(Task.CompletedTask));
    }

    /// <inheritdoc/>
    public override async Task<(TValue Value, bool Found)> TryGetValueAsync(TKey key, CancellationToken cancellationToken = default)
    {
        await _indexGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await TryGetValueAsyncCore(key, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _indexGate.Release();
        }
    }

    private async Task<(TValue Value, bool Found)> TryGetValueAsyncCore(TKey key, CancellationToken cancellationToken = default)
    {
        Guard.AgainstNullOrDefault(() => key);
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureIndexRecoveredAsync().ConfigureAwait(false);

        var (fileLocation, found) = await _index.TryGetValueAsync(key, cancellationToken).ConfigureAwait(false);
        if (!found)
        {
            return (default!, false);
        }

        // The index records whole frames, so the bytes read back must be exactly one valid frame;
        // its checksums are verified here just as the full scan verifies them.
        var frame = StorageFile.ReadBytes(fileLocation.Offset, fileLocation.Count);
        return (DecodeFrameAt(frame, fileLocation.Offset).Value, true);
    }

    /// <inheritdoc/>
    public override async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _indexGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ClearAsyncCore(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _indexGate.Release();
        }
    }

    private async Task ClearAsyncCore(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Clear the derived index BEFORE deleting the log. The index clear is cancellable (the
        // in-memory indexes check the token before clearing); deleting first meant an interrupt
        // there left every old offset in the index against a deleted log, so the next write of a
        // same-sized record landed at a stale offset and a point read of a cleared key served
        // another key's value. With the index cleared first, an interrupt at the index step
        // leaves log and index both untouched.
        //
        // Invalidate the recovery sentinel to a REBUILD lazy first, mirroring SetAsync/CompactAsync:
        // an interrupt after the index clear but before the log delete leaves an intact log behind
        // an empty index, and the pending rebuild makes the next operation re-scan it rather than a
        // completed sentinel reporting every durable key as missing.
        Volatile.Write(ref _indexRecovery, CreateRecoveryLazy());
        await _index.ClearAsync(cancellationToken).ConfigureAwait(false);
        await base.ClearAsync(cancellationToken).ConfigureAwait(false);
        // The log is now empty; the empty index IS the rebuilt state. Publish an already-completed
        // Lazy so a later first operation short-circuits instead of re-scanning.
        Volatile.Write(ref _indexRecovery, new Lazy<Task>(Task.CompletedTask));
    }

    /// <inheritdoc/>
    public override async Task SetBulkAsync(IEnumerable<KeyValuePair<TKey, TValue>> items, CancellationToken cancellationToken = default)
    {
        await _indexGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SetBulkAsyncCore(items, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _indexGate.Release();
        }
    }

    private async Task SetBulkAsyncCore(IEnumerable<KeyValuePair<TKey, TValue>> items, CancellationToken cancellationToken = default)
    {
        Guard.AgainstEmptyOrNullEnumerable(() => items);
        cancellationToken.ThrowIfCancellationRequested();

        // Validate-before-apply: reject the WHOLE batch before writing its first byte. A guard
        // inside the write loop let earlier items reach the log (and, through the pending
        // rebuild sentinel, the index) before a later invalid item threw.
        var batch = items.ToList();
        foreach (var item in batch)
        {
            Guard.AgainstNullOrDefault(() => item.Key);
            Guard.AgainstNullOrDefault(() => item.Value);
        }

        await EnsureIndexRecoveredAsync().ConfigureAwait(false);
        await RepairTornTailBeforeAppendAsync(cancellationToken).ConfigureAwait(false);

        // Collect each entry's on-disk location while writing, but defer recording it in the index
        // until after the data is fsync'd. The index must never publish an offset for data that is
        // not yet durable — a crash would otherwise leave the index pointing past the durable end of
        // the file (DDIA 2e — durable-before-ack). This mirrors SetAsync and the base SetBulkAsync.
        // Invalidate the recovery sentinel to a REBUILD lazy BEFORE the durable write, so an
        // interrupt anywhere in the post-fsync → index-update window (between durability and the
        // pending-offset loop below) makes the next consumer operation re-scan the log and recover
        // the durable-but-unindexed entries instead of the completed sentinel suppressing rebuild.
        Volatile.Write(ref _indexRecovery, CreateRecoveryLazy());

        var pending = new List<(TKey Key, FileLocation Location)>();

        // Unverified while the append is in flight (see SetAsync).
        TailVerified = false;
        var stream = StorageFile.Open(FileMode.Append);
        var batchStart = stream.Length;
        try
        {
            await using (stream.ConfigureAwait(false))
            {
                foreach (var item in batch)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var offset = stream.Length;
                    await WriteEntryAsync(stream, item.Key, item.Value, cancellationToken).ConfigureAwait(false);
                    var newLength = stream.Length;
                    pending.Add((item.Key, new FileLocation(offset, newLength - offset)));
                    // Track the running end offset so the inherited cached size is exact post-loop.
                    CachedFileSize = newLength;
                }

                // fsync before recording offsets and before close — acknowledge only after the entries
                // are durable on disk.
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (stream is FileStream fs)
                {
                    fs.Flush(flushToDisk: true);
                }

                TailVerified = true;
            }
        }
        catch
        {
            // Every item of the batch is reported failed, so none of its bytes may survive.
            await RemoveFailedAppendAsync(batchStart).ConfigureAwait(false);
            throw;
        }

        // The batch is durable, so the call completes: a cancellation arriving now must not report
        // committed writes as cancelled.
        foreach (var (key, location) in pending)
        {
            await _index.SetAsync(key, location, CancellationToken.None).ConfigureAwait(false);
        }

        // Index now reflects all durable writes. Publish the fast-path completed sentinel only after
        // every offset is recorded; an interrupt before here leaves the pending rebuild in place.
        Volatile.Write(ref _indexRecovery, new Lazy<Task>(Task.CompletedTask));
    }

    /// <inheritdoc/>
    public override async Task CompactAsync(CancellationToken cancellationToken = default)
    {
        await _indexGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await CompactAsyncCore(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _indexGate.Release();
        }
    }

    private async Task CompactAsyncCore(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var items = await GetAllItemsAsync(cancellationToken).ConfigureAwait(false);
        var latestItems = items.GroupBy(x => x.Key).Select(g => g.Last()).ToList();

        // Serialize the compacted entries in memory while capturing each entry's offset, then replace
        // the data file atomically and rebuild the in-memory index from the captured offsets. The
        // previous ClearAsync-then-SetBulkAsync path deleted the file before rewriting it, so a crash
        // in that window destroyed all data. WriteAllBytesAsync (temp + fsync + rename) makes the file
        // swap atomic; a crash before the index rebuild simply leaves the new compacted file with an
        // empty in-memory index, which is consistent because the index is non-durable and rebuilt on
        // open anyway.
        using var buffer = new MemoryStream();
        var locations = new List<(TKey Key, FileLocation Location)>(latestItems.Count);
        foreach (var item in latestItems)
        {
            var offset = buffer.Length;
            await WriteEntryAsync(buffer, item.Key, item.Value, cancellationToken).ConfigureAwait(false);
            locations.Add((item.Key, new FileLocation(offset, buffer.Length - offset)));
        }

        // Invalidate the recovery sentinel to a REBUILD lazy BEFORE the durable file swap. The
        // file replace is atomic and not cancellable mid-rename, but the index update that follows
        // (ClearAsync, then per-key SetAsync) can fault. An interrupt in the swap → clear window leaves the
        // index holding OLD-layout offsets against the NEW compacted file. With the sentinel reset
        // to a pending rebuild first, any such interrupt makes the next consumer operation
        // re-scan the new file via RebuildIndexFromLogAsync rather than serve stale offsets
        // (which would read another key's bytes silently, or throw past the shorter new EOF).
        Volatile.Write(ref _indexRecovery, CreateRecoveryLazy());

        var compacted = buffer.ToArray();
        await StorageFile.WriteAllBytesAsync(compacted, cancellationToken).ConfigureAwait(false);
        // The compacted file replaced the old one wholesale; its size is the buffer length, and no
        // failed append's bytes remain in it.
        CachedFileSize = compacted.Length;
        FailedAppendStart = -1;

        // The durable swap committed this compaction. Finish publishing the derived index
        // even if cancellation arrives now, so the caller observes the committed outcome.
        await _index.ClearAsync(CancellationToken.None).ConfigureAwait(false);
        foreach (var (key, location) in locations)
        {
            await _index.SetAsync(key, location, CancellationToken.None).ConfigureAwait(false);
        }

        // The index now fully reflects the rewritten log. Publish an already-completed Lazy only
        // AFTER the rebuild succeeds, so a later first operation short-circuits instead of
        // re-scanning. An interrupt before this point leaves the pending rebuild sentinel in place.
        Volatile.Write(ref _indexRecovery, new Lazy<Task>(Task.CompletedTask));
    }
}
