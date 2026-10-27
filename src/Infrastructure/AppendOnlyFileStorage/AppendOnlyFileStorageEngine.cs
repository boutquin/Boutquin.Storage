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
using Boutquin.Storage.Infrastructure.WriteAheadLog;

namespace Boutquin.Storage.Infrastructure.AppendOnlyFileStorage;

/// <summary>
/// Provides an append-only file-based storage engine with asynchronous operations.
/// </summary>
/// <typeparam name="TKey">The type of the keys in the store.</typeparam>
/// <typeparam name="TValue">The type of the values in the store.</typeparam>
/// <remarks>
/// <para>
/// <b>Why append-only?</b> This implements the append-only log pattern from "Designing Data-Intensive
/// Applications" (Kleppmann, 2e Ch. 4). Append-only writes are inherently sequential, making them much
/// faster than random-access writes on both HDDs (no seek penalty) and SSDs (aligned with write-block
/// boundaries, reduces write amplification). The trade-off is that reads require scanning the entire
/// file, which <see cref="CompactAsync"/> mitigates.
/// </para>
/// <para>
/// <b>Why does TryGetValueAsync scan the entire file?</b> Without an index, the only way to find a key
/// is a full sequential scan. This is acceptable for small files and is the simplest correct
/// implementation. For better read performance, use
/// <see cref="AppendOnlyFileStorageEngineWithIndex{TKey, TValue}"/> which maintains an in-memory
/// offset index.
/// </para>
/// <para>
/// <b>Why RemoveAsync throws NotSupportedException?</b> In an append-only log, data is never
/// overwritten or deleted in place — this would require random writes, defeating the sequential-write
/// advantage. Deletes are handled by writing tombstone markers during compaction, or by simply not
/// including the deleted key when the log is compacted.
/// </para>
/// <para>
/// <b>Concurrency contract — single mutator.</b> This engine performs no internal locking:
/// concurrent <see cref="SetAsync"/>/<see cref="SetBulkAsync"/> calls race on the shared append
/// stream and can interleave or fault. It is a single-threaded building block — compose it under
/// an external serialisation point, the way
/// <see cref="Boutquin.Storage.Infrastructure.LogSegmentFileStorage.LogSegmentedStorageEngine{TKey, TValue}"/>
/// drives its segments under one semaphore. For an internally synchronised engine, use the
/// LSM engine. Violating the contract with a SECOND live engine over the same path (a second
/// DI container, or a second process) no longer corrupts the file: each write opens its append
/// stream through <see cref="IStorageFile.Open"/>, which hands out append streams under an
/// exclusive OS lock (<see cref="FileShare.None"/>), so overlapping write windows fail fast
/// with <see cref="IOException"/> instead of overwriting each other's committed bytes.
/// Non-overlapping cross-instance writes land at the true end of the file (each open re-derives
/// it) but still violate the contract — the second instance's cached file size goes stale.
/// </para>
/// <para>
/// <b>Failure atomicity.</b> An append is acknowledged only after its fsync. If
/// <see cref="SetAsync"/> or <see cref="SetBulkAsync"/> fails or is cancelled after it opened
/// the append stream — a write, flush or fsync fault, or the caller's token firing — the file is
/// truncated back to the length it had before the call and the truncation is fsync'd, so a call
/// reported failed leaves none of its records behind, for this instance or a reopened one. If
/// that truncation also fails, the failed bytes are hidden from every read of this instance and
/// removed by its next append.
/// </para>
/// <para>
/// This class is not sealed to allow for extensibility and specialization. It serves as a base class
/// for other storage engine implementations, such as <see cref="AppendOnlyFileStorageEngineWithIndex{TKey, TValue}"/>,
/// which may add additional features or optimizations. For instance, the derived class
/// <see cref="AppendOnlyFileStorageEngineWithIndex{TKey, TValue}"/> enhances the base functionality
/// by adding indexing capabilities. Sealing this class would prevent such extensions and limit
/// the flexibility needed to create specialized storage engines that build upon the core append-only
/// functionality. Thus, to enable the creation of more advanced and feature-rich storage engine
/// implementations, this class remains unsealed.
/// </para>
/// </remarks>
public class AppendOnlyFileStorageEngine<TKey, TValue> :
    IFileBasedStorageEngine<TKey, TValue>
    where TKey : IStorageKey<TKey>, new()
    where TValue : ISerializable<TValue>, new()
{
    protected readonly IStorageFile StorageFile;
    public IEntrySerializer<TKey, TValue> EntrySerializer { get; }

    // Cached engine-visible file size. This engine is a single-mutator building block (see the
    // concurrency-contract remarks above): callers serialise their own writes, so the cache is
    // maintained without internal locking, mirroring StorageFile's own cached-size discipline (DR-9,
    // SPEC-CONCUR-01 single-threaded-by-contract). It is the bytes-written counter the segmented
    // engine's bulk/merge paths read instead of stat'ing the file once per item: seeded lazily from
    // the underlying file on first access, advanced by exactly the bytes each write appends, and reset
    // to the post-write length on compaction / to zero on clear. A negative sentinel means "not yet
    // seeded" so a fresh engine over a pre-existing log picks up its real length on first read.
    // Protected so the indexed subclass, which overrides the write paths, keeps the counter exact.
    private protected long CachedFileSize = -1;

    /// <summary>
    /// Lazily seeds the cached size from the underlying file (one stat, on first access only) and
    /// returns it. Callers serialise their own access per the single-mutator contract.
    /// </summary>
    private protected long EnsureCachedFileSize()
    {
        if (CachedFileSize < 0)
        {
            CachedFileSize = StorageFile.FileSize;
        }

        return CachedFileSize;
    }

    // True once this instance has checked the on-disk tail and truncated any torn final record, so
    // later appends land directly after the last clean record. A crash mid-append leaves a torn
    // frame at EOF; appending after it would bury the garbage mid-file, where the read path's
    // fail-loud mid-file classification then makes every read of the store throw. Single-mutator
    // contract, so no synchronisation.
    private protected bool TailVerified;

    /// <summary>
    /// Before the first append through this instance, truncates a torn final record (crash
    /// mid-append) back to the last cleanly parsed record boundary, using the read path's own
    /// torn-tail classification. Genuine mid-file corruption is never truncated: the scan throws
    /// <see cref="InvalidDataException"/> exactly as a read would.
    /// </summary>
    private protected async Task RepairTornTailBeforeAppendAsync(CancellationToken cancellationToken)
    {
        if (TailVerified)
        {
            return;
        }

        if (StorageFile.Exists())
        {
            var fileBytes = await StorageFile.ReadAllBytesAsync(cancellationToken).ConfigureAwait(false);
            long cleanEnd = 0;
            using (var scan = OpenLogScan(fileBytes))
            {
                while (scan.Position < scan.Length)
                {
                    if (!TryReadNextEntry(scan, out _))
                    {
                        break;
                    }

                    cleanEnd = scan.Position;
                }
            }

            if (cleanEnd < fileBytes.Length)
            {
                var repair = StorageFile.Open(FileMode.Open);
                await using (repair.ConfigureAwait(false))
                {
                    repair.SetLength(cleanEnd);
                    await repair.FlushAsync(cancellationToken).ConfigureAwait(false);
                    if (repair is FileStream fs)
                    {
                        fs.Flush(flushToDisk: true);
                    }
                }
            }

            CachedFileSize = cleanEnd;
        }

        // Any failed append the earlier repair could not remove sat past the last durable record,
        // so the truncation above removed it.
        FailedAppendStart = -1;
        TailVerified = true;
    }

    // Start offset of an append that was reported failed but whose bytes could not be truncated
    // away (the repair itself failed); -1 when there is none. Reads stop there, and the next append
    // truncates the file back to it before writing. Single-mutator contract, so no synchronisation.
    private protected long FailedAppendStart = -1;

    /// <summary>
    /// Opens a scan over the log bytes a reader may serve: everything before the start of an append
    /// that was reported failed but could not yet be truncated away.
    /// </summary>
    private protected MemoryStream OpenLogScan(byte[] fileBytes)
        => new(fileBytes, 0, FailedAppendStart >= 0 ? (int)Math.Min(fileBytes.Length, FailedAppendStart) : fileBytes.Length, writable: false);

    /// <summary>
    /// Removes every byte of an append that is being reported failed or cancelled: truncates the
    /// file back to <paramref name="appendStart"/>, the length it had before the append, and fsyncs
    /// the truncation, so neither this instance nor a reopened one serves the failed records — the
    /// write-ahead log's rule for a failed batch. If the truncation itself fails, the start is
    /// remembered: reads stop there and the next append truncates before writing. Called after the
    /// append stream is disposed, so no buffered byte can land after the truncation.
    /// </summary>
    private protected async Task RemoveFailedAppendAsync(long appendStart)
    {
        try
        {
            var repair = StorageFile.Open(FileMode.Open);
            await using (repair.ConfigureAwait(false))
            {
                if (repair.Length > appendStart)
                {
                    repair.SetLength(appendStart);
                }

                await repair.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                if (repair is FileStream fs)
                {
                    fs.Flush(flushToDisk: true);
                }
            }

            CachedFileSize = appendStart;
            TailVerified = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ObjectDisposedException)
        {
            // The caller rethrows the append's own failure; the bytes stay hidden until removed.
            FailedAppendStart = appendStart;
            TailVerified = false;
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AppendOnlyFileStorageEngine{TKey, TValue}"/> class.
    /// </summary>
    /// <param name="storageFile">The storage file to use for storing data.</param>
    /// <param name="entrySerializer">The serializer to use for serializing and deserializing entries.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="storageFile"/> or <paramref name="entrySerializer"/> is null.</exception>
    public AppendOnlyFileStorageEngine(
        IStorageFile storageFile,
        IEntrySerializer<TKey, TValue> entrySerializer)
    {
        Guard.AgainstNullOrDefault(() => storageFile);
        Guard.AgainstNullOrDefault(() => entrySerializer);

        StorageFile = storageFile;
        EntrySerializer = entrySerializer;
    }

    /// <inheritdoc/>
    public string FileName => StorageFile.FileName;

    /// <inheritdoc/>
    public string FileLocation => StorageFile.FileLocation;

    /// <inheritdoc/>
    /// <remarks>
    /// Served from the engine's cached byte counter, not a per-access <c>stat</c> of the underlying
    /// file. The segmented engine reads this once per item across a bulk write / small-segment merge;
    /// caching it here is what keeps those paths from issuing one stat syscall per item.
    /// </remarks>
    public long FileSize => EnsureCachedFileSize();

    /// <inheritdoc/>
    public virtual async Task SetAsync(TKey key, TValue value, CancellationToken cancellationToken = default)
    {
        Guard.AgainstNullOrDefault(() => key);
        Guard.AgainstNullOrDefault(() => value);
        cancellationToken.ThrowIfCancellationRequested();

        await RepairTornTailBeforeAppendAsync(cancellationToken).ConfigureAwait(false);

        // Seed the cache before the write so the post-write delta is correct over a pre-existing log.
        var sizeBefore = EnsureCachedFileSize();

        // Unverified while the append is in flight: a write that fails part-way leaves a torn frame
        // at EOF, and the next append must repair it rather than trust the earlier check.
        TailVerified = false;
        var rawStream = StorageFile.Open(FileMode.Append);
        var appendStart = rawStream.CanSeek ? rawStream.Length : sizeBefore;
        try
        {
            await AppendEntryAsync(rawStream, key, value, sizeBefore, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // The append is reported failed or cancelled, so none of its bytes may survive.
            await RemoveFailedAppendAsync(appendStart).ConfigureAwait(false);
            throw;
        }
    }

    // Writes and fsyncs one frame through the append stream, then disposes it.
    private async Task AppendEntryAsync(Stream rawStream, TKey key, TValue value, long sizeBefore, CancellationToken cancellationToken)
    {
        await using (rawStream.ConfigureAwait(false))
        {
            long bytesWritten;
            if (rawStream.CanSeek)
            {
                await WriteEntryAsync(rawStream, key, value, cancellationToken).ConfigureAwait(false);

                // fsync before close — acknowledge only after the write is durable on disk, matching
                // SetBulkAsync/CompactAsync. Without this the write returns while bytes sit in the OS
                // page cache; a crash would lose an acknowledged write (DDIA 2e — durable-before-ack).
                await rawStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (rawStream is FileStream fs)
                {
                    fs.Flush(flushToDisk: true);
                }

                TailVerified = true;

                // Advance the cached size by exactly the bytes appended (append stream's end offset).
                CachedFileSize = rawStream.Length;
            }
            else
            {
                // Non-seekable stream: wrap in a byte-counting proxy so we can advance the
                // cached size by the exact bytes written rather than leaving it stale at sizeBefore.
                // Without this, a following FileSize call returns the pre-write size and callers
                // that rely on CachedFileSize to skip a stat call see incorrect (too-small) values.
                var counting = new ByteCountingStream(rawStream);
                await WriteEntryAsync(counting, key, value, cancellationToken).ConfigureAwait(false);
                await counting.FlushAsync(cancellationToken).ConfigureAwait(false);
                TailVerified = true;
                bytesWritten = counting.BytesWritten;
                CachedFileSize = sizeBefore + bytesWritten;
            }
        }
    }

    /// <summary>
    /// Writes one entry to the given stream as a single checksummed frame (see the frame-format
    /// remarks on <see cref="TryReadNextEntry"/>).
    /// </summary>
    /// <param name="stream">The stream to write to.</param>
    /// <param name="key">The key to write.</param>
    /// <param name="value">The value to write.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <exception cref="OperationCanceledException">Thrown if the operation is canceled.</exception>
    protected async Task WriteEntryAsync(Stream stream, TKey key, TValue value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // The serializer writes into a private buffer, so the frame reaches the log in one write:
        // a serializer that fails part-way leaves nothing in the file.
        using var payload = new MemoryStream();
        await EntrySerializer.WriteEntryAsync(payload, key, value, cancellationToken).ConfigureAwait(false);
        var frame = EncodeFrame(payload.GetBuffer().AsSpan(0, checked((int)payload.Length)));
        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
    }

    // Frame layout, little-endian: [magic "BSAO" 4][version 1][payload length 4][payload CRC32C 4]
    // [header CRC32C 4 over the 13 bytes before it][payload][end marker 0xA5 1]. The payload is one
    // entry exactly as the configured IEntrySerializer writes it.
    private const uint FrameMagic = 0x4F_41_53_42; // 'B' 'S' 'A' 'O' in little-endian read order.
    private const byte FrameVersion = 1;
    private const int FrameHeaderSize = 17;

    // Ends every frame. It is non-zero, so whether a frame's last byte landed is decided by the
    // format, never by whether the caller's encoding happens to end in a zero byte (the shipped
    // serializer encodes a small little-endian integer, such as 0, with a trailing zero byte).
    private const byte EndMarker = 0xA5;
    private const int EndMarkerSize = 1;
    private const int FrameHeaderCoveredSize = FrameHeaderSize - sizeof(uint);
    private const int VersionOffset = 4;
    private const int PayloadLengthOffset = 5;
    private const int PayloadCrcOffset = 9;

    /// <summary>Builds one on-disk frame around <paramref name="payload"/>.</summary>
    internal static byte[] EncodeFrame(ReadOnlySpan<byte> payload)
    {
        var frame = new byte[FrameHeaderSize + payload.Length + EndMarkerSize];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, FrameMagic);
        frame[VersionOffset] = FrameVersion;
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(PayloadLengthOffset), payload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(PayloadCrcOffset), Crc32C.HashToUInt32(payload));
        BinaryPrimitives.WriteUInt32LittleEndian(
            frame.AsSpan(FrameHeaderCoveredSize), Crc32C.HashToUInt32(frame.AsSpan(0, FrameHeaderCoveredSize)));
        payload.CopyTo(frame.AsSpan(FrameHeaderSize));
        frame[^1] = EndMarker;
        return frame;
    }

    /// <summary>
    /// Reads the next frame from the log stream. Returns <see langword="false"/> at a torn tail
    /// (the interrupted final append), leaving the stream at the torn frame's start; fails loud on
    /// any other damage.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Frame format (version 1).</b> Each entry is one frame:
    /// <c>[magic "BSAO": 4][version: 1][payload length: 4][payload CRC32C: 4]
    /// [header CRC32C: 4][payload][end marker 0xA5: 1]</c>, all integers little-endian. The header
    /// checksum covers the magic, version, payload length and payload checksum, so damage to the
    /// length field is detected as corruption instead of being inferred from whether later bytes
    /// happen to parse. The payload is one entry exactly as the configured
    /// <see cref="IEntrySerializer{TKey, TValue}"/> writes it. The fixed non-zero end marker makes
    /// "the frame's last byte landed" a property of the format rather than of the caller's
    /// encoding, which may genuinely end in a zero byte.
    /// </para>
    /// <para>
    /// <b>Tear rule.</b> An append is acknowledged only after its fsync, and one fsync can cover
    /// several frames (<see cref="SetBulkAsync"/> writes the whole batch, then fsyncs once). So
    /// everything after the last durable frame boundary is unacknowledged, and it may be truncated
    /// exactly when it is consistent with one interrupted write: a strict prefix of the frames that
    /// write was laying down, followed by zero bytes where the file grew before its data landed.
    /// Frames that validate are served; the first frame that does not is a torn tail, and recovery
    /// truncates it, when (1) fewer than a header's bytes remain and they are a prefix of a frame
    /// header (the magic and version agree as far as they go), possibly followed by zero bytes;
    /// (2) a complete header fails its checksum but holds only a strict header prefix (its last
    /// byte is zero, and every checksum byte that landed matches the checksum of the header fields
    /// before it) followed by zero bytes to end-of-file; (3) the header validates and the
    /// payload and end marker it declares run past end-of-file; or (4) the header validates, the
    /// end marker is zero (it never landed), the payload before it is complete (it passes its
    /// checksum) or a prefix followed by zeros (its last byte is zero), and only zero bytes
    /// (possibly none) follow the frame to end-of-file. Any other damage — a header that fails its
    /// checksum with non-zero bytes after it, a header whose every byte landed yet fails its checksum
    /// (whatever payload follows), a frame whose end marker landed yet whose payload fails its
    /// checksum (the record was written completely and fsync'd before it was acknowledged), a
    /// payload whose last byte landed non-zero yet fails its checksum (whatever its end marker), an
    /// end marker that is neither zero nor the marker, a torn frame with non-zero bytes after it, or
    /// a checksum-valid frame the serializer cannot decode — throws
    /// <see cref="InvalidDataException"/> and nothing is truncated, so a later compaction or append
    /// can never erase the acknowledged records behind the damage.
    /// </para>
    /// <para>
    /// <b>What the format does not detect.</b> The tear rule detects crash tears and damage inside
    /// the file. Truncation of the file exactly at a frame boundary is indistinguishable from "those
    /// records were never written": the frames before the cut are all valid and nothing records
    /// how many followed them. Such truncation of fsync'd data is outside the format's threat model.
    /// Damage that only zeroes the trailing checksum bytes of the final header leaves bytes identical
    /// to a strict header prefix, so it is recovered as a torn append. Likewise, damage that only
    /// zeroes the final frame's end marker leaves bytes an interrupted append could produce, so it is
    /// recovered as a torn append; the same holds when the damage also reaches a payload whose
    /// genuine last byte is zero.
    /// </para>
    /// </remarks>
    /// <returns>
    /// <see langword="false"/> when the stream ends in a torn frame (caller stops scanning);
    /// <see langword="true"/> otherwise, with <paramref name="entry"/> carrying the decoded entry.
    /// </returns>
    /// <exception cref="InvalidDataException">The frame at the read position is damaged and is not
    /// a torn tail.</exception>
    protected bool TryReadNextEntry(Stream stream, out (TKey Key, TValue Value)? entry)
    {
        var frameStart = stream.Position;
        var remaining = stream.Length - frameStart;
        if (remaining <= 0)
        {
            entry = null;
            return false;
        }

        // The callers hold the whole log in memory; a frame is bounded by the bytes that remain.
        var rest = new byte[(int)Math.Min(remaining, FrameHeaderSize)];
        stream.ReadExactly(rest);
        if (rest.Length < FrameHeaderSize || !HeaderIsValid(rest))
        {
            stream.Position = frameStart;
            if (IsUnwrittenHeader(stream, frameStart))
            {
                entry = null;
                return false;
            }

            throw Corrupt(frameStart, "its header is incomplete or fails its checksum and is not an unwritten tail");
        }

        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(rest.AsSpan(PayloadLengthOffset));
        if (payloadLength < 0)
        {
            throw Corrupt(frameStart, $"its checksum-valid header declares a negative payload length {payloadLength}");
        }

        var frameEnd = frameStart + FrameHeaderSize + (long)payloadLength + EndMarkerSize;
        if (frameEnd > stream.Length)
        {
            // Valid header, payload or end marker short at end-of-file: the interrupted final append.
            stream.Position = frameStart;
            entry = null;
            return false;
        }

        var payload = new byte[payloadLength];
        stream.ReadExactly(payload);
        var marker = stream.ReadByte();
        var payloadIntact = Crc32C.HashToUInt32(payload) == BinaryPrimitives.ReadUInt32LittleEndian(rest.AsSpan(PayloadCrcOffset));
        if (marker != EndMarker || !payloadIntact)
        {
            // An interrupted write — of this frame or of the bulk batch it opens — leaves a prefix
            // of the frame followed by zeros: the end marker is still zero, and the payload before it
            // is complete (its checksum passes) or a prefix followed by zeros (its last byte is
            // zero). A frame whose end marker landed, or whose payload's last byte landed non-zero,
            // was written completely and fsync'd before its append was acknowledged, so a checksum
            // failure there is damage, never a tear (tear-rule case 4).
            if (marker != 0 || !(payloadIntact || FinalFrameTearRule.IsUnwrittenSuffix(payload, ReadOnlySpan<byte>.Empty)))
            {
                throw Corrupt(frameStart, "its payload fails its checksum or its end marker is wrong although its last byte landed");
            }

            // Only zero bytes follow: the rest of the interrupted write's extent never landed.
            if (IsZeroToEnd(stream))
            {
                stream.Position = frameStart;
                entry = null;
                return false;
            }

            throw Corrupt(frameStart, "its payload fails its checksum or its end marker is missing and non-zero bytes follow it");
        }

        entry = DecodePayload(payload, frameStart);
        return true;
    }

    /// <summary>
    /// Decodes one complete frame read back by offset (the indexed point read). The bytes must be
    /// exactly one valid frame; anything else is corruption, never a torn tail, because the index
    /// only ever records offsets of durably written frames.
    /// </summary>
    private protected (TKey Key, TValue Value) DecodeFrameAt(byte[] frame, long frameOffset)
    {
        if (frame.Length < FrameHeaderSize || !HeaderIsValid(frame))
        {
            throw Corrupt(frameOffset, "its header fails its checksum");
        }

        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(PayloadLengthOffset));
        if (payloadLength < 0 || FrameHeaderSize + (long)payloadLength + EndMarkerSize != frame.Length)
        {
            throw Corrupt(frameOffset, "its declared payload length does not match the indexed frame extent");
        }

        var payload = frame.AsSpan(FrameHeaderSize, payloadLength);
        if (Crc32C.HashToUInt32(payload) != BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(PayloadCrcOffset))
            || frame[^1] != EndMarker)
        {
            throw Corrupt(frameOffset, "its payload fails its checksum or its end marker is wrong");
        }

        return DecodePayload(payload.ToArray(), frameOffset);
    }

    /// <summary>
    /// True when every byte from the stream's position to its end is zero (vacuously true at
    /// end-of-stream). Scanned in bounded chunks; the position is left at the end.
    /// </summary>
    private static bool IsZeroToEnd(Stream stream)
    {
        var chunk = new byte[Math.Min(Math.Max(stream.Length - stream.Position, 1), 1 << 16)];
        int read;
        while ((read = stream.Read(chunk)) > 0)
        {
            if (chunk.AsSpan(0, read).ContainsAnyExcept((byte)0))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HeaderIsValid(ReadOnlySpan<byte> header)
        => BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(FrameHeaderCoveredSize)) ==
               Crc32C.HashToUInt32(header.Slice(0, FrameHeaderCoveredSize)) &&
           BinaryPrimitives.ReadUInt32LittleEndian(header) == FrameMagic &&
           header[VersionOffset] == FrameVersion;

    /// <summary>
    /// True when the bytes from <paramref name="frameStart"/> to end-of-file are a strict prefix of
    /// a frame header followed only by zero bytes: an append whose header never fully reached disk
    /// (tear-rule cases 1 and 2). The header's last byte must be zero (a header whose every byte
    /// landed would pass its checksum, so a complete header that fails is damage, not a tear), any
    /// checksum bytes that landed must match the checksum of the header fields before them, the
    /// magic and version bytes present must agree with a real header, and every byte after the
    /// header region must be zero.
    /// </summary>
    private static bool IsUnwrittenHeader(Stream stream, long frameStart)
    {
        var headerRegion = new byte[(int)Math.Min(stream.Length - frameStart, FrameHeaderSize)];
        stream.ReadExactly(headerRegion);

        // Every byte past the header region must be zero; scanned in bounded chunks.
        var chunk = new byte[Math.Min(stream.Length - stream.Position, 1 << 16)];
        int read;
        while ((read = stream.Read(chunk)) > 0)
        {
            if (chunk.AsSpan(0, read).ContainsAnyExcept((byte)0))
            {
                stream.Position = frameStart;
                return false;
            }
        }

        stream.Position = frameStart;

        // An interrupted append leaves a strict prefix of the header, so its last byte is zero. A
        // header whose every byte landed and still fails its checksum is damage to an fsync'd,
        // acknowledged record — even when only zeros (a payload the serializer encoded as zeros)
        // follow it.
        var nonZeroEnd = headerRegion.AsSpan().LastIndexOfAnyExcept((byte)0) + 1;
        if (nonZeroEnd == FrameHeaderSize)
        {
            return false;
        }

        // A zero last byte alone does not make a prefix: the header checksum's high byte is zero for
        // about one frame in 256. A real prefix that reaches into the checksum carries the leading
        // bytes of the checksum over the fields before it, so landed checksum bytes that disagree
        // with those fields are damage to a complete header.
        if (nonZeroEnd > FrameHeaderCoveredSize)
        {
            Span<byte> crc = stackalloc byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32LittleEndian(crc, Crc32C.HashToUInt32(headerRegion.AsSpan(0, FrameHeaderCoveredSize)));
            if (!headerRegion.AsSpan(FrameHeaderCoveredSize, nonZeroEnd - FrameHeaderCoveredSize)
                    .SequenceEqual(crc[..(nonZeroEnd - FrameHeaderCoveredSize)]))
            {
                return false;
            }
        }

        // The non-zero part of the header region must agree with a real header's magic and version.
        Span<byte> expected = stackalloc byte[VersionOffset + 1];
        BinaryPrimitives.WriteUInt32LittleEndian(expected, FrameMagic);
        expected[VersionOffset] = FrameVersion;
        for (var i = 0; i < Math.Min(nonZeroEnd, expected.Length); i++)
        {
            if (headerRegion[i] != expected[i])
            {
                return false;
            }
        }

        return true;
    }

    private (TKey Key, TValue Value) DecodePayload(byte[] payload, long frameOffset)
    {
        (TKey Key, TValue Value)? entry;
        using var stream = new MemoryStream(payload, writable: false);
        try
        {
            entry = EntrySerializer.ReadEntry(stream);
        }
        catch (Exception ex) when (ex is EndOfStreamException or DeserializationException or InvalidDataException
            or UnsupportedFormatVersionException or OverflowException or FormatException)
        {
            throw Corrupt(frameOffset, $"its checksum-valid payload cannot be decoded ({ex.GetType().Name})", ex);
        }

        if (entry is null || stream.Position != payload.Length)
        {
            throw Corrupt(frameOffset, "its checksum-valid payload is not exactly one entry");
        }

        return entry.Value;
    }

    private InvalidDataException Corrupt(long frameOffset, string reason, Exception? inner = null)
        => new(
            $"Append-only log {StorageFile.FileName} has a damaged frame at offset {frameOffset}: {reason}. " +
            "This is not a torn tail; treating it as end-of-log would silently drop durably-acknowledged records.",
            inner);

    /// <inheritdoc/>
    public virtual async Task<(TValue Value, bool Found)> TryGetValueAsync(TKey key, CancellationToken cancellationToken = default)
    {
        Guard.AgainstNullOrDefault(() => key);
        cancellationToken.ThrowIfCancellationRequested();

        // A missing file (fresh store, or post-ClearAsync) is an empty store, not an error.
        if (!StorageFile.Exists())
        {
            return (default!, false);
        }

        var fileBytes = await StorageFile.ReadAllBytesAsync(cancellationToken).ConfigureAwait(false);

        // Why scan the entire file and keep the last match? In an append-only log, later entries
        // supersede earlier ones (last-writer-wins). When a key is written multiple times, the last
        // occurrence reflects the current state. Returning the first match would return stale data.
        var result = (Value: default(TValue)!, Found: false);
        using (var stream = OpenLogScan(fileBytes))
        {
            while (stream.Position < stream.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryReadNextEntry(stream, out var entry))
                {
                    break;
                }
                if (entry.HasValue && entry.Value.Key.CompareTo(key) == 0)
                {
                    result = (entry.Value.Value, true);
                }
            }
        }

        return result;
    }

    /// <inheritdoc/>
    public async Task<bool> ContainsKeyAsync(TKey key, CancellationToken cancellationToken = default)
    {
        var (_, found) = await TryGetValueAsync(key, cancellationToken).ConfigureAwait(false);
        return found;
    }

    /// <inheritdoc/>
    public Task RemoveAsync(TKey key, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("Remove operation is not supported in an append-only storage engine.");
    }

    /// <inheritdoc/>
    public virtual async Task SetBulkAsync(IEnumerable<KeyValuePair<TKey, TValue>> items, CancellationToken cancellationToken = default)
    {
        Guard.AgainstEmptyOrNullEnumerable(() => items);
        cancellationToken.ThrowIfCancellationRequested();

        // Validate-before-apply: reject the WHOLE batch before writing its first byte, so a later
        // invalid item can never leave earlier items of the rejected call in the log.
        var batch = items.ToList();
        foreach (var item in batch)
        {
            Guard.AgainstNullOrDefault(() => item.Key);
            Guard.AgainstNullOrDefault(() => item.Value);
        }

        await RepairTornTailBeforeAppendAsync(cancellationToken).ConfigureAwait(false);

        var sizeBefore = EnsureCachedFileSize();

        // Unverified while the append is in flight (see SetAsync).
        TailVerified = false;
        var stream = StorageFile.Open(FileMode.Append);
        var appendStart = stream.CanSeek ? stream.Length : sizeBefore;
        try
        {
            await AppendBatchAsync(stream, batch, sizeBefore, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Every item of the batch is reported failed, so none of its bytes may survive.
            await RemoveFailedAppendAsync(appendStart).ConfigureAwait(false);
            throw;
        }
    }

    // Writes the whole batch through the append stream, fsyncs it once, then disposes the stream.
    private async Task AppendBatchAsync(Stream stream, List<KeyValuePair<TKey, TValue>> batch, long sizeBefore, CancellationToken cancellationToken)
    {
        await using (stream.ConfigureAwait(false))
        {
            foreach (var item in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();

                await WriteEntryAsync(stream, item.Key, item.Value, cancellationToken).ConfigureAwait(false);
            }

            // fsync before close: ensure the bulk write is durable on disk before the caller
            // (typically an LSM flush) truncates its WAL. Without this, a crash between close
            // and OS flush would lose the segment data while the WAL is already gone.
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (stream is FileStream fs)
            {
                fs.Flush(flushToDisk: true);
            }

            TailVerified = true;

            // The append stream's end offset is the new file length — update the cache so a following
            // FileSize read serves from memory.
            CachedFileSize = stream.CanSeek ? stream.Length : sizeBefore;
        }
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<(TKey Key, TValue Value)>> GetAllItemsAsync(CancellationToken cancellationToken = default)
    {
        var items = new List<(TKey Key, TValue Value)>();
        cancellationToken.ThrowIfCancellationRequested();

        // A missing file (fresh store, or post-ClearAsync) is an empty store, not an error.
        if (!StorageFile.Exists())
        {
            return items;
        }

        var fileBytes = await StorageFile.ReadAllBytesAsync(cancellationToken).ConfigureAwait(false);

        using var stream = OpenLogScan(fileBytes);
        while (stream.Position < stream.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryReadNextEntry(stream, out var entry))
            {
                break;
            }
            if (entry.HasValue)
            {
                items.Add(entry.Value);
            }
        }

        return items;
    }

    /// <inheritdoc/>
    public virtual Task ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StorageFile.Delete(FileDeletionHandling.DeleteIfExists);
        // The log is gone; the next append (which re-creates the file) starts from zero.
        CachedFileSize = 0;
        FailedAppendStart = -1;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public virtual async Task CompactAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var items = await GetAllItemsAsync(cancellationToken).ConfigureAwait(false);
        // Why g.Last() not g.First()? In an append-only log, later entries supersede earlier ones
        // (last-writer-wins semantics). When a key is written multiple times, the last occurrence
        // reflects the current state. Compaction deduplicates by keeping only the latest value for each key.
        var latestItems = items
            .GroupBy(x => x.Key)
            .Select(g => g.Last())
            .ToList();

        // Serialize the compacted entries fully in memory, then replace the file atomically.
        // The previous FileMode.Create path truncated the live file in place before rewriting, so a
        // crash mid-compaction left a truncated/partial file. WriteAllBytesAsync writes a temp file,
        // fsyncs it, and renames it over the target — a crash leaves the whole pre-compaction file
        // intact or the whole compacted file, never a partial one.
        using var buffer = new MemoryStream();
        foreach (var (Key, Value) in latestItems)
        {
            await WriteEntryAsync(buffer, Key, Value, cancellationToken).ConfigureAwait(false);
        }

        var compacted = buffer.ToArray();
        await StorageFile.WriteAllBytesAsync(compacted, cancellationToken).ConfigureAwait(false);
        // The compacted file replaced the old one wholesale; its size is the buffer length, and no
        // failed append's bytes remain in it.
        CachedFileSize = compacted.Length;
        FailedAppendStart = -1;
    }

    /// <summary>
    /// A write-only pass-through stream that counts the total bytes written. Used by
    /// <see cref="SetAsync"/> to advance <c>CachedFileSize</c> correctly on the rare
    /// non-seekable-stream path, where the underlying stream's <c>Length</c> is unavailable.
    /// </summary>
    private sealed class ByteCountingStream(Stream inner) : Stream
    {
        public long BytesWritten { get; private set; }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
            BytesWritten += count;
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            inner.Write(buffer);
            BytesWritten += buffer.Length;
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
            BytesWritten += count;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            BytesWritten += buffer.Length;
        }

        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
