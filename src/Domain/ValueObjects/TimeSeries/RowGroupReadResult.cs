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
namespace Boutquin.Storage.Domain.ValueObjects.TimeSeries;

/// <summary>
/// Result of reading a single row group with integrity validation enabled (Phase E3). Yields
/// either the decoded rows or — when the row group's content CRC32C fails to match the value
/// recorded in the footer at write time — a quarantine marker naming the row-group index and
/// the two CRCs so the caller can decide whether to fail the query, skip the group, or re-read
/// from a replica.
/// </summary>
/// <remarks>
/// <para><b>Theory:</b> DDIA 2e p. 140 bottom — "checksums in the log and discarding corrupted
/// entries" is the canonical storage-engine response to corruption; this type surfaces the
/// result instead of throwing so the caller's query plan can compose over partial results
/// (DDIA 2e Ch. 4 — canonical-bytes recomputation for CRC verification after corrupt-tolerant reads).</para>
/// <para><b>Semantics:</b> <see cref="IsOk"/> is <see langword="true"/> iff the CRC matched
/// (or no CRC was recorded) and the rows decoded without error; <see cref="Rows"/> is
/// non-null in that case. Otherwise <see cref="IsOk"/> is <see langword="false"/> and the
/// diagnostic CRCs are populated on the instance.</para>
/// </remarks>
/// <typeparam name="TRecord">The record type being read.</typeparam>
public sealed record RowGroupReadResult<TRecord>
{
    private RowGroupReadResult(
        int rowGroupIndex,
        IReadOnlyList<TRecord>? rows,
        bool isOk,
        uint expectedCrc,
        uint actualCrc)
    {
        RowGroupIndex = rowGroupIndex;
        Rows = rows;
        IsOk = isOk;
        ExpectedCrc = expectedCrc;
        ActualCrc = actualCrc;
    }

    /// <summary>Zero-based index of the row group this result describes.</summary>
    public int RowGroupIndex { get; }

    /// <summary><see langword="true"/> iff the row group decoded cleanly; <see cref="Rows"/> is populated.</summary>
    public bool IsOk { get; }

    /// <summary>
    /// The decoded rows in the row group, in row order. Non-null iff <see cref="IsOk"/> is
    /// <see langword="true"/>. May be empty if the row group's date range did not intersect
    /// the caller's <c>(from, to)</c> predicate.
    /// </summary>
    public IReadOnlyList<TRecord>? Rows { get; }

    /// <summary>
    /// CRC32C (Castagnoli) the footer recorded at write time. Populated iff <see cref="IsOk"/>
    /// is <see langword="false"/>; otherwise zero.
    /// </summary>
    public uint ExpectedCrc { get; }

    /// <summary>
    /// CRC32C (Castagnoli) computed at read time over the same canonical row-group byte
    /// representation used by the writer. Populated iff <see cref="IsOk"/> is
    /// <see langword="false"/>; otherwise zero.
    /// </summary>
    public uint ActualCrc { get; }

    /// <summary>Builds an <c>Ok</c> result carrying the decoded rows.</summary>
    public static RowGroupReadResult<TRecord> Ok(int rowGroupIndex, IReadOnlyList<TRecord> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return new RowGroupReadResult<TRecord>(rowGroupIndex, rows, isOk: true, 0u, 0u);
    }

    /// <summary>Builds a <c>Quarantined</c> result carrying the expected vs actual CRC.</summary>
    public static RowGroupReadResult<TRecord> Quarantined(int rowGroupIndex, uint expectedCrc, uint actualCrc) =>
        new(rowGroupIndex, rows: null, isOk: false, expectedCrc, actualCrc);
}
