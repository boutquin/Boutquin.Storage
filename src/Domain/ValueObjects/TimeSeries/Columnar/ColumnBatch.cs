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
using Boutquin.Storage.Domain.Interfaces.TimeSeries;

namespace Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar;

/// <summary>
/// A row-group-aligned slice of decoded column arrays, returned by
/// <c>IColumnarSegmentFormat&lt;TRecord&gt;.ReadBatchesAsync</c>. Each batch corresponds to
/// one row group in the on-disk segment.
/// </summary>
/// <typeparam name="TRecord">The dated record type whose fields are exposed as columns.</typeparam>
/// <param name="RowCount">Number of rows in this batch.</param>
/// <param name="Columns">Decoded column arrays keyed by property name. Each value is a
/// typed <see cref="Array"/> (e.g., <c>DateOnly[]</c>, <c>decimal[]</c>); cast with
/// the appropriate element type after checking the array's element type.</param>
/// <param name="MinDate">Earliest date in this batch.</param>
/// <param name="MaxDate">Latest date in this batch.</param>
public sealed record ColumnBatch<TRecord>(
    int RowCount,
    IReadOnlyDictionary<string, Array> Columns,
    DateOnly MinDate,
    DateOnly MaxDate)
    where TRecord : IHasDate, new();
