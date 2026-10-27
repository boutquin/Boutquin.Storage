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

using Boutquin.Storage.Domain.Interfaces.TimeSeries;
using Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar;

namespace Boutquin.Storage.Infrastructure.Analytics;

internal static class ColumnBatchGuard
{
    internal static void Validate<TRecord>(ColumnBatch<TRecord> batch)
        where TRecord : IHasDate, new()
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentOutOfRangeException.ThrowIfNegative(batch.RowCount);
        foreach (var (name, values) in batch.Columns)
        {
            // IsSZArray also rejects a rank-1 array with a non-zero lower bound (T[*]): operators index
            // columns by zero-based row, which on such an array reads the previous row's value.
            if (string.IsNullOrWhiteSpace(name) || values is null || !values.GetType().IsSZArray || values.Length != batch.RowCount)
            {
                throw new ArgumentException("Every named column must be a one-dimensional array matching RowCount.", nameof(batch));
            }
        }
    }

    internal static TValue[] GetColumn<TRecord, TValue>(ColumnBatch<TRecord> batch, string columnName)
        where TRecord : IHasDate, new()
    {
        Validate(batch);
        ArgumentException.ThrowIfNullOrWhiteSpace(columnName);
        if (!batch.Columns.TryGetValue(columnName, out var values))
        {
            throw new ArgumentException($"Column '{columnName}' does not exist in the batch.", nameof(columnName));
        }
        // Exact type match: CLR array covariance lets int[] pass `is uint[]` (or an int-backed enum
        // array) and string[] pass `is object[]`, which would reinterpret the column silently.
        if (values.GetType() != typeof(TValue[]) || values is not TValue[] typed)
        {
            throw new ArgumentException(
                $"Column '{columnName}' has element type {values.GetType().GetElementType()?.FullName}; expected {typeof(TValue).FullName}.",
                nameof(columnName));
        }
        return typed;
    }
}
