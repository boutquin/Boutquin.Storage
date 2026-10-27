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
using Boutquin.Storage.Domain.ValueObjects.TimeSeries;
using Boutquin.Storage.Infrastructure.LsmTree;

namespace Boutquin.Storage.Infrastructure.TimeSeries;

/// <summary>
/// Fully-assembled columnar time-series stack produced by
/// <see cref="ColumnarTimeSeriesEngineBuilder"/>. Owns the lifetime of the inner LSM engine;
/// dispose this when the store is no longer needed.
/// </summary>
/// <typeparam name="TDatasetKey">
/// The dataset partition key. Must be serializable and comparable (required by the
/// underlying LSM key type <c>TimeSeriesKey&lt;TDatasetKey&gt;</c>).
/// </typeparam>
/// <typeparam name="TRecord">
/// The dated record type. Must implement <see cref="IColumnarRehydratable{TSelf}"/> —
/// annotating the record struct with <c>[ColumnarStorable]</c> causes the source generator
/// to emit the required implementation.
/// </typeparam>
public sealed class ColumnarTimeSeriesStack<TDatasetKey, TRecord>
    : IAsyncDisposable, IDisposable
    where TDatasetKey : ISerializable<TDatasetKey>, IComparable<TDatasetKey>, new()
    where TRecord : ISerializable<TRecord>, IHasDate, IColumnarRehydratable<TRecord>, new()
{
    // int (not bool) so Interlocked.Exchange can provide atomic check-and-set.
    // 0 = not disposed, 1 = disposed.
    private int _disposed;

    internal ColumnarTimeSeriesStack(
        ITimeSeriesStore<TDatasetKey, TRecord> store,
        LsmStorageEngine<TimeSeriesKey<TDatasetKey>, TRecord> innerEngine)
    {
        Store = store;
        InnerEngine = innerEngine;
    }

    /// <summary>The store-level API: append, range query, delete, coverage.</summary>
    public ITimeSeriesStore<TDatasetKey, TRecord> Store { get; }

    /// <summary>
    /// The underlying LSM engine. Use for operational calls
    /// (<c>FlushAsync</c>, <c>CompactAsync</c>, <c>SegmentCount</c>) that the store
    /// interface does not expose.
    /// </summary>
    public LsmStorageEngine<TimeSeriesKey<TDatasetKey>, TRecord> InnerEngine { get; }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        // Interlocked.Exchange atomically sets _disposed to 1 and returns the previous value.
        // If already disposed, return without disposing InnerEngine again.
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Await InnerEngine.DisposeAsync() rather than calling Dispose() synchronously.
        // LsmStorageEngine.Dispose() performs GetAwaiter().GetResult() on async I/O teardown
        // (file-stream closes, WAL flushes) — blocking the calling thread-pool thread under
        // 'await using'. The async path avoids the blocking .GetResult() call entirely.
        await InnerEngine.DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Interlocked.Exchange atomically sets _disposed to 1 and returns the previous value.
        // If the previous value was already 1, another thread (or a re-entrant call) disposed
        // first — return immediately without disposing InnerEngine again.
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        InnerEngine.Dispose();
    }
}
