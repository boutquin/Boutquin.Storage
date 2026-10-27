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

using Boutquin.Storage.Domain.Interfaces.Analytics;
using Boutquin.Storage.Domain.Interfaces.TimeSeries;
using Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar;

namespace Boutquin.Storage.Infrastructure.Analytics;

/// <summary>Selects and defensively copies named columns from a typed batch.</summary>
/// <typeparam name="TRecord">The dated record represented by the batch metadata.</typeparam>
/// <remarks>Projection is O(selected columns × rows) and preserves the requested column order.
/// Copies prevent callers from mutating arrays owned by the source batch.</remarks>
public sealed class ProjectionOperator<TRecord>
    : IVectorizedOperator<ColumnBatch<TRecord>, ColumnBatch<TRecord>>
    where TRecord : IHasDate, new()
{
    private readonly string[] _columns;

    /// <summary>Creates a projection in the requested column order.</summary>
    /// <param name="columns">Case-sensitive unique column names. An empty sequence selects no columns.</param>
    /// <exception cref="ArgumentNullException"><paramref name="columns"/> is null.</exception>
    /// <exception cref="ArgumentException">A name is blank or duplicated.</exception>
    public ProjectionOperator(IEnumerable<string> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        _columns = columns.ToArray();
        if (_columns.Any(string.IsNullOrWhiteSpace) || _columns.Distinct(StringComparer.Ordinal).Count() != _columns.Length)
        {
            throw new ArgumentException("Projection columns must be nonblank and unique.", nameof(columns));
        }
    }

    /// <inheritdoc />
    public ColumnBatch<TRecord> Execute(ColumnBatch<TRecord> input)
    {
        ColumnBatchGuard.Validate(input);
        var projected = new Dictionary<string, Array>(_columns.Length, StringComparer.Ordinal);
        foreach (var name in _columns)
        {
            if (!input.Columns.TryGetValue(name, out var values))
            {
                throw new ArgumentException($"Column '{name}' does not exist in the batch.", nameof(input));
            }
            projected.Add(name, (Array)values.Clone());
        }
        return new ColumnBatch<TRecord>(input.RowCount, projected, input.MinDate, input.MaxDate);
    }
}
