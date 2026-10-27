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
/// Describes which fields of <c>TRecord</c> a reader needs from a columnar segment.
/// Passed to <c>IColumnarSegmentFormat&lt;TRecord&gt;.ReadAsync</c> so the format can skip
/// unprojected column spans entirely — this is the read-amplification win that column stores
/// provide over row stores.
/// </summary>
/// <param name="FieldNames">
/// The names of record fields to read. Empty (see <see cref="All"/>) selects every field the
/// segment declares in its footer. Names are matched case-sensitively against the column names
/// emitted by the columnar source-generator pipeline.
/// </param>
/// <param name="AllowMissing">
/// When <see langword="false"/> (default, strict mode): a projected field not present in the
/// segment's footer raises an exception — the caller is asserting the column exists and its
/// absence is a programmer error. When <see langword="true"/>: missing fields are silently
/// filled with <c>default(TField)</c>. Opt in to <see langword="true"/> only when the caller
/// knowingly spans segment generations written before a field was added to <c>TRecord</c>.
/// </param>
/// <remarks>
/// <para><b>Why strict-by-default:</b> silent defaults across schema evolution mask bugs —
/// a rename or a typo in <paramref name="FieldNames"/> would produce all-zero output rather
/// than a fast failure. Schema-evolution tolerance is a real need for long-lived stores, but it
/// is an opt-in choice, not a default.</para>
/// <para><b>Interaction with <see cref="All"/>:</b> when <see cref="FieldNames"/> is empty
/// (<see cref="All"/>), the reader projects exactly the columns the segment declares in its
/// footer and <see cref="AllowMissing"/> is not consulted — there is no named column whose
/// absence could be signalled. <see cref="AllowMissing"/> applies only when
/// <see cref="FieldNames"/> is non-empty.</para>
/// </remarks>
public readonly record struct ColumnProjection(
    IReadOnlyList<string> FieldNames,
    bool AllowMissing = false)
{
    /// <summary>
    /// The all-columns projection. Reads every column the segment declares in its footer.
    /// Equivalent to constructing with an empty <see cref="FieldNames"/> list;
    /// <see cref="AllowMissing"/> is not evaluated for this case.
    /// </summary>
    public static ColumnProjection All { get; } = new([]);
}
