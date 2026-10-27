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
using Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar;
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar;
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar.Codecs;
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar.Footer;

namespace Boutquin.Storage.Infrastructure.Tests.TimeSeries.Columnar;

/// <summary>
/// Finding #8 — value-predicate pushdown over a columnar segment. Activates the zone-map and
/// bitmap-index footer sections (previously computed and stored but never consulted) to skip whole
/// row groups that cannot match, with a row-level filter guaranteeing exact results (DDIA 2e Ch. 4
/// — data-skipping indexes).
/// </summary>
public sealed class ColumnarPredicatePushdownTests
{
    // ───── ColumnPredicate.Matches (row-level exactness) ─────────────────────

    [Theory]
    [InlineData(ComparisonOperator.Equal, 5, 5, true)]
    [InlineData(ComparisonOperator.Equal, 5, 6, false)]
    [InlineData(ComparisonOperator.NotEqual, 5, 6, true)]
    [InlineData(ComparisonOperator.NotEqual, 5, 5, false)]
    [InlineData(ComparisonOperator.LessThan, 4, 5, true)]
    [InlineData(ComparisonOperator.LessThan, 5, 5, false)]
    [InlineData(ComparisonOperator.LessThanOrEqual, 5, 5, true)]
    [InlineData(ComparisonOperator.LessThanOrEqual, 6, 5, false)]
    [InlineData(ComparisonOperator.GreaterThan, 6, 5, true)]
    [InlineData(ComparisonOperator.GreaterThan, 5, 5, false)]
    [InlineData(ComparisonOperator.GreaterThanOrEqual, 5, 5, true)]
    [InlineData(ComparisonOperator.GreaterThanOrEqual, 4, 5, false)]
    public void Matches_Int(ComparisonOperator op, int cell, int value, bool expected)
        => new ColumnPredicate("c", op, value).Matches(cell).Should().Be(expected);

    [Fact]
    public void Matches_Decimal_And_String_And_DateOnly()
    {
        new ColumnPredicate("c", ComparisonOperator.GreaterThan, 1.5m).Matches(2.0m).Should().BeTrue();
        new ColumnPredicate("c", ComparisonOperator.Equal, "AAPL").Matches("AAPL").Should().BeTrue();
        new ColumnPredicate("c", ComparisonOperator.Equal, "AAPL").Matches("MSFT").Should().BeFalse();
        new ColumnPredicate("c", ComparisonOperator.LessThan, new DateOnly(2026, 1, 5))
            .Matches(new DateOnly(2026, 1, 1)).Should().BeTrue();
    }

    [Fact]
    public void Matches_NullCell_OnlyEqualsNullValue()
    {
        new ColumnPredicate("c", ComparisonOperator.Equal, "x").Matches(null).Should().BeFalse();
        new ColumnPredicate("c", ComparisonOperator.GreaterThan, "x").Matches(null).Should().BeFalse();
    }

    // A predicate is evaluated against zone maps and bitmap indexes before any row: a null column
    // name or comparison value has no defined reading there, so it is refused at construction (and
    // through `with`) instead of failing mid-read with a raw NullReferenceException.
    [Fact]
    public void Constructor_NullColumnNameOrValue_ThrowsArgumentNull()
    {
        var nullName = () => new ColumnPredicate(null!, ComparisonOperator.Equal, 1);
        var nullValue = () => new ColumnPredicate("c", ComparisonOperator.NotEqual, null!);

        nullName.Should().Throw<ArgumentNullException>().WithParameterName("ColumnName");
        nullValue.Should().Throw<ArgumentNullException>().WithParameterName("Value");
    }

    [Fact]
    public void With_NullColumnNameOrValue_ThrowsArgumentNull()
    {
        var predicate = new ColumnPredicate("c", ComparisonOperator.Equal, 1);

        var nullName = () => predicate with { ColumnName = null! };
        var nullValue = () => predicate with { Value = null! };

        nullName.Should().Throw<ArgumentNullException>().WithParameterName("ColumnName");
        nullValue.Should().Throw<ArgumentNullException>().WithParameterName("Value");
    }

    // An undefined operator matches no row and skips no row group, so a read would silently return
    // an empty result instead of failing; it is refused at construction, like an undefined
    // SortDirection in ColumnSortSpec.
    [Fact]
    public void Constructor_UndefinedOperator_ThrowsArgumentOutOfRange()
    {
        var act = () => new ColumnPredicate("c", (ComparisonOperator)99, 1);

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("Operator");
    }

    [Fact]
    public void With_UndefinedOperator_ThrowsArgumentOutOfRange()
    {
        var predicate = new ColumnPredicate("c", ComparisonOperator.Equal, 1);

        var act = () => predicate with { Operator = (ComparisonOperator)6 };

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("Operator");
    }

    [Fact]
    public void Matches_NullCell_SatisfiesOnlyNotEqual()
    {
        new ColumnPredicate("c", ComparisonOperator.NotEqual, "x").Matches(null).Should().BeTrue();
        new ColumnPredicate("c", ComparisonOperator.LessThan, "x").Matches(null).Should().BeFalse();
    }

    [Fact]
    public void Matches_String_IsOrdinal_NotCultureSensitive()
    {
        // The column's documented order is ordinal byte-lex (ZoneMapComputer + the generated
        // columnar sort comparers use string.CompareOrdinal). The row-level predicate filter must
        // agree byte-for-byte, otherwise a row group skipped by the zone map can disagree with the
        // row filter, producing phantom or dropped rows under a non-invariant locale.
        //
        // Ordinal: 'a' (0x61) > 'B' (0x42), so "a" < "B" is FALSE under ordinal order. Under most
        // linguistic cultures (en-US, fr-FR, …) "a" sorts before "B", making the same comparison
        // TRUE. Pin the ordinal answer regardless of the ambient CI culture.
        var original = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture =
                System.Globalization.CultureInfo.GetCultureInfo("en-US");

            new ColumnPredicate("c", ComparisonOperator.LessThan, "B").Matches("a").Should().BeFalse();
            new ColumnPredicate("c", ComparisonOperator.GreaterThan, "B").Matches("a").Should().BeTrue();
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = original;
        }
    }

    [Theory]
    [InlineData(ComparisonOperator.Equal)]
    [InlineData(ComparisonOperator.NotEqual)]
    [InlineData(ComparisonOperator.LessThan)]
    [InlineData(ComparisonOperator.LessThanOrEqual)]
    [InlineData(ComparisonOperator.GreaterThan)]
    [InlineData(ComparisonOperator.GreaterThanOrEqual)]
    public void Matches_NaNCell_MatchesNoOperator_Double(ComparisonOperator op)
    {
        // NaN is unordered: it matches no predicate. The row filter must agree with the zone map,
        // which excludes NaN from [min,max]. Without this guard .NET's total order (NaN < everything,
        // NaN == NaN) produced phantom `<`/`<=` matches and, against a NaN-excluding zone map that
        // proves a finite group disjoint, dropped `!=` matches.
        new ColumnPredicate("c", op, 5.0).Matches(double.NaN).Should().BeFalse();
    }

    [Theory]
    [InlineData(ComparisonOperator.Equal)]
    [InlineData(ComparisonOperator.NotEqual)]
    [InlineData(ComparisonOperator.LessThan)]
    [InlineData(ComparisonOperator.GreaterThan)]
    public void Matches_NaNCell_MatchesNoOperator_Single(ComparisonOperator op)
        => new ColumnPredicate("c", op, 5.0f).Matches(float.NaN).Should().BeFalse();

    [Fact]
    public void Matches_NaNPredicateValue_MatchesNothing()
    {
        // A NaN query bound is unsatisfiable on every operator — no finite cell matches it, and the
        // zone map cannot prove anything against it either.
        new ColumnPredicate("c", ComparisonOperator.Equal, double.NaN).Matches(5.0).Should().BeFalse();
        new ColumnPredicate("c", ComparisonOperator.NotEqual, double.NaN).Matches(5.0).Should().BeFalse();
        new ColumnPredicate("c", ComparisonOperator.LessThan, double.NaN).Matches(5.0).Should().BeFalse();
    }

    // ───── Zone-map skip math ────────────────────────────────────────────────

    private static ColumnRowGroupStats Int32Stats(int min, int max)
        => new(BitConverter.GetBytes(min), BitConverter.GetBytes(max), NullCount: 0, DistinctCount: 2);

    [Theory]
    // range [10, 20]; predicate value 5 ⇒ skip for Equal/Less* (all cells > 5), keep for Greater*.
    [InlineData(ComparisonOperator.Equal, 5, true)]
    [InlineData(ComparisonOperator.Equal, 15, false)]
    [InlineData(ComparisonOperator.Equal, 25, true)]
    [InlineData(ComparisonOperator.LessThan, 10, true)]   // min(10) >= 10 ⇒ no cell < 10
    [InlineData(ComparisonOperator.LessThan, 11, false)]
    [InlineData(ComparisonOperator.LessThanOrEqual, 9, true)]
    [InlineData(ComparisonOperator.LessThanOrEqual, 10, false)]
    [InlineData(ComparisonOperator.GreaterThan, 20, true)]   // max(20) <= 20 ⇒ no cell > 20
    [InlineData(ComparisonOperator.GreaterThan, 19, false)]
    [InlineData(ComparisonOperator.GreaterThanOrEqual, 21, true)]
    [InlineData(ComparisonOperator.GreaterThanOrEqual, 20, false)]
    public void CanSkipByZoneMap_Int32Range(ComparisonOperator op, int value, bool expectedSkip)
        => ColumnarPredicatePushdown
            .CanSkipByZoneMap(Int32Stats(10, 20), op, value, ColumnFieldTypeCode.Int32)
            .Should().Be(expectedSkip);

    [Fact]
    public void CanSkipByZoneMap_NotEqual_OnlySkipsConstantGroupEqualToValue()
    {
        ColumnarPredicatePushdown.CanSkipByZoneMap(Int32Stats(7, 7), ComparisonOperator.NotEqual, 7, ColumnFieldTypeCode.Int32)
            .Should().BeTrue();
        ColumnarPredicatePushdown.CanSkipByZoneMap(Int32Stats(7, 8), ComparisonOperator.NotEqual, 7, ColumnFieldTypeCode.Int32)
            .Should().BeFalse();
    }

    [Fact]
    public void CanSkipByZoneMap_EmptyStats_NeverSkips()
        => ColumnarPredicatePushdown
            .CanSkipByZoneMap(new ColumnRowGroupStats([], [], 3, 0), ComparisonOperator.Equal, 5, ColumnFieldTypeCode.Int32)
            .Should().BeFalse();

    [Fact]
    public void CanSkipByZoneMap_DoubleColumnWithNaNSeed_DoesNotSkipMatchingFiniteRows()
    {
        // A row group whose FIRST value is NaN must not poison the zone map. NaN is unordered, so
        // v < min / v > max are both false for it; if it seeds min/max the bound collapses to NaN,
        // which Comparer.Default ranks below everything — making a "> 0.0" predicate falsely prove the
        // group disjoint and skip the matching finite rows 1.0 and 5.0 (silent read data-loss).
        var stats = ZoneMapComputer.Compute(new[] { double.NaN, 1.0, 5.0 }, ColumnFieldTypeCode.Double, 0, 3);
        ColumnarPredicatePushdown
            .CanSkipByZoneMap(stats, ComparisonOperator.GreaterThan, 0.0, ColumnFieldTypeCode.Double)
            .Should().BeFalse("rows 1.0 and 5.0 satisfy > 0.0; a NaN seed must not collapse the bound");
    }

    [Fact]
    public void CanSkipByZoneMap_SingleColumnWithNaNSeed_DoesNotSkipMatchingFiniteRows()
    {
        var stats = ZoneMapComputer.Compute(new[] { float.NaN, 1.0f, 5.0f }, ColumnFieldTypeCode.Single, 0, 3);
        ColumnarPredicatePushdown
            .CanSkipByZoneMap(stats, ComparisonOperator.GreaterThan, 0.0f, ColumnFieldTypeCode.Single)
            .Should().BeFalse("rows 1.0f and 5.0f satisfy > 0.0f; a NaN seed must not collapse the bound");
    }

    [Fact]
    public void CanSkipByZoneMap_AllNaNDoubleColumn_NeverSkips()
    {
        // No finite value exists, so there is no ordered bound — the group must be treated as
        // unskippable (empty stats), never skipped on a NaN-derived min/max.
        var stats = ZoneMapComputer.Compute(new[] { double.NaN, double.NaN }, ColumnFieldTypeCode.Double, 0, 2);
        ColumnarPredicatePushdown
            .CanSkipByZoneMap(stats, ComparisonOperator.GreaterThan, 0.0, ColumnFieldTypeCode.Double)
            .Should().BeFalse();
    }

    [Fact]
    public void CanSkipByZoneMap_String_NeverSkips_TruncationSafe()
        => ColumnarPredicatePushdown
            .CanSkipByZoneMap(
                new ColumnRowGroupStats(Encoding.UTF8.GetBytes("AAA"), Encoding.UTF8.GetBytes("MMM"), 0, 2),
                ComparisonOperator.GreaterThan, "ZZZ", ColumnFieldTypeCode.String)
            .Should().BeFalse();

    // ───── Bitmap-index skip (hand-built footer) ─────────────────────────────

    [Fact]
    public void CanSkipRowGroup_BitmapIndex_SkipsGroupsWithNoSetBit_AndAbsentValues()
    {
        // Two row groups: rows [0..3] and [4..7]. The indexed column "Cat" has value 5 only at
        // rows 4 and 5 (row group 1). Value 99 never appears.
        var presence = new[] { false, false, false, false, true, true, false, false };
        var bitmap = BitmapRleCodec.Encode(presence);
        var entry = new BitmapIndexEntry("Cat",
            [new BitmapIndexValue(BitConverter.GetBytes(5), bitmap)]);

        var d = new DateOnly(2026, 1, 1);
        var footer = new SegmentFooter(
            FormatVersion: 1,
            Columns: [new ColumnDescriptor("Cat", 0, 0, ColumnEncodingTag.DeltaVlq, 1, ColumnFieldTypeCode.Int32)],
            RowGroups:
            [
                new RowGroupDescriptor(d, d, RowCount: 4, FirstRowIndex: 0),
                new RowGroupDescriptor(d, d, RowCount: 4, FirstRowIndex: 4),
            ],
            ZoneMaps: null,
            BitmapIndexes: [entry]);

        var eq5 = new[] { new ColumnPredicate("Cat", ComparisonOperator.Equal, 5) };
        CompressedColumnarSegmentFormat<ColumnarBar>.CanSkipRowGroup(footer, 0, eq5).Should().BeTrue("no row in group 0 holds 5");
        CompressedColumnarSegmentFormat<ColumnarBar>.CanSkipRowGroup(footer, 1, eq5).Should().BeFalse("rows 4,5 hold 5");

        var eq99 = new[] { new ColumnPredicate("Cat", ComparisonOperator.Equal, 99) };
        CompressedColumnarSegmentFormat<ColumnarBar>.CanSkipRowGroup(footer, 0, eq99).Should().BeTrue("99 is absent from the segment");
        CompressedColumnarSegmentFormat<ColumnarBar>.CanSkipRowGroup(footer, 1, eq99).Should().BeTrue();
    }

    // ───── End-to-end StreamWhereAsync (exact results + row-group skipping) ──

    private static byte[] WriteBars(int rowGroupMaxRows)
    {
        var bars = new List<ColumnarBar>();
        for (var i = 0; i < 8; i++)
        {
            bars.Add(new ColumnarBar
            {
                Date = new DateOnly(2026, 1, 1).AddDays(i),
                Ticker = "T",
                Close = 100m + i,
                Volume = 1000 + i,
                TradeCount = i < 4 ? 10 + i : 100 + (i - 4), // rg0: 10..13, rg1: 100..103
                Vwap = 1.0 + i,
                IsAdjusted = false,
                Residual = 0.5 + i,
            });
        }

        var format = new CompressedColumnarSegmentFormat<ColumnarBar>(rowGroupMaxRows: rowGroupMaxRows);
        using var stream = new MemoryStream();
        format.WriteAsync(stream, bars).GetAwaiter().GetResult();
        return stream.ToArray();
    }

    private static async Task<List<ColumnarBar>> StreamWhere(byte[] bytes, params ColumnPredicate[] predicates)
    {
        var format = new CompressedColumnarSegmentFormat<ColumnarBar>(rowGroupMaxRows: 4);
        using var stream = new MemoryStream(bytes);
        var result = new List<ColumnarBar>();
        await foreach (var r in format.StreamWhereAsync(
            stream, DateOnly.MinValue, DateOnly.MaxValue, ColumnProjection.All, predicates).ConfigureAwait(true))
        {
            result.Add(r);
        }
        return result;
    }

    [Fact]
    public async Task StreamWhere_GreaterThanOrEqual_ReturnsOnlyMatchingRowGroup_AndSkipsTheOther()
    {
        var bytes = WriteBars(rowGroupMaxRows: 4); // 2 row groups
        var pred = new ColumnPredicate("TradeCount", ComparisonOperator.GreaterThanOrEqual, 100);

        var rows = await StreamWhere(bytes, pred);

        rows.Should().HaveCount(4);
        rows.Should().OnlyContain(b => b.TradeCount >= 100);

        // The first row group (TradeCount 10..13) is provably disjoint and must be skipped.
        var footer = FooterReader.Read(new MemoryStream(bytes));
        CompressedColumnarSegmentFormat<ColumnarBar>.CanSkipRowGroup(footer, 0, [pred]).Should().BeTrue();
        CompressedColumnarSegmentFormat<ColumnarBar>.CanSkipRowGroup(footer, 1, [pred]).Should().BeFalse();
    }

    [Fact]
    public async Task StreamWhere_Equality_ReturnsExactRow_AcrossRowGroups()
    {
        var bytes = WriteBars(rowGroupMaxRows: 4);
        var rows = await StreamWhere(bytes, new ColumnPredicate("TradeCount", ComparisonOperator.Equal, 11));

        rows.Should().ContainSingle().Which.TradeCount.Should().Be(11);
    }

    [Fact]
    public async Task StreamWhere_RangeSpanningBothGroups_DoesNotOverSkip()
    {
        var bytes = WriteBars(rowGroupMaxRows: 4);
        // TradeCount > 12 matches 13 (group 0) plus all of group 1 (100..103) — 5 rows.
        var rows = await StreamWhere(bytes, new ColumnPredicate("TradeCount", ComparisonOperator.GreaterThan, 12));

        rows.Should().HaveCount(5);
        rows.Should().OnlyContain(b => b.TradeCount > 12);
    }

    [Fact]
    public async Task StreamWhere_EmptyPredicates_EqualsPlainRangeRead()
    {
        var bytes = WriteBars(rowGroupMaxRows: 4);
        var rows = await StreamWhere(bytes);
        rows.Should().HaveCount(8);
    }
    [Fact]
    public void CanSkipByZoneMap_NotEqual_GroupWithNulls_NeverSkips()
    {
        // A constant group {7, 7, null} still matches `!= 7`: the null cell satisfies NotEqual
        // (ColumnPredicate.Matches returns true for null vs non-null). Skipping on min==max==value
        // alone would drop the matching null row — the guard must also require NullCount == 0.
        // Nulls are currently unreachable through the writer (it rejects null values and
        // Nullable<T> columns), so this pins the conservative contract for any future
        // null-enabling change rather than a live data path.
        var statsWithNull = new ColumnRowGroupStats(
            BitConverter.GetBytes(7), BitConverter.GetBytes(7), NullCount: 1, DistinctCount: 1);

        ColumnarPredicatePushdown
            .CanSkipByZoneMap(statsWithNull, ComparisonOperator.NotEqual, 7, ColumnFieldTypeCode.Int32)
            .Should().BeFalse("the null cell matches != 7, so the group contains a match");
    }

    [Fact]
    public void CanSkipByZoneMap_PredicateValueOfWrongClrType_FailsClosedWithoutThrowing()
    {
        // A predicate whose Value CLR type does not match the column type (long literal against
        // an Int32 column) must fail closed — return "cannot skip" — exactly like the bitmap
        // path's EncodeValue (which returns null ⇒ no skip). Previously the boxed
        // Comparer.Default.Compare threw ArgumentException mid-read.
        var act = () => ColumnarPredicatePushdown
            .CanSkipByZoneMap(Int32Stats(10, 20), ComparisonOperator.Equal, 5L, ColumnFieldTypeCode.Int32);

        act.Should().NotThrow().Which.Should().BeFalse("an unprovable skip must be conservative");
    }

    [Theory]
    [InlineData(ComparisonOperator.LessThan)]
    [InlineData(ComparisonOperator.LessThanOrEqual)]
    [InlineData(ComparisonOperator.GreaterThan)]
    [InlineData(ComparisonOperator.GreaterThanOrEqual)]
    public void CanSkipByZoneMap_UlongEnum_RangeOperator_NeverSkips(ComparisonOperator op)
    {
        // A ulong-underlying enum member above long.MaxValue widens (two's complement) to a
        // NEGATIVE int64, so the SIGNED order of the widened zone-map bounds diverges from the
        // UNSIGNED Enum.CompareTo order the row-level filter applies. A range skip across that
        // divergence silently drops matching rows; the guard must defer every range operator to
        // the row filter (no skip) for a ulong-underlying enum. SessionFlags.High widens negative.
        var min = WidenedEnumBytes(SessionFlags.Low);   // 5
        var max = WidenedEnumBytes(SessionFlags.High);  // negative widened
        var stats = new ColumnRowGroupStats(min, max, NullCount: 0, DistinctCount: 2);

        ColumnarPredicatePushdown
            .CanSkipByZoneMap(stats, op, SessionFlags.Low, ColumnFieldTypeCode.Enum)
            .Should().BeFalse("range skips are unsound across the ulong sign boundary — defer to the row filter");
    }

    [Fact]
    public void CanSkipByZoneMap_UlongEnum_Equal_StaysSkippable()
    {
        // Equality stays skippable for a ulong-underlying enum even though range is not: a value
        // outside the [min, max] widened span genuinely cannot appear, and the widened byte key is
        // sign-agnostic for equality. Constant group {Low}, probe None (outside) ⇒ skippable.
        var bound = WidenedEnumBytes(SessionFlags.Low);
        var stats = new ColumnRowGroupStats(bound, bound, NullCount: 0, DistinctCount: 1);

        ColumnarPredicatePushdown
            .CanSkipByZoneMap(stats, ComparisonOperator.Equal, SessionFlags.None, ColumnFieldTypeCode.Enum)
            .Should().BeTrue("no cell in a constant {Low} group equals None — Equal can skip");
    }

    [Fact]
    public void CanSkipByZoneMap_UlongEnum_NotEqual_DefersByValuePresence()
    {
        // NotEqual on a constant {Low} group probing None has matches (every Low cell satisfies
        // != None), so it must NOT skip — distinct from a range defer; the equality arm is still
        // reached (not blanket-rejected like range), it just evaluates to "cannot skip" here.
        var bound = WidenedEnumBytes(SessionFlags.Low);
        var stats = new ColumnRowGroupStats(bound, bound, NullCount: 0, DistinctCount: 1);

        ColumnarPredicatePushdown
            .CanSkipByZoneMap(stats, ComparisonOperator.NotEqual, SessionFlags.None, ColumnFieldTypeCode.Enum)
            .Should().BeFalse("every Low cell satisfies != None, so the group has matches");
    }

    private static byte[] WidenedEnumBytes(SessionFlags value)
    {
        // Mirror ZoneMapComputer.ComputeEnum's bit-transport widening for a ulong underlying.
        var widened = unchecked((long)Convert.ToUInt64(value, System.Globalization.CultureInfo.InvariantCulture));
        return BitConverter.GetBytes(widened);
    }

    [Fact]
    public async Task StreamWhere_FloatColumnPredicate_ReturnsExactRows()
    {
        // The Single zone-map/predicate arms became reachable with float column support —
        // pin the end-to-end pushdown: write float rows across two row groups, filter with a
        // float predicate, expect exact matches (and no false skip).
        var bars = Enumerable.Range(0, 8).Select(i => new FloatBar
        {
            Date = new DateOnly(2026, 1, 1).AddDays(i),
            Residual = i < 4 ? 1.0f + i : 100.0f + i, // rg0: 1..4, rg1: 104..107
        }).ToList();

        var format = new CompressedColumnarSegmentFormat<FloatBar>(rowGroupMaxRows: 4);
        using var stream = new MemoryStream();
        await format.WriteAsync(stream, bars).ConfigureAwait(true);
        stream.Position = 0;

        var rows = new List<FloatBar>();
        await foreach (var r in format.StreamWhereAsync(
            stream, DateOnly.MinValue, DateOnly.MaxValue, ColumnProjection.All,
            [new ColumnPredicate("Residual", ComparisonOperator.GreaterThan, 100.0f)]).ConfigureAwait(true))
        {
            rows.Add(r);
        }

        rows.Should().HaveCount(4);
        rows.Should().OnlyContain(b => b.Residual > 100.0f);
    }

}
