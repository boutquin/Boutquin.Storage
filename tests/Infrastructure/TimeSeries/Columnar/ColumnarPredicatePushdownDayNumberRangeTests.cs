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
/// F-V1-01 (predicate-pushdown DateOnly arm) — <see cref="ColumnarPredicatePushdown.DecodeBound"/>
/// length-guards a zone-map bound (F-U2-01) but did not RANGE-guard the DateOnly arm: a forged
/// 4-byte <c>0x7FFFFFFF</c> bound passes the width check yet day-number-overflows
/// <see cref="DateOnly.FromDayNumber(int)"/>. The decode must surface a TYPED
/// <see cref="CorruptSegmentException"/>, not a raw <see cref="ArgumentOutOfRangeException"/>.
/// </summary>
public sealed class ColumnarPredicatePushdownDayNumberRangeTests
{
    [Fact]
    public void DecodeBound_DateOnly_ForgedOutOfRangeDayNumber_ThrowsCorruptSegment()
    {
        // Width-correct (4 bytes) but out-of-DateOnly-range day number.
        var forged = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(forged, int.MaxValue);

        var act = () => ColumnarPredicatePushdown.DecodeBound(forged, ColumnFieldTypeCode.DateOnly);

        act.Should().Throw<CorruptSegmentException>(
            "a width-valid but range-invalid DateOnly bound must surface a typed corruption exception");
    }

    [Fact]
    public void DecodeBound_DateOnly_InRangeDayNumber_DecodesUnchanged()
    {
        // Regression guard — a legitimate in-range bound must still decode to the expected DateOnly.
        var d = new DateOnly(2020, 6, 15);
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, d.DayNumber);

        var decoded = ColumnarPredicatePushdown.DecodeBound(bytes, ColumnFieldTypeCode.DateOnly);

        decoded.Should().Be(d, "a valid in-range bound must still decode after the range guard is added");
    }
}
