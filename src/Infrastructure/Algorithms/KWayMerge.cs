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
namespace Boutquin.Storage.Infrastructure.Algorithms;

/// <summary>
/// Streaming k-way merge over a set of sorted <see cref="IAsyncEnumerable{T}"/> sources.
/// </summary>
/// <remarks>
/// <para>
/// <b>Theory:</b> DDIA 2e p. 120 prescribes a streaming k-way merge as the canonical LSM
/// compaction primitive: iterate over the SSTables one key at a time, copying the lowest key
/// across the open files; when the same key appears in more than one input, keep only the more
/// recent value. The output is a new merged segment, also sorted by key, with one value per key,
/// produced in O(k) memory.
/// </para>
/// <para>
/// <b>Algorithm:</b> A binary min-heap of per-source cursors keyed by the cursor's current key.
/// On each step the minimum cursor is emitted; duplicates at the same key collapse with
/// last-writer-wins by source index (higher index = newer segment). Each source is advanced lazily,
/// so peak memory is <c>O(sources.Count)</c> regardless of the total entry count in the merged stream.
/// </para>
/// <para>
/// <b>Complexity:</b> <c>O(N log k)</c> time where <c>N</c> is the total entry count and
/// <c>k</c> is <c>sources.Count</c>. Memory is <c>O(k)</c> — one cursor per source plus the heap
/// frame, not proportional to <c>N</c>. This is the key property callers rely on for
/// Phase B1 (compaction), Phase B2 (SSTable merge), and Phase B5 (ranged scan).
/// </para>
/// <para>
/// <b>Caller contract.</b> Every source MUST be sorted ascending by key under the supplied
/// comparer and MUST NOT emit duplicate keys within a single source. The merge tolerates
/// duplicates ACROSS sources (that is the deduplication point) but in-source duplicates would
/// violate the binary-search invariant downstream consumers (SSTable block index, MemTable scan)
/// depend on. The helper deliberately does not validate — sources are produced by the storage
/// engine's own internal iterators, so a runtime check would be dead weight on every entry.
/// </para>
/// <para>
/// <b>Reference:</b> Kleppmann &amp; Riccomini, <i>Designing Data-Intensive Applications</i>, 2nd
/// ed. (O'Reilly, 2026), Ch. 4, "Filling up the memtable and flushing to SSTables" pp. 119–120.
/// </para>
/// </remarks>
internal static class KWayMerge
{
    /// <summary>
    /// Merges <paramref name="sources"/> — each itself sorted ascending by key — into a single
    /// sorted stream, collapsing duplicate keys to the value from the highest-indexed source.
    /// </summary>
    /// <typeparam name="TKey">Key type. Compared via <paramref name="comparer"/>.</typeparam>
    /// <typeparam name="TValue">Value type. Not inspected; merge is by key only.</typeparam>
    /// <param name="sources">Per-source async iterators. Order matters: higher index wins on duplicate keys.</param>
    /// <param name="comparer">Key comparer. The sort order of every input MUST match this comparer.</param>
    /// <param name="cancellationToken">Propagates to every source's enumerator.</param>
    /// <returns>
    /// A single sorted <see cref="IAsyncEnumerable{T}"/> of <see cref="KeyValuePair{TKey,TValue}"/>
    /// with one entry per distinct key.
    /// </returns>
    internal static async IAsyncEnumerable<KeyValuePair<TKey, TValue>> MergeAsync<TKey, TValue>(
        IReadOnlyList<IAsyncEnumerable<KeyValuePair<TKey, TValue>>> sources,
        IComparer<TKey> comparer,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(comparer);

        if (sources.Count == 0)
        {
            yield break;
        }

        // Rent one enumerator per source. Every cursor holds (currentKey, currentValue, sourceIndex,
        // enumerator). The enumerator owns disposal; we release them all in the finally below.
        var enumerators = new IAsyncEnumerator<KeyValuePair<TKey, TValue>>[sources.Count];
        var heap = new MinHeap<Cursor<TKey, TValue>>(
            sources.Count,
            new CursorComparer<TKey, TValue>(comparer));

        try
        {
            for (var i = 0; i < sources.Count; i++)
            {
                var enumerator = sources[i].GetAsyncEnumerator(cancellationToken);
                enumerators[i] = enumerator;
                if (await enumerator.MoveNextAsync().ConfigureAwait(false))
                {
                    heap.Push(new Cursor<TKey, TValue>(
                        enumerator.Current.Key, enumerator.Current.Value, i));
                }
            }

            while (heap.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Pop every cursor sharing the minimum key; the last one (highest source index)
                // wins. This enforces DDIA's "keep the one from the most recent segment" rule.
                // The comparer tie-breaks equal keys by ASCENDING source index, so each popped
                // collision provably carries a higher index than the current winner — the
                // previous winner is always the loser: advance its source and adopt the collision.
                var winner = heap.Pop();
                while (heap.Count > 0 && comparer.Compare(heap.Peek().Key, winner.Key) == 0)
                {
                    var collision = heap.Pop();
                    await AdvanceAsync(enumerators, heap, winner.SourceIndex).ConfigureAwait(false);
                    winner = collision;
                }

                yield return new KeyValuePair<TKey, TValue>(winner.Key, winner.Value);

                await AdvanceAsync(enumerators, heap, winner.SourceIndex).ConfigureAwait(false);
            }
        }
        finally
        {
            foreach (var enumerator in enumerators)
            {
                if (enumerator is not null)
                {
                    await enumerator.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    // ValueTask: this runs once per merged entry on the hottest merge path, and the common
    // case (buffered source, synchronous MoveNextAsync) completes without a state machine
    // allocation.
    private static async ValueTask AdvanceAsync<TKey, TValue>(
        IAsyncEnumerator<KeyValuePair<TKey, TValue>>[] enumerators,
        MinHeap<Cursor<TKey, TValue>> heap,
        int sourceIndex)
    {
        // IAsyncEnumerator.MoveNextAsync() has no CancellationToken overload; cancellation is
        // checked by the outer merge loop's ct.ThrowIfCancellationRequested() guard instead.
        var enumerator = enumerators[sourceIndex];
        if (await enumerator.MoveNextAsync().ConfigureAwait(false))
        {
            heap.Push(new Cursor<TKey, TValue>(
                enumerator.Current.Key, enumerator.Current.Value, sourceIndex));
        }
        // If the enumerator is exhausted, leaving it out of the heap removes it from the merge.
        // Its disposal is deferred to the outer finally; we do not need to null it here.
    }

    // Cursor is a value type so heap pushes/pops stay allocation-free.
    private readonly record struct Cursor<TKey, TValue>(TKey Key, TValue Value, int SourceIndex);

    private sealed class CursorComparer<TKey, TValue>(IComparer<TKey> keyComparer) : IComparer<Cursor<TKey, TValue>>
    {
        private readonly IComparer<TKey> _keyComparer = keyComparer;

        public int Compare(Cursor<TKey, TValue> x, Cursor<TKey, TValue> y)
        {
            var c = _keyComparer.Compare(x.Key, y.Key);
            if (c != 0)
            {
                return c;
            }
            // Equal-key cursors: older source first so the duplicate-collapse loop above pops
            // the loser before the winner. This gives deterministic last-writer-wins ordering
            // without a secondary comparator indirection in the hot path.
            return x.SourceIndex.CompareTo(y.SourceIndex);
        }
    }

    // Small binary min-heap specialised for value-type elements. Kept private to KWayMerge so
    // the layout is predictable; a general collection would pull in generic-constraints baggage
    // we do not need here.
    private sealed class MinHeap<T>(int capacity, IComparer<T> comparer)
    {
        private readonly IComparer<T> _comparer = comparer;
        private T[] _items = new T[Math.Max(capacity, 4)];
        private int _count;

        public int Count => _count;

        public T Peek()
        {
            if (_count == 0)
            {
                throw new InvalidOperationException("Heap is empty.");
            }

            return _items[0];
        }

        public void Push(T item)
        {
            if (_count == _items.Length)
            {
                Array.Resize(ref _items, _items.Length * 2);
            }
            _items[_count] = item;
            SiftUp(_count);
            _count++;
        }

        public T Pop()
        {
            var result = _items[0];
            _count--;
            if (_count > 0)
            {
                _items[0] = _items[_count];
                _items[_count] = default!;
                SiftDown(0);
            }
            else
            {
                _items[0] = default!;
            }
            return result;
        }

        private void SiftUp(int index)
        {
            while (index > 0)
            {
                var parent = (index - 1) / 2;
                if (_comparer.Compare(_items[index], _items[parent]) >= 0)
                {
                    break;
                }
                (_items[index], _items[parent]) = (_items[parent], _items[index]);
                index = parent;
            }
        }

        private void SiftDown(int index)
        {
            while (true)
            {
                var left = (2 * index) + 1;
                var right = left + 1;
                var smallest = index;
                if (left < _count && _comparer.Compare(_items[left], _items[smallest]) < 0)
                {
                    smallest = left;
                }
                if (right < _count && _comparer.Compare(_items[right], _items[smallest]) < 0)
                {
                    smallest = right;
                }
                if (smallest == index)
                {
                    break;
                }
                (_items[index], _items[smallest]) = (_items[smallest], _items[index]);
                index = smallest;
            }
        }
    }
}
