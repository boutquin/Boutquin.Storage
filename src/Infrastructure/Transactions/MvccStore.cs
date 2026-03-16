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
namespace Boutquin.Storage.Infrastructure.Transactions;

/// <summary>
/// An in-memory MVCC key-value store that provides snapshot isolation.
/// </summary>
/// <remarks>
/// <para>
/// <b>How it works:</b> Each key maintains a version chain — a list of <see cref="VersionedValue{TValue}"/>
/// entries ordered newest-first. When a transaction reads, it walks the chain and returns the latest version
/// that is either (a) its own write or (b) committed before the reader started.
/// </para>
///
/// <para>
/// <b>Why SemaphoreSlim?</b> Unlike <c>lock</c>, <see cref="SemaphoreSlim"/> supports async/await.
/// The semaphore serializes writes to version chains but does not block readers from seeing their
/// own consistent snapshot.
/// </para>
///
/// <para>
/// <b>Thread safety:</b> All public methods are thread-safe via a <see cref="SemaphoreSlim"/> gate.
/// </para>
/// </remarks>
/// <typeparam name="TKey">The key type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
public sealed class MvccStore<TKey, TValue> : IMvccStore<TKey, TValue>
    where TKey : IComparable<TKey>
{
    private enum TransactionState { Active, Committed, Aborted }

    private readonly Dictionary<TKey, List<VersionedValue<TValue>>> _versionChains = [];
    private readonly Dictionary<long, TransactionState> _transactionStates = [];
    private readonly Dictionary<long, HashSet<long>> _snapshots = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _nextTransactionId;

    /// <inheritdoc />
    public long BeginTransaction()
    {
        _gate.Wait();
        try
        {
            var txnId = Interlocked.Increment(ref _nextTransactionId);
            _transactionStates[txnId] = TransactionState.Active;

            // Snapshot: record which transactions are committed right now
            var committedAtStart = new HashSet<long>();
            foreach (var (id, state) in _transactionStates)
            {
                if (state == TransactionState.Committed)
                {
                    committedAtStart.Add(id);
                }
            }

            _snapshots[txnId] = committedAtStart;
            return txnId;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<(TValue Value, bool Found)> ReadAsync(TKey key, long transactionId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_versionChains.TryGetValue(key, out var chain))
            {
                return (default!, false);
            }

            var snapshot = _snapshots[transactionId];

            // Walk the version chain (newest first) to find the latest visible version
            foreach (var version in chain)
            {
                // Visible if: own write, or committed before this txn started
                if (version.TransactionId == transactionId ||
                    snapshot.Contains(version.TransactionId))
                {
                    return version.IsDeleted ? (default!, false) : (version.Value, true);
                }
            }

            return (default!, false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task WriteAsync(TKey key, TValue value, long transactionId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_versionChains.TryGetValue(key, out var chain))
            {
                chain = [];
                _versionChains[key] = chain;
            }

            // Insert at the front (newest first)
            chain.Insert(0, new VersionedValue<TValue>(value, transactionId, false));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<bool> CommitAsync(long transactionId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_transactionStates.TryGetValue(transactionId, out var state) ||
                state != TransactionState.Active)
            {
                return false;
            }

            _transactionStates[transactionId] = TransactionState.Committed;
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void AbortTransaction(long transactionId)
    {
        _gate.Wait();
        try
        {
            _transactionStates[transactionId] = TransactionState.Aborted;
        }
        finally
        {
            _gate.Release();
        }
    }
}
