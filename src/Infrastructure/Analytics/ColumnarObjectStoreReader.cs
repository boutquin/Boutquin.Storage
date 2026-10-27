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

using Boutquin.Storage.Domain.Interfaces.ObjectStore;
using Boutquin.Storage.Domain.Interfaces.TimeSeries;
using Boutquin.Storage.Domain.Interfaces.TimeSeries.Columnar;
using Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar;

namespace Boutquin.Storage.Infrastructure.Analytics;

/// <summary>Streams typed column batches from a columnar segment stored as an object.</summary>
/// <typeparam name="TRecord">A dated, generated-columnar record that can be rehydrated by the format.</typeparam>
/// <remarks>The reader borrows both collaborators. Each enumeration opens and disposes one object
/// stream; batches retain decoded arrays and do not depend on the stream after they are yielded.</remarks>
public sealed class ColumnarObjectStoreReader<TRecord>
    where TRecord : IHasDate, IColumnarRehydratable<TRecord>, new()
{
    private readonly IObjectStore _objectStore;
    private readonly IColumnarSegmentFormat<TRecord> _format;

    /// <summary>Creates a reader over borrowed object-store and format collaborators.</summary>
    /// <param name="objectStore">Object store used to open segment streams.</param>
    /// <param name="format">Columnar decoder for <typeparamref name="TRecord"/>.</param>
    /// <exception cref="ArgumentNullException">A collaborator is null.</exception>
    public ColumnarObjectStoreReader(IObjectStore objectStore, IColumnarSegmentFormat<TRecord> format)
    {
        ArgumentNullException.ThrowIfNull(objectStore);
        ArgumentNullException.ThrowIfNull(format);
        _objectStore = objectStore;
        _format = format;
    }

    /// <summary>Reads row-group batches from the named object.</summary>
    /// <param name="key">Object-store key containing one complete columnar segment.</param>
    /// <param name="from">Inclusive lower date bound used for row-group pruning.</param>
    /// <param name="to">Inclusive upper date bound used for row-group pruning.</param>
    /// <param name="projection">Columns to decode.</param>
    /// <param name="direction">Persisted row-group iteration direction.</param>
    /// <param name="cancellationToken">Cancellation observed while opening or decoding the object.</param>
    /// <returns>Decoded row-group batches; boundary groups can contain rows outside the date range.</returns>
    /// <exception cref="ArgumentException"><paramref name="key"/> is blank or invalid for the store.</exception>
    /// <exception cref="FileNotFoundException">The object does not exist.</exception>
    public async IAsyncEnumerable<ColumnBatch<TRecord>> ReadBatchesAsync(string key,
        DateOnly from, DateOnly to, ColumnProjection projection,
        ScanDirection direction = ScanDirection.Ascending,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        using var stream = await _objectStore.ReadAsync(key, cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException($"Object '{key}' does not exist.", key);
        await foreach (var batch in _format.ReadBatchesAsync(stream, from, to, projection, direction,
            cancellationToken).ConfigureAwait(false))
        {
            yield return batch;
        }
    }
}
