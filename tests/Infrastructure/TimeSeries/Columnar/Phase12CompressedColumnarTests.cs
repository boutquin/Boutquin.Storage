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
using Boutquin.Storage.Domain.ValueObjects.TimeSeries;
using Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar;
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar;
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar.Codecs;
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar.Footer;

namespace Boutquin.Storage.Infrastructure.Tests.TimeSeries.Columnar;

/// <summary>
/// Phase-12 batch B fixtures and tests targeting MarketData-reachable hardening of
/// <see cref="CompressedColumnarSegmentFormat{TRecord}"/>: ScanDirection enum-bound
/// rejection, date-range early/late skip, multi-row-group quarantine for the all-tampered
/// case, projection edges, decimal-scale theory, nullable-rejection writer guard, and
/// single-row-group sizing.
/// </summary>

[ColumnarStorable]
public partial record Phase12DecimalBar : IHasDate
{
    public DateOnly Date { get; init; }

    [DecimalScale(0)]
    public decimal Scale0 { get; init; }

    [DecimalScale(2)]
    public decimal Scale2 { get; init; }

    [DecimalScale(4)]
    public decimal Scale4 { get; init; }

    [DecimalScale(8)]
    public decimal Scale8 { get; init; }

    public Phase12DecimalBar() { }
}

[ColumnarStorable]
public partial record Phase12NullableStringBar : IHasDate
{
    public DateOnly Date { get; init; }

    // Default null-suppressed initializer so the test can construct an instance whose
    // Value is null and exercise the runtime null-rejection guard at the writer boundary.
    public string Value { get; init; } = null!;

    public Phase12NullableStringBar() { }
}

public sealed class Phase12CompressedColumnarTests
{
    private static CodecBarInt32 MakeInt32Bar(int n) => new()
    {
        Date = new DateOnly(2026, 1, 1).AddDays(n),
        Value = n,
    };

    // ── 1, 2, 3: ScanDirection enum-bound rejection ─────────────────────────

    /// <summary>
    /// <see cref="CompressedColumnarSegmentFormat{TRecord}.ReadRowGroupsAsync"/> rejects every
    /// <see cref="ScanDirection"/> value that is not <see cref="ScanDirection.Ascending"/> —
    /// the v2.0.0 contract. Casting <c>(ScanDirection)1</c> simulates a future enum extension
    /// hitting the current binary; the read must fail fast rather than silently fall back to
    /// ascending iteration.
    /// </summary>
    [Fact]
    public async Task ReadRowGroupsAsync_NonAscendingDirection_Throws()
    {
        var format = new CompressedColumnarSegmentFormat<CodecBarInt32>();
        using var stream = new MemoryStream();
        await format.WriteAsync(stream, [MakeInt32Bar(0), MakeInt32Bar(1)]).ConfigureAwait(true);
        stream.Position = 0;

        Func<Task> act = async () =>
        {
            await foreach (var _ in format.ReadRowGroupsAsync(
                stream, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31),
                direction: (ScanDirection)1).ConfigureAwait(true))
            {
            }
        };

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>()
            .Where(e => e.ParamName == "direction").ConfigureAwait(true);
    }

    [Fact]
    public async Task ReadBatchesAsync_NonAscendingDirection_Throws()
    {
        var format = new CompressedColumnarSegmentFormat<CodecBarInt32>();
        using var stream = new MemoryStream();
        await format.WriteAsync(stream, [MakeInt32Bar(0), MakeInt32Bar(1)]).ConfigureAwait(true);
        stream.Position = 0;

        Func<Task> act = async () =>
        {
            await foreach (var _ in format.ReadBatchesAsync(
                stream, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31),
                ColumnProjection.All, direction: (ScanDirection)1).ConfigureAwait(true))
            {
            }
        };

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>()
            .Where(e => e.ParamName == "direction").ConfigureAwait(true);
    }

    [Fact]
    public async Task ReadAsync_NonAscendingDirection_Throws()
    {
        var format = new CompressedColumnarSegmentFormat<CodecBarInt32>();
        using var stream = new MemoryStream();
        await format.WriteAsync(stream, [MakeInt32Bar(0), MakeInt32Bar(1)]).ConfigureAwait(true);
        stream.Position = 0;

        Func<Task> act = async () =>
        {
            await foreach (var _ in format.ReadAsync(
                stream, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31),
                ColumnProjection.All, direction: (ScanDirection)1).ConfigureAwait(true))
            {
            }
        };

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>()
            .Where(e => e.ParamName == "direction").ConfigureAwait(true);
    }

    // ── 4 & 5: date-range filter skips out-of-range row groups ──────────────

    /// <summary>
    /// When the requested <c>(from, to)</c> window starts AFTER every row group's
    /// <c>MaxDate</c>, the early-skip arm fires for every group and the reader yields nothing.
    /// </summary>
    [Fact]
    public async Task ReadRowGroupsAsync_DateRangeAfterAllRowGroups_YieldsNothing()
    {
        var format = new CompressedColumnarSegmentFormat<CodecBarInt32>(rowGroupMaxRows: 4);
        var input = Enumerable.Range(0, 12).Select(MakeInt32Bar).ToList();

        using var stream = new MemoryStream();
        await format.WriteAsync(stream, input).ConfigureAwait(true);
        stream.Position = 0;

        // The data spans 2026-01-01 .. 2026-01-12. Request a window starting in 2027 — every
        // row group's MaxDate is well below the requested `from`, so the rg.MaxDate < from
        // arm fires for every group.
        var results = new List<RowGroupReadResult<CodecBarInt32>>();
        await foreach (var r in format.ReadRowGroupsAsync(
            stream, new DateOnly(2027, 1, 1), new DateOnly(2027, 12, 31)).ConfigureAwait(true))
        {
            results.Add(r);
        }

        results.Should().BeEmpty(
            "every row group's MaxDate is before the requested window — the early-skip arm fires for all of them");
    }

    /// <summary>
    /// Symmetric to <c>ReadRowGroupsAsync_DateRangeAfterAllRowGroups_YieldsNothing</c>: when
    /// the window's <c>to</c> is BEFORE every row group's <c>MinDate</c>, the late-skip arm
    /// fires for every group.
    /// </summary>
    [Fact]
    public async Task ReadRowGroupsAsync_DateRangeBeforeAllRowGroups_YieldsNothing()
    {
        var format = new CompressedColumnarSegmentFormat<CodecBarInt32>(rowGroupMaxRows: 4);
        var input = Enumerable.Range(0, 12).Select(MakeInt32Bar).ToList();

        using var stream = new MemoryStream();
        await format.WriteAsync(stream, input).ConfigureAwait(true);
        stream.Position = 0;

        var results = new List<RowGroupReadResult<CodecBarInt32>>();
        await foreach (var r in format.ReadRowGroupsAsync(
            stream, new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31)).ConfigureAwait(true))
        {
            results.Add(r);
        }

        results.Should().BeEmpty(
            "every row group's MinDate is after the requested window — the late-skip arm fires for all of them");
    }

    // ── 6: every row group's CRC mismatched ─────────────────────────────────

    /// <summary>
    /// When every row group's footer CRC has been tampered, every yielded result is
    /// <c>Quarantined</c>. Existing coverage proves the single-row-group case; this test
    /// asserts the loop's behaviour when the mismatch arm fires for every iteration.
    /// </summary>
    [Fact]
    public async Task ReadRowGroupsAsync_AllRowGroupsTampered_YieldsAllCorruptResults()
    {
        var format = new CompressedColumnarSegmentFormat<CodecBarInt32>(rowGroupMaxRows: 4);
        var input = Enumerable.Range(0, 12).Select(MakeInt32Bar).ToList();

        using var stream = new MemoryStream();
        await format.WriteAsync(stream, input).ConfigureAwait(true);

        // Read footer, tamper EVERY row group's CRC, rebuild segment with new footer.
        var bytes = stream.ToArray();
        using var footerProbe = new MemoryStream(bytes);
        var footer = FooterReader.Read(footerProbe);
        var tamperedCrcs = footer.RowGroupCrcs!.Select(c => c ^ 0xFFFFFFFFu).ToArray();
        var tamperedFooter = new SegmentFooter(
            footer.FormatVersion,
            footer.Columns,
            footer.RowGroups,
            footer.ZoneMaps,
            footer.BitmapIndexes,
            tamperedCrcs,
            footer.RowGroupByteOffsets);

        var oldFooterLengthPos = bytes.Length - SegmentMagic.TrailerSize;
        var oldFooterLength = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(oldFooterLengthPos, 4));
        var columnDataEnd = bytes.Length - SegmentMagic.TrailerSize - SegmentMagic.FooterCrcSize - (int)oldFooterLength;

        using var rebuilt = new MemoryStream();
        rebuilt.Write(bytes, 0, columnDataEnd);
        FooterWriter.Write(rebuilt, tamperedFooter);
        rebuilt.Position = 0;

        var results = new List<RowGroupReadResult<CodecBarInt32>>();
        await foreach (var r in format.ReadRowGroupsAsync(
            rebuilt, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31)).ConfigureAwait(true))
        {
            results.Add(r);
        }

        results.Should().HaveCount(footer.RowGroups.Count);
        results.Should().OnlyContain(r => !r.IsOk,
            "every row group's footer CRC was tampered — every yielded result must be Quarantined");
    }

    // ── 7: from > to short-circuits ─────────────────────────────────────────

    /// <summary>
    /// All three readers exit immediately when <c>from &gt; to</c> — even before the footer
    /// is read. This is the cheapest filter possible.
    /// </summary>
    [Fact]
    public async Task ReadAsync_FromGreaterThanTo_YieldsNothing()
    {
        var format = new CompressedColumnarSegmentFormat<CodecBarInt32>();
        using var stream = new MemoryStream();
        await format.WriteAsync(stream, Enumerable.Range(0, 5).Select(MakeInt32Bar).ToList()).ConfigureAwait(true);
        stream.Position = 0;

        var rowResults = new List<RowGroupReadResult<CodecBarInt32>>();
        await foreach (var r in format.ReadRowGroupsAsync(
            stream, new DateOnly(2026, 12, 31), new DateOnly(2026, 1, 1)).ConfigureAwait(true))
        {
            rowResults.Add(r);
        }

        var batches = new List<ColumnBatch<CodecBarInt32>>();
        stream.Position = 0;
        await foreach (var b in format.ReadBatchesAsync(
            stream, new DateOnly(2026, 12, 31), new DateOnly(2026, 1, 1), ColumnProjection.All).ConfigureAwait(true))
        {
            batches.Add(b);
        }

        var rows = new List<CodecBarInt32>();
        stream.Position = 0;
        await foreach (var r in format.ReadAsync(
            stream, new DateOnly(2026, 12, 31), new DateOnly(2026, 1, 1), ColumnProjection.All).ConfigureAwait(true))
        {
            rows.Add(r);
        }

        rowResults.Should().BeEmpty();
        batches.Should().BeEmpty();
        rows.Should().BeEmpty();
    }

    // ── 8 & 9: projection edges ─────────────────────────────────────────────

    /// <summary>
    /// A projection naming a subset of columns reads exactly those columns; non-projected
    /// fields rehydrate at <c>default(TField)</c>.
    /// </summary>
    [Fact]
    public async Task ReadAsync_ProjectionSubset_LeavesUnprojectedFieldsAtDefault()
    {
        var format = new CompressedColumnarSegmentFormat<ColumnarBar>(rowGroupMaxRows: 4);
        var input = Enumerable.Range(0, 8).Select(i => new ColumnarBar
        {
            Date = new DateOnly(2026, 1, 1).AddDays(i),
            Ticker = "AAPL",
            Close = 100m + i,
            Volume = 1_000_000L + i,
            TradeCount = 100 + i,
            Vwap = 100.0 + i,
            IsAdjusted = (i % 2) == 0,
            Residual = i * 0.01,
        }).ToList();

        using var stream = new MemoryStream();
        await format.WriteAsync(stream, input).ConfigureAwait(true);
        stream.Position = 0;

        var projection = new ColumnProjection([nameof(ColumnarBar.Date), nameof(ColumnarBar.Close)]);
        var rows = new List<ColumnarBar>();
        await foreach (var r in format.ReadAsync(
            stream, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), projection).ConfigureAwait(true))
        {
            rows.Add(r);
        }

        rows.Should().HaveCount(input.Count);
        rows.Select(r => r.Date).Should().Equal(input.Select(r => r.Date));
        rows.Select(r => r.Close).Should().Equal(input.Select(r => r.Close));
        // Non-projected fields stay at default(TField) — rehydrate sees no data for them.
        rows.Should().OnlyContain(r => r.Ticker == ""
            && r.Volume == 0
            && r.TradeCount == 0
            && r.Vwap == 0.0
            && r.IsAdjusted == false
            && r.Residual == 0.0);
    }

    /// <summary>
    /// A projection that names a missing column with <see cref="ColumnProjection.AllowMissing"/>
    /// set surfaces the missing slot — exercising the <c>missing.Add(name)</c> arm in
    /// <c>ResolveProjection</c>.
    /// </summary>
    [Fact]
    public async Task ReadAsync_ProjectionWithMissingColumnAndAllowMissing_FillsDefault()
    {
        var format = new CompressedColumnarSegmentFormat<CodecBarInt32>();
        var input = Enumerable.Range(0, 5).Select(MakeInt32Bar).ToList();

        using var stream = new MemoryStream();
        await format.WriteAsync(stream, input).ConfigureAwait(true);
        stream.Position = 0;

        // CodecBarInt32 declares only Date + Value; "PhantomColumn" is not in the segment.
        var projection = new ColumnProjection(
            [nameof(CodecBarInt32.Date), nameof(CodecBarInt32.Value), "PhantomColumn"],
            AllowMissing: true);

        var rows = new List<CodecBarInt32>();
        await foreach (var r in format.ReadAsync(
            stream, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), projection).ConfigureAwait(true))
        {
            rows.Add(r);
        }

        rows.Should().HaveCount(input.Count,
            "AllowMissing=true must permit the projection through and fill the missing column with defaults");
        rows.Select(r => r.Value).Should().Equal(input.Select(r => r.Value));
    }

    // ── 10: decimal-scale theory ────────────────────────────────────────────

    /// <summary>
    /// Round-trips the same logical values across four scale settings (0, 2, 4, 8) on a
    /// single record. Each scale uses the dictionary-decimal codec path; the canonical
    /// scaled-integer representation must reproduce the original logical decimal exactly.
    /// </summary>
    [Fact]
    public async Task RoundTrip_DecimalWithVariousScales_PreservesValues()
    {
        var format = new CompressedColumnarSegmentFormat<Phase12DecimalBar>();
        var input = Enumerable.Range(0, 10)
            .Select(i => new Phase12DecimalBar
            {
                Date = new DateOnly(2026, 1, 1).AddDays(i),
                Scale0 = 1000m + i,                  // integer values fit scale 0
                Scale2 = 1000m + (i * 0.25m),        // 2 decimal places
                Scale4 = 1000m + (i * 0.0001m),      // 4 decimal places
                Scale8 = 1000m + (i * 0.00000001m),  // 8 decimal places
            })
            .ToList();

        using var stream = new MemoryStream();
        await format.WriteAsync(stream, input).ConfigureAwait(true);
        stream.Position = 0;

        var rows = new List<Phase12DecimalBar>();
        await foreach (var r in format.ReadAsync(
            stream, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), ColumnProjection.All).ConfigureAwait(true))
        {
            rows.Add(r);
        }

        rows.Should().HaveCount(input.Count);
        for (var i = 0; i < input.Count; i++)
        {
            rows[i].Scale0.Should().Be(input[i].Scale0);
            rows[i].Scale2.Should().Be(input[i].Scale2);
            rows[i].Scale4.Should().Be(input[i].Scale4);
            rows[i].Scale8.Should().Be(input[i].Scale8);
        }
    }

    // ── 11: nullable rejection at writer ────────────────────────────────────

    /// <summary>
    /// The columnar writer rejects null reference and <see cref="Nullable{T}"/> values at the
    /// <c>BuildTypedValues</c> boundary so codec layers never see null. The Phase-12 spec
    /// originally proposed null-bitmap support; current v2.0.0 explicitly rejects nullable
    /// columns at write time. This test pins the rejection contract; the null-bitmap path is
    /// intentionally out of scope.
    /// </summary>
    [Fact]
    public async Task WriteAsync_NullStringPropertyValue_ThrowsInvalidOperation()
    {
        var format = new CompressedColumnarSegmentFormat<Phase12NullableStringBar>();
        var input = new[]
        {
            new Phase12NullableStringBar { Date = new DateOnly(2026, 1, 1), Value = "ok" },
            new Phase12NullableStringBar { Date = new DateOnly(2026, 1, 2), Value = null! },
        };

        using var stream = new MemoryStream();
        Func<Task> act = async () => await format.WriteAsync(stream, input).ConfigureAwait(true);

        (await act.Should().ThrowAsync<InvalidOperationException>().ConfigureAwait(true))
            .WithMessage("*null*");
    }

    // ── 12: single row group when input < rowGroupMaxRows ───────────────────

    /// <summary>
    /// When the input is smaller than <c>rowGroupMaxRows</c>, the writer emits a single
    /// row group — covering the loop-once branch of <c>PlanRowGroups</c>.
    /// </summary>
    [Fact]
    public async Task WriteAsync_FewerRowsThanRowGroupMax_ProducesSingleRowGroup()
    {
        var format = new CompressedColumnarSegmentFormat<CodecBarInt32>(rowGroupMaxRows: 100);
        var input = Enumerable.Range(0, 5).Select(MakeInt32Bar).ToList();

        using var stream = new MemoryStream();
        await format.WriteAsync(stream, input).ConfigureAwait(true);
        stream.Position = 0;

        var footer = FooterReader.Read(stream);
        footer.RowGroups.Should().HaveCount(1,
            "5 rows under a 100-row threshold must produce exactly one row group");
        footer.RowGroups[0].RowCount.Should().Be(input.Count);
    }

    // ═════════════════════════════════════════════════════════════════════════════════════
    // Batch B follow-up — tests the original Phase-12 spec named that were substituted or
    // skipped during the first pass. Each is now written against the actual implementation
    // behaviour rather than the spec's predicted behaviour.
    // ═════════════════════════════════════════════════════════════════════════════════════

    // ReadRowGroupsAsync_PerRowGroupCrcMismatch_YieldsCorruptResult
    // (originally substituted with the "all tampered" variant; the byte-tampering path is
    // distinct from the footer-CRC tampering path covered by RowGroupCrcTests).

    /// <summary>
    /// Tampers the on-disk bytes of one column inside a single row group's logical span,
    /// not the footer's stored CRC. Decode succeeds with garbage values (RawDouble has no
    /// internal structure to break), then the canonical-bytes recomputation diverges from
    /// the writer-recorded CRC, yielding <c>Quarantined</c> for ONLY that row group while
    /// the others surface <c>Ok</c>. Distinct from the existing
    /// <see cref="RowGroupCrcTests.ReadRowGroupsAsync_OnTamperedRowGroup_YieldsQuarantineForThatGroupOnly"/>
    /// which tampers the footer's stored CRC instead.
    /// </summary>
    [Fact]
    public async Task ReadRowGroupsAsync_RowGroupColumnDataTampered_YieldsQuarantineForThatGroup()
    {
        var format = new CompressedColumnarSegmentFormat<ColumnarBar>(rowGroupMaxRows: 4);
        var input = Enumerable.Range(0, 12).Select(i => new ColumnarBar
        {
            Date = new DateOnly(2026, 1, 1).AddDays(i),
            Ticker = "AAPL",
            Close = 100m + i,
            Volume = 1_000_000L + i,
            TradeCount = 100 + i,
            Vwap = 100.0 + i,
            IsAdjusted = (i % 2) == 0,
            Residual = i * 0.001,
        }).ToList();

        using var stream = new MemoryStream();
        await format.WriteAsync(stream, input).ConfigureAwait(true);

        // Locate Residual column — annotated with [ColumnEncoding(RawDouble)], a restartable
        // fixed-width 8-bytes-per-value codec. Tampering a single byte changes ONE decoded
        // double inside the targeted row group without breaking decode for any other row.
        var bytes = stream.ToArray();
        using (var probe = new MemoryStream(bytes))
        {
            var footer = FooterReader.Read(probe);
            var residual = footer.Columns.Single(c => c.Name == nameof(ColumnarBar.Residual));

            // Row group 1 (zero-indexed) starts at row index 4 within the column. RawDouble
            // is 8 bytes per value, so the start byte for RG 1 is residualOffset + 32.
            var rg1FirstByte = (int)residual.ByteOffset + 4 * 8;
            bytes[rg1FirstByte] ^= 0x55;
        }

        using var corrupt = new MemoryStream(bytes);
        var results = new List<RowGroupReadResult<ColumnarBar>>();
        await foreach (var r in format.ReadRowGroupsAsync(
            corrupt, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31)).ConfigureAwait(true))
        {
            results.Add(r);
        }

        results.Should().HaveCount(3, "12 rows / 4 per RG = 3 row groups");
        results[0].IsOk.Should().BeTrue("RG 0's bytes are intact");
        results[1].IsOk.Should().BeFalse(
            "RG 1's Residual bytes were tampered — canonical-bytes CRC must mismatch the footer's stored CRC");
        results[1].RowGroupIndex.Should().Be(1);
        results[2].IsOk.Should().BeTrue("RG 2's bytes are intact");
    }

    /// <summary>
    /// <see cref="CompressedColumnarSegmentFormat{TRecord}.ReadAsync"/> verifies each row group's
    /// CRC32C when it decodes every column: corruption that decodes successfully (a RawDouble bit
    /// flip) is rejected with <see cref="CorruptSegmentException"/> once the read reaches the
    /// corrupt row group, after the intact row group before it has been yielded.
    /// </summary>
    [Fact]
    public async Task ReadAsync_OneRowGroupBytesCorrupted_FailsClosedAtTheCorruptRowGroup()
    {
        var format = new CompressedColumnarSegmentFormat<ColumnarBar>(rowGroupMaxRows: 4);
        var input = Enumerable.Range(0, 12).Select(i => new ColumnarBar
        {
            Date = new DateOnly(2026, 1, 1).AddDays(i),
            Ticker = "AAPL",
            Close = 100m + i,
            Volume = 1_000_000L + i,
            TradeCount = 100 + i,
            Vwap = 100.0 + i,
            IsAdjusted = (i % 2) == 0,
            Residual = i * 0.001,
        }).ToList();

        using var stream = new MemoryStream();
        await format.WriteAsync(stream, input).ConfigureAwait(true);

        var bytes = stream.ToArray();
        long residualOffset;
        using (var probe = new MemoryStream(bytes))
        {
            var footer = FooterReader.Read(probe);
            var residual = footer.Columns.Single(c => c.Name == nameof(ColumnarBar.Residual));
            residualOffset = residual.ByteOffset;
        }

        // Tamper the first 8 bytes of RG 1's Residual span. Decode produces a different
        // double; the rest of the column decodes unchanged.
        var rg1FirstByte = (int)residualOffset + 4 * 8;
        bytes[rg1FirstByte] ^= 0x55;

        using var corrupt = new MemoryStream(bytes);
        var rows = new List<ColumnarBar>();
        var act = async () =>
        {
            await foreach (var r in format.ReadAsync(
                corrupt, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), ColumnProjection.All).ConfigureAwait(true))
            {
                rows.Add(r);
            }
        };

        (await act.Should().ThrowAsync<CorruptSegmentException>(
            "RG 1's decoded Residual no longer matches the footer's stored CRC").ConfigureAwait(true))
            .Which.Message.Should().Contain("row group 1");

        // RG 0 (rows 0..3) is intact and was served before the read reached RG 1; nothing from
        // the tampered row group surfaced.
        rows.Should().Equal(input.Take(4));
    }

    // ReadAsync_FooterClaimsColumnNotInRecordType (fix-5: now throws
    // ColumnNotFoundException instead of silently skipping; both ReadBatchesCoreAsync
    // and ReadRowGroupsAsync are now consistent on this path).

    /// <summary>
    /// When the footer carries a column descriptor whose name does not exist on
    /// <c>TRecord</c>, the reader throws <see cref="ColumnNotFoundException"/>
    /// to signal the schema mismatch explicitly. Silently skipping would produce incomplete
    /// records with no indication of the missing column.
    /// </summary>
    [Fact]
    public async Task ReadAsync_FooterClaimsColumnNotInRecordType_ThrowsColumnNotFoundException()
    {
        var format = new CompressedColumnarSegmentFormat<CodecBarInt32>();
        var input = Enumerable.Range(0, 5).Select(MakeInt32Bar).ToList();

        using var stream = new MemoryStream();
        await format.WriteAsync(stream, input).ConfigureAwait(true);

        // Read footer, append a Phantom column descriptor pointing at a zero-byte span so
        // the bytes-read step is a no-op. Then per-row-group decode hits the silent-skip
        // arm because TRecord exposes no "Phantom" property.
        var bytes = stream.ToArray();
        SegmentFooter footer;
        using (var probe = new MemoryStream(bytes))
        {
            footer = FooterReader.Read(probe);
        }
        var phantom = new ColumnDescriptor(
            Name: "Phantom",
            ByteOffset: 0L,
            ByteLength: 0L,
            EncodingTag: ColumnEncodingTag.Dictionary,
            CodecVersion: DictionaryCodec.Version,
            FieldType: ColumnFieldTypeCode.Int32);
        // Drop ZoneMaps and BitmapIndexes when synthesising the tampered footer — both
        // include per-column structures whose count must match the column list, and we're
        // adding a Phantom column without per-column stats. RowGroupCrcs is per-row-group
        // (independent of column count) so it stays.
        var tampered = new SegmentFooter(
            footer.FormatVersion,
            footer.Columns.Concat([phantom]).ToList(),
            footer.RowGroups,
            ZoneMaps: null,
            BitmapIndexes: null,
            footer.RowGroupCrcs,
            footer.RowGroupByteOffsets);

        var oldFooterLengthPos = bytes.Length - SegmentMagic.TrailerSize;
        var oldFooterLength = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(oldFooterLengthPos, 4));
        var columnDataEnd = bytes.Length - SegmentMagic.TrailerSize - SegmentMagic.FooterCrcSize - (int)oldFooterLength;

        using var rebuilt = new MemoryStream();
        rebuilt.Write(bytes, 0, columnDataEnd);
        FooterWriter.Write(rebuilt, tampered);
        rebuilt.Position = 0;

        // Act + Assert — reading a segment that declares a column the format instance does
        // not recognise must throw ColumnNotFoundException (fix-5: fail-fast, not silent skip).
        var act = async () =>
        {
            await foreach (var _ in format.ReadAsync(
                rebuilt, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), ColumnProjection.All).ConfigureAwait(true))
            {
            }
        };
        await act.Should().ThrowAsync<ColumnNotFoundException>()
            .WithMessage("*Phantom*", "the exception must name the unrecognised column")
            .ConfigureAwait(true);
    }

    // ReadAsync_FooterClaimsUnknownEncodingTag (existing
    // ColumnarErrorPathTests.UnknownEncodingTag_ThrowsUnsupportedEncodingException covers
    // ReadAsync; this test pins the same contract for ReadRowGroupsAsync).

    /// <summary>
    /// <see cref="CompressedColumnarSegmentFormat{TRecord}.ReadRowGroupsAsync"/> validates
    /// every column's encoding tag through <c>ValidateEncodingSupport</c> before any decode
    /// occurs; an unknown tag (outside the registered <see cref="ColumnEncodingTag"/> range)
    /// surfaces <see cref="UnsupportedEncodingException"/>. Existing coverage proves this for
    /// <c>ReadAsync</c>; this test pins the contract for the row-group reader too.
    /// </summary>
    [Fact]
    public async Task ReadRowGroupsAsync_FooterClaimsUnknownEncodingTag_Throws()
    {
        var format = new CompressedColumnarSegmentFormat<CodecBarInt32>();
        var input = Enumerable.Range(0, 5).Select(MakeInt32Bar).ToList();

        using var stream = new MemoryStream();
        await format.WriteAsync(stream, input).ConfigureAwait(true);

        // Tamper the first column's encoding-tag byte to 0xC8 (200 — outside the registered
        // 0..6 range), then repair the footer CRC so the envelope check passes and the
        // reader proceeds to the per-column codec validation.
        var bytes = stream.ToArray();
        TamperFirstColumnEncodingTag(bytes, newTag: 200);
        RepairFooterCrc(bytes);

        using var corrupt = new MemoryStream(bytes);
        Func<Task> act = async () =>
        {
            await foreach (var _ in format.ReadRowGroupsAsync(
                corrupt, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31)).ConfigureAwait(true))
            {
            }
        };

        (await act.Should().ThrowAsync<UnsupportedEncodingException>().ConfigureAwait(true))
            .Which.EncodingTag.Should().Be(200);
    }

    // ═════════════════════════════════════════════════════════════════════════════════════
    // Footer-tampering helpers — duplicated from ColumnarErrorPathTests to keep this file
    // self-contained (the originals are private; co-locating avoids a cross-file refactor).
    // ═════════════════════════════════════════════════════════════════════════════════════

    private static void TamperFirstColumnEncodingTag(byte[] bytes, byte newTag)
    {
        var (payloadStart, _) = LocateFooterPayload(bytes);
        var cursor = payloadStart + 10; // magic (4) + version (4) + column count (2)
        var nameLen = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(
            bytes.AsSpan(cursor, 2));
        cursor += 2 + nameLen;
        bytes[cursor] = newTag;
    }

    private static (int PayloadStart, int PayloadLength) LocateFooterPayload(byte[] bytes)
    {
        var trailerStart = bytes.Length - 8;
        var footerLength = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(trailerStart, 4));
        var footerPayloadEnd = trailerStart - 4;
        var footerPayloadStart = footerPayloadEnd - footerLength;
        return (footerPayloadStart, footerLength);
    }

    private static void RepairFooterCrc(byte[] bytes)
    {
        var (payloadStart, payloadLength) = LocateFooterPayload(bytes);
        var payload = bytes.AsSpan(payloadStart, payloadLength).ToArray();
        var crc = Boutquin.Storage.Infrastructure.Algorithms.Crc32C.HashToUInt32(payload);
        var crcOffset = payloadStart + payloadLength;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(crcOffset, 4), crc);
    }
}
