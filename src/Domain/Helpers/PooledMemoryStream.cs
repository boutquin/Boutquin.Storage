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
namespace Boutquin.Storage.Domain.Helpers;

/// <summary>
/// A write-only <see cref="Stream"/> backed by a buffer rented from
/// <see cref="ArrayPool{T}.Shared"/>. Used by the source-generated tagged-wire emitter to
/// materialise nested length-framed payloads without the per-record managed allocations
/// that <see cref="MemoryStream"/>+<c>ToArray()</c> would impose on every
/// <c>Serialize</c> call.
/// </summary>
/// <remarks>
/// <para>
/// <b>Theory:</b> Every <c>[StorageSerializable]</c> record's <c>Serialize</c> path needs a
/// scratch buffer to compose the payload before writing the LEN prefix on the outer stream.
/// The naive implementation — <c>new MemoryStream().ToArray()</c> — allocates the
/// growing-stream's internal buffer plus a final <see cref="byte"/>[] copy on every record.
/// On the LSM write hot path that runs once per WAL frame and once per SSTable block
/// entry, dominating the per-write allocation budget. <see cref="PooledMemoryStream"/>
/// rents from <see cref="ArrayPool{T}.Shared"/> instead so steady-state allocations drop to
/// near zero.
/// </para>
/// <para>
/// <b>Lifecycle.</b> Instances are <see cref="IDisposable"/>. Disposal returns the rented
/// buffer to the pool. Because the pool reuses buffers across callers, the rented buffer
/// is <em>not</em> cleared on dispose — callers must ensure they have not leaked references
/// to <see cref="WrittenSpan"/> after disposal.
/// </para>
/// <para>
/// <b>Growth strategy.</b> The buffer doubles when capacity is exceeded — same amortised
/// cost as <see cref="List{T}"/> / <see cref="MemoryStream"/>. Each grow rents the new size
/// from the pool, copies forward, and returns the old buffer.
/// </para>
/// <para>
/// <b>Thread safety.</b> Single-threaded. Concurrent writers must wrap externally.
/// </para>
/// </remarks>
public sealed class PooledMemoryStream : Stream
{
    private const int DefaultInitialCapacity = 256;

    private byte[] _buffer;
    private int _length;
    private bool _disposed;

    /// <summary>
    /// Initializes a new <see cref="PooledMemoryStream"/> with a 256-byte initial buffer.
    /// </summary>
    public PooledMemoryStream() : this(DefaultInitialCapacity)
    {
    }

    /// <summary>
    /// Initializes a new <see cref="PooledMemoryStream"/> with the requested initial buffer
    /// size — sized up by the pool's bucket layout.
    /// </summary>
    /// <param name="initialCapacity">Suggested initial buffer size in bytes. Must be ≥ 1.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown if <paramref name="initialCapacity"/> is less than 1.
    /// </exception>
    public PooledMemoryStream(int initialCapacity)
    {
        if (initialCapacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(initialCapacity), initialCapacity,
                "Initial capacity must be at least 1 byte.");
        }
        _buffer = ArrayPool<byte>.Shared.Rent(initialCapacity);
    }

    /// <inheritdoc/>
    public override bool CanRead => false;

    /// <inheritdoc/>
    public override bool CanSeek => false;

    /// <inheritdoc/>
    public override bool CanWrite => !_disposed;

    /// <inheritdoc/>
    public override long Length => _length;

    /// <inheritdoc/>
    public override long Position
    {
        get => _length;
        set => throw new NotSupportedException(
            "PooledMemoryStream is append-only — use Length to inspect the written byte count.");
    }

    /// <summary>
    /// The bytes written so far, as a contiguous span over the rented buffer. Valid only
    /// until the next write (which may grow the buffer) or <see cref="Dispose(bool)"/>.
    /// </summary>
    public ReadOnlySpan<byte> WrittenSpan => new(_buffer, 0, _length);

    /// <summary>
    /// Reserves <paramref name="count"/> contiguous zero-initialised bytes at the current end
    /// of the buffer and returns the byte offset of the reserved slot. The reserved bytes are
    /// counted as part of <see cref="Length"/>; subsequent writes append after them. Use
    /// <see cref="GetBackpatchSpan(int, int)"/> to obtain a writable span over the slot once
    /// any later writes have completed.
    /// </summary>
    /// <param name="count">Number of bytes to reserve. Must be ≥ 0.</param>
    /// <returns>The byte offset of the reserved slot's first byte.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="count"/> is negative.
    /// </exception>
    /// <exception cref="ObjectDisposedException">Thrown when the stream is disposed.</exception>
    /// <remarks>
    /// <para>
    /// <b>Why this exists.</b> The Phase-2 tagged-wire emitter writes a length-prefixed block
    /// whose LEN cannot be known until the body has been serialised. The naive solution
    /// double-buffers the body into a scratch <see cref="PooledMemoryStream"/>, then writes
    /// LEN + body to the parent — paying for one extra rented buffer per length-prefixed
    /// block. Reserving a fixed-width slot up front and backpatching the LEN at the end
    /// (the "backpatch-LEN trick" of Protobuf C++) lets the body stream into the parent
    /// directly, eliminating the scratch rental.
    /// </para>
    /// <para>
    /// <b>Span lifetime.</b> The returned offset is buffer-relative and stable across grows.
    /// The companion <see cref="GetBackpatchSpan(int, int)"/> returns a fresh
    /// <see cref="Span{T}"/> over the current backing buffer, which is the only safe way to
    /// write the slot — a <see cref="Span{T}"/> captured before a grow would point at the
    /// returned-to-pool buffer. Callers must call <see cref="GetBackpatchSpan(int, int)"/>
    /// only after every write to the stream that could trigger a grow has completed.
    /// </para>
    /// </remarks>
    public int Reserve(int count)
    {
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count,
                "Reserve byte count must be non-negative.");
        }
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureCapacity(_length + count);
        var offset = _length;
        _buffer.AsSpan(offset, count).Clear();
        _length += count;
        return offset;
    }

    /// <summary>
    /// Returns a writable <see cref="Span{T}"/> of <paramref name="length"/> bytes starting at
    /// <paramref name="offset"/> within the current backing buffer. Used to backpatch a slot
    /// previously reserved with <see cref="Reserve(int)"/> after later writes have completed.
    /// </summary>
    /// <param name="offset">Buffer-relative byte offset of the slot's first byte.</param>
    /// <param name="length">Number of bytes in the slot.</param>
    /// <returns>A writable span over the requested slot.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="offset"/> or <paramref name="length"/> is negative, or when
    /// the slot would extend past the bytes already written.
    /// </exception>
    /// <exception cref="ObjectDisposedException">Thrown when the stream is disposed.</exception>
    /// <remarks>
    /// The returned span is valid only until the next write that grows the buffer (which
    /// returns the current buffer to the pool and rents a larger one). Callers must use the
    /// span immediately and not retain it across subsequent writes.
    /// </remarks>
    public Span<byte> GetBackpatchSpan(int offset, int length)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), offset,
                "Backpatch offset must be non-negative.");
        }
        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length), length,
                "Backpatch length must be non-negative.");
        }
        if ((long)offset + length > _length)
        {
            throw new ArgumentOutOfRangeException(nameof(length), length,
                $"Backpatch slot [{offset}, {offset + length}) extends past the {_length} bytes written.");
        }
        return _buffer.AsSpan(offset, length);
    }

    /// <inheritdoc/>
    public override void Flush()
    {
        // Nothing to flush — the rented buffer is the storage.
    }

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("PooledMemoryStream is write-only.");

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException("PooledMemoryStream is append-only.");

    /// <inheritdoc/>
    public override void SetLength(long value) =>
        throw new NotSupportedException("PooledMemoryStream length tracks writes; SetLength is not supported.");

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        Write(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc/>
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureCapacity(_length + buffer.Length);
        buffer.CopyTo(_buffer.AsSpan(_length));
        _length += buffer.Length;
    }

    /// <inheritdoc/>
    public override void WriteByte(byte value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureCapacity(_length + 1);
        _buffer[_length++] = value;
    }

    /// <summary>
    /// Copies every byte written so far to <paramref name="destination"/> in a single
    /// <see cref="Stream.Write(byte[], int, int)"/> call. Replaces the
    /// <c>ToArray() + Write()</c> double-pass on the legacy <see cref="MemoryStream"/>
    /// pattern with a zero-copy hand-off.
    /// </summary>
    /// <param name="destination">The destination stream — must accept synchronous writes.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="destination"/> is <see langword="null"/>.
    /// </exception>
    public void WriteTo(Stream destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_length > 0)
        {
            destination.Write(_buffer, 0, _length);
        }
    }

    private void EnsureCapacity(int required)
    {
        if (required <= _buffer.Length)
        {
            return;
        }

        // Doubling in long arithmetic, clamped to the runtime's maximum array length: int
        // doubling overflows for required in (2^30, 2^31) — 2^30 × 2 wraps negative, then to
        // zero, then resets to the default capacity, spinning forever on the first payload
        // beyond 1 GiB. `required` is an int, so the clamped capacity always satisfies it
        // except in the sliver required ∈ (Array.MaxLength, int.MaxValue], where Rent returns
        // an Array.MaxLength buffer and the caller's subsequent copy fails loud
        // (ArgumentException) — a payload that large is unbuildable as a byte[] either way.
        long newCapacity = _buffer.Length == 0 ? DefaultInitialCapacity : _buffer.Length;
        while (newCapacity < required)
        {
            newCapacity *= 2;
        }

        var newBuffer = ArrayPool<byte>.Shared.Rent((int)Math.Min(newCapacity, Array.MaxLength));
        Array.Copy(_buffer, newBuffer, _length);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = newBuffer;
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (_buffer.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = [];
        }
        base.Dispose(disposing);
    }
}
