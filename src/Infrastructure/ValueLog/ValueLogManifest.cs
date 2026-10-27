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
using Boutquin.Storage.Infrastructure.WriteAheadLog;

namespace Boutquin.Storage.Infrastructure.ValueLog;

/// <summary>
/// Append-only on-disk record of which <c>valuelog_NNNNNN.log</c> segment files are live for
/// an <c>LsmStorageEngine</c>. A GC delete records the tombstone and fsyncs BEFORE the
/// segment file is unlinked; a startup recovery replays the log to reconstruct the live set
/// and reconciles any orphaned files against the tombstones.
/// </summary>
/// <remarks>
/// <para><b>File layout.</b> Fixed 8-byte header followed by a stream of 29-byte records.
/// Header: <c>[4B magic 'B','S','V','M'][4B format_version LE u32 = 1]</c>.
/// Record: <c>[1B kind][4B fileIndex LE u32][8B sealedLength LE u64][8B frameCount LE u64][4B segmentCrc32c LE u32][4B crc32c LE u32]</c>
/// where <c>kind = 0x00</c> = add, <c>0x01</c> = delete, <c>0x02</c> = seal. The final CRC covers
/// the first 25 bytes of the record. Add and delete records carry zeros in the three seal fields.</para>
/// <para><b>Seal.</b> Rotation seals the outgoing segment before its successor exists: the seal
/// record names the segment's final byte length, its frame count and a CRC32C over its bytes, and
/// it is fsync'd before the successor's file is created and its add recorded. Nothing is appended
/// to a sealed segment again, so a crash can interrupt an append only in the highest, unsealed
/// segment; a crash between the seal and the successor's add leaves a sealed highest segment, and
/// the writer then starts a fresh one. Recovery verifies every sealed segment against its seal and
/// fails loud on any difference; only the active segment is tear-repaired.</para>
/// <para><b>Recovery.</b> A record carries no length field — every record is exactly
/// <see cref="RecordSize"/> bytes and its CRC covers every other field — so the tear rule is
/// deterministic: a trailing partial record, or a final record whose landed bytes (a prefix with a
/// kind byte this format defines, whose landed CRC bytes, if any, equal the leading bytes of the CRC
/// over the record's fields) are followed only by zeros to end of file (the file length reached
/// disk, the later record bytes did not; an all-zero record is the empty prefix), is a torn append
/// and is truncated. A complete record whose CRC fails with no zero suffix, a final record whose
/// landed CRC bytes disagree with its fields, or a CRC failure anywhere before the final record, is
/// corruption, and raises
/// <see cref="Boutquin.Storage.Domain.Exceptions.CorruptValueLogException"/> with a synthetic
/// <c>fileIndex = 0</c> to flag the manifest itself rather than a segment. Dropping a torn final
/// record is additionally refused when independent evidence shows it was an acknowledged add: an
/// on-disk segment holding frames that the manifest neither lists as live nor records as deleted
/// (appends to a segment start only after its add is durable). The same evidence refuses a manifest
/// that ends cleanly on a record boundary or holds only its header while such a segment exists: the
/// writer's orphan sweep fails loud instead of deleting it. A manifest torn at creation (no longer
/// than its header, holding only a header prefix or zeros) gets its header rewritten, and a missing
/// one is created, only when no segment beside it holds frames — no segment exists before the header
/// is durable — otherwise construction fails loud and rewrites nothing. A failed append is truncated
/// back before its error propagates.</para>
/// </remarks>
public sealed class ValueLogManifest : IAsyncDisposable
{
    /// <summary>The magic bytes that open every manifest file.</summary>
    public static readonly byte[] Magic = [(byte)'B', (byte)'S', (byte)'V', (byte)'M'];

    /// <summary>The only format version the v2.0.0 GA accepts.</summary>
    public const uint CurrentFormatVersion = 1;

    /// <summary>Byte length of the manifest file header (magic + version).</summary>
    public const int HeaderSize = 8;

    /// <summary>Byte length of a single record on disk: kind(1) + fileIndex(4) + sealedLength(8) +
    /// frameCount(8) + segmentCrc32c(4) + crc32c(4).</summary>
    public const int RecordSize = 29;

    /// <summary>Record kind byte for an add-file entry.</summary>
    public const byte KindAdd = 0x00;

    /// <summary>Record kind byte for a delete-file tombstone entry.</summary>
    public const byte KindDelete = 0x01;

    /// <summary>Record kind byte for a segment seal: the sealed segment's final byte length, frame
    /// count and checksum.</summary>
    public const byte KindSeal = 0x02;

    private const int SealedLengthOffset = 5;
    private const int FrameCountOffset = 13;
    private const int SegmentCrcOffset = 21;
    private const int RecordCrcOffset = 25;

    private readonly string _path;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly HashSet<uint> _live = [];

    // Indices whose latest record is a delete: their files may legitimately linger on disk (a crash
    // between the delete's fsync and the unlink) for the writer's sweep to remove.
    private readonly HashSet<uint> _deleted = [];
    private readonly Dictionary<uint, SegmentSeal> _seals = [];
    private FileStream? _stream;

    // 0 = live, 1 = disposed. Set once by DisposeAsync (Interlocked) and re-read after every lock
    // acquisition, so a call queued on _lock when disposal starts wakes and throws instead of
    // appending to a closing stream.
    private int _disposed;

    // Poison latch: set when a failed append could not be truncated back, leaving the tail in an
    // unknown state. Every later record is refused so torn bytes are never sealed mid-file.
    private bool _appendFaulted;

    // Test-only observability (same convention as StorageFile._directoryFsyncCount): counts the
    // directory fsyncs this instance issued, so a test can prove every directory entry it created
    // was made durable. POSIX only; Windows issues none.
    internal int _directoryFsyncCount;

    /// <summary>Opens (or creates) the manifest file at <paramref name="path"/> and replays it
    /// so <see cref="GetLiveFilesAsync"/> reflects the reconstructed live set.</summary>
    public ValueLogManifest(string path)
    {
        Guard.AgainstNullOrWhiteSpace(() => path);

        _path = path;

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            // Every directory level created here gets a durable entry: the fsync of the manifest's
            // own entry below covers only its parent, so a bare create could lose the whole new
            // subtree — the manifest and every segment it names — to a crash.
            var created = DurableFile.CreateDirectoryDurable(directory);
            if (!OperatingSystem.IsWindows())
            {
                _directoryFsyncCount += created;
            }
        }

        var exists = File.Exists(path);
        if (exists && !IsTornCreation(path))
        {
            Replay();
            return;
        }

        // A manifest torn at creation or missing holds no record, which is fresh state only when no
        // segment beside it holds frames: the header is durable before any segment exists, and a
        // segment receives frames only after its add is durable. A segment holding frames therefore
        // proves the manifest lost acknowledged records, and treating it as fresh would let the
        // writer's sweep delete them. Refuse before rewriting or creating anything.
        if (FindUnrecordedSegmentHoldingFrames(DirectoryOf(path), _live, _deleted) is { } segmentPath)
        {
            throw new CorruptValueLogException(
                fileIndex: 0,
                offset: 0,
                $"Value-log manifest {path} is {(exists ? "torn at creation (no record)" : "missing")}, but segment " +
                $"{segmentPath} beside it holds frames: the manifest lost acknowledged records. Recovery refuses to " +
                "treat it as fresh state; restore the manifest from backup.");
        }

        if (exists)
        {
            // Torn at creation: a crash between creating the file and fsyncing its header. No
            // record can exist yet (segments are recorded only once the header is durable), so
            // rewriting the header loses nothing; refusing to open would block the engine forever.
            _stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
            _stream.SetLength(0);
            WriteHeader(_stream);
            _stream.Flush(flushToDisk: true);
        }
        else
        {
            _stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            WriteHeader(_stream);
            _stream.Flush(flushToDisk: true);

            // Fsync the containing directory so the manifest's directory entry is durable.
            // The header bytes are fsync'd above, but on POSIX a crash before the directory
            // write lands can lose the whole new file — and recovery treats value-log
            // segments without a manifest as an inconsistent state. Same discipline as WAL
            // segment creation and DurableFile.AtomicReplace.
            if (!OperatingSystem.IsWindows())
            {
                DurableFile.FsyncDirectory(path);
                _directoryFsyncCount++;
            }
        }
    }

    /// <summary>
    /// Snapshot of live value-log file indices. Never null; may be empty. Acquires the manifest
    /// lock asynchronously via <c>await _lock.WaitAsync</c> — there is no blocking property getter,
    /// so a caller on a request thread never synchronously blocks behind an in-flight add/delete.
    /// </summary>
    /// <remarks>
    /// Returns a defensive copy of the live set, not a live view: a subsequent
    /// <see cref="RecordAddAsync"/>/<see cref="RecordDeleteAsync"/> does not mutate a snapshot the
    /// caller already holds.
    /// </remarks>
    public async Task<IReadOnlySet<uint>> GetLiveFilesAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return new HashSet<uint>(_live);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Records a new <c>valuelog_NNNNNN.log</c> segment. The record is fsync'd before
    /// the method returns so a crash after the append cannot lose the file's existence.</summary>
    public async Task RecordAddAsync(uint fileIndex, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_live.Contains(fileIndex))
            {
                throw new InvalidOperationException(
                    $"ValueLogManifest already has a live entry for fileIndex={fileIndex}; cannot add twice.");
            }

            AppendRecord(KindAdd, fileIndex, default);
            _live.Add(fileIndex);
            _deleted.Remove(fileIndex);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Records the tombstone for a reclaimed segment. Fsync'd before returning so
    /// the GC can safely unlink the file afterwards without risking the tombstone being lost
    /// to a crash.</summary>
    public async Task RecordDeleteAsync(uint fileIndex, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (!_live.Contains(fileIndex))
            {
                throw new InvalidOperationException(
                    $"ValueLogManifest has no live entry for fileIndex={fileIndex}; cannot delete.");
            }

            AppendRecord(KindDelete, fileIndex, default);
            _live.Remove(fileIndex);
            _seals.Remove(fileIndex);
            _deleted.Add(fileIndex);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Records the seal of a live segment the writer has finished appending to: its final byte
    /// length, frame count and segment checksum. Fsync'd before returning, so the seal is durable
    /// before the successor segment is created.
    /// </summary>
    internal async Task RecordSealAsync(uint fileIndex, SegmentSeal seal, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (!_live.Contains(fileIndex) || _seals.ContainsKey(fileIndex))
            {
                throw new InvalidOperationException(
                    $"ValueLogManifest cannot seal fileIndex={fileIndex}: it is not live or is already sealed.");
            }

            AppendRecord(KindSeal, fileIndex, seal);
            _seals[fileIndex] = seal;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Snapshot of the indices whose latest manifest record is a delete. Their segment files
    /// may linger after a crash between the delete's fsync and the unlink.</summary>
    internal async Task<IReadOnlySet<uint>> GetDeletedFilesAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return new HashSet<uint>(_deleted);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Snapshot of the recorded seals of live segments, keyed by file index.</summary>
    internal async Task<IReadOnlyDictionary<uint, SegmentSeal>> GetSealsAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return new Dictionary<uint, SegmentSeal>(_seals);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Closes the manifest stream. Idempotent. A call holding the manifest lock when disposal starts
    /// finishes first — its record is fsync'd and reported as written — and every call queued on the
    /// lock then throws <see cref="ObjectDisposedException"/>, as does every later call.
    /// </summary>
    /// <remarks>
    /// The lock <see cref="SemaphoreSlim"/> is deliberately never disposed. It allocates no kernel
    /// handle unless <see cref="SemaphoreSlim.AvailableWaitHandle"/> is read, which this type never
    /// does, and a disposed semaphore neither wakes its waiters nor accepts the holder's release.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Drain: wait for the holder to finish its append before closing the stream under it.
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_stream is not null)
            {
                await _stream.DisposeAsync().ConfigureAwait(false);
                _stream = null;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private void AppendRecord(byte kind, uint fileIndex, SegmentSeal seal)
    {
        if (_stream is null)
        {
            throw new InvalidOperationException("Manifest stream is not open.");
        }

        Span<byte> record = stackalloc byte[RecordSize];
        record.Clear();
        record[0] = kind;
        BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(1, 4), fileIndex);
        BinaryPrimitives.WriteInt64LittleEndian(record.Slice(SealedLengthOffset, 8), seal.Length);
        BinaryPrimitives.WriteInt64LittleEndian(record.Slice(FrameCountOffset, 8), seal.FrameCount);
        BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(SegmentCrcOffset, 4), seal.Checksum);

        var crc = Crc32C.HashToUInt32(record[..RecordCrcOffset]);
        BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(RecordCrcOffset, 4), crc);

        if (_appendFaulted)
        {
            throw new CorruptValueLogException(
                fileIndex: 0,
                offset: 0,
                $"Value-log manifest {_path} refuses further records: an earlier append fault could not be " +
                "repaired, so torn bytes may sit at its tail. Reopen the manifest to run recovery.");
        }

        var recordStart = _stream.Seek(0, SeekOrigin.End);
        try
        {
            _stream.Write(record);
            _stream.Flush(flushToDisk: true);
        }
        catch
        {
            // A fault mid-record (short write, fsync failure) leaves torn or unacknowledged bytes
            // at the tail. Without repair the next record seals them mid-file, where replay can
            // only fail closed; and a record that landed but was never acknowledged would be
            // replayed as live although the caller saw the append fail. Truncating back to the
            // record's start is always safe — nothing acknowledged lies after it.
            RepairTornAppendOrPoison(recordStart);
            throw;
        }
    }

    /// <summary>
    /// Truncates the manifest back to <paramref name="recordStart"/> after a failed append. If
    /// the truncation itself fails, the tail is in an unknown state and the manifest refuses
    /// every later record, the same discipline as the value-log writer.
    /// </summary>
    private void RepairTornAppendOrPoison(long recordStart)
    {
        try
        {
            _stream!.SetLength(recordStart);
            _stream.Flush(flushToDisk: true);
        }
        catch
        {
            _appendFaulted = true;
        }
    }

    /// <summary>
    /// Whether the file is a manifest whose creation a crash interrupted: no longer than the
    /// header, and holding only a strict prefix of the header this build writes, or zero bytes
    /// where the header's data never reached the disk. Any other short content is a foreign or
    /// damaged file and stays fatal in <see cref="ValidateHeader"/>.
    /// </summary>
    private static bool IsTornCreation(string path)
    {
        if (new FileInfo(path).Length > HeaderSize)
        {
            return false;
        }

        var bytes = File.ReadAllBytes(path);
        Span<byte> expected = stackalloc byte[HeaderSize];
        Magic.CopyTo(expected);
        BinaryPrimitives.WriteUInt32LittleEndian(expected[4..], CurrentFormatVersion);
        var strictPrefix = bytes.Length < HeaderSize && bytes.AsSpan().SequenceEqual(expected[..bytes.Length]);
        return strictPrefix || !bytes.AsSpan().ContainsAnyExcept((byte)0);
    }

    private static void WriteHeader(Stream stream)
    {
        stream.Write(Magic, 0, Magic.Length);
        Span<byte> version = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(version, CurrentFormatVersion);
        stream.Write(version);
    }

    private void Replay()
    {
        long goodLength;
        long readerLength;

        // Scope the read stream to the replay loop only. The trailing-bytes truncation below opens a
        // FileShare.None write stream, which requires the read handle to be fully closed first — so
        // the read stream is disposed by leaving this using block, NOT by an explicit in-block
        // Dispose() that would then be redundant with the block's own dispose (F-86).
        using (var reader = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            ValidateHeader(reader);

            Span<byte> record = stackalloc byte[RecordSize];
            goodLength = HeaderSize;
            readerLength = reader.Length;

            while (true)
            {
                var read = ReadAtMost(reader, record);
                if (read == 0)
                {
                    break;
                }

                if (read < RecordSize)
                {
                    // Trailing partial record — a torn append; truncate at the last good offset.
                    break;
                }

                var recordedCrc = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(RecordCrcOffset, 4));
                var computedCrc = Crc32C.HashToUInt32(record[..RecordCrcOffset]);
                if (recordedCrc != computedCrc)
                {
                    // The CRC authenticates every field of the record, so a complete record that
                    // fails it is damage, not a torn append — unless it is the final record and its
                    // landed bytes stop short of its end: a prefix a writer could produce followed
                    // only by zeros to end of file, the unwritten extent a crash leaves when the file
                    // length reached disk but the later record bytes did not (an all-zero record is
                    // the empty prefix). Each record is fsync'd before its append returns, so that
                    // record was never acknowledged; an acknowledged add it might have been is still
                    // caught by the directory evidence below.
                    if (goodLength + RecordSize == readerLength && IsUnwrittenRecordTail(record))
                    {
                        break;
                    }

                    throw new CorruptValueLogException(
                        fileIndex: 0,
                        offset: (ulong)goodLength,
                        $"Value-log manifest record at offset {goodLength} failed CRC check (expected=0x{recordedCrc:X8}, actual=0x{computedCrc:X8}).");
                }

                var kind = record[0];
                var fileIndex = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(1, 4));

                switch (kind)
                {
                    case KindAdd:
                        _live.Add(fileIndex);
                        _deleted.Remove(fileIndex);
                        break;
                    case KindDelete:
                        _live.Remove(fileIndex);
                        _seals.Remove(fileIndex);
                        _deleted.Add(fileIndex);
                        break;
                    case KindSeal:
                        if (!_live.Contains(fileIndex) || _seals.ContainsKey(fileIndex))
                        {
                            // The writer seals a live segment exactly once; a checksum-valid seal that
                            // contradicts the replayed history is not something it could have written.
                            throw new CorruptValueLogException(
                                fileIndex: 0,
                                offset: (ulong)goodLength,
                                $"Value-log manifest record at offset {goodLength} seals segment {fileIndex}, which is not live or is already sealed.");
                        }

                        _seals[fileIndex] = new SegmentSeal(
                            BinaryPrimitives.ReadInt64LittleEndian(record.Slice(SealedLengthOffset, 8)),
                            BinaryPrimitives.ReadInt64LittleEndian(record.Slice(FrameCountOffset, 8)),
                            BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(SegmentCrcOffset, 4)));
                        break;
                    default:
                        throw new CorruptValueLogException(
                            fileIndex: 0,
                            offset: (ulong)goodLength,
                            $"Value-log manifest record at offset {goodLength} carries unknown kind byte 0x{kind:X2}.");
                }

                goodLength += RecordSize;
            }
        }

        if (goodLength < readerLength)
        {
            ThrowIfUnrecordedSegmentHoldsFrames(goodLength);
        }

        // The read stream is now closed (using block exited). Truncate any trailing partial bytes.
        if (goodLength < readerLength)
        {
            using var truncator = new FileStream(_path, FileMode.Open, FileAccess.Write, FileShare.None);
            truncator.SetLength(goodLength);
            truncator.Flush(flushToDisk: true);
        }

        _stream = new FileStream(_path, FileMode.Open, FileAccess.Write, FileShare.Read);
        _stream.Seek(0, SeekOrigin.End);
    }

    /// <summary>
    /// Whether a CRC-failing record is the landed prefix of an interrupted write: it ends in a
    /// non-empty run of zeros, every landed CRC byte matches the CRC of the record fields before
    /// it, and a non-zero kind byte names a kind this format defines.
    /// </summary>
    private static bool IsUnwrittenRecordTail(ReadOnlySpan<byte> record)
    {
        // A zero last byte alone does not make a prefix: the record CRC's high byte is zero for
        // about one record in 256. A real prefix that reaches into the CRC carries the leading
        // bytes of the CRC over the fields before it, so landed CRC bytes that disagree with those
        // fields are damage to a complete record — the shared final-frame tear rule.
        if (!FinalFrameTearRule.IsUnwrittenChecksumTrailer(
                record[RecordCrcOffset..], Crc32C.HashToUInt32(record[..RecordCrcOffset])))
        {
            return false;
        }

        return record[0] is KindAdd or KindDelete or KindSeal;
    }

    /// <summary>
    /// Refuses to drop a torn final record that independent evidence shows was an acknowledged
    /// add: an on-disk segment holding frames beyond its header whose index the replayed records
    /// neither list as live nor record as deleted. Appends to a segment start only after its add
    /// record is durable, so such a segment's add was acknowledged; dropping the damaged record
    /// would leave it unlisted, and the writer's orphan sweep deletes unlisted segments. The
    /// evidence comes from the directory, never from the damaged bytes themselves.
    /// </summary>
    private void ThrowIfUnrecordedSegmentHoldsFrames(long tornOffset)
    {
        if (FindUnrecordedSegmentHoldingFrames(DirectoryOf(_path), _live, _deleted) is { } segmentPath)
        {
            throw new CorruptValueLogException(
                fileIndex: 0,
                offset: (ulong)tornOffset,
                $"Value-log manifest {_path} ends in a damaged record at offset {tornOffset}, but segment " +
                $"{segmentPath} holds frames and no manifest record lists or deletes it: the damaged record " +
                "was its acknowledged add. Recovery refuses to drop it, which would let the orphan sweep delete " +
                "the segment.");
        }
    }

    /// <summary>
    /// The first segment file in <paramref name="directory"/> that holds frames beyond its header while
    /// its index is neither in <paramref name="live"/> nor in <paramref name="deleted"/>, or null. Appends
    /// to a segment start only after its add record is durable, so such a segment proves the manifest
    /// lost an acknowledged add; a frameless one is the reachable crash between creation and the add.
    /// </summary>
    internal static string? FindUnrecordedSegmentHoldingFrames(
        string directory,
        IReadOnlySet<uint> live,
        IReadOnlySet<uint> deleted)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }

        foreach (var segmentPath in Directory.EnumerateFiles(directory, "valuelog_*.log"))
        {
            var name = Path.GetFileNameWithoutExtension(segmentPath);
            if (!uint.TryParse(name.AsSpan("valuelog_".Length), out var index)
                || live.Contains(index)
                || deleted.Contains(index)
                || new FileInfo(segmentPath).Length <= ValueLogFrame.HeaderSize)
            {
                continue;
            }

            return segmentPath;
        }

        return null;
    }

    private static string DirectoryOf(string path)
    {
        var directory = Path.GetDirectoryName(path);
        return string.IsNullOrEmpty(directory) ? "." : directory;
    }

    private void ValidateHeader(FileStream stream)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        var read = ReadAtMost(stream, header);
        if (read < HeaderSize)
        {
            throw new UnrecognisedValueLogFormatException(
                _path,
                $"Value-log manifest {_path} is shorter than its {HeaderSize}-byte header ({read} bytes present).");
        }

        if (!header[..4].SequenceEqual(Magic))
        {
            throw new UnrecognisedValueLogFormatException(
                _path,
                $"Value-log manifest {_path} opens with bytes 0x{header[0]:X2}{header[1]:X2}{header[2]:X2}{header[3]:X2}; expected magic 'BSVM'.");
        }

        var version = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(4, 4));
        if (version != CurrentFormatVersion)
        {
            throw new UnrecognisedValueLogFormatException(
                _path,
                $"Value-log manifest {_path} declares format_version={version}; this build accepts only version {CurrentFormatVersion}.");
        }
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

    /// <summary>
    /// The integrity metadata a seal record carries for a segment the writer finished appending to.
    /// </summary>
    /// <param name="Length">The segment's final byte length, header included.</param>
    /// <param name="FrameCount">The number of entry frames in the segment.</param>
    /// <param name="Checksum">CRC32C over the segment's bytes <c>[0, Length)</c>.</param>
    internal readonly record struct SegmentSeal(long Length, long FrameCount, uint Checksum);
}
