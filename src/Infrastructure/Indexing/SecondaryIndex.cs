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
namespace Boutquin.Storage.Infrastructure.Indexing;

/// <summary>
/// An in-memory secondary index that maps derived index keys to sets of primary keys.
/// </summary>
/// <typeparam name="TKey">The type of the primary key.</typeparam>
/// <typeparam name="TValue">The type of the value being indexed.</typeparam>
/// <typeparam name="TIndexKey">The type of the derived index key.</typeparam>
/// <param name="keyExtractor">A function that extracts the index key from a value.</param>
/// <remarks>
/// <para>
/// <b>Why SemaphoreSlim for thread safety?</b> The secondary index may be updated concurrently
/// (e.g., during background compaction while serving reads). SemaphoreSlim provides async-compatible
/// mutual exclusion without the overhead of a full lock. It's non-reentrant, which is fine because
/// each public method is a single atomic operation.
/// </para>
///
/// <para>
/// <b>Why Dictionary + HashSet?</b> The index needs O(1) lookup by index key (Dictionary) and O(1)
/// add/remove of primary keys per index key (HashSet). This is more efficient than a sorted structure
/// because secondary index lookups don't need ordering — they just need the set of matching primary keys.
/// </para>
/// </remarks>
public sealed class SecondaryIndex<TKey, TValue, TIndexKey>(Func<TValue, TIndexKey> keyExtractor) : ISecondaryIndex<TKey, TValue, TIndexKey>
    where TKey : IComparable<TKey>, IEquatable<TKey>
    where TIndexKey : IComparable<TIndexKey>
{
    private readonly Dictionary<TIndexKey, HashSet<TKey>> _index = [];
    private readonly Func<TValue, TIndexKey> _keyExtractor = keyExtractor ?? throw new ArgumentNullException(nameof(keyExtractor));
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    // int (not bool) so Interlocked.Exchange can provide atomic check-and-set.
    // 0 = not disposed, 1 = disposed.
    private int _disposed;

    /// <inheritdoc/>
    public async Task IndexAsync(TKey primaryKey, TValue value, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentNullException.ThrowIfNull(primaryKey);
        ArgumentNullException.ThrowIfNull(value);

        var indexKey = _keyExtractor(value);

        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (!_index.TryGetValue(indexKey, out var primaryKeys))
            {
                primaryKeys = [];
                _index[indexKey] = primaryKeys;
            }
            primaryKeys.Add(primaryKey);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<TKey>> LookupAsync(TIndexKey indexKey, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentNullException.ThrowIfNull(indexKey);

        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (_index.TryGetValue(indexKey, out var primaryKeys))
            {
                return primaryKeys.ToList();
            }
            return [];
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <inheritdoc/>
    public async Task RemoveAsync(TKey primaryKey, TValue value, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentNullException.ThrowIfNull(primaryKey);
        ArgumentNullException.ThrowIfNull(value);

        var indexKey = _keyExtractor(value);

        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (_index.TryGetValue(indexKey, out var primaryKeys))
            {
                primaryKeys.Remove(primaryKey);
                if (primaryKeys.Count == 0)
                {
                    _index.Remove(indexKey);
                }
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <inheritdoc/>
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            _index.Clear();
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Marks the index disposed; safe to call multiple times and concurrently. After dispose, every
    /// public method throws <see cref="ObjectDisposedException"/>, including a call already queued on
    /// the semaphore: it wakes when the current holder releases, observes the disposal and throws. An
    /// operation that held the semaphore when Dispose ran completes normally. The
    /// <see cref="SemaphoreSlim"/> is deliberately never disposed: it allocates no kernel handle unless
    /// <see cref="SemaphoreSlim.AvailableWaitHandle"/> is read, which this type never does, and disposing
    /// it under a holder would make the holder's release throw after its update ran and strand every
    /// queued caller forever.
    /// </remarks>
    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
}
