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

namespace Boutquin.Storage.Domain.ValueObjects.TimeSeries;

/// <summary>
/// Construction-time configuration for a time-series storage engine
/// (<see cref="ITimeSeriesStorageEngine{TDatasetKey, TRecord}"/>). Selects the on-disk
/// segment format. Columnar row-group sizing and the per-block sort order are not options
/// members: they are fixed when the columnar format itself is constructed (the
/// <c>CompressedColumnarSegmentFormat</c> and <c>ColumnarSegmentFormat</c> constructors, or the
/// <c>ColumnarTimeSeriesEngineBuilder</c> parameters that forward to them), because that format
/// is what the engine writes with.
/// </summary>
/// <typeparam name="TRecord">
/// The dated record type. The base options constrain only <c>TRecord : IHasDate, new()</c> —
/// the format-agnostic minimum — so a <c>[StorageSerializable]</c>-only record (one that does
/// NOT implement the columnar rehydration contract) can configure a row store. The columnar
/// rehydration requirement lives on the columnar surfaces only, including the derived
/// <see cref="ColumnarTimeSeriesStoreOptions{TRecord}"/>.
/// </typeparam>
/// <remarks>
/// <para><b>Lives on the engine, not the store.</b> The row-vs-columnar decision is an
/// on-disk segment-format property. Segment flush is owned by the engine; therefore the
/// options that drive flush live on the engine. <c>LsmTimeSeriesStore</c> composes over
/// <see cref="ITimeSeriesStorageEngine{TDatasetKey, TRecord}"/> and remains
/// storage-engine-agnostic.</para>
/// <para><b>Row is default.</b> The row-oriented format is a mixed-workload default per
/// DDIA 2e p.138; flip to <see cref="TimeSeriesSegmentFormat.Columnar"/> for analytical
/// scans that project a subset of fields. The columnar format implementation is supplied via
/// <see cref="ColumnarTimeSeriesStoreOptions{TRecord}"/> (the columnar-constrained derived
/// options) — the base options carry no columnar-format member, so the row path type-checks for
/// records that do not implement the rehydration contract.</para>
/// <para><b>Why this lives under <c>ValueObjects.TimeSeries</c>, not <c>Interfaces.TimeSeries</c>:</b>
/// project architecture tests enforce that <c>Domain.Interfaces.*</c> namespaces contain only
/// interfaces. Configuration records — which are concrete types even when they are
/// immutable — live alongside the other concrete value objects.</para>
/// <para><b>Inheritance:</b> this base is a non-sealed <c>record</c> so the columnar-constrained
/// <see cref="ColumnarTimeSeriesStoreOptions{TRecord}"/> can derive from it and carry the
/// <c>ColumnarFormat</c> member at its own constraint boundary. Record value semantics
/// (structural equality, <c>with</c>-expressions) are preserved through the hierarchy.</para>
/// </remarks>
public record TimeSeriesStoreOptions<TRecord>
    where TRecord : IHasDate, new()
{
    /// <summary>
    /// Initializes a new instance with default options (row format). Safe for use with
    /// object-initializer syntax when every default is acceptable or when all properties are
    /// explicitly set via <see langword="init"/> setters.
    /// </summary>
    public TimeSeriesStoreOptions() { }

    /// <summary>
    /// The on-disk segment layout. Defaults to <see cref="TimeSeriesSegmentFormat.Row"/> —
    /// the DDIA 2e p.138 mixed-workload default. Selecting
    /// <see cref="TimeSeriesSegmentFormat.Columnar"/> additionally requires supplying the
    /// columnar format via <see cref="ColumnarTimeSeriesStoreOptions{TRecord}"/>, which fixes this
    /// property to <see cref="TimeSeriesSegmentFormat.Columnar"/>.
    /// </summary>
    public virtual TimeSeriesSegmentFormat SegmentFormat { get; init; } = TimeSeriesSegmentFormat.Row;

    /// <summary>
    /// Whether a columnar format implementation has been supplied. The base (row-safe) options
    /// never carry a columnar format, so this is always <see langword="false"/>;
    /// <see cref="ColumnarTimeSeriesStoreOptions{TRecord}"/> overrides it to report whether its
    /// <c>ColumnarFormat</c> member is set. The engine decorator uses this — rather than naming
    /// the columnar-constrained derived type, which it cannot, having dropped the rehydration
    /// constraint from <c>TRecord</c> — to fail fast when columnar is selected without a format.
    /// </summary>
    public virtual bool IsColumnarFormatSupplied => false;
}
