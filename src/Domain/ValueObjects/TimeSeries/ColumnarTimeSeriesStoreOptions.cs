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
using Boutquin.Storage.Domain.Interfaces.TimeSeries.Columnar;

namespace Boutquin.Storage.Domain.ValueObjects.TimeSeries;

/// <summary>
/// Columnar-constrained time-series engine options. Derives from
/// <see cref="TimeSeriesStoreOptions{TRecord}"/> and adds the strongly-typed
/// <see cref="ColumnarFormat"/> member. Because the <c>ColumnarFormat</c> property type names
/// <see cref="IColumnarSegmentFormat{TRecord}"/> — which itself constrains
/// <c>TRecord : IHasDate, IColumnarRehydratable&lt;TRecord&gt;, new()</c> — this options type
/// carries the columnar rehydration constraint at its own boundary, keeping the format-agnostic
/// base options (and the row store/engine that consume them) free of it.
/// </summary>
/// <typeparam name="TRecord">
/// The dated columnar record type. Must implement <see cref="IColumnarRehydratable{TRecord}"/>
/// (emitted by the <c>[ColumnarStorable]</c> source generator) so the columnar reader can
/// materialise records from decoded column arrays.
/// </typeparam>
/// <remarks>
/// <para><b>Why a separate type (Option A).</b> The columnar format reference is the only piece
/// of options state that requires the rehydration contract. Splitting it onto a columnar-only
/// derived options type lets a <c>[StorageSerializable]</c>-only record configure a row store
/// through the base <see cref="TimeSeriesStoreOptions{TRecord}"/> while the type system still
/// enforces "columnar options ⇒ rehydratable record" at compile time — a compile-time guarantee
/// the rejected <c>object?</c>-format alternative could not provide.</para>
/// <para><b>Construction validation.</b> The <c>SegmentFormat</c> on a columnar options instance
/// is forced to <see cref="TimeSeriesSegmentFormat.Columnar"/> (a different value set through an
/// object initializer or a <see langword="with"/> expression is refused), and supplying a
/// <see langword="null"/> <see cref="ColumnarFormat"/> is rejected at construction — the columnar
/// path has no meaning without a format.</para>
/// </remarks>
public sealed record ColumnarTimeSeriesStoreOptions<TRecord>
    : TimeSeriesStoreOptions<TRecord>
    where TRecord : IHasDate, IColumnarRehydratable<TRecord>, new()
{
    /// <summary>
    /// Initializes columnar options with <see cref="TimeSeriesStoreOptions{TRecord}.SegmentFormat"/>
    /// fixed to <see cref="TimeSeriesSegmentFormat.Columnar"/>. <see cref="ColumnarFormat"/> must be
    /// set via its <see langword="init"/> setter (object-initializer syntax) — see the validating
    /// constructor overload for the fail-fast path.
    /// </summary>
    public ColumnarTimeSeriesStoreOptions()
    {
        SegmentFormat = TimeSeriesSegmentFormat.Columnar;
    }

    /// <summary>
    /// Constructs columnar options with the supplied <paramref name="columnarFormat"/>, validated
    /// non-<see langword="null"/> at construction time.
    /// </summary>
    /// <param name="columnarFormat">The per-record columnar format implementation.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="columnarFormat"/> is <see langword="null"/>.
    /// </exception>
    public ColumnarTimeSeriesStoreOptions(IColumnarSegmentFormat<TRecord> columnarFormat)
    {
        ArgumentNullException.ThrowIfNull(columnarFormat);
        SegmentFormat = TimeSeriesSegmentFormat.Columnar;
        ColumnarFormat = columnarFormat;
    }

    /// <summary>
    /// The on-disk segment layout, always <see cref="TimeSeriesSegmentFormat.Columnar"/> on columnar
    /// options. These options carry a columnar format, so a <see cref="TimeSeriesSegmentFormat.Row"/>
    /// label would route the engine down the row path and leave that format unconsulted; setting any
    /// other value through an object initializer or a <see langword="with"/> expression is refused.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown when the value set is not <see cref="TimeSeriesSegmentFormat.Columnar"/>.
    /// </exception>
    public override TimeSeriesSegmentFormat SegmentFormat
    {
        get => base.SegmentFormat;
        init
        {
            if (value != TimeSeriesSegmentFormat.Columnar)
            {
                throw new ArgumentException(
                    $"ColumnarTimeSeriesStoreOptions always describe a columnar store; SegmentFormat cannot be {value}. "
                    + "Use the base TimeSeriesStoreOptions<TRecord> to configure a row store.",
                    nameof(value));
            }

            base.SegmentFormat = value;
        }
    }

    /// <summary>
    /// The columnar format implementation. Typically emitted by the source generator for a
    /// <c>[ColumnarStorable]</c>-annotated record type. The on-disk columnar writing is performed
    /// by the inner <c>LsmStorageEngine</c>'s <c>ColumnarSegmentFormat</c>, which must wrap this same
    /// <see cref="IColumnarSegmentFormat{TRecord}"/> instance; the time-series engine verifies that
    /// at construction when its inner engine is an <c>LsmStorageEngine</c>.
    /// </summary>
    public IColumnarSegmentFormat<TRecord>? ColumnarFormat { get; init; }

    /// <inheritdoc />
    public override bool IsColumnarFormatSupplied => ColumnarFormat is not null;
}
