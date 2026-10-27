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
using Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar;

namespace Boutquin.Storage.Infrastructure.TimeSeries;

/// <summary>
/// In-memory reference implementation of <see cref="ITimeSeriesStore{TDatasetKey,TRecord}"/>.
/// Backed by a <see cref="ConcurrentDictionary{TKey,TValue}"/> of per-dataset
/// <see cref="SortedList{TKey,TValue}"/> maps from <see cref="DateOnly"/> to record (a
/// <see cref="SortedList{TKey,TValue}"/> rather than a <see cref="SortedDictionary{TKey,TValue}"/>
/// so coverage can read the min/max key by index in O(1)).
/// </summary>
/// <remarks>
/// <para><b>Semantics:</b> last-writer-wins on <c>(dataset, date)</c>, ascending date iteration
/// for range scans, coverage computed from the sorted map's first and last key.</para>
/// <para><b>Why no coverage index:</b> unlike <c>LsmTimeSeriesStore</c>, the in-memory store
/// answers <c>GetCoverageAsync</c> directly from the sorted map (no segment scan needed), so a
/// secondary index would add bookkeeping without benefit. The LSM variant needs the index because
/// scanning segments is O(N).</para>
/// </remarks>
/// <typeparam name="TDatasetKey">Dataset partition key type.</typeparam>
/// <typeparam name="TRecord">Dated record type.</typeparam>
public sealed class InMemoryTimeSeriesStore<TDatasetKey, TRecord>
    : ITimeSeriesStore<TDatasetKey, TRecord>
    where TDatasetKey : ISerializable<TDatasetKey>, IComparable<TDatasetKey>, new()
    where TRecord : ISerializable<TRecord>, IHasDate, new()
{
    // SortedList (not SortedDictionary) so GetCoverageAsync can read the min/max key by index in
    // O(1) (Keys[0] / Keys[Count-1]) instead of enumerating the whole KeyCollection — the latter
    // does not implement IList<T>, so Enumerable.Last() on it is O(N). Insert is O(N) on a
    // SortedList vs O(log N) on a SortedDictionary, an acceptable trade for the in-memory
    // reference store, whose coverage query is on the LSM sibling's per-append fallback path.
    //
    // Datasets are identified by serialized-bytes identity, not TDatasetKey.Equals: the key
    // contract promises no value equality. This is the dataset identity of TimeSeriesKey that the
    // LSM sibling resolves through (TimeSeriesKey.CompareDatasets: comparer-equal and byte-identical),
    // so a comparer-equal dataset with other bytes is a different dataset on both stores.
    private readonly ConcurrentDictionary<TDatasetKey, SortedList<DateOnly, TRecord>> _byDataset =
        new(SerializedKeyEqualityComparer<TDatasetKey>.Instance);

    /// <inheritdoc />
    public Task AppendAsync(
        TDatasetKey dataset,
        IEnumerable<TRecord> records,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        cancellationToken.ThrowIfCancellationRequested();

        // A rejected batch applies nothing, as on the LSM sibling: materialise the batch and read
        // every record's date before the slot is touched, so a faulting enumerator or a null
        // element throws here, and observe cancellation once more after enumeration finishes.
        var batch = records as IList<TRecord> ?? [.. records];
        var dates = new DateOnly[batch.Count];
        for (var i = 0; i < batch.Count; i++)
        {
            dates[i] = batch[i].Date;
        }

        cancellationToken.ThrowIfCancellationRequested();

        // The slot is resolved outside its lock, and DeleteRangeAsync drops a slot it empties
        // (under that lock). A writer that fetched the slot before the drop would otherwise write
        // into an orphan no reader can reach — so re-validate the mapping once the lock is held
        // and retry against the replacement slot. A dropped slot is never re-inserted, so the
        // re-check under the lock is sufficient.
        while (true)
        {
            var slot = _byDataset.GetOrAdd(dataset, _ => []);
            lock (slot)
            {
                if (!_byDataset.TryGetValue(dataset, out var current) || !ReferenceEquals(current, slot))
                {
                    continue;
                }

                for (var i = 0; i < batch.Count; i++)
                {
                    slot[dates[i]] = batch[i];
                }
            }

            return Task.CompletedTask;
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<TRecord> GetRangeAsync(
        TDatasetKey dataset,
        DateOnly from,
        DateOnly to,
        ColumnProjection? projection = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (from > to)
        {
            yield break;
        }

        if (!_byDataset.TryGetValue(dataset, out var slot))
        {
            yield break;
        }

        // Null means "all columns" — resolve before use so downstream code never sees null.
        var effectiveProjection = projection ?? ColumnProjection.All;

        // Snapshot under the lock so iteration is stable; the snapshot is small for typical
        // windows and avoids holding the lock across yields.
        List<TRecord> snapshot;
        lock (slot)
        {
            snapshot = [];
            foreach (var kvp in slot)
            {
                if (kvp.Key < from)
                {
                    continue;
                }

                if (kvp.Key > to)
                {
                    break;
                }

                snapshot.Add(kvp.Value);
            }
        }

        // Apply ProjectTo uniformly to every record so the in-memory store's GetRangeAsync
        // shape matches the LSM+columnar path's shape. Records that don't implement
        // IProjectable<TRecord> flow through unchanged — the same documented trade-off the
        // LSM engine's memtable path makes.
        // The snapshot is already materialised synchronously above, so the enumeration itself does
        // no asynchronous work. A single await keeps the async-iterator shape (and satisfies the
        // compiler) without paying the per-record continuation cost of an `await Task.Yield()` on
        // every element — that per-item yield forced a thread-pool hop for each record with no
        // benefit to a caller draining an in-memory snapshot.
        await Task.CompletedTask.ConfigureAwait(false);

        var applyProjection = effectiveProjection.FieldNames.Count > 0;
        foreach (var r in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (applyProjection && r is IProjectable<TRecord> projectable)
            {
                yield return projectable.ProjectTo(effectiveProjection);
            }
            else
            {
                yield return r;
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Reference implementation: materialises the full prefix via <see cref="GetRangeAsync"/>
    /// (identical LWW/projection semantics) and yields the newest <paramref name="count"/>
    /// records in descending order. O(n) per call — acceptable for the in-memory reference;
    /// the LSM sibling prunes by date windows instead.
    /// </remarks>
    public IAsyncEnumerable<TRecord> GetTailAsync(
        TDatasetKey dataset,
        int count,
        DateOnly? onOrBefore = null,
        ColumnProjection? projection = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        return TailCoreAsync(dataset, count, onOrBefore, projection ?? ColumnProjection.All, cancellationToken);
    }

    private async IAsyncEnumerable<TRecord> TailCoreAsync(
        TDatasetKey dataset,
        int count,
        DateOnly? onOrBefore,
        ColumnProjection projection,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var end = onOrBefore ?? DateOnly.MaxValue;
        var all = new List<TRecord>();
        await foreach (var r in GetRangeAsync(dataset, DateOnly.MinValue, end, projection, cancellationToken)
            .ConfigureAwait(false))
        {
            all.Add(r);
        }

        for (var i = all.Count - 1; i >= 0 && all.Count - i <= count; i--)
        {
            yield return all[i];
        }
    }

    /// <inheritdoc />
    public Task<DateRange?> GetCoverageAsync(TDatasetKey dataset, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_byDataset.TryGetValue(dataset, out var slot))
        {
            return Task.FromResult<DateRange?>(null);
        }

        lock (slot)
        {
            if (slot.Count == 0)
            {
                return Task.FromResult<DateRange?>(null);
            }

            // SortedList.Keys is an IList<DateOnly> over a sorted backing array, so the smallest
            // and largest keys are direct indexed reads — O(1) each, no enumeration. (A
            // SortedDictionary.KeyCollection does not implement IList<T>, so Last() on it would be
            // O(N); the slot type is a SortedList precisely to make this query O(1).)
            var keys = slot.Keys;
            var min = keys[0];
            var max = keys[keys.Count - 1];

            return Task.FromResult<DateRange?>(new DateRange(min, max));
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DateRange>> GetGapsAsync(
        TDatasetKey dataset,
        DateRange requested,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (requested.IsEmpty)
        {
            return [];
        }

        var coverage = await GetCoverageAsync(dataset, cancellationToken).ConfigureAwait(false);
        if (coverage is null)
        {
            return [requested];
        }

        return requested.Minus(coverage.Value);
    }

    /// <inheritdoc />
    public Task DeleteRangeAsync(
        TDatasetKey dataset,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (from > to)
        {
            return Task.CompletedTask;
        }

        if (!_byDataset.TryGetValue(dataset, out var slot))
        {
            return Task.CompletedTask;
        }

        lock (slot)
        {
            // Materialise the to-remove list — cannot mutate during iteration.
            var toRemove = new List<DateOnly>();
            foreach (var key in slot.Keys)
            {
                if (key < from)
                {
                    continue;
                }

                if (key > to)
                {
                    break;
                }

                toRemove.Add(key);
            }

            foreach (var k in toRemove)
            {
                slot.Remove(k);
            }

            if (slot.Count == 0)
            {
                // Remove only THIS slot: a deleter can hold a slot another deleter already
                // dropped, and an unconditional key removal would then drop the replacement slot
                // a concurrent append created (losing that append).
                _byDataset.TryRemove(new KeyValuePair<TDatasetKey, SortedList<DateOnly, TRecord>>(dataset, slot));
            }
        }

        return Task.CompletedTask;
    }
}
