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
using Boutquin.Storage.Domain.Interfaces.TimeSeries.Columnar;

namespace Boutquin.Storage.Infrastructure.TimeSeries.Columnar;

/// <summary>
/// Read-only, seekable view over a contiguous <c>[offset, offset + length)</c> window of a base
/// stream. Presents the window as a stand-alone stream whose position 0 maps to
/// <c>offset</c> in the base — so a consumer that assumes "the stream <i>is</i> the segment"
/// (e.g. <see cref="Footer.FooterReader"/> seeking from the end) works unchanged against one
/// block embedded in a multi-block LSM segment file.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it exists.</b> The columnar reader seeks straight to each projected column's
/// byte span and reads only that span. When the block is first slurped whole into a
/// <c>MemoryStream</c>, those seeks skip nothing on disk — the whole block already came off the
/// platter. Handing the reader a <see cref="WindowReadStream"/> over the open segment
/// <c>FileStream</c> instead makes the column-level seeks real disk-skips: unprojected column
/// spans are never read.
/// </para>
/// <para>
/// <b>Byte accounting.</b> <see cref="BytesRead"/> counts only the bytes actually delivered
/// through this window, so a test can assert a projected read touches strictly fewer bytes than
/// a full read of the same fixture.
/// </para>
/// <para>
/// <b>Borrowed base.</b> The base stream is borrowed, not owned — <see cref="Dispose"/> does not
/// dispose it. The caller controls the base stream's lifecycle (the segment <c>FileStream</c>
/// outlives several per-block windows).
/// </para>
/// <para>
/// <b>Not thread-safe.</b> Each <see cref="Read(byte[], int, int)"/> re-seeks the base stream to
/// the window's mapped position, so concurrent use against a shared base stream would race. The
/// columnar read path drives one window at a time, sequentially.
/// </para>
/// </remarks>
internal sealed class WindowReadStream : Stream, IPhysicalSegmentIdentity
{
    private readonly Stream _base;
    private readonly long _offset;
    private readonly long _length;
    private long _position;

    /// <summary>
    /// Creates a window over <paramref name="baseStream"/> spanning
    /// <c>[<paramref name="offset"/>, <paramref name="offset"/> + <paramref name="length"/>)</c>.
    /// </summary>
    /// <param name="baseStream">The readable, seekable stream to window. Borrowed, not owned.</param>
    /// <param name="offset">Absolute byte offset of the window's start in the base stream.</param>
    /// <param name="length">Window length in bytes.</param>
    /// <param name="physicalSegmentIdentity">
    /// Optional stable identity of the physical extent represented by this window. When omitted,
    /// the window uses an object-scoped identity because an arbitrary base stream carries no
    /// physical-source provenance.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="baseStream"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="baseStream"/> is not readable or seekable, or
    /// <paramref name="physicalSegmentIdentity"/> is supplied but blank.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="offset"/> or <paramref name="length"/> is negative, or the window runs past
    /// the base stream's end.
    /// </exception>
    public WindowReadStream(
        Stream baseStream,
        long offset,
        long length,
        string? physicalSegmentIdentity = null)
    {
        ArgumentNullException.ThrowIfNull(baseStream);
        if (!baseStream.CanRead || !baseStream.CanSeek)
        {
            throw new ArgumentException("Base stream must be readable and seekable.", nameof(baseStream));
        }
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (physicalSegmentIdentity is not null && string.IsNullOrWhiteSpace(physicalSegmentIdentity))
        {
            throw new ArgumentException("Physical segment identity must be nonempty when supplied.", nameof(physicalSegmentIdentity));
        }
        // Written as a subtraction so a colossal length cannot wrap offset + length past long.MaxValue
        // into a negative sum that would slip under the bound.
        if (length > baseStream.Length - offset)
        {
            throw new ArgumentOutOfRangeException(
                nameof(length),
                $"Window at offset {offset} with length {length} runs past the base stream's end ({baseStream.Length}).");
        }

        _base = baseStream;
        _offset = offset;
        _length = length;
        _position = 0;
        PhysicalSegmentIdentity = physicalSegmentIdentity ?? $"window:{Guid.NewGuid():N}";
    }

    /// <inheritdoc />
    public string PhysicalSegmentIdentity { get; }

    /// <summary>The total number of bytes delivered through this window via <c>Read</c>.</summary>
    public long BytesRead { get; private set; }

    /// <inheritdoc/>
    public override bool CanRead => true;

    /// <inheritdoc/>
    public override bool CanSeek => true;

    /// <inheritdoc/>
    public override bool CanWrite => false;

    /// <inheritdoc/>
    public override long Length => _length;

    /// <inheritdoc/>
    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return Read(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc/>
    public override int Read(Span<byte> buffer)
    {
        var remaining = _length - _position;
        if (remaining <= 0 || buffer.Length == 0)
        {
            return 0;
        }

        var toRead = (int)Math.Min(buffer.Length, remaining);
        // Re-seek the base each read: the consumer seeks the window arbitrarily, and the base
        // position may have been moved by a previous window over the same stream.
        _base.Seek(_offset + _position, SeekOrigin.Begin);

        // Fill up to toRead by looping on the base. A FileStream may satisfy a Read with fewer
        // bytes than requested (a partial read), whereas a MemoryStream never does. The columnar
        // reader's footer/column decode paths were written against MemoryStream and assume a
        // single Read returns the whole requested span; looping here preserves that contract so
        // a windowed FileStream is a drop-in replacement and never surfaces a short read except
        // at the window's end.
        var total = 0;
        while (total < toRead)
        {
            var n = _base.Read(buffer.Slice(total, toRead - total));
            if (n == 0)
            {
                break;
            }
            total += n;
        }
        _position += total;
        BytesRead += total;
        return total;
    }

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };

        if (target < 0 || target > _length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(offset),
                $"Seek target {target} is outside the window [0, {_length}].");
        }

        _position = target;
        return _position;
    }

    /// <inheritdoc/>
    public override void Flush()
    {
        // Read-only — nothing to flush.
    }

    /// <inheritdoc/>
    public override void SetLength(long value) =>
        throw new NotSupportedException("WindowReadStream is read-only.");

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("WindowReadStream is read-only.");

    /// <summary>
    /// Disposes the window without disposing the borrowed base stream.
    /// </summary>
    /// <param name="disposing">Whether the call originates from <see cref="Stream.Dispose()"/>.</param>
    protected override void Dispose(bool disposing)
    {
        // The base stream is borrowed — do not dispose it here. Intentionally does not call
        // base.Dispose(disposing) onto _base.
        base.Dispose(disposing);
    }
}
