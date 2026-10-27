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
namespace Boutquin.Storage.Domain.Enums;

/// <summary>
/// Direction of a per-column sort declared by
/// <see cref="ValueObjects.TimeSeries.ColumnSortSpec"/> for columnar segment writes.
/// </summary>
/// <remarks>
/// <para>
/// Direction applies per column: a multi-key spec may mix ascending and descending entries.
/// Both directions are honoured by the generator-emitted comparer on
/// <c>[ColumnarStorable]</c> records and by the reflection-fallback comparator for
/// hand-written records. Sort direction governs the <i>physical row order within a written
/// segment block</i>; it is independent of <see cref="ScanDirection"/>, which governs
/// iteration order on the read path (v2.0.0 reads ship ascending iteration only).
/// </para>
/// </remarks>
public enum SortDirection
{
    /// <summary>Ascending sort: the natural calendar / numeric / lexicographic order.</summary>
    Ascending = 0,

    /// <summary>Descending sort: the exact inverse of <see cref="Ascending"/> per column.</summary>
    Descending = 1,
}
