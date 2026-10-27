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
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar;

namespace Boutquin.Storage.Infrastructure.Tests.TimeSeries.Columnar;

/// <summary>
/// Direct coverage for <see cref="ZoneMapComputer.Compute"/> — the per-row-group min/max/null
/// statistics computation. The integration tests in <see cref="ZoneMapTests"/> exercise the
/// computer indirectly through <c>CompressedColumnarSegmentFormat.WriteAsync</c>; these tests
/// pin the per-arm contract directly so a future regression in any one branch surfaces here.
/// </summary>
public sealed class ZoneMapComputerTests
{
    [Fact]
    public void Compute_EmptyRowCount_ReturnsAllZeroes()
    {
        var stats = ZoneMapComputer.Compute(new int[] { 1, 2, 3 }, ColumnFieldTypeCode.Int32, 0, 0);
        stats.MinValue.Should().BeEmpty();
        stats.MaxValue.Should().BeEmpty();
        stats.NullCount.Should().Be(0);
        stats.DistinctCount.Should().Be(0);
    }

    [Fact]
    public void Compute_NullColumnValues_Throws()
    {
        Action act = () => ZoneMapComputer.Compute(null!, ColumnFieldTypeCode.Int32, 0, 1);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Compute_Int32_MinMaxAndDistinct()
    {
        var values = new[] { 5, 1, 3, 1, 2 };
        var stats = ZoneMapComputer.Compute(values, ColumnFieldTypeCode.Int32, 0, 5);
        BitConverter.ToInt32(stats.MinValue).Should().Be(1);
        BitConverter.ToInt32(stats.MaxValue).Should().Be(5);
        stats.DistinctCount.Should().Be(4);
    }

    [Fact]
    public void Compute_Int64_MinMaxAndDistinct()
    {
        var values = new long[] { 1L, 2L, 1L };
        var stats = ZoneMapComputer.Compute(values, ColumnFieldTypeCode.Int64, 0, 3);
        BitConverter.ToInt64(stats.MinValue).Should().Be(1L);
        BitConverter.ToInt64(stats.MaxValue).Should().Be(2L);
        stats.DistinctCount.Should().Be(2);
    }

    [Fact]
    public void Compute_Double_MinMaxAndDistinct()
    {
        var values = new[] { 1.5, 0.5, 2.5, 1.5 };
        var stats = ZoneMapComputer.Compute(values, ColumnFieldTypeCode.Double, 0, 4);
        BitConverter.ToDouble(stats.MinValue).Should().Be(0.5);
        BitConverter.ToDouble(stats.MaxValue).Should().Be(2.5);
        stats.DistinctCount.Should().Be(3);
    }

    [Fact]
    public void Compute_Single_MinMaxAndDistinct()
    {
        var values = new[] { 1.5f, 0.5f, 2.5f };
        var stats = ZoneMapComputer.Compute(values, ColumnFieldTypeCode.Single, 0, 3);
        BitConverter.ToSingle(stats.MinValue).Should().Be(0.5f);
        BitConverter.ToSingle(stats.MaxValue).Should().Be(2.5f);
    }

    [Fact]
    public void Compute_DateOnly_MinMaxAndDistinct()
    {
        var values = new[]
        {
            new DateOnly(2020, 1, 5),
            new DateOnly(2020, 1, 1),
            new DateOnly(2020, 1, 5),
        };
        var stats = ZoneMapComputer.Compute(values, ColumnFieldTypeCode.DateOnly, 0, 3);
        DateOnly.FromDayNumber(BitConverter.ToInt32(stats.MinValue)).Should().Be(new DateOnly(2020, 1, 1));
        DateOnly.FromDayNumber(BitConverter.ToInt32(stats.MaxValue)).Should().Be(new DateOnly(2020, 1, 5));
        stats.DistinctCount.Should().Be(2);
    }

    [Fact]
    public void Compute_Decimal_MinMaxAndDistinct()
    {
        var values = new[] { 1.5m, 0.5m, 2.5m };
        var stats = ZoneMapComputer.Compute(values, ColumnFieldTypeCode.Decimal, 0, 3);
        stats.MinValue.Length.Should().Be(16);
        stats.MaxValue.Length.Should().Be(16);
        stats.DistinctCount.Should().Be(3);
    }

    [Fact]
    public void Compute_Boolean_AllTrue_MinAndMaxAreTrue()
    {
        var stats = ZoneMapComputer.Compute(new[] { true, true, true }, ColumnFieldTypeCode.Boolean, 0, 3);
        stats.MinValue.Should().Equal(new byte[] { 1 });
        stats.MaxValue.Should().Equal(new byte[] { 1 });
        stats.DistinctCount.Should().Be(1);
    }

    [Fact]
    public void Compute_Boolean_AllFalse_MinAndMaxAreFalse()
    {
        var stats = ZoneMapComputer.Compute(new[] { false, false }, ColumnFieldTypeCode.Boolean, 0, 2);
        stats.MinValue.Should().Equal(new byte[] { 0 });
        stats.MaxValue.Should().Equal(new byte[] { 0 });
        stats.DistinctCount.Should().Be(1);
    }

    [Fact]
    public void Compute_Boolean_Mixed_DistinctCountTwo()
    {
        var stats = ZoneMapComputer.Compute(new[] { true, false, true, false }, ColumnFieldTypeCode.Boolean, 0, 4);
        stats.MinValue.Should().Equal(new byte[] { 0 });
        stats.MaxValue.Should().Equal(new byte[] { 1 });
        stats.DistinctCount.Should().Be(2);
    }

    [Fact]
    public void Compute_String_NullValuesCounted()
    {
        var values = new string?[] { "a", null, "c", null, "b" };
        var stats = ZoneMapComputer.Compute(values, ColumnFieldTypeCode.String, 0, 5);
        Encoding.UTF8.GetString(stats.MinValue).Should().Be("a");
        Encoding.UTF8.GetString(stats.MaxValue).Should().Be("c");
        stats.NullCount.Should().Be(2);
        stats.DistinctCount.Should().Be(4); // a, b, c, null
    }

    [Fact]
    public void Compute_String_AllNull_EmptyMinMax()
    {
        var values = new string?[] { null, null };
        var stats = ZoneMapComputer.Compute(values, ColumnFieldTypeCode.String, 0, 2);
        stats.MinValue.Should().BeEmpty();
        stats.MaxValue.Should().BeEmpty();
        stats.NullCount.Should().Be(2);
    }

    [Fact]
    public void Compute_String_TruncatesAt256Bytes()
    {
        // Min/max strings get truncated to the first 256 UTF-8 bytes.
        var longString = new string('x', 1024);
        var values = new string?[] { longString };
        var stats = ZoneMapComputer.Compute(values, ColumnFieldTypeCode.String, 0, 1);
        stats.MinValue.Length.Should().Be(256);
        stats.MaxValue.Length.Should().Be(256);
    }

    [Fact]
    public void Compute_String_Utf8MultibyteAtBoundary_TruncatesOnCodepointBoundary()
    {
        // Build a string whose 256th-byte cut would split a multi-byte UTF-8 sequence.
        // 'ñ' is 2 bytes in UTF-8 — interleave to land mid-character at byte 256.
        // 256 / 2 = 128 'ñ' chars take exactly 256 bytes — add one more 'ñ' to overflow,
        // forcing the truncator to walk back to the last codepoint boundary.
        var s = new string('ñ', 130);
        var stats = ZoneMapComputer.Compute(new string?[] { s }, ColumnFieldTypeCode.String, 0, 1);
        // Length must be ≤ 256 AND must be a multiple of 2 (since every char encodes to 2 bytes
        // and the truncator walks back to a codepoint boundary).
        stats.MinValue.Length.Should().BeLessThanOrEqualTo(256);
        (stats.MinValue.Length % 2).Should().Be(0, "truncation should not split a 2-byte UTF-8 sequence");
    }

    [Fact]
    public void Compute_UnsupportedFieldType_Throws()
    {
        var values = new int[] { 1 };
        Action act = () => ZoneMapComputer.Compute(values, (ColumnFieldTypeCode)0xFE, 0, 1);
        act.Should().Throw<NotSupportedException>().WithMessage("*field type*");
    }

    [Fact]
    public void Compute_FirstRowIndex_OffsetRespected()
    {
        // Stats should only consider the slice [first..first+count].
        var values = new[] { 999, 1, 2, 3, 999 };
        var stats = ZoneMapComputer.Compute(values, ColumnFieldTypeCode.Int32, firstRowIndex: 1, rowCount: 3);
        BitConverter.ToInt32(stats.MinValue).Should().Be(1);
        BitConverter.ToInt32(stats.MaxValue).Should().Be(3);
    }

    // ───── AC-10.4 — NaN-exclusion (Double / Single) ─────

    [Fact]
    public void Compute_Double_NaNExcludedFromMinMaxBound()
    {
        // NaN is unordered; it must never seed or pollute the min/max bound. With a NaN FIRST,
        // an unguarded computer would seed min = max = NaN (Comparer ranks NaN below everything),
        // collapsing the bound and making a downstream zone-map skip falsely prove disjointness.
        // The bound must be computed over the finite values only.
        var values = new[] { double.NaN, 1.0, 5.0, double.NaN, 3.0 };
        var stats = ZoneMapComputer.Compute(values, ColumnFieldTypeCode.Double, 0, 5);

        BitConverter.ToDouble(stats.MinValue).Should().Be(1.0, "NaN must not seed the lower bound");
        BitConverter.ToDouble(stats.MaxValue).Should().Be(5.0, "NaN must not seed the upper bound");
    }

    [Fact]
    public void Compute_Single_NaNExcludedFromMinMaxBound()
    {
        var values = new[] { float.NaN, 1.0f, 5.0f, float.NaN, 3.0f };
        var stats = ZoneMapComputer.Compute(values, ColumnFieldTypeCode.Single, 0, 5);

        BitConverter.ToSingle(stats.MinValue).Should().Be(1.0f, "NaN must not seed the lower bound");
        BitConverter.ToSingle(stats.MaxValue).Should().Be(5.0f, "NaN must not seed the upper bound");
    }

    [Fact]
    public void Compute_Double_AllNaN_EmptyBound()
    {
        // No ordered value exists — the bound must be empty so the reader treats the group as
        // unskippable rather than skipping on a NaN-derived min/max.
        var values = new[] { double.NaN, double.NaN };
        var stats = ZoneMapComputer.Compute(values, ColumnFieldTypeCode.Double, 0, 2);

        stats.MinValue.Should().BeEmpty("an all-NaN group has no ordered lower bound");
        stats.MaxValue.Should().BeEmpty("an all-NaN group has no ordered upper bound");
    }

    // ───── AC-10.4 — ComputeEnum (int-underlying and ulong-underlying widening) ─────

    [Fact]
    public void Compute_Enum_IntUnderlying_MinMaxFollowSignedWidenedOrder()
    {
        // TradeVenue: Unknown = -1, Nyse = 0, Nasdaq = 1, Otc = 2. The enum widens to int64;
        // min/max follow the widened signed order, so Unknown (-1) is min and Otc (2) is max.
        var values = new[] { TradeVenue.Nasdaq, TradeVenue.Unknown, TradeVenue.Otc, TradeVenue.Nyse };
        var stats = ZoneMapComputer.Compute(values, ColumnFieldTypeCode.Enum, 0, 4);

        BitConverter.ToInt64(stats.MinValue).Should().Be(-1L, "Unknown = -1 is the signed-widened min");
        BitConverter.ToInt64(stats.MaxValue).Should().Be(2L, "Otc = 2 is the signed-widened max");
        stats.DistinctCount.Should().Be(4);
    }

    [Fact]
    public void Compute_Enum_UlongUnderlyingAboveLongMax_WidensToNegativeBitTransport()
    {
        // SessionFlags.High = 0x8000_0000_0000_0001 widens (two's complement) to a NEGATIVE int64.
        // ComputeEnum uses the same bit-transport widening as the encode and query sides, so the
        // bound must carry the widened (negative) value verbatim — internal consistency over the
        // sign boundary is exactly why the range-skip guard exists (see CanSkipByZoneMap).
        var values = new[] { SessionFlags.Low, SessionFlags.High, SessionFlags.None };
        var stats = ZoneMapComputer.Compute(values, ColumnFieldTypeCode.Enum, 0, 3);

        var expectedHighWidened = unchecked((long)0x8000_0000_0000_0001UL);
        BitConverter.ToInt64(stats.MinValue).Should().Be(expectedHighWidened,
            "High widens to a negative int64, ranking as the signed-widened min");
        BitConverter.ToInt64(stats.MaxValue).Should().Be(5L, "Low = 5 is the signed-widened max");
        stats.DistinctCount.Should().Be(3);
    }
}
