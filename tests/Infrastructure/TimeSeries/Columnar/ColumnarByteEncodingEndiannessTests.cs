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

/// <summary>
/// Pins the on-disk byte layout of columnar zone-map bounds and bitmap-index keys to
/// <b>little-endian, host-independent</b>. The decode side
/// (<c>ColumnarPredicatePushdown.DecodeBound</c>) reads these footer-extension bytes with
/// <c>BinaryPrimitives.Read*LittleEndian</c>; the encode side must agree on every host, not only on
/// little-endian hardware. These assertions document the wire layout and catch a regression to a
/// host-endian encoder (e.g. <c>BitConverter.GetBytes</c>) on a big-endian build, or any byte-order
/// / width change. (On a little-endian build the two encoders are byte-identical, so all pre-existing
/// segments and fixtures are unaffected by the fix these tests guard.)
/// </summary>
public sealed class ColumnarByteEncodingEndiannessTests
{
    [Fact]
    public void EncodeValue_Int32_IsLittleEndian()
    {
        // 0x04030201: each byte distinct so the order is visible.
        var bytes = ColumnarPredicatePushdown.EncodeValue(0x04030201, ColumnFieldTypeCode.Int32);

        bytes.Should().Equal(0x01, 0x02, 0x03, 0x04);
    }

    [Fact]
    public void EncodeValue_Int64_IsLittleEndian()
    {
        var bytes = ColumnarPredicatePushdown.EncodeValue(0x0807060504030201L, ColumnFieldTypeCode.Int64);

        bytes.Should().Equal(0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08);
    }

    [Fact]
    public void EncodeValue_DateOnly_IsLittleEndianDayNumber()
    {
        var date = DateOnly.FromDayNumber(0x010203);
        var expected = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(expected, date.DayNumber);

        ColumnarPredicatePushdown.EncodeValue(date, ColumnFieldTypeCode.DateOnly).Should().Equal(expected);
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(-5)]
    [InlineData(0)]
    [InlineData(123456789)]
    public void EncodeValue_DecodeBound_RoundTripsInt32(int value)
    {
        var bytes = ColumnarPredicatePushdown.EncodeValue(value, ColumnFieldTypeCode.Int32)!;

        ColumnarPredicatePushdown.DecodeBound(bytes, ColumnFieldTypeCode.Int32).Should().Be(value);
    }

    [Fact]
    public void ZoneMap_Int32Bounds_AreLittleEndian()
    {
        // min = -5 (0xFFFFFFFB), max = 10 — decodes back through the little-endian reader exactly.
        var stats = ZoneMapComputer.Compute(new[] { 10, -5, 7 }, ColumnFieldTypeCode.Int32, 0, 3);

        var expectedMin = new byte[4];
        var expectedMax = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(expectedMin, -5);
        BinaryPrimitives.WriteInt32LittleEndian(expectedMax, 10);

        stats.MinValue.Should().Equal(expectedMin);
        stats.MaxValue.Should().Equal(expectedMax);
        ColumnarPredicatePushdown.DecodeBound(stats.MinValue, ColumnFieldTypeCode.Int32).Should().Be(-5);
        ColumnarPredicatePushdown.DecodeBound(stats.MaxValue, ColumnFieldTypeCode.Int32).Should().Be(10);
    }

    [Fact]
    public void ZoneMap_BoundsMatch_QueryEncoding()
    {
        // The zone-map bound bytes and the query-side EncodeValue bytes must be byte-identical, or a
        // bitmap-index equality lookup would miss its own bucket. This is the cross-component contract
        // the shared little-endian encoder guarantees on every host.
        var stats = ZoneMapComputer.Compute(new[] { 42L }, ColumnFieldTypeCode.Int64, 0, 1);
        var queryBytes = ColumnarPredicatePushdown.EncodeValue(42L, ColumnFieldTypeCode.Int64)!;

        stats.MinValue.Should().Equal(queryBytes);
        stats.MaxValue.Should().Equal(queryBytes);
    }
}
