// Copyright (c) 2024-2026 Pierre G. Boutquin. All rights reserved.
//
//   Licensed under the Apache License, Version 2.0 (the "License").
//   You may not use this file except in compliance with the License.
//   You may obtain a copy of the License at
//
//       http://www.apache.org/licenses/LICENSE-2.0
//
//   Unless required by applicable law or agreed to in writing, software
//   distributed under the License is distributed on an "AS IS" BASIS,
//   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//
//   See the License for the specific language governing permissions and
//   limitations under the License.
//

namespace Boutquin.Storage.Infrastructure.Batch;

/// <summary>A small typed in-memory frame for batch projection, grouping and equijoins.</summary>
/// <typeparam name="TRow">Row type; immutable rows are recommended.</typeparam>
/// <remarks>The constructor copies the row sequence, not mutable objects. Every operation
/// materializes its result, so frames are for bounded partitions/results, not an out-of-core
/// substitute for ExternalMergeSorter. GroupBy retains all group rows and joins may multiply
/// duplicate keys. Caller-supplied selectors define null/key/value semantics.</remarks>
public sealed class MiniDataFrame<TRow>
{
    /// <summary>Copies a finite row sequence into a read-only frame.</summary>
    /// <param name="rows">Rows to materialize.</param>
    public MiniDataFrame(IEnumerable<TRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        Rows = Array.AsReadOnly(rows.ToArray());
    }
    /// <summary>Gets the materialized row sequence.</summary>
    public IReadOnlyList<TRow> Rows { get; }
    /// <summary>Filters rows into a new frame.</summary>
    /// <param name="predicate">Row predicate.</param>
    /// <returns>Matching rows in input order.</returns>
    public MiniDataFrame<TRow> Where(Func<TRow, bool> predicate) => new(Rows.Where(predicate));
    /// <summary>Projects rows into a new typed shape.</summary>
    /// <typeparam name="TOut">Projected row type.</typeparam>
    /// <param name="projection">Row projection.</param>
    /// <returns>The projected frame.</returns>
    public MiniDataFrame<TOut> Select<TOut>(Func<TRow, TOut> projection) => new(Rows.Select(projection));
    /// <summary>Reduces each equality-keyed group into one output row.</summary>
    /// <typeparam name="TKey">Grouping key type.</typeparam>
    /// <typeparam name="TOut">Aggregate row type.</typeparam>
    /// <param name="key">Equality grouping key.</param>
    /// <param name="aggregate">Group reduction.</param>
    /// <returns>One aggregate per group, in first-key occurrence order.</returns>
    public MiniDataFrame<TOut> GroupBy<TKey, TOut>(Func<TRow, TKey> key, Func<TKey, IEnumerable<TRow>, TOut> aggregate)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        return new(Rows.GroupBy(key).Select(group => aggregate(group.Key, group)));
    }
    /// <summary>Performs an inner equijoin, retaining the Cartesian matches for duplicate keys.</summary>
    /// <typeparam name="TRight">Right row type.</typeparam>
    /// <typeparam name="TKey">Shared equality key type.</typeparam>
    /// <typeparam name="TOut">Joined output type.</typeparam>
    /// <param name="right">Right frame.</param>
    /// <param name="leftKey">Left key selector.</param>
    /// <param name="rightKey">Right key selector.</param>
    /// <param name="project">Matched-row projection.</param>
    /// <returns>The materialized join.</returns>
    public MiniDataFrame<TOut> Join<TRight, TKey, TOut>(MiniDataFrame<TRight> right,
        Func<TRow, TKey> leftKey, Func<TRight, TKey> rightKey, Func<TRow, TRight, TOut> project)
    {
        ArgumentNullException.ThrowIfNull(right);
        return new(Rows.Join(right.Rows, leftKey, rightKey, project));
    }
}
