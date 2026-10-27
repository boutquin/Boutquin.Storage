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
using Boutquin.Storage.Domain.Attributes;
using Boutquin.Storage.Domain.Interfaces.TimeSeries;
using Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar;
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar;

namespace Boutquin.Storage.Infrastructure.Tests.TimeSeries.Columnar;

/// <summary>
/// Fixture with BitmapIndex-annotated double/float columns for the signed-zero parity tests.
/// Top-level because the columnar generator emits namespace-level partials.
/// </summary>
[ColumnarStorable]
public partial record SignedZeroBar : IHasDate
{
    public DateOnly Date { get; init; }

    [BitmapIndex]
    public double Level { get; init; }

    [BitmapIndex]
    public float Ratio { get; init; }
}

/// <summary>
/// Fixture with a BitmapIndex-annotated decimal column for the decimal signed-zero parity tests.
/// </summary>
[ColumnarStorable]
public partial record SignedZeroDecimalBar : IHasDate
{
    public DateOnly Date { get; init; }

    [BitmapIndex]
    public decimal Amount { get; init; }
}

/// <summary>
/// Pushed and unpushed evaluation must agree on ZERO IDENTITY. The row-level filter compares
/// via <c>Comparer&lt;double&gt;.Default</c>, for which −0.0 == +0.0; a bitmap index that
/// buckets the two zeros as distinct bit patterns lets the equality skip declare "value
/// absent" for a segment whose every row MATCHES the predicate — a silent false drop, the one
/// outcome data skipping must never produce.
/// </summary>
public sealed class BitmapSignedZeroParityTests
{
    private static byte[] Write(params SignedZeroBar[] bars)
    {
        var format = new CompressedColumnarSegmentFormat<SignedZeroBar>(rowGroupMaxRows: 2);
        using var stream = new MemoryStream();
        format.WriteAsync(stream, bars).GetAwaiter().GetResult();
        return stream.ToArray();
    }

    private static async Task<List<SignedZeroBar>> StreamWhere(byte[] bytes, params ColumnPredicate[] predicates)
    {
        var format = new CompressedColumnarSegmentFormat<SignedZeroBar>(rowGroupMaxRows: 2);
        using var stream = new MemoryStream(bytes);
        var result = new List<SignedZeroBar>();
        await foreach (var r in format.StreamWhereAsync(
            stream, DateOnly.MinValue, DateOnly.MaxValue, ColumnProjection.All, predicates).ConfigureAwait(true))
        {
            result.Add(r);
        }

        return result;
    }

    private static SignedZeroBar Bar(int day, double level, float ratio = 1.0f) => new()
    {
        Date = new DateOnly(2026, 1, 1).AddDays(day),
        Level = level,
        Ratio = ratio,
    };

    [Fact]
    public async Task Equal_PositiveZeroQuery_FindsStoredNegativeZero_Double()
    {
        // Every row is −0.0; the row filter says every row matches Equal(0.0).
        var bytes = Write(Bar(0, -0.0), Bar(1, -0.0), Bar(2, -0.0), Bar(3, -0.0));

        var rows = await StreamWhere(bytes, new ColumnPredicate("Level", ComparisonOperator.Equal, 0.0));

        rows.Should().HaveCount(4,
            "the unpushed row filter (Comparer<double>.Default) treats -0.0 == +0.0, and the bitmap skip must agree");
    }

    [Fact]
    public async Task Equal_ZeroQuery_AcrossRowGroupsWithMixedZeroSigns_ReturnsAllRows()
    {
        // RG0 holds −0.0 rows, RG1 holds +0.0 rows (rowGroupMaxRows: 2). Both groups match.
        var bytes = Write(Bar(0, -0.0), Bar(1, -0.0), Bar(2, 0.0), Bar(3, 0.0));

        var rows = await StreamWhere(bytes, new ColumnPredicate("Level", ComparisonOperator.Equal, 0.0));

        rows.Should().HaveCount(4, "both zero signs are the same logical value to the row filter");
    }

    [Fact]
    public async Task Equal_NegativeZeroQueryLiteral_FindsStoredPositiveZero_Double()
    {
        var bytes = Write(Bar(0, 0.0), Bar(1, 0.0));

        var rows = await StreamWhere(bytes, new ColumnPredicate("Level", ComparisonOperator.Equal, -0.0));

        rows.Should().HaveCount(2, "a -0.0 query literal is the same logical value as the stored +0.0");
    }

    [Fact]
    public async Task Flush_BitmapStringKeyOver64KiB_ThrowsTypedGuard()
    {
        // Bitmap keys are equality keys and are deliberately NOT truncated (unlike zone-map
        // string bounds), but the footer's key-length field is a ushort — a key whose UTF-8
        // exceeds 65,535 bytes must be rejected with a CLEAR typed error at flush time, not a
        // raw OverflowException from the checked((ushort)…) narrowing inside FooterWriter.
        var format = new CompressedColumnarSegmentFormat<IndexableBar>();
        var rows = new[]
        {
            new IndexableBar { Date = new DateOnly(2026, 1, 1), Ticker = new string('x', 70_000) },
        };

        using var stream = new MemoryStream();
        var act = () => format.WriteAsync(stream, rows);

        await act.Should().ThrowAsync<InvalidOperationException>(
                "an over-wide bitmap key is a caller-data limit, not an arithmetic accident")
            .WithMessage("*65,535*");
    }

    [Fact]
    public async Task Equal_PositiveZeroQuery_FindsStoredNegativeZero_Single()
    {
        var bytes = Write(Bar(0, 1.0, -0.0f), Bar(1, 1.0, -0.0f));

        var rows = await StreamWhere(bytes, new ColumnPredicate("Ratio", ComparisonOperator.Equal, 0.0f));

        rows.Should().HaveCount(2, "single-precision zero identity must match the row filter too");
    }

    /// <summary>
    /// The durable regression net for the skip/filter-agreement invariant class: for EVERY
    /// operator and every adversarial float value (±0.0, NaN, a non-canonical NaN payload,
    /// ±Infinity, subnormals, extremes), the pushed evaluation must return exactly the rows
    /// the unpushed row filter accepts. A single divergence is a silent false drop or a
    /// false include — the case-based tests above each pin one instance; this sweeps the
    /// cross product mechanically.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task StreamWhere_MatchesUnpushedRowFilter_OverAdversarialFloatPayloads(int seed)
    {
        double[] pool =
        [
            0.0, -0.0, double.NaN, BitConverter.Int64BitsToDouble(unchecked((long)0xFFF8_0000_0000_0F0F)),
            double.PositiveInfinity, double.NegativeInfinity, double.Epsilon, -double.Epsilon,
            1.5, -1.5, double.MaxValue, double.MinValue,
        ];

        var rng = new Random(seed * 7919);
        var bars = Enumerable.Range(0, 24)
            .Select(i => Bar(i, pool[rng.Next(pool.Length)], (float)pool[rng.Next(pool.Length)]))
            .ToArray();
        var bytes = Write(bars); // rowGroupMaxRows: 2 → 12 row groups, plenty of skip opportunities

        foreach (var op in Enum.GetValues<ComparisonOperator>())
        {
            foreach (var literal in pool)
            {
                var predicate = new ColumnPredicate("Level", op, literal);

                var pushed = (await StreamWhere(bytes, predicate)).Select(b => b.Date).ToList();
                var unpushed = bars.Where(b => predicate.Matches(b.Level)).Select(b => b.Date).ToList();

                pushed.Should().Equal(unpushed,
                    $"pushed and unpushed evaluation must agree for {op} {literal:R} (seed {seed})");
            }
        }
    }

    private static byte[] WriteDecimal(params decimal[] amounts)
    {
        var format = new CompressedColumnarSegmentFormat<SignedZeroDecimalBar>(rowGroupMaxRows: 2);
        using var stream = new MemoryStream();
        var bars = amounts
            .Select((a, i) => new SignedZeroDecimalBar { Date = new DateOnly(2026, 1, 1).AddDays(i), Amount = a })
            .ToArray();
        format.WriteAsync(stream, bars).GetAwaiter().GetResult();
        return stream.ToArray();
    }

    private static async Task<int> CountWhereDecimal(byte[] bytes, ColumnPredicate predicate)
    {
        var format = new CompressedColumnarSegmentFormat<SignedZeroDecimalBar>(rowGroupMaxRows: 2);
        using var stream = new MemoryStream(bytes);
        var count = 0;
        await foreach (var _ in format.StreamWhereAsync(
            stream, DateOnly.MinValue, DateOnly.MaxValue, ColumnProjection.All, [predicate]).ConfigureAwait(true))
        {
            count++;
        }

        return count;
    }

    [Fact]
    public async Task Equal_PositiveZeroQuery_FindsStoredNegativeZero_Decimal()
    {
        // decimal.Negate(0.00m) carries the sign bit; Comparer<decimal>.Default says it equals 0m.
        var negZero = decimal.Negate(0.00m);
        var bytes = WriteDecimal(negZero, negZero, negZero, negZero);

        var rows = await CountWhereDecimal(bytes, new ColumnPredicate("Amount", ComparisonOperator.Equal, 0m));

        rows.Should().Be(4,
            "the unpushed row filter treats -0m == 0m, so the decimal bitmap skip must agree");
    }

    [Fact]
    public async Task Equal_NegativeZeroQueryLiteral_FindsStoredPositiveZero_Decimal()
    {
        var bytes = WriteDecimal(0m, 0.00m);

        var rows = await CountWhereDecimal(bytes, new ColumnPredicate("Amount", ComparisonOperator.Equal, decimal.Negate(0m)));

        rows.Should().Be(2, "a -0m query literal is the same logical value as the stored zeros");
    }
}
