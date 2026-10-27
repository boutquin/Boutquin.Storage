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
using System.Collections;

namespace Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar;

/// <summary>
/// A single-column value predicate (<c>column OP value</c>) used to push filtering down into a
/// columnar segment read. A conjunction (logical AND) of predicates lets the reader (a) skip whole
/// row groups whose zone-map <c>[min, max]</c> span is provably disjoint from the predicate, and
/// (b) consult a bitmap index for equality on an indexed column — the two data-skipping mechanisms
/// of DDIA 2e Ch. 4. The predicate is also evaluated row-by-row on the decoded column so the result
/// set is exact regardless of skip granularity.
/// </summary>
/// <remarks>
/// <para><b>Type contract:</b> <see cref="Value"/> must be the same CLR type as the target column's
/// cells (e.g. an <see cref="int"/> value for an <c>int</c> column). Comparison uses
/// <see cref="Comparer.Default"/>, which requires the compared values implement
/// <see cref="IComparable"/> and share a type; a mismatched value type throws at evaluation time.
/// <c>string</c> cells are the one exception: they compare via
/// <see cref="string.CompareOrdinal(string, string)"/> (ordinal byte-lex), matching the column's
/// zone-map and sort order, never the culture-sensitive default comparer.
/// Supported column types mirror the zone-map set: <c>int</c>, <c>long</c>, <c>float</c>,
/// <c>double</c>, <c>decimal</c>, <c>bool</c>, <see cref="System.DateOnly"/>, <c>string</c>,
/// and any CLR enum (the predicate value is the enum itself; ulong-underlying enums are
/// range-filtered row-by-row rather than zone-map-skipped).</para>
/// <para><b>Null contract:</b> <see cref="ColumnName"/> and <see cref="Value"/> are required; a
/// <see langword="null"/> for either throws <see cref="ArgumentNullException"/> at construction and
/// through a <c>with</c> expression. A read consults zone maps and bitmap indexes before any row, and
/// neither has a reading for a null comparison value. <see cref="Operator"/> must be a defined
/// <see cref="ComparisonOperator"/>; an undefined value throws <see cref="ArgumentOutOfRangeException"/>
/// at construction and through a <c>with</c> expression.</para>
/// </remarks>
/// <param name="ColumnName">Name of the target column. Matched case-sensitively against the
/// columnar field descriptor's name.</param>
/// <param name="Operator">The comparison applied as <c>cell OP <see cref="Value"/></c>.</param>
/// <param name="Value">The right-hand comparison value. Must match the column's CLR type.</param>
public sealed record ColumnPredicate(string ColumnName, ComparisonOperator Operator, object Value)
{
    // Each required member is validated twice by the same check: in the initializer (the primary
    // constructor path) and in the init accessor (the path a `with` expression takes, which never
    // runs the initializer).

    /// <summary>Name of the target column, matched case-sensitively. Never <see langword="null"/>.</summary>
    public string ColumnName
    {
        get;
        init => field = value ?? throw new ArgumentNullException(nameof(ColumnName));
    } = ColumnName ?? throw new ArgumentNullException(nameof(ColumnName));

    /// <summary>
    /// The comparison applied as <c>cell OP <see cref="Value"/></c>. Always a defined
    /// <see cref="ComparisonOperator"/>: an undefined value would match no row and skip no row
    /// group, so a read would return an empty result instead of failing.
    /// </summary>
    public ComparisonOperator Operator
    {
        get;
        init => field = RequireDefined(value, nameof(Operator));
    } = RequireDefined(Operator, nameof(Operator));

    /// <summary>The right-hand comparison value, of the column's CLR type. Never <see langword="null"/>.</summary>
    public object Value
    {
        get;
        init => field = value ?? throw new ArgumentNullException(nameof(Value));
    } = Value ?? throw new ArgumentNullException(nameof(Value));

    /// <summary>
    /// Evaluates the predicate against a single decoded column cell. A null cell differs from every
    /// (non-null) <see cref="Value"/>, so it satisfies only <see cref="ComparisonOperator.NotEqual"/>;
    /// every other operator against a null cell is false.
    /// </summary>
    public bool Matches(object? cell)
    {
        if (cell is null)
        {
            return Operator == ComparisonOperator.NotEqual;
        }

        // NaN is unordered: it matches no predicate, on either side. This keeps the row filter
        // consistent with the zone map, which excludes NaN from its [min,max] bounds — a finite
        // group can be proved disjoint and skipped, so a row filter that (via .NET's total order,
        // where NaN sorts below everything and NaN == NaN) reported a NaN cell as a `<`/`<=`/`!=`
        // match would disagree with the skip decision, producing phantom rows or dropping the NaN
        // row outright. A NaN query bound is likewise unsatisfiable on every operator.
        if (IsNaN(cell) || IsNaN(Value))
        {
            return false;
        }

        // Strings compare ordinally, not via Comparer.Default (which is culture-sensitive). The
        // column's order is ordinal byte-lex — ZoneMapComputer and the generated columnar sort
        // comparers both use string.CompareOrdinal — so the row-level filter must agree byte-for-byte
        // or a zone-map skip can disagree with the row filter under a non-invariant locale.
        var cmp = cell is string cs && Value is string vs
            ? string.CompareOrdinal(cs, vs)
            : Comparer.Default.Compare(cell, Value);
        return Operator switch
        {
            ComparisonOperator.Equal => cmp == 0,
            ComparisonOperator.NotEqual => cmp != 0,
            ComparisonOperator.LessThan => cmp < 0,
            ComparisonOperator.LessThanOrEqual => cmp <= 0,
            ComparisonOperator.GreaterThan => cmp > 0,
            ComparisonOperator.GreaterThanOrEqual => cmp >= 0,
            _ => false,
        };
    }

    private static ComparisonOperator RequireDefined(ComparisonOperator value, string paramName)
        => Enum.IsDefined(value)
            ? value
            : throw new ArgumentOutOfRangeException(
                paramName,
                value,
                $"ColumnPredicate operator {(byte)value} is not a defined ComparisonOperator value.");

    private static bool IsNaN(object? value) => value switch
    {
        double d => double.IsNaN(d),
        float f => float.IsNaN(f),
        _ => false,
    };
}
