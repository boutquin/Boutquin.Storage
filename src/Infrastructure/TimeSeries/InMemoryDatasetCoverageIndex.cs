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
using Boutquin.Storage.Domain.Interfaces.TimeSeries;

namespace Boutquin.Storage.Infrastructure.TimeSeries;

/// <summary>
/// In-memory, ephemeral implementation of <see cref="IDatasetCoverageIndex{TDatasetKey}"/>.
/// </summary>
/// <remarks>
/// Backed by a <see cref="ConcurrentDictionary{TKey,TValue}"/>; every operation is O(1) expected.
/// Used as the coverage-index surface for <see cref="InMemoryTimeSeriesStore{TDatasetKey,TRecord}"/>
/// and as a component in tests that do not exercise durability.
/// </remarks>
/// <typeparam name="TDatasetKey">The dataset partition key type.</typeparam>
public sealed class InMemoryDatasetCoverageIndex<TDatasetKey>
    : IDatasetCoverageIndex<TDatasetKey>
    where TDatasetKey : IComparable<TDatasetKey>, ISerializable<TDatasetKey>, new()
{
    // Keyed by serialized-bytes identity, like FileDatasetCoverageIndex: the dataset-key contract
    // promises no value equality, and the store hands this index both caller-held and
    // deserialized instances of one dataset.
    private readonly ConcurrentDictionary<TDatasetKey, DateRange> _ranges =
        new(SerializedKeyEqualityComparer<TDatasetKey>.Instance);

    /// <inheritdoc />
    public Task<DateRange?> GetAsync(TDatasetKey dataset, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_ranges.TryGetValue(dataset, out var r) ? r : (DateRange?)null);
    }

    /// <inheritdoc />
    public Task RegisterAsync(
        TDatasetKey dataset,
        DateOnly min,
        DateOnly max,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Same contract as FileDatasetCoverageIndex: an inverted interval is refused, because the
        // store serves a cached range verbatim and would report a populated window as a gap.
        if (min > max)
        {
            throw new ArgumentException(
                $"Coverage interval is inverted: min {min:O} is after max {max:O}.", nameof(min));
        }

        _ranges.AddOrUpdate(
            dataset,
            _ => new DateRange(min, max),
            (_, existing) => new DateRange(
                existing.From < min ? existing.From : min,
                existing.To > max ? existing.To : max));

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SetAsync(TDatasetKey dataset, DateRange? coverage, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (coverage is { } range && range.From > range.To)
        {
            throw new ArgumentException(
                $"Coverage range is inverted: From {range.From:O} is after To {range.To:O}.", nameof(coverage));
        }

        if (coverage is null)
        {
            _ranges.TryRemove(dataset, out _);
        }
        else
        {
            _ranges[dataset] = coverage.Value;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task RebuildAsync(
        IAsyncEnumerable<(TDatasetKey Dataset, DateOnly Date)> scan,
        CancellationToken cancellationToken = default)
    {
        var rebuilt = new Dictionary<TDatasetKey, (DateOnly Min, DateOnly Max)>(SerializedKeyEqualityComparer<TDatasetKey>.Instance);

        await foreach (var (ds, date) in scan.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (rebuilt.TryGetValue(ds, out var cur))
            {
                rebuilt[ds] = (
                    date < cur.Min ? date : cur.Min,
                    date > cur.Max ? date : cur.Max);
            }
            else
            {
                rebuilt[ds] = (date, date);
            }
        }

        _ranges.Clear();
        foreach (var kvp in rebuilt)
        {
            _ranges[kvp.Key] = new DateRange(kvp.Value.Min, kvp.Value.Max);
        }
    }
}
