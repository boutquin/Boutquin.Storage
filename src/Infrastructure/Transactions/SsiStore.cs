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
/// A serializable snapshot isolation (SSI) store that wraps an <see cref="MvccStore{TKey, TValue}"/>
/// and adds conflict detection at commit time.
/// </summary>
/// <remarks>
/// <para>
/// <b>How conflict detection works:</b> SSI tracks read sets and write sets per transaction. At commit time:
/// </para>
/// <list type="bullet">
/// <item>For each key in this transaction's read set, check if any concurrent transaction wrote to that key
/// and committed since this transaction started (rw-dependency → abort).</item>
/// <item>For each key in this transaction's write set, check if any concurrent transaction read that key
/// and committed since this transaction started (wr-dependency forming a potential cycle → abort).</item>
/// </list>
///
/// <para>
/// <b>Why optimistic?</b> Unlike two-phase locking, SSI allows transactions to proceed without blocking.
/// Conflicts are detected only at commit time, which yields better throughput under low contention.
/// </para>
///
/// <para>
/// <b>Thread safety:</b> Delegates to the underlying MvccStore's thread safety, plus uses its own
/// <see cref="SemaphoreSlim"/> for read/write set tracking.
/// </para>
/// </remarks>
/// <typeparam name="TKey">The key type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
public sealed class SsiStore<TKey, TValue> : ISsiStore<TKey, TValue>
    where TKey : IComparable<TKey>
{
    private readonly MvccStore<TKey, TValue> _mvcc = new();
    private readonly Dictionary<long, HashSet<TKey>> _readSets = [];
    private readonly Dictionary<long, HashSet<TKey>> _writeSets = [];
    private readonly Dictionary<long, HashSet<long>> _committedAtStart = [];
    private readonly HashSet<long> _committedTxns = [];
    private readonly Dictionary<long, HashSet<TKey>> _committedWriteSets = [];
    private readonly Dictionary<long, HashSet<TKey>> _committedReadSets = [];
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <inheritdoc />
    public long BeginTransaction()
    {
        _gate.Wait();
        try
        {
            var txnId = _mvcc.BeginTransaction();
            _readSets[txnId] = [];
            _writeSets[txnId] = [];
            // Snapshot which txns are already committed — anything NOT in this set is concurrent
            _committedAtStart[txnId] = [.. _committedTxns];
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
        var result = await _mvcc.ReadAsync(key, transactionId, ct).ConfigureAwait(false);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_readSets.TryGetValue(transactionId, out var readSet))
            {
                readSet.Add(key);
            }
        }
        finally
        {
            _gate.Release();
        }

        return result;
    }

    /// <inheritdoc />
    public async Task WriteAsync(TKey key, TValue value, long transactionId, CancellationToken ct = default)
    {
        await _mvcc.WriteAsync(key, value, transactionId, ct).ConfigureAwait(false);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_writeSets.TryGetValue(transactionId, out var writeSet))
            {
                writeSet.Add(key);
            }
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
            var myReadSet = _readSets.GetValueOrDefault(transactionId) ?? [];
            var myWriteSet = _writeSets.GetValueOrDefault(transactionId) ?? [];

            var snapshot = _committedAtStart.GetValueOrDefault(transactionId) ?? [];

            // Check for rw-dependency conflicts:
            // If any concurrent transaction (committed after we started) wrote to a key we read, abort.
            foreach (var committedTxn in _committedTxns)
            {
                if (snapshot.Contains(committedTxn))
                {
                    continue; // Not concurrent — was already committed when we started
                }

                if (_committedWriteSets.TryGetValue(committedTxn, out var theirWriteSet) &&
                    myReadSet.Overlaps(theirWriteSet))
                {
                    _mvcc.AbortTransaction(transactionId);
                    CleanupTransaction(transactionId);
                    return false;
                }
            }

            // Check for wr-dependency conflicts:
            // If any concurrent transaction (committed after we started) read a key we wrote, abort.
            foreach (var committedTxn in _committedTxns)
            {
                if (snapshot.Contains(committedTxn))
                {
                    continue;
                }

                if (_committedReadSets.TryGetValue(committedTxn, out var theirReadSet) &&
                    myWriteSet.Overlaps(theirReadSet))
                {
                    _mvcc.AbortTransaction(transactionId);
                    CleanupTransaction(transactionId);
                    return false;
                }
            }

            // No conflicts — commit
            var committed = await _mvcc.CommitAsync(transactionId, ct).ConfigureAwait(false);
            if (committed)
            {
                _committedTxns.Add(transactionId);
                _committedWriteSets[transactionId] = myWriteSet;
                _committedReadSets[transactionId] = myReadSet;
            }

            return committed;
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
            _mvcc.AbortTransaction(transactionId);
            CleanupTransaction(transactionId);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void CleanupTransaction(long transactionId)
    {
        _readSets.Remove(transactionId);
        _writeSets.Remove(transactionId);
        _committedAtStart.Remove(transactionId);
    }
}
