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
using Boutquin.Storage.Infrastructure.KeyValueStore;

namespace Boutquin.Storage.Infrastructure.WriteAheadLog;

/// <summary>
/// A directory-based WriteAheadLog that provides durability for key-value operations.
/// Frames are written into rolling segment files <c>wal_NNNNNN.log</c> and durability is
/// established via group-commit fsync (D1) by default.
/// </summary>
/// <typeparam name="TKey">The type of the keys in the WriteAheadLog.</typeparam>
/// <typeparam name="TValue">The type of the values in the WriteAheadLog.</typeparam>
/// <remarks>
/// <para>
/// <b>Frame format.</b> v2.0.0 ships a single WAL frame format. Each segment file begins
/// with an 8-byte header <c>[4B magic 'B','S','W','L'] [4B format_version LE uint32 = 1]</c>
/// written once on segment creation. Each entry:
/// <code>
/// [4B payload-length (LE int32)] [1B EntryKind] [1B ValueKind]
/// [8B SequenceNumber LE uint64] [4B header CRC32C (LE uint32)]
/// [N bytes payload] [4B payload CRC32C (LE uint32)]
/// </code>
/// The kind byte carries <see cref="EntryKind.Set"/> (<c>0x00</c>) or
/// <see cref="EntryKind.Delete"/> (<c>0x01</c>); the 8-byte seqno carries the engine-assigned
/// monotonic sequence number so recovery can reseed the engine counter. Two CRC32C checksums
/// (Castagnoli polynomial — same variant as the SSTable footer and value-log) authenticate the
/// frame: the header checksum covers <c>[length][entry_kind][value_kind][seqno]</c>, the payload
/// checksum covers the payload bytes. Because the length prefix is authenticated on its own, a
/// bit-flip in it surfaces as a header-checksum mismatch rather than as a frame that merely
/// appears to run past end of file.
/// </para>
/// <para>
/// <b>Segment rotation (D2).</b> Segments roll over once
/// <see cref="WriteAheadLogOptions.SegmentMaxBytes"/> is reached (default 64 MiB). Files are
/// named <c>wal_NNNNNN.log</c> with a six-digit monotonic counter — same pattern as SSTable
/// segments. <see cref="RecoverAsync"/> enumerates segments in counter order;
/// <see cref="TruncateAsync"/> deletes every segment and resets the counter (matches the
/// "watermark = entire log" case used by <c>LsmStorageEngine</c> after a successful flush).
/// </para>
/// <para>
/// <b>Group commit (D1).</b> Appends are coalesced through a single-reader channel and a
/// background flusher loop performs one fsync per batch. The batch is bounded by
/// <see cref="WriteAheadLogOptions.GroupCommitBatchSize"/> (default 256) and
/// <see cref="WriteAheadLogOptions.GroupCommitLatencyMs"/> (default 5 ms). Throughput scales
/// with the disk's sustained write bandwidth instead of being capped at "one fsync per
/// append" (hundreds of IOPS).
/// </para>
/// <para>
/// <b>Durability invariants preserved (DDIA p. 128).</b> (a) The frame is fsync'd before
/// any batched <c>SetResult</c> fires — writers see completion only after durable write.
/// (b) MemTable mutation may happen before, during, or after fsync — recovery replays the
/// WAL independently. (c) Group commit preserves the WAL-durable-before-apply invariant;
/// batch size only affects latency, not correctness.
/// </para>
/// <para>
/// <b>Failure semantics.</b> If a batch's write or fsync throws (disk full, I/O error), the
/// flusher loop sets <see cref="WriteAheadLogCorruptException"/> on every TCS in the batch, closes
/// the append stream and truncates the segment back to the batch's start (fsync'd), so no frame of
/// a batch its callers saw fail is replayed by a later recovery, marks the WAL corrupt, and continues
/// running so reads stay available. If that truncation fails too, the batch's bytes are whatever
/// reached the disk. Subsequent appends fast-fail
/// with <see cref="WriteAheadLogCorruptException"/> — including appends that were already queued
/// when the batch failed, which are refused without being written, so the failed batch's torn
/// bytes stay a torn tail instead of being sealed mid-segment by a later acknowledged frame.
/// </para>
/// <para>
/// <b>Recovery semantics:</b> Recovery reads each segment frame by frame and applies one
/// deterministic tear rule, resting only on bytes the writer authenticated: everything after the
/// last whole frame is the unacknowledged, interrupted write — and is truncated — exactly when it is
/// consistent with a single interrupted write, i.e. a strict prefix of what the writer was writing
/// followed only by zeros (the file length reached disk, the rest of the data did not; no frame
/// encodes as zeros). Concretely, a torn tail is a frame cut short at end of file (a partial header,
/// or a valid header whose payload is short); a header that fails its checksum but is a strict prefix
/// of a header the writer emits, with only zeros after it to end of file (an all-zero run is the empty
/// prefix); or a frame whose payload checksum fails while its checksum trailer is an unwritten suffix —
/// its last byte zero and every trailer byte that landed equal to the checksum of the landed payload —
/// with only zeros (or nothing) after it: the landed data of a group-commit batch, written whole
/// before its single fsync, can stop inside any of its frames, and the rest of the batch reads as
/// zeros. On the last segment a torn tail is truncated and recovery returns every entry before it.
/// Any other damage — a complete header whose checksum fails, a payload checksum failure on a frame
/// whose last byte landed (each append is acknowledged only after its fsync, so that frame was written
/// completely) or whose landed checksum bytes disagree with its payload, a checksum failure with a
/// non-zero byte anywhere after it, or a torn tail on any segment but the last — is corruption of acknowledged
/// history: recovery throws <see cref="WriteAheadLogCorruptException"/> naming the segment and the
/// frame's offset, and leaves the segment untouched rather than destroy the valid frames that follow.
/// A crash that reorders page writeback inside the final, not-yet-acknowledged batch can leave zeros
/// followed by later bytes of the batch; recovery fails closed there too, which loses no acknowledged
/// write but needs an operator to inspect the segment. A complete frame whose checksums match but
/// which this reader refuses (length outside the accepted range, unknown kind or value-kind byte, a
/// key or value whose decoder throws) is bytes the writer authenticated, never a torn append, so recovery and the
/// append-side repair throw <see cref="WriteAheadLogCorruptException"/> at that frame wherever it
/// sits, with the decoder's exception as the cause, and leave the segment untouched. Segment-header
/// damage (too short, wrong magic, unknown format version) throws the same typed exception naming
/// the segment, with one exception: a frameless first segment (<c>wal_000001.log</c>, the only one
/// ever created in place) that is the last segment and is empty or holds only a header prefix and/or
/// zero bytes is a crash inside segment creation, which recovery truncates to empty and the append
/// path re-initialises. Every later segment is published by rotation only after its header is
/// durable, so the same shape there — the segment emptied, cut inside its header or zero-filled —
/// throws <see cref="WriteAheadLogCorruptException"/> on both paths and leaves the file untouched,
/// as a deleted last segment does.
/// </para>
/// <para>
/// <b>Segment seals.</b> When a segment rotates, the writer first creates its successor under a
/// pending name (<c>wal_NNNNNN.pending</c>: header written, file and directory fsync'd), then appends
/// one last frame to the full segment — an <see cref="EntryKind.SegmentSeal"/> frame whose payload
/// records the segment's own index, its length before the seal, its data-frame count, and a CRC32C
/// chained over its file header and each frame's stored checksums — fsyncs it, and only then renames
/// the successor to its segment name and fsyncs the directory. A sealed segment is never written
/// again. Recovery therefore requires every segment but the last to end in a seal that matches it,
/// the segment indices from the first segment to the last to be contiguous, and the last segment not
/// to be sealed; a sealed segment emptied, cut to its header or at a frame boundary, extended, with
/// frames reordered, or renamed or swapped with another segment file, a missing middle segment, and
/// a deleted last segment (which leaves its sealed predecessor last) — damage that leaves no damaged
/// frame — throw <see cref="WriteAheadLogCorruptException"/>. Only the last segment is ever
/// tear-repaired. Every crash state of a rotation recovers in full: before the seal is durable the
/// full segment is still the last one (a torn seal is truncated like any interrupted write) and the
/// pending successor is discarded; after it, the pending successor is published and appends resume
/// there. Reclamation (<see cref="TruncateCompletedSegmentsAsync"/>, <see cref="TruncateAsync"/>)
/// deletes oldest-first, only ever a contiguous prefix, making each unlink durable before the next,
/// so no crash state of a reclamation looks like a missing segment; and it forgets the current
/// segment's header before unlinking it, so an unlink whose directory fsync then fails never leaves a
/// later append writing into a recreated, headerless file. A reclamation that fails part-way resumes
/// on the highest surviving segment, or on segment 1 when none survives, so the writer never
/// re-creates a later-numbered segment in place.
/// </para>
/// <para>
/// <b>What the format does not detect.</b> The format detects crash tears and damage to bytes that
/// were written, not the deliberate removal of whole, fsync'd frames from the end of the log. The last
/// segment cut exactly at a frame boundary (including down to its bare, valid header; and, for the
/// first segment only, to nothing) is indistinguishable
/// from "those frames were never written", and deleting every segment is indistinguishable from an
/// empty log; neither is a crash state, and recovery returns what remains. Closing these would need a
/// durable high-water mark written on every append, at the cost of a second fsync per append.
/// </para>
/// <para>
/// <b>Standalone-append safety.</b> A consumer is NOT required to call
/// <see cref="RecoverAsync"/> before <see cref="AppendAsync"/>. When the WAL opens a
/// pre-existing segment for append for the first time after construction, it repairs any torn
/// tail left by a prior crash (scans to the last cleanly-parsed frame boundary and truncates
/// there) before the first frame is written — using the same frame scan and tear rule recovery
/// uses, so the repaired boundary matches where recovery would stop, and damage recovery would
/// refuse makes the append fail with the same typed exception. This guarantees the appended frame
/// lands at a clean boundary and survives the next <see cref="RecoverAsync"/>; without it, an
/// append after a crash's torn bytes would be silently dropped on the next recovery, which
/// stops at the tear. The repair runs only on the first open of an existing
/// segment (never on subsequent appends) and is a no-op when the tail is already clean (e.g.
/// after <see cref="RecoverAsync"/> already repaired it), so the steady-state append path is
/// unaffected.
/// </para>
/// <para>
/// <b>Single writer, enforced.</b> Exactly one live instance may own a WAL directory. The
/// constructor takes an OS-level exclusive lock on the sidecar <c>wal.lock</c> file
/// (<see cref="FileShare.None"/>: a mandatory sharing violation on Windows, an exclusive
/// <c>flock</c> on Unix — both exclude other handles in the same process as well as other
/// processes) and holds it until <see cref="Dispose"/>. Without it, two instances — a second
/// DI container in-process, or a second process — each pin an independent append offset
/// (.NET append-mode streams write at the offset captured at open; <see cref="FileShare"/>
/// alone grants no write exclusion on Unix) and overwrite each other's committed frames.
/// Cooperative multi-writer coordination (the <c>AppendOnlyEventLog</c> remedy) is not
/// possible here: sequence numbers are engine-assigned and rotation/truncation state is
/// per-instance, so a second live instance is always a topology error and fails fast with
/// <see cref="IOException"/> at construction.
/// </para>
/// </remarks>
public sealed class WriteAheadLog<TKey, TValue> : IWriteAheadLog<TKey, TValue>
    where TKey : IStorageKey<TKey>, new()
    where TValue : ISerializable<TValue>, new()
{
    /// <summary>
    /// Maximum allowed payload size (256 MB). Appends whose payload is larger are rejected before
    /// acknowledgement, and recovery treats a larger length prefix as corrupt to guard against
    /// reading garbage length values.
    /// </summary>
    private const int MaxPayloadSize = 256 * 1024 * 1024;

    // File header: [4B magic 'BSWL'][4B format_version LE uint32].
    private static readonly byte[] s_magic = [(byte)'B', (byte)'S', (byte)'W', (byte)'L'];
    // Format-version 1 — the first persisted WAL format under the greenfield premise. Uses
    // Crc32C (Castagnoli polynomial) for both per-frame checksums, matching every other on-disk
    // surface in the engine (SSTable footer, value-log). The header checksum authenticates the
    // length prefix together with the kind, value-kind and seqno fields, separately from the
    // payload checksum, so a rotted length prefix is detected as corruption instead of being
    // inferred from the bytes around it.
    private const uint FormatVersion = 1;
    private const int FileHeaderSize = 8;

    // Each in-file entry (v2.0.0 WiscKey-integrated):
    //   [4B length][1B entry_kind][1B value_kind][8B seqno][4B header CRC32C][payload][4B payload CRC32C].
    // The header CRC covers the 14 bytes before it; the payload CRC covers the payload bytes.
    // The value_kind byte is the Phase-WiscKey addition — orthogonal to entry_kind
    // For Set+Inline frames the payload is [key][value]; for Set+ValueLogPointer it is
    // [key][12-byte pointer]; for Delete frames it is [key] only (value_kind is written as
    // Inline/0x00 and ignored).
    private const int LengthPrefixSize = 4;
    private const int KindByteSize = 1;
    private const int ValueKindByteSize = 1;
    private const int SeqnoBytesSize = 8;
    private const int CrcBytesSize = 4;
    private const int HeaderFieldsSize = LengthPrefixSize + KindByteSize + ValueKindByteSize + SeqnoBytesSize;
    private const int FrameHeaderSize = HeaderFieldsSize + CrcBytesSize;
    private const int FrameOverheadBytes = FrameHeaderSize + CrcBytesSize;

    // Segment seal (EntryKind.SegmentSeal), the final frame of every rotated segment. Payload:
    //   [8B segment index LE uint64][8B sealed length LE uint64][8B frame count LE uint64]
    //   [4B segment digest LE uint32]
    // The segment index is the counter in the file's own name, so a sealed segment file renamed,
    // or swapped with another, no longer matches its seal. The sealed length is the seal frame's
    // offset (the file header plus every data frame), the count counts those data frames, and the
    // digest is a CRC32C over the 8-byte file header followed, in order, by each data frame's stored
    // header and payload checksums. Each frame's two checksums already authenticate every byte of
    // that frame, so chaining them authenticates the segment's content and frame order without
    // re-hashing every payload byte on the flusher thread.
    private const int SealPayloadSize = 8 + 8 + 8 + CrcBytesSize;

    // Segment file naming: wal_000001.log, wal_000002.log, ... — a monotonic counter zero-padded
    // to at least six digits, same shape as SSTable segments so operators see one consistent
    // layout. The padding is a minimum width: from index 1,000,000 the name grows to seven digits,
    // so discovery parses the whole digit run, never a fixed-width slice.
    private const string SegmentFilePattern = "wal_{0:D6}.log";
    private const string SegmentFileGlob = "wal_*.log";

    // A rotation's successor segment is created, fsync'd and made durable under this name BEFORE the
    // current segment is sealed, and renamed to its segment name only after the seal is durable. So a
    // sealed segment always has a successor on disk — under one name or the other — and a sealed
    // LAST segment with no pending successor is a deleted segment, not a crash state. The extension
    // keeps it outside SegmentFileGlob: segment discovery never mistakes it for a segment.
    private const string PendingSuccessorPattern = "wal_{0:D6}.pending";
    private const string PendingSuccessorGlob = "wal_*.pending";
    private const int SegmentDigits = 6;

    // Sidecar writer-lock filename. Deliberately outside the SegmentFileGlob shape so segment
    // discovery, recovery, and truncation never mistake it for a segment.
    private const string WriterLockFileName = "wal.lock";

    private readonly string _directory;
    private readonly WriteAheadLogOptions _options;

    // OS-level exclusive lock on the sidecar <directory>/wal.lock file, held for this
    // instance's whole lifetime and released (the handle disposed, the file never deleted) by
    // Dispose. This is the single-writer enforcement: the long-lived append stream below pins
    // its file offset at open time (.NET append-mode streams pwrite at that offset on Unix —
    // no O_APPEND — and FileShare grants no write exclusion there), so a second live instance
    // over the same directory would land group-commits at a stale offset, burying committed
    // frames — the same corruption class fixed in AppendOnlyEventLog (2026-07-16, MarketData
    // ingestion.log incident). Unlike the event log, WAL writers cannot cooperate (sequence
    // numbers are engine-assigned; rotation state is per-instance), so the second instance
    // fails fast at construction instead of coordinating per append.
    private readonly FileStream _writerLock;

    // Test-only observability (same convention as FileRaftLog._directoryFsyncCount): counts the
    // directory fsyncs performed after segment DELETIONS. On POSIX filesystems an unlink is not
    // durable until the directory inode lands (Pillai et al. 2014 §3); without it a crash can
    // resurrect a deleted WAL segment whose unconditional replay shadows newer SSTable state.
    internal int _deletionDirectoryFsyncCount;

    // Test-only observability (same convention): the number of directory levels the constructor
    // created with every new entry fsync'd into its parent. A WAL whose own directory entry is not
    // durable can vanish with every acknowledged frame in it after a crash.
    internal int _durablyCreatedDirectoryLevels;

    // Test-only fault injection (same convention as the 2PC journal's FaultInjector): invoked with
    // a step name immediately before that durable step runs, so a test can make the step fail
    // exactly as a failing disk would — after every earlier step of the operation has landed.
    // Null in production. Steps: "deletion-directory-fsync", "rotation-seal" (the pending successor
    // is durable, the seal not yet written) and "rotation-publish" (the seal is durable, the
    // successor not yet renamed to its segment name).
    internal Action<string>? _faultInjector;

    // Group-commit state. Both are created unconditionally in the constructor — the earlier
    // "optional group commit" design is retired, so the fields are non-nullable and use sites
    // carry no suppression dance.
    private readonly Channel<FlusherRequest> _channel;
    private readonly Task _flusherTask;
    private readonly CancellationTokenSource _shutdownCts = new();
    private FlusherRequest? _pendingControl;

    // One permit per append frame that may exist at once — built, queued, or in the flusher's
    // batch. A producer takes a permit BEFORE serializing and renting its frame, and the flusher
    // returns it with the frame, so WriteQueueCapacity caps pinned frame buffers rather than only
    // admitted requests (producers blocked on a full channel would otherwise each hold one).
    private readonly SemaphoreSlim _appendFrameSlots;

    // Stream / segment state. Touched by the flusher loop only — group commit serialises every
    // mutation through the single-reader channel, and Dispose awaits flusher completion before
    // closing the stream, so no additional gate is required.
    private FileStream? _appendStream;
    private long _currentSegmentLength;
    private int _currentSegmentIndex = 1;
    private bool _headerResolvedForCurrent;

    // What the current segment's seal will record: its data-frame count and the running seal-digest
    // CRC32C state (file header, then each written frame's two stored checksums). Initialised when
    // the segment is created or first opened for append (from the same scan the tail repair runs),
    // advanced per frame the flusher writes. Flusher-loop state, like the stream fields above.
    private long _currentSegmentFrameCount;
    private uint _currentSegmentCrcState = Algorithms.Crc32C.InitialState;

    // Highest sequence number appended to each segment, keyed by segment index. Flusher-loop
    // state (appends update the current segment's entry; recovery records every scanned
    // segment's max). Drives TruncateCompletedSegmentsAsync: a segment is deletable only
    // when its recorded max seqno is fully covered by the caller's flushed watermark —
    // discarding the "corresponding part" of the log (DDIA 2e Ch. 4), never frames that
    // still back unflushed memtable state. Segments with no recorded max are retained.
    private readonly Dictionary<int, ulong> _segmentMaxSeqnos = [];

    private int _disposed;
    private volatile bool _isCorrupt;

    /// <summary>
    /// Initializes a new instance of the <see cref="WriteAheadLog{TKey, TValue}"/> class.
    /// </summary>
    /// <param name="directory">
    /// The directory in which WAL segment files (<c>wal_NNNNNN.log</c>) are stored. Created on
    /// demand.
    /// </param>
    /// <param name="options">
    /// Optional group-commit and segment-rotation configuration. <see langword="null"/>
    /// applies the defaults (group commit on, 256-append batch / 5 ms latency cap, 64 MiB
    /// segment rotation threshold).
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown if <paramref name="directory"/> is <see langword="null"/> or whitespace.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown if <paramref name="options"/> carries a value outside the documented valid
    /// range for any field.
    /// </exception>
    /// <exception cref="IOException">
    /// Thrown if another live <see cref="WriteAheadLog{TKey, TValue}"/> instance — in this
    /// process or another — already holds the directory's exclusive writer lock
    /// (<c>wal.lock</c>). See the single-writer remarks on the class.
    /// </exception>
    public WriteAheadLog(string directory, WriteAheadLogOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentNullException(nameof(directory));
        }

        _options = options ?? new WriteAheadLogOptions();
        ValidateOptions(_options);

        _directory = directory;

        // Every directory level this creates gets its entry fsync'd into its parent: segment
        // creation fsyncs only _directory itself, so without this a crash could drop the WAL
        // directory's own entry — and every acknowledged frame beneath it.
        _durablyCreatedDirectoryLevels = DurableFile.CreateDirectoryDurable(_directory);

        // Single-writer enforcement — see the _writerLock field remarks. Acquired before any
        // other startup work so a second live instance fails here, having started nothing.
        _writerLock = AcquireWriterLock(_directory);

        // Initialise the segment counter to the highest existing segment so appends RESUME
        // in that segment after a restart (recovery repairs any torn tail first); a fresh
        // directory starts at 1.
        _currentSegmentIndex = DiscoverNextSegmentIndex();

        // Bounded write queue (FullMode.Wait) so a flusher that falls behind under sustained
        // write pressure applies async back-pressure to producers instead of letting the queue
        // — and the rented ArrayPool frame each request pins — grow without bound. Wait mode
        // never drops a frame, so durability is preserved: AppendAsync's await on WriteAsync
        // simply blocks until the flusher frees a slot.
        _channel = Channel.CreateBounded<FlusherRequest>(new BoundedChannelOptions(_options.WriteQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            AllowSynchronousContinuations = false,
        });
        _appendFrameSlots = new SemaphoreSlim(_options.WriteQueueCapacity, _options.WriteQueueCapacity);
        _flusherTask = Task.Run(FlusherLoopAsync);
    }

    /// <summary>
    /// Acquires the OS-level exclusive writer lock by opening the sidecar
    /// <c>wal.lock</c> file with <see cref="FileShare.None"/> — a mandatory sharing violation
    /// for any other handle on Windows, an exclusive <c>flock</c> on Unix; both exclude other
    /// handles within the same process as well as other processes. No polling: a second live
    /// <see cref="WriteAheadLog{TKey, TValue}"/> over the same directory is a topology error
    /// (uncoordinated writers overwrite each other's committed frames), so contention fails
    /// fast rather than waits. The sidecar file itself is deliberately never deleted —
    /// unlinking it would hand a concurrent opener a lock on a dead inode.
    /// </summary>
    private static FileStream AcquireWriterLock(string directory)
    {
        var lockPath = Path.Combine(directory, WriterLockFileName);
        try
        {
            return new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);
        }
        catch (IOException ex)
        {
            throw new IOException(
                $"WriteAheadLog directory '{directory}' already has a live writer — the sidecar " +
                $"lock '{WriterLockFileName}' is held by another WriteAheadLog instance (in this " +
                "process or another). Two live instances over one directory pin independent append " +
                "offsets and overwrite each other's committed frames; dispose the existing instance " +
                "instead of constructing a second one.",
                ex);
        }
    }

    private static void ValidateOptions(WriteAheadLogOptions options)
    {
        if (options.GroupCommitBatchSize is < 1 or > 4096)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.GroupCommitBatchSize,
                "GroupCommitBatchSize must be between 1 and 4096.");
        }
        if (options.GroupCommitLatencyMs is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.GroupCommitLatencyMs,
                "GroupCommitLatencyMs must be between 1 and 1000.");
        }
        if (options.SegmentMaxBytes < 1L * 1024L * 1024L)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.SegmentMaxBytes,
                "SegmentMaxBytes must be at least 1 MiB so individual frames always fit.");
        }
        if (options.WriteQueueCapacity is < 1 or > 65536)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.WriteQueueCapacity,
                "WriteQueueCapacity must be between 1 and 65536.");
        }
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when the serialized frame payload exceeds the 256 MiB ceiling recovery accepts;
    /// nothing is written or acknowledged.
    /// </exception>
    public Task AppendAsync(TKey key, TValue value, ulong sequenceNumber, CancellationToken cancellationToken = default)
        => AppendCoreAsync(key, value, pointer: null, EntryKind.Set, ValueKind.Inline, sequenceNumber, cancellationToken);

    /// <inheritdoc/>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when the serialized frame payload exceeds the 256 MiB ceiling recovery accepts;
    /// nothing is written or acknowledged.
    /// </exception>
    public Task AppendPointerAsync(
        TKey key,
        Boutquin.Storage.Domain.ValueObjects.ValueLog.ValueLogPointer pointer,
        ulong sequenceNumber,
        CancellationToken cancellationToken = default)
        => AppendCoreAsync(key, default!, pointer, EntryKind.Set, ValueKind.ValueLogPointer, sequenceNumber, cancellationToken);

    /// <inheritdoc/>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when the serialized frame payload exceeds the 256 MiB ceiling recovery accepts;
    /// nothing is written or acknowledged.
    /// </exception>
    public Task AppendDeleteAsync(TKey key, ulong sequenceNumber, CancellationToken cancellationToken = default)
        => AppendCoreAsync(key, default!, pointer: null, EntryKind.Delete, ValueKind.Inline, sequenceNumber, cancellationToken);

    private async Task AppendCoreAsync(
        TKey key,
        TValue value,
        Boutquin.Storage.Domain.ValueObjects.ValueLog.ValueLogPointer? pointer,
        EntryKind kind,
        ValueKind valueKind,
        ulong sequenceNumber,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfCorrupt();

        await _appendFrameSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        byte[] frameBuffer;
        int frameLength;
        try
        {
            (frameBuffer, frameLength) = BuildFrame(key, value, pointer, kind, valueKind, sequenceNumber);
        }
        catch
        {
            _appendFrameSlots.Release();
            throw;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new FlusherRequest(
            RequestKind.Append, frameBuffer, frameLength, tcs, RecoverTcs: null, sequenceNumber);
        try
        {
            await _channel.Writer.WriteAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(frameBuffer);
            _appendFrameSlots.Release();
            throw;
        }

        await tcs.Task.ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<WalEntry<TKey, TValue>>> RecoverAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        var tcs = new TaskCompletionSource<IReadOnlyList<WalEntry<TKey, TValue>>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new FlusherRequest(RequestKind.Recover, Frame: null, FrameLength: 0, CompletionTcs: null, RecoverTcs: tcs);
        await _channel.Writer.WriteAsync(request, cancellationToken).ConfigureAwait(false);
        return await tcs.Task.ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task TruncateAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new FlusherRequest(RequestKind.Truncate, Frame: null, FrameLength: 0, CompletionTcs: tcs, RecoverTcs: null);
        await _channel.Writer.WriteAsync(request, cancellationToken).ConfigureAwait(false);
        await tcs.Task.ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task TruncateCompletedSegmentsAsync(ulong upToSeqnoInclusive, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new FlusherRequest(
            RequestKind.TruncateCompleted, Frame: null, FrameLength: 0, CompletionTcs: tcs, RecoverTcs: null,
            SequenceNumber: upToSeqnoInclusive);
        await _channel.Writer.WriteAsync(request, cancellationToken).ConfigureAwait(false);
        await tcs.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the path of the WAL segment currently being written to (numbered by
    /// <c>_currentSegmentIndex</c>). Surfaces the segment files to consumers that need to
    /// inspect the rotation state — primarily tests asserting D2 behaviour. Using the
    /// instance counter keeps the path consistent with what the next append will hit.
    /// </summary>
    public string GetCurrentSegmentPath() => Path.Combine(_directory, string.Format(
        System.Globalization.CultureInfo.InvariantCulture,
        SegmentFilePattern,
        _currentSegmentIndex));

    /// <summary>
    /// Returns every WAL segment file the recovery path will consider, in counter order.
    /// Lets consumers (and tests) observe the rotation state without reaching into
    /// <see cref="Directory"/>.<c>EnumerateFiles</c>.
    /// </summary>
    public IReadOnlyList<string> GetAllSegmentPaths() => DiscoverSegmentFiles();

    private static (byte[] Buffer, int Length) BuildFrame(
        TKey key,
        TValue value,
        Boutquin.Storage.Domain.ValueObjects.ValueLog.ValueLogPointer? pointer,
        EntryKind kind,
        ValueKind valueKind,
        ulong sequenceNumber)
    {
        // Serialize the payload first — a frame's length prefix counts payload bytes only.
        // (Set, Inline)          → [key][value]
        // (Set, ValueLogPointer) → [key][12-byte pointer]
        // (Delete, Inline)       → [key]
        using var payloadStream = new MemoryStream();
        key.Serialize(payloadStream);
        if (kind == EntryKind.Set)
        {
            if (valueKind == ValueKind.Inline)
            {
                value.Serialize(payloadStream);
            }
            else
            {
                var pointerValue = pointer
                    ?? throw new InvalidOperationException("ValueKind.ValueLogPointer requires a non-null pointer.");
                Span<byte> pointerBytes = stackalloc byte[Boutquin.Storage.Domain.ValueObjects.ValueLog.ValueLogPointer.SerializedSize];
                pointerValue.WriteTo(pointerBytes);
                payloadStream.Write(pointerBytes);
            }
        }

        if (!payloadStream.TryGetBuffer(out var payloadSegment))
        {
            payloadSegment = new ArraySegment<byte>(payloadStream.ToArray());
        }
        var payloadLength = (int)payloadStream.Length;
        if (payloadLength <= 0)
        {
            throw new InvalidOperationException(
                "WAL payload serialised to zero bytes — TKey/TValue serializers must emit at least one byte.");
        }

        // The reader classifies any length prefix above MaxPayloadSize as a corrupt frame, so a
        // larger payload must be refused here, before it is rented, enqueued, fsync'd and
        // acknowledged: an acknowledged frame that recovery cannot read is a lost write.
        if (payloadLength > MaxPayloadSize)
        {
            throw new ArgumentOutOfRangeException(
                kind == EntryKind.Set && valueKind == ValueKind.Inline ? nameof(value) : nameof(key),
                payloadLength,
                $"WAL payload serialised to {payloadLength} bytes, above the {MaxPayloadSize}-byte frame " +
                "ceiling recovery accepts. Route large values through the value log instead.");
        }

        var frameLength = FrameOverheadBytes + payloadLength;
        var frame = ArrayPool<byte>.Shared.Rent(frameLength);

        var span = frame.AsSpan(0, frameLength);
        BinaryPrimitives.WriteInt32LittleEndian(span[..LengthPrefixSize], payloadLength);
        span[LengthPrefixSize] = (byte)kind;
        span[LengthPrefixSize + KindByteSize] = (byte)valueKind;
        BinaryPrimitives.WriteUInt64LittleEndian(
            span.Slice(LengthPrefixSize + KindByteSize + ValueKindByteSize, SeqnoBytesSize), sequenceNumber);

        // Header CRC = [length][entry_kind][value_kind][seqno]. It authenticates every field that
        // frames or dispatches the entry — the length prefix included — on its own, so recovery can
        // tell a frame whose header is intact (a torn payload at end of file) from a frame whose
        // header is damaged (corruption) without guessing where the next frame starts.
        BinaryPrimitives.WriteUInt32LittleEndian(
            span.Slice(HeaderFieldsSize, CrcBytesSize), Algorithms.Crc32C.HashToUInt32(span[..HeaderFieldsSize]));

        var payload = span.Slice(FrameHeaderSize, payloadLength);
        payloadSegment.AsSpan(0, payloadLength).CopyTo(payload);
        BinaryPrimitives.WriteUInt32LittleEndian(
            span.Slice(FrameHeaderSize + payloadLength, CrcBytesSize), Algorithms.Crc32C.HashToUInt32(payload));

        return (frame, frameLength);
    }

    private async Task FlusherLoopAsync()
    {
        var reader = _channel.Reader;
        var batch = new List<FlusherRequest>(_options.GroupCommitBatchSize);

        while (true)
        {
            bool hasItem;
            try
            {
                hasItem = await reader.WaitToReadAsync(_shutdownCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                hasItem = reader.TryPeek(out _);
            }

            if (!hasItem)
            {
                break;
            }

            batch.Clear();
            DrainImmediate(reader, batch);

            if (batch.Count > 0 && _pendingControl is null && batch.Count < _options.GroupCommitBatchSize)
            {
                await TryFillBatchAsync(reader, batch).ConfigureAwait(false);
            }

            if (batch.Count > 0)
            {
                ExecuteAppendBatch(batch);
            }

            if (_pendingControl is { } control)
            {
                _pendingControl = null;
                ExecuteControl(control);
            }
        }
    }

    private void DrainImmediate(ChannelReader<FlusherRequest> reader, List<FlusherRequest> batch)
    {
        while (batch.Count < _options.GroupCommitBatchSize && reader.TryRead(out var req))
        {
            if (req.Kind != RequestKind.Append)
            {
                _pendingControl = req;
                return;
            }
            batch.Add(req);
        }
    }

    private async Task TryFillBatchAsync(ChannelReader<FlusherRequest> reader, List<FlusherRequest> batch)
    {
        // Wait up to GroupCommitLatencyMs for additional appends, so partial batches still
        // benefit from coalescing under low concurrency.
        using var deadlineCts = new CancellationTokenSource(_options.GroupCommitLatencyMs);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token, deadlineCts.Token);

        // Once the batch holds every frame slot, no further append can arrive until this batch
        // completes — waiting out the latency window would only add delay.
        while (batch.Count < _options.GroupCommitBatchSize && batch.Count < _options.WriteQueueCapacity)
        {
            try
            {
                if (!await reader.WaitToReadAsync(linked.Token).ConfigureAwait(false))
                {
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }

            DrainImmediate(reader, batch);
            if (_pendingControl is not null)
            {
                return;
            }
        }
    }

    private void ExecuteAppendBatch(List<FlusherRequest> batch)
    {
        // Where this batch's first frame starts, once known; -1 while nothing has been written.
        var batchStart = -1L;
        var frameCountBefore = 0L;
        var digestBefore = 0U;
        try
        {
            if (_isCorrupt)
            {
                // An earlier batch failed after some of its bytes may have reached the segment.
                // Appends already queued when it failed passed ThrowIfCorrupt at enqueue; writing
                // one now would land it after those torn bytes and acknowledge it, sealing the tear
                // into the middle of the segment where recovery can only fail closed. Fail it
                // without writing, so the torn bytes stay a torn tail.
                var refused = CorruptStateException();
                for (var i = 0; i < batch.Count; i++)
                {
                    batch[i].CompletionTcs!.SetException(refused);
                }

                return;
            }

            EnsureSegmentReady();
            batchStart = _currentSegmentLength;
            frameCountBefore = _currentSegmentFrameCount;
            digestBefore = _currentSegmentCrcState;
            var batchMaxSeqno = 0UL;
            for (var i = 0; i < batch.Count; i++)
            {
                var req = batch[i];
                _appendStream!.Write(req.Frame!, 0, req.FrameLength);
                _currentSegmentLength += req.FrameLength;
                _currentSegmentFrameCount++;
                AppendToSealDigest(req.Frame!.AsSpan(0, req.FrameLength), ref _currentSegmentCrcState);
                if (req.SequenceNumber > batchMaxSeqno)
                {
                    batchMaxSeqno = req.SequenceNumber;
                }
            }
            // Record the segment's running max seqno BEFORE the fsync outcome is known:
            // over-recording can only cause TruncateCompletedSegmentsAsync to retain a
            // segment longer (safe); under-recording could delete frames that are the
            // only durable copy of an acknowledged write (data loss).
            if (batchMaxSeqno > 0
                && (!_segmentMaxSeqnos.TryGetValue(_currentSegmentIndex, out var knownMax) || batchMaxSeqno > knownMax))
            {
                _segmentMaxSeqnos[_currentSegmentIndex] = batchMaxSeqno;
            }
            _appendStream!.Flush(flushToDisk: true);

            for (var i = 0; i < batch.Count; i++)
            {
                batch[i].CompletionTcs!.SetResult();
            }
        }
        catch (WriteAheadLogCorruptException ex)
        {
            // The segment's own recovery classification (EnsureSegmentReady's tail repair) refused
            // it: surface that typed exception as-is, so it keeps the segment path and the offset
            // of the damaged or rejected frame instead of an unlocated group-commit failure.
            _isCorrupt = true;
            for (var i = 0; i < batch.Count; i++)
            {
                batch[i].CompletionTcs!.SetException(ex);
            }
        }
        catch (Exception ex)
        {
            _isCorrupt = true;
            if (batchStart >= 0)
            {
                // Every append in this batch is reported failed, so none of its bytes may survive
                // as history: a frame that landed whole — the write succeeded, the fsync failed —
                // would otherwise be replayed by the next recovery as an acknowledged write.
                RemoveFailedBatch(batchStart, frameCountBefore, digestBefore);
            }

            var corrupt = new WriteAheadLogCorruptException(
                $"WriteAheadLog group-commit fsync failed against segment '{CurrentSegmentName()}'. " +
                "WAL is now corrupt; subsequent appends will fast-fail.",
                GetCurrentSegmentPath(),
                // Byte offset unknown after a fsync failure — we know up to the last acked frame
                // but the exact file position is not inspected here.
                -1L,
                ex);
            for (var i = 0; i < batch.Count; i++)
            {
                batch[i].CompletionTcs!.SetException(corrupt);
            }
        }
        finally
        {
            for (var i = 0; i < batch.Count; i++)
            {
                ArrayPool<byte>.Shared.Return(batch[i].Frame!);
            }
            _appendFrameSlots.Release(batch.Count);
        }

        MaybeRotateAfterWrite();
    }

    /// <summary>
    /// Truncates the current segment back to <paramref name="batchStart"/> after a failed batch and
    /// fsyncs the truncation. The append stream is closed first so none of its buffered bytes can
    /// land after the truncation. If the repair fails too, the WAL stays corrupt (it already refuses
    /// every append) and the failed batch's bytes are whatever reached the disk.
    /// </summary>
    private void RemoveFailedBatch(long batchStart, long frameCountBefore, uint digestBefore)
    {
        try
        {
            CloseAppendStreamLocked();
        }
        catch (Exception closeFault) when (closeFault is not OutOfMemoryException)
        {
            // A buffered write the failed stream could not flush; the truncation below removes
            // whatever part of it reached the file.
            _appendStream = null;
        }

        try
        {
            using var repair = new FileStream(GetCurrentSegmentPath(), FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            if (repair.Length > batchStart)
            {
                repair.SetLength(batchStart);
            }

            repair.Flush(flushToDisk: true);
            _currentSegmentLength = batchStart;
            _currentSegmentFrameCount = frameCountBefore;
            _currentSegmentCrcState = digestBefore;
        }
        catch (Exception repairFault) when (repairFault is not OutOfMemoryException)
        {
            // Nothing more can be done here; the WAL is already marked corrupt.
        }
    }

    private void ExecuteControl(FlusherRequest control)
    {
        switch (control.Kind)
        {
            case RequestKind.Truncate:
                try
                {
                    CloseAppendStreamLocked();
                    // Forget the current segment BEFORE the first unlink: if a deletion or its
                    // directory fsync then fails, the next append still re-validates (or re-creates)
                    // the segment's header instead of appending into a file that may no longer exist.
                    ForgetCurrentSegmentState();
                    try
                    {
                        DeleteAllSegments();
                    }
                    finally
                    {
                        // 1 once every unlink landed. When a deletion or its directory fsync failed
                        // part-way, the highest surviving segment — or still 1 — so the next append
                        // never re-creates a later-numbered segment in place (only segment 1 is
                        // created in place; recovery refuses any other segment found without its header).
                        _currentSegmentIndex = DiscoverNextSegmentIndex();
                    }

                    _segmentMaxSeqnos.Clear();
                    control.CompletionTcs!.SetResult();
                }
                catch (Exception ex)
                {
                    control.CompletionTcs!.SetException(ex);
                }
                break;

            case RequestKind.TruncateCompleted:
                try
                {
                    ExecuteTruncateCompleted(control.SequenceNumber);
                    control.CompletionTcs!.SetResult();
                }
                catch (Exception ex)
                {
                    control.CompletionTcs!.SetException(ex);
                }
                break;

            case RequestKind.Recover:
                try
                {
                    CloseAppendStreamLocked();
                    var entries = ReadAllSegmentsAsync(CancellationToken.None).GetAwaiter().GetResult();
                    control.RecoverTcs!.SetResult(entries);
                }
                catch (Exception ex)
                {
                    control.RecoverTcs!.SetException(ex);
                }
                break;
        }
    }

    /// <summary>
    /// Deletes, oldest first, the run of segments whose highest appended sequence number is ≤
    /// <paramref name="upToSeqnoInclusive"/> — frames fully covered by a durable SSTable
    /// flush — stopping at the first segment that must be retained, so the survivors stay a
    /// contiguous run. Segments carrying any frame above the watermark are retained: they still back
    /// unflushed memtable state. Segments whose max seqno was never recorded (no appends
    /// observed and no recovery scan) are conservatively retained. When the current append
    /// segment itself is fully covered, it is closed and deleted too, and the counter moves
    /// to a fresh segment (resetting to 1 when no segments remain, mirroring TruncateAsync).
    /// </summary>
    private void ExecuteTruncateCompleted(ulong upToSeqnoInclusive)
    {
        var currentDeleted = false;
        try
        {
            foreach (var path in DiscoverSegmentFiles())
            {
                // Only a contiguous prefix is reclaimed, oldest first: recovery treats a missing segment
                // between two present ones as lost history, so the first segment that must be retained
                // ends the reclamation even when later segments are covered (seqnos need not grow with
                // the segment index).
                var index = ParseIndex(path);
                if (index < 0
                    || !_segmentMaxSeqnos.TryGetValue(index, out var maxSeqno)
                    || maxSeqno > upToSeqnoInclusive)
                {
                    break;
                }

                if (index == _currentSegmentIndex)
                {
                    CloseAppendStreamLocked();
                    // Before the unlink, not after it: a failed directory fsync throws out of this loop
                    // with the file already gone, and a still-resolved header would let the next append
                    // recreate the segment as a headerless file that recovery rejects.
                    ForgetCurrentSegmentState();
                    currentDeleted = true;
                }

                DeleteSegmentDurably(path);
                _segmentMaxSeqnos.Remove(index);
            }
        }
        finally
        {
            if (currentDeleted)
            {
                // Resume on the highest surviving segment (matching reopen semantics), or reset to 1
                // when the directory emptied — also when an unlink's directory fsync failed, so the
                // next append never re-creates a later-numbered segment in place (only segment 1 is
                // created in place; recovery refuses any other segment found without its header).
                _currentSegmentIndex = DiscoverNextSegmentIndex();
            }
        }
    }

    /// <summary>
    /// Drops everything the writer knows about the current segment — header resolution, length,
    /// frame count and seal digest — so the next append re-runs <see cref="EnsureSegmentReady"/>'s
    /// header check from the file actually on disk. Called with the append stream closed, before a
    /// reclamation unlinks the current segment.
    /// </summary>
    private void ForgetCurrentSegmentState()
    {
        _headerResolvedForCurrent = false;
        _currentSegmentLength = 0;
        _currentSegmentFrameCount = 0;
        _currentSegmentCrcState = Algorithms.Crc32C.InitialState;
    }

    private void MaybeRotateAfterWrite()
    {
        // A corrupt log writes nothing more: a failed batch may have left torn bytes that must stay
        // a torn tail, never be sealed into the middle of the segment.
        if (_isCorrupt || _appendStream is null || _currentSegmentLength < _options.SegmentMaxBytes)
        {
            return;
        }

        // Three durable steps, in this order: (1) create segment N+1 under its pending name — header
        // written, file and directory fsync'd; (2) seal segment N and fsync the seal; (3) rename the
        // pending file to segment N+1 and fsync the directory. Every crash state is then one recovery
        // resolves without guessing: before (2) lands, segment N is unsealed and still the last segment
        // (no seal or a torn seal is tear-repaired like any interrupted write) and the pending file is
        // discarded; after (2), N is sealed and its successor exists, pending or published, so the
        // pending file is published. A sealed segment with no successor at all can only be a deleted
        // last segment, which recovery and the append path refuse. Recovery also requires every
        // segment but the last to carry a seal that matches it — including the segment index in its
        // name — which is what makes a sealed segment cut short, emptied, extended, reordered or
        // swapped with another fail loud. Two extra fsyncs per rotation, none per append.
        var successorIndex = _currentSegmentIndex + 1;
        var pendingPath = PendingSuccessorPath(successorIndex);
        var successorPath = SegmentPath(successorIndex);
        try
        {
            using (var successor = new FileStream(pendingPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                WriteFileHeader(successor);
                successor.Flush(flushToDisk: true);
            }

            if (!OperatingSystem.IsWindows())
            {
                DurableFile.FsyncDirectory(pendingPath);
            }

            _faultInjector?.Invoke("rotation-seal");
            var seal = BuildSealFrame(
                _currentSegmentIndex, _currentSegmentLength, _currentSegmentFrameCount, _currentSegmentCrcState);
            _appendStream.Write(seal);
            _appendStream.Flush(flushToDisk: true);

            // The segment file stays on disk; recovery will continue to read it.
            CloseAppendStreamLocked();

            _faultInjector?.Invoke("rotation-publish");
            File.Move(pendingPath, successorPath);
            if (!OperatingSystem.IsWindows())
            {
                DurableFile.FsyncDirectory(successorPath);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Every frame in the segment is already acknowledged; only the rotation failed. Stop
            // writing (a partial seal is a torn tail recovery truncates; a pending successor is
            // resolved on the next open) and fail later appends fast.
            _isCorrupt = true;
            try
            {
                CloseAppendStreamLocked();
            }
            catch (IOException)
            {
                // The stream's buffered seal bytes could not be flushed either — already corrupt.
            }

            return;
        }

        // The next append goes into the published successor, whose header is already durable.
        _currentSegmentIndex = successorIndex;
        _currentSegmentLength = FileHeaderSize;
        _currentSegmentFrameCount = 0;
        _currentSegmentCrcState = SealDigestAfterFileHeader();
        _headerResolvedForCurrent = true;
    }

    /// <summary>
    /// Settles a rotation a crash interrupted. A pending successor whose predecessor is the last
    /// segment and is sealed was created before that seal and is published under its segment name;
    /// any other pending file (its predecessor unsealed, torn or absent, or its segment name already
    /// taken) belongs to a rotation whose seal never landed and holds nothing but a file header, so
    /// it is discarded. A pending file to be published must hold exactly a valid file header — it
    /// was fsync'd before the seal — and anything else throws <see cref="WriteAheadLogCorruptException"/>.
    /// Runs from the flusher loop only, before recovery and before the first append to a segment.
    /// </summary>
    private void ResolveInterruptedRotation()
    {
        if (!Directory.Exists(_directory))
        {
            return;
        }

        var pending = Directory.GetFiles(_directory, PendingSuccessorGlob);
        if (pending.Length == 0)
        {
            return;
        }

        foreach (var pendingPath in pending)
        {
            var index = ParseIndex(pendingPath);
            var segments = DiscoverSegmentFiles();
            var publish = index > 1
                && segments.Count > 0
                && ParseIndex(segments[^1]) == index - 1
                && ScanSegmentAsync(segments[^1], entries: null, CancellationToken.None).GetAwaiter().GetResult().Sealed;

            if (publish)
            {
                if (new FileInfo(pendingPath).Length != FileHeaderSize || IsTornSegmentCreation(pendingPath))
                {
                    throw new WriteAheadLogCorruptException(
                        $"WriteAheadLog pending segment '{Path.GetFileName(pendingPath)}' follows the sealed segment " +
                        $"'{Path.GetFileName(segments[^1])}' but is not a bare segment header. It was made durable " +
                        "before that seal was written, so this is damage; recovery stops.",
                        pendingPath,
                        0L);
                }

                using (var fs = new FileStream(pendingPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    ValidateHeader(fs, pendingPath);
                }

                var successorPath = SegmentPath(index);
                File.Move(pendingPath, successorPath);
                if (!OperatingSystem.IsWindows())
                {
                    DurableFile.FsyncDirectory(successorPath);
                }
            }
            else
            {
                File.Delete(pendingPath);
                if (!OperatingSystem.IsWindows())
                {
                    DurableFile.FsyncDirectory(pendingPath);
                }
            }
        }
    }

    /// <summary>
    /// The exception for a sealed segment with no successor: the writer makes a segment's successor
    /// durable before sealing it, so the successor existed and has been deleted.
    /// </summary>
    private WriteAheadLogCorruptException MissingSuccessor(string sealedSegmentPath)
    {
        var missing = SegmentPath(ParseIndex(sealedSegmentPath) + 1);
        return new WriteAheadLogCorruptException(
            $"WriteAheadLog segment '{Path.GetFileName(missing)}' is missing after the sealed segment " +
            $"'{Path.GetFileName(sealedSegmentPath)}'. A segment's successor is made durable before the segment " +
            "is sealed, so the last segment of the log was deleted, with whatever acknowledged frames it held; " +
            "recovery cannot tell how much history is gone and stops.",
            missing,
            0L);
    }

    private string SegmentPath(int index) => Path.Combine(_directory, string.Format(
        System.Globalization.CultureInfo.InvariantCulture, SegmentFilePattern, index));

    private string PendingSuccessorPath(int index) => Path.Combine(_directory, string.Format(
        System.Globalization.CultureInfo.InvariantCulture, PendingSuccessorPattern, index));

    /// <summary>
    /// Builds the <see cref="EntryKind.SegmentSeal"/> frame for segment <paramref name="segmentIndex"/>
    /// of <paramref name="sealedLength"/> bytes holding <paramref name="frameCount"/> data frames whose
    /// running seal digest is <paramref name="digestState"/>.
    /// </summary>
    private static byte[] BuildSealFrame(int segmentIndex, long sealedLength, long frameCount, uint digestState)
    {
        var frame = new byte[FrameOverheadBytes + SealPayloadSize];
        var span = frame.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(span[..LengthPrefixSize], SealPayloadSize);
        span[LengthPrefixSize] = (byte)EntryKind.SegmentSeal;
        span[LengthPrefixSize + KindByteSize] = (byte)ValueKind.Inline;
        BinaryPrimitives.WriteUInt64LittleEndian(
            span.Slice(LengthPrefixSize + KindByteSize + ValueKindByteSize, SeqnoBytesSize), 0UL);
        BinaryPrimitives.WriteUInt32LittleEndian(
            span.Slice(HeaderFieldsSize, CrcBytesSize), Algorithms.Crc32C.HashToUInt32(span[..HeaderFieldsSize]));

        var payload = span.Slice(FrameHeaderSize, SealPayloadSize);
        BinaryPrimitives.WriteUInt64LittleEndian(payload[..8], (ulong)segmentIndex);
        BinaryPrimitives.WriteUInt64LittleEndian(payload.Slice(8, 8), (ulong)sealedLength);
        BinaryPrimitives.WriteUInt64LittleEndian(payload.Slice(16, 8), (ulong)frameCount);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.Slice(24, CrcBytesSize), Algorithms.Crc32C.Finalize(digestState));
        BinaryPrimitives.WriteUInt32LittleEndian(
            span.Slice(FrameHeaderSize + SealPayloadSize, CrcBytesSize), Algorithms.Crc32C.HashToUInt32(payload));
        return frame;
    }

    /// <summary>The seal digest's state after the 8-byte file header — a new segment's starting state.</summary>
    private static uint SealDigestAfterFileHeader()
    {
        Span<byte> header = stackalloc byte[FileHeaderSize];
        s_magic.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], FormatVersion);
        var state = Algorithms.Crc32C.InitialState;
        Algorithms.Crc32C.Append(header, ref state);
        return state;
    }

    /// <summary>
    /// Advances the seal digest by one data frame: its stored header checksum, then its stored
    /// payload checksum (<paramref name="frame"/> is the whole encoded frame).
    /// </summary>
    private static void AppendToSealDigest(ReadOnlySpan<byte> frame, ref uint state)
    {
        Algorithms.Crc32C.Append(frame.Slice(HeaderFieldsSize, CrcBytesSize), ref state);
        Algorithms.Crc32C.Append(frame[^CrcBytesSize..], ref state);
    }

    /// <summary>
    /// Ensures the active segment file is open and has a valid header. Either writes the
    /// header (new / empty segment) or validates an existing one. Must be called from the
    /// flusher loop — group commit serialises every state mutation through the single-reader
    /// channel.
    /// </summary>
    private void EnsureSegmentReady()
    {
        if (_appendStream is not null)
        {
            return;
        }

        if (!_headerResolvedForCurrent)
        {
            // First open of this segment by this instance: settle a rotation a crash interrupted
            // before deciding which segment the append goes into.
            ResolveInterruptedRotation();
        }

        var segmentPath = GetCurrentSegmentPath();

        while (!_headerResolvedForCurrent)
        {
            if (ValidateOrInitHeader(segmentPath))
            {
                _currentSegmentFrameCount = 0;
                _currentSegmentCrcState = SealDigestAfterFileHeader();
                break;
            }

            // First open of a PRE-EXISTING segment for append: repair any torn tail before the
            // first frame lands. A standalone consumer that appends without calling RecoverAsync
            // first would otherwise write after a crash's garbage bytes; the next RecoverAsync —
            // which stops at the first unreadable frame — then silently drops the freshly
            // acknowledged append. Recovery already repairs the tail when it runs, so when
            // RecoverAsync DID run the scan finds a clean end and this is a no-op. The scan runs
            // only on this first open of an existing segment, never on subsequent appends, so the
            // common path is untouched. The same scan yields what the segment's seal will record.
            var scan = RepairTornTail(segmentPath);
            if (scan.Sealed)
            {
                // Nothing may be written after a seal, so appends go into the successor — which the
                // writer made durable before the seal, so it must exist (ResolveInterruptedRotation
                // has published a pending one). A sealed segment without it is a deleted last
                // segment: refuse, rather than create a fresh segment over the hole.
                _headerResolvedForCurrent = false;
                if (!File.Exists(SegmentPath(_currentSegmentIndex + 1)))
                {
                    throw MissingSuccessor(segmentPath);
                }

                _currentSegmentIndex++;
                segmentPath = GetCurrentSegmentPath();
                continue;
            }

            _currentSegmentFrameCount = scan.FrameCount;
            _currentSegmentCrcState = scan.DigestState;
        }

        _appendStream = new FileStream(
            segmentPath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite,
            bufferSize: 4096,
            useAsync: true);

        // Resync the running length to the actual on-disk size so a re-opened segment after
        // process restart picks up rotation accounting where it left off.
        _currentSegmentLength = _appendStream.Length;
    }

    /// <summary>
    /// Writes a fresh header into a new, empty or torn-created segment (returning
    /// <see langword="true"/>), or validates the header of an existing one (returning
    /// <see langword="false"/>). Either way the segment's header is resolved afterwards.
    /// </summary>
    private bool ValidateOrInitHeader(string segmentPath)
    {
        // The active segment is always the highest-numbered one (or a new one), so on segment 1 a
        // torn segment creation here — a header prefix and/or zeros with no frame — is re-initialised
        // exactly as an empty file is; it holds nothing acknowledged. Any later segment was published
        // by rotation with a durable header, so the same shape there is damage and is refused.
        var exists = File.Exists(segmentPath);
        if (!exists || HoldsNoSegmentHeader(segmentPath))
        {
            if (exists)
            {
                ThrowIfPublishedByRotation(segmentPath);
            }

            using (var fs = new FileStream(
                segmentPath,
                FileMode.OpenOrCreate,
                FileAccess.Write,
                FileShare.None))
            {
                fs.SetLength(0);
                WriteFileHeader(fs);
                fs.Flush(flushToDisk: true);
            }

            // Fsync the containing directory so the newly-created segment's directory entry is
            // durable. The frame bytes are fsync'd (Flush(flushToDisk: true)), but without the
            // directory fsync a crash after the header flush yet before the directory write lands
            // can lose the whole new segment on POSIX — dropping writes the engine already
            // acknowledged into that segment after a rotation or first append.
            if (!OperatingSystem.IsWindows())
            {
                DurableFile.FsyncDirectory(segmentPath);
            }

            _headerResolvedForCurrent = true;
            return true;
        }

        using (var fs = new FileStream(
            segmentPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite))
        {
            ValidateHeader(fs, segmentPath);
        }
        _headerResolvedForCurrent = true;
        return false;
    }

    /// <summary>
    /// Whether <paramref name="segmentPath"/> holds no segment header at all: empty, or a torn
    /// segment creation (<see cref="IsTornSegmentCreation"/>).
    /// </summary>
    private static bool HoldsNoSegmentHeader(string segmentPath) =>
        new FileInfo(segmentPath).Length == 0 || IsTornSegmentCreation(segmentPath);

    /// <summary>
    /// Throws when <paramref name="segmentPath"/>, found holding no segment header, is numbered above 1.
    /// Only segment 1 is ever created in place (a fresh or emptied log starts there); every later
    /// segment is published by rotation after its header was written and fsync'd under a pending name,
    /// so an empty, header-cut or zero-filled one lost bytes that were durable — and with them whatever
    /// acknowledged frames it held — exactly like a deleted one.
    /// </summary>
    private static void ThrowIfPublishedByRotation(string segmentPath)
    {
        if (ParseIndex(segmentPath) <= 1)
        {
            return;
        }

        throw new WriteAheadLogCorruptException(
            $"WriteAheadLog segment '{Path.GetFileName(segmentPath)}' is empty or holds less than its file header " +
            $"({new FileInfo(segmentPath).Length} bytes). Rotation publishes every segment after the first only " +
            "once its header is durable, so this is damage that lost the segment's acknowledged frames, not a " +
            "crash inside segment creation; recovery cannot tell how much history is gone and stops.",
            segmentPath,
            0L);
    }

    /// <summary>
    /// Whether <paramref name="segmentPath"/> is a torn segment creation: non-empty, no longer
    /// than the file header, and holding a strict prefix of the expected header followed only by
    /// zero bytes (all zeros included) — what a crash leaves between creating the file and the
    /// header fsync landing. A complete valid header, or any other byte, is not torn creation.
    /// </summary>
    private static bool IsTornSegmentCreation(string segmentPath)
    {
        Span<byte> actual = stackalloc byte[FileHeaderSize];
        int length;
        using (var handle = File.OpenHandle(segmentPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            var fileLength = RandomAccess.GetLength(handle);
            if (fileLength == 0 || fileLength > FileHeaderSize)
            {
                return false;
            }

            length = (int)fileLength;
            if (ReadFully(handle, actual[..length], 0) != length)
            {
                return false;
            }
        }

        Span<byte> expected = stackalloc byte[FileHeaderSize];
        s_magic.CopyTo(expected);
        BinaryPrimitives.WriteUInt32LittleEndian(expected[4..], FormatVersion);
        if (length == FileHeaderSize && actual.SequenceEqual(expected))
        {
            return false;
        }

        var matched = 0;
        while (matched < length && actual[matched] == expected[matched])
        {
            matched++;
        }

        return actual[matched..length].IndexOfAnyExcept((byte)0) < 0;
    }

    private static void WriteFileHeader(FileStream stream)
    {
        Span<byte> header = stackalloc byte[FileHeaderSize];
        s_magic.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], FormatVersion);
        stream.Write(header);
    }

    private static void ValidateHeader(Stream fs, string segmentPath)
    {
        if (fs.Length < FileHeaderSize)
        {
            throw new WriteAheadLogCorruptException(
                $"WriteAheadLog segment '{Path.GetFileName(segmentPath)}' is too short " +
                $"({fs.Length} bytes) to carry a valid header (expected at least {FileHeaderSize} bytes).",
                segmentPath,
                0L);
        }

        Span<byte> header = stackalloc byte[FileHeaderSize];
        var initialPosition = fs.Position;
        fs.Seek(0, SeekOrigin.Begin);
        var read = 0;
        while (read < FileHeaderSize)
        {
            var n = fs.Read(header[read..]);
            if (n == 0)
            {
                break;
            }
            read += n;
        }
        fs.Seek(initialPosition, SeekOrigin.Begin);

        if (read < FileHeaderSize ||
            header[0] != s_magic[0] || header[1] != s_magic[1] ||
            header[2] != s_magic[2] || header[3] != s_magic[3])
        {
            throw new WriteAheadLogCorruptException(
                $"WriteAheadLog segment '{Path.GetFileName(segmentPath)}' is missing the 'BSWL' magic " +
                "at offset 0. v2.0.0 recognises a single WAL format; no fallback reader exists.",
                segmentPath,
                0L);
        }

        var version = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        if (version != FormatVersion)
        {
            throw new WriteAheadLogCorruptException(
                $"WriteAheadLog segment '{Path.GetFileName(segmentPath)}' carries unknown format version " +
                $"{version}; supported: {FormatVersion}.",
                segmentPath,
                0L);
        }
    }

    private async Task<IReadOnlyList<WalEntry<TKey, TValue>>> ReadAllSegmentsAsync(CancellationToken cancellationToken)
    {
        var entries = new List<WalEntry<TKey, TValue>>();
        ResolveInterruptedRotation();
        var segments = DiscoverSegmentFiles();
        ThrowIfSegmentMissing(segments);
        foreach (var segmentPath in segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ReferenceEquals(segmentPath, segments[^1]) && HoldsNoSegmentHeader(segmentPath))
            {
                // A crash inside segment creation (create, write the 8-byte header, fsync) leaves
                // a frameless last segment that is empty or holds a header prefix and/or zeros. Only
                // segment 1 is ever created in place; rotation publishes segment N+1 only after its
                // header is durable, so the same shape there is damage that lost acknowledged frames.
                ThrowIfPublishedByRotation(segmentPath);

                // No frame was ever written to it, so nothing acknowledged is lost: discard the torn
                // header so the next append re-initialises the segment.
                using var tornStream = new FileStream(
                    segmentPath, FileMode.Open, FileAccess.Write, FileShare.Read);
                tornStream.SetLength(0);
                tornStream.Flush(flushToDisk: true);
                continue;
            }

            var firstNewEntry = entries.Count;
            var (lastGoodOffset, tornTail, isSealed, _, _) =
                await ScanSegmentAsync(segmentPath, entries, cancellationToken).ConfigureAwait(false);

            if (isSealed && ReferenceEquals(segmentPath, segments[^1]))
            {
                // A sealed segment's successor was made durable before the seal: with no later
                // segment (and no pending one, which ResolveInterruptedRotation published), the last
                // segment of the log was deleted.
                throw MissingSuccessor(segmentPath);
            }

            if (!tornTail && !isSealed && !ReferenceEquals(segmentPath, segments[^1]))
            {
                // Every segment but the last was sealed when the writer rotated past it, and is never
                // written again. One without its seal — emptied, cut to its header, or cut at a frame
                // boundary — lost acknowledged frames without leaving a damaged one behind; replaying
                // the later segments over that hole would reconstruct a state that never existed.
                throw new WriteAheadLogCorruptException(
                    $"WriteAheadLog segment '{Path.GetFileName(segmentPath)}' is followed by later segments but " +
                    $"ends at byte offset {lastGoodOffset} without the seal written when the log rotated past it. " +
                    "Its tail was lost; recovery cannot produce a consistent prefix and stops.",
                    segmentPath,
                    lastGoodOffset);
            }

            if (tornTail)
            {
                if (!ReferenceEquals(segmentPath, segments[^1]))
                {
                    // A torn tail in a NON-last segment is mid-history damage, not an interrupted
                    // append: writes demonstrably continued into later segments. Replaying those
                    // later segments after a hole would reconstruct a state that never existed at
                    // any point in time — fail loudly instead.
                    throw new WriteAheadLogCorruptException(
                        $"WriteAheadLog segment '{Path.GetFileName(segmentPath)}' has an unreadable frame at " +
                        $"byte offset {lastGoodOffset} but is followed by later segments. This is mid-history " +
                        "corruption, not tail damage; recovery cannot produce a consistent prefix and stops.",
                        segmentPath,
                        lastGoodOffset);
                }

                // Repair the torn tail on the LAST segment by truncating at the end of its last
                // whole frame. Without the repair, post-recovery appends land after the torn bytes
                // at physical EOF, and the NEXT recovery — which stops at the tear — silently drops
                // every acknowledged entry written after this point. Mirrors
                // ValueLogWriter.InitialiseAsync.
                using var repairStream = new FileStream(
                    segmentPath, FileMode.Open, FileAccess.Write, FileShare.Read);
                repairStream.SetLength(lastGoodOffset);
                repairStream.Flush(flushToDisk: true);
            }

            // Record each scanned segment's max seqno so TruncateCompletedSegmentsAsync can
            // reclaim segments that pre-date this process (the append-side tracking only
            // covers frames written by this instance).
            var segmentMax = 0UL;
            for (var i = firstNewEntry; i < entries.Count; i++)
            {
                if (entries[i].SequenceNumber > segmentMax)
                {
                    segmentMax = entries[i].SequenceNumber;
                }
            }
            var index = ParseIndex(segmentPath);
            if (segmentMax > 0 && index >= 0
                && (!_segmentMaxSeqnos.TryGetValue(index, out var knownMax) || segmentMax > knownMax))
            {
                _segmentMaxSeqnos[index] = segmentMax;
            }
        }
        return entries;
    }

    private static async Task<int> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(
                buffer.AsMemory(totalRead, buffer.Length - totalRead),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            totalRead += read;
        }
        return totalRead;
    }

    private static async Task ReadAndValidateHeaderAsync(FileStream fs, string segmentPath, CancellationToken ct)
    {
        if (fs.Length < FileHeaderSize)
        {
            throw new WriteAheadLogCorruptException(
                $"WriteAheadLog segment '{Path.GetFileName(segmentPath)}' is too short ({fs.Length} bytes) " +
                $"to carry a valid header (expected at least {FileHeaderSize} bytes).",
                segmentPath,
                0L);
        }

        var header = new byte[FileHeaderSize];
        fs.Seek(0, SeekOrigin.Begin);
        var read = await ReadExactAsync(fs, header, ct).ConfigureAwait(false);
        if (read < FileHeaderSize ||
            header[0] != s_magic[0] || header[1] != s_magic[1] ||
            header[2] != s_magic[2] || header[3] != s_magic[3])
        {
            throw new WriteAheadLogCorruptException(
                $"WriteAheadLog segment '{Path.GetFileName(segmentPath)}' is missing the 'BSWL' magic at " +
                "offset 0. v2.0.0 recognises a single WAL format; no fallback reader exists.",
                segmentPath,
                0L);
        }

        var version = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));
        if (version != FormatVersion)
        {
            throw new WriteAheadLogCorruptException(
                $"WriteAheadLog segment '{Path.GetFileName(segmentPath)}' carries unknown format version " +
                $"{version}; supported: {FormatVersion}.",
                segmentPath,
                0L);
        }
    }

    /// <summary>
    /// What reading the frame at the current position found. <see cref="Entry"/> is a frame both
    /// checksums accept and this reader decodes; <see cref="Seal"/> is a checksum-valid
    /// <see cref="EntryKind.SegmentSeal"/> frame; <see cref="TornTail"/> is the interrupted final
    /// append the tear rule truncates; <see cref="Corrupt"/> and <see cref="Rejected"/> fail loud.
    /// </summary>
    private enum FrameOutcome
    {
        Entry,
        Seal,
        TornTail,
        Corrupt,
        Rejected,
    }

    private readonly struct RawFrame(
        FrameOutcome outcome,
        TKey? key = default,
        TValue? value = default,
        Boutquin.Storage.Domain.ValueObjects.ValueLog.ValueLogPointer? pointer = null,
        EntryKind kind = EntryKind.Set,
        ValueKind valueKind = ValueKind.Inline,
        ulong sequenceNumber = 0,
        string? reason = null,
        Exception? cause = null,
        uint headerChecksum = 0,
        uint payloadChecksum = 0,
        byte[]? sealPayload = null)
    {
        public readonly FrameOutcome Outcome = outcome;
        public readonly TKey? Key = key;
        public readonly TValue? Value = value;
        public readonly Boutquin.Storage.Domain.ValueObjects.ValueLog.ValueLogPointer? Pointer = pointer;
        public readonly EntryKind Kind = kind;
        public readonly ValueKind ValueKind = valueKind;
        public readonly ulong SequenceNumber = sequenceNumber;

        // Why a Corrupt or Rejected frame fails loud, and the decoder's exception for a frame
        // whose key or value would not decode.
        public readonly string? Reason = reason;
        public readonly Exception? Cause = cause;

        // An Entry's stored checksums (the seal digest chains them); a Seal's 28-byte payload.
        public readonly uint HeaderChecksum = headerChecksum;
        public readonly uint PayloadChecksum = payloadChecksum;
        public readonly byte[]? SealPayload = sealPayload;
    }

    /// <summary>
    /// The outcome of scanning one segment: the offset just past its last whole frame, whether a
    /// torn tail follows it, whether it ends in a verified seal, and — for an unsealed segment — the
    /// data-frame count and seal-digest state a seal written now would record.
    /// </summary>
    private readonly record struct SegmentScan(
        long LastGoodOffset, bool TornTail, bool Sealed, long FrameCount, uint DigestState);

    private static WriteAheadLogCorruptException RejectedFrame(string segmentPath, long frameOffset, RawFrame frame)
    {
        var message =
            $"WriteAheadLog segment '{Path.GetFileName(segmentPath)}' has a complete frame at byte offset {frameOffset} " +
            $"whose checksums match but which this reader rejects ({frame.Reason}). A checksum-valid frame is " +
            "acknowledged history, not a torn append; recovery refuses to truncate it and stops.";
        return frame.Cause is null
            ? new WriteAheadLogCorruptException(message, segmentPath, frameOffset)
            : new WriteAheadLogCorruptException(message, segmentPath, frameOffset, frame.Cause);
    }

    private static WriteAheadLogCorruptException CorruptFrame(string segmentPath, long frameOffset, RawFrame frame) => new(
        $"WriteAheadLog segment '{Path.GetFileName(segmentPath)}' has a damaged frame at byte offset {frameOffset} " +
        $"({frame.Reason}). Only a single interrupted write is a torn tail — a header prefix, or a frame whose " +
        "checksum trailer never fully landed, followed by nothing but zeros to end of file, or a frame cut short " +
        "at end of file; this is corruption of acknowledged history, so recovery refuses to truncate it and stops.",
        segmentPath,
        frameOffset);

    /// <summary>
    /// Reads the frame at <paramref name="fileStream"/>'s position and applies the tear rule. The
    /// header checksum authenticates the length prefix, so every classification rests on bytes the
    /// writer authenticated, never on a guess about where the next frame starts:
    /// <list type="bullet">
    /// <item>fewer than a header's bytes left — a partial header at end of file — is a torn tail;</item>
    /// <item>a header whose checksum fails is a torn tail when it is a strict prefix of a header the
    /// writer emits followed by zeros and every byte after it to end of file is zero (the landed data
    /// of the interrupted write stopped inside the header; an all-zero run from the frame start is the
    /// empty prefix), and corruption otherwise;</item>
    /// <item>a valid header whose payload and payload checksum run past end of file is a torn tail;</item>
    /// <item>a payload checksum failure is a torn tail when the frame's checksum trailer is an unwritten
    /// suffix (its last byte zero, the landed trailer bytes equal to the checksum of the landed payload)
    /// and every byte after the frame to end of file is zero (it is the last frame written — a group
    /// commit writes a whole batch before its single fsync, so the rest of an interrupted batch reads
    /// as zeros); it is corruption when the frame's last byte landed, when a landed trailer byte
    /// disagrees with the payload, or when any non-zero byte follows it;</item>
    /// <item>a frame both checksums accept but this reader refuses (length outside the accepted range,
    /// unknown kind or value-kind byte, a key or value that fails to decode) is rejected.</item>
    /// </list>
    /// The position is left just past the frame only for <see cref="FrameOutcome.Entry"/>.
    /// </summary>
    private static async Task<RawFrame> ReadFrameAsync(FileStream fileStream, CancellationToken cancellationToken)
    {
        var frameStart = fileStream.Position;
        var fileLength = fileStream.Length;
        if (fileLength - frameStart < FrameHeaderSize)
        {
            return new RawFrame(FrameOutcome.TornTail);
        }

        var header = new byte[FrameHeaderSize];
        if (await ReadExactAsync(fileStream, header, cancellationToken).ConfigureAwait(false) < FrameHeaderSize)
        {
            return new RawFrame(FrameOutcome.TornTail);
        }

        var storedHeaderCrc = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(HeaderFieldsSize, CrcBytesSize));
        if (storedHeaderCrc != Algorithms.Crc32C.HashToUInt32(header.AsSpan(0, HeaderFieldsSize)))
        {
            return IsHeaderPrefix(header)
                && await IsZeroToEndAsync(fileStream, frameStart + FrameHeaderSize, cancellationToken).ConfigureAwait(false)
                ? new RawFrame(FrameOutcome.TornTail)
                : new RawFrame(FrameOutcome.Corrupt, reason: "frame header checksum mismatch");
        }

        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(header);
        var kindByte = header[LengthPrefixSize];
        var valueKindByte = header[LengthPrefixSize + KindByteSize];
        var seqno = BinaryPrimitives.ReadUInt64LittleEndian(
            header.AsSpan(LengthPrefixSize + KindByteSize + ValueKindByteSize, SeqnoBytesSize));
        if (payloadLength <= 0 || payloadLength > MaxPayloadSize)
        {
            // The writer never emits such a length, and the header checksum says this one is what it
            // wrote: the frame's extent is unknowable, so it cannot be treated as a torn tail.
            return new RawFrame(
                FrameOutcome.Rejected, sequenceNumber: seqno,
                reason: $"payload length {payloadLength} outside 1..{MaxPayloadSize}");
        }

        var frameEnd = frameStart + FrameOverheadBytes + payloadLength;
        if (frameEnd > fileLength)
        {
            // A valid header whose payload is short at end of file: the interrupted final append.
            return new RawFrame(FrameOutcome.TornTail);
        }

        var payload = new byte[payloadLength];
        var checksumBytes = new byte[CrcBytesSize];
        if (await ReadExactAsync(fileStream, payload, cancellationToken).ConfigureAwait(false) < payloadLength
            || await ReadExactAsync(fileStream, checksumBytes, cancellationToken).ConfigureAwait(false) < CrcBytesSize)
        {
            return new RawFrame(FrameOutcome.TornTail);
        }

        var storedPayloadCrc = BinaryPrimitives.ReadUInt32LittleEndian(checksumBytes);
        var computedPayloadCrc = Algorithms.Crc32C.HashToUInt32(payload);
        if (storedPayloadCrc != computedPayloadCrc)
        {
            // The interrupted write: a group commit writes a whole batch before its one fsync, so the
            // landed data can stop inside this frame, leaving its checksum trailer an unwritten suffix
            // and the rest of the batch zero. A frame whose last byte landed was written completely
            // and acknowledged, so its checksum failure is damage, never a tear.
            if (!FinalFrameTearRule.IsUnwrittenChecksumTrailer(checksumBytes, computedPayloadCrc))
            {
                return new RawFrame(
                    FrameOutcome.Corrupt,
                    reason: "payload checksum mismatch on a frame whose last byte landed or whose landed checksum bytes disagree with its payload");
            }

            return await IsZeroToEndAsync(fileStream, frameEnd, cancellationToken).ConfigureAwait(false)
                ? new RawFrame(FrameOutcome.TornTail)
                : new RawFrame(FrameOutcome.Corrupt, reason: "payload checksum mismatch with non-zero bytes after it");
        }

        // Both checksums match — interpret the framing fields. An unknown kind / value-kind value is
        // refused as a rejected frame rather than damage: a disk failure that flipped a kind byte
        // WOULD be caught by the header checksum, so reaching this point with an unknown value means
        // the writer wrote it intentionally (version mismatch).
        if (kindByte == (byte)EntryKind.SegmentSeal)
        {
            return valueKindByte == (byte)ValueKind.Inline && payloadLength == SealPayloadSize
                ? new RawFrame(FrameOutcome.Seal, kind: EntryKind.SegmentSeal, sealPayload: payload)
                : new RawFrame(
                    FrameOutcome.Rejected, kind: EntryKind.SegmentSeal,
                    reason: $"segment seal with value kind 0x{valueKindByte:X2} and a {payloadLength}-byte payload " +
                            $"(expected 0x00 and {SealPayloadSize} bytes)");
        }

        if (kindByte != (byte)EntryKind.Set && kindByte != (byte)EntryKind.Delete)
        {
            return new RawFrame(FrameOutcome.Rejected, reason: $"unknown entry kind 0x{kindByte:X2}");
        }
        var kind = (EntryKind)kindByte;

        if (valueKindByte != (byte)ValueKind.Inline && valueKindByte != (byte)ValueKind.ValueLogPointer)
        {
            return new RawFrame(FrameOutcome.Rejected, kind: kind, reason: $"unknown value kind 0x{valueKindByte:X2}");
        }
        var valueKind = (ValueKind)valueKindByte;

        try
        {
            using var entryStream = new MemoryStream(payload, writable: false);
            var key = TKey.Deserialize(entryStream);
            TValue value;
            Boutquin.Storage.Domain.ValueObjects.ValueLog.ValueLogPointer? pointer = null;
            if (kind == EntryKind.Delete)
            {
                value = new TValue();
            }
            else if (valueKind == ValueKind.Inline)
            {
                value = TValue.Deserialize(entryStream);
            }
            else
            {
                // ValueKind.ValueLogPointer — read 12 bytes directly.
                value = default!;
                Span<byte> pointerBytes = stackalloc byte[Boutquin.Storage.Domain.ValueObjects.ValueLog.ValueLogPointer.SerializedSize];
                var read = 0;
                while (read < pointerBytes.Length)
                {
                    var n = entryStream.Read(pointerBytes[read..]);
                    if (n == 0)
                    {
                        break;
                    }
                    read += n;
                }
                if (read < pointerBytes.Length)
                {
                    return new RawFrame(
                        FrameOutcome.Rejected, kind: kind, valueKind: valueKind, sequenceNumber: seqno,
                        reason: $"value-log pointer payload shorter than {pointerBytes.Length} bytes");
                }
                pointer = Boutquin.Storage.Domain.ValueObjects.ValueLog.ValueLogPointer.ReadFrom(pointerBytes);
            }
            if (entryStream.Position != entryStream.Length)
            {
                return new RawFrame(
                    FrameOutcome.Rejected, kind: kind, valueKind: valueKind, sequenceNumber: seqno,
                    reason: "payload contains bytes beyond the decoded entry");
            }

            return new RawFrame(
                FrameOutcome.Entry, key, value, pointer, kind, valueKind, seqno,
                headerChecksum: storedHeaderCrc, payloadChecksum: storedPayloadCrc);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            // Any decode fault on checksum-valid bytes is a reader/writer disagreement over bytes the
            // writer authenticated — whatever exception type the key or value contract chose to raise
            // (InvalidDataException, UnsupportedFormatVersionException, NotSupportedException, …).
            return new RawFrame(
                FrameOutcome.Rejected, kind: kind, valueKind: valueKind, sequenceNumber: seqno,
                reason: $"key or value failed to decode: {ex.GetType().Name}", cause: ex);
        }
    }

    /// <summary>
    /// Whether a frame header that failed its checksum is a strict prefix of a header the writer
    /// emits followed by zeros — what an interrupted write leaves when its landed bytes stop inside
    /// the header. The header's last byte must be zero (a header whose every byte landed would pass
    /// its checksum, so a complete header that fails is rot, not a tear), and every field the landed
    /// bytes cover must hold a value the writer writes: a length in 1..<see cref="MaxPayloadSize"/>
    /// once all four length bytes are covered, and known kind and value-kind bytes (zero, the value an
    /// unlanded byte reads as, is a known value of both).
    /// </summary>
    private static bool IsHeaderPrefix(byte[] header)
    {
        var landed = header.AsSpan(0, FrameHeaderSize).LastIndexOfAnyExcept((byte)0) + 1;
        if (landed == FrameHeaderSize)
        {
            return false;
        }

        if (landed >= LengthPrefixSize)
        {
            var length = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (length <= 0 || length > MaxPayloadSize)
            {
                return false;
            }
        }

        return IsKnownEntryKind(header[LengthPrefixSize])
            && header[LengthPrefixSize + KindByteSize] is (byte)ValueKind.Inline or (byte)ValueKind.ValueLogPointer;
    }

    private static bool IsKnownEntryKind(byte kindByte) =>
        kindByte is (byte)EntryKind.Set or (byte)EntryKind.Delete or (byte)EntryKind.SegmentSeal;

    /// <summary>
    /// Whether every byte from <paramref name="start"/> to end of file is zero (vacuously true at end
    /// of file) — the unwritten extent a crash leaves when a file's length reached disk but its data
    /// did not. No frame encodes as zeros (an all-zero header fails its checksum), so such a run holds
    /// no frame: whatever precedes it is the last thing written.
    /// </summary>
    private static async Task<bool> IsZeroToEndAsync(FileStream fileStream, long start, CancellationToken cancellationToken)
    {
        fileStream.Seek(start, SeekOrigin.Begin);
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await fileStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return true;
            }

            if (buffer.AsSpan(0, read).ContainsAnyExcept((byte)0))
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Truncates <paramref name="segmentPath"/> at the end of its last whole frame when it ends in a
    /// torn tail. Uses the same scan (<see cref="ScanSegmentAsync"/>) recovery uses, so the repaired
    /// boundary always matches where recovery would stop; a damaged or rejected frame throws instead
    /// and leaves the segment untouched. A no-op when the tail is already clean. Returns the scan, which
    /// says whether the segment is sealed and what a seal written now would record. Runs from the
    /// flusher loop only, before the first append to a pre-existing (always the highest) segment.
    /// </summary>
    private static SegmentScan RepairTornTail(string segmentPath)
    {
        var scan = ScanSegmentAsync(segmentPath, entries: null, CancellationToken.None).GetAwaiter().GetResult();
        if (!scan.TornTail)
        {
            return scan;
        }

        using var repairStream = new FileStream(
            segmentPath, FileMode.Open, FileAccess.Write, FileShare.Read);
        repairStream.SetLength(scan.LastGoodOffset);
        repairStream.Flush(flushToDisk: true);
        return scan;
    }

    private static int ReadFully(Microsoft.Win32.SafeHandles.SafeFileHandle handle, Span<byte> buffer, long offset)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = RandomAccess.Read(handle, buffer[total..], offset + total);
            if (n == 0)
            {
                break;
            }
            total += n;
        }
        return total;
    }

    /// <summary>
    /// Scans one segment frame by frame under the tear rule (<see cref="ReadFrameAsync"/>),
    /// appending each decoded entry to <paramref name="entries"/> when one is supplied. Returns the
    /// byte offset just past the last whole frame, whether a torn tail follows it, and whether the
    /// segment ends in a seal that matches it. A damaged or rejected frame, or a seal that does not
    /// match the segment (another length, frame count or digest, or any byte after it), throws
    /// <see cref="WriteAheadLogCorruptException"/>. The single source of truth for "where does the
    /// clean log end", shared by recovery and the append-side repair. An empty file reports a clean,
    /// unsealed zero-length end.
    /// </summary>
    private static async Task<SegmentScan> ScanSegmentAsync(
        string segmentPath,
        List<WalEntry<TKey, TValue>>? entries,
        CancellationToken cancellationToken)
    {
        var fileStream = new FileStream(
            segmentPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            useAsync: true);
        await using (fileStream.ConfigureAwait(false))
        {
            if (fileStream.Length == 0)
            {
                return new SegmentScan(0, TornTail: false, Sealed: false, 0, Algorithms.Crc32C.InitialState);
            }

            await ReadAndValidateHeaderAsync(fileStream, segmentPath, cancellationToken).ConfigureAwait(false);
            var lastGoodOffset = fileStream.Position;
            var frameCount = 0L;
            var digest = SealDigestAfterFileHeader();
            var checksums = new byte[2 * CrcBytesSize];

            while (lastGoodOffset < fileStream.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();

                fileStream.Seek(lastGoodOffset, SeekOrigin.Begin);
                var frame = await ReadFrameAsync(fileStream, cancellationToken).ConfigureAwait(false);
                switch (frame.Outcome)
                {
                    case FrameOutcome.TornTail:
                        System.Diagnostics.Trace.TraceWarning(
                            $"Boutquin.Storage: WAL segment '{Path.GetFileName(segmentPath)}' — torn tail at byte " +
                            $"offset {lastGoodOffset}; recovery truncates the segment there.");
                        return new SegmentScan(lastGoodOffset, TornTail: true, Sealed: false, frameCount, digest);
                    case FrameOutcome.Corrupt:
                        throw CorruptFrame(segmentPath, lastGoodOffset, frame);
                    case FrameOutcome.Rejected:
                        throw RejectedFrame(segmentPath, lastGoodOffset, frame);
                    case FrameOutcome.Seal:
                        VerifySeal(segmentPath, lastGoodOffset, frame.SealPayload!, frameCount, digest, fileStream);
                        return new SegmentScan(fileStream.Position, TornTail: false, Sealed: true, frameCount, digest);
                }

                entries?.Add(new WalEntry<TKey, TValue>(
                    frame.Key!,
                    frame.SequenceNumber,
                    frame.Kind,
                    frame.ValueKind,
                    frame.Kind == EntryKind.Set && frame.ValueKind == ValueKind.Inline ? frame.Value : default,
                    frame.Pointer));
                frameCount++;
                BinaryPrimitives.WriteUInt32LittleEndian(checksums.AsSpan(0, CrcBytesSize), frame.HeaderChecksum);
                BinaryPrimitives.WriteUInt32LittleEndian(checksums.AsSpan(CrcBytesSize, CrcBytesSize), frame.PayloadChecksum);
                Algorithms.Crc32C.Append(checksums, ref digest);
                lastGoodOffset = fileStream.Position;
            }

            return new SegmentScan(lastGoodOffset, TornTail: false, Sealed: false, frameCount, digest);
        }
    }

    /// <summary>
    /// Verifies a checksum-valid seal read at <paramref name="sealOffset"/> against the segment the
    /// scan actually read: the seal must name the segment index in the file's name, sit exactly where
    /// the recorded length says, record the number
    /// of data frames before it and their digest, and be the last bytes of the file. Any mismatch is
    /// damage that left every frame valid — frames removed, added or reordered, or bytes after the
    /// seal — and throws <see cref="WriteAheadLogCorruptException"/>.
    /// </summary>
    private static void VerifySeal(
        string segmentPath, long sealOffset, byte[] sealPayload, long frameCount, uint digestState, FileStream fileStream)
    {
        var recordedIndex = BinaryPrimitives.ReadUInt64LittleEndian(sealPayload.AsSpan(0, 8));
        var recordedLength = BinaryPrimitives.ReadUInt64LittleEndian(sealPayload.AsSpan(8, 8));
        var recordedCount = BinaryPrimitives.ReadUInt64LittleEndian(sealPayload.AsSpan(16, 8));
        var recordedDigest = BinaryPrimitives.ReadUInt32LittleEndian(sealPayload.AsSpan(24, CrcBytesSize));
        var actualDigest = Algorithms.Crc32C.Finalize(digestState);
        var fileIndex = ParseIndex(segmentPath);

        string? mismatch = null;
        if (fileIndex < 0 || recordedIndex != (ulong)fileIndex)
        {
            mismatch = $"the seal belongs to segment {recordedIndex}, but the file is named as segment {fileIndex} " +
                       "(segment files renamed or swapped reorder history)";
        }
        else if (recordedLength != (ulong)sealOffset || recordedCount != (ulong)frameCount)
        {
            mismatch = $"the seal records {recordedLength} bytes and {recordedCount} frames, but it sits at byte " +
                       $"{sealOffset} after {frameCount} frames";
        }
        else if (recordedDigest != actualDigest)
        {
            mismatch = $"the seal's digest 0x{recordedDigest:X8} does not match the frames before it (0x{actualDigest:X8})";
        }
        else if (fileStream.Position != fileStream.Length)
        {
            mismatch = $"{fileStream.Length - fileStream.Position} bytes follow the seal";
        }

        if (mismatch is not null)
        {
            throw new WriteAheadLogCorruptException(
                $"WriteAheadLog segment '{Path.GetFileName(segmentPath)}' does not match its seal at byte offset " +
                $"{sealOffset}: {mismatch}. A sealed segment is never written again, so this is damage to " +
                "acknowledged history; recovery refuses to replay it and stops.",
                segmentPath,
                sealOffset);
        }
    }

    private void CloseAppendStreamLocked()
    {
        _appendStream?.Dispose();
        _appendStream = null;
    }

    private List<string> DiscoverSegmentFiles()
    {
        if (!Directory.Exists(_directory))
        {
            return [];
        }

        var paths = Directory.GetFiles(_directory, SegmentFileGlob);
        // GetFiles' order is platform-dependent — explicitly sort by the embedded counter so
        // recovery always replays segments in monotonic order.
        Array.Sort(paths, static (a, b) =>
        {
            var ai = ParseIndex(a);
            var bi = ParseIndex(b);
            return ai.CompareTo(bi);
        });
        return [.. paths];
    }

    /// <summary>
    /// Throws when the segment indices from the first segment to the last are not contiguous. The
    /// writer creates segments in counter order and reclamation only ever removes a contiguous
    /// prefix, durably, one segment at a time, so a missing index between two present segments is a
    /// lost segment of acknowledged history — whose seal went with it, leaving no damaged frame.
    /// </summary>
    private void ThrowIfSegmentMissing(List<string> segments)
    {
        var previous = -1;
        string? previousPath = null;
        foreach (var segmentPath in segments)
        {
            var index = ParseIndex(segmentPath);
            if (index < 0)
            {
                continue;
            }

            if (previousPath is not null && index != previous + 1)
            {
                var missing = Path.Combine(_directory, string.Format(
                    System.Globalization.CultureInfo.InvariantCulture, SegmentFilePattern, previous + 1));
                throw new WriteAheadLogCorruptException(
                    $"WriteAheadLog segment '{Path.GetFileName(missing)}' is missing between " +
                    $"'{Path.GetFileName(previousPath)}' and '{Path.GetFileName(segmentPath)}'. Replaying the later " +
                    "segments across the hole would reconstruct a state that never existed; recovery stops.",
                    missing,
                    0L);
            }

            previous = index;
            previousPath = segmentPath;
        }
    }

    private int DiscoverNextSegmentIndex()
    {
        var segments = DiscoverSegmentFiles();
        if (segments.Count == 0)
        {
            return 1;
        }
        var max = ParseIndex(segments[^1]);
        return max < 1 ? 1 : max;
    }

    private static int ParseIndex(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        // Filename shape: wal_ followed by at least six digits — the whole digit run is the index.
        if (name.Length < 4 + SegmentDigits || !name.StartsWith("wal_", StringComparison.Ordinal))
        {
            return -1;
        }
        return int.TryParse(
            name.AsSpan(4),
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out var index) ? index : -1;
    }

    private void DeleteAllSegments()
    {
        foreach (var path in DiscoverSegmentFiles())
        {
            DeleteSegmentDurably(path);
        }
    }

    /// <summary>
    /// Deletes one segment and makes the unlink durable before the caller deletes the next, so
    /// deleting oldest-first leaves every crash state a contiguous run of segments — never the gap
    /// recovery refuses as a lost segment.
    /// </summary>
    private void DeleteSegmentDurably(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (FileNotFoundException)
        {
            // Concurrent delete — fine.
            return;
        }

        FsyncDirectoryAfterDeletions();
    }

    /// <summary>
    /// Makes a segment unlink durable. POSIX unlink is not durable until the
    /// directory inode lands (Pillai et al. 2014 §3 — the same crash model segment creation
    /// already guards); a crash can otherwise resurrect a deleted segment, and replay applies
    /// its frames unconditionally into the memtable, shadowing newer flushed state with old
    /// values. Unlinks of several segments are not ordered against each other until each lands,
    /// so each one is made durable before the next is attempted. Windows skips this directory
    /// barrier; persistence across power loss is not established by that implementation.
    /// </summary>
    private void FsyncDirectoryAfterDeletions()
    {
        _faultInjector?.Invoke("deletion-directory-fsync");
        if (!OperatingSystem.IsWindows())
        {
            // Any path INSIDE the directory works — FsyncDirectory opens the containing dir.
            DurableFile.FsyncDirectory(Path.Combine(_directory, "wal"));
            Interlocked.Increment(ref _deletionDirectoryFsyncCount);
        }
    }

    private string CurrentSegmentName() => string.Format(
        System.Globalization.CultureInfo.InvariantCulture, SegmentFilePattern, _currentSegmentIndex);

    private void ThrowIfCorrupt()
    {
        if (_isCorrupt)
        {
            throw CorruptStateException();
        }
    }

    private static WriteAheadLogCorruptException CorruptStateException() => new(
        "WriteAheadLog has entered a corrupt state — a previous batched write or fsync failed. " +
        "Reads remain available; restart the engine to recover up to the last valid frame.");

    /// <summary>
    /// Disposes the WriteAheadLog. Stops the group-commit flusher loop and waits for any
    /// in-flight batch to complete before closing the active segment stream — so an
    /// <c>await Append + Dispose</c> sequence leaves every fsync'd entry on disk.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _channel.Writer.TryComplete();
            try
            {
                _flusherTask.GetAwaiter().GetResult();
            }
            catch
            {
                // Suppress flusher faults during shutdown — they're already surfaced via the
                // per-batch TaskCompletionSource exceptions seen by the original callers.
            }
        }
        finally
        {
            CloseAppendStreamLocked();
            _shutdownCts.Cancel();
            _shutdownCts.Dispose();
            // Release the exclusive writer lock LAST, so no successor instance can open its
            // append stream while this instance's flusher could still be mid-batch. Disposing
            // the handle releases the OS lock; the sidecar file itself is never deleted.
            _writerLock.Dispose();
        }
    }

    private enum RequestKind
    {
        Append,
        Truncate,
        TruncateCompleted,
        Recover,
    }

    private sealed record FlusherRequest(
        RequestKind Kind,
        byte[]? Frame,
        int FrameLength,
        TaskCompletionSource? CompletionTcs,
        TaskCompletionSource<IReadOnlyList<WalEntry<TKey, TValue>>>? RecoverTcs,
        ulong SequenceNumber = 0);
}

/// <summary>
/// The one final-frame tear rule of the durable logs. A frame whose checksum fails is the
/// interrupted final write only when its bytes are what a single interrupted write leaves: a prefix
/// of the frame the writer was laying down followed by zeros. Every log acknowledges an append only
/// after its fsync, so a frame whose last byte landed was written completely; if it then fails its
/// checksum, the damage is to acknowledged data and the log must fail loud.
/// </summary>
internal static class FinalFrameTearRule
{
    /// <summary>
    /// Whether <paramref name="frameTail"/> — the frame's final region — ends in an unwritten zero
    /// suffix: its last byte is zero, and every byte before that suffix agrees with
    /// <paramref name="expected"/> as far as the writer's bytes for that region are known (pass an
    /// empty span when they are not, as for a payload the checksum in the header covers). An empty
    /// region never qualifies.
    /// </summary>
    internal static bool IsUnwrittenSuffix(ReadOnlySpan<byte> frameTail, ReadOnlySpan<byte> expected)
    {
        var landed = frameTail.LastIndexOfAnyExcept((byte)0) + 1;
        if (landed == frameTail.Length)
        {
            return false;
        }

        var known = Math.Min(landed, expected.Length);
        return frameTail[..known].SequenceEqual(expected[..known]);
    }

    /// <summary>
    /// Whether a frame's trailing checksum is an unwritten suffix: its last byte is zero and the
    /// checksum bytes that landed are the leading bytes of <paramref name="checksumOfPayload"/>, the
    /// checksum over the payload bytes on disk. When any checksum byte landed the payload landed in
    /// full, so its checksum is known; a disagreeing landed byte is damage, not a tear. The checksum's
    /// genuine high byte is zero for about one frame in 256, which is why a zero last byte alone does
    /// not decide.
    /// </summary>
    internal static bool IsUnwrittenChecksumTrailer(ReadOnlySpan<byte> trailer, uint checksumOfPayload)
    {
        Span<byte> expected = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(expected, checksumOfPayload);
        return IsUnwrittenSuffix(trailer, expected);
    }
}
