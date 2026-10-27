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
/// End-to-end bit-exactness pins for the columnar write→encode→decode→Rehydrate path. The
/// codec unit tests already pin bit-exactness per codec; these pins cover the full path —
/// typed-array build, codec dispatch, footer round-trip, and generated Rehydrate — so a
/// miswire anywhere between record and disk fails loudly. Multi-row-group shapes double as
/// cross-column row-alignment checks (record equality fails if any column shifts by a row).
/// </summary>
public sealed class ColumnarBitExactnessPinsTests
{
    private static async Task<List<ColumnarBar>> RoundTripAsync(
        IReadOnlyList<ColumnarBar> input, int rowGroupMaxRows = 3)
    {
        var format = new CompressedColumnarSegmentFormat<ColumnarBar>(rowGroupMaxRows: rowGroupMaxRows);
        using var stream = new MemoryStream();
        await format.WriteAsync(stream, input).ConfigureAwait(true);
        stream.Position = 0;

        var output = new List<ColumnarBar>();
        await foreach (var r in format.ReadAsync(
            stream, DateOnly.MinValue, DateOnly.MaxValue,
            Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar.ColumnProjection.All).ConfigureAwait(true))
        {
            output.Add(r);
        }

        return output;
    }

    [Fact]
    public async Task RoundTrip_DoubleColumns_PreserveNaNPayloadInfAndSignedZeroBitExact()
    {
        // Vwap routes through GorillaXor, Residual through RawDouble — both must preserve the
        // exact IEEE-754 bit pattern, including a non-canonical NaN payload, ±Inf, -0.0, and a
        // subnormal. Equality comparison would pass for distinct NaN payloads and collapse
        // -0.0 == +0.0; the assertion below compares raw bits.
        var payloadNaN = BitConverter.Int64BitsToDouble(unchecked((long)0x7FF8_0000_DEAD_BEEFUL));
        var doubles = new[]
        {
            payloadNaN, double.NegativeInfinity, double.PositiveInfinity,
            -0.0, double.Epsilon, 1.0 / 3.0,
        };
        var d0 = new DateOnly(2024, 1, 1);
        var input = doubles.Select((v, i) => new ColumnarBar
        {
            Date = d0.AddDays(i),
            Ticker = $"T{i}",
            Close = i,
            Volume = i,
            TradeCount = i,
            Vwap = v,
            IsAdjusted = i % 2 == 0,
            Residual = v,
        }).ToList();

        var output = await RoundTripAsync(input).ConfigureAwait(true);

        output.Should().HaveCount(input.Count);
        for (var i = 0; i < input.Count; i++)
        {
            BitConverter.DoubleToInt64Bits(output[i].Vwap).Should()
                .Be(BitConverter.DoubleToInt64Bits(input[i].Vwap), $"Vwap row {i} (GorillaXor) must be bit-exact");
            BitConverter.DoubleToInt64Bits(output[i].Residual).Should()
                .Be(BitConverter.DoubleToInt64Bits(input[i].Residual), $"Residual row {i} (RawDouble) must be bit-exact");
        }
    }

    [Fact]
    public async Task RoundTrip_DictionaryDecimalColumn_IsValuePreserving_DistinctValuesBitExact()
    {
        // The columnar decimal contract is VALUE-preserving, not representation-preserving:
        // the dictionary keys distinct values by decimal equality (scale-insensitive), so
        // value-equal inputs of different scales (1m vs 1.0m) collapse to one stored
        // representation (first-encountered, deterministically); ScaledIntegerBitPacked
        // likewise re-scales to the column's common scale. Distinct VALUES, however, must
        // round-trip bit-exactly — that is what this pin asserts.
        var decimals = new[] { 1m, 1.0m, 1.00m, 0.5m, 0.50m, 123.456m };
        var d0 = new DateOnly(2024, 1, 1);
        var input = decimals.Select((v, i) => new ColumnarBar
        {
            Date = d0.AddDays(i),
            Ticker = "X",
            Close = v,
            Volume = i,
            TradeCount = i,
            Vwap = i,
            IsAdjusted = false,
            Residual = i,
        }).ToList();

        var output = await RoundTripAsync(input).ConfigureAwait(true);

        output.Should().HaveCount(input.Count);
        // Value equality for every row (decimal == is scale-insensitive)…
        output.Select(b => b.Close).Should().Equal(decimals);
        // …and bit-exact representation for rows whose value appears at a single scale.
        decimal.GetBits(output[5].Close).Should().Equal(decimal.GetBits(123.456m));
    }

    [Fact]
    public async Task RoundTrip_MultiRowGroup_AllColumnsStayRowAligned()
    {
        // Every column carries a position-dependent value; record equality on read-back fails
        // if any column's values shift relative to another (DDIA 2e: "each column storing the
        // rows in the same order"). Three row groups exercise the per-row-group slicing.
        var d0 = new DateOnly(2024, 1, 1);
        var input = Enumerable.Range(0, 8).Select(i => new ColumnarBar
        {
            Date = d0.AddDays(i),
            Ticker = $"TK{i}",
            Close = 100m + (i * 0.25m),
            Volume = 1_000 + i,
            TradeCount = 10 + i,
            Vwap = 100.0 + (i * 0.5),
            IsAdjusted = i % 2 == 0,
            Residual = i * 0.001,
        }).ToList();

        var output = await RoundTripAsync(input, rowGroupMaxRows: 3).ConfigureAwait(true);

        output.Should().Equal(input);
    }
    [Fact]
    public async Task RoundTrip_FloatColumn_PreservesNaNPayloadInfAndSignedZeroBitExact()
    {
        // float columns ride the GorillaXor stream as zero-extended raw float bits (no FP
        // widening conversion, which could quiet a signaling NaN) — the round-trip must be
        // bit-exact for NaN payloads, ±Inf, -0f, a subnormal, and ordinary values. Also
        // covers the CRC path (RowGroupCanonicalBytes Single arm) via ReadRowGroupsAsync.
        var payloadNaN = BitConverter.Int32BitsToSingle(unchecked((int)0xFFC0_BEEF));
        var floats = new[]
        {
            payloadNaN, float.NegativeInfinity, float.PositiveInfinity,
            -0.0f, float.Epsilon, 1.0f / 3.0f,
        };
        var d0 = new DateOnly(2024, 1, 1);
        var input = floats.Select((v, i) => new FloatBar
        {
            Date = d0.AddDays(i),
            Residual = v,
        }).ToList();

        var format = new CompressedColumnarSegmentFormat<FloatBar>(rowGroupMaxRows: 3);
        using var stream = new MemoryStream();
        await format.WriteAsync(stream, input).ConfigureAwait(true);
        stream.Position = 0;

        var output = new List<FloatBar>();
        await foreach (var r in format.ReadRowGroupsAsync(
            stream, DateOnly.MinValue, DateOnly.MaxValue).ConfigureAwait(true))
        {
            r.IsOk.Should().BeTrue("an intact float column must pass its row-group CRC");
            output.AddRange(r.Rows!);
        }

        output.Should().HaveCount(input.Count);
        for (var i = 0; i < input.Count; i++)
        {
            BitConverter.SingleToInt32Bits(output[i].Residual).Should()
                .Be(BitConverter.SingleToInt32Bits(input[i].Residual), $"Residual row {i} must be bit-exact");
        }
    }

    [Fact]
    public async Task RoundTrip_EnumColumns_PreserveValuesAcrossUnderlyings()
    {
        // Enum columns ride the DeltaVlq stream as the underlying value widened to int64.
        // Pins: negative members (signed widening), a 64-bit underlying at long.MaxValue,
        // multi-row-group slicing, and the row-group CRC path.
        var venues = new[]
        {
            TradeVenue.Unknown, TradeVenue.Nyse, TradeVenue.Nasdaq,
            TradeVenue.Otc, TradeVenue.Unknown, TradeVenue.Nasdaq,
        };
        var d0 = new DateOnly(2024, 1, 1);
        var input = venues.Select((v, i) => new EnumBar
        {
            Date = d0.AddDays(i),
            Venue = v,
            Quality = i % 2 == 0 ? QualityFlag.Raw : QualityFlag.Adjusted,
        }).ToList();

        var format = new CompressedColumnarSegmentFormat<EnumBar>(rowGroupMaxRows: 3);
        using var stream = new MemoryStream();
        await format.WriteAsync(stream, input).ConfigureAwait(true);
        stream.Position = 0;

        var output = new List<EnumBar>();
        await foreach (var r in format.ReadRowGroupsAsync(
            stream, DateOnly.MinValue, DateOnly.MaxValue).ConfigureAwait(true))
        {
            r.IsOk.Should().BeTrue("an intact enum column must pass its row-group CRC");
            output.AddRange(r.Rows!);
        }

        output.Should().Equal(input);
    }

    [Fact]
    public async Task StreamWhere_EnumColumnPredicate_ReturnsExactRows()
    {
        // Zone-map pushdown on an enum column: predicate value is the enum itself; the
        // skip math runs on the widened int64 bounds on both sides.
        var d0 = new DateOnly(2024, 1, 1);
        var input = Enumerable.Range(0, 8).Select(i => new EnumBar
        {
            Date = d0.AddDays(i),
            Venue = i < 4 ? TradeVenue.Nyse : TradeVenue.Otc, // rg0 all Nyse, rg1 all Otc
            Quality = QualityFlag.Raw,
        }).ToList();

        var format = new CompressedColumnarSegmentFormat<EnumBar>(rowGroupMaxRows: 4);
        using var stream = new MemoryStream();
        await format.WriteAsync(stream, input).ConfigureAwait(true);
        stream.Position = 0;

        var rows = new List<EnumBar>();
        await foreach (var r in format.StreamWhereAsync(
            stream, DateOnly.MinValue, DateOnly.MaxValue,
            Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar.ColumnProjection.All,
            [new Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar.ColumnPredicate(
                "Venue", ComparisonOperator.Equal, TradeVenue.Otc)]).ConfigureAwait(true))
        {
            rows.Add(r);
        }

        rows.Should().HaveCount(4);
        rows.Should().OnlyContain(b => b.Venue == TradeVenue.Otc);
    }

    /// <summary>
    /// Range predicate on a signed-underlying enum — the widened int64 zone-map order
    /// matches the enum's natural order, so the skip math itself is exercised (rg0 is
    /// provably disjoint and skipped, rg1 surfaces its exact rows).
    /// </summary>
    [Fact]
    public async Task StreamWhere_IntEnumRangePredicate_ReturnsExactRows()
    {
        var d0 = new DateOnly(2024, 1, 1);
        var input = Enumerable.Range(0, 8).Select(i => new EnumBar
        {
            Date = d0.AddDays(i),
            Venue = i < 4 ? TradeVenue.Nyse : TradeVenue.Otc, // rg0 all Nyse(0), rg1 all Otc(2)
            Quality = QualityFlag.Raw,
        }).ToList();

        var format = new CompressedColumnarSegmentFormat<EnumBar>(rowGroupMaxRows: 4);
        using var stream = new MemoryStream();
        await format.WriteAsync(stream, input).ConfigureAwait(true);
        stream.Position = 0;

        var rows = new List<EnumBar>();
        await foreach (var r in format.StreamWhereAsync(
            stream, DateOnly.MinValue, DateOnly.MaxValue,
            Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar.ColumnProjection.All,
            [new Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar.ColumnPredicate(
                "Venue", ComparisonOperator.GreaterThanOrEqual, TradeVenue.Otc)]).ConfigureAwait(true))
        {
            rows.Add(r);
        }

        rows.Should().HaveCount(4);
        rows.Should().OnlyContain(b => b.Venue == TradeVenue.Otc);
    }

    /// <summary>
    /// Conservative-skip contract for ulong-underlying enums: a member above
    /// <c>long.MaxValue</c> widens to a NEGATIVE int64, so the signed widened zone-map
    /// order diverges from the row filter's unsigned <c>Enum.CompareTo</c>. A range
    /// predicate the high member satisfies must never zone-map-skip its row group —
    /// the skip must stay a conservative superset of the row-level match.
    /// </summary>
    [Fact]
    public async Task StreamWhere_UlongEnumRangePredicate_DoesNotFalseSkipHighMembers()
    {
        var d0 = new DateOnly(2024, 1, 1);
        var input = Enumerable.Range(0, 8).Select(i => new UlongEnumBar
        {
            Date = d0.AddDays(i),
            Flags = i < 4 ? SessionFlags.Low : SessionFlags.High, // rg0 all Low, rg1 all High
        }).ToList();

        var format = new CompressedColumnarSegmentFormat<UlongEnumBar>(rowGroupMaxRows: 4);
        using var stream = new MemoryStream();
        await format.WriteAsync(stream, input).ConfigureAwait(true);
        stream.Position = 0;

        var rows = new List<UlongEnumBar>();
        await foreach (var r in format.StreamWhereAsync(
            stream, DateOnly.MinValue, DateOnly.MaxValue,
            Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar.ColumnProjection.All,
            [new Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar.ColumnPredicate(
                "Flags", ComparisonOperator.GreaterThan, SessionFlags.Low)]).ConfigureAwait(true))
        {
            rows.Add(r);
        }

        rows.Should().HaveCount(4,
            "SessionFlags.High > SessionFlags.Low under the enum's unsigned order; the " +
            "zone-map skip must not drop the row group whose widened bounds are negative");
        rows.Should().OnlyContain(b => b.Flags == SessionFlags.High);
    }

    /// <summary>
    /// Equality stays skippable for ulong-underlying enums — the widened byte key is
    /// sign-agnostic, so an Equal predicate on the high member must return its exact rows.
    /// </summary>
    [Fact]
    public async Task StreamWhere_UlongEnumEqualPredicate_ReturnsExactRows()
    {
        var d0 = new DateOnly(2024, 1, 1);
        var input = Enumerable.Range(0, 8).Select(i => new UlongEnumBar
        {
            Date = d0.AddDays(i),
            Flags = i < 4 ? SessionFlags.Low : SessionFlags.High,
        }).ToList();

        var format = new CompressedColumnarSegmentFormat<UlongEnumBar>(rowGroupMaxRows: 4);
        using var stream = new MemoryStream();
        await format.WriteAsync(stream, input).ConfigureAwait(true);
        stream.Position = 0;

        var rows = new List<UlongEnumBar>();
        await foreach (var r in format.StreamWhereAsync(
            stream, DateOnly.MinValue, DateOnly.MaxValue,
            Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar.ColumnProjection.All,
            [new Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar.ColumnPredicate(
                "Flags", ComparisonOperator.Equal, SessionFlags.High)]).ConfigureAwait(true))
        {
            rows.Add(r);
        }

        rows.Should().HaveCount(4);
        rows.Should().OnlyContain(b => b.Flags == SessionFlags.High);
    }
}
