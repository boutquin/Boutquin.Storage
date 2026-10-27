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
using Boutquin.Storage.Infrastructure.LsmTree;

namespace Boutquin.Storage.Infrastructure.ValueLog;

/// <summary>
/// Summary of a single GC round produced by <see cref="ValueLogGarbageCollector.RunAsync"/>.
/// </summary>
/// <param name="FilesReclaimed">Closed value-log segment files fully shadowed by live data
/// and unlinked in this round.</param>
/// <param name="BytesReclaimed">Total bytes freed by <paramref name="FilesReclaimed"/>.</param>
/// <param name="FilesSkipped">Closed files retained because at least one live SSTable or
/// snapshot-pinned segment still references them.</param>
public readonly record struct GcResult(int FilesReclaimed, long BytesReclaimed, int FilesSkipped);

/// <summary>
/// Whole-file garbage collector for the WiscKey value log. Scans every live + pinned SSTable
/// entry for <see cref="Domain.Enums.ValueKind.ValueLogPointer"/> block entries, builds the
/// liveness set <c>L</c> of <c>(fileIndex, offset)</c> pairs, and unlinks every closed
/// value-log segment whose contents are fully shadowed.
/// </summary>
/// <remarks>
/// <para><b>Whole-file policy.</b> Partial-file rewrites are out of scope for v2.0.0
/// (whole-file GC policy); a closed segment is reclaimable only when
/// <c>L ∩ file = ∅</c>. The current writer's append-target file is never a candidate.</para>
/// <para><b>Durability order.</b> For each reclaimable file: (1) record a delete tombstone
/// in <see cref="ValueLogManifest"/> and fsync; (2) unlink the file. A crash between (1)
/// and (2) leaves an orphan file the next startup's reconciliation sweep removes — the
/// manifest tombstone prevents the orphan from being re-added to the live set.</para>
/// <para><b>Concurrency.</b> Two concurrent GC rounds on the same engine are serialised by the
/// engine's per-engine GC gate (acquired in <c>LsmStorageEngine.CompactValueLogAsync</c> before
/// the collector is constructed), not by this class — a fresh collector is built per call, so a
/// per-instance gate here could never be contended. Writes proceed unblocked against the current
/// append-target file.</para>
/// </remarks>
public sealed class ValueLogGarbageCollector
{
    private readonly object _engine;
    private readonly Action<Exception>? _onScanFailure;

    /// <summary>
    /// Wraps <paramref name="engine"/> so <see cref="RunAsync"/> has a consistent internal
    /// handle. <paramref name="engine"/> must be an <c>LsmStorageEngine&lt;TKey, TValue&gt;</c>
    /// instance; the constructor erases the type parameters so downstream callers can
    /// construct without knowing them. Internal-only mechanism — public callers go through
    /// <c>LsmStorageEngine.CompactValueLogAsync</c>.
    /// </summary>
    /// <param name="engine">The host LSM engine (as <c>IValueLogGcHost</c>).</param>
    /// <param name="onScanFailure">
    /// Optional observer invoked when an unexpected exception escapes the lock-free liveness scan
    /// (F-57). Without it such a throw would propagate out of the GC round and be effectively
    /// swallowed by the caller; with it the failure is surfaced (logged) and the round fails closed
    /// — it reclaims nothing — rather than faulting or silently dropping the diagnostic.
    /// </param>
    internal ValueLogGarbageCollector(object engine, Action<Exception>? onScanFailure = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _onScanFailure = onScanFailure;
    }

    /// <summary>
    /// Runs one GC round. Returns the count + bytes reclaimed and the count retained.
    /// Caller serialisation contract: same-engine GC rounds are serialised by the engine's
    /// per-engine GC gate in <c>LsmStorageEngine.CompactValueLogAsync</c> before this collector
    /// is constructed; this method does not gate concurrent callers itself.
    /// </summary>
    public async Task<GcResult> RunAsync(CancellationToken cancellationToken = default)
    {
        return await RunImplAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<GcResult> RunImplAsync(CancellationToken cancellationToken)
    {
        // Extract the engine's closed-file manifest + writer state under the engine lock.
        // The liveness-set scan itself runs under the engine lock so the segment set is
        // stable; GC writes (manifest tombstone + unlink) run outside the lock so concurrent
        // writes against the current append-target are not blocked.
        (HashSet<(uint FileIndex, ulong Offset)> livePointers,
         var currentFileIndex,
         List<uint> closedFileIndexes,
         var directory,
         ValueLogManifest manifest,
         Dictionary<uint, long> closedFileSizes,
         var scanComplete) = await CollectUnderEngineLockAsync(cancellationToken).ConfigureAwait(false);

        var filesReclaimed = 0;
        long bytesReclaimed = 0;
        var filesSkipped = 0;

        // Fail-closed: a concurrent compaction disposed a segment reader mid-scan, so the
        // liveness set is partial. Reclaiming on it could free a value-log file a live pointer
        // still references. Skip reclaim entirely this round; the next round (after compaction
        // settles) makes progress. Every closed file is reported as skipped, not reclaimed.
        if (!scanComplete)
        {
            return new GcResult(FilesReclaimed: 0, BytesReclaimed: 0, FilesSkipped: closedFileIndexes.Count);
        }

        // Project the live pointer set down to the distinct set of referenced file indexes ONCE,
        // so the per-closed-file reclaimability check is an O(1) HashSet probe instead of a linear
        // scan over every live pointer. Total work is O(livePointers + closedFiles), not the prior
        // O(closedFiles × livePointers) (F-58).
        var liveFileIndexes = new HashSet<uint>();
        foreach (var (idx, _) in livePointers)
        {
            liveFileIndexes.Add(idx);
        }

        foreach (var fileIndex in closedFileIndexes)
        {
            if (fileIndex == currentFileIndex)
            {
                continue;
            }

            // File is reclaimable iff no live pointer references an offset inside it — O(1) probe.
            if (liveFileIndexes.Contains(fileIndex))
            {
                filesSkipped++;
                continue;
            }

            // Record the delete tombstone + fsync BEFORE unlinking so a crash between the
            // manifest append and the unlink leaves a recoverable state: the manifest says
            // the file is gone and the next startup sweep will remove the orphan.
            await manifest.RecordDeleteAsync(fileIndex, cancellationToken).ConfigureAwait(false);

            var path = ValueLogWriter.ResolveSegmentPath(directory, fileIndex);
            try
            {
                var size = closedFileSizes.TryGetValue(fileIndex, out var s) ? s : 0L;
                // Release every reader's cached handle on the segment before and after the
                // unlink: before, so the unlink can succeed where an open handle blocks it; after,
                // so a handle re-opened in between cannot keep the unlinked blocks allocated. A
                // reclaimed segment holds no live pointer, so no correct read needs it again.
                ValueLogReader.ReleaseSegmentHandles(directory, fileIndex);
                File.Delete(path);
                ValueLogReader.ReleaseSegmentHandles(directory, fileIndex);
                filesReclaimed++;
                bytesReclaimed += size;
            }
            catch (IOException)
            {
                // Leave the orphan; next-start recovery reconciles it.
            }
        }

        return new GcResult(filesReclaimed, bytesReclaimed, filesSkipped);
    }

    private async Task<(HashSet<(uint FileIndex, ulong Offset)> LivePointers,
             uint CurrentFileIndex,
             List<uint> ClosedFileIndexes,
             string Directory,
             ValueLogManifest Manifest,
             Dictionary<uint, long> ClosedFileSizes,
             bool ScanComplete)> CollectUnderEngineLockAsync(CancellationToken cancellationToken)
    {
        // Use reflection-free dispatch via the internal engine-level helper. The engine type
        // surfaces the value-log handles + directory + options as internal properties on
        // LsmStorageEngine<TKey, TValue>; each instantiation has its own closed generic
        // representation. Rather than adding a non-generic interface, we call through an
        // internal typed interface defined in the partial class.
        if (_engine is not IValueLogGcHost host)
        {
            throw new InvalidOperationException(
                "ValueLogGarbageCollector requires an LsmStorageEngine<TKey, TValue> as its host.");
        }

        // Phase A: acquire engine lock; snapshot manifest state + segment references.
        // SnapshotSegmentsUnderLock captures segment reader references (O(N) list copy,
        // not segment data reads) and returns a lock-free scan delegate.
        // Await the async entry so the thread-pool thread is released while the semaphore
        // is contested — prevents thread-pool starvation across the await-bound critical section.
        await host.EnterLockForGcAsync(cancellationToken).ConfigureAwait(false);
        Func<HashSet<(uint FileIndex, ulong Offset)>, bool> scanDelegate;
        ValueLogManifest manifest;
        uint currentFileIndex;
        List<uint> closedFileIndexes;
        Dictionary<uint, long> closedFileSizes;
        var directory = host.ValueLogDirectory;
        try
        {
            manifest = host.ValueLogManifest ?? throw new InvalidOperationException(
                "ValueLogGarbageCollector invoked before the value-log subsystem was initialised; no live pointers can exist yet.");
            var writer = host.ValueLogWriter;
            currentFileIndex = writer?.CurrentFileIndex ?? 0u;
            var liveFiles = await manifest.GetLiveFilesAsync(cancellationToken).ConfigureAwait(false);
            closedFileIndexes = [.. liveFiles];
            closedFileSizes = writer?.FileSizes.ToDictionary(kv => kv.Key, kv => kv.Value)
                              ?? [];
            // Captures segment reader references without reading any segment data.
            scanDelegate = host.SnapshotSegmentsUnderLock();
        }
        finally
        {
            host.ExitLockForGc();
        }

        // Phase B: scan segment data WITHOUT holding the engine lock. Segment *bytes* are
        // immutable, but a captured reader can be disposed by a concurrent compaction; the
        // delegate reports completeness so a mid-scan disposal yields a skip-reclaim round
        // (scanComplete == false) rather than a faulted GC round.
        var livePointers = new HashSet<(uint, ulong)>();
        bool scanComplete;
        try
        {
            scanComplete = scanDelegate(livePointers);
        }
        catch (Exception ex)
        {
            // An UNEXPECTED throw escaped the lock-free scan (the delegate already converts the
            // benign concurrent-retirement races — ObjectDisposedException / IOException — into a
            // false return). Surface it through the failure observer (logged), then fail closed:
            // treat the round as an incomplete scan so NOTHING is reclaimed. Swallowing the throw
            // here without surfacing it would lose the diagnostic; letting it propagate would fault
            // the whole CompactValueLogAsync call on a recoverable, skip-this-round condition (F-57).
            _onScanFailure?.Invoke(ex);
            return (livePointers, currentFileIndex, closedFileIndexes, directory, manifest, closedFileSizes, ScanComplete: false);
        }

        return (livePointers, currentFileIndex, closedFileIndexes, directory, manifest, closedFileSizes, scanComplete);
    }
}

/// <summary>
/// Internal contract <see cref="LsmStorageEngine{TKey, TValue}"/> implements so
/// <see cref="ValueLogGarbageCollector"/> can reach its private state without knowing the
/// generic type arguments. Implementation lives in a partial class.
/// </summary>
internal interface IValueLogGcHost
{
    ValueLogManifest? ValueLogManifest { get; }
    ValueLogWriter? ValueLogWriter { get; }
    string ValueLogDirectory { get; }
    /// <summary>
    /// Captures segment reader references while the engine lock is held; returns a
    /// delegate that scans those references for live value-log pointers without any lock.
    /// Must be called while the engine lock is held (between <see cref="EnterLockForGcAsync"/>
    /// and <see cref="ExitLockForGc"/>). The returned delegate populates the supplied set and
    /// returns <see langword="true"/> when the scan completed over every captured reader, or
    /// <see langword="false"/> when a reader was disposed by a concurrent compaction mid-scan
    /// (the lock-free scan races segment retirement) — in which case the liveness set is
    /// partial and the caller MUST NOT reclaim any file this round (fail-closed: a partial
    /// scan can omit a live pointer, so reclaiming on it could free a referenced value-log file).
    /// </summary>
    Func<HashSet<(uint FileIndex, ulong Offset)>, bool> SnapshotSegmentsUnderLock();
    /// <summary>
    /// Asynchronously acquires the engine lock before GC state is read. Awaiting this method
    /// releases the thread-pool thread while the semaphore is contested, preventing thread-pool
    /// starvation when the GC path is entered from an async context.
    /// </summary>
    ValueTask EnterLockForGcAsync(CancellationToken ct = default);
    void ExitLockForGc();
}
