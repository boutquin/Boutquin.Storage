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
namespace Boutquin.Storage.Distributed.Transactions;

// A shared mechanism makes the comparison hinge on one actual semantic difference: retained
// shared read locks. Both modes hold write locks and keep writes private until atomic publication.
internal sealed class PointIsolationEngine<TKey, TValue>(bool strict) : IDisposable where TKey : notnull
{
    private sealed class Transaction
    {
        internal Dictionary<TKey, (TValue? Value, bool Found)> Writes { get; } = [];
        internal HashSet<TKey> Reads { get; } = [];
        internal HashSet<TKey> Exclusive { get; } = [];
    }
    private sealed class KeyLock
    {
        internal long? Writer { get; set; }
        internal HashSet<long> Readers { get; } = [];
    }
    private readonly object _gate = new();
    private readonly Dictionary<TKey, TValue?> _values = [];
    private readonly Dictionary<long, Transaction> _transactions = [];
    private readonly Dictionary<TKey, KeyLock> _locks = [];
    // Wait-for registrations: every lock each blocked transaction is currently waiting for, one entry
    // per pending acquire, because one transaction may have several operations in flight. Settling one
    // acquire (granted, cancelled, aborted) removes only that acquire's entry, so a sibling's live wait
    // stays visible to cycle detection. Blockers are derived from the live lock table at detection
    // time, never cached, so a reader granted after a waiter registered is still seen as a blocker.
    private sealed class Wait(TKey key, bool exclusive)
    {
        internal TKey Key { get; } = key;
        internal bool Exclusive { get; } = exclusive;
    }
    private readonly Dictionary<long, List<Wait>> _waits = [];
    // Deadlock victims already aborted by the engine; the caller's AbortAsync acknowledges them.
    private readonly HashSet<long> _victims = [];
    private TaskCompletionSource _changed = NewSignal();
    private long _sequence;
    private bool _disposed;
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal long Begin()
    {
        lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); var id = checked(++_sequence); _transactions.Add(id, new Transaction()); return id; }
    }
    internal async Task<(TValue? Value, bool Found)> ReadAsync(TKey key, long id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key); ct.ThrowIfCancellationRequested();
        if (strict) { await AcquireAsync(key, id, false, ct).ConfigureAwait(false); }
        lock (_gate)
        {
            var transaction = Active(id);
            if (transaction.Writes.TryGetValue(key, out var pending)) { return pending; }
            return _values.TryGetValue(key, out var value) ? (value, true) : (default, false);
        }
    }
    internal async Task WriteAsync(TKey key, TValue? value, bool found, long id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);
        await AcquireAsync(key, id, true, ct).ConfigureAwait(false);
        lock (_gate) { Active(id).Writes[key] = (value, found); }
    }
    private async Task AcquireAsync(TKey key, long id, bool exclusive, CancellationToken ct)
    {
        var wait = new Wait(key, exclusive);
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested(); Task change;
                lock (_gate)
                {
                    var transaction = Active(id);
                    if (!_locks.TryGetValue(key, out var held)) { held = new KeyLock(); _locks.Add(key, held); }
                    var allowed = (!held.Writer.HasValue || held.Writer == id) && (!exclusive || held.Readers.All(reader => reader == id));
                    if (allowed)
                    {
                        // Unregister under the same lock as the grant: a granted acquire must never be
                        // seen as a wait, or it could fabricate a cycle.
                        Unregister(id, wait);
                        if (exclusive) { held.Writer = id; transaction.Exclusive.Add(key); }
                        else { held.Readers.Add(id); transaction.Reads.Add(key); }
                        // A grant adds wait-for edges too: every transaction already waiting on this key is
                        // now also blocked by the new holder, which can close a cycle with no new wait.
                        // Waking those waiters makes each re-run detection against the updated lock table.
                        if (_waits.Values.Any(pending => pending.Any(other => EqualityComparer<TKey>.Default.Equals(other.Key, key)))) { Pulse(); }
                        return;
                    }
                    // A cycle closes when a transaction starts (or resumes) waiting, or when a lock grant
                    // (above) gives a waiter a new blocker; a grant wakes the key's waiters, which resume
                    // here, so checking here finds every deadlock. The requester whose wait closes the cycle is the victim:
                    // it is aborted in place (locks released, writes discarded) so the others progress;
                    // its other pending acquires fail on their next wake-up.
                    Register(id, wait);
                    if (WaitsOnItself(id, wait))
                    {
                        _waits.Remove(id); Release(id, transaction); _transactions.Remove(id); _victims.Add(id); Pulse();
                        throw new TransactionDeadlockException(id);
                    }
                    change = _changed.Task;
                }
                // Cancellation removes only this waiter's registration (in the finally). Prior locks
                // deliberately remain until explicit transaction completion, including the shared
                // lock retained during an upgrade wait.
                await change.WaitAsync(ct).ConfigureAwait(false);
            }
        }
        finally
        {
            // Every exit — cancellation, victim, an aborted or disposed transaction — drops this
            // acquire's registration and no other.
            lock (_gate) { Unregister(id, wait); }
        }
    }
    private void Register(long id, Wait wait)
    {
        if (!_waits.TryGetValue(id, out var waits)) { waits = []; _waits.Add(id, waits); }
        if (!waits.Contains(wait)) { waits.Add(wait); }
    }
    private void Unregister(long id, Wait wait)
    {
        if (_waits.TryGetValue(id, out var waits) && waits.Remove(wait) && waits.Count == 0) { _waits.Remove(id); }
    }
    private bool WaitsOnItself(long start, Wait wait)
    {
        var pending = new Stack<long>(Blockers(start, wait)); var seen = new HashSet<long>();
        while (pending.Count > 0)
        {
            var next = pending.Pop();
            if (next == start) { return true; }
            if (seen.Add(next) && _waits.TryGetValue(next, out var waits))
            {
                foreach (var pendingWait in waits) { foreach (var blocker in Blockers(next, pendingWait)) { pending.Push(blocker); } }
            }
        }
        return false;
    }
    private IEnumerable<long> Blockers(long id, Wait wait)
    {
        if (!_locks.TryGetValue(wait.Key, out var held)) { yield break; }
        if (held.Writer is { } writer && writer != id) { yield return writer; }
        if (wait.Exclusive) { foreach (var reader in held.Readers) { if (reader != id) { yield return reader; } } }
    }
    private void Release(long id, Transaction transaction)
    {
        foreach (var key in transaction.Reads.Concat(transaction.Exclusive).Distinct())
        {
            var held = _locks[key]; held.Readers.Remove(id); if (held.Writer == id) { held.Writer = null; }
            if (held.Writer is null && held.Readers.Count == 0) { _locks.Remove(key); }
        }
    }
    internal Task CompleteAsync(long id, bool commit, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            // The engine already aborted a deadlock victim; acknowledging that abort is idempotent.
            if (!commit && !_disposed && _victims.Remove(id)) { return Task.CompletedTask; }
            var transaction = Active(id);
            if (commit)
            {
                foreach (var pair in transaction.Writes)
                { if (pair.Value.Found) { _values[pair.Key] = pair.Value.Value; } else { _values.Remove(pair.Key); } }
            }
            Release(id, transaction);
            _transactions.Remove(id); Pulse();
        }
        return Task.CompletedTask;
    }
    private Transaction Active(long id)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_victims.Contains(id)) { throw new TransactionDeadlockException(id); }
        return _transactions.TryGetValue(id, out var transaction) ? transaction : throw new InvalidOperationException("Transaction is not active.");
    }
    private void Pulse() { var old = _changed; _changed = NewSignal(); old.TrySetResult(); }
    public void Dispose()
    {
        lock (_gate) { if (_disposed) { return; } _disposed = true; _transactions.Clear(); _locks.Clear(); _values.Clear(); _waits.Clear(); _victims.Clear(); Pulse(); }
    }
}

/// <summary>The lock manager chose this transaction as a deadlock victim and aborted it.</summary>
/// <remarks>The transaction's locks are released and its staged writes discarded, so the other transactions in the
/// wait-for cycle can proceed. Its other operations then fail with this exception; <c>AbortAsync</c> acknowledges the
/// abort idempotently. Retry the work in a new transaction.</remarks>
public sealed class TransactionDeadlockException : InvalidOperationException
{
    /// <summary>Creates the victim notification for one transaction.</summary>
    /// <param name="transactionId">The aborted transaction.</param>
    public TransactionDeadlockException(long transactionId)
        : base($"Transaction {transactionId} was aborted to resolve a deadlock; retry it in a new transaction.") => TransactionId = transactionId;
    /// <summary>The transaction the lock manager aborted.</summary>
    public long TransactionId { get; }
}
