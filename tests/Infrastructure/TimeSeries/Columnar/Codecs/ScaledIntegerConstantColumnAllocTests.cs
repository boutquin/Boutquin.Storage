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
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar.Codecs;
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar.Footer;

namespace Boutquin.Storage.Infrastructure.Tests.TimeSeries.Columnar.Codecs;

/// <summary>
/// Allocation-before-bound DoS guard for the <b>constant-column</b> (<c>bitWidth==0</c>) arm of
/// <see cref="ScaledIntegerBitPackedCodec"/>. A constant decimal column legitimately encodes any
/// number of identical values in a ~0-byte body, so the payload carries no ceiling on the row
/// count. The footer-supplied <c>RowCount</c> (CRC is forgeable) is therefore the only count
/// source, and it MUST be bounded against an independent segment-level ceiling
/// (<c>maxDecodableRows</c>, threaded from the caller) before <c>new decimal[count]</c> — otherwise
/// a forged <c>RowCount</c> over a tiny segment drives a multi-gigabyte allocation (OOM DoS) on the
/// production columnar read path.
///
/// The existing <see cref="CodecUnboundedRowCountAllocTests"/> /
/// <see cref="CodecSliceUnboundedRowCountAllocTests"/> only exercise <c>bitWidth&gt;0</c>, whose
/// payload-derived ceiling already bounds the count — the <c>bitWidth==0</c> arm has no such
/// payload ceiling and is the gap closed here.
/// </summary>
public sealed class ScaledIntegerConstantColumnAllocTests
{
    private const int ScaledIntegerHeaderSize = 10; // scale (1) + min (8) + bitWidth (1)

    // A forged count whose decimal[] (~16 B/element) is large enough to prove an over-allocation
    // happened today (no bound ⇒ no throw) but small enough not to OOM the test host before the
    // post-fix bound fires. 30M × 16 B ≈ 480 MB.
    private const int ForgedRowCount = 30_000_000;

    [Fact]
    public void DecodeDecimal_ConstantColumn_ForgedCountExceedingSegmentCeiling_ThrowsBeforeAllocating()
    {
        // bitWidth==0 constant-column header: scale=2, min=0, bitWidth=0 (10 bytes, empty body).
        // The payload offers no ceiling, so the caller-supplied maxDecodableRows ceiling (here the
        // 10-byte header length, standing in for the segment byte length) must reject the forged
        // count BEFORE new decimal[ForgedRowCount] is allocated.
        var payload = new byte[ScaledIntegerHeaderSize];
        payload[0] = 2;  // scale
        payload[9] = 0;  // bitWidth = 0 → constant column

        var act = () => ScaledIntegerBitPackedCodec.DecodeDecimal(
            payload, ForgedRowCount, maxDecodableRows: payload.Length);

        act.Should().Throw<InvalidDataException>(
            "a constant column cannot legitimately declare more rows than the segment ceiling permits");
    }

    [Fact]
    public void DecodeDecimalSlice_ConstantColumn_ForgedCountExceedingSegmentCeiling_ThrowsBeforeAllocating()
    {
        var payload = new byte[ScaledIntegerHeaderSize];
        payload[0] = 2;  // scale
        payload[9] = 0;  // bitWidth = 0 → constant column

        var act = () => ScaledIntegerBitPackedCodec.DecodeDecimalSlice(
            payload, firstRowIndex: 0, count: ForgedRowCount, maxDecodableRows: payload.Length);

        act.Should().Throw<InvalidDataException>(
            "the slice arm shares the constant-column unbounded-allocation surface");
    }

    [Fact]
    public void DecodeDecimal_ConstantColumn_CountWithinCeiling_RoundTrips()
    {
        // A legitimate constant column with a small row count, well within the ceiling, still
        // decodes — the bound rejects only forgery, not honest constant columns.
        var encoded = ScaledIntegerBitPackedCodec.EncodeDecimal(
            [12.3400m, 12.3400m, 12.3400m, 12.3400m]);
        encoded[9].Should().Be((byte)0, "all-equal values produce a zero bit-width constant column");

        var decoded = ScaledIntegerBitPackedCodec.DecodeDecimal(
            encoded, count: 4, maxDecodableRows: 4);

        decoded.Should().Equal(12.3400m, 12.3400m, 12.3400m, 12.3400m);
    }

    [Fact]
    public void DecodeDecimalSlice_ConstantColumn_CountWithinCeiling_RoundTrips()
    {
        var encoded = ScaledIntegerBitPackedCodec.EncodeDecimal(
            [12.3400m, 12.3400m, 12.3400m, 12.3400m]);
        encoded[9].Should().Be((byte)0);

        var slice = ScaledIntegerBitPackedCodec.DecodeDecimalSlice(
            encoded, firstRowIndex: 1, count: 2, maxDecodableRows: 4);

        slice.Should().Equal(12.3400m, 12.3400m);
    }

    // ───── End-to-end production path (the CR2 reproduction) ──────────────────

    [Fact]
    public async Task ReadRowGroupsAsync_ForgedConstantDecimalColumn_RejectsBeforeGiantAlloc()
    {
        // Write a real single-row-group segment whose Price column is constant (all 12.3400m ⇒
        // bitWidth==0). Forge the row group's RowCount far beyond the segment byte length and
        // reorder the footer so the constant Price (ScaledIntegerBitPacked) column decodes FIRST —
        // exactly the CR2 probe. Pre-fix the production DecodeColumn path allocates the unbounded
        // new decimal[RowCount]; post-fix the segment ceiling (input.Length) threaded into the codec
        // rejects the forged count with a bounded corruption exception.
        var format = new CompressedColumnarSegmentFormat<Phase10ScaledDecimalBar>();
        var input = new List<Phase10ScaledDecimalBar>
        {
            new() { Date = new DateOnly(2026, 1, 1), Price = 12.3400m },
            new() { Date = new DateOnly(2026, 1, 2), Price = 12.3400m },
            new() { Date = new DateOnly(2026, 1, 3), Price = 12.3400m },
            new() { Date = new DateOnly(2026, 1, 4), Price = 12.3400m },
        };

        using var stream = new MemoryStream();
        await format.WriteAsync(stream, input).ConfigureAwait(true);
        var bytes = stream.ToArray();

        var footer = FooterReader.Read(new MemoryStream(bytes));

        // Forge a row count that exceeds the production read-side per-row-group allocation ceiling
        // (CompressedColumnarSegmentFormat.MaxDecodableRowsPerRowGroup = 1 << 26). int.MaxValue
        // (~2.1B) dwarfs it, so the constant-column ceiling rejects it as corruption BEFORE the
        // ~32 GB new decimal[count] would be allocated. (The old segment-byte ceiling rejected a
        // mere 30M forge, but that bound false-rejected valid large compressible columns and was
        // replaced by the per-row-group cap — F-W3-regress-01.)
        const int forgedEndToEndRowCount = int.MaxValue;
        var forgedRowGroups = footer.RowGroups
            .Select(rg => new RowGroupDescriptor(rg.MinDate, rg.MaxDate, forgedEndToEndRowCount, rg.FirstRowIndex))
            .ToList();

        // Decode the constant Price (ScaledIntegerBitPacked) column FIRST so its unbounded alloc
        // would fire before the bounded Date (DeltaOfDeltaVlq) column's own guard could throw.
        var priceFirst = footer.Columns
            .OrderByDescending(c => c.EncodingTag == ColumnEncodingTag.ScaledIntegerBitPacked)
            .ToList();

        var forgedFooter = new SegmentFooter(
            footer.FormatVersion,
            priceFirst,
            forgedRowGroups,
            ZoneMaps: null,
            BitmapIndexes: null,
            RowGroupCrcs: null,
            RowGroupByteOffsets: footer.RowGroupByteOffsets);

        var oldFooterLengthPos = bytes.Length - SegmentMagic.TrailerSize;
        var oldFooterLength = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(oldFooterLengthPos, 4));
        var columnDataEnd = bytes.Length - SegmentMagic.TrailerSize - SegmentMagic.FooterCrcSize - (int)oldFooterLength;

        using var rebuilt = new MemoryStream();
        rebuilt.Write(bytes, 0, columnDataEnd);
        FooterWriter.Write(rebuilt, forgedFooter);
        rebuilt.Position = 0;

        // The forged RowCount (int.MaxValue) dwarfs the per-row-group ceiling, so the constant-
        // column decode must reject it (corruption) rather than allocate ~32 GB — the production
        // caller threads the per-row-group ceiling into the codec. ColumnDecodeException wraps it.
        var act = async () =>
        {
            await foreach (var _ in format.ReadRowGroupsAsync(
                rebuilt, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31)).ConfigureAwait(true))
            {
            }
        };

        (await act.Should().ThrowAsync<ColumnDecodeException>().ConfigureAwait(true))
            .Which.Message.Should().Contain("Price",
                "the forged constant Price column is rejected at decode time, before the giant allocation");
    }
}
