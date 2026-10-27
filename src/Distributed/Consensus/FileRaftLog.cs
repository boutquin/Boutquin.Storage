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
using System.Buffers.Binary;
using Boutquin.Storage.Infrastructure.KeyValueStore;
using Boutquin.Storage.Infrastructure.WriteAheadLog;

namespace Boutquin.Storage.Distributed.Consensus;

/// <summary>
/// File-backed <see cref="IRaftLog{TCommand}"/> — the post-GA completion of the Raft
/// persistence seam. Frames each entry to an append-only file with a trailing checksum so the
/// replicated log survives a crash, and drops in through the same <see cref="RaftNode{TCommand}"/>
/// ctor parameter as <see cref="InMemoryRaftLog{TCommand}"/> (binary-compatible substitution).
/// </summary>
/// <typeparam name="TCommand">The command type the state machine processes. Serialised through
/// the caller-supplied delegates — the type carries no <c>ISerializable</c> constraint, matching
/// <see cref="RaftNode{TCommand}"/>'s unconstrained <c>TCommand</c>.</typeparam>
/// <remarks>
/// <para>
/// <b>Frame format (version 1).</b> Each entry is
/// <c>[magic "BSRL": 4][version: 1][term: 8][command length: 4][command CRC32C: 4]
/// [header CRC32C: 4 over the 21 bytes before it][command bytes][end marker 0xA5: 1]</c>, all
/// integers little-endian. The header checksum covers the magic, version, term, command length and
/// command checksum, so damage to the length or term is detected as corruption instead of being
/// inferred from whether later bytes happen to parse. The fixed non-zero end marker makes "the
/// frame's last byte landed" a property of the format rather than of the caller's command
/// encoding, which may genuinely end in a zero byte. The checksum is the repo-standard
/// Castagnoli <c>Crc32C</c> (the repo's single-checksum convention).
/// </para>
/// <para>
/// <b>Torn-write recovery.</b> Construction replays every frame from the head. Each append
/// writes and fsyncs one whole frame, so only the final frame can be partial, and the tear rule
/// is deterministic. The frame at the replay position is a torn final append — recovery stops
/// there and physically truncates the file back to the last good frame — when (1) fewer than a
/// header's bytes remain and they are a prefix of a frame header, possibly followed by zero
/// bytes; (2) a complete header fails its checksum but holds only a strict header prefix (its
/// last byte is zero, and every checksum byte that landed matches the checksum of the header
/// fields before it) followed by zero bytes to end-of-file; (3) the header validates and the
/// command and end marker it declares run past end-of-file; or (4) the header validates, the frame
/// ends exactly at end-of-file, its end marker is zero (it never landed), and the command before it
/// is what an interrupted write leaves: complete (it passes its checksum) or a prefix followed by
/// zeros (its last byte is zero). Any other damage — including a final header whose every byte
/// landed yet fails its checksum, whatever its command bytes, a final frame whose end marker landed
/// yet whose command fails its checksum, whatever the command's own last byte, and a final frame
/// whose command's last byte landed non-zero yet fails its checksum, whatever its end marker —
/// throws
/// <see cref="InvalidDataException"/> and nothing is truncated: erasing committed entries the
/// cluster has replicated would violate State Machine Safety.
/// </para>
/// <para>
/// <b>What the format does not detect.</b> The tear rule detects crash tears and damage inside
/// the file. Truncation of the file exactly at a frame boundary is indistinguishable from "those
/// entries were never written": the frames before the cut are all valid and nothing records how
/// many followed them. Such truncation of fsync'd data is outside the format's threat model.
/// Damage that only zeroes the trailing checksum bytes of the final header leaves bytes identical
/// to a strict header prefix, so it is recovered as a torn append. Likewise, damage that only
/// zeroes the final frame's end marker leaves bytes an interrupted append could produce, so it is
/// recovered as a torn append; the same holds when the damage also reaches a command whose genuine
/// last byte is zero.
/// </para>
/// <para>
/// <b>Durability.</b> Both <see cref="Append"/> and <see cref="TruncateFrom"/> flush to disk
/// (<c>FileStream.Flush(flushToDisk: true)</c> = fsync) before returning, so every committed
/// entry and every truncation survives a crash.
/// </para>
/// <para>
/// <b>In-memory mirror.</b> Each entry's term, command bytes and byte offset are cached in memory
/// so <see cref="GetEntry"/> / <see cref="GetRange"/> / <see cref="Count"/> stay O(1)/O(k)
/// without re-reading the file. The cache is a faithful mirror of disk — every mutation updates
/// both — and every read decodes a fresh command from a private copy of the cached bytes, so a
/// caller mutating a command it was handed can never change what the log serves or replicates.
/// </para>
/// <para>
/// <b>Failure atomicity.</b> If <see cref="Append"/> fails after any bytes may have reached the
/// file (a write, fsync or seek fault), the file is truncated back to the failed frame's start
/// and fsync'd, so the file again matches the mirror and the append may be retried. If that
/// repair also fails, or <see cref="TruncateFrom"/> fails part-way, the durable state is unknown
/// and the log faults: every later read or mutation throws <see cref="InvalidOperationException"/>
/// until the instance is disposed and the file reopened, which replays the durable state.
/// </para>
/// <para>
/// <b>Threading contract.</b> Not thread-safe — same as the in-memory default; the consuming
/// <see cref="RaftNode{TCommand}"/> serialises every log mutation through the cluster tick loop.
/// </para>
/// <para>
/// <b>Reference:</b> Ongaro &amp; Ousterhout (2014); Kleppmann &amp; Riccomini,
/// <i>Designing Data-Intensive Applications</i>, 2nd ed. (O'Reilly, 2026), Ch. 10 p. 366–370.
/// </para>
/// </remarks>
public sealed class FileRaftLog<TCommand> : IRaftLog<TCommand>, IDisposable
{
    private const uint FrameMagic = 0x4C_52_53_42; // 'B' 'S' 'R' 'L' in little-endian read order.
    private const byte FrameVersion = 1;
    private const int VersionOffset = 4;
    private const int TermOffset = 5;
    private const int CommandLengthOffset = 13;
    private const int CommandCrcOffset = 17;
    private const int HeaderCrcOffset = 21;
    private const int HeaderSize = 25;

    // Ends every frame. It is non-zero, so whether a frame's last byte landed is decided by the
    // format, never by whether the caller's command encoding happens to end in a zero byte.
    private const byte EndMarker = 0xA5;
    private const int EndMarkerSize = 1;

    private readonly Func<TCommand, byte[]> _serialize;
    private readonly Func<byte[], TCommand> _deserialize;
    private readonly FileStream _stream;

    // The mirror keeps each entry's encoded command bytes, exactly as they sit on disk, and
    // decodes a fresh command per read, so no caller ever holds an object the mirror owns.
    private readonly List<(long Term, byte[] Command)> _entries = [];
    private readonly List<long> _offsets = [];
    private bool _disposed;

    // Set when a failed mutation left the file in a state the mirror cannot describe. Every later
    // read or mutation throws, so the log never serves or extends a state that disagrees with disk.
    private bool _faulted;

    // Test-only observability (InternalsVisibleTo). Counts how many times the file-creating
    // directory fsync ran, so a test can pin that the durability step fires exactly once when
    // the constructor creates a new log file and never when it reopens an existing one.
    internal int _directoryFsyncCount;

    /// <summary>
    /// Opens (or creates) the log file at <paramref name="path"/>, replaying any previously
    /// persisted entries and truncating a torn final append if one is found.
    /// </summary>
    /// <param name="path">The durable file path. Must be non-empty; its directory must exist.</param>
    /// <param name="serialize">Encodes a command to bytes for the on-disk frame.</param>
    /// <param name="deserialize">Decodes a command from the bytes produced by
    /// <paramref name="serialize"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null, empty, or whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="serialize"/> or
    /// <paramref name="deserialize"/> is null.</exception>
    public FileRaftLog(string path, Func<TCommand, byte[]> serialize, Func<byte[], TCommand> deserialize)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Raft log path must be non-empty.", nameof(path));
        }

        _serialize = serialize ?? throw new ArgumentNullException(nameof(serialize));
        _deserialize = deserialize ?? throw new ArgumentNullException(nameof(deserialize));

        // Did the log file already exist before we opened it? If not, FileMode.OpenOrCreate
        // CREATES a new directory entry — and that entry is not durable until the containing
        // directory's inode is fsync'd. Without it, a crash after a frame flush yet before the
        // directory write lands can lose the whole log file, dropping entries the cluster has
        // already replicated and acknowledged (Raft persist-before-respond, §5.2). An EMPTY
        // existing file is fsync'd the same way: it is what a crash before the fsync, or an open
        // whose fsync threw, leaves behind, and no open has yet made its entry durable. Mirrors
        // WriteAheadLog's re-initialisation of a missing or empty segment.
        var fileIsNew = !File.Exists(path);

        _stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        try
        {
            if ((fileIsNew || _stream.Length == 0) && !OperatingSystem.IsWindows())
            {
                DurableFile.FsyncDirectory(path);
                _directoryFsyncCount++;
            }

            Replay();
        }
        catch
        {
            // A refused open (corruption, a failing deserializer, an I/O error) returns no
            // instance to dispose, so release the exclusive handle here rather than leaving the
            // file locked until finalization.
            _stream.Dispose();
            throw;
        }
    }

    /// <inheritdoc/>
    public int Count
    {
        get
        {
            ThrowIfFaulted();
            return _entries.Count;
        }
    }

    /// <inheritdoc/>
    public (long Term, TCommand Command) GetEntry(int index)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfFaulted();
        if (index < 0 || index >= _entries.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(index),
                $"Log index {index} is outside [0, {_entries.Count}).");
        }
        return Decoded(_entries[index]);
    }

    /// <inheritdoc/>
    public void Append((long Term, TCommand Command) entry)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfFaulted();

        var command = _serialize(entry.Command);
        var frame = new byte[HeaderSize + command.Length + EndMarkerSize];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, FrameMagic);
        frame[VersionOffset] = FrameVersion;
        BinaryPrimitives.WriteInt64LittleEndian(frame.AsSpan(TermOffset), entry.Term);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(CommandLengthOffset), command.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(CommandCrcOffset), Crc32C.HashToUInt32(command));
        BinaryPrimitives.WriteUInt32LittleEndian(
            frame.AsSpan(HeaderCrcOffset), Crc32C.HashToUInt32(frame.AsSpan(0, HeaderCrcOffset)));
        command.CopyTo(frame.AsSpan(HeaderSize));
        frame[^1] = EndMarker;

        // The mirror holds a private copy of the frame's command bytes — exactly what Replay will
        // rebuild after a restart — never the caller's object, which the caller may mutate after
        // this call returns (a reused buffer would otherwise change what the leader serves and
        // replicates while disk keeps the original). Decoding before the write also refuses a
        // command Replay could not decode, so the log never seals an unopenable frame.
        var mirrored = frame.AsSpan(HeaderSize, command.Length).ToArray();
        _ = _deserialize(frame.AsSpan(HeaderSize, command.Length).ToArray());

        var offset = _stream.Length;
        try
        {
            _stream.Seek(0, SeekOrigin.End);
            _stream.Write(frame, 0, frame.Length);
            _stream.Flush(flushToDisk: true);
        }
        catch
        {
            // The frame was never acknowledged, but some or all of its bytes may be in the file.
            // Left there, a retry duplicates the entry and a later append seals torn bytes into
            // the middle of the log. Restore the last good boundary, or fault if that fails too.
            RepairFailedAppendOrFault(offset);
            throw;
        }

        _entries.Add((entry.Term, mirrored));
        _offsets.Add(offset);
    }

    /// <inheritdoc/>
    public void TruncateFrom(int fromIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfFaulted();
        if (fromIndex < 0 || fromIndex > _entries.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(fromIndex),
                $"Truncate index {fromIndex} is outside [0, {_entries.Count}].");
        }
        if (fromIndex == _entries.Count)
        {
            return;
        }

        var byteOffset = _offsets[fromIndex];
        try
        {
            _stream.SetLength(byteOffset);
            _stream.Seek(0, SeekOrigin.End);
            _stream.Flush(flushToDisk: true);
        }
        catch
        {
            // How much of the truncation reached disk is unknown; only a replay can tell.
            _faulted = true;
            throw;
        }

        _entries.RemoveRange(fromIndex, _entries.Count - fromIndex);
        _offsets.RemoveRange(fromIndex, _offsets.Count - fromIndex);
    }

    /// <inheritdoc/>
    public IReadOnlyList<(long Term, TCommand Command)> GetRange(int startIndex, int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfFaulted();
        // count > Count - startIndex, not startIndex + count > Count: the sum overflows int for a
        // count near int.MaxValue and would pass the guard.
        if (startIndex < 0 || count < 0 || startIndex > _entries.Count || count > _entries.Count - startIndex)
        {
            throw new ArgumentOutOfRangeException(
                nameof(startIndex),
                $"Range [{startIndex}, {(long)startIndex + count}) is outside [0, {_entries.Count}).");
        }
        if (count == 0)
        {
            return [];
        }

        var range = new (long Term, TCommand Command)[count];
        for (var i = 0; i < count; i++)
        {
            range[i] = Decoded(_entries[startIndex + i]);
        }

        return range;
    }

    // A fresh command decoded from a private copy of the mirrored bytes: the deserializer may
    // return (or wrap) the array it is given, so it never sees the mirror's own buffer.
    private (long Term, TCommand Command) Decoded((long Term, byte[] Command) entry)
        => (entry.Term, _deserialize((byte[])entry.Command.Clone()));

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _stream.Dispose();
    }

    /// <summary>
    /// Truncates the file back to <paramref name="frameStart"/> after a failed append and fsyncs
    /// the truncation. If the repair itself fails the file tail is unknown, so the log faults.
    /// </summary>
    private void RepairFailedAppendOrFault(long frameStart)
    {
        try
        {
            _stream.SetLength(frameStart);
            _stream.Seek(0, SeekOrigin.End);
            _stream.Flush(flushToDisk: true);
        }
        catch
        {
            _faulted = true;
        }
    }

    private void ThrowIfFaulted()
    {
        if (_faulted)
        {
            throw new InvalidOperationException(
                "The Raft log's on-disk state is uncertain after a failed write. Dispose this " +
                "instance and reopen the file to recover the durable log.");
        }
    }

    /// <summary>
    /// Replays frames from the head of the file into the in-memory mirror, applying the tear rule
    /// from the type remarks: a torn final append is truncated back to the last good entry; any
    /// other damage throws <see cref="InvalidDataException"/> and leaves the file untouched.
    /// </summary>
    private void Replay()
    {
        _stream.Seek(0, SeekOrigin.Begin);
        var length = _stream.Length;
        long position = 0;
        var header = new byte[HeaderSize];

        while (position < length)
        {
            var headerRead = (int)Math.Min(length - position, HeaderSize);
            ReadExactly(header, headerRead);
            if (headerRead < HeaderSize || !HeaderIsValid(header))
            {
                if (IsUnwrittenHeader(position, length, header.AsSpan(0, headerRead)))
                {
                    break;
                }

                throw Corrupt(position, "its header is incomplete or fails its checksum and is not an unwritten tail");
            }

            var commandLength = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(CommandLengthOffset));
            if (commandLength < 0)
            {
                throw Corrupt(position, $"its checksum-valid header declares a negative command length {commandLength}");
            }

            var frameEnd = position + HeaderSize + (long)commandLength + EndMarkerSize;
            if (frameEnd > length)
            {
                // Valid header, command or end marker short at end-of-file: the interrupted final append.
                break;
            }

            var command = new byte[commandLength];
            ReadExactly(command, commandLength);
            var marker = new byte[EndMarkerSize];
            ReadExactly(marker, EndMarkerSize);
            var commandIntact = Crc32C.HashToUInt32(command) == BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(CommandCrcOffset));
            if (marker[0] != EndMarker || !commandIntact)
            {
                // An interrupted append leaves a prefix of the frame followed by zeros, so its end
                // marker is still zero and the command before it is either complete (its checksum
                // passes) or a prefix followed by zeros (its last byte is zero). A final frame whose
                // end marker landed was written completely, and a command whose last byte landed
                // non-zero was written completely, so a checksum failure in either is damage to an
                // fsync'd, acknowledged entry — the same rule as a complete header.
                if (frameEnd == length
                    && FinalFrameTearRule.IsUnwrittenSuffix(marker, ReadOnlySpan<byte>.Empty)
                    && (commandIntact || FinalFrameTearRule.IsUnwrittenSuffix(command, ReadOnlySpan<byte>.Empty)))
                {
                    break;
                }

                throw Corrupt(position, frameEnd == length
                    ? "its command fails its checksum or its end marker is wrong although its last byte landed"
                    : "its command fails its checksum or its end marker is wrong and later frames follow it");
            }

            // Decode once so a checksum-valid frame the deserializer refuses fails the open
            // instead of a later read.
            _ = _deserialize((byte[])command.Clone());

            var term = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(TermOffset));
            _entries.Add((term, command));
            _offsets.Add(position);
            position = frameEnd;
        }

        // Drop any torn tail so the next append starts from a clean boundary.
        if (position < length)
        {
            _stream.SetLength(position);
            _stream.Flush(flushToDisk: true);
        }
        _stream.Seek(0, SeekOrigin.End);
    }

    private static bool HeaderIsValid(ReadOnlySpan<byte> header)
        => BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(HeaderCrcOffset)) ==
               Crc32C.HashToUInt32(header.Slice(0, HeaderCrcOffset)) &&
           BinaryPrimitives.ReadUInt32LittleEndian(header) == FrameMagic &&
           header[VersionOffset] == FrameVersion;

    /// <summary>
    /// True when the bytes from <paramref name="frameStart"/> to end-of-file are a strict prefix
    /// of a frame header followed only by zero bytes: an append whose header never fully reached
    /// disk. The header's last byte must be zero (a header whose every byte landed would pass its
    /// checksum, so a complete header that fails is damage, not a tear), any checksum bytes that
    /// landed must match the checksum of the header fields before them, and the magic and version
    /// bytes present must agree with a real header.
    /// </summary>
    private bool IsUnwrittenHeader(long frameStart, long length, ReadOnlySpan<byte> headerRegion)
    {
        _stream.Seek(frameStart + headerRegion.Length, SeekOrigin.Begin);
        var chunk = new byte[(int)Math.Min(Math.Max(length - _stream.Position, 1), 1 << 16)];
        int read;
        while ((read = _stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (chunk.AsSpan(0, read).ContainsAnyExcept((byte)0))
            {
                return false;
            }
        }

        // An interrupted append leaves a strict prefix of the header, so its last byte is zero. A
        // header whose every byte landed and still fails its checksum is damage to an fsync'd,
        // acknowledged entry — even when nothing but zeros (an empty or all-zero command) follows.
        var nonZeroEnd = headerRegion.LastIndexOfAnyExcept((byte)0) + 1;
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
            Span<byte> crc = stackalloc byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32LittleEndian(crc, Crc32C.HashToUInt32(headerRegion[..HeaderCrcOffset]));
            if (!headerRegion[HeaderCrcOffset..nonZeroEnd].SequenceEqual(crc[..(nonZeroEnd - HeaderCrcOffset)]))
            {
                return false;
            }
        }

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

    private static InvalidDataException Corrupt(long offset, string reason)
        => new(
            $"Raft log has a damaged frame at offset {offset}: {reason}. This is mid-file corruption, " +
            "not a torn tail; truncating here would erase committed entries.");

    private bool ReadExactly(byte[] buffer, int count)
    {
        var read = 0;
        while (read < count)
        {
            var n = _stream.Read(buffer, read, count - read);
            if (n == 0)
            {
                return false;
            }
            read += n;
        }
        return true;
    }
}
