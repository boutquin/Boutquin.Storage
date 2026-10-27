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
using Microsoft.Win32.SafeHandles;

namespace Boutquin.Storage.Infrastructure.ValueLog;

/// <summary>
/// Random-access value-log reader. Dereferences a <see cref="ValueLogPointer"/> by reading
/// the entry frame at its offset, validating its header and payload CRC32C checksums, and returning the payload
/// bytes to the engine.
/// </summary>
/// <remarks>
/// <para><b>Handle pooling.</b> The reader holds an LRU-bounded pool of
/// <see cref="SafeFileHandle"/>s (one per live value-log segment), capped by
/// <see cref="ValueLogOptions.ReadHandleCacheSize"/>. Writes against the same file on the
/// writer side do not invalidate the handle — both sides rely on
/// <see cref="FileShare.Read"/> on the writer's stream.</para>
/// <para><b>Concurrency.</b> The hot path (cache hit) avoids the global open-lock: handle
/// lookup uses a <see cref="ConcurrentDictionary{TKey, TValue}"/> and the access clock is
/// updated via <see cref="Interlocked.Increment(ref long)"/>. Per-bucket dictionary writes
/// still synchronise inside the dictionary, and concurrent stamp updates on the same key are
/// best-effort — the recorded stamp is approximately monotonic across threads, which is the
/// design point of an approximate-LRU policy: eviction order may be off by a few entries
/// under heavy contention but a hot entry is never permanently ejected. The cold path
/// (handle open + LRU eviction) takes a short <c>_openLock</c>; once a handle is returned
/// the random read executes through <see cref="RandomAccess"/> without further synchronisation.</para>
/// <para><b>Use-after-free safety.</b> The cache lookup and the read are necessarily two
/// separate steps, so a concurrent cold-path eviction can <c>SafeFileHandle.Dispose()</c>
/// the very handle a reader just fetched. To close that window the reader pins the handle for
/// the whole read via <c>SafeHandle.DangerousAddRef</c> /
/// <c>SafeHandle.DangerousRelease</c>: eviction's <c>Dispose</c> only marks the handle
/// for close and the OS file descriptor is released when the last ref-count drains, so an
/// in-flight <see cref="RandomAccess"/> read never touches a closed descriptor. If
/// <c>DangerousAddRef</c> loses the race (eviction already disposed the handle before the pin),
/// it throws <see cref="ObjectDisposedException"/>; the reader catches that, retries the cache
/// lookup, and re-opens the segment. The retry-on-<see cref="ObjectDisposedException"/> loop is
/// the sole safety mechanism — no post-pin cache re-validation is needed, because a pinned
/// handle that eviction has already removed from the cache still reads correctly and its
/// descriptor closes when the pin's ref-count drains.</para>
/// </remarks>
public sealed class ValueLogReader : IDisposable
{
    private readonly string _directory;
    private readonly int _cacheSize;
    private readonly bool _validateCrc;

    // Lock-free read path: ConcurrentDictionary for O(1) handle lookup without a global lock.
    private readonly ConcurrentDictionary<uint, SafeFileHandle> _handles = new();

    // Approximate LRU via an interlocked clock: each access stamps the entry with a
    // monotonically-increasing tick; eviction picks the entry with the smallest stamp.
    private readonly ConcurrentDictionary<uint, long> _lastAccess = new();
    private long _clock;

    // Cold-path lock: only held when opening a new handle or evicting an old one.
    private readonly Lock _openLock = new();

    private volatile bool _disposed;

    // Every live reader, keyed by its normalised directory, so the garbage collector can release
    // the handles readers cache on a segment it unlinks. The collector reaches the directory but
    // not the engine's reader, and on POSIX an unlinked file's blocks stay allocated while any
    // descriptor on it is open — reclaiming without releasing would free nothing. Weak references
    // keep the registry from extending a reader's lifetime; Dispose deregisters.
    private static readonly Lock s_registryLock = new();
    private static readonly List<WeakReference<ValueLogReader>> s_registry = [];
    private readonly string _registryKey;

    public ValueLogReader(string directory, ValueLogOptions options)
    {
        Guard.AgainstNullOrWhiteSpace(() => directory);
        Guard.AgainstNullOrDefault(() => options);

        _directory = directory;
        _cacheSize = Math.Max(1, options.ReadHandleCacheSize);
        _validateCrc = options.ValidateCrcOnRead != Boutquin.Storage.Domain.Enums.CrcValidationMode.Never;
        _registryKey = RegistryKey(directory);

        lock (s_registryLock)
        {
            s_registry.Add(new WeakReference<ValueLogReader>(this));
        }
    }

    /// <summary>
    /// Closes the handle every live reader over <paramref name="directory"/> caches for segment
    /// <paramref name="fileIndex"/>. Called by the garbage collector around the unlink of a
    /// reclaimed segment. A read already in flight keeps its pinned descriptor until it finishes;
    /// a later read of that segment re-opens it (and fails, once the file is gone).
    /// </summary>
    internal static void ReleaseSegmentHandles(string directory, uint fileIndex)
    {
        var key = RegistryKey(directory);
        var readers = new List<ValueLogReader>();
        lock (s_registryLock)
        {
            s_registry.RemoveAll(entry => !entry.TryGetTarget(out _));
            foreach (var entry in s_registry)
            {
                if (entry.TryGetTarget(out var reader) && string.Equals(reader._registryKey, key, StringComparison.Ordinal))
                {
                    readers.Add(reader);
                }
            }
        }

        foreach (var reader in readers)
        {
            reader.ReleaseHandle(fileIndex);
        }
    }

    private static string RegistryKey(string directory)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));

    private void ReleaseHandle(uint fileIndex)
    {
        lock (_openLock)
        {
            if (_handles.TryRemove(fileIndex, out var handle))
            {
                _lastAccess.TryRemove(fileIndex, out _);
                handle.Dispose();
            }
        }
    }

    /// <summary>
    /// Resolves <paramref name="pointer"/>'s payload bytes. Validates the entry frame's header and payload CRCs
    /// and throws <see cref="CorruptValueLogPointerException"/> on mismatch.
    /// </summary>
    public byte[] Read(ValueLogPointer pointer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Pin the handle for the entire read so a concurrent LRU eviction cannot dispose the
        // OS file descriptor out from under the RandomAccess.Read calls below. SafeHandle's
        // ref-count defers the actual close until DangerousRelease drains it.
        var handle = AcquirePinnedHandle(pointer.FileIndex);
        try
        {
            // Bound the pointer offset against the segment file length BEFORE the header read so a
            // corrupt/forged pointer landing in the final < EntryHeaderSize bytes (or at/past EOF)
            // surfaces the typed CorruptValueLogPointerException — not a raw EndOfStreamException
            // leaked from the unbounded header ReadFully. Matches the length-bound guard below.
            var fileLength = RandomAccess.GetLength(handle);
            // Subtract the header size from the validated file length before comparing the unsigned
            // offset. Adding to a forged offset near long.MaxValue would wrap before the bound check.
            if (fileLength < ValueLogFrame.EntryHeaderSize
                || pointer.Offset > (ulong)(fileLength - ValueLogFrame.EntryHeaderSize))
            {
                throw new CorruptValueLogPointerException(
                    pointer.FileIndex, pointer.Offset,
                    $"Value-log entry at (fileIndex={pointer.FileIndex}, offset={pointer.Offset}) starts within the "
                    + $"final {ValueLogFrame.EntryHeaderSize} bytes of, or past the end of, the {fileLength}-byte segment; likely corrupt pointer.");
            }

            Span<byte> headerBuf = stackalloc byte[ValueLogFrame.EntryHeaderSize];
            ReadFully(handle, headerBuf, (long)pointer.Offset, RandomAccess.Read);

            // The header checksum authenticates the length, kind and sequence number before any of
            // them is trusted — in particular before the length sizes an allocation.
            if (_validateCrc)
            {
                var recordedHeaderCrc = ValueLogFrame.ReadHeaderCrc(headerBuf);
                var computedHeaderCrc = ValueLogFrame.ComputeHeaderCrc(headerBuf);
                if (recordedHeaderCrc != computedHeaderCrc)
                {
                    throw new CorruptValueLogPointerException(
                        pointer.FileIndex, pointer.Offset, recordedHeaderCrc, computedHeaderCrc);
                }
            }

            var length = (ulong)BinaryPrimitives.ReadUInt32LittleEndian(headerBuf);
            var kind = headerBuf[ValueLogFrame.KindOffset];

            // Identity check: the frame's sequence number must be the one the pointer was issued
            // for. A frame the CRC accepts can still belong to a different write when the address
            // was truncated and then reused; serving it would return another key's value. A
            // stored value whose identity does not match is treated as lost.
            var sequenceNumber = BinaryPrimitives.ReadUInt64LittleEndian(headerBuf.Slice(ValueLogFrame.SeqnoOffset, 8));
            if (pointer.SequenceCheck == 0
                || ValueLogPointer.ComputeSequenceCheck(sequenceNumber) != pointer.SequenceCheck)
            {
                throw new CorruptValueLogPointerException(
                    pointer.FileIndex, pointer.Offset,
                    $"Value-log entry at (fileIndex={pointer.FileIndex}, offset={pointer.Offset}) carries sequence number "
                    + $"{sequenceNumber}, whose check value does not match the pointer's ({pointer.SequenceCheck}); the "
                    + "value the pointer referenced is lost (address reused or pointer corrupt).");
            }

            if (kind != ValueLogFrame.KindPayload)
            {
                throw new CorruptValueLogPointerException(
                    pointer.FileIndex, pointer.Offset,
                    $"Value-log entry at (fileIndex={pointer.FileIndex}, offset={pointer.Offset}) does not carry a payload frame (kind byte 0x{kind:X2}).");
            }

            if (length > int.MaxValue)
            {
                throw new CorruptValueLogPointerException(
                    pointer.FileIndex, pointer.Offset,
                    $"Value-log entry at (fileIndex={pointer.FileIndex}, offset={pointer.Offset}) declares length={length} which exceeds int.MaxValue; likely corrupt pointer.");
            }

            // Bound the declared length by the bytes that actually follow the header + CRC in the
            // segment file BEFORE allocating, so a corrupt pointer near a small file cannot drive a
            // huge allocation (OOM / DoS) ahead of the CRC check. Long arithmetic. The writer's
            // ScanEffectiveLengthAsync applies the same file-size bound on its side.
            var payloadStart = (long)pointer.Offset + ValueLogFrame.EntryHeaderSize;
            if ((long)length > fileLength - payloadStart - ValueLogFrame.EntryCrcSize)
            {
                throw new CorruptValueLogPointerException(
                    pointer.FileIndex, pointer.Offset,
                    $"Value-log entry at (fileIndex={pointer.FileIndex}, offset={pointer.Offset}) declares length={length} exceeding the bytes remaining in the {fileLength}-byte segment; likely corrupt pointer.");
            }

            var payload = new byte[length];
            if (length > 0)
            {
                ReadFully(handle, payload, (long)pointer.Offset + ValueLogFrame.EntryHeaderSize, RandomAccess.Read);
            }

            if (_validateCrc)
            {
                Span<byte> crcBuf = stackalloc byte[ValueLogFrame.EntryCrcSize];
                ReadFully(handle, crcBuf, (long)pointer.Offset + ValueLogFrame.EntryHeaderSize + (long)length, RandomAccess.Read);
                var recordedCrc = BinaryPrimitives.ReadUInt32LittleEndian(crcBuf);

                var computedCrc = Crc32C.HashToUInt32(payload);
                if (recordedCrc != computedCrc)
                {
                    throw new CorruptValueLogPointerException(
                        pointer.FileIndex, pointer.Offset, recordedCrc, computedCrc);
                }
            }

            return payload;
        }
        finally
        {
            handle.DangerousRelease();
        }
    }

    /// <summary>
    /// Resolves the segment handle for <paramref name="fileIndex"/> and pins it via
    /// <c>SafeHandle.DangerousAddRef</c> so the caller can read through it
    /// without an eviction disposing the file descriptor mid-read. The caller MUST call
    /// <c>SafeHandle.DangerousRelease</c> exactly once when done.
    /// </summary>
    /// <remarks>
    /// Two threads can interleave between the cache lookup in <see cref="GetHandle"/> and the
    /// <c>DangerousAddRef</c> here: the cold-path evictor may dispose the handle in that gap.
    /// SafeHandle reports the loss by throwing <see cref="ObjectDisposedException"/> from
    /// <c>DangerousAddRef</c>; we catch it and retry the whole lookup, which re-opens the
    /// segment (a fresh handle, never the disposed one). The retry is bounded only by how
    /// quickly evictions recur, which is itself bounded by cache pressure — in practice one
    /// or two iterations.
    /// </remarks>
    private SafeFileHandle AcquirePinnedHandle(uint fileIndex)
    {
        while (true)
        {
            var handle = GetHandle(fileIndex);
            var pinned = false;
            try
            {
                handle.DangerousAddRef(ref pinned);
            }
            catch (ObjectDisposedException)
            {
                // Eviction disposed this handle between the cache lookup and the pin. The
                // cache entry is already gone (eviction removes before disposing); retry to
                // re-open the segment under a fresh handle.
                continue;
            }

            if (pinned)
            {
                return handle;
            }
        }
    }

    /// <summary>
    /// Per-call random-read delegate. Mirrors
    /// <see cref="System.IO.RandomAccess.Read(SafeFileHandle, Span{byte}, long)"/>; abstracted so
    /// the read-fully loop can be exercised against an injected short-reading source in tests
    /// (a genuine short read is not deterministically reproducible on every platform).
    /// </summary>
    internal delegate int RandomReadFunc(SafeFileHandle handle, Span<byte> buffer, long fileOffset);

    /// <summary>
    /// Reads exactly <c>buffer.Length</c> bytes into <paramref name="buffer"/> starting at
    /// <paramref name="fileOffset"/>, looping until the span is fully filled. The underlying
    /// <see cref="System.IO.RandomAccess.Read(SafeFileHandle, Span{byte}, long)"/> may return
    /// fewer bytes than requested even when the bytes are durably present, so a single call can
    /// leave the buffer tail unfilled — its return count is load-bearing and must be honoured.
    /// A 0-byte return before the buffer is full is genuine end-of-file on a file the caller has
    /// already bounds-checked as large enough, i.e. real truncation: it throws
    /// <see cref="EndOfStreamException"/> rather than returning a partially-filled buffer.
    /// </summary>
    internal static void ReadFully(SafeFileHandle handle, Span<byte> buffer, long fileOffset, RandomReadFunc read)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = read(handle, buffer[total..], fileOffset + total);
            if (n <= 0)
            {
                throw new EndOfStreamException(
                    $"Value-log read reached end-of-file after {total} of {buffer.Length} requested bytes at offset {fileOffset}.");
            }

            total += n;
        }
    }

    public void Dispose()
    {
        // Publish the terminal state under the same lock that admits a cold-path open.
        // A read already holding a pinned handle may finish; no later open can leak a handle.
        lock (_openLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var handle in _handles.Values)
            {
                handle.Dispose();
            }
            _handles.Clear();
            _lastAccess.Clear();
        }

        lock (s_registryLock)
        {
            s_registry.RemoveAll(entry => !entry.TryGetTarget(out var reader) || ReferenceEquals(reader, this));
        }
    }

    private SafeFileHandle GetHandle(uint fileIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Hot path: lock-free lookup. Stamp the access clock atomically on hit.
        if (_handles.TryGetValue(fileIndex, out var existing))
        {
            _lastAccess[fileIndex] = Interlocked.Increment(ref _clock);
            return existing;
        }

        // Cold path: open a new handle, evicting LRU entries if at capacity.
        lock (_openLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // Double-check after acquiring the lock — another thread may have opened it.
            if (_handles.TryGetValue(fileIndex, out existing))
            {
                _lastAccess[fileIndex] = Interlocked.Increment(ref _clock);
                return existing;
            }

            while (_handles.Count >= _cacheSize)
            {
                EvictLruLocked();
            }

            var path = ValueLogWriter.ResolveSegmentPath(_directory, fileIndex);
            if (!File.Exists(path))
            {
                throw new MissingValueLogFileException(
                    fileIndex,
                    $"Value-log segment file {path} is missing on disk; referenced by a live pointer.");
            }

            var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            _handles[fileIndex] = handle;
            _lastAccess[fileIndex] = Interlocked.Increment(ref _clock);
            return handle;
        }
    }

    private void EvictLruLocked()
    {
        var oldestIndex = uint.MaxValue;
        var oldestTick = long.MaxValue;

        foreach (var kv in _lastAccess)
        {
            if (kv.Value < oldestTick && _handles.ContainsKey(kv.Key))
            {
                oldestTick = kv.Value;
                oldestIndex = kv.Key;
            }
        }

        if (oldestIndex == uint.MaxValue)
        {
            return;
        }

        if (_handles.TryRemove(oldestIndex, out var evicted))
        {
            _lastAccess.TryRemove(oldestIndex, out _);
            evicted.Dispose();
        }
    }
}
