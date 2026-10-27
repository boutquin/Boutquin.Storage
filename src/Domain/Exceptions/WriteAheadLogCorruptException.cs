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
namespace Boutquin.Storage.Domain.Exceptions;

/// <summary>
/// The WriteAheadLog has entered a corrupt state and rejects further appends. Typically
/// raised when a group-commit fsync (D1) fails — the underlying disk has reported an I/O
/// error or "no space left on device" — and the WAL stream can no longer make durability
/// guarantees.
/// </summary>
/// <remarks>
/// <para>
/// <b>Failure semantics.</b> When the flusher
/// loop's batched fsync throws, every waiter in the failed batch sees the underlying
/// exception (set on its TaskCompletionSource), the WAL marks itself corrupt, and every
/// subsequent <c>AppendAsync</c> / <c>AppendDeleteAsync</c> fast-fails with this exception.
/// The flusher loop does not crash — the process continues serving reads. On the next
/// process start, <c>RecoverAsync</c> truncates the WAL at the last valid CRC boundary and
/// the engine reseeds its sequence-number counter from the surviving segments.
/// </para>
/// <para>
/// <b>Replay-invariants clause.</b> A writer that observed a fsync failure should be prepared
/// to see the same pre-crash keys replayed on the next startup — the failed batch may have
/// reached the OS page cache (and therefore the next replay) even though it never reached
/// the platter.
/// </para>
/// </remarks>
public sealed class WriteAheadLogCorruptException : Exception
{
    /// <summary>
    /// The absolute path of the WAL segment that triggered the corruption exception,
    /// or <see cref="string.Empty"/> when no specific segment is implicated (e.g. the
    /// fast-fail path after a prior fsync failure).
    /// </summary>
    public string SegmentPath { get; }

    /// <summary>
    /// The byte offset of the last cleanly-parsed frame in the corrupt segment, or
    /// <c>-1</c> when the offset is not known (e.g. an fsync failure where no frame
    /// boundary has been inspected).  A non-negative value indicates how much data is
    /// recoverable by truncating the segment at this position.
    /// </summary>
    public long LastGoodOffset { get; }

    /// <summary>
    /// Initialises a new instance with a message only.  <see cref="SegmentPath"/> is
    /// set to <see cref="string.Empty"/> and <see cref="LastGoodOffset"/> to <c>-1</c>.
    /// </summary>
    public WriteAheadLogCorruptException(string message)
        : base(message)
    {
        SegmentPath = string.Empty;
        LastGoodOffset = -1L;
    }

    /// <summary>
    /// Initialises a new instance with a message and an inner exception.
    /// <see cref="SegmentPath"/> is set to <see cref="string.Empty"/> and
    /// <see cref="LastGoodOffset"/> to <c>-1</c>.
    /// </summary>
    public WriteAheadLogCorruptException(string message, Exception innerException)
        : base(message, innerException)
    {
        SegmentPath = string.Empty;
        LastGoodOffset = -1L;
    }

    /// <summary>
    /// Initialises a new instance capturing the corrupt segment's path.
    /// <see cref="LastGoodOffset"/> defaults to <c>-1</c> (unknown).
    /// </summary>
    /// <param name="message">Human-readable description of the corruption.</param>
    /// <param name="segmentPath">Absolute path of the corrupt WAL segment.</param>
    public WriteAheadLogCorruptException(string message, string segmentPath)
        : base(message)
    {
        SegmentPath = segmentPath;
        LastGoodOffset = -1L;
    }

    /// <summary>
    /// Initialises a new instance capturing the corrupt segment's path and the byte
    /// offset of the last good frame.
    /// </summary>
    /// <param name="message">Human-readable description of the corruption.</param>
    /// <param name="segmentPath">Absolute path of the corrupt WAL segment.</param>
    /// <param name="lastGoodOffset">
    /// Byte offset of the last cleanly-parsed frame boundary.  Pass <c>-1</c> when
    /// the offset is not known.
    /// </param>
    public WriteAheadLogCorruptException(string message, string segmentPath, long lastGoodOffset)
        : base(message)
    {
        SegmentPath = segmentPath;
        LastGoodOffset = lastGoodOffset;
    }

    /// <summary>
    /// Initialises a new instance capturing the corrupt segment's path, the byte offset
    /// of the last good frame, and an inner exception that caused the corruption.
    /// </summary>
    /// <param name="message">Human-readable description of the corruption.</param>
    /// <param name="segmentPath">Absolute path of the corrupt WAL segment.</param>
    /// <param name="lastGoodOffset">Byte offset of the last cleanly-parsed frame boundary.</param>
    /// <param name="innerException">The underlying exception that triggered the corruption.</param>
    public WriteAheadLogCorruptException(string message, string segmentPath, long lastGoodOffset, Exception innerException)
        : base(message, innerException)
    {
        SegmentPath = segmentPath;
        LastGoodOffset = lastGoodOffset;
    }
}
