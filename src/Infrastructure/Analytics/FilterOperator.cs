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

/// <summary>Filters all columns in a batch with a predicate over one typed column.</summary>
/// <typeparam name="TRecord">The dated record represented by the batch metadata.</typeparam>
/// <typeparam name="TValue">The exact runtime element type of the predicate column.</typeparam>
/// <remarks>Runs in O(rows + selected rows × columns) time and allocates a new typed array for
/// every input column. The input arrays are never mutated.</remarks>
public sealed class FilterOperator<TRecord, TValue>
    : IVectorizedOperator<ColumnBatch<TRecord>, ColumnBatch<TRecord>>
    where TRecord : IHasDate, new()
{
    private readonly string _columnName;
    private readonly Func<TValue, bool> _predicate;

    /// <summary>Creates a batch filter.</summary>
    /// <param name="columnName">Case-sensitive name of the predicate column.</param>
    /// <param name="predicate">Deterministic predicate invoked once per column value, including null values.</param>
    /// <exception cref="ArgumentException"><paramref name="columnName"/> is blank.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="predicate"/> is null.</exception>
    public FilterOperator(string columnName, Func<TValue, bool> predicate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(columnName);
        ArgumentNullException.ThrowIfNull(predicate);
        _columnName = columnName;
        _predicate = predicate;
    }

    /// <inheritdoc />
    public ColumnBatch<TRecord> Execute(ColumnBatch<TRecord> input)
    {
        var predicateColumn = ColumnBatchGuard.GetColumn<TRecord, TValue>(input, _columnName);
        var selected = new List<int>(input.RowCount);
        for (var row = 0; row < predicateColumn.Length; row++)
        {
            if (_predicate(predicateColumn[row]))
            {
                selected.Add(row);
            }
        }

        var columns = new Dictionary<string, Array>(input.Columns.Count, StringComparer.Ordinal);
        foreach (var (name, source) in input.Columns)
        {
            var elementType = source.GetType().GetElementType()
                ?? throw new ArgumentException("Column arrays must expose an element type.", nameof(input));
            var destination = Array.CreateInstance(elementType, selected.Count);
            for (var row = 0; row < selected.Count; row++)
            {
                destination.SetValue(source.GetValue(selected[row]), row);
            }
            columns.Add(name, destination);
        }

        var minDate = input.MinDate;
        var maxDate = input.MaxDate;
        if (selected.Count > 0 && columns.TryGetValue("Date", out var dateValues) && dateValues is DateOnly[] dates)
        {
            minDate = dates.Min();
            maxDate = dates.Max();
        }
        return new ColumnBatch<TRecord>(selected.Count, columns, minDate, maxDate);
    }
}
