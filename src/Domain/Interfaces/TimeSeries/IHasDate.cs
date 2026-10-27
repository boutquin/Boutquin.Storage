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
/// Marker applied by a record type that carries a single sort-defining date.
/// </summary>
/// <remarks>
/// <para>Consumers apply this to their domain records (e.g. <c>Bar</c>, <c>ScalarObservation</c>,
/// <c>FxRate</c>) so that <see cref="ITimeSeriesStore{TDatasetKey,TRecord}"/> can key them by
/// <c>(dataset, Date)</c> without reflecting on arbitrary properties.</para>
/// <para>The date is expected to be the record's canonical time stamp — the one used for
/// deduplication, ordering, and range scans. A record with multiple candidate dates (e.g. a
/// trade with both event-time and ingestion-time) must choose one and document the choice at
/// the consumer level.</para>
/// </remarks>
public interface IHasDate
{
    /// <summary>
    /// Gets the sort-defining date for this record.
    /// </summary>
    DateOnly Date { get; }
}
