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
/// Cross-row-group bitmap index for a single low-cardinality column (Phase E2). Maps each
/// distinct observed value to a <c>BitmapRleCodec</c>-encoded presence bitmap
/// spanning every row in the segment. Lets a point-lookup predicate
/// (<c>WHERE col = X</c>) resolve to one footer-scan + one bitmap test, skipping every row
/// group whose bitmap returns false.
/// </summary>
/// <remarks>
/// <para><b>Theory:</b> DDIA 2e Ch. 4 (Figs 4-8, 4-9) — bitmap encoding plus
/// cross-column bitmap-AND is the primary mechanism for point and conjunctive predicates on
/// low-cardinality analytical columns. This type extends DDIA's per-row-group construction
/// with a cross-row-group layer so point lookups whose target values span multiple row groups
/// need only consult the footer and one RLE bitmap per distinct value, not every row group.</para>
/// <para><b>Encoding of <see cref="ValueToBitmap"/>:</b> keys are canonical on-disk bytes of
/// the distinct value — the same primitive layout as <c>ColumnRowGroupStats.MinValue</c>, with
/// two equality-specific canonicalisations the zone-map bounds do not apply: decimal keys are
/// scale-normalized (100m and 100.00m share one bucket) and double/float NaN payloads collapse
/// to one canonical-NaN bucket, because bucket lookup is byte-equality while zone-map bounds
/// compare by value. Values are the full-segment bitmap encoded via
/// <c>BitmapRleCodec</c>. A bitmap of length N
/// (where N = total row count across all row groups) sets bit <c>i</c> to <see langword="true"/>
/// iff the column's row-<c>i</c> value equals the key.</para>
/// <para><b>Greenfield-compatibility note:</b> the bitmap-index footer section is additive —
/// segments written before Phase E simply omit it, and readers treat its absence as "no
/// bitmap index available" and fall through to full column decode. No format-version bump
/// is taken — under the greenfield premise (no persisted pre-2.0.0 columnar segments), v1
/// evolves additively.</para>
/// </remarks>
public sealed record BitmapIndexEntry
{
    /// <summary>Creates a new bitmap-index entry.</summary>
    /// <param name="ColumnName">
    /// Name of the column this bitmap index describes. Matched case-sensitively against
    /// <see cref="ColumnarFieldDescriptor.Name"/>.
    /// </param>
    /// <param name="ValueToBitmap">
    /// Per-distinct-value bitmap blobs. Key: canonical on-disk bytes of the value. Value:
    /// <c>BitmapRleCodec</c>-encoded bitmap spanning the whole segment.
    /// </param>
    public BitmapIndexEntry(string ColumnName, IReadOnlyList<BitmapIndexValue> ValueToBitmap)
    {
        ArgumentNullException.ThrowIfNull(ColumnName);
        ArgumentNullException.ThrowIfNull(ValueToBitmap);
        this.ColumnName = ColumnName;

        // Equality and hash iterate this list, so snapshot it on the way in (a caller's later
        // mutation must not change this record's identity) and expose a read-only wrapper on the
        // way out (a consumer must not rewrite it through a cast of the getter) — as the sibling
        // BitmapIndexValue and ColumnSortSpec do.
        this.ValueToBitmap = Array.AsReadOnly(ValueToBitmap.ToArray());
    }

    /// <summary>Name of the column this bitmap index describes.</summary>
    public string ColumnName { get; }

    /// <summary>Per-distinct-value bitmap blobs.</summary>
    public IReadOnlyList<BitmapIndexValue> ValueToBitmap { get; }

    /// <inheritdoc />
    public bool Equals(BitmapIndexEntry? other) =>
        other is not null
        && string.Equals(ColumnName, other.ColumnName, StringComparison.Ordinal)
        && ValueToBitmap.Count == other.ValueToBitmap.Count
        && ValueToBitmap.SequenceEqual(other.ValueToBitmap);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(ColumnName, StringComparer.Ordinal);
        foreach (var entry in ValueToBitmap)
        {
            hash.Add(entry);
        }
        return hash.ToHashCode();
    }
}
