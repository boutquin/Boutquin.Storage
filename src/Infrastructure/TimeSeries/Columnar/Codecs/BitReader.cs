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
/// MSB-first forward-only bit reader over a <see cref="ReadOnlySpan{Byte}"/>. Mirror of
/// <see cref="BitWriter"/>.
/// </summary>
/// <remarks>
/// Ref struct so the underlying span can live on the stack; the reader is typically scoped to
/// a single <c>Decode</c> method. Exceeding the buffer raises
/// <see cref="EndOfStreamException"/> — callers bound reads by the logical row count and
/// never speculatively read padding bits.
/// </remarks>
internal ref struct BitReader
{
    private readonly ReadOnlySpan<byte> _input;
    private int _byteIndex;
    private int _bitsInCurrent;

    public BitReader(ReadOnlySpan<byte> input)
    {
        _input = input;
        _byteIndex = 0;
        _bitsInCurrent = 0;
    }

    public bool ReadBit()
    {
        if (_byteIndex >= _input.Length)
        {
            throw new EndOfStreamException("BitReader ran off the end of the input buffer.");
        }

        var bit = (_input[_byteIndex] >> (7 - _bitsInCurrent)) & 1;
        _bitsInCurrent++;
        if (_bitsInCurrent == 8)
        {
            _byteIndex++;
            _bitsInCurrent = 0;
        }

        return bit != 0;
    }

    /// <summary>
    /// Reads <paramref name="bitCount"/> bits and returns them as the low bits of a <see cref="ulong"/>,
    /// MSB-first (first bit read is the most significant of the result).
    /// </summary>
    public ulong ReadBits(int bitCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bitCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bitCount, 64);
        ulong result = 0;
        for (var i = 0; i < bitCount; i++)
        {
            result = (result << 1) | (ReadBit() ? 1UL : 0UL);
        }

        return result;
    }
}
