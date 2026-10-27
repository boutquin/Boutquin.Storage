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
using Boutquin.Storage.Domain.ValueObjects.TimeSeries;
using Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar;

// TimeSeriesStoreOptions lives in Domain.ValueObjects.TimeSeries (per Domain.Interfaces
// architecture rule — that namespace holds interfaces only).
namespace Boutquin.Storage.Domain.Interfaces.TimeSeries;

/// <summary>
/// A time-series-specialised LSM storage engine. Narrows the generic LSM contract to
/// dated records keyed by <see cref="TimeSeriesKey{TDatasetKey}"/> and exposes the
/// <see cref="TimeSeriesStoreOptions{TRecord}"/> that drive on-disk segment format selection.
/// </summary>
/// <typeparam name="TDatasetKey">
/// The per-entity partition key type (e.g. a <c>provider:granularity:ticker</c> record struct).
/// </typeparam>
/// <typeparam name="TRecord">The dated record type.</typeparam>
/// <remarks>
/// <para><b>Why a specialised interface:</b> the generic <see cref="ILsmStorageEngine{TKey,
/// TValue}"/> has no <see cref="IHasDate"/> constraint and therefore cannot host the columnar
/// format, whose <c>IColumnarSegmentFormat&lt;TRecord&gt;</c> requires
/// <c>TRecord : IHasDate, new()</c>. This interface is the minimum additional type-system
/// surface needed to carry row-vs-columnar options next to the flush path they control,
/// without loosening or widening the generic engine contract.</para>
/// <para><b>No new methods.</b> The interface extends the generic LSM contract but adds only
/// the <see cref="Options"/> property. All storage-engine operations (Set/Get/Remove/Bulk/
/// Flush/Compact/GetRange) are inherited unchanged — consumers that do not need to inspect
/// the options treat the engine as an ordinary
/// <c>ILsmStorageEngine&lt;TimeSeriesKey&lt;TDatasetKey&gt;, TRecord&gt;</c>.</para>
/// </remarks>
public interface ITimeSeriesStorageEngine<TDatasetKey, TRecord>
    : ILsmStorageEngine<TimeSeriesKey<TDatasetKey>, TRecord>
    where TDatasetKey : ISerializable<TDatasetKey>, IComparable<TDatasetKey>, new()
    where TRecord : ISerializable<TRecord>, IHasDate, new()
{
    /// <summary>
    /// Gets the segment-format configuration this engine was constructed with. The value is
    /// fixed for the engine's lifetime — format is selected once at construction and all
    /// segments the engine writes share the selected format.
    /// </summary>
    TimeSeriesStoreOptions<TRecord> Options { get; }

    /// <summary>
    /// Range scan with column projection. Threads <paramref name="projection"/> through the
    /// on-disk segment reader chain; columnar segments skip unprojected column byte ranges at
    /// read time and decode only the projected columns. Row-format segments materialise the
    /// full record and ignore the projection.
    /// </summary>
    /// <param name="startKey">Inclusive lower bound of the key range.</param>
    /// <param name="endKey">Inclusive upper bound of the key range.</param>
    /// <param name="projection">Which fields to materialise. <see cref="ColumnProjection.All"/> selects every field.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <remarks>
    /// <para><b>Why not on <c>ILsmStorageEngine</c>:</b> projection is a columnar-only concept.
    /// Putting it on the generic engine would leak time-series-specific typing onto every
    /// non-time-series consumer. This surface is the first point in the engine hierarchy where
    /// the <see cref="IHasDate"/> constraint exists, so columnar format dispatch can live here
    /// without bleeding into the generic path.</para>
    /// </remarks>
    IAsyncEnumerable<(TimeSeriesKey<TDatasetKey> Key, TRecord Value)> GetRangeAsync(
        TimeSeriesKey<TDatasetKey> startKey,
        TimeSeriesKey<TDatasetKey> endKey,
        ColumnProjection projection,
        CancellationToken cancellationToken = default);
}
