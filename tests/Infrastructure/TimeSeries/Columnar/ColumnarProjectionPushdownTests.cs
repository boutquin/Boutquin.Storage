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

namespace Boutquin.Storage.Infrastructure.Tests.TimeSeries.Columnar;

/// <summary>
/// Pins true projection-pushdown for the columnar read path. The earlier projection seam shipped
/// the <see cref="ColumnProjection"/> parameter; this proves the implementation now actually
/// skips the on-disk bytes of unprojected columns rather than reading the whole segment and
/// discarding at materialisation time.
/// <list type="bullet">
///   <item><description><b>Byte accounting (acceptance).</b> A projected read touches strictly
///   fewer stream bytes than a full read of the same fixture, measured through the
///   <see cref="WindowReadStream.BytesRead"/> read counter.</description></item>
///   <item><description><b>Fast-path equivalence (SPEC-INV-02).</b> The disk-skip result is
///   record-identical to the materialisation-time filter (full read then
///   <c>ProjectTo</c>).</description></item>
///   <item><description><b>Block-in-file window.</b> The same win holds when the segment lives
///   at a non-zero offset inside a larger file — the scenario the LSM block container
///   feeds.</description></item>
/// </list>
/// </summary>
public sealed class ColumnarProjectionPushdownTests
{
    // Three of MarketDataBar's twelve columns. Date is included because the reader filters rows
    // by Date — the same Date-augmentation the engine applies before the segment read.
    private static readonly ColumnProjection s_projection =
        new(["Close", "Volume", "Date"]);

    private static IReadOnlyList<MarketDataBar> Corpus(int count)
    {
        var start = new DateOnly(2018, 1, 1);
        var random = new Random(7);
        var list = new List<MarketDataBar>(count);
        for (var i = 0; i < count; i++)
        {
            var price = 100m + (decimal)(random.NextDouble() * 50);
            list.Add(new MarketDataBar
            {
                Date = start.AddDays(i),
                Open = price,
                High = price + 1m,
                Low = price - 1m,
                Close = price + 0.5m,
                Volume = 1_000_000L + i,
                Vwap = price + 0.25m,
                TradeCount = 500 + i,
                Dividend = 0m,
                SplitRatio = 1m,
                AdjustedClose = price + 0.5m,
                SourceFlags = i % 8,
            });
        }
        return list;
    }

    private static async Task<byte[]> WriteSegmentAsync(IReadOnlyList<MarketDataBar> bars)
    {
        var format = new CompressedColumnarSegmentFormat<MarketDataBar>();
        using var ms = new MemoryStream();
        await format.WriteAsync(ms, bars);
        return ms.ToArray();
    }

    private static async Task<List<MarketDataBar>> ReadAllAsync(
        byte[] segment, ColumnProjection projection, long windowOffset = 0)
        => (await ReadCountingAsync(segment, projection, windowOffset)).Rows;

    private static async Task<(List<MarketDataBar> Rows, long BytesRead)> ReadCountingAsync(
        byte[] segment, ColumnProjection projection, long windowOffset = 0)
    {
        var format = new CompressedColumnarSegmentFormat<MarketDataBar>();
        var backing = new byte[windowOffset + segment.Length + 16];
        Array.Copy(segment, 0, backing, windowOffset, segment.Length);
        using var baseStream = new MemoryStream(backing, writable: false);
        using var window = new WindowReadStream(baseStream, windowOffset, segment.Length);

        var list = new List<MarketDataBar>();
        await foreach (var bar in format.ReadAsync(
            window, DateOnly.MinValue, DateOnly.MaxValue, projection))
        {
            list.Add(bar);
        }
        return (list, window.BytesRead);
    }

    [Fact]
    public async Task ProjectedRead_TouchesStrictlyFewerBytes_ThanFullRead()
    {
        var segment = await WriteSegmentAsync(Corpus(2_000));

        var (fullRows, fullBytes) = await ReadCountingAsync(segment, ColumnProjection.All);
        var (projRows, projBytes) = await ReadCountingAsync(segment, s_projection);

        fullRows.Should().HaveCount(2_000);
        projRows.Should().HaveCount(2_000);

        projBytes.Should().BeLessThan(fullBytes,
            "projecting 3 of 12 columns must skip the on-disk bytes of the other 9 columns");
        // Sanity: the full read does not read more than the whole segment, and the projected
        // read reads a meaningful subset (not zero, not everything).
        fullBytes.Should().BeLessThanOrEqualTo(segment.Length);
        projBytes.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ProjectedRead_IsRecordIdenticalTo_FullReadThenMaterialisationFilter()
    {
        var corpus = Corpus(1_500);
        var segment = await WriteSegmentAsync(corpus);

        var full = await ReadAllAsync(segment, ColumnProjection.All);
        var projected = await ReadAllAsync(segment, s_projection);

        projected.Should().HaveCount(full.Count);
        for (var i = 0; i < projected.Count; i++)
        {
            // SPEC-INV-02: the disk-skip result is byte-identical to filtering a full read at
            // materialisation time. MarketDataBar is a record, so value-equality is the
            // byte-identity proxy.
            var materialisationFilterResult =
                ((IProjectable<MarketDataBar>)full[i]).ProjectTo(s_projection);
            projected[i].Should().Be(materialisationFilterResult,
                $"row {i}: projection-pushdown must equal full-read-then-ProjectTo");
        }
    }

    [Fact]
    public async Task ProjectedRead_AtNonZeroBlockOffset_SkipsAndStaysEquivalent()
    {
        var corpus = Corpus(1_000);
        var segment = await WriteSegmentAsync(corpus);
        const long blockOffset = 4_096; // the segment sits mid-file, like an LSM block

        var (fullRows, fullBytes) = await ReadCountingAsync(segment, ColumnProjection.All, blockOffset);
        var (projRows, projBytes) = await ReadCountingAsync(segment, s_projection, blockOffset);

        projBytes.Should().BeLessThan(fullBytes,
            "the disk-skip must hold for a segment embedded at a non-zero offset (the container scenario)");

        projRows.Should().HaveCount(fullRows.Count);
        for (var i = 0; i < projRows.Count; i++)
        {
            projRows[i].Should().Be(((IProjectable<MarketDataBar>)fullRows[i]).ProjectTo(s_projection));
        }
    }
}
