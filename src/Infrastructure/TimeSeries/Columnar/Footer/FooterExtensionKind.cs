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
namespace Boutquin.Storage.Infrastructure.TimeSeries.Columnar.Footer;

/// <summary>
/// Tag byte identifying an optional trailing section appended to the columnar-segment footer
/// payload after the row-group list. Each extension section is encoded as
/// <c>[kind: 1B][length: uint32 LE][payload: length bytes]</c>; readers that encounter an
/// unknown kind skip it by advancing <c>length</c> bytes. This mechanism lets Phase E
/// (zone maps, bitmap indexes, row-group CRCs) land without a format-version bump and
/// leaves room for future additions on the same v1 substrate.
/// </summary>
internal enum FooterExtensionKind : byte
{
    /// <summary>Per-(row-group, column) <see cref="Domain.ValueObjects.TimeSeries.Columnar.ColumnRowGroupStats"/> (Phase E1).</summary>
    ZoneMaps = 0x01,

    /// <summary>Cross-row-group <see cref="Domain.ValueObjects.TimeSeries.Columnar.BitmapIndexEntry"/> list (Phase E2).</summary>
    BitmapIndexes = 0x02,

    /// <summary>Per-row-group CRC32C (Castagnoli) for data-integrity quarantining (Phase E3).</summary>
    RowGroupCrcs = 0x03,

    /// <summary>
    /// Per-column byte-run offsets for non-restartable codecs (GorillaXor, DeltaOfDeltaVlq).
    /// Maps each such column name to the byte offset of each row group's independent encoding
    /// run within the column blob, enabling O(1) per-row-group seeks during batch reads (P3.2).
    /// </summary>
    RowGroupByteOffsets = 0x04,
}
