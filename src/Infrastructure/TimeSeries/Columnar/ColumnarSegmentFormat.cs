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
using Boutquin.Storage.Domain.Helpers;
using Boutquin.Storage.Domain.Interfaces.TimeSeries;
using Boutquin.Storage.Domain.Interfaces.TimeSeries.Columnar;
using Boutquin.Storage.Domain.ValueObjects.SortedStringTable;
using Boutquin.Storage.Domain.ValueObjects.TimeSeries;
using Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar;
using Boutquin.Storage.Infrastructure.DataStructures;
using Boutquin.Storage.Infrastructure.KeyValueStore;
using Boutquin.Storage.Infrastructure.LsmTree;

namespace Boutquin.Storage.Infrastructure.TimeSeries.Columnar;

/// <summary>
/// LSM-segment-level columnar format. Wraps an
/// <see cref="IColumnarSegmentFormat{TRecord}"/> — typically the source-generator-emitted
/// <see cref="CompressedColumnarSegmentFormat{TRecord}"/> — in a per-dataset envelope so that
/// a single LSM segment can hold records for many datasets while still writing each dataset's
/// records as a contiguous column-oriented block.
/// </summary>
/// <typeparam name="TDatasetKey">The per-entity partition key type.</typeparam>
/// <typeparam name="TRecord">The dated record type.</typeparam>
/// <remarks>
/// <para><b>Why an envelope is needed:</b> the LSM engine flushes
/// <see cref="TimeSeriesKey{TDatasetKey}"/>-keyed entries that span many datasets. The inner
/// <see cref="IColumnarSegmentFormat{TRecord}"/> sees only <typeparamref name="TRecord"/> and
/// the per-record date — it has no notion of dataset partitioning. A segment-level envelope
/// groups records by dataset (records for the same dataset are contiguous in the sorted
/// flush thanks to <see cref="TimeSeriesKey{TDatasetKey}"/>'s lex ordering), calls the inner
/// format once per dataset block, and records the resulting byte offsets in a dataset-index footer
/// so the reader can seek to any dataset's block in O(log n) key lookups + one seek.</para>
/// <para><b>Bounded blocks.</b> A block is encoded in memory and a whole-block read loads it into
/// one byte array, both capped near 2 GiB, while write admission bounds each record (128 MiB) but
/// not their sum. The writer therefore cuts a dataset's records into blocks of consecutive dates
/// holding at most 256 MiB of serialized records each (a single larger record gets its own block),
/// so every acknowledged record can be flushed and compacted. A dataset's blocks are adjacent in
/// the index and cover ascending, non-overlapping date spans.</para>
/// <para><b>Records are stored inline only.</b> A columnar cell carries no value-kind slot, so
/// this format cannot store a WiscKey value-log pointer. The LSM engine therefore keeps value-log
/// dispatch off for it whatever <c>LsmStorageEngineOptions.ValueLog</c> says — a hand-wired
/// <c>ValueLog: null</c>, which would otherwise resolve to the 1 KiB default threshold, included —
/// and keeps every record inline up to 128 MiB of serialized bytes, the largest inline value the
/// engine's value-log options accept. A larger record is refused with
/// <see cref="ValueTooLargeException"/> before the write is acknowledged. The writer still refuses a
/// pointer entry with <see cref="NotSupportedException"/> as a last line of defence.</para>
/// <para><b>File layout:</b>
/// <code>
/// [header: 4 magic bytes 'B','S','L','C' + uint32 format-version]
/// [dataset block 1]  — bytes produced by IColumnarSegmentFormat.WriteAsync for one dataset's
///                        records (all of them, or one bounded run of consecutive dates)
/// [dataset block 2]
/// ...
/// [dataset block N]
/// [footer]
///   uint32 datasetCount (the number of block entries; a dataset may have several)
///   uint64 maxSequenceNumber (segment-level; highest LSM seqno among entries + tombstones at
///          seal time — mirrors the SST footer's max_sequence_number; recovery seeds the
///          engine's per-segment watermark from it)
///   for each block, in ascending (dataset key, date) order:
///     uint32 keyByteLength
///     keyBytes (length = keyByteLength; TDatasetKey.Serialize output)
///     int64  blockByteOffset (absolute, from start of file)
///     int64  blockByteLength
///     int32  rowCount
///     int32  minDateDayNumber
///     int32  maxDateDayNumber
///   uint32 footerCrc32C (Castagnoli; over the footer from datasetCount through the last
///          dataset entry, excluding the CRC itself and the trailer)
/// [trailer (last 8 bytes): uint32 footerLengthBytes + uint32 trailer magic 'C','L','S','B']
/// </code>
/// </para>
/// <para><b>Reader robustness:</b> the trailer's magic <c>CLSB</c> is validated before any
/// other byte is trusted — a non-Boutquin file or a truncated stream surfaces as
/// <see cref="InvalidSegmentException"/> / <see cref="TruncatedSegmentException"/> without
/// the reader ever seeking to arbitrary offsets derived from garbage bytes. Footer CRC
/// validation happens next; a mismatch is <see cref="CorruptSegmentException"/>.</para>
/// <para><b>Determinism:</b> byte-identical output for identical input — no embedded
/// timestamps, no non-deterministic iteration order. Dataset blocks are emitted in ascending
/// dataset-key order (the natural order of the flushed LSM entries).</para>
/// <para><b>Scan direction:</b> the envelope's reader threads <see cref="ScanDirection"/>
/// through to the inner <see cref="IColumnarSegmentFormat{TRecord}"/> end-to-end and
/// rejects any non-Ascending value with <see cref="ArgumentOutOfRangeException"/> at the
/// envelope boundary — so envelope, inner format, and engine all enforce the same single
/// supported direction. v2.0.0 ships only <see cref="ScanDirection.Ascending"/>; descending
/// iteration is a future enhancement and will land additively across all three surfaces.</para>
/// </remarks>
public sealed class ColumnarSegmentFormat<TDatasetKey, TRecord>
    : ISegmentFormat<TimeSeriesKey<TDatasetKey>, TRecord>,
      ISegmentEntryAdmission<TimeSeriesKey<TDatasetKey>, TRecord>,
      IColumnarSegmentEnvelope
    where TDatasetKey : ISerializable<TDatasetKey>, IComparable<TDatasetKey>, new()
    where TRecord : ISerializable<TRecord>, IHasDate, IColumnarRehydratable<TRecord>, new()
{
    /// <summary>Header magic 'B','S','L','C' (LSM-Columnar) read little-endian as <c>uint</c>.</summary>
    internal const uint HeaderMagic = 0x434C_5342u; // 'C','L','S','B' LE == 'B','S','L','C'

    /// <summary>Trailer magic 'C','L','S','B' (reversed) read little-endian as <c>uint</c>.</summary>
    internal const uint TrailerMagic = 0x4253_4C43u; // 'B','S','L','C' LE == 'C','L','S','B'

    /// <summary>The current outer-envelope format version this build writes.</summary>
    internal const uint CurrentFormatVersion = 1u;

    internal const int HeaderSize = 8;  // 4 magic + 4 version
    internal const int TrailerSize = 8; // 4 footer-length + 4 trailer-magic

    private readonly IColumnarSegmentFormat<TRecord> _inner;
    private readonly SegmentBloomFilterOptions<TimeSeriesKey<TDatasetKey>> _bloomOptions = new(enabled: false);
    // The sort spec resolved against TRecord once, at construction; null when no spec was given.
    private readonly IComparer<TRecord>? _sortComparer;

    /// <summary>
    /// Default upper bound on the serialized record bytes one dataset block holds: twice the 128 MiB
    /// largest inline record, well inside the ~2 GiB cap of the in-memory block buffer and of the
    /// byte array a whole-block read allocates, with headroom for column-encoding overhead.
    /// </summary>
    internal const long DefaultMaxBlockSerializedBytes = 256L * 1024 * 1024;

    // A dataset's records are cut into consecutive-date blocks of at most this many serialized bytes
    // (a single larger record still gets a block of its own). Internal so tests can exercise the cut
    // without gigabytes of data.
    private readonly long _maxBlockSerializedBytes = DefaultMaxBlockSerializedBytes;

    internal long MaxBlockSerializedBytes
    {
        get => _maxBlockSerializedBytes;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _maxBlockSerializedBytes = value;
        }
    }

    /// <summary>
    /// Creates a columnar segment format backed by <paramref name="innerColumnarFormat"/>.
    /// The inner format writes each per-dataset block; this wrapper adds the dataset-index
    /// envelope that lets an LSM segment carry many datasets.
    /// </summary>
    /// <param name="innerColumnarFormat">
    /// Per-dataset columnar writer/reader. Typically
    /// <see cref="CompressedColumnarSegmentFormat{TRecord}"/> for production use.
    /// </param>
    /// <param name="sortSpec">
    /// Optional column sort order applied to rows within each per-dataset block before the
    /// inner format encodes them. When <see langword="null"/>, rows are emitted in their
    /// incoming order (the LSM flush order: ascending <c>TimeSeriesKey</c> = ascending date
    /// within a dataset). This parameter is the only place a columnar sort order is configured;
    /// the time-series engine options carry no sort spec.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="innerColumnarFormat"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="sortSpec"/> names a column that is not a sortable public property of
    /// <typeparamref name="TRecord"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// <typeparamref name="TRecord"/> carries no generated sort comparer and
    /// <paramref name="sortSpec"/> names a property of a <see cref="Nullable{T}"/> type, or of a
    /// type that does not implement <see cref="IComparable"/>.
    /// </exception>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0027", Justification = "The additive Bloom overload has a required options discriminator; the existing constructor signature is preserved.")]
    public ColumnarSegmentFormat(
        IColumnarSegmentFormat<TRecord> innerColumnarFormat,
        ColumnSortSpec? sortSpec = null)
    {
        ArgumentNullException.ThrowIfNull(innerColumnarFormat);
        _inner = innerColumnarFormat;
        // Every flush and compaction sorts each block by this spec, so a spec that does not resolve
        // against TRecord would fault every flush after its writes were acknowledged. Resolving it
        // here refuses it before the engine exists.
        _sortComparer = sortSpec is null ? null : ResolveSortComparer(sortSpec);
    }

    /// <summary>Uses explicit Bloom options without an additional column sort order.</summary>
    /// <param name="bloomOptions">Borrowed immutable full-key filter settings.</param>
    /// <param name="innerColumnarFormat">Borrowed per-dataset format.</param>
    public ColumnarSegmentFormat(SegmentBloomFilterOptions<TimeSeriesKey<TDatasetKey>> bloomOptions,
        IColumnarSegmentFormat<TRecord> innerColumnarFormat) : this(bloomOptions, innerColumnarFormat, null) { }

    /// <summary>Adds persisted point-membership filters to each columnar segment without changing range pruning.</summary>
    /// <param name="bloomOptions">Borrowed immutable options and custom codec. Segment readers own created/restored filters.</param>
    /// <param name="innerColumnarFormat">Borrowed per-dataset columnar format.</param>
    /// <param name="sortSpec">Optional validated column sort order.</param>
    /// <exception cref="ArgumentNullException">Options or inner format are null.</exception>
    public ColumnarSegmentFormat(
        SegmentBloomFilterOptions<TimeSeriesKey<TDatasetKey>> bloomOptions,
        IColumnarSegmentFormat<TRecord> innerColumnarFormat,
        ColumnSortSpec? sortSpec) : this(innerColumnarFormat, sortSpec)
    {
        ArgumentNullException.ThrowIfNull(bloomOptions);
        _bloomOptions = bloomOptions;
    }

    /// <inheritdoc />
    public string FileExtension => ".bsct";

    bool IColumnarSegmentEnvelope.IsDescribedBy(object options)
        => options is ColumnarTimeSeriesStoreOptions<TRecord> columnar
            && ReferenceEquals(columnar.ColumnarFormat, _inner);

    /// <inheritdoc />
    /// <remarks>
    /// A columnar segment carries no per-cell value-kind slot, so it cannot store a value-log
    /// pointer: the engine keeps every record inline over this format (up to 128 MiB, the bound the
    /// columnar stack builder also pins) whatever value-log options it was given.
    /// </remarks>
    public bool SupportsValueLogPointers => false;

    void ISegmentEntryAdmission<TimeSeriesKey<TDatasetKey>, TRecord>.ValidateEntry(
        TimeSeriesKey<TDatasetKey> key, TRecord value)
    {
        _ = DatasetKeyEncoding.SerializeValidated(key.Dataset);
        EnsureKeyDateMatchesRecord(key, value, nameof(key));
        // The inner format owns the per-record write limits (e.g. the bitmap-index key cap); asking
        // it here refuses an unwritable record before the engine acknowledges it.
        (_inner as IColumnarRecordAdmission<TRecord>)?.ValidateRecord(value);
    }

    // The envelope persists only the record; every reader rebuilds the key as
    // (dataset, record.Date). An entry whose key date differs from its record's date would
    // therefore move to the record's date across a flush, so it is refused.
    private static void EnsureKeyDateMatchesRecord(TimeSeriesKey<TDatasetKey> key, TRecord record, string paramName)
    {
        if (key.Date != record.Date)
        {
            throw new ArgumentException(
                $"ColumnarSegmentFormat stores a record under (dataset, record.Date); the key date {key.Date:O} "
                + $"differs from the record's date {record.Date:O}, so the entry would become unreachable at its key.",
                paramName);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// This is the narrow <i>seqno-filter-support</i> flag: it answers "does a segment this
    /// format writes carry a per-entry sequence-number slot a read can filter on?", and for the
    /// columnar format the honest answer is <see langword="false"/> — row-group ordering is by
    /// <see cref="IHasDate"/>, and there is no per-row seqno slot.
    /// <para>
    /// It is NOT the engine-level snapshot-isolation capability. The LSM engine enforces
    /// isolation by segment pinning + an at-acquisition memtable copy (see
    /// <c>LsmStorageEngine.SupportsSnapshots</c> and <c>AcquireSnapshotAsync</c>), a
    /// format-agnostic mechanism that does not require this flag to be <see langword="true"/>.
    /// Columnar time-series stores therefore support snapshots at the engine level even though
    /// this format-level flag is <see langword="false"/>.
    /// </para>
    /// </remarks>
    public bool SupportsSnapshots => false;

    /// <inheritdoc />
    public async Task<ISegmentReader<TimeSeriesKey<TDatasetKey>, TRecord>> WriteAsync(
        string segmentFolder,
        string segmentFileName,
        IReadOnlyList<SegmentWriteEntry<TimeSeriesKey<TDatasetKey>, TRecord>> sortedEntries,
        IReadOnlyDictionary<TimeSeriesKey<TDatasetKey>, ulong>? tombstones = null,
        CancellationToken cancellationToken = default)
    {
        // Tombstones are persisted in the outer envelope footer (see the footer-write block
        // below). A no-WAL reopen recovers them via ParseFooter → GetTombstoneSeqnos, so a delete
        // flushed to a columnar segment does not resurrect from an older data segment on restart
        // (DDIA Ch. 4 p. 121, 124).
        ArgumentNullException.ThrowIfNull(segmentFolder);
        ArgumentNullException.ThrowIfNull(segmentFileName);
        ArgumentNullException.ThrowIfNull(sortedEntries);

        // Columnar layouts carry no per-cell ValueKind slot. The engine never routes a value to
        // the value log for this format (SupportsValueLogPointers is false), so a pointer here
        // means a caller bypassed that dispatch; it is rejected loudly rather than silently dereferenced.
        //
        // Keys must be strictly ascending (fail-fast, mirroring the SSTable writer). The
        // dataset-grouping loop below cuts a block wherever the dataset changes, and the reader
        // accepts several blocks of one dataset only when their date spans ascend without
        // overlapping, so an out-of-order dataset would be written as a block no read trusts; a
        // duplicate key would store two rows of which a read returns whichever sorts first rather
        // than the newer one.
        for (var idx = 0; idx < sortedEntries.Count; idx++)
        {
            _ = DatasetKeyEncoding.SerializeValidated(sortedEntries[idx].Key.Dataset);
            if (sortedEntries[idx].Kind != ValueKind.Inline)
            {
                throw new NotSupportedException(
                    $"ColumnarSegmentFormat does not support ValueKind.{sortedEntries[idx].Kind}");
            }

            EnsureKeyDateMatchesRecord(sortedEntries[idx].Key, sortedEntries[idx].InlineValue!, nameof(sortedEntries));

            if (idx > 0 && sortedEntries[idx].Key.CompareTo(sortedEntries[idx - 1].Key) <= 0)
            {
                throw new ArgumentException(
                    $"Entries must be sorted in strictly ascending key order. Key at index {idx} is not greater than key at index {idx - 1}.",
                    nameof(sortedEntries));
            }
        }

        // A columnar segment carries no per-row seqno, so readers resolve a same-segment
        // data/tombstone pair with the segment's max seqno as the data's effective version. That
        // is sound only if every data entry sharing a key with a persisted tombstone is NEWER than
        // it. Compaction can hand us the opposite pairing (a snapshot-preserved tombstone beside
        // the older data it shadows); that data is dead for every reader of this new segment
        // (older snapshots read their pinned pre-compaction files), so drop it here rather than
        // let the segment max seqno resurrect it.
        if (tombstones is { Count: > 0 })
        {
            // Match tombstones to entries by key identity (serialized bytes), not by the caller
            // map's comparer: compaction hands us entry keys and tombstone keys deserialized from
            // different segments, and the dataset-key contract promises no value equality.
            var shadowing = new Dictionary<TimeSeriesKey<TDatasetKey>, ulong>(
                tombstones.Count, SerializedKeyEqualityComparer<TimeSeriesKey<TDatasetKey>>.Instance);
            foreach (var (tombstoneKey, seqno) in tombstones)
            {
                shadowing[tombstoneKey] = shadowing.TryGetValue(tombstoneKey, out var prior) && prior > seqno ? prior : seqno;
            }

            List<SegmentWriteEntry<TimeSeriesKey<TDatasetKey>, TRecord>>? kept = null;
            for (var idx = 0; idx < sortedEntries.Count; idx++)
            {
                var entry = sortedEntries[idx];
                var shadowed = shadowing.TryGetValue(entry.Key, out var tombstoneSeqno)
                    && tombstoneSeqno > entry.SequenceNumber;
                if (shadowed && kept is null)
                {
                    kept = new List<SegmentWriteEntry<TimeSeriesKey<TDatasetKey>, TRecord>>(sortedEntries.Count);
                    for (var prior = 0; prior < idx; prior++)
                    {
                        kept.Add(sortedEntries[prior]);
                    }
                }

                if (!shadowed)
                {
                    kept?.Add(entry);
                }
            }

            if (kept is not null)
            {
                sortedEntries = kept;
            }
        }

        var path = Path.Combine(segmentFolder, segmentFileName);

        // Durable, crash-atomic write: stream the outer envelope to a sibling temp file, fsync it
        // (Flush(flushToDisk: true) below), then atomically rename over the final path with a
        // directory fsync (DurableFile.AtomicReplace). Writing in place with FileMode.Create + a
        // non-durable FlushAsync let the manifest reference a segment whose bytes might never reach
        // disk, and truncated any pre-existing segment before the body was written (DDIA Ch. 4 — a
        // segment file must be durable before it is referenced). Mirrors SortedStringTable.Write.
        var tempPath = path + ".tmp";

        // Stream the outer envelope directly to the FileStream; only the footer buffers in
        // memory for CRC. This eliminates the ~segment-sized MemoryStream that the prior
        // triple-buffer approach required.
        var fs = new FileStream(
            tempPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None);
        await using (fs.ConfigureAwait(false))
        {
            // Header — streamed directly.
            WriteUInt32Le(fs, HeaderMagic);
            WriteUInt32Le(fs, CurrentFormatVersion);
            var filePosition = 8L; // header: 4B magic + 4B version

            var blocks = new List<BlockMetadata>();
            var total = sortedEntries.Count;

            // Segment-level max sequence number — the highest LSM seqno among the flushed entries
            // and the persisted tombstones (R2-G). Mirrors SsTableFooter.MaxSequenceNumber: a
            // recovered columnar segment seeds the engine's per-segment max-seqno watermark from
            // this field instead of the reader's data-derived 0, so the C-3 re-insert-vs-tombstone
            // stamp stays correct after a no-WAL reopen. Columnar entries carry no per-row seqno on
            // disk; this single segment-wide watermark is the recovery-relevant fact.
            ulong maxSequenceNumber = 0UL;
            var i = 0;
            while (i < total)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // A block holds exactly one dataset under the key's own identity (comparer-equal and
                // byte-identical): every row is read back under the block's dataset bytes, so a
                // comparer-equal dataset with other bytes starts its own block.
                var datasetKey = sortedEntries[i].Key.Dataset;
                var j = i;
                while (j < total && TimeSeriesKey<TDatasetKey>.CompareDatasets(sortedEntries[j].Key.Dataset, datasetKey) == 0)
                {
                    j++;
                }

                // Records for this dataset are sortedEntries[i..j). They are written as one or more
                // blocks of consecutive dates, each holding at most MaxBlockSerializedBytes of
                // serialized records (or one record, when a single record exceeds it). A block is
                // encoded in memory and read back into one byte array, both capped near 2 GiB, while
                // admission bounds only each record (128 MiB inline) and the memtable bounds only the
                // record count; without the cut, a dataset whose acknowledged records summed past the
                // buffer cap would fault every flush and compaction of its segment.
                var blockStart = i;
                var nextRecordBytes = SerializedByteCount(sortedEntries[i].InlineValue!);
                while (blockStart < j)
                {
                    var blockEnd = blockStart;
                    var blockBytes = 0L;
                    while (blockEnd < j && (blockEnd == blockStart || blockBytes + nextRecordBytes <= MaxBlockSerializedBytes))
                    {
                        blockBytes += nextRecordBytes;
                        blockEnd++;
                        nextRecordBytes = blockEnd < j ? SerializedByteCount(sortedEntries[blockEnd].InlineValue!) : 0L;
                    }

                    var blockMetadata = await WriteBlockAsync(
                        fs, filePosition, datasetKey, sortedEntries, blockStart, blockEnd, cancellationToken).ConfigureAwait(false);
                    filePosition += blockMetadata.BlockLength;
                    blocks.Add(blockMetadata);
                    for (var k = blockStart; k < blockEnd; k++)
                    {
                        if (sortedEntries[k].SequenceNumber > maxSequenceNumber)
                        {
                            maxSequenceNumber = sortedEntries[k].SequenceNumber;
                        }
                    }

                    blockStart = blockEnd;
                }

                i = j;
            }

            // Fold the tombstone deletion seqnos into the segment-level max. A tombstone-only
            // columnar flush carries no data entries, so without this its watermark would be 0
            // and a post-recovery re-insert could out-rank it — the exact R2-G edge.
            if (tombstones is { Count: > 0 })
            {
                foreach (var tombstoneSeqno in tombstones.Values)
                {
                    if (tombstoneSeqno > maxSequenceNumber)
                    {
                        maxSequenceNumber = tombstoneSeqno;
                    }
                }
            }

            using var bloom = SegmentBloomFilter<TimeSeriesKey<TDatasetKey>>.Create(_bloomOptions, sortedEntries.Count);
            foreach (var entry in sortedEntries)
            {
                bloom.Add(entry.Key);
            }

            // Footer — only this buffers in memory for CRC computation.
            var footerOffset = filePosition;
            using (var footerContent = new MemoryStream())
            {
                WriteUInt32Le(footerContent, (uint)blocks.Count);
                // Segment-level max sequence number (R2-G) follows datasetCount, mirroring the
                // SST footer's max_sequence_number placement. Read unconditionally on recovery —
                // there is no pre-footer columnar segment on disk (greenfield premise), so no
                // dual-read / legacy-reader branch exists.
                WriteUInt64Le(footerContent, maxSequenceNumber);
                foreach (var b in blocks)
                {
                    WriteUInt32Le(footerContent, (uint)b.KeyBytes.Length);
                    footerContent.Write(b.KeyBytes, 0, b.KeyBytes.Length);
                    WriteInt64Le(footerContent, b.BlockOffset);
                    WriteInt64Le(footerContent, b.BlockLength);
                    WriteInt32Le(footerContent, b.RowCount);
                    WriteInt32Le(footerContent, b.MinDateDayNumber);
                    WriteInt32Le(footerContent, b.MaxDateDayNumber);
                }

                // Tombstone section (Option A) — appended after the block index so the section is
                // covered by the footer CRC + footer-length, meaning a torn write fails the CRC
                // check on reopen rather than recovering a partial tombstone set. The count is
                // always written (0 when none), so ParseFooter reads it unconditionally; there is
                // no pre-tombstone format on disk under the greenfield premise. Keys are
                // TimeSeriesKey<TDatasetKey> (dataset + day number) — the same key space the engine
                // tracks tombstones in.
                WriteUInt32Le(footerContent, (uint)(tombstones?.Count ?? 0));
                if (tombstones is { Count: > 0 })
                {
                    foreach (var (tombstoneKey, tombstoneSeqno) in tombstones)
                    {
                        using var tombstoneKeyStream = new MemoryStream();
                        tombstoneKey.Serialize(tombstoneKeyStream);
                        var tombstoneKeyBytes = tombstoneKeyStream.ToArray();
                        WriteUInt32Le(footerContent, (uint)tombstoneKeyBytes.Length);
                        footerContent.Write(tombstoneKeyBytes, 0, tombstoneKeyBytes.Length);
                        WriteUInt64Le(footerContent, tombstoneSeqno);
                    }
                }

                if (_bloomOptions.Enabled)
                {
                    var bloomState = bloom.Serialize();
                    if (bloom.IsCustom)
                    {
                        SegmentBloomFilter<TimeSeriesKey<TDatasetKey>>.ValidateState(
                            bloomState, _bloomOptions, sortedEntries.Select(entry => entry.Key));
                    }
                    footerContent.Write(bloomState);
                }
                var footerBytes = footerContent.ToArray();
                await fs.WriteAsync(footerBytes, cancellationToken).ConfigureAwait(false);
                var crc = Crc32C.HashToUInt32(footerBytes);
                WriteUInt32Le(fs, crc);
                filePosition += footerBytes.Length + 4; // +4 for CRC u32
            }

            var footerLength = filePosition - footerOffset; // includes the CRC suffix
            WriteUInt32Le(fs, (uint)footerLength);
            WriteUInt32Le(fs, TrailerMagic);

            // fsync the temp file's bytes to disk before the atomic rename below.
            fs.Flush(flushToDisk: true);
        }

        // Atomically publish the fully-written, fsync'd temp file over the final path and fsync
        // the containing directory so the rename itself is durable.
        DurableFile.AtomicReplace(tempPath, path);

        return new ColumnarSegmentReader(_inner, path, _bloomOptions);
    }

    // Writes sortedEntries[start..end) — consecutive dates of one dataset — as one block at
    // blockOffset and returns its footer entry.
    private async Task<BlockMetadata> WriteBlockAsync(
        Stream output,
        long blockOffset,
        TDatasetKey datasetKey,
        IReadOnlyList<SegmentWriteEntry<TimeSeriesKey<TDatasetKey>, TRecord>> sortedEntries,
        int start,
        int end,
        CancellationToken cancellationToken)
    {
        var records = new TRecord[end - start];
        var minDate = sortedEntries[start].Key.Date;
        var maxDate = sortedEntries[start].Key.Date;
        for (var k = start; k < end; k++)
        {
            records[k - start] = sortedEntries[k].InlineValue!;
            var d = sortedEntries[k].Key.Date;
            if (d < minDate)
            {
                minDate = d;
            }

            if (d > maxDate)
            {
                maxDate = d;
            }
        }

        // Apply sort spec when the caller's requested order differs from the flush order.
        if (_sortComparer is { } comparer)
        {
            Array.Sort(records, comparer);
        }

        // Each inner block records its column byte-offsets relative to the start of the
        // stream it was handed. We therefore write each block into its own buffer so its
        // internal offsets start at 0, then copy the bytes directly to the FileStream.
        // This matches the inner format's seek-from-beginning read contract while
        // eliminating the outer MemoryStream.
        using var blockBuffer = new MemoryStream();
        await _inner.WriteAsync(blockBuffer, records, cancellationToken).ConfigureAwait(false);

        blockBuffer.Position = 0;
        await blockBuffer.CopyToAsync(output, cancellationToken).ConfigureAwait(false);

        // Serialise the dataset key once so we can measure its byte length for the footer.
        using var keyStream = new MemoryStream();
        datasetKey.Serialize(keyStream);

        return new BlockMetadata(
            KeyBytes: keyStream.ToArray(),
            BlockOffset: blockOffset,
            BlockLength: blockBuffer.Length,
            RowCount: end - start,
            MinDateDayNumber: minDate.DayNumber,
            MaxDateDayNumber: maxDate.DayNumber);
    }

    private static long SerializedByteCount(TRecord record)
    {
        using var counter = new ByteCountingStream();
        record.Serialize(counter);
        return counter.Length;
    }

    /// <summary>A write-only stream that counts the bytes written to it and keeps none.</summary>
    private sealed class ByteCountingStream : Stream
    {
        private long _length;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => _length;

        public override long Position
        {
            get => _length;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            ValidateBufferArguments(buffer, offset, count);
            _length += count;
        }

        public override void Write(ReadOnlySpan<byte> buffer) => _length += buffer.Length;

        public override void WriteByte(byte value) => _length++;
    }

    /// <inheritdoc />
    public ISegmentReader<TimeSeriesKey<TDatasetKey>, TRecord> Open(string segmentFolder, string segmentFileName)
    {
        ArgumentNullException.ThrowIfNull(segmentFolder);
        ArgumentNullException.ThrowIfNull(segmentFileName);
        return new ColumnarSegmentReader(_inner, Path.Combine(segmentFolder, segmentFileName), _bloomOptions);
    }

    private readonly record struct BlockMetadata(
        byte[] KeyBytes,
        long BlockOffset,
        long BlockLength,
        int RowCount,
        int MinDateDayNumber,
        int MaxDateDayNumber);

    private static IComparer<TRecord> ResolveSortComparer(ColumnSortSpec spec)
    {
        // Prefer the source-generator-emitted IComparer<TRecord> when the record
        // type carries one. The generated comparer dispatches via a strongly-typed
        // Comparer<T>.Default chain captured at construction time, so per-element cost is
        // one delegate invocation instead of one PropertyInfo.GetValue + boxing +
        // virtual-call CompareTo per sort column.
        //
        // Fall back to the reflection-based comparator (a live fallback, not dead legacy code) when
        // no generated method is available — this preserves correctness for hand-written records that
        // implement IColumnarRehydratable<T> directly without going through the
        // [ColumnarStorable] generator pipeline (test stubs, integration fixtures).
        //
        // Both comparers are stateless, so the one resolved at construction serves every flush.
        return ColumnSortComparerFactory<TRecord>.TryCreate(spec)
            ?? Comparer<TRecord>.Create(BuildReflectionComparison(spec));
    }

    // Legacy compiled-lambda + reflection comparator — preserved as a fallback for hand-
    // written records that don't emit a CreateColumnSortComparer factory. For a row group of
    // N records this loop makes O(N log N · K) accessor invocations (K = sort columns); each
    // invocation goes through a compiled delegate around PropertyInfo.GetValue plus a virtual
    // CompareTo on the boxed result. Roughly an order of magnitude slower than the
    // generator-emitted strongly-typed Comparer<T>.Default chain, but correct for any
    // IComparable property the user names in their SortSpec.
    private static Comparison<TRecord> BuildReflectionComparison(ColumnSortSpec spec)
    {
        // Each sort-spec entry is a (Name, Direction) tuple. Unknown names and undefined
        // direction values are rejected up front so a sort error is attributable to the spec
        // rather than appearing mid-comparison.
        var comparisons = new Comparison<TRecord>[spec.Columns.Count];
        for (var i = 0; i < spec.Columns.Count; i++)
        {
            var (name, direction) = spec.Columns[i];
            if (direction is not SortDirection.Ascending and not SortDirection.Descending)
            {
                throw new ArgumentOutOfRangeException(nameof(spec),
                    $"ColumnSortSpec entry at index {i} declares undefined SortDirection value {direction}.");
            }

            var prop = typeof(TRecord).GetProperty(
                name,
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                ?? throw new ArgumentException(
                    $"ColumnSortSpec column '{name}' does not match any public "
                    + $"property on {typeof(TRecord).Name}. Check the SortSpec on the engine options.",
                    nameof(spec));

            var accessor = BuildSortAccessor(prop);

            // String columns compare ordinally: the boxed IComparable.CompareTo on string is
            // culture-sensitive, which would make on-disk row order depend on the writing
            // machine's locale. The columnar stack is ordinal everywhere else (ZoneMapComputer
            // min/max uses string.CompareOrdinal, the generated comparer emits CompareOrdinal);
            // the reflection fallback must agree with its generated sibling.
            Comparison<TRecord> ascending = prop.PropertyType == typeof(string)
                ? (a, b) => string.CompareOrdinal((string)accessor(a), (string)accessor(b))
                : (a, b) => accessor(a).CompareTo(accessor(b));

            // Descending inverts by swapping arguments — never by negating, which would
            // overflow on int.MinValue comparison results.
            comparisons[i] = direction == SortDirection.Descending
                ? (a, b) => ascending(b, a)
                : ascending;
        }

        return (a, b) =>
        {
            for (var i = 0; i < comparisons.Length; i++)
            {
                var cmp = comparisons[i](a, b);
                if (cmp != 0)
                {
                    return cmp;
                }
            }
            return 0;
        };
    }

    /// <summary>
    /// Compiles a delegate that reads <paramref name="prop"/> from a <typeparamref name="TRecord"/>
    /// and boxes the value to <see cref="IComparable"/> for the sort comparator. Compiled once
    /// per sort column when the format resolves its sort spec; the resulting delegate is
    /// roughly an order of magnitude cheaper per call than
    /// <see cref="System.Reflection.PropertyInfo.GetValue(object?)"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="prop"/>'s declared type does not implement <see cref="IComparable"/>,
    /// or is a <see cref="Nullable{T}"/>. Sort spec columns must be ordered, non-nullable
    /// types — <c>Nullable&lt;T&gt;</c> is rejected because a null value boxes to a
    /// <see langword="null"/> reference and the comparator's <c>va.CompareTo(vb)</c> would
    /// NRE before the writer's null-rejection guard
    /// (<see cref="CompressedColumnarSegmentFormat{TRecord}"/>) ever ran. Mirroring the
    /// writer's no-null contract here keeps the failure surface attributable to the sort
    /// spec rather than a downstream comparator crash.
    /// </exception>
    private static Func<TRecord, IComparable> BuildSortAccessor(System.Reflection.PropertyInfo prop)
    {
        var propType = prop.PropertyType;

        // Reject Nullable<T> explicitly — see <exception> remarks above.
        if (propType.IsGenericType && propType.GetGenericTypeDefinition() == typeof(Nullable<>))
        {
            throw new InvalidOperationException(
                $"ColumnSortSpec column '{prop.Name}' on {typeof(TRecord).Name} is "
                + $"{propType.FullName} (Nullable<T>). Sort spec columns must be non-nullable "
                + "ordered types so the comparator never sees a null value — the columnar "
                + "writer rejects null property values at write time, but sort runs first.");
        }

        if (!typeof(IComparable).IsAssignableFrom(propType))
        {
            throw new InvalidOperationException(
                $"ColumnSortSpec column '{prop.Name}' on {typeof(TRecord).Name} is of type "
                + $"{propType.FullName}, which does not implement IComparable. Sort "
                + "spec columns must be ordered types.");
        }

        // record => (IComparable)record.<Property>
        var record = System.Linq.Expressions.Expression.Parameter(typeof(TRecord), "record");
        var access = System.Linq.Expressions.Expression.Property(record, prop);
        var box = System.Linq.Expressions.Expression.Convert(access, typeof(IComparable));
        return System.Linq.Expressions.Expression
            .Lambda<Func<TRecord, IComparable>>(box, record)
            .Compile();
    }

    private static void WriteUInt32Le(Stream s, uint value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        s.Write(b);
    }

    private static void WriteInt32Le(Stream s, int value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b, value);
        s.Write(b);
    }

    private static void WriteInt64Le(Stream s, long value)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(b, value);
        s.Write(b);
    }

    private static void WriteUInt64Le(Stream s, ulong value)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(b, value);
        s.Write(b);
    }

    private sealed class ColumnarSegmentReader(IColumnarSegmentFormat<TRecord> inner, string path,
        SegmentBloomFilterOptions<TimeSeriesKey<TDatasetKey>> bloomOptions)
                : ISegmentReader<TimeSeriesKey<TDatasetKey>, TRecord>,
          IProjectingSegmentReader<TimeSeriesKey<TDatasetKey>, TRecord, ColumnProjection>, IDisposable
    {
        private readonly IColumnarSegmentFormat<TRecord> _inner = inner;
        private readonly string _path = path;
        private readonly Lock _indexLock = new();
        private SegmentBloomFilter<TimeSeriesKey<TDatasetKey>>? _bloom;
        private bool _disposed;
        private IReadOnlyList<BlockIndex>? _index;
        private IReadOnlyDictionary<TimeSeriesKey<TDatasetKey>, ulong>? _tombstones;
        // Cached segment-level max seqno parsed from the footer (R2-G). Populated by LoadIndex
        // alongside _index/_tombstones; null until the footer is first parsed.
        private ulong? _maxSequenceNumber;

        public async Task<(BlockEntry Entry, bool Found)> TryGetBlockEntryAsync(
            TimeSeriesKey<TDatasetKey> key,
            ulong maxSequenceNumber = ulong.MaxValue,
            CancellationToken cancellationToken = default)
        {
            var (value, found) = await TryGetValueInternalAsync(key, cancellationToken).ConfigureAwait(false);
            if (!found)
            {
                return (default, false);
            }
            // Columnar segments have no per-row seqno or kind slot — synthesize an Inline
            // BlockEntry stamped with the segment's footer max seqno, the same effective version
            // the engine's scan and compaction merges stamp. A sentinel 0 would always lose to a
            // tombstone preserved in this same segment, hiding a newer re-insert from point reads
            // while the scan still returns it. The writer drops any data older than a same-key
            // tombstone, so the stamp never out-ranks a delete it should lose to. The
            // maxSequenceNumber filter is not applied: a reader of this segment (live engine or a
            // snapshot that pinned it) always reads at or above its max seqno.
            using var keyMs = new MemoryStream();
            key.Serialize(keyMs);
            using var valMs = new MemoryStream();
            value!.Serialize(valMs);
            return (new BlockEntry(keyMs.ToArray(), MaxSequenceNumber, ValueKind.Inline, valMs.ToArray()), true);
        }

        private async Task<(TRecord Value, bool Found)> TryGetValueInternalAsync(
            TimeSeriesKey<TDatasetKey> key,
            CancellationToken cancellationToken)
        {
            if (!File.Exists(_path))
            {
                return (default!, false);
            }

            var index = LoadIndex();
            if (_bloom is not null && !_bloom.Contains(key))
            {
                return (default!, false);
            }
            var block = FindBlock(index, key.Dataset, key.Date.DayNumber);
            if (block is null)
            {
                return (default!, false);
            }

            var blockBytes = await ReadBlockBytesAsync(block, cancellationToken).ConfigureAwait(false);
            using var blockMemoryStream = new MemoryStream(blockBytes, writable: false);
            using var blockStream = new WindowReadStream(
                blockMemoryStream,
                0,
                blockBytes.Length,
                CreatePhysicalSegmentIdentity(_path, block.BlockOffset, block.BlockLength));
            await foreach (var rec in _inner.ReadAsync(
                blockStream, key.Date, key.Date, ColumnProjection.All, cancellationToken: cancellationToken)
                .ConfigureAwait(false))
            {
                if (rec.Date == key.Date)
                {
                    return (rec, true);
                }
            }

            return (default!, false);
        }

        private async Task<IEnumerable<(TimeSeriesKey<TDatasetKey> Key, TRecord Value)>> GetAllItemsInternalAsync(
            ScanDirection direction,
            CancellationToken cancellationToken)
        {
            var results = new List<(TimeSeriesKey<TDatasetKey> Key, TRecord Value)>();
            if (!File.Exists(_path))
            {
                return results;
            }

            var index = LoadIndex();
            foreach (var block in index)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var blockBytes = await ReadBlockBytesAsync(block, cancellationToken).ConfigureAwait(false);
                using var blockMemoryStream = new MemoryStream(blockBytes, writable: false);
                using var blockStream = new WindowReadStream(
                    blockMemoryStream,
                    0,
                    blockBytes.Length,
                    CreatePhysicalSegmentIdentity(_path, block.BlockOffset, block.BlockLength));
                // Thread the envelope's direction through to the inner format so the row-order
                // contract is end-to-end consistent. The inner format rejects non-Ascending
                // (v2.0.0 ships ascending only); when descending lands, both surfaces extend
                // together without a silent default-passes-Ascending hazard.
                //
                // The inner format yields records in ON-DISK order, which — when the caller
                // supplied a non-date ColumnSortSpec — is NOT date order. But this reader is a
                // source to the generic LSM k-way merge (KWayMerge requires each source ascending
                // by key) and to the range-scan early-break in LsmStorageEngine.GetRangeAsync
                // ("once we pass endKey, stop"). Both rely on ascending TimeSeriesKey order, which
                // for a fixed dataset means ascending Date. Yielding on-disk order under a non-date
                // sort makes the first record's key overshoot endKey and aborts the whole scan,
                // silently dropping every in-range row. The columnar SORT ORDER is a physical-layout
                // choice (row-group locality / compression); it must not leak into the segment
                // reader's iteration contract. Sort each block's records by Date here so the
                // emitted key sequence is ascending regardless of the on-disk column sort. Datasets
                // are already emitted in ascending order by the envelope, so per-block date sort
                // yields a globally ascending key stream.
                var blockRecords = new List<TRecord>();
                await foreach (var rec in _inner.ReadAsync(
                    blockStream, DateOnly.MinValue, DateOnly.MaxValue, ColumnProjection.All, direction, cancellationToken)
                    .ConfigureAwait(false))
                {
                    blockRecords.Add(rec);
                }

                blockRecords.Sort(static (a, b) => a.Date.CompareTo(b.Date));
                foreach (var rec in blockRecords)
                {
                    results.Add((new TimeSeriesKey<TDatasetKey>(block.DatasetKey, rec.Date), rec));
                }
            }

            return results;
        }

        public Task ClearAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }

            lock (_indexLock)
            {
                _index = null;
                _tombstones = null;
                _maxSequenceNumber = null;
                _bloom?.Dispose();
                _bloom = null;
                _disposed = true;
            }

            return Task.CompletedTask;
        }

        // Columnar segments have no PER-ROW seqno slot, but the outer envelope footer persists a
        // SEGMENT-LEVEL max seqno (R2-G) — the highest LSM seqno among the flushed entries and
        // tombstones at seal time. Recovery seeds the engine's per-segment max-seqno watermark
        // from this so the C-3 re-insert-vs-tombstone stamp stays correct after a no-WAL reopen,
        // instead of the data-derived 0 that used to be reported. Missing file ⇒ 0 (nothing to
        // recover). Read unconditionally — no pre-footer columnar segment exists on disk
        // (greenfield premise), so there is no legacy-format branch.
        public ulong MaxSequenceNumber
        {
            get
            {
                if (!File.Exists(_path))
                {
                    return 0UL;
                }

                LoadIndex(); // populates _maxSequenceNumber under _indexLock as a side effect
                lock (_indexLock)
                {
                    return _maxSequenceNumber ?? 0UL;
                }
            }
        }

        public bool TryGetRange(out TimeSeriesKey<TDatasetKey> min, out TimeSeriesKey<TDatasetKey> max)
        {
            // Columnar segments are partitioned per-dataset; the engine-side range filter does not
            // apply to a hierarchical TimeSeriesKey (the relevant range is the per-dataset Date
            // window, captured in the row-group zone maps, not a single segment-wide MinKey/MaxKey).
            // Reporting "no range available" tells the engine to fall through to the segment's
            // block-level scan, which the columnar reader implements directly.
            min = default;
            max = default;
            return false;
        }

        // Tombstones persisted in the outer envelope footer (Option A). LoadIndex parses the
        // footer once and caches both the block index and the tombstone map; recovery
        // (LsmStorageEngine.RecoverSegmentsFromDisk) consumes this to rebuild per-segment
        // tombstone state so a no-WAL reopen does not resurrect a delete.
        public IReadOnlyDictionary<TimeSeriesKey<TDatasetKey>, ulong> GetTombstoneSeqnos()
        {
            if (!File.Exists(_path))
            {
                return System.Collections.Frozen.FrozenDictionary<TimeSeriesKey<TDatasetKey>, ulong>.Empty;
            }

            LoadIndex(); // populates _tombstones under _indexLock as a side effect
            lock (_indexLock)
            {
                return _tombstones
                    ?? System.Collections.Frozen.FrozenDictionary<TimeSeriesKey<TDatasetKey>, ulong>.Empty;
            }
        }

        public async Task<(TRecord Value, ulong SequenceNumber, bool Found)> TryGetValueWithSeqnoAsync(
            TimeSeriesKey<TDatasetKey> key,
            ulong maxSequenceNumber,
            CancellationToken cancellationToken = default)
        {
            // Columnar records carry no per-row seqno — a hit reports the segment's footer max
            // seqno as its effective version (see TryGetBlockEntryAsync).
            var (value, found) = await TryGetValueInternalAsync(key, cancellationToken).ConfigureAwait(false);
            return (value, found ? MaxSequenceNumber : 0UL, found);
        }

        public async IAsyncEnumerable<SegmentWriteEntry<TimeSeriesKey<TDatasetKey>, TRecord>> StreamBlockEntriesAsync(
            ulong maxSequenceNumber = ulong.MaxValue,
            ScanDirection direction = ScanDirection.Ascending,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (direction != ScanDirection.Ascending)
            {
                // Non-Ascending iteration would require either a reverse-walk over the
                // dataset index or a buffered scan; v2.0.0 ships ascending only. Threading
                // direction here keeps the envelope's surface in lockstep with the inner
                // IColumnarSegmentFormat<TRecord>.ReadAsync contract so that when descending
                // lands in a future release, both surfaces extend together.
                throw new ArgumentOutOfRangeException(nameof(direction),
                    $"ScanDirection {direction} is not supported in v2.0.0; only Ascending is implemented.");
            }

            var items = await GetAllItemsInternalAsync(direction, cancellationToken).ConfigureAwait(false);
            var segmentMaxSeqno = MaxSequenceNumber;
            foreach (var (key, value) in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Columnar-stored entries are always Inline (ValueKind.ValueLogPointer is
                // rejected at WriteAsync); yield the engine-facing discriminated record
                // directly, stamped with the segment's footer max seqno (no per-row slot) so a
                // snapshot scan resolves a same-segment tombstone exactly as the point read does.
                yield return SegmentWriteEntry<TimeSeriesKey<TDatasetKey>, TRecord>.Inline(key, value, segmentMaxSeqno);
            }
        }

        /// <summary>
        /// Streams every record whose key falls in
        /// <c>[<paramref name="startKey"/>, <paramref name="endKey"/>]</c> with only the fields
        /// named by <paramref name="projection"/> materialised. Unprojected column byte ranges
        /// are not decoded (and in compressed-column mode, not read from disk either, per the
        /// per-column offset map in the segment footer).
        /// </summary>
        /// <remarks>
        /// Only dataset blocks whose key falls in <c>[startKey.Dataset, endKey.Dataset]</c> are
        /// opened; within each block, only records in <c>[from, to]</c> (the intersection of
        /// the block's date span with the caller's range) are yielded. This preserves the
        /// two-level skip the envelope + inner format already deliver for non-projected scans.
        /// </remarks>
        public async IAsyncEnumerable<KeyValuePair<TimeSeriesKey<TDatasetKey>, TRecord>>
            StreamProjectedRangeAsync(
                TimeSeriesKey<TDatasetKey> startKey,
                TimeSeriesKey<TDatasetKey> endKey,
                ColumnProjection projection,
                ScanDirection direction = ScanDirection.Ascending,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (direction != ScanDirection.Ascending)
            {
                throw new ArgumentOutOfRangeException(nameof(direction),
                    $"ScanDirection {direction} is not supported in v2.0.0; only Ascending is implemented.");
            }

            if (!File.Exists(_path))
            {
                yield break;
            }

            // Empty range — preserve the non-projected path's semantics.
            if (startKey.CompareTo(endKey) > 0)
            {
                yield break;
            }

            var index = LoadIndex();
            foreach (var block in index)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Dataset-level skip: only block whose dataset key falls inside
                // [startKey.Dataset, endKey.Dataset] contributes rows. The envelope emits
                // blocks in ascending dataset order so we could break early on overshoot, but
                // the linear scan in FindBlock is already O(datasets-per-segment) — matching
                // that here keeps the code straightforward.
                var cmpStart = TimeSeriesKey<TDatasetKey>.CompareDatasets(block.DatasetKey, startKey.Dataset);
                var cmpEnd = TimeSeriesKey<TDatasetKey>.CompareDatasets(block.DatasetKey, endKey.Dataset);
                if (cmpStart < 0 || cmpEnd > 0)
                {
                    continue;
                }

                // Intersect the block's date span with the caller's date range. If the
                // start/end keys refer to this block's dataset specifically, tighten the
                // date filter using their Date; otherwise the full block span applies.
                var from = cmpStart == 0 ? startKey.Date : DateOnly.FromDayNumber(block.MinDateDayNumber);
                var to = cmpEnd == 0 ? endKey.Date : DateOnly.FromDayNumber(block.MaxDateDayNumber);
                if (from > to)
                {
                    continue;
                }

                // Projection-pushdown: window the open segment file over this block's
                // byte span rather than slurping the whole block into memory first. The inner
                // format seeks straight to each projected column's span through the window, so
                // unprojected columns are never read off disk — the read-amplification win the
                // columnar layout promises. (The non-projected StreamBlockEntriesAsync path still
                // reads whole blocks; it materialises every column anyway.)
                // The inner format yields records in ON-DISK order, which — under a non-date
                // ColumnSortSpec — is NOT date order. This reader's contract
                // (IProjectingSegmentReader.StreamProjectedRangeAsync) promises ascending-by-key
                // order, and an ascending-expecting consumer (k-way merge / range early-break)
                // silently drops every in-range row after the first out-of-order key. Buffer this
                // block's projected records and sort ascending-by-Date before yielding — the same
                // per-block date sort the sibling full-scan path (GetAllItemsInternalAsync) applies,
                // so the emitted key sequence is ascending regardless of the on-disk column sort.
                // Buffering is bounded per-block (row-group/block size), matching the full-scan
                // path's memory profile; the projection-pushdown disk-skip is preserved because the
                // inner ReadAsync still windows and decodes only the projected columns.
                var blockRecords = new List<TRecord>();
                var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
                await using (fs.ConfigureAwait(false))
                {
                    // The footer's block extent is forgeable; validate it with the typed exceptions the
                    // whole-block read path uses before the window's argument checks can see it.
                    ValidateBlockExtent(block, fs.Length);
                    using var blockStream = new WindowReadStream(
                        fs,
                        block.BlockOffset,
                        block.BlockLength,
                        CreatePhysicalSegmentIdentity(_path, block.BlockOffset, block.BlockLength));
                    // Thread the envelope's direction through to the inner format so the row-order
                    // contract is end-to-end consistent (see StreamBlockEntriesAsync above).
                    await foreach (var rec in _inner.ReadAsync(
                        blockStream, from, to, projection, direction, cancellationToken)
                        .ConfigureAwait(false))
                    {
                        blockRecords.Add(rec);
                    }
                }

                blockRecords.Sort(static (a, b) => a.Date.CompareTo(b.Date));
                foreach (var rec in blockRecords)
                {
                    yield return new KeyValuePair<TimeSeriesKey<TDatasetKey>, TRecord>(
                        new TimeSeriesKey<TDatasetKey>(block.DatasetKey, rec.Date),
                        rec);
                }
            }
        }

        private IReadOnlyList<BlockIndex> LoadIndex()
        {
            lock (_indexLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_index is not null)
                {
                    return _index;
                }

                var parsed = ParseFooter(_path);
                var bloom = parsed.BloomState.Length == 0 ? null
                    : SegmentBloomFilter<TimeSeriesKey<TDatasetKey>>.Deserialize(parsed.BloomState, bloomOptions);
                _bloom = bloom;
                _index = parsed.Blocks;
                _tombstones = parsed.Tombstones;
                _maxSequenceNumber = parsed.MaxSequenceNumber;
                return _index;
            }
        }

        public void Dispose()
        {
            lock (_indexLock)
            {
                _disposed = true;
                _bloom?.Dispose();
                _bloom = null;
            }
        }

        // Returns the block of `dataset` whose date span covers `dayNumber`. A dataset may span
        // several blocks; ParseFooter guarantees their date spans ascend without overlapping, so at
        // most one block can hold the date.
        private static BlockIndex? FindBlock(IReadOnlyList<BlockIndex> index, TDatasetKey dataset, int dayNumber)
        {
            // The LSM segment may hold dozens of datasets per flush — linear scan is adequate
            // (index keys are sorted; the match is the key's dataset identity). If
            // segment dataset counts grow past ~1k, switch to a dictionary keyed by the
            // serialised key bytes.
            for (var i = 0; i < index.Count; i++)
            {
                if (TimeSeriesKey<TDatasetKey>.CompareDatasets(index[i].DatasetKey, dataset) == 0
                    && dayNumber >= index[i].MinDateDayNumber
                    && dayNumber <= index[i].MaxDateDayNumber)
                {
                    return index[i];
                }
            }

            return null;
        }

        private static string CreatePhysicalSegmentIdentity(string path, long blockOffset, long blockLength)
        {
            var normalizedPath = Path.GetFullPath(path);
            return FormattableString.Invariant(
                $"bsct:{normalizedPath.Length}:{normalizedPath}:{blockOffset}:{blockLength}");
        }

        private async Task<byte[]> ReadBlockBytesAsync(BlockIndex block, CancellationToken cancellationToken)
        {
            var fs = new FileStream(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            await using (fs.ConfigureAwait(false))
            {
                // The footer block offset/length are attacker-controllable: the footer CRC covers
                // only the footer body and is forgeable. Bound the block span against the actual
                // file length BEFORE the new byte[BlockLength] allocation — otherwise a forged
                // BlockLength (e.g. long.MaxValue) surfaces an uncaught OverflowException at the
                // allocation, or drives an unbounded allocation, instead of the typed truncation
                // the contract promises. The overrun check is written overflow-safe
                // (BlockLength > fs.Length - BlockOffset rather than BlockOffset + BlockLength >
                // fs.Length) so a colossal BlockLength cannot wrap the sum. The projection-pushdown
                // sibling path runs the same check; mirrors the inner format's ValidateColumnExtent guard.
                ValidateBlockExtent(block, fs.Length);

                fs.Seek(block.BlockOffset, SeekOrigin.Begin);
                var buffer = new byte[block.BlockLength];
                await ReadExactlyAsync(fs, buffer, cancellationToken).ConfigureAwait(false);
                return buffer;
            }
        }

        // Shared by every path that opens a block (whole-block reads and the projection-pushdown
        // window) so a forged extent fails with the same typed exception on each.
        private static void ValidateBlockExtent(BlockIndex block, long fileLength)
        {
            if (block.BlockOffset < 0 || block.BlockLength < 0)
            {
                throw new CorruptSegmentException(
                    $"Block declares a negative extent (offset {block.BlockOffset}, length {block.BlockLength}); segment is corrupt.",
                    block.BlockOffset);
            }

            if (block.BlockLength > fileLength - block.BlockOffset)
            {
                throw new TruncatedSegmentException(
                    $"Block declares offset {block.BlockOffset} + length {block.BlockLength}, "
                    + $"which overruns the {fileLength}-byte segment.",
                    fileLength,
                    block.BlockOffset + block.BlockLength);
            }
        }

        private static async Task ReadExactlyAsync(Stream s, byte[] buffer, CancellationToken ct)
        {
            var total = 0;
            while (total < buffer.Length)
            {
                var read = await s.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    throw new TruncatedSegmentException(
                        $"Unexpected end of segment stream after {total} of {buffer.Length} bytes.",
                        s.Length,
                        s.Position + (buffer.Length - total));
                }

                total += read;
            }
        }

        private static ParsedFooter ParseFooter(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var length = fs.Length;

            if (length < HeaderSize + TrailerSize)
            {
                throw new TruncatedSegmentException(
                    $"Segment file length {length} is smaller than the minimum header+trailer {HeaderSize + TrailerSize}.",
                    length,
                    HeaderSize + TrailerSize);
            }

            // Validate trailer magic BEFORE trusting footerLength (which would otherwise be
            // a seek offset derived from garbage bytes on a non-Boutquin file).
            Span<byte> trailer = stackalloc byte[TrailerSize];
            fs.Seek(-TrailerSize, SeekOrigin.End);
            fs.ReadExactly(trailer);
            var footerLength = BinaryPrimitives.ReadUInt32LittleEndian(trailer[..4]);
            var trailerMagic = BinaryPrimitives.ReadUInt32LittleEndian(trailer[4..]);
            if (trailerMagic != TrailerMagic)
            {
                throw new InvalidSegmentException(
                    $"Segment '{Path.GetFileName(path)}' has trailer magic 0x{trailerMagic:X8}, expected 0x{TrailerMagic:X8}. File is not a Boutquin columnar LSM segment.");
            }

            if (footerLength > length - HeaderSize - TrailerSize)
            {
                throw new TruncatedSegmentException(
                    $"Footer length {footerLength} overruns the file ({length} bytes).",
                    length,
                    HeaderSize + footerLength + TrailerSize);
            }

            // Validate header magic + version.
            Span<byte> header = stackalloc byte[HeaderSize];
            fs.Seek(0, SeekOrigin.Begin);
            fs.ReadExactly(header);
            var headerMagic = BinaryPrimitives.ReadUInt32LittleEndian(header[..4]);
            if (headerMagic != HeaderMagic)
            {
                throw new InvalidSegmentException(
                    $"Segment '{Path.GetFileName(path)}' has header magic 0x{headerMagic:X8}, expected 0x{HeaderMagic:X8}.");
            }

            var formatVersion = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            if (formatVersion > CurrentFormatVersion)
            {
                throw new UnsupportedFormatVersionException(formatVersion, CurrentFormatVersion);
            }

            // Read the footer payload.
            var footerOffset = length - TrailerSize - footerLength;
            fs.Seek(footerOffset, SeekOrigin.Begin);
            var footerBytes = new byte[footerLength];
            fs.ReadExactly(footerBytes);

            // Split the CRC suffix off the payload.
            const int crcSize = 4;
            if (footerBytes.Length < crcSize)
            {
                throw new CorruptSegmentException(
                    $"Footer payload is shorter than the minimum CRC suffix ({footerBytes.Length} < {crcSize} bytes).",
                    footerOffset);
            }

            var payload = footerBytes.AsSpan(0, footerBytes.Length - crcSize);
            var storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(footerBytes.AsSpan(footerBytes.Length - crcSize, crcSize));
            var computedCrc = Crc32C.HashToUInt32(payload);
            if (storedCrc != computedCrc)
            {
                throw new CorruptSegmentException(
                    $"Footer CRC mismatch: stored 0x{storedCrc:X8}, computed 0x{computedCrc:X8}.",
                    footerOffset);
            }

            var reader = new SpanReader(payload);
            var datasetCount = reader.ReadUInt32Le();
            // Segment-level max sequence number (R2-G), written immediately after datasetCount.
            // Read unconditionally — the greenfield premise guarantees no pre-footer columnar
            // segment exists on disk, so there is no legacy layout to branch on.
            var maxSequenceNumber = reader.ReadUInt64Le();
            // Clamp the pre-size by the payload length (each block entry is > 1 byte, so the count
            // cannot exceed it): the count is CRC-gated, but a forged-CRC count would otherwise
            // pre-allocate a multi-GB List before the per-entry reads bound it. Defensive only.
            var blocks = new List<BlockIndex>((int)Math.Min(datasetCount, (uint)payload.Length));
            for (var i = 0; i < datasetCount; i++)
            {
                var keyLen = (int)reader.ReadUInt32Le();
                var keyBytes = reader.ReadBytes(keyLen);
                var datasetKey = DecodeFooterKey(keyBytes, TDatasetKey.Deserialize, $"block {i} dataset key", footerOffset);
                var blockOffset = reader.ReadInt64Le();
                var blockLength = reader.ReadInt64Le();
                var rowCount = reader.ReadInt32Le();
                var minDn = reader.ReadInt32Le();
                var maxDn = reader.ReadInt32Le();
                // A forged-but-CRC-valid footer can carry block day-numbers outside the DateOnly
                // range; StreamProjectedRangeAsync later calls FromDayNumber on these, which would
                // leak a raw ArgumentOutOfRangeException out of the public read API. Reject at decode
                // with the typed corruption exception so every read path fails closed.
                if (!CorruptionGuards.IsValidDayNumber(minDn) || !CorruptionGuards.IsValidDayNumber(maxDn))
                {
                    throw new CorruptSegmentException(
                        $"Footer block {i} declares day-number span [{minDn}, {maxDn}]; each must fall within "
                        + $"the DateOnly range [{DateOnly.MinValue.DayNumber}, {DateOnly.MaxValue.DayNumber}].",
                        footerOffset);
                }

                // The writer emits blocks in ascending (dataset, date) order — one dataset's records
                // may span several blocks of consecutive, non-overlapping dates — and every scan relies
                // on it (FindBlock serves the one block covering a date; the k-way merge and the
                // range-scan early break need an ascending source). Anything else is corruption.
                if (minDn > maxDn)
                {
                    throw new CorruptSegmentException(
                        $"Footer block {i} declares an inverted day-number span [{minDn}, {maxDn}].",
                        footerOffset);
                }

                if (blocks.Count > 0)
                {
                    var datasetOrder = TimeSeriesKey<TDatasetKey>.CompareDatasets(datasetKey, blocks[^1].DatasetKey);
                    if (datasetOrder < 0 || (datasetOrder == 0 && minDn <= blocks[^1].MaxDateDayNumber))
                    {
                        throw new CorruptSegmentException(
                            $"Footer block {i} does not sort strictly after block {i - 1}: the block index must "
                            + "ascend by dataset key, and blocks of one dataset must cover ascending, "
                            + "non-overlapping date spans.",
                            footerOffset);
                    }
                }

                blocks.Add(new BlockIndex(datasetKey, blockOffset, blockLength, rowCount, minDn, maxDn));
            }

            // Tombstone section (Option A) — always present (count 0 when none). Keys are
            // TimeSeriesKey<TDatasetKey>; the section sits inside the CRC-checked footer payload,
            // so a corrupt section is already rejected by the CRC check above.
            var tombstoneCount = reader.ReadUInt32Le();
            // Clamp the pre-size by the payload length — see the block-index count above (defensive).
            // Keyed by serialized-bytes identity so a caller's own key instance resolves the
            // deserialized footer keys; a key recorded twice keeps its newest seqno.
            var tombstones = new Dictionary<TimeSeriesKey<TDatasetKey>, ulong>(
                (int)Math.Min(tombstoneCount, (uint)payload.Length),
                SerializedKeyEqualityComparer<TimeSeriesKey<TDatasetKey>>.Instance);
            for (var i = 0; i < tombstoneCount; i++)
            {
                var tombKeyLen = (int)reader.ReadUInt32Le();
                var tombKeyBytes = reader.ReadBytes(tombKeyLen);
                var tombstoneKey = DecodeFooterKey(
                    tombKeyBytes, TimeSeriesKey<TDatasetKey>.Deserialize, $"tombstone {i} key", footerOffset);
                var tombstoneSeqno = reader.ReadUInt64Le();
                if (!tombstones.TryGetValue(tombstoneKey, out var recorded) || tombstoneSeqno > recorded)
                {
                    tombstones[tombstoneKey] = tombstoneSeqno;
                }
            }

            if (reader.Remaining > 256 * 1024 * 1024 + 512)
            {
                throw new CorruptSegmentException("Bloom envelope exceeds the supported size.", footerOffset);
            }
            return new ParsedFooter(blocks, tombstones, maxSequenceNumber, reader.ReadBytes(reader.Remaining));
        }

        // A key decoded from a forged-but-CRC-valid footer fails inside its own deserialiser, whose
        // corruption types are the BCL decode exceptions; surface the envelope's typed corruption
        // exception instead, as the block day-number guard does.
        private static T DecodeFooterKey<T>(byte[] keyBytes, Func<Stream, T> deserialize, string field, long footerOffset)
        {
            using var keyStream = new MemoryStream(keyBytes, writable: false);
            try
            {
                return deserialize(keyStream);
            }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or ArgumentException
                or OverflowException or FormatException)
            {
                throw new CorruptSegmentException(
                    $"Footer {field} does not decode: {ex.Message}", footerOffset, ex);
            }
        }

        private sealed record ParsedFooter(
            IReadOnlyList<BlockIndex> Blocks,
            IReadOnlyDictionary<TimeSeriesKey<TDatasetKey>, ulong> Tombstones,
            ulong MaxSequenceNumber,
            byte[] BloomState);

        private sealed record BlockIndex(
            TDatasetKey DatasetKey,
            long BlockOffset,
            long BlockLength,
            int RowCount,
            int MinDateDayNumber,
            int MaxDateDayNumber);

        private ref struct SpanReader
        {
            private readonly ReadOnlySpan<byte> _buffer;
            private int _position;
            public readonly int Remaining => _buffer.Length - _position;

            public SpanReader(ReadOnlySpan<byte> buffer)
            {
                _buffer = buffer;
                _position = 0;
            }

            public uint ReadUInt32Le()
            {
                EnsureRemaining(4);
                var v = BinaryPrimitives.ReadUInt32LittleEndian(_buffer.Slice(_position, 4));
                _position += 4;
                return v;
            }

            public int ReadInt32Le()
            {
                EnsureRemaining(4);
                var v = BinaryPrimitives.ReadInt32LittleEndian(_buffer.Slice(_position, 4));
                _position += 4;
                return v;
            }

            public long ReadInt64Le()
            {
                EnsureRemaining(8);
                var v = BinaryPrimitives.ReadInt64LittleEndian(_buffer.Slice(_position, 8));
                _position += 8;
                return v;
            }

            public ulong ReadUInt64Le()
            {
                EnsureRemaining(8);
                var v = BinaryPrimitives.ReadUInt64LittleEndian(_buffer.Slice(_position, 8));
                _position += 8;
                return v;
            }

            public byte[] ReadBytes(int count)
            {
                EnsureRemaining(count);
                var bytes = _buffer.Slice(_position, count).ToArray();
                _position += count;
                return bytes;
            }

            // The footer payload is CRC-gated, so this guard is only reachable through a
            // forged-CRC footer — but a forged length field must still surface as the typed
            // CorruptSegmentException, not a raw ArgumentOutOfRangeException from span
            // slicing. Mirrors the inner FooterReader's EnsureRemaining discipline.
            private readonly void EnsureRemaining(int count)
            {
                if (count < 0 || (long)_position + count > _buffer.Length)
                {
                    throw new CorruptSegmentException(
                        $"Footer field at offset {_position} declares {count} bytes but only "
                        + $"{_buffer.Length - _position} remain. Footer is forged or corrupt.",
                        _position);
                }
            }
        }
    }
}

/// <summary>
/// Lets <see cref="LsmTimeSeriesStorageEngine{TDatasetKey, TRecord}"/> check, without naming the
/// columnar record constraint, that its columnar options describe the format this envelope
/// actually wraps.
/// </summary>
internal interface IColumnarSegmentEnvelope
{
    /// <summary>
    /// <see langword="true"/> when <paramref name="options"/> is a
    /// <see cref="ColumnarTimeSeriesStoreOptions{TRecord}"/> whose
    /// <see cref="ColumnarTimeSeriesStoreOptions{TRecord}.ColumnarFormat"/> is the inner format
    /// this envelope writes and reads.
    /// </summary>
    /// <param name="options">The time-series engine options to check.</param>
    bool IsDescribedBy(object options);
}
