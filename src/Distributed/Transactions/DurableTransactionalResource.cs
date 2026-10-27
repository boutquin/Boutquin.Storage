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

/// <summary>A durable string-key participant whose prepared key locks and final writes replay from one fsynced event log.</summary>
/// <remarks><para><b>Concurrency:</b> Internally serialized; one live owner per journal path. Prepare conflicts reject rather than wait.
/// Reads see committed values while prepared writes stay private. Commit atomically publishes this participant's whole write set.</para>
/// <para><b>Recovery:</b> Prepared locks survive reopen and block other prepares until a coordinator decision arrives.
/// No autonomous timeout can safely release them. A failed append poisons the instance; reopen before retry.</para>
/// <para><b>Cost:</b> Prepare/commit O(write-set size) plus fsync; startup O(retained log). IDs and terminal outcomes are retained.
/// Reference: DDIA, transactions and two-phase commit. This is a single-process reference participant, not a replicated service.</para></remarks>
public sealed class DurableTransactionalResource : ITransactionalResource, IDisposable
{
    // Writes is journaled through an ordinal-equality interface shape: JSON materializes it as a
    // Dictionary (ordinal), never a default-comparer SortedDictionary whose culture comparison would
    // merge ordinally distinct keys (precomposed vs decomposed text) before Apply can canonicalize.
    private sealed record Entry(string ResourceId, string Id, string State, IReadOnlyDictionary<string, string?>? Writes);
    private readonly TransactionJournal<Entry> _journal;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Entry> _transactions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _locks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private int _disposed;
    private DurableTransactionalResource(string resourceId, TransactionJournal<Entry> journal) { ResourceId = resourceId; _journal = journal; }
    internal Action<string, bool>? FaultInjector { set => _journal.FaultInjector = value; }
    /// <inheritdoc />
    public string ResourceId { get; }
    /// <summary>Opens an exclusively owned journal and replays values, terminal decisions and prepared locks.</summary>
    /// <param name="resourceId">Stable nonblank resource identity, verified against every replayed record.</param><param name="path">Journal file path.</param><param name="ct">Startup cancellation token.</param>
    /// <returns>A fully recovered participant. Dispose waits for a call in progress before closing the journal.</returns>
    /// <exception cref="InvalidDataException">The journal belongs to another resource or contains an invalid transition.</exception>
    /// <exception cref="IOException">Another live owner holds the journal or storage fails.</exception>
    public static async Task<DurableTransactionalResource> CreateAsync(string resourceId, string path, CancellationToken ct = default)
    {
        TransactionData.ValidateText(resourceId);
        var journal = await TransactionJournal<Entry>.OpenAsync(path, ct).ConfigureAwait(false);
        var result = new DurableTransactionalResource(resourceId, journal);
        try { await foreach (var record in journal.Replay(ct).ConfigureAwait(false)) { result.Apply(record.Event); } return result; }
        // Replayed records are untrusted: a CRC-valid frame with null or invalid fields is corruption,
        // so any non-I/O failure decoding or applying it surfaces as the documented typed exception.
        catch (Exception ex) when (ex is not (IOException or OperationCanceledException))
        { result.Dispose(); throw new InvalidDataException("Invalid participant journal record.", ex); }
        catch { result.Dispose(); throw; }
    }
    /// <inheritdoc />
    public async Task<PrepareResult> PrepareAsync(RequestId id, IReadOnlyDictionary<string, string?> writes, CancellationToken ct = default)
    {
        id.Validate(); var copy = TransactionData.Copy(writes);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureUsable();
            if (_transactions.TryGetValue(id.Value, out var previous))
            {
                if (previous.Writes is not null && TransactionData.Fingerprint(previous.Writes) != TransactionData.Fingerprint(copy))
                { throw new InvalidOperationException("Transaction ID was reused with different writes."); }
                return previous.State == "Committed" ? PrepareResult.Committed : previous.State == "Prepared" ? PrepareResult.Prepared : PrepareResult.Rejected;
            }
            var state = copy.Keys.Any(_locks.ContainsKey) ? "Aborted" : "Prepared";
            var entry = new Entry(ResourceId, id.Value, state, copy);
            await _journal.AppendAsync(entry, "Prepare", ct).ConfigureAwait(false); Apply(entry);
            return state == "Prepared" ? PrepareResult.Prepared : PrepareResult.Rejected;
        }
        finally { _gate.Release(); }
    }
    /// <inheritdoc />
    public async Task<CommitResult> CommitAsync(RequestId id, CancellationToken ct = default)
    {
        id.Validate(); await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureUsable();
            if (!_transactions.TryGetValue(id.Value, out var previous)) { throw new InvalidOperationException("An unknown transaction cannot commit."); }
            if (previous.State == "Aborted") { return CommitResult.Aborted; }
            if (previous.State == "Committed") { return CommitResult.Committed; }
            var entry = previous with { State = "Committed" };
            await _journal.AppendAsync(entry, "Commit", ct).ConfigureAwait(false); Apply(entry);
            return CommitResult.Committed;
        }
        finally { _gate.Release(); }
    }
    /// <inheritdoc />
    public async Task<AbortResult> AbortAsync(RequestId id, CancellationToken ct = default)
    {
        id.Validate(); await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureUsable(); _transactions.TryGetValue(id.Value, out var previous);
            if (previous?.State == "Committed") { return AbortResult.AlreadyCommitted; }
            if (previous?.State == "Aborted") { return AbortResult.Aborted; }
            var entry = new Entry(ResourceId, id.Value, "Aborted", previous?.Writes);
            await _journal.AppendAsync(entry, "Abort", ct).ConfigureAwait(false); Apply(entry);
            return AbortResult.Aborted;
        }
        finally { _gate.Release(); }
    }
    /// <summary>Reads committed state without exposing a prepared transaction's private writes.</summary>
    /// <param name="key">Nonblank ordinal key.</param><param name="ct">Cancellation token; reads have no durable effect.</param>
    /// <returns>The committed text, or null for absent/deleted keys.</returns>
    public async Task<string?> ReadAsync(string key, CancellationToken ct = default)
    {
        TransactionData.ValidateText(key); await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { EnsureUsable(); return _values.GetValueOrDefault(key); } finally { _gate.Release(); }
    }
    private void Apply(Entry entry)
    {
        // Replay yields an unordered ordinal Dictionary. Restore ordinal canonical ordering
        // before retaining or fingerprinting replayed intent.
        if (entry.Writes is not null) { entry = entry with { Writes = TransactionData.Copy(entry.Writes) }; }
        if (entry.ResourceId != ResourceId || string.IsNullOrWhiteSpace(entry.Id) || entry.State is not ("Prepared" or "Committed" or "Aborted"))
        { throw new InvalidDataException("Invalid participant journal record."); }
        _transactions.TryGetValue(entry.Id, out var previous);
        if (previous is not null && previous.State != "Prepared") { throw new InvalidDataException("A final participant decision cannot change."); }
        if (entry.State == "Committed" && previous?.State != "Prepared") { throw new InvalidDataException("Commit lacks durable preparation."); }
        if (entry.State == "Committed" && (entry.Writes is null || TransactionData.Fingerprint(entry.Writes) != TransactionData.Fingerprint(previous!.Writes!)))
        { throw new InvalidDataException("Commit writes differ from the durably prepared intent."); }
        if (entry.State == "Prepared")
        {
            if (entry.Writes is null || previous is not null || entry.Writes.Keys.Any(_locks.ContainsKey)) { throw new InvalidDataException("Conflicting prepared intents."); }
            foreach (var key in entry.Writes.Keys) { _locks.Add(key, entry.Id); }
        }
        else
        {
            if (entry.State == "Committed") { TransactionData.Apply(_values, entry.Writes!); }
            if (previous?.Writes is { } writes) { foreach (var key in writes.Keys) { _locks.Remove(key); } }
        }
        _transactions[entry.Id] = entry;
    }
    private void EnsureUsable() { ObjectDisposedException.ThrowIf(_disposed != 0, this); _journal.EnsureUsable(); }
    /// <summary>Idempotently closes owned files. It neither aborts prepared work nor creates a commit point. Dispose waits for a call in progress to finish; calls queued behind it, and later calls, throw <see cref="ObjectDisposedException"/>.</summary>
    /// <remarks>The gate is never disposed: it allocates no kernel handle, and a disposed semaphore neither wakes its waiters nor
    /// accepts the holder's release, which would report a durable vote as a disposal fault.</remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) { return; }
        _gate.Wait();
        try { _journal.Dispose(); } finally { _gate.Release(); }
    }
}
