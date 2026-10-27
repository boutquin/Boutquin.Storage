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
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar;

namespace Boutquin.Storage.Infrastructure.Tests.TimeSeries.Columnar;

// F-W1-02 — ColumnarPredicatePushdown.DecodeDecimal reads four raw int32 words from a zone-map
// decimal bound and constructs `new decimal(bits)` with no validity check. EnsureBoundWidth
// guarantees the 16-byte width but not the flags-word validity: a forged-but-CRC-valid footer
// carrying an invalid decimal flags word leaks a raw ArgumentException out of the predicate-
// pushdown read path instead of the typed CorruptSegmentException the sibling DecodeDateOnlyBound
// already throws.
public sealed class ColumnarPredicatePushdownForgedDecimalTests
{
    private static byte[] BuildBound(int lo, int mid, int hi, int flags)
    {
        var bytes = new byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0, 4), lo);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4, 4), mid);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8, 4), hi);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12, 4), flags);
        return bytes;
    }

    [Fact]
    public void DecodeBound_Decimal_ForgedScaleAbove28_ThrowsCorruptSegment_NotRawArgument()
    {
        // Width-correct (16 bytes) but flags word carries scale = 31 (0x001F0000).
        var forged = BuildBound(lo: 0, mid: 0, hi: 0, flags: 0x001F0000);

        var act = () => ColumnarPredicatePushdown.DecodeBound(forged, ColumnFieldTypeCode.Decimal);

        act.Should().Throw<CorruptSegmentException>(
            "a width-valid but bit-invalid decimal bound must surface a typed corruption exception, "
            + "not a raw ArgumentException from new decimal(bits)");
    }

    [Fact]
    public void DecodeBound_Decimal_ForgedReservedBits_ThrowsCorruptSegment_NotRawArgument()
    {
        var forged = BuildBound(lo: 0, mid: 0, hi: 0, flags: 0x7FFFFFFF);

        var act = () => ColumnarPredicatePushdown.DecodeBound(forged, ColumnFieldTypeCode.Decimal);

        act.Should().Throw<CorruptSegmentException>(
            "a forged decimal bound with reserved bits set must surface a typed corruption exception");
    }

    [Fact]
    public void DecodeBound_Decimal_ValidBound_DecodesUnchanged()
    {
        // Regression guard — a legitimate in-range bound must still decode to the expected decimal.
        var value = -42.0001m;
        var bits = decimal.GetBits(value);
        var bytes = BuildBound(bits[0], bits[1], bits[2], bits[3]);

        var decoded = ColumnarPredicatePushdown.DecodeBound(bytes, ColumnFieldTypeCode.Decimal);

        decoded.Should().Be(value, "a valid in-range decimal bound must still decode after the bit guard");
    }
}
