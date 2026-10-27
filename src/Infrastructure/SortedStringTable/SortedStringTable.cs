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
using Boutquin.Storage.Infrastructure.Compression;
using Boutquin.Storage.Infrastructure.DataStructures;
using Boutquin.Storage.Infrastructure.KeyValueStore;

namespace Boutquin.Storage.Infrastructure.SortedStringTable;

/// <summary>
/// Sorted String Table (SSTable) backed by a <see cref="StorageFile"/>, using the
/// block-structured v1 on-disk format introduced at v2.0.0 GA.
/// </summary>
/// <typeparam name="TKey">The type of the keys in the SSTable.</typeparam>
/// <typeparam name="TValue">The type of the values in the SSTable.</typeparam>
/// <remarks>
/// <para><b>On-disk format:</b>
/// <c>[block 0][block 1]...[block N-1][footer][4B footer_length]</c>. Each block is
/// framed as <c>[4B uncompressed_length][4B compressed_length][compressed payload][4B CRC32C]</c>
/// and holds a prefix-compressed entry stream with restart points every
/// <c>RestartInterval</c> entries. The footer carries the block index (first-key →
/// offset), bloom-filter bits, and codec id. The footer is introduced by the
/// <c>'B','S','S','1'</c> magic — a file-family signature — and carries an independent
/// <c>format_version</c> byte (currently 1). See <see cref="SsTableFooter"/> for the
/// magic-vs-version split rationale.</para>
/// <para><b>Single format.</b> v2.0.0 ships one SSTable format. A file whose tail does
/// not resolve to a valid footer (via <see cref="SsTableFooter.Deserialize"/>) is
/// either truncated, corrupt or non-SSTable and raises <see cref="InvalidDataException"/> on
/// open, carrying the failed footer check as its inner exception. There is no fallback reader.</para>
/// <para><b>Thread safety:</b> Not thread-safe. External synchronization required.</para>
/// <para><b>Reference:</b> DDIA 2e p. 119.</para>
/// </remarks>
public sealed class SortedStringTable<TKey, TValue> : ISortedStringTable<TKey, TValue>
    where TKey : IStorageKey<TKey>, new()
    where TValue : ISerializable<TValue>, new()
{
    public const int DefaultBlockSizeBytes = 16 * 1024;
    public const int DefaultRestartInterval = 16;

    /// <summary>
    /// The most serialized key-plus-value bytes one entry can carry: a block holding that entry
    /// alone, at the worst-case per-entry overhead, still fits the ceiling the block readers enforce.
    /// </summary>
    internal const long MaxEntryKeyAndValueBytes =
        (long)BlockFormat.MaxUncompressedBlockBytes - BlockFormat.BlockTrailerBytes - BlockFormat.MaxEntryOverheadBytes;

    private readonly KeyValueStore.StorageFile _storageFile;
    private readonly ICompressionCodec _codec;
    private readonly ICompressionCodecRegistry _codecRegistry;
    private readonly int _blockSizeBytes;
    private readonly int _restartInterval;

    // Per-block cache seam (DDIA Ch. 4 p. 79). When _blockCache is non-null, point lookups consult
    // the cache before touching disk; on miss, the decompressed payload is inserted with
    // cost == payload.Length. The cache key's segment identity must be unique among every file
    // sharing the cache, so the table takes it from the one process-wide sequence at construction
    // rather than from its caller: an identity a caller chose could equal one already issued to
    // another table, which would then be answered with that table's cached block. _segmentId is
    // 0 (caching off) only when no cache was supplied. A Write replaces the file, so it takes a
    // fresh identity: blocks cached for the previous contents are keyed by the retired identity
    // and can never answer a lookup of the new file (they age out of the cache).
    private readonly IBlockCache? _blockCache;
    private long _segmentId;

    // Loaded at construction when an SSTable footer is detected, or after Write.
    private SsTableFooter? _footer;
    private SegmentBloomFilter<TKey>? _bloom;
    private readonly SegmentBloomFilterOptions<TKey> _bloomOptions;
    private readonly bool _initializing;
    private long _dataEnd;
    private ulong _maxSeqno;
    private ICompressionCodec? _readCodec;
    private volatile bool _deleted;
    private volatile bool _disposed;

    // Decoded MinKey/MaxKey cache. The footer's MinKey/MaxKey live as ReadOnlyMemory<byte>
    // and are immutable once the segment is open, so the TKey decode amortises to one call
    // per bound per segment-lifetime. Compaction-strategy decisions consult TryGetRange on
    // every flush + compaction tick (BuildSegmentMetadataList), which would otherwise pay
    // a fresh ToArray()+Deserialize per call per segment.
    private bool _rangeCached;
    private TKey? _cachedMin;
    private TKey? _cachedMax;

    // P4.7 — CRC and size accumulated inline during WriteToDisk so the flush path never
    // re-reads its own bytes. Zero until the first Write call completes.
    internal uint WrittenFileCrc32C { get; private set; }
    internal long WrittenFileSizeBytes { get; private set; }

    /// <summary>
    /// Initializes an SSTable bound to <paramref name="fileLocation"/>/<paramref name="fileName"/>.
    /// When <paramref name="codec"/> is null, the default LZ4 codec is used. When a block cache is
    /// supplied, the table caches its decompressed blocks there under a segment identity it takes
    /// from a process-wide sequence, so no two tables sharing the cache ever read each other's blocks.
    /// An absent file is initialized as a valid empty table; an existing invalid file fails the open.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0027", Justification = "The additive Bloom overload has a required options discriminator; the existing constructor signature is preserved.")]
    public SortedStringTable(
        string fileLocation,
        string fileName,
        ICompressionCodec? codec = null,
        int blockSizeBytes = DefaultBlockSizeBytes,
        int restartInterval = DefaultRestartInterval,
        ICompressionCodecRegistry? codecRegistry = null,
        IBlockCache? blockCache = null)
        : this(new SegmentBloomFilterOptions<TKey>(), fileLocation, fileName, codec, blockSizeBytes, restartInterval, codecRegistry, blockCache)
    {
    }

    /// <summary>Opens with explicit Bloom options and the default LZ4 block layout, without a shared cache.</summary>
    /// <param name="bloomOptions">Borrowed immutable settings.</param>
    /// <param name="fileLocation">Containing directory.</param>
    /// <param name="fileName">Table filename.</param>
    public SortedStringTable(SegmentBloomFilterOptions<TKey> bloomOptions, string fileLocation, string fileName)
        : this(bloomOptions, fileLocation, fileName, null, DefaultBlockSizeBytes, DefaultRestartInterval, null, null) { }

    /// <summary>Opens a table with explicit persisted Bloom configuration. Existing files use their stored filter.</summary>
    /// <param name="bloomOptions">Borrowed immutable options; created/restored filters are owned by this table.</param>
    /// <param name="fileLocation">Containing directory.</param>
    /// <param name="fileName">Table filename; an absent file is initialized as an empty native table.</param>
    /// <param name="codec">Borrowed block codec, or null for LZ4.</param>
    /// <param name="blockSizeBytes">Positive target block size.</param>
    /// <param name="restartInterval">Positive prefix restart interval.</param>
    /// <param name="codecRegistry">Borrowed compression registry for reopening.</param>
    /// <param name="blockCache">Borrowed shared cache.</param>
    /// <exception cref="ArgumentNullException">Options are null.</exception>
    /// <exception cref="InvalidDataException">Persisted metadata or custom Bloom codec is invalid or unavailable.</exception>
    public SortedStringTable(
        SegmentBloomFilterOptions<TKey> bloomOptions,
        string fileLocation,
        string fileName,
        ICompressionCodec? codec,
        int blockSizeBytes,
        int restartInterval,
        ICompressionCodecRegistry? codecRegistry,
        IBlockCache? blockCache)
        : this(bloomOptions, fileLocation, fileName, codec, blockSizeBytes, restartInterval, codecRegistry, blockCache, true)
    {
    }

    internal SortedStringTable(
        SegmentBloomFilterOptions<TKey> bloomOptions, string fileLocation, string fileName,
        ICompressionCodec? codec, int blockSizeBytes, int restartInterval,
        ICompressionCodecRegistry? codecRegistry, IBlockCache? blockCache, bool initializeAbsent)
    {
        ArgumentNullException.ThrowIfNull(bloomOptions);
        _bloomOptions = bloomOptions;
        Guard.AgainstNullOrWhiteSpace(() => fileLocation);
        Guard.AgainstNullOrWhiteSpace(() => fileName);
        Guard.AgainstNegativeOrZero(() => blockSizeBytes);
        Guard.AgainstNegativeOrZero(() => restartInterval);

        _storageFile = new KeyValueStore.StorageFile(fileLocation, fileName, createIfMissing: false);
        _codec = codec ?? new LZ4Codec();
        _codecRegistry = codecRegistry ?? new DefaultCompressionCodecRegistry();
        _blockSizeBytes = blockSizeBytes;
        _restartInterval = restartInterval;
        _blockCache = blockCache;
        _segmentId = blockCache is null ? 0 : BlockCacheSegmentIdentity.Next();

        if (_storageFile.Exists())
        {
            TryOpenExistingFile();
        }
        else if (initializeAbsent)
        {
            // A newly bound table is a valid empty table, including its footer. Leaving an
            // empty placeholder would make a later open indistinguishable from truncation.
            _initializing = true;
            Write([]);
            _initializing = false;
        }
    }

    /// <inheritdoc/>
    public void Write(IEnumerable<SegmentWriteEntry<TKey, TValue>> items)
        => Write(items, tombstones: null);

    /// <summary>
    /// Writes <paramref name="items"/> to the segment, optionally persisting a
    /// <paramref name="tombstones"/> map (deleted key → deletion seqno) into the footer.
    /// The compactor's tombstone-GC step uses the persisted seqno to prove that no live
    /// entry older than the deletion survives in any other segment before reclaiming the
    /// marker (DDIA Ch. 4 p. 124). Pass <see langword="null"/> to write a tombstone-free
    /// segment — recovery treats the absence as an empty tombstone set.
    /// </summary>
    public void Write(
        IEnumerable<SegmentWriteEntry<TKey, TValue>> items,
        IReadOnlyDictionary<TKey, ulong>? tombstones)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Guard.AgainstNullOrDefault(() => items);

        // Materialise into a list this call owns, and validate and write that one list. A caller's
        // list is copied too: the order check reads it through the indexer and the writer through
        // the enumerator, and a list that changes between those two traversals (another writer, or
        // an indexer and enumerator that disagree) would have one order checked and another written.
        List<SegmentWriteEntry<TKey, TValue>> entries = [.. items];

        for (var i = 0; i < entries.Count; i++)
        {
            ValidateEntryInvariant(entries[i], i);
            if (i > 0 && entries[i].Key.CompareTo(entries[i - 1].Key) <= 0)
            {
                throw new ArgumentException(
                    $"Items must be sorted in strictly ascending key order. Key at index {i} is not greater than key at index {i - 1}.",
                    nameof(items));
            }
        }

        WriteToDisk(entries, tombstones ?? s_emptyTombstones);
    }

    private static readonly IReadOnlyDictionary<TKey, ulong> s_emptyTombstones =
        new Dictionary<TKey, ulong>();

    /// <summary>
    /// Decodes the footer's persisted tombstone list into a <typeparamref name="TKey"/> →
    /// deletion-seqno map. Returns an empty map when the segment is unwritten or the footer
    /// carries no tombstones. The tombstone seqnos are the load-bearing input the compactor
    /// uses to safely reclaim deletion markers (DDIA Ch. 4 p. 124).
    /// </summary>
    public IReadOnlyDictionary<TKey, ulong> GetTombstoneSeqnos()
    {
        ThrowIfUnavailable();
        if (_footer is null || _footer.Tombstones.Count == 0)
        {
            return s_emptyTombstones;
        }

        var map = new Dictionary<TKey, ulong>(_footer.Tombstones.Count);
        foreach (var t in _footer.Tombstones)
        {
            var key = FromKeyBytes(t.Key.ToArray());
            map[key] = t.SequenceNumber;
        }
        return map;
    }

    private static void ValidateEntryInvariant(SegmentWriteEntry<TKey, TValue> entry, int index)
    {
        switch (entry.Kind)
        {
            case ValueKind.Inline:
                if (!entry.PointerBytes.IsEmpty)
                {
                    throw new ArgumentException(
                        $"Entry at index {index} has Kind=Inline but PointerBytes is non-empty; violates SegmentWriteEntry invariant.",
                        nameof(entry));
                }
                break;
            case ValueKind.ValueLogPointer:
                if (entry.PointerBytes.Length != Domain.ValueObjects.ValueLog.ValueLogPointer.SerializedSize)
                {
                    throw new ArgumentException(
                        $"Entry at index {index} has Kind=ValueLogPointer but PointerBytes.Length={entry.PointerBytes.Length}; expected {Domain.ValueObjects.ValueLog.ValueLogPointer.SerializedSize}.",
                        nameof(entry));
                }
                break;
            default:
                // Routing-invariant violation. The LSM engine's flush path stamps every entry
                // with a ValueKind drawn from the {Inline, ValueLogPointer} enumeration. A new
                // ValueKind member added upstream without extending this validator's switch
                // (and the matching switch in WriteToDisk) would silently fall through here.
                // Name the contract explicitly so the diagnostic points at the drift —
                // analogous to CompressedColumnarSegmentFormat.DecodeColumn's non-restartable
                // routing guard.
                throw new ArgumentException(
                    $"Entry at index {index} has Kind={entry.Kind} (raw byte 0x{(byte)entry.Kind:X2}). " +
                    "SortedStringTable supports only ValueKind.Inline (0x00) and " +
                    "ValueKind.ValueLogPointer (0x01); reaching this path indicates a drift " +
                    "between the LSM engine's value-classification routing and the SSTable " +
                    "writer's enumeration. If a new ValueKind is introduced upstream, both " +
                    "ValidateEntryInvariant and WriteToDisk's per-entry switch must be extended " +
                    "in lockstep.",
                    nameof(entry));
        }
    }

    /// <inheritdoc/>
    public IEnumerable<(TKey Key, TValue Value, ulong SequenceNumber)> EnumerateInline(ulong maxSequenceNumber = ulong.MaxValue)
    {
        ThrowIfUnavailable();
        if (_footer is null)
        {
            yield break;
        }

        foreach (var entry in EnumerateBlockEntries())
        {
            if (entry.SequenceNumber > maxSequenceNumber)
            {
                continue;
            }

            // Pointer entries are skipped — the raw 12-byte pointer body cannot be decoded
            // as TValue at this layer. Pointer-aware callers iterate BlockEntry via
            // EnumerateBlockEntriesFiltered.
            if (entry.Kind != ValueKind.Inline)
            {
                continue;
            }

            yield return (
                FromKeyBytes(entry.Key.ToArray()),
                FromBytes(entry.Value.ToArray()),
                entry.SequenceNumber);
        }
    }

    /// <summary>
    /// Streams every persisted <see cref="BlockEntry"/> whose seqno is
    /// <c>≤ <paramref name="maxSequenceNumber"/></c>, surfacing the raw discriminated
    /// representation so callers can skip <c>TValue.Deserialize</c> on pointer entries.
    /// </summary>
    public IEnumerable<BlockEntry> EnumerateBlockEntriesFiltered(ulong maxSequenceNumber = ulong.MaxValue)
    {
        ThrowIfUnavailable();
        if (_footer is null)
        {
            yield break;
        }

        foreach (var entry in EnumerateBlockEntries())
        {
            if (entry.SequenceNumber > maxSequenceNumber)
            {
                continue;
            }
            yield return entry;
        }
    }

    /// <inheritdoc/>
    public ulong MaxSequenceNumber
    {
        get
        {
            ThrowIfUnavailable();
            return _maxSeqno;
        }
    }

    /// <inheritdoc/>
    public bool TryGetValue(TKey key, out TValue value, ulong maxSequenceNumber = ulong.MaxValue)
    {
        value = default!;
        if (!TryGetBlockEntry(key, out var entry, maxSequenceNumber))
        {
            return false;
        }
        if (entry.Kind != ValueKind.Inline)
        {
            // Pointer entries cannot be resolved to TValue at the SSTable layer — the engine's
            // read path dereferences via ValueLogReader. Surface as miss to the
            // ISortedStringTable caller that only understands inline values.
            return false;
        }
        value = FromBytes(entry.Value.ToArray());
        return true;
    }

    /// <summary>
    /// Finds the raw <see cref="BlockEntry"/> for <paramref name="key"/> visible under the
    /// seqno filter. The engine's read path dispatches on <see cref="BlockEntry.Kind"/>; inline
    /// entries deserialise directly, pointer entries decode the 12-byte body and read through
    /// the value-log reader.
    /// </summary>
    public bool TryGetBlockEntry(TKey key, out BlockEntry entry, ulong maxSequenceNumber = ulong.MaxValue)
    {
        ThrowIfUnavailable();
        Guard.AgainstNullOrDefault(() => key);

        entry = default;
        if (_footer is null || _bloom is null)
        {
            return false;
        }

        var keyBytes = ToBytes(key);

        if (!_bloom.Contains(key, keyBytes))
        {
            return false;
        }

        var candidate = SelectBlock(_footer.BlockIndex, key);
        if (candidate < 0)
        {
            return false;
        }

        var candidateOffset = _footer.BlockIndex[candidate].Offset;
        var nextOffset = candidate + 1 < _footer.BlockIndex.Count
            ? _footer.BlockIndex[candidate + 1].Offset
            : _dataEnd;

        // Block-cache seam (DDIA Ch. 4 p. 79). On hit, the decompressed payload is reused
        // without touching disk or the codec. On miss, fall through to the on-disk read +
        // decompress and cache the resulting payload for the next lookup.
        var cacheActive = _blockCache is not null && _segmentId != 0;
        var cacheKey = cacheActive
            ? new BlockCacheKey(_segmentId, candidateOffset)
            : default;
        byte[]? payload = null;
        if (cacheActive && _blockCache!.TryGet(cacheKey, out var cached))
        {
            payload = cached;
        }

        if (payload is null)
        {
            using var stream = OpenIndependentReadStream();
            stream.Seek(candidateOffset, SeekOrigin.Begin);

            var blockLen = (int)(nextOffset - candidateOffset);
            var blockBytes = new byte[blockLen];
            ReadExact(stream, blockBytes);

            // P4.6 — decompress once, binary-search restart offsets to skip ahead, then
            // linearly scan only the target section. Avoids decoding ~(restartInterval - 1)
            // entries before the target for a cost of O(log R) key comparisons.
            payload = BlockFormat.DecompressPayload(blockBytes, ReadCodec);
            if (cacheActive)
            {
                _blockCache!.Put(cacheKey, payload, payload.Length);
            }
        }
        var restartOffsets = BlockFormat.ParseRestartOffsets(payload);
        var entriesEnd = BlockFormat.GetEntriesEnd(payload);
        var startOffset = BinarySearchRestartForKey(payload, restartOffsets, key);

        foreach (var e in BlockFormat.DecodeFromOffset(payload, startOffset, entriesEnd))
        {
            var entryKey = FromKeyBytes(e.Key.ToArray());
            var cmp = entryKey.CompareTo(key);
            if (cmp == 0)
            {
                if (e.SequenceNumber > maxSequenceNumber)
                {
                    return false;
                }
                entry = e;
                return true;
            }
            if (cmp > 0)
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Surfaces the SST footer's <c>MinKey</c> / <c>MaxKey</c> as decoded <typeparamref name="TKey"/>
    /// values for engine consumers that need a per-segment range filter without scanning every
    /// block. Returns <see langword="false"/> when the segment is empty or has not been written /
    /// opened yet.
    /// </summary>
    /// <param name="min">When the call returns <see langword="true"/>, the lowest key persisted.</param>
    /// <param name="max">When the call returns <see langword="true"/>, the highest key persisted.</param>
    /// <returns><see langword="true"/> when the segment carries at least one entry; otherwise <see langword="false"/>.</returns>
    /// <remarks>
    /// The footer MinKey/MaxKey slots are the canonical record of the segment's key range — see
    /// <see cref="SsTableFooter"/>. Decoding amortises one <see cref="ISerializable{T}.Deserialize"/>
    /// call per bound, well below the cost of the previous engine-side block scan.
    /// </remarks>
    public bool TryGetRange(out TKey min, out TKey max)
    {
        ThrowIfUnavailable();
        if (_footer is null || _footer.EntryCount == 0)
        {
            min = default!;
            max = default!;
            return false;
        }

        if (!_rangeCached)
        {
            using (var minStream = new MemoryStream(_footer.MinKey.ToArray(), writable: false))
            {
                _cachedMin = TKey.Deserialize(minStream);
            }
            using (var maxStream = new MemoryStream(_footer.MaxKey.ToArray(), writable: false))
            {
                _cachedMax = TKey.Deserialize(maxStream);
            }
            _rangeCached = true;
        }

        min = _cachedMin!;
        max = _cachedMax!;
        return true;
    }

    /// <inheritdoc/>
    public int GetEntryCount()
    {
        ThrowIfUnavailable();
        return _footer is not null ? (int)_footer.EntryCount : 0;
    }

    /// <inheritdoc/>
    public DateTime GetCreationTime() => File.GetCreationTime(_storageFile.FilePath);

    /// <inheritdoc/>
    public DateTime GetLastModificationTime() => File.GetLastWriteTime(_storageFile.FilePath);

    // ---- file surface ----
    //
    // Deliberately no raw whole-file mutator (write/append bytes or text, Create, a writable Open):
    // _footer, _bloom, _dataEnd, _maxSeqno, _readCodec, the decoded range and the block-cache
    // identity are all derived from the bytes this table wrote, so a raw rewrite would leave the
    // instance answering from bytes no longer on disk. Write replaces the contents; Delete removes
    // them and rejects content reads until the next successful Write.

    public string FilePath => _storageFile.FilePath;
    public long FileSize => _storageFile.FileSize;
    public string FileName => _storageFile.FileName;
    public string FileLocation => _storageFile.FileLocation;
    public bool Exists() => _storageFile.Exists();

    /// <inheritdoc/>
    public void Delete(FileDeletionHandling deletionHandling)
    {
        _storageFile.Delete(deletionHandling);
        // Retain immutable read metadata behind the retired flag. A lock-free scan that passed
        // its first guard just before this delete must never mistake a nulled footer for an empty
        // table; it either scans the original contents or fails a later lifetime/file check.
        _deleted = true;
        WrittenFileCrc32C = 0;
        WrittenFileSizeBytes = 0;
    }

    public byte[] ReadBytes(long offset, long count)
    {
        ThrowIfUnavailable();
        return _storageFile.ReadBytes(offset, count);
    }
    public Task<byte[]> ReadBytesAsync(long offset, long count, CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();
        return _storageFile.ReadBytesAsync(offset, count, cancellationToken);
    }
    public byte[] ReadAllBytes()
    {
        ThrowIfUnavailable();
        return _storageFile.ReadAllBytes();
    }
    public Task<byte[]> ReadAllBytesAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();
        return _storageFile.ReadAllBytesAsync(cancellationToken);
    }
    public string ReadAllText(Encoding encoding)
    {
        ThrowIfUnavailable();
        return _storageFile.ReadAllText(encoding);
    }
    public Task<string> ReadAllTextAsync(Encoding encoding, CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();
        return _storageFile.ReadAllTextAsync(encoding, cancellationToken);
    }
    public void Dispose()
    {
        _disposed = true;
        _bloom?.Dispose();
        _storageFile.Dispose();
    }

    private void ThrowIfUnavailable() => ObjectDisposedException.ThrowIf(_disposed || _deleted, this);

    // ---- write/read ----

    private void WriteToDisk(IList<SegmentWriteEntry<TKey, TValue>> items, IReadOnlyDictionary<TKey, ulong> tombstones)
    {
        var finalPath = _storageFile.FilePath;
        var tempPath = finalPath + ".tmp";

        var blockIndex = new List<SsTableBlockIndexEntry>();
        var bloom = SegmentBloomFilter<TKey>.Create(
            _initializing ? new SegmentBloomFilterOptions<TKey>() : _bloomOptions, items.Count);
        var transferred = false;
        try
        {

            // Per-entry encoded tuples. For Inline entries the value slot carries
            // TValue.Serialize output; for ValueLogPointer entries it carries the raw 12-byte
            // pointer body verbatim — the WiscKey invariant that the flush + compaction paths
            // never dereference through ValueLogReader lives here
            // Inline entries serialise via TValue.Serialize; pointer entries write the raw 12-byte body verbatim.
            var serialised = new List<(byte[] Key, byte[] Value, ulong Seqno, ValueKind Kind)>(items.Count);
            foreach (var entry in items)
            {
                var keyBytes = ToBytes(entry.Key);
                var valueBytes = entry.Kind switch
                {
                    ValueKind.Inline => ToBytes(entry.InlineValue!),
                    ValueKind.ValueLogPointer => entry.PointerBytes.ToArray(),
                    // Dead by construction — ValidateEntryInvariant runs first (see Write() at L115)
                    // and rejects every unsupported ValueKind before reaching this dispatch.
                    // Reaching here means the validator and the writer have drifted internally —
                    // promote to InvalidOperationException to signal "engine inconsistency", not
                    // caller error. Same shape as CompressedColumnarSegmentFormat.DecodeColumn's
                    // non-restartable routing guard.
                    _ => throw new InvalidOperationException(
                        $"SortedStringTable WriteToDisk reached its ValueKind dispatch default for " +
                        $"Kind={entry.Kind} (raw byte 0x{(byte)entry.Kind:X2}). ValidateEntryInvariant " +
                        "is supposed to catch unsupported ValueKind values before this switch runs; " +
                        "reaching here indicates ValidateEntryInvariant and WriteToDisk's switch " +
                        "have drifted. Extend both in lockstep when a new ValueKind ships."),
                };
                // Refuse, before the first output byte, an entry that no block can hold: both block
                // decoders reject a payload above the ceiling, so writing it would commit a segment
                // whose acknowledged data reads back as corrupt.
                if ((long)keyBytes.Length + valueBytes.Length > MaxEntryKeyAndValueBytes)
                {
                    throw new ArgumentException(
                        $"SSTable entry with a {keyBytes.Length}-byte key and a {valueBytes.Length}-byte value cannot fit " +
                        $"the {BlockFormat.MaxUncompressedBlockBytes}-byte block ceiling the reader enforces.",
                        nameof(items));
                }

                serialised.Add((keyBytes, valueBytes, entry.SequenceNumber, entry.Kind));
                bloom.Add(entry.Key, keyBytes);
            }

            using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);

            // P4.7 — accumulate CRC inline as blocks and footer are written so the engine
            // never needs to re-read the file to verify integrity. The running state starts at
            // InitialState; each FlushBlock call feeds the framed bytes; the footer is fed last.
            var crcState = Algorithms.Crc32C.InitialState;
            var currentBlock = new List<BlockEntry>();
            var currentBlockBytes = 0;
            var currentBlockWorstCaseBytes = (long)BlockFormat.BlockTrailerBytes;
            byte[]? currentBlockFirstKey = null;
            long currentBlockOffset = 0;
            var maxSeqnoThisWrite = 0UL;

            for (var i = 0; i < serialised.Count; i++)
            {
                var (key, value, seqno, kind) = serialised[i];
                if (seqno > maxSeqnoThisWrite)
                {
                    maxSeqnoThisWrite = seqno;
                }

                currentBlockFirstKey ??= key;
                var approxSize = key.Length + 8 + 1 + value.Length + 6;
                var worstCaseSize = WorstCaseEntryBytes(key, value);

                // The configured block size is a target; the reader's ceiling is a hard bound, so a
                // block also closes before its worst-case encoded payload could cross it.
                if (currentBlock.Count > 0
                    && (currentBlockBytes + approxSize > _blockSizeBytes
                        || currentBlockWorstCaseBytes + worstCaseSize > BlockFormat.MaxUncompressedBlockBytes))
                {
                    FlushBlock(fileStream, currentBlock, currentBlockFirstKey!, currentBlockOffset, blockIndex, ref crcState);
                    currentBlockOffset = fileStream.Position;
                    currentBlockFirstKey = key;
                    currentBlock.Clear();
                    currentBlockBytes = 0;
                    currentBlockWorstCaseBytes = BlockFormat.BlockTrailerBytes;
                }

                currentBlock.Add(new BlockEntry(key, seqno, kind, value));
                currentBlockBytes += approxSize;
                currentBlockWorstCaseBytes += worstCaseSize;
            }

            if (currentBlock.Count > 0)
            {
                FlushBlock(fileStream, currentBlock, currentBlockFirstKey!, currentBlockOffset, blockIndex, ref crcState);
            }

            var minKey = serialised.Count > 0 ? serialised[0].Key : [];
            var maxKey = serialised.Count > 0 ? serialised[^1].Key : [];

            // Encode tombstones to (key bytes + seqno) pairs, sorted by key for deterministic
            // on-disk layout. The compactor consumes them via GetTombstoneSeqnos after read.
            var tombstoneEntries = new List<SsTableTombstoneEntry>(tombstones.Count);
            if (tombstones.Count > 0)
            {
                foreach (var (k, seqno) in tombstones)
                {
                    tombstoneEntries.Add(new SsTableTombstoneEntry(ToBytes(k), seqno));
                }
            }

            var footer = new SsTableFooter(
                FormatVersion: SsTableFooter.CurrentFormatVersion,
                CodecId: _codec.CodecId,
                EntryCount: (uint)serialised.Count,
                MinKey: minKey,
                MaxKey: maxKey,
                MaxSequenceNumber: maxSeqnoThisWrite,
                BlockIndex: blockIndex,
                BloomBitsCount: bloom.BitsCount,
                BloomHashCount: bloom.HashCount,
                BloomBits: bloom.Bits.ToArray(),
                Tombstones: tombstoneEntries)
            {
                BloomState = bloom.IsCustom ? bloom.Serialize() : ReadOnlyMemory<byte>.Empty
            };

            if (!footer.BloomState.IsEmpty)
            {
                SegmentBloomFilter<TKey>.ValidateState(footer.BloomState, _bloomOptions, items.Select(entry => entry.Key));
            }
            var footerBytes = footer.Serialize();
            var dataEnd = fileStream.Position;
            fileStream.Write(footerBytes);
            // Include footer bytes in the CRC so the scope matches ComputeFileCrc32C (whole file).
            Algorithms.Crc32C.Append(footerBytes, ref crcState);
            fileStream.Flush(flushToDisk: true);
            var totalSizeBytes = fileStream.Position;
            fileStream.Close();

            // Use DurableFile.AtomicReplace rather than File.Move so the directory entry's
            // rename is fsync'd on POSIX before we return — without it, a crash between the
            // rename syscall and the directory-entry flush leaves the new segment invisible
            // at the next engine start, producing a CRC mismatch on recovery. DDIA 2nd ed.
            // p.121 "immutable segment files — crash recovery is simplified" depends on this
            // durability-of-commit property.
            DurableFile.AtomicReplace(tempPath, finalPath);

            _footer = footer;
            _bloom?.Dispose();
            _bloom = bloom;
            transferred = true;
            _dataEnd = dataEnd;
            _readCodec = null;
            _maxSeqno = maxSeqnoThisWrite;
            // Invalidate the decoded MinKey/MaxKey cache — the new footer carries a new range
            // (even when the new range happens to coincide with the previous one, the byte
            // representations may differ and the cache needs a fresh decode).
            _rangeCached = false;
            _cachedMin = default;
            _cachedMax = default;
            // The cached blocks of the previous file are keyed by the old identity; retire it.
            if (_blockCache is not null)
            {
                _segmentId = BlockCacheSegmentIdentity.Next();
            }
            WrittenFileCrc32C = Algorithms.Crc32C.Finalize(crcState);
            WrittenFileSizeBytes = totalSizeBytes;
            _deleted = false;
        }
        finally
        {
            if (!transferred)
            {
                bloom.Dispose();
            }
        }
    }

    private static long WorstCaseEntryBytes(byte[] key, byte[] value)
        => (long)key.Length + value.Length + BlockFormat.MaxEntryOverheadBytes;

    private void FlushBlock(
        FileStream fileStream,
        IReadOnlyList<BlockEntry> block,
        byte[] firstKey,
        long blockOffset,
        List<SsTableBlockIndexEntry> blockIndex,
        ref uint crcState)
    {
        var framed = BlockFormat.WriteBlock(block, _codec, _restartInterval);
        fileStream.Write(framed);
        Algorithms.Crc32C.Append(framed, ref crcState);
        blockIndex.Add(new SsTableBlockIndexEntry(firstKey, blockOffset));
    }

    private static int SelectBlock(IReadOnlyList<SsTableBlockIndexEntry> index, TKey key)
    {
        if (index.Count == 0)
        {
            return -1;
        }

        // Each index entry points to a block whose smallest key is FirstKey. Find the
        // block whose range (this.FirstKey ≤ k < next.FirstKey) contains the target.
        var lo = 0;
        var hi = index.Count - 1;
        var result = -1;
        while (lo <= hi)
        {
            var mid = lo + (hi - lo) / 2;
            var midKey = FromKeyBytes(index[mid].FirstKey.ToArray());
            var cmp = midKey.CompareTo(key);
            if (cmp <= 0)
            {
                result = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return result;
    }

    /// <summary>
    /// Binary searches the restart offsets to find the byte offset of the last restart
    /// section whose first key is ≤ <paramref name="key"/>. Decoding entries from that
    /// offset forward is guaranteed to find (or rule out) the target key.
    /// </summary>
    private static int BinarySearchRestartForKey(byte[] payload, int[] restartOffsets, TKey key)
    {
        if (restartOffsets.Length == 0)
        {
            return 0;
        }

        var lo = 0;
        var hi = restartOffsets.Length - 1;
        var result = restartOffsets[0];
        while (lo <= hi)
        {
            var mid = lo + (hi - lo) / 2;
            var midKeyBytes = BlockFormat.ReadKeyAtOffset(payload, restartOffsets[mid]);
            var midKey = FromKeyBytes(midKeyBytes);
            if (midKey.CompareTo(key) <= 0)
            {
                result = restartOffsets[mid];
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return result;
    }

    /// <summary>
    /// Streaming block-entry iterator — preserves the raw <see cref="BlockEntry"/>
    /// (including the seqno + value-kind slots) for callers that need seqno-aware iteration.
    /// </summary>
    private IEnumerable<BlockEntry> EnumerateBlockEntries()
    {
        ThrowIfUnavailable();
        if (_footer is null || _footer.EntryCount == 0)
        {
            yield break;
        }

        using var stream = OpenIndependentReadStream();
        stream.Seek(0, SeekOrigin.Begin);
        while (stream.Position < _dataEnd)
        {
            var entries = BlockFormat.ReadBlock(stream, ReadCodec);
            foreach (var e in entries)
            {
                ThrowIfUnavailable();
                yield return e;
                ThrowIfUnavailable();
            }
        }
        ThrowIfUnavailable();
    }

    /// <summary>
    /// Streaming block-entry iterator that positions the read head at the first data block
    /// that may contain <paramref name="startKey"/> (using the sparse block index), skipping
    /// all prior blocks. Within the starting block, entries whose decoded key is less than
    /// <paramref name="startKey"/> are skipped. The seqno filter
    /// <c>≤ <paramref name="maxSequenceNumber"/></c> is applied to every yielded entry.
    /// </summary>
    /// <remarks>
    /// Block-seek complexity: O(log B) index binary-search where B = number of blocks, then
    /// O(entries-in-starting-block) within-block linear scan, then O(entries-in-remaining-blocks)
    /// for the suffix. The full-scan cost of entries-before-startKey across earlier blocks is
    /// eliminated — only whole blocks that provably lie below startKey are skipped. This is the
    /// dominant SSTable optimisation for GetRangeAsync with a high startKey.
    /// </remarks>
    internal IEnumerable<BlockEntry> EnumerateBlockEntriesFrom(TKey startKey, ulong maxSequenceNumber = ulong.MaxValue)
    {
        ThrowIfUnavailable();
        if (_footer is null || _footer.EntryCount == 0)
        {
            yield break;
        }

        // Use the block index to find the last block whose first key ≤ startKey.
        // That block may contain startKey (it is the first candidate block).
        // SelectBlock returns -1 when all block first-keys exceed startKey — this means
        // startKey is less than every key in the SSTable, so ALL blocks must be included.
        // In that case we start from block 0 and emit everything (no within-block skip needed).
        var startBlock = SelectBlock(_footer.BlockIndex, startKey);
        long startOffset;
        var needWithinBlockSkip = true;
        if (startBlock < 0)
        {
            // startKey < all keys in the SSTable. All entries are ≥ startKey; start from offset 0
            // and skip the within-block key filter (every entry qualifies).
            startOffset = 0;
            needWithinBlockSkip = false;
        }
        else
        {
            startOffset = _footer.BlockIndex[startBlock].Offset;
        }

        using var stream = OpenIndependentReadStream();
        stream.Seek(startOffset, SeekOrigin.Begin);

        var firstBlock = needWithinBlockSkip;
        while (stream.Position < _dataEnd)
        {
            var entries = BlockFormat.ReadBlock(stream, ReadCodec);
            foreach (var e in entries)
            {
                ThrowIfUnavailable();
                if (e.SequenceNumber > maxSequenceNumber)
                {
                    continue;
                }

                if (firstBlock)
                {
                    // Within the starting block, skip entries whose key is before startKey.
                    // Once we find an entry ≥ startKey, all subsequent entries in the block
                    // and all later blocks are in order and need no further key-skip.
                    var entryKey = FromKeyBytes(e.Key.ToArray());
                    if (entryKey.CompareTo(startKey) < 0)
                    {
                        continue;
                    }
                    // This entry is ≥ startKey; stop skipping for the rest of the scan.
                    firstBlock = false;
                }

                yield return e;
                ThrowIfUnavailable();
            }

            if (firstBlock)
            {
                // We scanned the whole starting block without finding a key ≥ startKey.
                // This can happen when the starting block's last key < startKey (e.g., the
                // block index returned the block immediately before an empty range). All
                // subsequent blocks have first keys > this block's last key, so they are ≥
                // the next block's first key ≥ startKey — emit them normally.
                firstBlock = false;
            }
        }
        ThrowIfUnavailable();
    }

    private void TryOpenExistingFile()
    {
        if (!_storageFile.Exists())
        {
            return;
        }

        // An existing file that cannot be read is a fault that must propagate — swallowing
        // it would silently degrade the table to "unwritten empty": every read reports
        // not-found and a subsequent Write atomically replaces the real contents.
        //
        // Tail-read, never File.ReadAllBytes: the footer is designed for end-relative parsing
        // (trailing footer-length word) and block reads stream from the file separately, so
        // materializing the whole file here is a transient O(file) allocation — and a hard
        // failure ceiling: File.ReadAllBytes throws for files ≥ 2 GiB, making a large
        // compacted segment unopenable after restart.
        using var stream = new FileStream(
            _storageFile.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var fileLength = stream.Length;

        if (fileLength == 0)
        {
            throw new InvalidDataException($"Existing SSTable '{_storageFile.FileName}' is empty and has no valid footer.");
        }

        var invalidFooter = new Func<InvalidDataException>(() => new InvalidDataException(
            $"File '{_storageFile.FileName}' is not a valid SSTable: footer magic / trailer " +
            "check failed. v2.0.0 recognises a single SSTable format; no fallback reader exists."));

        byte[] tail;
        uint trailerBody;
        if (fileLength < 8)
        {
            // Too short to carry [magic + trailer word]; hand the whole (tiny) file to the
            // parser so the failure surfaces with the standard message.
            tail = new byte[fileLength];
            stream.ReadExactly(tail);
            trailerBody = 0;
        }
        else
        {
            Span<byte> trailerWord = stackalloc byte[4];
            stream.Seek(-4, SeekOrigin.End);
            stream.ReadExactly(trailerWord);
            trailerBody = BinaryPrimitives.ReadUInt32LittleEndian(trailerWord);

            // The trailer word is decoded from untrusted bytes — bound it against the bytes
            // actually present (and int range) BEFORE it sizes the tail allocation.
            if (trailerBody == 0 || trailerBody > fileLength - 4 || trailerBody > int.MaxValue - 4)
            {
                throw invalidFooter();
            }

            var tailLength = (int)trailerBody + 4;
            tail = new byte[tailLength];
            stream.Seek(-tailLength, SeekOrigin.End);
            stream.ReadExactly(tail);
        }

        // The throwing reader, not TryReadFromEnd: the open fails either way, and the specific check
        // that failed (footer CRC, format_version, magic, a truncated body) is the diagnosis an
        // operator needs, so it travels as the message and the inner exception.
        SsTableFooter footer;
        try
        {
            footer = SsTableFooter.Deserialize(tail);
        }
        catch (Exception ex)
        {
            // As broad as TryReadFromEnd's catch was: any parse failure of untrusted footer bytes is
            // an invalid file, never a different exception type escaping the open.
            throw new InvalidDataException(
                $"File '{_storageFile.FileName}' is not a valid SSTable: {ex.Message} " +
                "v2.0.0 recognises a single SSTable format; no fallback reader exists.",
                ex);
        }

        _footer = footer;
        _dataEnd = fileLength - 4 - trailerBody;

        // The footer CRC covers the footer body only and is forgeable (it guards against bit-rot,
        // not against a re-CRC'd forgery). The point-lookup path computes a block's byte length as
        // the difference between two consecutive block offsets and allocates new byte[blockLen]; an
        // out-of-order or out-of-bounds offset therefore drives an unbounded allocation (colossal
        // positive blockLen) or an OverflowException (negative blockLen). Validate the block-offset
        // invariants here, once at open, so a forged footer is rejected before any block read can
        // allocate: offsets must be non-negative, strictly increasing, and within the data section
        // (every block lies in [0, _dataEnd), so each offset is <= _dataEnd).
        ValidateBlockOffsets(footer.BlockIndex, _dataEnd);

        // If the on-disk footer's codec differs from the configured codec, resolve the
        // correct codec via the registry so reads decompress correctly. The configured
        // _codec remains the one used for future writes.
        if (footer.CodecId != _codec.CodecId)
        {
            var resolved = _codecRegistry.Resolve(footer.CodecId) ?? throw new UnsupportedCompressionCodecException(footer.CodecId);
            _readCodec = resolved;
        }

        _bloom = footer.BloomState.IsEmpty ? SegmentBloomFilter<TKey>.FromNative(
            footer.BloomBitsCount,
            footer.BloomHashCount,
            footer.BloomBits.ToArray()) : SegmentBloomFilter<TKey>.Deserialize(footer.BloomState, _bloomOptions);

        // MaxSequenceNumber is carried in the footer so the reopen path never scans blocks
        // to recover seqno state. The footer carries a single CurrentFormatVersion under the
        // greenfield premise — no fallback reader exists for any older shape.
        _maxSeqno = footer.MaxSequenceNumber;
    }

    // StorageFile.Open owns one mutable stream field and closes the previous handle on each
    // call. SSTable readers are immutable and intentionally shared by point reads, scans, and
    // compaction, so every read operation needs its own handle; otherwise a point lookup can
    // close a compaction enumerator between blocks. FileShare.Delete preserves the engine's
    // deferred-reclamation contract while an already-open immutable read drains.
    private FileStream OpenIndependentReadStream()
        => new(
            _storageFile.FilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete);

    private ICompressionCodec ReadCodec => _readCodec ?? _codec;

    // ---- shared helpers ----

    private static byte[] ToBytes<T>(T value) where T : ISerializable<T>
    {
        using var ms = new MemoryStream();
        value!.Serialize(ms);
        return ms.ToArray();
    }

    private static TValue FromBytes(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        return TValue.Deserialize(ms);
    }

    private static TKey FromKeyBytes(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        return TKey.Deserialize(ms);
    }

    private static void ReadExact(Stream stream, Span<byte> destination)
    {
        var total = 0;
        while (total < destination.Length)
        {
            var read = stream.Read(destination[total..]);
            if (read == 0)
            {
                throw new EndOfStreamException(
                    $"Unexpected end of stream while reading SSTable (got {total} of {destination.Length} bytes).");
            }
            total += read;
        }
    }

    // A block's byte length is derived from consecutive offsets in the (forgeable-CRC) footer, so
    // the point-lookup allocation new byte[nextOffset - candidateOffset] is only safe when the
    // offsets are non-negative, strictly increasing, and bounded by the data section. Rejecting a
    // violation here — once, at open — means every downstream block read is allocation-safe without
    // re-checking. dataEnd is the EXCLUSIVE upper bound of the data section ([0, dataEnd)): it is the
    // synthetic past-the-end marker the last block's nextOffset uses, so every real block offset —
    // including the final one — must be strictly < dataEnd. An offset == dataEnd is forged (a point
    // lookup on it would compute blockLen = dataEnd - offset == 0 against an empty/out-of-section span).
    private static void ValidateBlockOffsets(IReadOnlyList<SsTableBlockIndexEntry> blockIndex, long dataEnd)
    {
        var previous = -1L;
        for (var i = 0; i < blockIndex.Count; i++)
        {
            var offset = blockIndex[i].Offset;
            if (offset < 0 || offset >= dataEnd || offset <= previous)
            {
                throw new InvalidDataException(
                    $"SSTable block index is corrupt: block {i} declares offset {offset}, which is not a "
                    + $"strictly-increasing value within [0, {dataEnd}) (previous offset {previous}). "
                    + "The footer CRC passed but the block offsets are forged or bit-rotten; a forged "
                    + "offset would drive an unbounded block allocation on lookup.");
            }

            previous = offset;
        }
    }
}

/// <summary>
/// Issues <see cref="BlockCacheKey.SegmentId"/> values for <see cref="SortedStringTable{TKey, TValue}"/>
/// instances constructed with a block cache. One process-wide sequence, deliberately outside the
/// generic table type (whose statics would be per closed type), so tables of different key and
/// value types sharing one cache cannot take the same identity either.
/// </summary>
internal static class BlockCacheSegmentIdentity
{
    private static long s_last;

    /// <summary>Returns the next identity; always positive, so caching stays enabled for it.</summary>
    /// <returns>An identity no earlier call in this process returned.</returns>
    internal static long Next() => Interlocked.Increment(ref s_last);
}
