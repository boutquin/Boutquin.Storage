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
namespace Boutquin.Storage.Domain.Exceptions;

/// <summary>
/// A strict-mode columnar projection named a column that the segment's footer does not
/// declare. The caller asserted the column should exist — for strict readers, the column's
/// absence is a programmer error (typo, rename, cross-generation read without opt-in to
/// schema-evolution tolerance). Callers that knowingly span segment generations set
/// <c>ColumnProjection.AllowMissing = true</c> to receive <c>default(TField)</c> fills
/// instead.
/// </summary>
/// <remarks>
/// The exception message enumerates the columns the segment <em>does</em> declare so the
/// caller can correlate the miss with a rename or a projection-name typo without opening
/// the file.
/// </remarks>
public sealed class ColumnNotFoundException(
    string columnName,
    uint segmentFormatVersion,
    IReadOnlyList<string> availableColumns) : Exception($"Column '{columnName}' is not present in this columnar segment (format v{segmentFormatVersion}). Available columns: [{string.Join(", ", availableColumns)}]. Set ColumnProjection.AllowMissing = true to receive default values instead of this exception.")
{
    /// <summary>The column name that was requested but not found.</summary>
    public string ColumnName { get; } = columnName;

    /// <summary>The segment's declared format version.</summary>
    public uint SegmentFormatVersion { get; } = segmentFormatVersion;

    /// <summary>The column names the segment actually declares, in footer order.</summary>
    /// <remarks>Defensive copy taken at construction so mutations to the caller's source list
    /// after the exception is created do not alter the snapshot captured in the exception.</remarks>
    public IReadOnlyList<string> AvailableColumns { get; } = availableColumns.ToArray();
}
