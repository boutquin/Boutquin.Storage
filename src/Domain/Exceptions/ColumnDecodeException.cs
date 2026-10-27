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
/// A per-column codec failed mid-decode — the column's on-disk bytes are corrupt in a way
/// the codec can detect (e.g., VLQ overrun, dictionary-id out of range, bit-packed payload
/// length mismatch) but the footer, CRC, and format version all validated. Thin wrapper that
/// carries the offending column / row-group context alongside the inner decoder exception.
/// </summary>
/// <remarks>
/// Distinguished from <see cref="CorruptSegmentException"/> (which fires on footer-level
/// integrity checks) and <see cref="TruncatedSegmentException"/> (file-level truncation).
/// This exception fires per-column, after the envelope has been validated, when the column
/// payload itself is internally inconsistent.
/// </remarks>
public sealed class ColumnDecodeException(
    string columnName,
    int rowGroupIndex,
    long byteOffset,
    string message,
    Exception innerException) : Exception(message, innerException)
{
    /// <summary>The column whose decode failed.</summary>
    public string ColumnName { get; } = columnName;

    /// <summary>The row-group index within the segment where the failure occurred, or -1 if unavailable.</summary>
    public int RowGroupIndex { get; } = rowGroupIndex;

    /// <summary>The byte offset within the column's span where the failure was detected, or -1 if unavailable.</summary>
    public long ByteOffset { get; } = byteOffset;
}
