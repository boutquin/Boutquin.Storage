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
namespace Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar;

/// <summary>
/// Per-(row-group, column) zone-map statistics written into the columnar-segment footer.
/// Lets a reader skip whole row groups whose <see cref="MinValue"/>/<see cref="MaxValue"/>
/// span does not overlap a predicate range, without decoding any column bytes.
/// </summary>
/// <remarks>
/// <para><b>Theory:</b> DDIA 2e Ch. 4 — "zone maps" or "data skipping indexes" are
/// the primary mechanism by which analytical columnar stores avoid reading row groups that
/// cannot contribute to a query. For a range predicate <c>col BETWEEN x AND y</c>, any row
/// group whose <c>[min, max]</c> is disjoint from <c>[x, y]</c> is provably empty of matches
/// and can be skipped at footer-scan time.</para>
/// <para><b>Encoding of <see cref="MinValue"/>/<see cref="MaxValue"/>:</b> canonical on-disk
/// bytes per the column's CLR type — int32/int64/float/double LE for fixed-width numerics,
/// 4-byte day-number LE for <c>DateOnly</c>, 16-byte <c>decimal.GetBits</c> LE for
/// <see cref="decimal"/>, a single byte (0/1) for <c>bool</c>, UTF-8 bytes (truncated to
/// 256 bytes) for <c>string</c>. A column with no observable values in this row group
/// (e.g., entirely null) encodes <see cref="MinValue"/> and <see cref="MaxValue"/> as empty
/// arrays; consumers MUST treat empty min/max as "no information" and fall through to full
/// decode.</para>
/// <para><b>Greenfield-compatibility note:</b> the zone-map footer section is additive —
/// segments written before Phase E simply omit it, and readers that find the section absent
/// treat every row group as having no stats (full-scan fall-through). No format-version bump
/// is taken — under the greenfield premise (no persisted pre-2.0.0 columnar segments), v1
/// evolves additively.</para>
/// </remarks>
public sealed record ColumnRowGroupStats
{
    private readonly byte[] _minValue;
    private readonly byte[] _maxValue;

    /// <summary>Creates a new set of zone-map statistics.</summary>
    /// <param name="MinValue">
    /// Canonical on-disk bytes of the minimum observed non-null value in the row group, or
    /// empty if no non-null values are present. Never <see langword="null"/>.
    /// </param>
    /// <param name="MaxValue">
    /// Canonical on-disk bytes of the maximum observed non-null value in the row group, or
    /// empty if no non-null values are present. Never <see langword="null"/>.
    /// </param>
    /// <param name="NullCount">
    /// Number of rows in the row group whose value for this column is null. For non-nullable
    /// CLR types this is always <c>0</c>. Must be non-negative.
    /// </param>
    /// <param name="DistinctCount">
    /// Exact count of distinct observed values in the row group (including null if present
    /// counts as one distinct). Readers may use this to choose between zone-map skipping and
    /// bitmap-index lookup. Must be non-negative.
    /// </param>
    public ColumnRowGroupStats(byte[] MinValue, byte[] MaxValue, int NullCount, int DistinctCount)
    {
        ArgumentNullException.ThrowIfNull(MinValue);
        ArgumentNullException.ThrowIfNull(MaxValue);
        ArgumentOutOfRangeException.ThrowIfNegative(NullCount);
        ArgumentOutOfRangeException.ThrowIfNegative(DistinctCount);

        // Defensive copy on construction: the record's value-equality and hash code derive from
        // these byte payloads, so storing the caller's array by reference would let a
        // post-construction mutation of the caller's array silently change this record's identity
        // (and relocate it in any hash-based collection). Clone keeps the byte[] public shape
        // (DR-2) while making the value object truly immutable. This is a cold zone-map build
        // path, not a per-row scan, so the clone has no per-row allocation cost.
        _minValue = (byte[])MinValue.Clone();
        _maxValue = (byte[])MaxValue.Clone();
        this.NullCount = NullCount;
        this.DistinctCount = DistinctCount;
    }

    /// <summary>Canonical on-disk bytes of the minimum observed non-null value in the row group.</summary>
    public byte[] MinValue => (byte[])_minValue.Clone();

    /// <summary>Canonical on-disk bytes of the maximum observed non-null value in the row group.</summary>
    public byte[] MaxValue => (byte[])_maxValue.Clone();

    /// <summary>Number of rows in the row group whose value for this column is null.</summary>
    public int NullCount { get; }

    /// <summary>Exact count of distinct observed values in the row group.</summary>
    public int DistinctCount { get; }

    /// <inheritdoc />
    public bool Equals(ColumnRowGroupStats? other) =>
        other is not null
        && NullCount == other.NullCount
        && DistinctCount == other.DistinctCount
        && _minValue.AsSpan().SequenceEqual(other._minValue)
        && _maxValue.AsSpan().SequenceEqual(other._maxValue);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(NullCount);
        hash.Add(DistinctCount);
        hash.AddBytes(_minValue);
        hash.AddBytes(_maxValue);
        return hash.ToHashCode();
    }
}
