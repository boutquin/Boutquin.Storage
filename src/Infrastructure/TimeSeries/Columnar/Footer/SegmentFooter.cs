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

namespace Boutquin.Storage.Infrastructure.TimeSeries.Columnar.Footer;

/// <summary>
/// In-memory representation of a columnar segment footer. Produced by the writer after all
/// column data has been emitted; consumed by the reader after locating and CRC-validating
/// the footer bytes via the trailer.
/// </summary>
/// <param name="FormatVersion">
/// The segment-format version this footer was written under. Readers compare against
/// <see cref="SegmentMagic.CurrentFormatVersion"/> and raise
/// <c>UnsupportedFormatVersionException</c> on mismatch.
/// </param>
/// <param name="Columns">Per-column descriptors in the order they were written.</param>
/// <param name="RowGroups">Per-row-group descriptors in date-ascending order.</param>
/// <param name="ZoneMaps">
/// Optional per-(row-group, column) zone-map statistics (Phase E1). Outer list is indexed by
/// row-group; inner list is indexed by column (same order as <paramref name="Columns"/>).
/// Null when the writer did not emit zone-map statistics — readers MUST treat null as
/// "no information" and fall through to full row-group scan.
/// </param>
/// <param name="BitmapIndexes">
/// Optional cross-row-group bitmap indexes for low-cardinality columns (Phase E2). Null when
/// the writer did not emit any bitmap indexes. Each entry names the column and carries the
/// per-distinct-value bitmap blobs.
/// </param>
/// <param name="RowGroupCrcs">
/// Optional per-row-group CRC32C (Castagnoli) for data-integrity validation at row-group
/// granularity (Phase E3). Indexed by row-group; count equals <paramref name="RowGroups"/>
/// count when present. Null when the writer did not emit row-group CRCs.
/// </param>
/// <param name="RowGroupByteOffsets">
/// Optional per-column byte-run offsets for non-restartable codecs (GorillaXor,
/// DeltaOfDeltaVlq). Each entry maps a column name to a list of byte offsets — one per row
/// group — within that column's blob. The slice for row group <c>i</c> starts at
/// <c>offsets[i]</c> and ends at <c>offsets[i+1]</c> (or the end of the blob for the last
/// group). Null when no non-restartable columns are present or when the segment was written
/// without P3.2 support.
/// </param>
internal sealed record SegmentFooter(
    uint FormatVersion,
    IReadOnlyList<ColumnDescriptor> Columns,
    IReadOnlyList<RowGroupDescriptor> RowGroups,
    IReadOnlyList<IReadOnlyList<ColumnRowGroupStats>>? ZoneMaps = null,
    IReadOnlyList<BitmapIndexEntry>? BitmapIndexes = null,
    IReadOnlyList<uint>? RowGroupCrcs = null,
    IReadOnlyDictionary<string, IReadOnlyList<int>>? RowGroupByteOffsets = null);
