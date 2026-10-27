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

/// <summary>Reduces one typed column without converting its values through floating point.</summary>
/// <typeparam name="TRecord">The dated record represented by the batch metadata.</typeparam>
/// <typeparam name="TValue">The exact runtime element type of the aggregate column.</typeparam>
/// <typeparam name="TAccumulator">The accumulator and result type.</typeparam>
/// <remarks>Runs in O(rows) time and O(1) additional space. Empty batches return the seed.
/// Nullable elements are passed unchanged to the reducer.</remarks>
public sealed class AggregateOperator<TRecord, TValue, TAccumulator>
    : IVectorizedOperator<ColumnBatch<TRecord>, TAccumulator>
    where TRecord : IHasDate, new()
{
    private readonly string _columnName;
    private readonly TAccumulator _seed;
    private readonly Func<TAccumulator, TValue, TAccumulator> _reducer;

    /// <summary>Creates a typed batch aggregate.</summary>
    /// <param name="columnName">Case-sensitive aggregate-column name.</param>
    /// <param name="seed">Initial accumulator and empty-batch result.</param>
    /// <param name="reducer">Deterministic left fold applied in persisted row order.</param>
    /// <exception cref="ArgumentException"><paramref name="columnName"/> is blank.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="reducer"/> is null.</exception>
    public AggregateOperator(string columnName, TAccumulator seed, Func<TAccumulator, TValue, TAccumulator> reducer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(columnName);
        ArgumentNullException.ThrowIfNull(reducer);
        _columnName = columnName;
        _seed = seed;
        _reducer = reducer;
    }

    /// <inheritdoc />
    public TAccumulator Execute(ColumnBatch<TRecord> input)
    {
        var values = ColumnBatchGuard.GetColumn<TRecord, TValue>(input, _columnName);
        var result = _seed;
        foreach (var value in values)
        {
            result = _reducer(result, value);
        }
        return result;
    }
}
