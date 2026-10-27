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
namespace Boutquin.Storage.Infrastructure.TimeSeries.Columnar.Codecs;

/// <summary>
/// MSB-first append-only bit-stream writer over an in-memory byte buffer. Used by codecs
/// that pack variable-width bit fields tightly (Gorilla XOR; bit-packed integer payloads).
/// </summary>
/// <remarks>
/// <para>Within each byte, the first bit written occupies bit 7 (the MSB). Once 8 bits have
/// been accumulated the byte is flushed to the internal buffer and a fresh byte begins. The
/// final partial byte, if any, is flushed by <see cref="ToArray"/>; unused trailing bits are
/// zero (padding, ignored by the reader once the consumer knows the logical stop point).</para>
/// <para><b>Scope:</b> simple, correctness-first. One bit per method call on the hot path.
/// For Phase 3b this is fast enough; a later phase can replace the implementation with a
/// batched ulong-accumulator for throughput-sensitive paths without changing the caller
/// surface.</para>
/// </remarks>
internal sealed class BitWriter
{
    private readonly MemoryStream _buffer = new();
    private byte _current;
    private int _bitsInCurrent;

    public void WriteBit(bool set)
    {
        if (set)
        {
            _current |= (byte)(1 << (7 - _bitsInCurrent));
        }

        _bitsInCurrent++;
        if (_bitsInCurrent == 8)
        {
            _buffer.WriteByte(_current);
            _current = 0;
            _bitsInCurrent = 0;
        }
    }

    /// <summary>
    /// Writes the low <paramref name="bitCount"/> bits of <paramref name="value"/>, MSB-first
    /// (bit <c>bitCount-1</c> emitted first).
    /// </summary>
    public void WriteBits(ulong value, int bitCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bitCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bitCount, 64);
        for (var i = bitCount - 1; i >= 0; i--)
        {
            WriteBit(((value >> i) & 1UL) != 0);
        }
    }

    /// <summary>
    /// Returns the accumulated bytes. Any pending partial byte is flushed with trailing-zero
    /// padding. Safe to call once at end-of-stream; subsequent writes begin a fresh buffer.
    /// </summary>
    public byte[] ToArray()
    {
        if (_bitsInCurrent > 0)
        {
            _buffer.WriteByte(_current);
            _current = 0;
            _bitsInCurrent = 0;
        }

        return _buffer.ToArray();
    }
}
