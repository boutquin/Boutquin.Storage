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

/// <summary>Provides approximate search through a k-means coarse quantizer and inverted lists.</summary>
public sealed class IvfVectorIndex<TId> : IVectorIndex<TId> where TId : notnull
{
    private readonly object _gate = new();
    private readonly IVectorDistanceMetric _metric;
    private readonly int _partitionCount;
    private readonly int _probeCount;
    private readonly int _trainingIterations;
    private readonly Dictionary<TId, VectorEntry<TId>> _entries = [];
    private List<Partition> _partitions = [];
    private bool _dirty = true;
    private long _nextSequence;

    /// <summary>Initializes an IVF index.</summary>
    public IvfVectorIndex(int dimension, IVectorDistanceMetric metric, int partitionCount = 16, int probeCount = 2, int trainingIterations = 8)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimension);
        ArgumentNullException.ThrowIfNull(metric);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(partitionCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(probeCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(trainingIterations);
        if (probeCount > partitionCount)
        {
            throw new ArgumentOutOfRangeException(nameof(probeCount), "Probe count cannot exceed partition count.");
        }

        Dimension = dimension;
        _metric = metric;
        _partitionCount = partitionCount;
        _probeCount = probeCount;
        _trainingIterations = trainingIterations;
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
            _dirty = true;
        }
    }

    /// <inheritdoc />
    public bool Remove(TId id)
    {
        lock (_gate)
        {
            var removed = _entries.Remove(id);
            _dirty |= removed;
            return removed;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<(TId Id, float Distance)> Search(ReadOnlySpan<float> query, int count) =>
        Search(query, count, _probeCount);

    /// <summary>Searches with an explicit number of coarse partitions to probe.</summary>
    public IReadOnlyList<(TId Id, float Distance)> Search(ReadOnlySpan<float> query, int count, int probeCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(probeCount);
        var copy = VectorValidation.Copy(query, Dimension, nameof(query));
        lock (_gate)
        {
            RebuildIfDirty();
            var selected = _partitions.OrderBy(partition => VectorRanking.Distance(_metric, copy, partition.Centroid))
                .ThenBy(static partition => partition.Ordinal)
                .Take(Math.Min(probeCount, _partitions.Count));
            return VectorSearch.Rank(selected.SelectMany(static partition => partition.Entries), copy, count, _metric);
        }
    }

    private void RebuildIfDirty()
    {
        if (!_dirty)
        {
            return;
        }

        var entries = _entries.Values.OrderBy(static entry => entry.Sequence).ToArray();
        if (entries.Length == 0)
        {
            _partitions = [];
            _dirty = false;
            return;
        }

        var count = Math.Min(_partitionCount, entries.Length);
        var centroids = new List<float[]>(count) { entries[0].Vector.ToArray() };
        var selectedSequences = new HashSet<long> { entries[0].Sequence };
        while (centroids.Count < count)
        {
            var next = entries.Select(entry => (entry, distance: centroids.Min(centroid => VectorRanking.Distance(_metric, entry.Vector, centroid))))
                .OrderByDescending(static candidate => candidate.distance)
                .ThenBy(static candidate => candidate.entry.Sequence)
                .First(candidate => !selectedSequences.Contains(candidate.entry.Sequence));
            centroids.Add(next.entry.Vector.ToArray());
            selectedSequences.Add(next.entry.Sequence);
        }

        int[] assignments = new int[entries.Length];
        void Assign()
        {
            for (var index = 0; index < entries.Length; index++)
            {
                assignments[index] = Enumerable.Range(0, count)
                    .OrderBy(centroid => VectorRanking.Distance(_metric, entries[index].Vector, centroids[centroid]))
                    .ThenBy(static centroid => centroid)
                    .First();
            }
        }

        for (var iteration = 0; iteration < _trainingIterations; iteration++)
        {
            Assign();

            for (var centroid = 0; centroid < count; centroid++)
            {
                var members = Enumerable.Range(0, entries.Length).Where(index => assignments[index] == centroid).ToArray();
                if (members.Length == 0)
                {
                    continue;
                }

                // Sum in double and divide once: a float running sum of per-member quotients can
                // round past float.MaxValue, while the true mean never exceeds the largest member.
                var sum = new double[Dimension];
                foreach (var member in members)
                {
                    for (var dimension = 0; dimension < Dimension; dimension++)
                    {
                        sum[dimension] += entries[member].Vector[dimension];
                    }
                }

                var mean = new float[Dimension];
                for (var dimension = 0; dimension < Dimension; dimension++)
                {
                    mean[dimension] = (float)(sum[dimension] / members.Length);
                }

                centroids[centroid] = mean;
            }
        }

        // The last iteration moved the centroids after assigning; each list must hold exactly the
        // vectors whose nearest final centroid it is, the same rule Search uses to route a query.
        // A centroid that won no vector is not published: a probe routed to it would contribute
        // nothing, and with a non-L2 metric such centroids can be nearest to whole query regions.
        Assign();
        _partitions = Enumerable.Range(0, count)
            .Select(index => new Partition(index, centroids[index], entries.Where((_, entryIndex) => assignments[entryIndex] == index).ToArray()))
            .Where(static partition => partition.Entries.Count > 0)
            .ToList();
        _dirty = false;
    }

    private sealed record Partition(int Ordinal, float[] Centroid, IReadOnlyList<VectorEntry<TId>> Entries);
}
