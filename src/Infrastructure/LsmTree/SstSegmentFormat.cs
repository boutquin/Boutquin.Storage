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
using Boutquin.Storage.Domain.Interfaces.Compression;
using Boutquin.Storage.Domain.ValueObjects.SortedStringTable;

namespace Boutquin.Storage.Infrastructure.LsmTree;

/// <summary>
/// P4.7 — internal contract an <see cref="ISegmentReader{TKey,TValue}"/> may implement to
/// expose the CRC and size that were accumulated inline during the write pass. When present,
/// the LSM engine reads these directly instead of re-scanning the segment file via
/// <see cref="SegmentManifest.ComputeFileCrc32C"/>.
/// </summary>
internal interface ISegmentReaderWithIntegrity
{
    uint WrittenFileCrc32C { get; }
    long WrittenFileSizeBytes { get; }
}

/// <summary>
/// Opt-in capability for <see cref="ISegmentReader{TKey,TValue}"/> implementations that
/// support block-level seek. The LSM engine's <c>GetRangeAsync</c> path checks for this
/// interface at runtime; non-SST readers (e.g., columnar) fall back to the full-scan
/// <see cref="ISegmentReader{TKey,TValue}.StreamBlockEntriesAsync"/> path.
/// </summary>
/// <typeparam name="TKey">The key type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
internal interface ISstSegmentSeekable<TKey, TValue>
    where TKey : IStorageKey<TKey>, new()
    where TValue : ISerializable<TValue>, new()
{
    /// <summary>
    /// Streams every segment entry whose key is <c>≥ <paramref name="startKey"/></c> and whose
    /// per-entry seqno is <c>≤ <paramref name="maxSequenceNumber"/></c>, in ascending key order.
    /// Uses the SSTable sparse block index to skip whole data blocks that provably lie before
    /// <paramref name="startKey"/>, reducing the on-disk read cost from
    /// O(keys-before-startKey) to O(blocks-before-startKey) + O(entries-in-starting-block).
    /// </summary>
    /// <param name="startKey">Inclusive lower bound; entries whose key is less than this are skipped.</param>
    /// <param name="maxSequenceNumber">Inclusive upper bound on per-entry seqno.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    IAsyncEnumerable<SegmentWriteEntry<TKey, TValue>> StreamBlockEntriesFromAsync(
        TKey startKey,
        ulong maxSequenceNumber = ulong.MaxValue,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Block-structured <see cref="ISegmentFormat{TKey, TValue}"/>, backed by
/// <c>SortedStringTable&lt;TKey, TValue&gt;</c>. The default row-oriented LSM segment format
/// shipped alongside the opt-in columnar format at v2.0.0 GA. Every flushed entry carries the LSM-assigned sequence number on disk so that
/// recovery can reseed the engine's monotonic counter to
/// <c>max(sst_max, wal_max) + 1</c>.
/// </summary>
/// <typeparam name="TKey">The type of the keys in segments this format writes.</typeparam>
/// <typeparam name="TValue">The type of the values in segments this format writes.</typeparam>
/// <remarks>
/// <para><b>Behaviour.</b> Every call to <see cref="WriteAsync"/> materialises one
/// <c>SortedStringTable&lt;TKey, TValue&gt;</c> and hands the entries to its single
/// seqno + value-kind-carrying <c>Write</c> method. Segment files carry the <c>.sst</c>
/// extension.</para>
/// <para><b>Construction.</b> Created with the given block-layout knobs and an optional shared
/// block cache. When <paramref name="blockCache"/> is non-null, every SortedStringTable
/// instantiated by this format participates in the cache under a segment identity unique to
/// that table instance within the process, so engines, folders and reopened stores sharing one
/// cache never read each other's blocks.</para>
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0027", Justification = "The additive Bloom overload has a required options discriminator; the existing constructor signature is preserved.")]
public sealed class SstSegmentFormat<TKey, TValue>(
    ICompressionCodec? codec = null,
    int blockSizeBytes = SortedStringTable.SortedStringTable<TKey, TValue>.DefaultBlockSizeBytes,
    int restartInterval = SortedStringTable.SortedStringTable<TKey, TValue>.DefaultRestartInterval,
    ICompressionCodecRegistry? codecRegistry = null,
    IBlockCache? blockCache = null) : ISegmentFormat<TKey, TValue>, ISegmentEntrySizeLimit
    where TKey : IStorageKey<TKey>, new()
    where TValue : ISerializable<TValue>, new()
{
    private readonly ICompressionCodec? _codec = codec;
    private readonly int _blockSizeBytes = blockSizeBytes;
    private readonly int _restartInterval = restartInterval;
    private readonly ICompressionCodecRegistry? _codecRegistry = codecRegistry;
    private readonly IBlockCache? _blockCache = blockCache;
    private readonly SegmentBloomFilterOptions<TKey> _bloomOptions = new();

    /// <summary>Uses explicit Bloom options with the default LZ4 block layout and no shared cache.</summary>
    /// <param name="bloomOptions">Borrowed immutable options and codec; segment readers own their filters.</param>
    public SstSegmentFormat(SegmentBloomFilterOptions<TKey> bloomOptions)
        : this(bloomOptions, null, 16384, 16, null, null) { }

    /// <summary>Configures physical filters for every flush and compaction; opening uses the persisted state.</summary>
    /// <param name="bloomOptions">Borrowed immutable options and codec; segment readers own their filters.</param>
    /// <param name="codec">Borrowed compression codec, or null for LZ4.</param>
    /// <param name="blockSizeBytes">Positive target block size.</param>
    /// <param name="restartInterval">Positive prefix restart interval.</param>
    /// <param name="codecRegistry">Borrowed compression registry.</param>
    /// <param name="blockCache">Borrowed block cache.</param>
    /// <exception cref="ArgumentNullException">Options are null.</exception>
    public SstSegmentFormat(
        SegmentBloomFilterOptions<TKey> bloomOptions,
        ICompressionCodec? codec,
        int blockSizeBytes,
        int restartInterval,
        ICompressionCodecRegistry? codecRegistry,
        IBlockCache? blockCache)
        : this(codec, blockSizeBytes, restartInterval, codecRegistry, blockCache)
    {
        ArgumentNullException.ThrowIfNull(bloomOptions);
        _bloomOptions = bloomOptions;
    }

    /// <summary>The block cache every table this format opens reads through, or <see langword="null"/>.</summary>
    internal IBlockCache? BlockCache => _blockCache;

    /// <inheritdoc />
    public string FileExtension => ".sst";

    /// <inheritdoc />
    long ISegmentEntrySizeLimit.MaxEntryKeyAndValueBytes
        => SortedStringTable.SortedStringTable<TKey, TValue>.MaxEntryKeyAndValueBytes;

    /// <inheritdoc />
    /// <remarks>
    /// SSTable entries persist a per-entry sequence number in the C6 footer slot, which the
    /// LSM engine uses to filter snapshot-visible entries during point lookups and scans.
    /// </remarks>
    public bool SupportsSnapshots => true;

    /// <inheritdoc />
    /// <remarks>
    /// Every SSTable entry carries a value-kind discriminator, so a 12-byte value-log pointer is
    /// stored in place of a value the engine routed to the value log.
    /// </remarks>
    public bool SupportsValueLogPointers => true;

    /// <inheritdoc />
    public Task<ISegmentReader<TKey, TValue>> WriteAsync(
        string segmentFolder,
        string segmentFileName,
        IReadOnlyList<SegmentWriteEntry<TKey, TValue>> sortedEntries,
        IReadOnlyDictionary<TKey, ulong>? tombstones = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(segmentFolder);
        ArgumentNullException.ThrowIfNull(segmentFileName);
        ArgumentNullException.ThrowIfNull(sortedEntries);
        cancellationToken.ThrowIfCancellationRequested();

        var sst = BuildSsTable(segmentFolder, segmentFileName, initializeAbsent: false);
        try
        {
            sst.Write(sortedEntries, tombstones);
        }
        catch
        {
            sst.Dispose();
            throw;
        }
        return Task.FromResult<ISegmentReader<TKey, TValue>>(new SstSegmentReader(sst));
    }

    /// <inheritdoc />
    public ISegmentReader<TKey, TValue> Open(string segmentFolder, string segmentFileName)
    {
        ArgumentNullException.ThrowIfNull(segmentFolder);
        ArgumentNullException.ThrowIfNull(segmentFileName);

        var sst = BuildSsTable(segmentFolder, segmentFileName);
        return new SstSegmentReader(sst);
    }

    // Each table instance takes a cache identity no other instance in the process shares (the
    // table issues it at construction). A name derived from the file (the counter in
    // seg_NNNNNN.sst) is not unique over a cache's lifetime: every engine numbers its own folder
    // from 0, and a store reopened after a clear numbers its folder from 0 again, so a shared cache
    // would answer a lookup in one file with another file's decompressed block. An
    // instance-scoped identity cannot alias, and the engine opens each segment file through one
    // instance, so it keeps that file's cache hits.
    private SortedStringTable.SortedStringTable<TKey, TValue> BuildSsTable(string segmentFolder, string segmentFileName, bool initializeAbsent = true)
        => new(_bloomOptions, segmentFolder, segmentFileName, _codec, _blockSizeBytes, _restartInterval, _codecRegistry, _blockCache, initializeAbsent);

    private sealed class SstSegmentReader(SortedStringTable.SortedStringTable<TKey, TValue> inner)
        : ISegmentReader<TKey, TValue>, ISegmentReaderWithIntegrity, ISstSegmentSeekable<TKey, TValue>, IDisposable
    {
        public void Dispose() => _inner.Dispose();
        private readonly SortedStringTable.SortedStringTable<TKey, TValue> _inner = inner;

        // P4.7 — surfaces the CRC and size that were accumulated inline during WriteToDisk
        // so the engine never needs to re-read the file after a flush.
        uint ISegmentReaderWithIntegrity.WrittenFileCrc32C => _inner.WrittenFileCrc32C;
        long ISegmentReaderWithIntegrity.WrittenFileSizeBytes => _inner.WrittenFileSizeBytes;

        public Task<(BlockEntry Entry, bool Found)> TryGetBlockEntryAsync(
            TKey key, ulong maxSequenceNumber = ulong.MaxValue, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var found = _inner.TryGetBlockEntry(key, out var entry, maxSequenceNumber);
            return Task.FromResult((found ? entry : default, found));
        }

        public async IAsyncEnumerable<SegmentWriteEntry<TKey, TValue>> StreamBlockEntriesAsync(
            ulong maxSequenceNumber = ulong.MaxValue,
            ScanDirection direction = ScanDirection.Ascending,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (direction != ScanDirection.Ascending)
            {
                // SST stores entries in ascending key + ascending seqno order on disk; reverse
                // iteration would require either a separate footer index or a buffered scan.
                // v2.0.0 ships ascending only — match the columnar inner-format contract.
                throw new ArgumentOutOfRangeException(nameof(direction),
                    $"ScanDirection {direction} is not supported in v2.0.0; only Ascending is implemented.");
            }

            // The underlying block enumeration performs its disk reads synchronously inside the
            // IEnumerable's MoveNext (block-at-a-time via BlockFormat.ReadBlock); the former
            // per-item `Task.Yield()` did not gate that I/O — it only posted a thread-pool
            // continuation for every entry. Satisfy the async-iterator contract once with a
            // synchronously-completed await; cooperative cancellation is the per-item check.
            await Task.CompletedTask.ConfigureAwait(false);
            foreach (var e in _inner.EnumerateBlockEntriesFiltered(maxSequenceNumber))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return BlockEntryToSegmentWriteEntry(e);
            }
        }

        /// <inheritdoc/>
        public async IAsyncEnumerable<SegmentWriteEntry<TKey, TValue>> StreamBlockEntriesFromAsync(
            TKey startKey,
            ulong maxSequenceNumber = ulong.MaxValue,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            // Synchronous block reads — satisfy the async-iterator contract once.
            await Task.CompletedTask.ConfigureAwait(false);
            foreach (var e in _inner.EnumerateBlockEntriesFrom(startKey, maxSequenceNumber))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return BlockEntryToSegmentWriteEntry(e);
            }
        }

        public Task ClearAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _inner.Delete(FileDeletionHandling.DeleteIfExists);
            _inner.Dispose();
            return Task.CompletedTask;
        }

        public ulong MaxSequenceNumber => _inner.MaxSequenceNumber;

        public bool TryGetRange(out TKey min, out TKey max) => _inner.TryGetRange(out min, out max);

        public IReadOnlyDictionary<TKey, ulong> GetTombstoneSeqnos() => _inner.GetTombstoneSeqnos();

        public Task<(TValue Value, ulong SequenceNumber, bool Found)> TryGetValueWithSeqnoAsync(
            TKey key, ulong maxSequenceNumber, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_inner.TryGetBlockEntry(key, out var entry, maxSequenceNumber))
            {
                return Task.FromResult((default(TValue)!, 0UL, false));
            }
            if (entry.Kind != ValueKind.Inline)
            {
                // Inline-only wrapper — pointer entries are not deserialised at the segment layer.
                return Task.FromResult((default(TValue)!, 0UL, false));
            }
            using var ms = new MemoryStream(entry.Value.ToArray(), writable: false);
            var value = TValue.Deserialize(ms);
            return Task.FromResult((value, entry.SequenceNumber, true));
        }

        private static SegmentWriteEntry<TKey, TValue> BlockEntryToSegmentWriteEntry(BlockEntry entry)
        {
            using var keyMs = new MemoryStream(entry.Key.ToArray(), writable: false);
            var key = TKey.Deserialize(keyMs);
            if (entry.Kind == ValueKind.Inline)
            {
                using var valMs = new MemoryStream(entry.Value.ToArray(), writable: false);
                var value = TValue.Deserialize(valMs);
                return SegmentWriteEntry<TKey, TValue>.Inline(key, value, entry.SequenceNumber);
            }
            return SegmentWriteEntry<TKey, TValue>.Pointer(key, entry.Value, entry.SequenceNumber);
        }
    }
}
