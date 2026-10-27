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
namespace Boutquin.Storage.Domain.Interfaces.TimeSeries;

/// <summary>
/// Secondary index tracking the min/max date present per dataset, updated on every
/// <see cref="ITimeSeriesStore{TDatasetKey,TRecord}.AppendAsync"/> /
/// <see cref="ITimeSeriesStore{TDatasetKey,TRecord}.DeleteRangeAsync"/>.
/// </summary>
/// <remarks>
/// <para><b>Advisory, not authoritative:</b> a corrupted or missing manifest can always
/// be rebuilt from a full scan of the backing store via <see cref="RebuildAsync"/>.
/// Consumers must never treat this index as the source of truth for "what records exist" —
/// only for "what coverage is declared".</para>
/// </remarks>
/// <typeparam name="TDatasetKey">The dataset partition key type. Its encoding must fully deserialize
/// to a comparer-equal key and reproduce identical bytes. Operations reject lossy encodings with
/// <see cref="ArgumentException"/> so they cannot alias another dataset's coverage.</typeparam>
public interface IDatasetCoverageIndex<TDatasetKey>
    where TDatasetKey : IComparable<TDatasetKey>, ISerializable<TDatasetKey>, new()
{
    /// <summary>
    /// Returns the declared coverage for <paramref name="dataset"/>, or <c>null</c> if the
    /// dataset has never been registered (or was fully cleared).
    /// </summary>
    Task<DateRange?> GetAsync(TDatasetKey dataset, CancellationToken cancellationToken = default);

    /// <summary>
    /// Merges the supplied <c>[min, max]</c> interval into the declared coverage for
    /// <paramref name="dataset"/> under min/max rules — the stored range widens to enclose
    /// the new interval.
    /// </summary>
    Task RegisterAsync(
        TDatasetKey dataset,
        DateOnly min,
        DateOnly max,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the declared coverage for <paramref name="dataset"/> with the supplied range.
    /// Passing <c>null</c> clears the dataset entry.
    /// Used after a delete-range or rebuild to shrink or reset coverage.
    /// </summary>
    Task SetAsync(
        TDatasetKey dataset,
        DateRange? coverage,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Rebuilds the entire index from a full scan of <paramref name="scan"/>, replacing any
    /// prior state. Each tuple is a <c>(dataset, date)</c> observation; the index records the
    /// min/max date per dataset.
    /// </summary>
    Task RebuildAsync(
        IAsyncEnumerable<(TDatasetKey Dataset, DateOnly Date)> scan,
        CancellationToken cancellationToken = default);
}
