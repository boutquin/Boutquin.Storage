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
/// Declares the column sort order for rows written inside a columnar segment block.
/// Columnar writers honour this spec so the on-disk row order maximises run-length
/// opportunities for the first sort column and compression ratios across the board —
/// the warehouse-style recommendation in DDIA 2e p.140.
/// </summary>
/// <remarks>
/// <para><b>Shape:</b> each entry is a <c>(string Name, SortDirection Direction)</c>
/// tuple so per-column direction is first-class — a multi-key spec may mix
/// <see cref="SortDirection.Ascending"/> and <see cref="SortDirection.Descending"/> entries.
/// The shape was previously <c>IReadOnlyList&lt;string&gt;</c> (ascending-only by API), which
/// would have required a breaking change to surface directional sorts.</para>
/// <para><b>Default for time-series workloads:</b>
/// <see cref="DateAscending"/>. For daily-bar records this is the natural calendar order
/// already imposed by the LSM flush, so the default requires no re-sort on the write path.</para>
/// <para><b>Columnar path only.</b> A sort spec is supplied to the columnar segment format's
/// constructor (<c>ColumnarSegmentFormat</c>'s <c>sortSpec</c> parameter, or the
/// <c>sortSpec</c> parameter of <c>ColumnarTimeSeriesEngineBuilder.BuildAsync</c>); the
/// row-oriented segment format has no sort spec.</para>
/// </remarks>
public sealed record ColumnSortSpec
{
    /// <summary>
    /// Ordered list of <c>(Name, Direction)</c> entries defining the sort key,
    /// most-significant first.
    /// </summary>
    public IReadOnlyList<(string Name, SortDirection Direction)> Columns { get; }

    /// <summary>
    /// Creates a sort spec from <paramref name="columns"/>. Each entry must carry a
    /// non-null, non-whitespace property name and a valid <see cref="SortDirection"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="columns"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="columns"/> is empty, or any entry's <c>Name</c> is <see langword="null"/>
    /// or whitespace.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Any entry's <c>Direction</c> is not a defined <see cref="SortDirection"/> value.
    /// </exception>
    public ColumnSortSpec(IReadOnlyList<(string Name, SortDirection Direction)> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        if (columns.Count == 0)
        {
            throw new ArgumentException("ColumnSortSpec requires at least one column entry.", nameof(columns));
        }

        for (var i = 0; i < columns.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(columns[i].Name))
            {
                throw new ArgumentException(
                    $"ColumnSortSpec column at index {i} has a null or whitespace Name.", nameof(columns));
            }

            // The on-disk physical row order and the generated/reflection comparer both branch on
            // this enum; an undefined value would silently encode an out-of-contract layout. Reject
            // it fail-fast at construction rather than let a forged/uninitialised enum flow into
            // segment writes. ArgumentOutOfRangeException (an ArgumentException subtype) is the
            // semantically correct type for an out-of-range enum.
            if (!Enum.IsDefined(columns[i].Direction))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(columns),
                    columns[i].Direction,
                    $"ColumnSortSpec column at index {i} ('{columns[i].Name}') has an undefined " +
                    $"SortDirection value ({(int)columns[i].Direction}).");
            }
        }

        // Defensive copy so post-construction mutations to the caller's list cannot alter
        // the spec's sort order, which is load-bearing for on-disk segment layout decisions.
        // Exposed through a read-only wrapper so the output edge is protected too: a bare array
        // could be cast back from IReadOnlyList and mutated — including the shared DateAscending.
        Columns = Array.AsReadOnly(columns.ToArray());
    }

    /// <summary>
    /// The default time-series sort spec: ascending <c>Date</c>. Matches the natural flush
    /// order imposed by <c>TimeSeriesKey</c> so no re-sort is needed on the write path for
    /// daily-bar workloads.
    /// </summary>
    public static ColumnSortSpec DateAscending { get; } = new([("Date", SortDirection.Ascending)]);

    /// <inheritdoc />
    public bool Equals(ColumnSortSpec? other) =>
        other is not null && Columns.SequenceEqual(other.Columns);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var (name, direction) in Columns)
        {
            hash.Add(name, StringComparer.Ordinal);
            hash.Add(direction);
        }
        return hash.ToHashCode();
    }
}
