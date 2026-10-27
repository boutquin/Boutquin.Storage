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
/// A read-side complement to <see cref="PooledMemoryStream"/>: rents a buffer from
/// <see cref="ArrayPool{T}.Shared"/>, fills it from a <see cref="Stream"/>, exposes it as
/// a <see cref="MemoryStream"/> for deserialization, and returns the buffer to the pool on
/// disposal. Used by the source-generated tagged-wire deserializer to eliminate the
/// <c>new byte[length]</c> heap allocations that a naïve implementation would impose on
/// every <c>Deserialize</c> call.
/// </summary>
/// <remarks>
/// <para>
/// <b>Usage pattern</b> — mirrors the write-side <see cref="PooledMemoryStream"/> idiom:
/// <code>
/// using var buf = PooledReadBuffer.RentAndRead(stream, payloadLength);
/// using var ms  = buf.AsStream();
/// // … deserialize from ms …
/// </code>
/// The disposal order of consecutive <c>using var</c> statements is LIFO, so
/// <c>ms</c> is disposed before <c>buf</c> — the underlying array remains valid for the
/// entire lifetime of the <see cref="MemoryStream"/>.
/// </para>
/// <para>
/// <b>Ownership</b> — the caller must not access <see cref="AsStream"/> after
/// <see cref="Dispose"/> has been called; the rented array is returned to the pool and
/// may be reused by other code.
/// </para>
/// </remarks>
public sealed class PooledReadBuffer : IDisposable
{
    private byte[]? _buffer;
    private readonly int _length;
    private bool _disposed;

    private PooledReadBuffer(byte[] buffer, int length)
    {
        _buffer = buffer;
        _length = length;
    }

    /// <summary>
    /// Rents a buffer of at least <paramref name="length"/> bytes from
    /// <see cref="ArrayPool{T}.Shared"/>, reads exactly <paramref name="length"/> bytes
    /// from <paramref name="stream"/>, and returns a <see cref="PooledReadBuffer"/>
    /// that owns the rented array.
    /// </summary>
    /// <param name="stream">The source stream to read from.</param>
    /// <param name="length">The exact number of bytes to read.</param>
    /// <returns>A <see cref="PooledReadBuffer"/> that must be disposed after use.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="stream"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="length"/> is negative.
    /// </exception>
    /// <exception cref="System.IO.EndOfStreamException">
    /// Thrown when the stream ends before <paramref name="length"/> bytes are available.
    /// </exception>
    public static PooledReadBuffer RentAndRead(Stream stream, int length)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        // The declared length is corruption-controlled data on EVERY stream kind: a 5-byte
        // corrupt frame declaring int.MaxValue must never drive a ~2 GB rent before the short
        // stream is observed. Seekable streams expose the remaining byte count, so bound the
        // claim before renting the exact length; non-seekable streams (network, pipe,
        // decompression) cannot pre-check, so allocation grows geometrically only as bytes
        // actually arrive — the same discipline as VarintHelpers.ReadAllBounded. A CanSeek-gated
        // bound alone is exactly the pattern the BSSG021 analyzer classifies as NOT a bound.
        if (stream.CanSeek)
        {
            var remaining = stream.Length - stream.Position;
            if (length > remaining)
            {
                throw new EndOfStreamException(
                    $"Declared payload length {length} exceeds the {remaining} bytes remaining in the stream — corrupt length prefix.");
            }

            var seekableBuf = ArrayPool<byte>.Shared.Rent(Math.Max(length, 1));
            try
            {
                VarintHelpers.ReadExact(stream, seekableBuf.AsSpan(0, length));
            }
            catch
            {
                // Without this, every truncated frame leaks a pool slot — sustained pool churn
                // on a corrupt-segment scan loop.
                ArrayPool<byte>.Shared.Return(seekableBuf);
                throw;
            }

            return new PooledReadBuffer(seekableBuf, length);
        }

        // Non-seekable: trust window seeded small, doubled only once the stream has actually
        // supplied every byte of the current window.
        var window = Math.Min(length, 4096);
        var buf = ArrayPool<byte>.Shared.Rent(Math.Max(window, 1));
        var filled = 0;
        try
        {
            while (filled < length)
            {
                if (filled == window)
                {
                    window = (int)Math.Min((long)window * 2, length);
                    if (window > buf.Length)
                    {
                        var grown = ArrayPool<byte>.Shared.Rent(window);
                        Buffer.BlockCopy(buf, 0, grown, 0, filled);
                        ArrayPool<byte>.Shared.Return(buf);
                        buf = grown;
                    }
                }

                var read = stream.Read(buf, filled, window - filled);
                if (read == 0)
                {
                    throw new EndOfStreamException(
                        $"Stream ended after {filled} of the declared {length} payload bytes — truncated stream or corrupt length prefix.");
                }

                filled += read;
            }
        }
        catch
        {
            // Same pool-hygiene rule as the seekable path.
            ArrayPool<byte>.Shared.Return(buf);
            throw;
        }

        return new PooledReadBuffer(buf, length);
    }

    /// <summary>
    /// Returns a read-only <see cref="MemoryStream"/> view over exactly the bytes that
    /// were read by <see cref="RentAndRead"/>. The stream is positioned at offset 0.
    /// </summary>
    /// <returns>
    /// A <see cref="MemoryStream"/> backed by the rented buffer. The caller is
    /// responsible for disposing it, but the stream itself does not own the underlying
    /// array — disposing it does not return the array to the pool.
    /// </returns>
    /// <exception cref="ObjectDisposedException">
    /// Thrown when this <see cref="PooledReadBuffer"/> has already been disposed.
    /// </exception>
    public MemoryStream AsStream()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new MemoryStream(_buffer!, 0, _length, writable: false);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_buffer is not null)
        {
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = null;
        }
    }
}
