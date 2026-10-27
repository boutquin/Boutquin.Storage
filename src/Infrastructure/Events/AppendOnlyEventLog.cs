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
using System.Text.Json;

using Boutquin.Storage.Domain.Interfaces.Events;
using Boutquin.Storage.Infrastructure.KeyValueStore;
using Boutquin.Storage.Infrastructure.Schemas;
using Boutquin.Storage.Infrastructure.WriteAheadLog;

namespace Boutquin.Storage.Infrastructure.Events;

/// <summary>
/// File-backed append-only event log with magic-tagged frames, a header checksum over every
/// header field, a payload checksum, and fsync durability. Backed by a long-lived writer
/// stream reused across appends (DDIA 2nd ed. Ch. 4 on framing and durable log append).
///
/// <para><b>On-disk frame (version 1):</b>
/// <c>[4B magic "BSEL"] [1B version=1] [8B position (LE int64)] [4B payload length (LE int32)]
///    [4B payload CRC32C (LE uint32)] [4B header CRC32C (LE uint32) over the 21 bytes before it]
///    [N B payload]</c>.
/// The header checksum covers the magic, version, position, payload length and payload
/// checksum, so damage to the length field is detected as corruption instead of inferred from
/// whether later bytes happen to parse.</para>
///
/// <para><b>Corruption policy — fail closed on mid-log loss, recover the torn tail.</b>
/// <c>IEventLog</c> assigns gap-free positions, so a reader that silently skipped a corrupt
/// MID-LOG frame would hand stream/CDC consumers a non-prefix history (a lost change they can
/// never detect). Each append writes and fsyncs one whole frame, so only the final frame can be
/// partial, and the tear rule is deterministic: the frame at the read position is a torn tail
/// when (1) fewer than a header's bytes remain and they are a prefix of a frame header, possibly
/// followed by zero bytes; (2) a complete header fails its checksum but holds only a strict
/// header prefix (its last byte is zero, and every checksum byte that landed matches the
/// checksum of the header fields before it) followed by zero bytes to the end of the scanned range
/// (the file grew before its data landed); (3) the header validates and its payload runs past
/// the end; or (4) the header validates, the payload ends exactly at the end, fails its
/// checksum and its last byte is zero (a payload prefix followed by zero bytes; a serialized
/// event never ends in a zero byte). Reads treat a torn tail as end-of-log, and construction
/// trims the file back to the end of the last valid frame so subsequent appends never bury
/// garbage inside the log. Any other damage — including a header whose every byte landed yet
/// fails its checksum, whatever follows it, and a final payload whose last byte landed yet fails
/// its checksum — throws <see cref="InvalidDataException"/> from reads and from the recovering
/// constructor. A checksum-valid frame that does not decode (or decodes to null) is foreign or
/// forged bytes and throws <see cref="InvalidDataException"/> from reads.</para>
///
/// <para><b>What the format does not detect.</b> The tear rule detects crash tears and damage
/// inside the file. Truncation of the file exactly at a frame boundary is indistinguishable from
/// "those events were never written": the frames before the cut are all valid and nothing records
/// how many followed them. Such truncation of fsync'd data is outside the format's threat
/// model. Damage that only zeroes the trailing checksum bytes of the final header leaves bytes
/// identical to a strict header prefix, so it is recovered as a torn append; damage that only
/// zeroes the trailing bytes of the final payload likewise reads as a payload prefix.</para>
///
/// <para><b>Multi-writer coordination — every append is serialized through an OS-level
/// exclusive lock.</b> More than one instance may legitimately write the same log file within
/// one process (e.g. two DI containers each holding a "singleton") or across processes.
/// A <see cref="FileStream"/> opened for appending tracks its file offset independently of the
/// file's live length, and on Unix <see cref="FileShare"/> alone provides no write exclusion —
/// so uncoordinated writers overwrite each other's committed frames (observed in the field as
/// a frame with its leading magic bytes sheared off, which readers then classify as mid-log
/// corruption). Each append therefore (1) acquires an exclusive lock on the sidecar file
/// <c>&lt;log&gt;.lock</c> (<see cref="FileShare.None"/>, which maps to a mandatory sharing
/// violation on Windows and an OS <c>flock</c> on Unix — both exclude other handles in the
/// same process as well as other processes), (2) re-derives the true end-of-log offset and
/// next position when another writer has advanced or trimmed the file since this instance's
/// last append, and (3) writes the frame at the live end. The sidecar file is never deleted:
/// unlinking it while another writer waits on it would hand the waiter a lock on a dead inode.
/// Startup recovery holds the same lock around its scan-and-trim, so it can never truncate a
/// frame another live writer just committed.</para>
/// </summary>
/// <typeparam name="TEvent">The event type.</typeparam>
public sealed class AppendOnlyEventLog<TEvent> : IEventLog<TEvent>, IDisposable
{
    // Frame magic — the ASCII bytes "BSEL" identifying a Boutquin Storage Event Log frame.
    private const uint FrameMagic = 0x4C_45_53_42; // 'B' 'S' 'E' 'L' in little-endian read order.
    private const byte FrameVersion = 1;

    private const int MagicSize = 4;
    private const int VersionSize = 1;
    private const int PositionSize = 8;
    private const int LengthPrefixSize = 4;
    private const int ChecksumSize = 4;
    private const int PositionOffset = MagicSize + VersionSize;
    private const int PayloadLengthOffset = PositionOffset + PositionSize;
    private const int PayloadCrcOffset = PayloadLengthOffset + LengthPrefixSize;
    private const int HeaderCrcOffset = PayloadCrcOffset + ChecksumSize;
    private const int HeaderSize = HeaderCrcOffset + ChecksumSize;

    // Polling interval while waiting for another writer to release the sidecar append lock.
    // Contention is rare (appends are fsync-bound) and short-lived, so a small fixed delay
    // beats platform-specific blocking-lock plumbing.
    private static readonly TimeSpan s_appendLockRetryDelay = TimeSpan.FromMilliseconds(2);

    private readonly string _filePath;
    private readonly string _appendLockPath;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly JsonSerializerOptions? _jsonOptions;
    private FileStream? _writeStream;
    private long _nextPosition;

    // End offset of the last frame this instance knows to be committed. When the file's live
    // length disagrees at append time, another cooperating writer has appended (or a recovery
    // has trimmed a torn tail) since our last write — triggering re-derivation before we
    // choose a write offset and position.
    private long _committedEndOffset;

    // An offset this instance has seen to be the end of a valid frame (0 for an empty log). Every
    // byte before it belongs to frames that no writer ever trims or rewrites, so a reader can
    // classify the log's tail from here instead of from the start. Read and written only while
    // holding the sidecar append lock; Interlocked keeps the 64-bit value whole across threads.
    private long _knownFrameEnd;
    private int _disposed;

    // Test-only fault-injection seam (InternalsVisibleTo). When set, it is used in place of the
    // single durable frame write so a test can simulate a torn write — bytes partially reaching
    // disk before an I/O fault — and assert the append's crash-atomicity recovery truncates the
    // partial frame. Null on the production path (the real single WriteAsync runs instead).
    internal Func<Stream, byte[], CancellationToken, ValueTask>? _frameWriteFaultHook;

    // Test-only observability (InternalsVisibleTo). Counts how many times the first-creation
    // directory fsync ran, so a test can pin that the durability step fires exactly once on the
    // append that creates the file and never on subsequent appends.
    internal int _directoryFsyncCount;

    /// <summary>
    /// Creates and fully initialises an <see cref="AppendOnlyEventLog{TEvent}"/>, recovering
    /// the write position from any existing log file.
    ///
    /// <para>Recovery runs asynchronously inside this factory so the caller is never blocked on
    /// the file-system scan under a single-threaded <see cref="System.Threading.SynchronizationContext"/>
    /// (e.g. ASP.NET Core request context, WPF dispatcher, Unity main thread). The previous
    /// synchronous constructor ran <c>.MoveNextAsync().AsTask().GetAwaiter().GetResult()</c>
    /// from within the constructor body — a pattern that deadlocks under a single-threaded
    /// context that does not pump continuations while blocked.</para>
    /// </summary>
    /// <param name="filePath">Path to the log file.</param>
    /// <param name="jsonOptions">Optional JSON serializer options.</param>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>A fully-initialised event log with its write position recovered.</returns>
    public static async Task<AppendOnlyEventLog<TEvent>> CreateAsync(
        string filePath,
        JsonSerializerOptions? jsonOptions = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            // Durable creation — see DurableFile.CreateDirectoryDurable: the first-append fsync
            // covers only the log file's entry in its own directory, not freshly created ancestors.
            _ = DurableFile.CreateDirectoryDurable(dir);
        }

        var instance = new AppendOnlyEventLog<TEvent>(filePath, jsonOptions);
        (instance._nextPosition, instance._committedEndOffset) =
            await instance.RecoverPositionAsync(ct).ConfigureAwait(false);
        return instance;
    }

    // Private constructor: side-effect-free field initialisation only.
    // All async startup work (position recovery) runs in CreateAsync.
    private AppendOnlyEventLog(string filePath, JsonSerializerOptions? jsonOptions)
    {
        _filePath = filePath;
        _appendLockPath = filePath + ".lock";
        _jsonOptions = jsonOptions;
    }

    /// <inheritdoc />
    public async ValueTask<long> AppendAsync(TEvent @event, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed == 1, this);

        // A null event frames the JSON literal null, which the reader can never return: the frame
        // is CRC-valid, so it would survive every reopen and make each replay through its
        // position throw. Reject it before any position is acknowledged. (`is null` rather than
        // ThrowIfNull so a value-type TEvent is not boxed on the hot append path.)
        if (@event is null)
        {
            throw new ArgumentNullException(nameof(@event));
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(@event, _jsonOptions);

        // The same hazard for any event the reader cannot decode — for example an EventEnvelope
        // carrying default(SchemaVersion), whose Value 0 the SchemaVersion init accessor refuses on
        // read. Decode the exact payload with the reader's own call and options, so a frame is
        // acknowledged only if every later replay of it can return an event — and the event that
        // was appended: JSON writes an unpaired surrogate as U+FFFD, so two distinct appended keys
        // would otherwise replay as one.
        EnsureReadable(@event, payload, nameof(@event));

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed == 1, this);

            // Cross-writer exclusion. Held for the whole frame write (offset derivation
            // through durable flush) so no other instance — in this process or another —
            // can interleave a write or a recovery trim mid-frame.
            using var appendLock = await AcquireAppendLockAsync(ct).ConfigureAwait(false);

            // Lazily open the persistent writer stream on first append. Reuse it for every
            // subsequent append — no open/close per write, no syscall amplification on the
            // hot write path. Durable via Flush(flushToDisk: true) below.
            if (_writeStream is null)
            {
                // Did the log file already exist before we opened it? If not, this open CREATES a
                // new directory entry — and the entry itself is not durable until the containing
                // directory's inode is fsync'd. Without that, a crash after the frame flush yet
                // before the directory write lands can lose the whole new file, dropping appends
                // the engine already acknowledged. Mirrors WriteAheadLog's first-segment create.
                //
                // OpenOrCreate + ReadWrite (not FileMode.Append): an append-mode stream pins its
                // write offset independently of the file's live length, so a frame committed by
                // another cooperating writer would be overwritten at this stream's stale offset.
                // The write offset is chosen explicitly per append instead, under the sidecar
                // lock. FileShare.ReadWrite because exclusion lives in the sidecar lock, not in
                // share modes (which Unix does not enforce for write access anyway).
                //
                // An EMPTY existing file is fsync'd the same way: it is what a crash before the
                // fsync, or an append whose fsync threw, leaves behind, and no append has yet made
                // its entry durable. The stream is published only after that fsync succeeds, so a
                // failed fsync is retried by the next append instead of being skipped for good.
                var fileIsNew = !File.Exists(_filePath);

                var writeStream = new FileStream(
                    _filePath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.ReadWrite,
                    bufferSize: 4096,
                    FileOptions.None);

                try
                {
                    if ((fileIsNew || writeStream.Length == 0) && !OperatingSystem.IsWindows())
                    {
                        DurableFile.FsyncDirectory(_filePath);
                        _directoryFsyncCount++;
                    }
                }
                catch
                {
                    await writeStream.DisposeAsync().ConfigureAwait(false);
                    throw;
                }

                _writeStream = writeStream;
            }

            // Another cooperating writer may have appended frames — or a startup recovery may
            // have trimmed a torn tail — since this instance's last write. Re-derive the true
            // end offset and next position from the file before choosing either.
            var liveLength = RandomAccess.GetLength(_writeStream.SafeFileHandle);
            if (liveLength != _committedEndOffset)
            {
                await ResynchronizeWithFileAsync(liveLength, ct).ConfigureAwait(false);
            }

            var position = _nextPosition;

            // Assemble the WHOLE frame in one buffer and write it in a single durable operation:
            // separate writes gave cancellation/fault a mid-frame window, and a torn partial frame
            // left ahead of the next append would be buried mid-log as permanent corruption.
            var frame = EncodeFrame(position, payload);

            // Crash-atomicity: capture the committed length BEFORE writing. If the write or the
            // durable flush faults (or is cancelled) after some bytes reached disk, truncate back
            // to this boundary so a partial frame is never observable by the reader. The corrected
            // invariant: a faulted/cancelled append leaves the log at exactly its pre-append length.
            var committedLength = _committedEndOffset;
            _writeStream.Position = committedLength;
            try
            {
                if (_frameWriteFaultHook is { } hook)
                {
                    await hook(_writeStream, frame, ct).ConfigureAwait(false);
                }
                else
                {
                    await _writeStream.WriteAsync(frame, ct).ConfigureAwait(false);
                }

                // Sync flush: .NET 10 exposes no FlushAsync(flushToDisk: true) overload — the
                // asynchronous FileStream.FlushAsync() only flushes the in-process buffer to the
                // OS page cache, not to durable storage.  Durable-before-ack requires the
                // synchronous Flush(flushToDisk: true) call.  This is a platform constraint,
                // not an oversight; callers must supply a stream backed by a real durable device
                // (not an in-memory stream) for the durability guarantee to hold.
                _writeStream.Flush(flushToDisk: true);
            }
            catch
            {
                // Roll the stream back to the last committed frame boundary so no partial frame
                // survives to poison the next append. Best-effort: if the stream itself is dead,
                // recovery on next open still trims the torn tail (it has no valid frame after it).
                try
                {
                    _writeStream.SetLength(committedLength);
                    _writeStream.Flush(flushToDisk: true);
                }
                catch
                {
                    // Swallow rollback failures — the original fault is the meaningful one to
                    // surface, and torn-tail recovery on reopen is the backstop.
                }

                throw;
            }

            _nextPosition = position + 1;
            _committedEndOffset = committedLength + frame.Length;
            Interlocked.Exchange(ref _knownFrameEnd, _committedEndOffset);
            return position;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Acquires the OS-level exclusive append lock by opening the sidecar
    /// <c>&lt;log&gt;.lock</c> file with <see cref="FileShare.None"/> — a mandatory sharing
    /// violation for any other handle on Windows, an exclusive <c>flock</c> on Unix; both
    /// exclude other handles within the same process as well as other processes. Polls on
    /// contention (appends are fsync-bound, so waits are short). Disposing the returned
    /// stream releases the lock; the sidecar file itself is deliberately never deleted
    /// (unlinking it would hand a concurrent waiter a lock on a dead inode).
    /// </summary>
    private async ValueTask<FileStream> AcquireAppendLockAsync(CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    _appendLockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.None);
            }
            catch (IOException ex) when (IsLockHeldByAnotherHandle(ex))
            {
                // Another writer holds the lock — back off briefly and retry. Every other I/O
                // fault (missing directory, disk full, read-only volume, …) propagates: waiting
                // cannot clear it, and retrying would hang the append or recovery indefinitely.
                await Task.Delay(s_appendLockRetryDelay, ct).ConfigureAwait(false);
            }
        }
    }

    // True only for the failure FileShare.None produces when another handle holds the sidecar:
    // a sharing/lock violation on Windows; on Unix, .NET reports the refused non-blocking flock as
    // a plain IOException whose HResult is the raw errno EWOULDBLOCK (11 on Linux, 35 on
    // macOS/BSD). IOException subtypes (DirectoryNotFound, FileNotFound, PathTooLong, …) never
    // signal contention.
    private static bool IsLockHeldByAnotherHandle(IOException ex)
    {
        if (ex.GetType() != typeof(IOException))
        {
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            const int ErrorSharingViolation = unchecked((int)0x80070020);
            const int ErrorLockViolation = unchecked((int)0x80070021);
            return ex.HResult is ErrorSharingViolation or ErrorLockViolation;
        }

        var ewouldblock = OperatingSystem.IsLinux() || OperatingSystem.IsAndroid() ? 11 : 35;
        return ex.HResult == ewouldblock;
    }

    /// <summary>
    /// Re-derives <see cref="_nextPosition"/> and <see cref="_committedEndOffset"/> from the
    /// file after another cooperating writer changed it. Called only while holding both the
    /// instance write lock and the sidecar append lock. Scans forward from the last offset
    /// this instance knows to be committed (or from the start, when the file shrank — another
    /// instance's recovery trimmed a torn tail). A torn tail found here can only belong to a
    /// crashed writer — no live cooperating writer can be mid-append while we hold the append
    /// lock — so it is trimmed before our frame is chosen an offset; appending behind garbage
    /// would otherwise bury it as permanent mid-log corruption. Mid-log corruption (a valid
    /// frame after damage) fails closed with <see cref="InvalidDataException"/> instead of
    /// appending to a log whose history is already non-prefix.
    /// </summary>
    private async Task ResynchronizeWithFileAsync(long liveLength, CancellationToken ct)
    {
        var scanFrom = liveLength < _committedEndOffset ? 0 : _committedEndOffset;

        long maxPosition = scanFrom == 0 ? -1 : _nextPosition - 1;
        var lastValidEnd = scanFrom;

        using (var fs = new FileStream(
            _filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            bufferSize: 1 << 16, useAsync: true))
        {
            fs.Position = scanFrom;
            await foreach (var frame in EnumerateFramesAsync(fs, fs.Length, ct).ConfigureAwait(false))
            {
                if (frame.Position > maxPosition)
                {
                    maxPosition = frame.Position;
                }

                lastValidEnd = frame.EndOffset;
            }
        }

        if (lastValidEnd < liveLength)
        {
            _writeStream!.SetLength(lastValidEnd);
            _writeStream.Flush(flushToDisk: true);
        }

        _nextPosition = maxPosition + 1;
        _committedEndOffset = lastValidEnd;
    }

    // Runs ReadFromAsync's decode on a payload before it is framed. Any failure, a null result, or a
    // result that differs from the appended event is a frame the reader could never return as
    // appended, so the append is refused as an invalid argument and no position is consumed.
    private void EnsureReadable(TEvent original, byte[] payload, string paramName)
    {
        TEvent? decoded;
        try
        {
            decoded = JsonSerializer.Deserialize<TEvent>(payload, _jsonOptions);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new ArgumentException(
                $"Event of type {typeof(TEvent)} cannot be read back from its serialized form ({ex.GetType().Name}: {ex.Message}); " +
                "appending it would acknowledge a frame every replay fails on.",
                paramName,
                ex);
        }

        if (decoded is null)
        {
            throw new ArgumentException(
                $"Event of type {typeof(TEvent)} serializes to a form that reads back as null; " +
                "appending it would acknowledge a frame every replay fails on.",
                paramName);
        }

        var difference = JsonRoundTripIdentity.FindDifference(original, decoded, _jsonOptions);
        if (difference is not null)
        {
            throw new ArgumentException(
                $"Event of type {typeof(TEvent)} does not replay as the event appended ({difference}); " +
                "appending it would acknowledge a frame every replay returns altered.",
                paramName);
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<(long Position, TEvent Event)> ReadFromAsync(
        long position, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed == 1, this);
        ArgumentOutOfRangeException.ThrowIfNegative(position);

        if (!File.Exists(_filePath))
        {
            yield break;
        }

        // Stream frame-by-frame rather than slurping the whole file into memory (finding #14):
        // an append-only event log is unbounded, so a per-call `new byte[fs.Length]` both caps the
        // log at int.MaxValue bytes and allocates the entire history on every read. The reader
        // below holds at most one frame's bytes at a time.
        using var fs = new FileStream(
            _filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            bufferSize: 1 << 16, useAsync: true);

        // Bound the read by the ACKNOWLEDGED end of the log. An appender's frame bytes reach the
        // OS before its fsync completes, and a failed fsync rolls them back and reassigns the
        // position — so a reader that consumed them would hold an event the log never committed.
        // Every writer holds the sidecar append lock from its write through fsync (or rollback),
        // so while holding that lock no cooperating writer is mid-append. The file can still end
        // in a crashed writer's torn tail, which the next live writer trims and overwrites with a
        // frame that is not yet acknowledged; so the bound is the end of the last valid frame
        // under the lock, never the raw length. The lock is released before the first event is
        // yielded.
        long committedLength;
        using (await AcquireAppendLockAsync(ct).ConfigureAwait(false))
        {
            committedLength = await FindLastFrameEndAsync(fs, ct).ConfigureAwait(false);
        }

        fs.Position = 0;
        await foreach (var frame in EnumerateFramesAsync(fs, committedLength, ct).ConfigureAwait(false))
        {
            if (frame.Position >= position)
            {
                // AppendAsync never frames an event its own decode cannot read back (EnsureReadable),
                // so a CRC-valid frame that fails to decode or decodes to null is foreign or forged
                // bytes: the log's fail-closed corruption class.
                TEvent? decoded;
                try
                {
                    decoded = JsonSerializer.Deserialize<TEvent>(frame.Payload, _jsonOptions);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    throw new InvalidDataException(
                        $"Event log '{_filePath}' frame at position {frame.Position} cannot be decoded " +
                        $"({ex.GetType().Name}: {ex.Message}), which this log never writes — corrupt or foreign data.",
                        ex);
                }

                var @event = decoded
                    ?? throw new InvalidDataException(
                        $"Event log '{_filePath}' frame at position {frame.Position} holds a null event, " +
                        "which this log never writes — corrupt or foreign data.");
                yield return (frame.Position, @event);
            }
        }
    }

    /// <summary>
    /// Returns the end of the last valid frame in the file, applying the tear rule from the last
    /// frame end this instance knows (or from the start when the file is now shorter than that).
    /// Must be called while holding the sidecar append lock, so no cooperating writer is mid-append
    /// and the bytes classified here cannot change underneath the scan. Mid-log damage throws.
    /// </summary>
    private async Task<long> FindLastFrameEndAsync(FileStream fs, CancellationToken ct)
    {
        var length = fs.Length;
        var scanFrom = Interlocked.Read(ref _knownFrameEnd);
        if (scanFrom > length)
        {
            scanFrom = 0;
        }

        var lastFrameEnd = scanFrom;
        fs.Position = scanFrom;
        await foreach (var frame in EnumerateFramesAsync(fs, length, ct).ConfigureAwait(false))
        {
            lastFrameEnd = frame.EndOffset;
        }

        Interlocked.Exchange(ref _knownFrameEnd, lastFrameEnd);
        return lastFrameEnd;
    }

    /// <summary>Builds one on-disk frame for <paramref name="payload"/> at <paramref name="position"/>.</summary>
    internal static byte[] EncodeFrame(long position, ReadOnlySpan<byte> payload)
    {
        var frame = new byte[HeaderSize + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, FrameMagic);
        frame[MagicSize] = FrameVersion;
        BinaryPrimitives.WriteInt64LittleEndian(frame.AsSpan(PositionOffset), position);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(PayloadLengthOffset), payload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(PayloadCrcOffset), Crc32C.HashToUInt32(payload));
        BinaryPrimitives.WriteUInt32LittleEndian(
            frame.AsSpan(HeaderCrcOffset), Crc32C.HashToUInt32(frame.AsSpan(0, HeaderCrcOffset)));
        payload.CopyTo(frame.AsSpan(HeaderSize));
        return frame;
    }

    private static bool HeaderIsValid(ReadOnlySpan<byte> header)
        => BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(HeaderCrcOffset)) ==
               Crc32C.HashToUInt32(header.Slice(0, HeaderCrcOffset)) &&
           BinaryPrimitives.ReadUInt32LittleEndian(header) == FrameMagic &&
           header[MagicSize] == FrameVersion;

    /// <summary>
    /// Streams valid frames from <paramref name="fs"/> one at a time, reading exactly each frame's
    /// bytes from disk (no whole-file buffering), and applies the tear rule from the type remarks:
    /// a torn final frame ends the enumeration at the last valid frame; any other damage throws
    /// <see cref="InvalidDataException"/> (fail closed).
    /// </summary>
    private async IAsyncEnumerable<ParsedFrame> EnumerateFramesAsync(
        FileStream fs, long snapshotLength, [EnumeratorCancellation] CancellationToken ct)
    {
        // The enumeration reads and classifies only within the caller's snapshotLength window,
        // captured once while holding the sidecar append lock (so it ends at an acknowledged
        // frame boundary). Readers share the file with an active appender (FileShare.ReadWrite),
        // so bytes appended AFTER the snapshot may belong to an in-flight frame and never take
        // part in the classification.
        var header = new byte[HeaderSize];
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var frameStart = fs.Position;
            var remaining = snapshotLength - frameStart;
            if (remaining <= 0)
            {
                yield break;
            }

            var headerRead = await ReadAtMostAsync(fs, header.AsMemory(0, (int)Math.Min(remaining, HeaderSize)), ct).ConfigureAwait(false);
            if (headerRead < HeaderSize || !HeaderIsValid(header))
            {
                if (await IsUnwrittenHeaderAsync(fs, frameStart, snapshotLength, header.AsMemory(0, headerRead), ct).ConfigureAwait(false))
                {
                    yield break; // torn tail: a header that never fully reached disk.
                }

                throw Corrupt(frameStart, "its header is incomplete or fails its checksum and is not an unwritten tail");
            }

            var framePosition = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(PositionOffset));
            var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(PayloadLengthOffset));
            if (payloadLength < 0)
            {
                throw Corrupt(frameStart, $"its checksum-valid header declares a negative payload length {payloadLength}");
            }

            var frameEnd = frameStart + HeaderSize + payloadLength;
            if (frameEnd > snapshotLength)
            {
                yield break; // torn tail: valid header, payload short at the end.
            }

            var payload = new byte[payloadLength];
            await ReadAtMostAsync(fs, payload, ct).ConfigureAwait(false);
            if (Crc32C.HashToUInt32(payload) != BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(PayloadCrcOffset)))
            {
                // An interrupted append leaves a payload prefix followed by zeros, so its last byte
                // is zero (a serialized event never ends in one). A final payload whose last byte
                // landed and still fails its checksum is damage to an fsync'd, acknowledged event.
                if (frameEnd == snapshotLength && FinalFrameTearRule.IsUnwrittenSuffix(payload, ReadOnlySpan<byte>.Empty))
                {
                    yield break; // torn tail: the final frame's payload did not fully land.
                }

                throw Corrupt(frameStart, frameEnd == snapshotLength
                    ? "its payload fails its checksum although its last byte landed"
                    : "its payload fails its checksum and later frames follow it");
            }

            yield return new ParsedFrame(framePosition, payload, frameEnd);
        }
    }

    /// <summary>
    /// True when the bytes from <paramref name="frameStart"/> to <paramref name="snapshotLength"/>
    /// are a strict prefix of a frame header followed only by zero bytes: an append whose header
    /// never fully reached disk. The header's last byte must be zero (a header whose every byte
    /// landed would pass its checksum, so a complete header that fails is damage, not a tear), any
    /// checksum bytes that landed must match the checksum of the header fields before them, and the
    /// magic and version bytes present must agree with a real header.
    /// </summary>
    private static async Task<bool> IsUnwrittenHeaderAsync(
        FileStream fs, long frameStart, long snapshotLength, ReadOnlyMemory<byte> headerRegion, CancellationToken ct)
    {
        fs.Position = frameStart + headerRegion.Length;
        var chunk = new byte[(int)Math.Min(Math.Max(snapshotLength - fs.Position, 1), 1 << 16)];
        while (fs.Position < snapshotLength)
        {
            var want = (int)Math.Min(chunk.Length, snapshotLength - fs.Position);
            var read = await ReadAtMostAsync(fs, chunk.AsMemory(0, want), ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (chunk.AsSpan(0, read).ContainsAnyExcept((byte)0))
            {
                return false;
            }
        }

        // An interrupted append leaves a strict prefix of the header, so its last byte is zero. A
        // header whose every byte landed and still fails its checksum is damage to an fsync'd,
        // acknowledged frame — even when only zeros follow it.
        var nonZeroEnd = headerRegion.Span.LastIndexOfAnyExcept((byte)0) + 1;
        if (nonZeroEnd == HeaderSize)
        {
            return false;
        }

        // A zero last byte alone does not make a prefix: the header checksum's high byte is zero for
        // about one frame in 256. A real prefix that reaches into the checksum carries the leading
        // bytes of the checksum over the fields before it, so landed checksum bytes that disagree
        // with those fields are damage to a complete header.
        if (nonZeroEnd > HeaderCrcOffset)
        {
            Span<byte> crc = stackalloc byte[ChecksumSize];
            BinaryPrimitives.WriteUInt32LittleEndian(crc, Crc32C.HashToUInt32(headerRegion.Span[..HeaderCrcOffset]));
            if (!headerRegion.Span[HeaderCrcOffset..nonZeroEnd].SequenceEqual(crc[..(nonZeroEnd - HeaderCrcOffset)]))
            {
                return false;
            }
        }

        Span<byte> expected = stackalloc byte[MagicSize + VersionSize];
        BinaryPrimitives.WriteUInt32LittleEndian(expected, FrameMagic);
        expected[MagicSize] = FrameVersion;
        for (var i = 0; i < Math.Min(nonZeroEnd, expected.Length); i++)
        {
            if (headerRegion.Span[i] != expected[i])
            {
                return false;
            }
        }

        return true;
    }

    private InvalidDataException Corrupt(long frameOffset, string reason)
        => new(
            $"Event log '{_filePath}' has a damaged 'BSEL' frame at byte offset {frameOffset}: {reason}. " +
            "This is mid-log damage (or a foreign file), not a recoverable torn tail. Restore the log " +
            "from a replica or backup; refusing to serve a history with silently missing events.");

    // Reads up to buffer.Length bytes; returns the number actually read (< length at EOF).
    private static async Task<int> ReadAtMostAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[total..], ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            total += read;
        }
        return total;
    }

    /// <summary>
    /// Scans the existing file on startup to recover <see cref="_nextPosition"/> and
    /// <see cref="_committedEndOffset"/>, applying the same tear rule as
    /// <see cref="ReadFromAsync"/>: a torn final frame is trimmed, any other damage throws. Runs under the sidecar append lock so the torn-tail trim can
    /// never race a live writer's in-flight append — without it, a frame committed between
    /// this scan and the trim would be truncated away as if it were tail garbage.
    /// </summary>
    private async Task<(long NextPosition, long CommittedEnd)> RecoverPositionAsync(
        CancellationToken ct)
    {
        if (!File.Exists(_filePath))
        {
            return (0, 0);
        }

        using var appendLock = await AcquireAppendLockAsync(ct).ConfigureAwait(false);

        using var fs = new FileStream(
            _filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite,
            bufferSize: 1 << 16, useAsync: true);

        if (fs.Length == 0)
        {
            return (0, 0);
        }

        fs.Position = 0;

        // The frame walk applies the tear rule from the first byte: a zero-filled or partially
        // written first frame is a torn tail (trimmed to empty below); a foreign file's first
        // header fails its checksum and throws. The walk streams frame by frame (an append-only
        // event log is unbounded, so slurping the whole history would cap recovery at
        // int.MaxValue bytes), with each payload bounded by the bytes remaining. Recovery runs
        // once, at CreateAsync time, fully async.
        long maxPosition = -1;
        long lastValidEnd = 0;
        await foreach (var frame in EnumerateFramesAsync(fs, fs.Length, ct).ConfigureAwait(false))
        {
            if (frame.Position > maxPosition)
            {
                maxPosition = frame.Position;
            }

            lastValidEnd = frame.EndOffset;
        }

        // Trim a torn tail (crash mid-append) back to the end of the last valid frame, so the
        // next append continues a clean frame sequence instead of burying garbage mid-log.
        // Mid-log corruption never reaches here — the enumeration above throws for it.
        if (lastValidEnd < fs.Length)
        {
            fs.SetLength(lastValidEnd);
            fs.Flush(flushToDisk: true);
        }

        Interlocked.Exchange(ref _knownFrameEnd, lastValidEnd);
        return (maxPosition + 1, lastValidEnd);
    }

    private readonly struct ParsedFrame(long position, byte[] payload, long endOffset)
    {
        public long Position { get; } = position;
        public byte[] Payload { get; } = payload;

        /// <summary>Stream offset one past this frame's checksum — the truncation point if every later byte is a torn tail.</summary>
        public long EndOffset { get; } = endOffset;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Atomically mark disposed; bail if another thread beat us to it.
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        // Drain in-flight writes before disposing the semaphore — otherwise an in-flight
        // AppendAsync holding the semaphore would ObjectDisposedException on Release.
        _writeLock.Wait();
        try
        {
            _writeStream?.Dispose();
            _writeStream = null;
        }
        finally
        {
            _writeLock.Release();
            _writeLock.Dispose();
        }
    }
}
