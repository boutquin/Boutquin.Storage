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
namespace Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar;

/// <summary>
/// One (distinct-value, encoded-bitmap) pair inside a <see cref="BitmapIndexEntry"/>. The
/// value is the canonical on-disk bytes of a distinct value observed in the indexed column;
/// the bitmap is the <c>BitmapRleCodec</c>-encoded boolean vector identifying which rows in
/// the whole segment carry that value.
/// </summary>
public sealed record BitmapIndexValue
{
    private readonly byte[] _value;
    private readonly byte[] _bitmap;

    /// <summary>Creates a new (value, bitmap) entry.</summary>
    /// <param name="Value">Canonical on-disk bytes of the distinct value. Never <see langword="null"/>.</param>
    /// <param name="Bitmap">
    /// <c>BitmapRleCodec</c>-encoded presence bitmap spanning every row in the segment.
    /// Never <see langword="null"/>.
    /// </param>
    public BitmapIndexValue(byte[] Value, byte[] Bitmap)
    {
        ArgumentNullException.ThrowIfNull(Value);
        ArgumentNullException.ThrowIfNull(Bitmap);

        // Defensive copy on construction: this record's value-equality and hash code derive from
        // these byte payloads, so storing the caller's array by reference would let a
        // post-construction mutation silently change this record's identity. Clone keeps the
        // byte[] public shape (DR-2) while making the value object truly immutable. This is a cold
        // bitmap-index build path, not a per-row scan, so the clone has no per-row allocation cost.
        _value = (byte[])Value.Clone();
        _bitmap = (byte[])Bitmap.Clone();
    }

    /// <summary>Canonical on-disk bytes of the distinct value.</summary>
    public byte[] Value => (byte[])_value.Clone();

    /// <summary><c>BitmapRleCodec</c>-encoded presence bitmap spanning every row in the segment.</summary>
    public byte[] Bitmap => (byte[])_bitmap.Clone();

    /// <inheritdoc />
    public bool Equals(BitmapIndexValue? other) =>
        other is not null
        && _value.AsSpan().SequenceEqual(other._value)
        && _bitmap.AsSpan().SequenceEqual(other._bitmap);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.AddBytes(_value);
        hash.AddBytes(_bitmap);
        return hash.ToHashCode();
    }
}
