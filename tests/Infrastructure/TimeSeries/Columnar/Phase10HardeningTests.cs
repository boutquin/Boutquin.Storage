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
using System.Reflection;
using Boutquin.Storage.Domain.Attributes;
using Boutquin.Storage.Domain.Interfaces.TimeSeries;
using Boutquin.Storage.Domain.Interfaces.TimeSeries.Columnar;
using Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar;
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar;
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar.Footer;

namespace Boutquin.Storage.Infrastructure.Tests.TimeSeries.Columnar;

/// <summary>
/// Test record with a Nullable&lt;T&gt; column property — used to pin
/// <c>DiscoverColumns</c>'s constructor-time rejection of Nullable shapes (hardening #1).
/// Hand-built to bypass <c>[ColumnarStorable]</c> source-generator validation; the
/// generator may or may not let Nullable through to the plan, but the runtime guard
/// is what matters here. The static <c>ColumnarFieldPlan</c> mirrors the shape the
/// generator emits.
/// </summary>
public sealed class NullableColumnBar : IHasDate, ISerializable<NullableColumnBar>, IColumnarRehydratable<NullableColumnBar>, IEquatable<NullableColumnBar>
{
    public DateOnly Date { get; set; }
    public decimal? Bid { get; set; }

    public NullableColumnBar() { }

    public static IReadOnlyList<ColumnarFieldDescriptor> ColumnarFieldPlan { get; } =
    [
        new ColumnarFieldDescriptor("Date", ColumnEncoding.DeltaOfDeltaVlq),
        new ColumnarFieldDescriptor("Bid", ColumnEncoding.Dictionary),
    ];

    public static NullableColumnBar Rehydrate(IReadOnlyDictionary<string, Array> columns, int rowIndex)
        => new();

    public void Serialize(Stream stream) { }
    public static NullableColumnBar Deserialize(Stream stream) => new();
    public bool Equals(NullableColumnBar? other) => other is not null && Date == other.Date && Bid == other.Bid;
    public override bool Equals(object? obj) => obj is NullableColumnBar b && Equals(b);
    public override int GetHashCode() => HashCode.Combine(Date, Bid);
}

/// <summary>
/// Phase-10 follow-up hardening — covers four MarketData-reachable arms left uncovered
/// by the original Phase-10 push, plus one bug-hunt target (dead arms in
/// <c>DecodeColumn</c> for non-restartable encodings, analogous to the
/// <c>BuildConstraints</c> fix), plus a hardening-via-DiscoverColumns hoist for
/// Nullable&lt;T&gt; column property types. Each test pins a single observable contract.
/// </summary>
public sealed class Phase10HardeningTests
{
    // ── 1. RawDouble FULL-column decode (single-row-group restartable path) ─

    /// <summary>
    /// <c>RawDouble</c> is restartable and routes through the FULL-column
    /// <c>DecodeColumn</c> dispatch (not <c>DecodeColumnSlice</c>) when the segment has
    /// exactly one row group with no <c>RowGroupByteOffsets</c> entry. Pinning the L769
    /// case in <c>DecodeColumn</c> — used by MarketData's `[ColumnEncoding(RawDouble)]`
    /// override on high-entropy double columns (e.g., residuals).
    /// </summary>
    [Fact]
    public async Task RawDouble_FullColumnDecode_RoundTripsThroughDecodeColumn()
    {
        // Single record → exactly one row group → no per-row-group byte offsets in the
        // footer → DecodeColumn (not DecodeColumnSlice) is the dispatch path.
        var format = new CompressedColumnarSegmentFormat<ColumnarBar>();
        var input = new[]
        {
            new ColumnarBar
            {
                Date = new DateOnly(2026, 1, 1),
                Ticker = "AAPL",
                Close = 100m,
                Volume = 1_000_000L,
                TradeCount = 50,
                Vwap = 100.5,
                IsAdjusted = false,
                Residual = 0.123456789e-10,
            },
        };

        using var stream = new MemoryStream();
        await format.WriteAsync(stream, input).ConfigureAwait(true);
        stream.Position = 0;

        var read = new List<ColumnarBar>();
        await foreach (var r in format.ReadAsync(
            stream, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), ColumnProjection.All).ConfigureAwait(true))
        {
            read.Add(r);
        }

        read.Should().ContainSingle();
        read[0].Residual.Should().Be(0.123456789e-10,
            "RawDouble preserves bit-exact double payload through the FULL-column DecodeColumn path");
    }

    // ── 2. ScaledIntegerBitPacked DecodeSlice with date-range filter ────────

    /// <summary>
    /// <c>ScaledIntegerBitPackedCodec.DecodeDecimalSlice</c> at L796 of
    /// <c>CompressedColumnarSegmentFormat.DecodeColumnSlice</c> is reached when a
    /// projected range scan crosses a row-group boundary on a high-cardinality decimal
    /// column. MarketData's typical Bar shape uses ScaledIntegerBitPacked for prices
    /// when distinct-value cardinality exceeds the dictionary threshold; this test pins
    /// the slice-decode path on that arm.
    /// </summary>
    [Fact]
    public async Task ScaledIntegerBitPacked_DecodeSlice_PreservesValuesAcrossRowGroupBoundary()
    {
        // Tiny rowGroupMaxRows forces multiple row groups; each gets its own slice at
        // read time. ScaledIntegerBitPacked is restartable, so the slice path is the
        // L795-796 arm in DecodeColumnSlice (not the per-row-group offset arm).
        var format = new CompressedColumnarSegmentFormat<Phase10ScaledDecimalBar>(rowGroupMaxRows: 4);

        var input = Enumerable.Range(0, 12)
            .Select(i => new Phase10ScaledDecimalBar
            {
                Date = new DateOnly(2026, 1, 1).AddDays(i),
                // Distinct values to push past the dictionary cardinality threshold and
                // route through ScaledIntegerBitPacked. Exact values matter — the test
                // asserts byte-exact preservation through DecodeSlice.
                Price = 100.00m + (decimal)i * 0.0001m,
            })
            .ToList();

        using var stream = new MemoryStream();
        await format.WriteAsync(stream, input).ConfigureAwait(true);
        stream.Position = 0;

        // Date range that spans the second AND third row groups (rows 4..11) — forces
        // DecodeSlice to fire across at least two row-group boundaries.
        var read = new List<Phase10ScaledDecimalBar>();
        await foreach (var r in format.ReadAsync(
            stream, new DateOnly(2026, 1, 5), new DateOnly(2026, 1, 12), ColumnProjection.All).ConfigureAwait(true))
        {
            read.Add(r);
        }

        read.Should().HaveCount(8, "the requested date range covers rows 4..11");
        for (var i = 0; i < read.Count; i++)
        {
            var expectedPrice = 100.00m + (decimal)(i + 4) * 0.0001m;
            read[i].Price.Should().Be(expectedPrice,
                "ScaledIntegerBitPacked DecodeSlice preserves the byte-exact decimal scale");
        }
    }

    // ── 3. Bug-hunt: DecodeColumn dead arms for non-restartable encodings ──

    /// <summary>
    /// <b>BuildConstraints-style bug-hunt.</b> <c>DecodeColumn</c> at L745–777 is the
    /// FULL-column decoder, called from <c>ReadRowGroupsAsync</c> only when
    /// <c>footer.RowGroupByteOffsets?.TryGetValue(...) == false</c> — i.e., the column
    /// was NOT encoded per-row-group. Per <c>IsNonRestartable</c> at L638–639, GorillaXor
    /// and DeltaOfDeltaVlq are ALWAYS encoded per-row-group via
    /// <c>EncodeColumnPerRowGroup</c>. Their <c>RowGroupByteOffsets</c> entries are always
    /// populated, so the reader always takes the per-row-group path for them.
    /// <para>
    /// Yet <c>DecodeColumn</c> still has cases for them at L765–768. Those arms are dead
    /// code by construction — but a future maintainer who flips <c>IsNonRestartable</c>
    /// (or adds a new non-restartable codec) without updating the encoder dispatch would
    /// silently re-enable a code path that has never been tested. The arm would decode
    /// the wrong byte format and produce silently-wrong values.
    /// </para>
    /// <para>
    /// The fix replaces the dead arms with a fail-loud
    /// <see cref="InvalidOperationException"/> so the drift surfaces at the first
    /// affected read. This test pins the new contract by invoking the private
    /// <c>DecodeColumn</c> directly via reflection with a non-restartable encoding;
    /// the production path can never reach here legitimately.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(ColumnEncoding.GorillaXor)]
    [InlineData(ColumnEncoding.DeltaOfDeltaVlq)]
    public void DecodeColumn_NonRestartableEncoding_ThrowsInvalidOperationException(ColumnEncoding encoding)
    {
        // Build a minimal harness: any TRecord that DiscoverColumns accepts will do —
        // we only invoke DecodeColumn via reflection, which doesn't actually run a real
        // codec. ColumnarBar is convenient because it's already in the test corpus.
        var format = new CompressedColumnarSegmentFormat<ColumnarBar>();

        var formatType = typeof(CompressedColumnarSegmentFormat<ColumnarBar>);
        var decodeColumn = formatType.GetMethod(
            "DecodeColumn",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("DecodeColumn not found via reflection.");

        // Build a ColumnInfo whose Encoding is non-restartable. The emitter never produces
        // such a ColumnInfo for the FULL-column decoder path in production — that's the
        // exact invariant we're pinning. ColumnInfo is a nested type of the generic
        // CompressedColumnarSegmentFormat<TRecord>, so it inherits the outer's type
        // parameter and must be closed via MakeGenericType against ColumnarBar.
        var openColumnInfoType = typeof(CompressedColumnarSegmentFormat<>)
            .GetNestedType("ColumnInfo", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ColumnInfo nested type not found.");
        var columnInfoType = openColumnInfoType.MakeGenericType(typeof(ColumnarBar));
        var columnInfoCtor = columnInfoType.GetConstructors()[0];

        var dateProp = typeof(ColumnarBar).GetProperty("Date")!;
        var columnInfo = columnInfoCtor.Invoke(
        [
            "TestColumn",
            dateProp,
            encoding,
            ColumnEncodingTag.GorillaXor, // tag value is irrelevant for the DecodeColumn dispatch
            (byte)0,
        ]);

        // SegmentFooter — minimal valid shape with a single row group so the `total` count
        // computation in DecodeColumn produces a non-zero value.
        var footerType = typeof(Boutquin.Storage.Infrastructure.TimeSeries.Columnar.Footer.FooterReader)
            .Assembly.GetType("Boutquin.Storage.Infrastructure.TimeSeries.Columnar.Footer.SegmentFooter")!;
        var rowGroupType = typeof(Boutquin.Storage.Infrastructure.TimeSeries.Columnar.Footer.FooterReader)
            .Assembly.GetType("Boutquin.Storage.Infrastructure.TimeSeries.Columnar.Footer.RowGroupDescriptor")!;
        var rg = Activator.CreateInstance(
            rowGroupType,
            new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 1), 1, 0)!;
        var rowGroupsList = (System.Collections.IList)Activator.CreateInstance(
            typeof(List<>).MakeGenericType(rowGroupType))!;
        rowGroupsList.Add(rg);
        var footerCtorParams = footerType.GetConstructors()[0].GetParameters();
        var footerArgs = new object?[footerCtorParams.Length];
        for (var i = 0; i < footerCtorParams.Length; i++)
        {
            footerArgs[i] = footerCtorParams[i].Name switch
            {
                "FormatVersion" => (uint)1,
                "Columns" => Activator.CreateInstance(
                    typeof(List<>).MakeGenericType(typeof(ColumnDescriptor))),
                "RowGroups" => rowGroupsList,
                _ => null,
            };
        }
        var footer = footerCtorParams[0].ParameterType == footerType
            ? footerArgs[0]
            : Activator.CreateInstance(footerType, footerArgs);

        // Invoke DecodeColumn — must throw InvalidOperationException once the dead arms
        // are replaced with the routing-bug guard. DecodeColumn derives its allocation
        // ceiling internally from the footer's per-row-group cap (no maxDecodableRows
        // parameter), so the routing-guard arm is reached without any ceiling argument.
        Action act = () => decodeColumn.Invoke(null, [columnInfo, new byte[1024], footer]);

        var ex = act.Should().Throw<TargetInvocationException>()
            .Which.InnerException;
        ex.Should().BeOfType<InvalidOperationException>(
            "non-restartable encodings must never reach the FULL-column DecodeColumn path; " +
            "reaching it indicates a drift between IsNonRestartable and the encoder's " +
            "per-row-group dispatch — fail loudly so the bug surfaces at the first affected read");
        ex!.Message.Should().Contain("non-restartable",
            "the guard message must call out the routing-invariant violation by name");
    }

    // ── 4. Hardening #1: Nullable<T> rejected at constructor (DiscoverColumns) ──

    /// <summary>
    /// <b>Hardening #1.</b> Nullable&lt;T&gt; column property types are not supported
    /// (codecs do not encode null payloads). Currently the rejection happens at write
    /// time via <c>MapTypeToCode</c> — but only AFTER <c>BuildTypedValues</c> and the
    /// per-column encoding work has run. Hoisting the rejection to
    /// <c>DiscoverColumns</c> (constructor time) means a misconfigured record fails
    /// fast at engine construction, not after wasted encode cycles for a half-written
    /// segment.
    /// </summary>
    [Fact]
    public void Constructor_RecordWithNullableColumnProperty_RejectsAtDiscoverColumnsTime()
    {
        Action act = () => new CompressedColumnarSegmentFormat<NullableColumnBar>();

        var ex = act.Should().Throw<NotSupportedException>().Which;
        ex.Message.Should().Contain("Bid",
            "the constructor-time guard must name the offending column to make the error actionable");
        ex.Message.Should().Contain("Nullable",
            "the message must call out Nullable<T> as the unsupported shape — not just generic " +
            "'unsupported type', so a future engineer immediately understands the constraint");
    }
}

[ColumnarStorable]
public partial record Phase10ScaledDecimalBar : IHasDate
{
    public DateOnly Date { get; init; }

    [DecimalScale(4)]
    [ColumnEncoding(ColumnEncoding.ScaledIntegerBitPacked)]
    public decimal Price { get; init; }

    public Phase10ScaledDecimalBar() { }
}
