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
/// Footer-level metadata for one row group: its date span and row count. Lets the reader
/// skip non-matching groups by scanning the footer alone before seeking into column data.
/// </summary>
/// <param name="MinDate">Inclusive minimum date in this row group.</param>
/// <param name="MaxDate">Inclusive maximum date in this row group.</param>
/// <param name="RowCount">Number of rows in this row group.</param>
/// <param name="FirstRowIndex">
/// Absolute 0-based row index of the first row in this group within the whole segment.
/// Required so the reader knows the starting offset into each column's data span
/// (per-column sliced offsets are derived as <c>FirstRowIndex × sizeof(field-type)</c> for
/// fixed-width fields; variable-width types like strings record a per-group offset table in
/// a future format version).
/// </param>
internal readonly record struct RowGroupDescriptor(
    DateOnly MinDate,
    DateOnly MaxDate,
    int RowCount,
    int FirstRowIndex);
