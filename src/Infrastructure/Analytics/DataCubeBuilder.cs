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

using System.Collections.ObjectModel;
using Boutquin.Storage.Domain.Interfaces.Analytics;
using Boutquin.Storage.Domain.ValueObjects.Analytics;

namespace Boutquin.Storage.Infrastructure.Analytics;

/// <summary>Builds a compact in-memory cube across all configured grouping sets.</summary>
/// <typeparam name="TRecord">The input fact-row type.</typeparam>
/// <typeparam name="TMeasure">The exact measure and accumulator type.</typeparam>
/// <remarks>For D dimensions and N rows, construction is O(N × 2^D) time and O(cells) space.
/// Omitted dimensions denote ALL; present null values remain distinct from ALL.</remarks>
public sealed class DataCubeBuilder<TRecord, TMeasure> : IDataCubeBuilder<TRecord, TMeasure>
{
    private const int MaximumDimensions = 20;
    private readonly CubeDimension<TRecord>[] _dimensions;
    private readonly Func<TRecord, TMeasure> _measureSelector;
    private readonly TMeasure _seed;
    private readonly Func<TMeasure, TMeasure, TMeasure> _aggregate;

    /// <summary>Creates a cube builder.</summary>
    /// <param name="dimensions">Unique named dimension selectors, borrowed for construction and then copied.</param>
    /// <param name="measureSelector">Deterministic selector for one row's measure.</param>
    /// <param name="seed">Identity value and empty grand-total value.</param>
    /// <param name="aggregate">Deterministic measure combiner.</param>
    /// <exception cref="ArgumentNullException">A required collaborator is null.</exception>
    /// <exception cref="ArgumentException">A dimension is blank, duplicated, or lacks a selector.</exception>
    /// <exception cref="ArgumentOutOfRangeException">More than 20 dimensions are supplied.</exception>
    public DataCubeBuilder(IEnumerable<CubeDimension<TRecord>> dimensions,
        Func<TRecord, TMeasure> measureSelector, TMeasure seed,
        Func<TMeasure, TMeasure, TMeasure> aggregate)
    {
        ArgumentNullException.ThrowIfNull(dimensions);
        ArgumentNullException.ThrowIfNull(measureSelector);
        ArgumentNullException.ThrowIfNull(aggregate);
        _dimensions = dimensions.ToArray();
        if (_dimensions.Length > MaximumDimensions)
        {
            throw new ArgumentOutOfRangeException(nameof(dimensions),
                $"At most {MaximumDimensions} dimensions are supported because a cube has 2^N grouping sets.");
        }
        if (_dimensions.Any(d => d is null || string.IsNullOrWhiteSpace(d.Name) || d.Selector is null)
            || _dimensions.Select(d => d.Name).Distinct(StringComparer.Ordinal).Count() != _dimensions.Length)
        {
            throw new ArgumentException("Cube dimensions must have unique nonblank names and selectors.", nameof(dimensions));
        }
        _measureSelector = measureSelector;
        _seed = seed;
        _aggregate = aggregate;
    }

    /// <inheritdoc />
    public IReadOnlyList<DataCubeCell<TMeasure>> Build(IEnumerable<TRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var cells = new Dictionary<CubeKey, Accumulator>(CubeKeyComparer.Instance)
        {
            [new CubeKey(0, [])] = new(_seed, 0),
        };

        foreach (var record in records)
        {
            var values = _dimensions.Select(d => d.Selector(record)).ToArray();
            var measure = _measureSelector(record);
            var groupingSetCount = 1 << _dimensions.Length;
            for (var mask = 0; mask < groupingSetCount; mask++)
            {
                var key = new CubeKey(mask, values);
                if (!cells.TryGetValue(key, out var accumulator))
                {
                    accumulator = new Accumulator(_seed, 0);
                }
                cells[key] = new Accumulator(_aggregate(accumulator.Value, measure), accumulator.RowCount + 1);
            }
        }

        return cells.Select(pair =>
        {
            var dimensions = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var i = 0; i < _dimensions.Length; i++)
            {
                if ((pair.Key.Mask & (1 << i)) != 0)
                {
                    dimensions.Add(_dimensions[i].Name, pair.Key.Values[i]);
                }
            }
            return new DataCubeCell<TMeasure>(new ReadOnlyDictionary<string, object?>(dimensions),
                pair.Value.Value, pair.Value.RowCount);
        }).ToArray();
    }

    private sealed record CubeKey(int Mask, object?[] Values);
    private readonly record struct Accumulator(TMeasure Value, long RowCount);

    private sealed class CubeKeyComparer : IEqualityComparer<CubeKey>
    {
        internal static CubeKeyComparer Instance { get; } = new();

        public bool Equals(CubeKey? x, CubeKey? y)
        {
            if (ReferenceEquals(x, y))
            {
                return true;
            }
            if (x is null || y is null || x.Mask != y.Mask)
            {
                return false;
            }
            for (var i = 0; i < x.Values.Length; i++)
            {
                if ((x.Mask & (1 << i)) != 0 && !Equals(x.Values[i], y.Values[i]))
                {
                    return false;
                }
            }
            return true;
        }

        public int GetHashCode(CubeKey obj)
        {
            var hash = new HashCode();
            hash.Add(obj.Mask);
            for (var i = 0; i < obj.Values.Length; i++)
            {
                if ((obj.Mask & (1 << i)) != 0)
                {
                    hash.Add(obj.Values[i]);
                }
            }
            return hash.ToHashCode();
        }
    }
}
