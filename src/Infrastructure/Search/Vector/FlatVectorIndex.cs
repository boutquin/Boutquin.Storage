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
using Boutquin.Storage.Domain.Interfaces.Search;

namespace Boutquin.Storage.Infrastructure.Search.Vector;

/// <summary>Provides exact nearest-neighbor search by scanning every stored vector.</summary>
public sealed class FlatVectorIndex<TId> : IVectorIndex<TId> where TId : notnull
{
    private readonly object _gate = new();
    private readonly IVectorDistanceMetric _metric;
    private readonly Dictionary<TId, VectorEntry<TId>> _entries = [];
    private long _nextSequence;

    /// <summary>Initializes an exact index.</summary>
    public FlatVectorIndex(int dimension, IVectorDistanceMetric metric)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimension);
        ArgumentNullException.ThrowIfNull(metric);
        Dimension = dimension;
        _metric = metric;
    }

    /// <inheritdoc />
    public int Dimension { get; }

    /// <inheritdoc />
    public void Upsert(TId id, ReadOnlySpan<float> vector)
    {
        var copy = VectorValidation.Copy(vector, Dimension, nameof(vector));
        lock (_gate)
        {
            var sequence = _entries.TryGetValue(id, out var existing) ? existing.Sequence : _nextSequence++;
            _entries[id] = new VectorEntry<TId>(id, copy, sequence);
        }
    }

    /// <inheritdoc />
    public bool Remove(TId id)
    {
        lock (_gate)
        {
            return _entries.Remove(id);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<(TId Id, float Distance)> Search(ReadOnlySpan<float> query, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        var copy = VectorValidation.Copy(query, Dimension, nameof(query));
        lock (_gate)
        {
            return VectorSearch.Rank(_entries.Values, copy, count, _metric);
        }
    }
}

internal sealed record VectorEntry<TId>(TId Id, float[] Vector, long Sequence) where TId : notnull;

internal static class VectorSearch
{
    internal static IReadOnlyList<(TId Id, float Distance)> Rank<TId>(
        IEnumerable<VectorEntry<TId>> entries,
        float[] query,
        int count,
        IVectorDistanceMetric metric) where TId : notnull =>
        entries.Select(entry => (Entry: entry, Distance: VectorRanking.Distance(metric, query, entry.Vector)))
            .OrderBy(static result => result.Distance)
            .ThenBy(static result => result.Entry.Sequence)
            .Take(count)
            .Select(static result => (result.Entry.Id, (float)result.Distance))
            .ToArray();
}
