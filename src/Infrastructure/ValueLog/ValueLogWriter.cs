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
using Boutquin.Storage.Domain.ValueObjects.ValueLog;
using Boutquin.Storage.Infrastructure.KeyValueStore;
using Boutquin.Storage.Infrastructure.WriteAheadLog;

namespace Boutquin.Storage.Infrastructure.ValueLog;

/// <summary>
/// Single-writer owner of the value-log append-target. Each <c>SetAsync</c> call above the
/// <see cref="ValueLogOptions.ValueLogThresholdBytes"/> threshold appends one entry frame,
/// fsyncs the segment, and returns a <see cref="ValueLogPointer"/> the engine then embeds
/// in the SSTable block entry.
/// </summary>
/// <remarks>
/// <para><b>Concurrency.</b> The engine's existing write <c>SemaphoreSlim</c> serialises
/// <see cref="AppendAsync"/>; the writer itself holds no internal lock. A parallel GC thread
/// runs against <em>closed</em> files only — it never touches the current append target.</para>
/// <para><b>Rotation.</b> Before each append, the writer checks whether the frame would take the
/// segment past <see cref="ValueLogOptions.SegmentMaxBytes"/>; if so, it first records the
/// segment's seal in the manifest (final byte length, frame count, CRC32C over its bytes; fsync'd),
/// then opens a fresh <c>valuelog_NNNNNN.log</c> whose index is <c>current + 1</c> and records its
/// add before writing. A sealed segment never receives another frame. Recovery verifies every
/// sealed segment against its seal and tear-repairs only the highest, unsealed one.</para>
/// <para><b>Orphan sweep.</b> On open the writer removes a segment the manifest does not list as live
/// only when the manifest records its delete or the segment holds no frame (a crash between its
/// creation and its add). A segment holding frames that the manifest neither lists nor deletes proves
/// the manifest lost an acknowledged add, so open fails with
/// <see cref="Boutquin.Storage.Domain.Exceptions.CorruptValueLogException"/> before anything is
/// deleted.</para>
/// <para><b>Outside the threat model.</b> The active segment carries no seal, so truncating it exactly at
/// a frame boundary is indistinguishable from those frames never having been written. The format
/// detects crash tears and damage to acknowledged bytes, not deliberate frame-boundary truncation of
/// fsync'd data in the active segment.</para>
/// </remarks>
public sealed class ValueLogWriter : IAsyncDisposable
{
    private const string SegmentFilePrefix = "valuelog_";
    private const string SegmentFileSuffix = ".log";

    private readonly string _directory;
    private readonly ValueLogManifest _manifest;
    private readonly ValueLogOptions _options;
    private readonly Dictionary<uint, long> _fileSizes = [];

    private uint _currentFileIndex;
    private FileStream? _currentStream;

    // Cached byte length of the currently-open append target. Read by the rotation check instead
    // of stat-ing _currentStream.Length on every append (F-7). DR-9: this field inherits the
    // writer's existing concurrency model — AppendAsync / MaybeRotateAsync / OpenFreshSegmentAsync
    // are append-serialized by the engine's write semaphore (single-writer by contract; see the
    // type's <remarks>), so the counter is mutated only on that serialized path and needs no extra
    // synchronization. It is set whenever _currentStream changes (open/resume/rotate) and bumped by
    // the exact frame size after each append, so it always equals the on-disk length of the current
    // segment without a syscall.
    private long _currentFileBytes;

    // Integrity metadata of the current append target, kept on the same serialized path as
    // _currentFileBytes: the number of frames it holds and the running CRC32C state over every
    // byte of it. Rotation records them as the segment's seal. _currentSealed is set once the seal
    // is durable; from then on no frame lands in the segment, even if a retried rotation finds a
    // smaller frame that would still fit.
    private long _currentFrameCount;
    private uint _currentCrcState;
    private bool _currentSealed;

    // Atomic first-caller-wins dispose flag (0 = live, 1 = disposed). Interlocked.Exchange in
    // DisposeAsync guarantees exactly one caller runs the teardown even when several threads race
    // DisposeAsync concurrently, and a racing AppendAsync sees a consistent disposed state rather
    // than a half-torn-down stream (F-8). Read as `_disposed != 0`.
    private int _disposed;

    // Poison latch: set when a torn-append repair itself failed, leaving the segment tail in an
    // unknown state. Every subsequent append fast-fails (same discipline as the WAL's corrupt
    // latch) so torn bytes can never be sealed into the middle of the file by a later append.
    private bool _appendFaulted;

    // Test-only observability (same convention as StorageFile._directoryFsyncCount): counts the
    // directory fsyncs this instance issued, so a test can prove every directory entry it created
    // was made durable. POSIX only; Windows issues none.
    internal int _directoryFsyncCount;

    // Test seam: invoked with the new segment's file index immediately before the manifest add
    // during segment creation. Lets a test inject a manifest-record failure at the exact program
    // point where a real RecordAddAsync fault (I/O error, cancellation) would fire. No-op when
    // unset — same convention as LsmStorageEngine.IngestGateWindowHook.
    internal Func<uint, Task>? RotationManifestRecordHook { get; set; }

    // Test seam: when set, replaces the frame write in AppendAsync. Lets a test simulate a
    // mid-frame fault (partial write followed by an exception) — a state a real FileStream
    // cannot be made to produce deterministically. No-op when unset.
    internal Func<FileStream, ReadOnlyMemory<byte>, CancellationToken, Task>? FrameWriteInterceptor { get; set; }

    private ValueLogWriter(string directory, ValueLogManifest manifest, ValueLogOptions options)
    {
        _directory = directory;
        _manifest = manifest;
        _options = options;
    }

    /// <summary>
    /// Opens (or creates) the value-log directory at <paramref name="directory"/>, verifies every
    /// sealed manifest-live segment against its seal, scans the active (highest) segment to its
    /// effective EOF (truncating a torn final append), and selects an append target.
    /// </summary>
    public static async Task<ValueLogWriter> OpenAsync(
        string directory,
        ValueLogManifest manifest,
        ValueLogOptions options,
        CancellationToken cancellationToken = default)
    {
        Guard.AgainstNullOrWhiteSpace(() => directory);
        Guard.AgainstNullOrDefault(() => manifest);
        Guard.AgainstNullOrDefault(() => options);

        // A created segment directory needs a durable entry of its own: segment creation fsyncs
        // only the segment's parent, so a bare create could lose the directory — and every
        // acknowledged frame in it — when the manifest lives elsewhere.
        var created = DurableFile.CreateDirectoryDurable(directory);

        var writer = new ValueLogWriter(directory, manifest, options);
        if (!OperatingSystem.IsWindows())
        {
            writer._directoryFsyncCount += created;
        }
        await writer.InitialiseAsync(cancellationToken).ConfigureAwait(false);
        return writer;
    }

    /// <summary>The file-index suffix of the currently-open append target.</summary>
    public uint CurrentFileIndex
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            return _currentFileIndex;
        }
    }

    /// <summary>Byte length of the currently-open append target (the cached counter; no syscall).</summary>
    public long CurrentFileBytes
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            return _currentFileBytes;
        }
    }

    /// <summary>Map of <c>fileIndex → on-disk bytes</c> across every manifest-live value-log
    /// segment, including the current append target. Exposed for observability and for the
    /// GC's whole-file reclamation bookkeeping.</summary>
    public IReadOnlyDictionary<uint, long> FileSizes
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            return new Dictionary<uint, long>(_fileSizes);
        }
    }

    /// <summary>
    /// Appends <paramref name="payload"/> as a new entry frame tagged with
    /// <paramref name="sequenceNumber"/>, fsyncs the segment, and returns the pointer a
    /// subsequent SSTable write embeds. Rotates to a new segment if the current one would
    /// exceed <see cref="ValueLogOptions.SegmentMaxBytes"/>.
    /// </summary>
    /// <exception cref="ValueTooLargeException">
    /// Thrown when <paramref name="payload"/> exceeds
    /// <see cref="ValueLogOptions.MaxEntryBytes"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="payload"/> is empty: an empty frame is all zeros after its
    /// header, which recovery could not tell apart from an unwritten tail.
    /// </exception>
    public async Task<ValueLogPointer> AppendAsync(
        ReadOnlyMemory<byte> payload,
        ulong sequenceNumber,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        if (_appendFaulted)
        {
            throw new CorruptValueLogException(
                _currentFileIndex,
                (ulong)_currentFileBytes,
                "A previous append fault could not be repaired (torn-tail truncation failed); " +
                "the writer refuses further appends so torn bytes are never sealed mid-file. " +
                "Reopen the value log to run recovery.");
        }

        // An empty payload's checksum is CRC32C of zero bytes, which is 0, so its whole frame after
        // the header would be zeros: header rot on such a frame, acknowledged and final, would match
        // the tear rule's unwritten-tail shape and be truncated. Every frame must carry a non-zero
        // byte after its header (a non-empty all-zero payload has a non-zero checksum), so a complete
        // header that fails its checksum is always told apart from a torn one.
        if (payload.IsEmpty)
        {
            throw new ArgumentException(
                "A value-log entry must not be empty: an empty frame has nothing after its header that " +
                "recovery could tell apart from an unwritten tail.",
                nameof(payload));
        }

        if (payload.Length > _options.MaxEntryBytes)
        {
            throw new ValueTooLargeException(payload.Length, _options.MaxEntryBytes);
        }

        var frameSize = ValueLogFrame.FrameSize(payload.Length);
        await MaybeRotateAsync(frameSize, cancellationToken).ConfigureAwait(false);

        // Capture the append-target stream into a local once. The single-writer contract (DR-9)
        // serializes AppendAsync against itself and against DisposeAsync, but a concurrent
        // DisposeAsync that nulls _currentStream must never turn into a NullReferenceException on a
        // later dereference — the local pins the reference, so a racing dispose surfaces a typed
        // ObjectDisposedException from WriteAsync, not an NRE from a null field (F-8).
        var stream = _currentStream;
        if (stream is null)
        {
            throw new InvalidOperationException("Append target stream is not open.");
        }

        // Build frame bytes in a pooled buffer to avoid per-append allocation for large payloads.
        var totalLen = checked((int)frameSize);
        var rented = System.Buffers.ArrayPool<byte>.Shared.Rent(totalLen);
        try
        {
            ulong offset;
            // Frame start offset for torn-tail repair; -1 until the Position read succeeds so a
            // fault BEFORE the frame's start is known can never truncate to a wrong offset.
            var repairOffset = -1L;
            try
            {
                offset = (ulong)stream.Position;
                repairOffset = (long)offset;

                var frame = rented.AsSpan(0, totalLen);

                BinaryPrimitives.WriteUInt32LittleEndian(frame[..ValueLogFrame.HeaderCrcOffset], (uint)payload.Length);
                frame[ValueLogFrame.KindOffset] = ValueLogFrame.KindPayload;
                BinaryPrimitives.WriteUInt64LittleEndian(frame.Slice(ValueLogFrame.SeqnoOffset, 8), sequenceNumber);
                BinaryPrimitives.WriteUInt32LittleEndian(
                    frame.Slice(ValueLogFrame.HeaderCrcOffset, 4),
                    ValueLogFrame.ComputeHeaderCrc(frame[..ValueLogFrame.EntryHeaderSize]));
                payload.Span.CopyTo(frame.Slice(ValueLogFrame.EntryHeaderSize, payload.Length));

                BinaryPrimitives.WriteUInt32LittleEndian(
                    frame.Slice(ValueLogFrame.EntryHeaderSize + payload.Length, ValueLogFrame.EntryCrcSize),
                    Crc32C.HashToUInt32(payload.Span));

                // Write the frame on the async path — no synchronous Stream.Write blocks the request
                // thread (F-4). The fsync stays synchronous: FileStream.Flush(flushToDisk:true) has no
                // async equivalent that guarantees the same disk-durability barrier.
                if (FrameWriteInterceptor is { } frameWriteInterceptor)
                {
                    await frameWriteInterceptor(stream, rented.AsMemory(0, totalLen), cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await stream.WriteAsync(rented.AsMemory(0, totalLen), cancellationToken).ConfigureAwait(false);
                }

                stream.Flush(flushToDisk: true);
            }
            catch (Exception ex) when ((ex is ObjectDisposedException or NotSupportedException) && _disposed != 0)
            {
                // The single-writer contract (DR-9) serializes AppendAsync against DisposeAsync, but a
                // caller that violates it can have the pinned stream disposed mid-append. A disposed
                // FileStream throws ObjectDisposedException (write) or NotSupportedException (Position
                // — seeking unsupported once closed). Surface a single typed ObjectDisposedException so
                // the racing append is a clean, recognisable signal rather than a leaked low-level
                // stream exception (F-8 dispose-atomicity guarantee).
                throw new ObjectDisposedException(nameof(ValueLogWriter));
            }
            catch when (repairOffset >= 0)
            {
                // A fault mid-frame (partial write, fsync failure, cancellation observed inside
                // WriteAsync) leaves torn bytes at the tail with the stream position advanced past
                // them. Without repair, the NEXT append seals those torn bytes into the middle of
                // the file — at reopen the sequential scan classifies them as mid-file corruption
                // (unreadable frame followed by a valid one) and refuses to start, even though every
                // acknowledged frame is intact. The failed frame was never acknowledged and nothing
                // references it, so truncating back to its start offset is always safe.
                RepairTornAppendOrPoison(stream, repairOffset);
                throw;
            }

            // Advance the cached byte counter by exactly the frame just written (single-writer
            // serialized path — DR-9). _fileSizes mirrors it for the GC's whole-file bookkeeping.
            _currentFileBytes += totalLen;
            _fileSizes[_currentFileIndex] = _currentFileBytes;
            _currentFrameCount++;
            Crc32C.Append(rented.AsSpan(0, totalLen), ref _currentCrcState);

            return new ValueLogPointer(_currentFileIndex, offset, sequenceNumber);
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public async ValueTask DisposeAsync()
    {
        // First-caller-wins: only the thread that flips 0 → 1 runs the teardown. A concurrent
        // second DisposeAsync sees the prior value 1 and returns a clean no-op, so the stream is
        // never disposed twice and a racing AppendAsync never observes a half-disposed stream (F-8).
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_currentStream is not null)
        {
            await _currentStream.DisposeAsync().ConfigureAwait(false);
            _currentStream = null;
        }
    }

    /// <summary>Resolves the on-disk path for a segment file with the given index.</summary>
    public static string ResolveSegmentPath(string directory, uint fileIndex)
        => Path.Combine(directory, $"{SegmentFilePrefix}{fileIndex:D6}{SegmentFileSuffix}");

    private async Task InitialiseAsync(CancellationToken cancellationToken)
    {
        // Reconciliation sweep: delete any on-disk segment file the manifest does NOT list as
        // live. This is the GC's documented crash contract — the delete-tombstone is fsync'd
        // BEFORE the unlink, so a crash between the two leaves an orphan file that would
        // otherwise leak disk space forever (no pointer can reference it; the manifest says
        // it is dead). Also covers a crash between segment-file creation and the manifest's
        // add record.
        // Snapshot the live set once via the async accessor (no blocking property getter), then
        // reconcile on-disk files against it.
        var liveFiles = await _manifest.GetLiveFilesAsync(cancellationToken).ConfigureAwait(false);

        // The sweep may remove only what the manifest vouches for: a segment it records as deleted,
        // or one that never received a frame. Appends to a segment start only after its add record
        // is durable, so a segment holding frames that the manifest neither lists nor deletes proves
        // the manifest lost an acknowledged add (truncated at a record boundary, emptied, replaced or
        // missing). Refuse before deleting anything, whatever the manifest's own replay concluded.
        var deletedFiles = await _manifest.GetDeletedFilesAsync(cancellationToken).ConfigureAwait(false);
        if (ValueLogManifest.FindUnrecordedSegmentHoldingFrames(_directory, liveFiles, deletedFiles) is { } unrecorded)
        {
            throw new CorruptValueLogException(
                fileIndex: 0,
                offset: 0,
                $"Value-log segment {unrecorded} holds frames, but the manifest neither lists it as live nor records " +
                "its delete: the manifest lost the segment's acknowledged add. Recovery refuses to open rather than " +
                "let the orphan sweep delete acknowledged values; restore the manifest from backup.");
        }

        foreach (var path in Directory.EnumerateFiles(_directory, $"{SegmentFilePrefix}*{SegmentFileSuffix}"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (!uint.TryParse(name.AsSpan(SegmentFilePrefix.Length), out var index)
                || liveFiles.Contains(index))
            {
                continue;
            }

            try
            {
                File.Delete(path);
            }
            catch (FileNotFoundException)
            {
                // Concurrent delete — fine.
            }
        }

        if (liveFiles.Count == 0)
        {
            await OpenFreshSegmentAsync(fileIndex: 0, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Only the highest live segment can hold an interrupted append: rotation seals a segment
        // before its successor exists and never appends to it again. Every lower segment must
        // therefore carry a seal and match it exactly; the active segment alone gets the tear rule.
        // Sealed segments are verified before the active one is repaired, so a failure leaves
        // every byte in place.
        var seals = await _manifest.GetSealsAsync(cancellationToken).ConfigureAwait(false);
        var highest = liveFiles.Max();
        SegmentScan activeScan = default;
        foreach (var index in liveFiles.Order())
        {
            var path = ResolveSegmentPath(_directory, index);

            if (!File.Exists(path))
            {
                throw new MissingValueLogFileException(
                    index,
                    $"Value-log segment file {path} named by the manifest is missing on disk.");
            }

            if (seals.TryGetValue(index, out var seal))
            {
                await VerifySealedSegmentAsync(path, index, seal, cancellationToken).ConfigureAwait(false);
                _fileSizes[index] = seal.Length;
                continue;
            }

            if (index != highest)
            {
                throw new CorruptValueLogException(
                    index,
                    0,
                    $"Value-log segment {path} lies below the active segment {highest} but the manifest holds no seal " +
                    "for it. Rotation seals a segment before its successor exists, so its contents cannot be vouched " +
                    "for; recovery refuses to use it.");
            }

            activeScan = await ScanSegmentAsync(path, index, sealedSegment: false, cancellationToken).ConfigureAwait(false);
        }

        var highestPath = ResolveSegmentPath(_directory, highest);
        if (seals.ContainsKey(highest))
        {
            // A crash after the highest segment's seal became durable but before its successor's
            // add: the segment is closed, so appends go to a fresh one.
            await OpenFreshSegmentAsync(highest + 1, cancellationToken).ConfigureAwait(false);
            return;
        }

        _fileSizes[highest] = activeScan.Length;
        if (new FileInfo(highestPath).Length != activeScan.Length)
        {
            // Truncate the torn tail of the interrupted final append — the scan stopped at the
            // last good frame, so the on-disk length must match before appends resume.
            using var trunc = new FileStream(highestPath, FileMode.Open, FileAccess.Write, FileShare.None);
            trunc.SetLength(activeScan.Length);
            trunc.Flush(flushToDisk: true);
        }

        // Resume appending to the active segment if it still has room; otherwise seal it and
        // rotate to a fresh segment.
        _currentFileIndex = highest;
        _currentFileBytes = activeScan.Length;
        _currentFrameCount = activeScan.FrameCount;
        _currentCrcState = activeScan.CrcState;
        _currentSealed = false;
        if (activeScan.Length < _options.SegmentMaxBytes)
        {
            _currentStream = new FileStream(highestPath, FileMode.Open, FileAccess.Write, FileShare.Read);
            _currentStream.Seek(0, SeekOrigin.End);
        }
        else
        {
            await SealCurrentSegmentAsync(cancellationToken).ConfigureAwait(false);
            await OpenFreshSegmentAsync(highest + 1, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Verifies a sealed segment against its seal: exact byte length, every frame intact (no tear
    /// rule applies — a sealed segment cannot hold an interrupted append), the recorded frame count
    /// and the recorded checksum. Any difference fails loud and leaves the file untouched.
    /// </summary>
    private static async Task VerifySealedSegmentAsync(
        string path,
        uint fileIndex,
        ValueLogManifest.SegmentSeal seal,
        CancellationToken cancellationToken)
    {
        var length = new FileInfo(path).Length;
        if (length != seal.Length)
        {
            throw new CorruptValueLogException(
                fileIndex,
                (ulong)Math.Min(length, seal.Length),
                $"Sealed value-log segment {path} is {length} bytes long but its seal records {seal.Length}. " +
                "A sealed segment never changes length, so bytes were lost or added; recovery refuses to use it.");
        }

        var scan = await ScanSegmentAsync(path, fileIndex, sealedSegment: true, cancellationToken).ConfigureAwait(false);
        var checksum = Crc32C.Finalize(scan.CrcState);
        if (scan.FrameCount != seal.FrameCount || checksum != seal.Checksum)
        {
            throw new CorruptValueLogException(
                fileIndex,
                0,
                $"Sealed value-log segment {path} holds {scan.FrameCount} frames with checksum 0x{checksum:X8}, " +
                $"but its seal records {seal.FrameCount} frames with checksum 0x{seal.Checksum:X8}. Its contents " +
                "changed after it was sealed; recovery refuses to use it.");
        }
    }

    /// <summary>Records the current append target's seal in the manifest (durable on return).</summary>
    private async Task SealCurrentSegmentAsync(CancellationToken cancellationToken)
    {
        await _manifest.RecordSealAsync(
            _currentFileIndex,
            new ValueLogManifest.SegmentSeal(_currentFileBytes, _currentFrameCount, Crc32C.Finalize(_currentCrcState)),
            cancellationToken).ConfigureAwait(false);
        _currentSealed = true;
    }

    /// <summary>Running CRC32C state over a fresh segment's file header.</summary>
    private static uint SegmentHeaderCrcState()
    {
        Span<byte> header = stackalloc byte[ValueLogFrame.HeaderSize];
        ValueLogFrame.Magic.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], ValueLogFrame.CurrentFormatVersion);
        var state = Crc32C.InitialState;
        Crc32C.Append(header, ref state);
        return state;
    }

    /// <summary>The result of a segment scan: the clean length, the frames in it and the running
    /// CRC32C state over its bytes <c>[0, Length)</c>.</summary>
    private readonly record struct SegmentScan(long Length, long FrameCount, uint CrcState);

    /// <summary>
    /// Scans a segment frame by frame. For the active segment (<paramref name="sealedSegment"/> is
    /// false) the tear rule applies and the scan stops at the start of a torn final append; for a
    /// sealed segment any shape the tear rule would truncate is corruption, because a sealed segment
    /// cannot hold an interrupted append.
    /// </summary>
    private static async Task<SegmentScan> ScanSegmentAsync(string path, uint fileIndex, bool sealedSegment, CancellationToken cancellationToken)
    {
        var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            var total = reader.Length;

            if (total < ValueLogFrame.HeaderSize)
            {
                throw new UnrecognisedValueLogFormatException(
                    path,
                    $"Value-log segment {path} is shorter than its {ValueLogFrame.HeaderSize}-byte header ({total} bytes present).");
            }

            var headerBuf = new byte[ValueLogFrame.HeaderSize];
            var headerRead = ReadAtMost(reader, headerBuf);
            if (headerRead < ValueLogFrame.HeaderSize || !headerBuf.AsSpan(0, 4).SequenceEqual(ValueLogFrame.Magic))
            {
                throw new UnrecognisedValueLogFormatException(
                    path,
                    $"Value-log segment {path} opens with bytes that do not match the 'BSVL' magic.");
            }

            var version = BinaryPrimitives.ReadUInt32LittleEndian(headerBuf.AsSpan(4, 4));
            if (version != ValueLogFrame.CurrentFormatVersion)
            {
                throw new UnrecognisedValueLogFormatException(
                    path,
                    $"Value-log segment {path} declares format_version={version}; this build accepts only version {ValueLogFrame.CurrentFormatVersion}.");
            }

            // The tear rule (see ValueLogFrame): every decision rests on bytes the writer
            // authenticated — the header checksum vouches for the length before it is used, so a
            // damaged length is detected, never inferred from where later frames might start.
            // The running segment checksum and frame count advance only over accepted frames.
            var segmentCrcState = Crc32C.InitialState;
            Crc32C.Append(headerBuf, ref segmentCrcState);
            var frameCount = 0L;
            var pos = (long)ValueLogFrame.HeaderSize;
            var entryHeader = new byte[ValueLogFrame.EntryHeaderSize];
            var crcTrailer = new byte[ValueLogFrame.EntryCrcSize];
            var crcScratch = new byte[64 * 1024];

            while (pos < total)
            {
                cancellationToken.ThrowIfCancellationRequested();

                reader.Seek(pos, SeekOrigin.Begin);
                if (total - pos < ValueLogFrame.EntryHeaderSize
                    || ReadAtMost(reader, entryHeader) < ValueLogFrame.EntryHeaderSize)
                {
                    // A partial header at end of file: the interrupted final append.
                    ThrowIfSealed(sealedSegment, path, fileIndex, pos, "a partial frame header at end of file");
                    break;
                }

                var recordedHeaderCrc = ValueLogFrame.ReadHeaderCrc(entryHeader);
                var computedHeaderCrc = ValueLogFrame.ComputeHeaderCrc(entryHeader);
                if (recordedHeaderCrc != computedHeaderCrc)
                {
                    if (IsUnwrittenHeaderTail(reader, pos, total, entryHeader, crcScratch))
                    {
                        // The landed bytes of the final append stop inside its header and everything
                        // after them reads as zeros to EOF: the unwritten extent a crash leaves when
                        // the file length reached disk but the later bytes did not (an all-zero run
                        // from the frame start is the empty-prefix case). Appends are fsync'd one
                        // frame at a time before their pointer is returned, so nothing acknowledged
                        // lies at or after this frame: a torn tail, whatever the run's length.
                        ThrowIfSealed(sealedSegment, path, fileIndex, pos, "a frame header prefix followed by zeros to end of file");
                        break;
                    }

                    throw new CorruptValueLogException(
                        fileIndex,
                        (ulong)pos,
                        $"Value-log segment {path} entry at offset {pos} failed its header checksum " +
                        $"(expected=0x{recordedHeaderCrc:X8}, actual=0x{computedHeaderCrc:X8}) with non-zero bytes after its header. " +
                        "Only a partial header, a header prefix followed by zeros to end of file, a payload short at end of file or a final frame " +
                        "whose payload checksum fails with its checksum trailer unlanded and only zeros after it is a torn tail; this is corruption, so recovery refuses to truncate.");
                }

                var length = BinaryPrimitives.ReadUInt32LittleEndian(entryHeader);
                var frameEnd = pos + ValueLogFrame.FrameSize(length);
                if (frameEnd > total)
                {
                    // An authenticated header whose payload is short at end of file: the
                    // interrupted final append. MaxEntryBytes is an append-time cap and plays no
                    // part here — a frame acknowledged under a larger cap is live data.
                    ThrowIfSealed(sealedSegment, path, fileIndex, pos, "a frame whose payload is short at end of file");
                    break;
                }

                // Verify the payload checksum, streamed through a fixed scratch buffer so a large
                // frame never materialises its payload.
                var crcState = Crc32C.InitialState;
                var frameSegmentCrcState = segmentCrcState;
                Crc32C.Append(entryHeader, ref frameSegmentCrcState);
                for (var remaining = (long)length; remaining > 0;)
                {
                    var n = (int)Math.Min(crcScratch.Length, remaining);
                    ReadExactLocal(reader, crcScratch.AsSpan(0, n));
                    Crc32C.Append(crcScratch.AsSpan(0, n), ref crcState);
                    Crc32C.Append(crcScratch.AsSpan(0, n), ref frameSegmentCrcState);
                    remaining -= n;
                }

                ReadExactLocal(reader, crcTrailer);
                Crc32C.Append(crcTrailer, ref frameSegmentCrcState);
                var recordedCrc = BinaryPrimitives.ReadUInt32LittleEndian(crcTrailer);
                var crcComputed = Crc32C.Finalize(crcState);

                if (recordedCrc != crcComputed)
                {
                    // Each append writes and fsyncs one frame before its pointer is returned, so an
                    // interrupted one leaves a prefix of its frame followed by zeros: its checksum
                    // trailer is an unwritten suffix. A frame whose last byte landed was written
                    // completely and acknowledged; its checksum failure is damage, never a tear.
                    if (!FinalFrameTearRule.IsUnwrittenChecksumTrailer(crcTrailer, crcComputed))
                    {
                        throw new CorruptValueLogException(
                            fileIndex,
                            (ulong)pos,
                            $"Value-log segment {path} entry at offset {pos} failed its payload checksum " +
                            $"(expected=0x{recordedCrc:X8}, actual=0x{crcComputed:X8}) although its last byte landed " +
                            "or its landed checksum bytes disagree with its payload; the frame was written completely, " +
                            "so this is corruption of an acknowledged value, not a torn tail, and recovery refuses to truncate.");
                    }

                    if (frameEnd == total || IsZeroToEnd(reader, frameEnd, total, crcScratch))
                    {
                        // The interrupted final append — its trailer unlanded, nothing but zeros to
                        // EOF after it.
                        ThrowIfSealed(sealedSegment, path, fileIndex, pos, "a final frame whose payload checksum fails");
                        break;
                    }

                    throw new CorruptValueLogException(
                        fileIndex,
                        (ulong)pos,
                        $"Value-log segment {path} entry at offset {pos} failed its payload checksum " +
                        $"(expected=0x{recordedCrc:X8}, actual=0x{crcComputed:X8}) with non-zero bytes after it; this is " +
                        "corruption, not a torn tail, so recovery refuses to truncate.");
                }

                segmentCrcState = frameSegmentCrcState;
                frameCount++;
                pos = frameEnd;
            }

            return new SegmentScan(pos, frameCount, segmentCrcState);
        }
        finally
        {
            await reader.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A shape the tear rule repairs in the active segment is corruption in a sealed one: every
    /// frame of a sealed segment was acknowledged before its successor existed.
    /// </summary>
    private static void ThrowIfSealed(bool sealedSegment, string path, uint fileIndex, long offset, string shape)
    {
        if (sealedSegment)
        {
            throw new CorruptValueLogException(
                fileIndex,
                (ulong)offset,
                $"Sealed value-log segment {path} holds {shape} at offset {offset}. Only the active segment can " +
                "hold an interrupted append, so this is corruption of acknowledged frames; recovery refuses to truncate.");
        }
    }

    private async Task OpenFreshSegmentAsync(uint fileIndex, CancellationToken cancellationToken)
    {
        var (stream, initialBytes) = await CreateManifestedSegmentAsync(fileIndex, cancellationToken).ConfigureAwait(false);

        _currentFileIndex = fileIndex;
        _currentStream = stream;
        _currentFileBytes = initialBytes;
        _fileSizes[fileIndex] = initialBytes;
        _currentFrameCount = 0;
        _currentCrcState = SegmentHeaderCrcState();
        _currentSealed = false;
    }

    /// <summary>
    /// Creates a fresh segment file and takes it all the way to fully-established — header
    /// written and fsync'd, directory entry durable, manifest add recorded — WITHOUT touching
    /// the writer's published append-target state. On any failure the partially-created file
    /// is torn down and the writer's state is untouched, so segment creation is
    /// atomic-or-nothing from the caller's perspective.
    /// </summary>
    /// <remarks>
    /// Publishing the new segment as the append target before the manifest add is durable is
    /// a data-loss window: a failed <c>RecordAddAsync</c> (I/O error, caller cancellation)
    /// would leave subsequent appends landing in a segment the manifest does not list as
    /// live, and the next startup's reconciliation sweep deletes exactly those files —
    /// destroying acknowledged, fsync'd values. Establish-fully-then-publish closes it.
    /// </remarks>
    private async Task<(FileStream Stream, long InitialBytes)> CreateManifestedSegmentAsync(
        uint fileIndex,
        CancellationToken cancellationToken)
    {
        var path = ResolveSegmentPath(_directory, fileIndex);
        if (File.Exists(path))
        {
            // Stale file from a crashed prior run or a failed prior rotation attempt — the
            // manifest is authoritative and does not list it. Delete and recreate.
            File.Delete(path);
        }

        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        try
        {
            // Heap-backed header buffer (not stackalloc) so the bytes can be handed to WriteAsync as a
            // Memory<byte> on the async open path — no synchronous Stream.Write blocks here (F-5). The
            // header is 8 bytes and this path runs once per segment open, so the allocation is cold.
            var header = new byte[ValueLogFrame.HeaderSize];
            ValueLogFrame.Magic.CopyTo(header.AsSpan());
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), ValueLogFrame.CurrentFormatVersion);
            await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);

            // Fsync the containing directory so the new segment's directory entry is durable
            // BEFORE the manifest records the add below. Crash ordering without it: values
            // appended + fsync'd + acked, manifest add durable, directory entry lost → restart
            // finds the manifest naming a file that does not exist (MissingValueLogFileException,
            // engine cannot start) and the acknowledged values are gone. Same discipline as WAL
            // segment creation.
            if (!OperatingSystem.IsWindows())
            {
                DurableFile.FsyncDirectory(path);
                _directoryFsyncCount++;
            }

            // Record the add in the manifest AFTER writing + fsyncing the header, so a crash
            // between header write and manifest fsync leaves a stale on-disk file the next
            // startup will reconcile against the (non-live) manifest state and delete.
            if (RotationManifestRecordHook is { } manifestRecordHook)
            {
                await manifestRecordHook(fileIndex).ConfigureAwait(false);
            }

            await _manifest.RecordAddAsync(fileIndex, cancellationToken).ConfigureAwait(false);

            return (stream, stream.Length);
        }
        catch
        {
            // Atomic-or-nothing: never leave a created-but-unmanifested file behind as a live
            // handle. Best-effort delete — if the unlink itself fails, the next startup's
            // reconciliation sweep removes the orphan (the manifest never recorded it).
            await stream.DisposeAsync().ConfigureAwait(false);
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // Orphan cleanup is deferred to the reconciliation sweep.
            }

            throw;
        }
    }

    private async Task MaybeRotateAsync(long nextFrameSize, CancellationToken cancellationToken)
    {
        if (_currentStream is null)
        {
            return;
        }

        // Read the cached counter, not _currentStream.Length — the append path keeps it exact and a
        // per-append stat syscall is avoidable (F-7, DR-9 single-writer).
        // A pointer encodes its offset in 40 bits, so a segment also rotates before any frame
        // could start beyond ValueLogPointer.MaxOffset, whatever SegmentMaxBytes allows.
        // A sealed segment always rotates: an earlier rotation sealed it and then failed to create
        // the successor, and no frame may land in a sealed segment, even one that would still fit.
        if (!_currentSealed
            && _currentFileBytes + nextFrameSize <= Math.Min(_options.SegmentMaxBytes, (long)ValueLogPointer.MaxOffset))
        {
            return;
        }

        // Seal-then-create-then-swap: record the outgoing segment's seal (durable) BEFORE its
        // successor exists, so only the highest segment can ever hold an interrupted append; then
        // fully establish the successor BEFORE tearing down the current append target. If creation
        // fails at any step, the sealed segment remains the (closed) current target and the next
        // append simply retries the creation — the writer never points at an unmanifested segment.
        if (!_currentSealed)
        {
            await SealCurrentSegmentAsync(cancellationToken).ConfigureAwait(false);
        }

        var nextIndex = _currentFileIndex + 1;
        var (stream, initialBytes) = await CreateManifestedSegmentAsync(nextIndex, cancellationToken).ConfigureAwait(false);

        await _currentStream.DisposeAsync().ConfigureAwait(false);
        _currentFileIndex = nextIndex;
        _currentStream = stream;
        _currentFileBytes = initialBytes;
        _fileSizes[nextIndex] = initialBytes;
        _currentFrameCount = 0;
        _currentCrcState = SegmentHeaderCrcState();
        _currentSealed = false;
    }

    /// <summary>
    /// Restores the segment tail after a failed append by truncating back to the failed
    /// frame's start offset (the frame was never acknowledged, so removal is always safe).
    /// If the truncation itself fails the tail state is unknown — the writer is poisoned so
    /// no later append can seal torn bytes into the middle of the file.
    /// </summary>
    private void RepairTornAppendOrPoison(FileStream stream, long frameStartOffset)
    {
        try
        {
            stream.SetLength(frameStartOffset);
            stream.Flush(flushToDisk: true);
        }
        catch
        {
            _appendFaulted = true;
        }
    }

    /// <summary>
    /// Whether the frame at <paramref name="frameStart"/> is the unwritten remainder of an
    /// interrupted append: its header's landed bytes are a prefix a writer could have produced (a
    /// non-zero kind byte must be a kind this format defines) and every byte after that prefix —
    /// the rest of the header, the payload and any extent beyond — is zero to <paramref name="total"/>.
    /// The landed prefix ends at the header's last non-zero byte; an all-zero header is the
    /// empty prefix.
    /// </summary>
    private static bool IsUnwrittenHeaderTail(FileStream reader, long frameStart, long total, ReadOnlySpan<byte> header, byte[] scratch)
    {
        var landed = header.LastIndexOfAnyExcept((byte)0) + 1;
        if (landed > ValueLogFrame.KindOffset
            && header[ValueLogFrame.KindOffset] is not (ValueLogFrame.KindPayload or ValueLogFrame.KindReserved))
        {
            return false;
        }

        return IsZeroToEnd(reader, frameStart + ValueLogFrame.EntryHeaderSize, total, scratch);
    }

    /// <summary>
    /// Whether every byte from <paramref name="start"/> to <paramref name="total"/> is zero, read
    /// through <paramref name="scratch"/>.
    /// </summary>
    private static bool IsZeroToEnd(FileStream reader, long start, long total, byte[] scratch)
    {
        reader.Seek(start, SeekOrigin.Begin);
        for (var remaining = total - start; remaining > 0;)
        {
            var read = ReadAtMost(reader, scratch.AsSpan(0, (int)Math.Min(scratch.Length, remaining)));
            if (read == 0 || scratch.AsSpan(0, read).ContainsAnyExcept((byte)0))
            {
                return false;
            }

            remaining -= read;
        }

        return true;
    }

    private static int ReadAtMost(Stream stream, Span<byte> destination)
    {
        var total = 0;
        while (total < destination.Length)
        {
            var read = stream.Read(destination[total..]);
            if (read == 0)
            {
                return total;
            }
            total += read;
        }
        return total;
    }

    private static void ReadExactLocal(Stream stream, Span<byte> destination)
    {
        var total = 0;
        while (total < destination.Length)
        {
            var read = stream.Read(destination[total..]);
            if (read == 0)
            {
                throw new EndOfStreamException(
                    $"Unexpected end of stream while reading value-log frame (got {total} of {destination.Length} bytes).");
            }
            total += read;
        }
    }
}
