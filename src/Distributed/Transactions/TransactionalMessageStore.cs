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
using System.Collections.ObjectModel;
namespace Boutquin.Storage.Distributed.Transactions;

/// <summary>A durable local outbox, inbox and idempotency state machine sharing one atomic event-record boundary.</summary>
/// <remarks><para><b>Atomicity:</b> One fsynced mutation record contains business writes, messages, request identity,
/// intent and response. Replay applies all or none of that record. No independent checkpoint participates in this boundary.</para>
/// <para><b>Concurrency:</b> Internally serialized, one live journal owner. Handlers run under the gate over copied committed
/// state and must not re-enter or perform external side effects. An interrupted append poisons the instance until reopen.</para>
/// <para><b>Cost:</b> Each new mutation is O(state snapshot + batch size) plus fsync; direct Commit avoids the snapshot copy.
/// Replay, identities, responses and acknowledgements are retained without compaction. Reference: DDIA, idempotence and transactional outbox.</para></remarks>
public sealed class TransactionalMessageStore : ITransactionalMessageOutbox, IInboxDedupStore, IIdempotencyStore, IDisposable
{
    private sealed record Entry(string Id, string Kind, string Intent, MessageMutation? Mutation);
    private readonly TransactionJournal<Entry> _journal;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Entry> _requests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly List<OutboxMessage> _messages = [];
    private readonly HashSet<string> _messageIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _acknowledged = new(StringComparer.Ordinal);
    private int _disposed;
    private TransactionalMessageStore(TransactionJournal<Entry> journal) { _journal = journal; }
    internal Action<string, bool>? FaultInjector { set => _journal.FaultInjector = value; }
    /// <summary>Opens and replays a single exclusively owned mutation journal.</summary>
    /// <param name="path">File path for the framed, CRC-protected, fsynced event log.</param><param name="ct">Startup cancellation token.</param>
    /// <returns>A recovered store with business state, dedup outcomes and pending deliveries reconstructed.</returns>
    /// <exception cref="IOException">Storage fails or another live owner holds the journal.</exception>
    /// <exception cref="InvalidDataException">Replay violates identity or acknowledgement ordering.</exception>
    public static async Task<TransactionalMessageStore> CreateAsync(string path, CancellationToken ct = default)
    {
        var journal = await TransactionJournal<Entry>.OpenAsync(path, ct).ConfigureAwait(false);
        var result = new TransactionalMessageStore(journal);
        try { await foreach (var entry in journal.Replay(ct).ConfigureAwait(false)) { result.Apply(entry.Event); } return result; }
        // Replayed records are untrusted: deserialization runs MessageMutation's validating constructor,
        // so any non-I/O failure decoding or applying a CRC-valid frame is corruption, typed as documented.
        catch (Exception ex) when (ex is not (IOException or OperationCanceledException))
        { result.Dispose(); throw new InvalidDataException("Invalid message-store journal record.", ex); }
        catch { result.Dispose(); throw; }
    }
    /// <inheritdoc />
    public Task<string> CommitAsync(RequestId id, string intent, MessageMutation mutation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        // Include the actual immutable effects, not only a caller label, in direct-commit identity.
        return MutateAsync(id, "Outbox", TransactionData.Fingerprint(new { Intent = intent, Mutation = mutation }), _ => mutation, ct, intent);
    }
    /// <inheritdoc />
    public Task<string> ProcessAsync(RequestId id, string intent, Func<IReadOnlyDictionary<string, string>, MessageMutation> handler, CancellationToken ct = default)
        => MutateAsync(id, "Inbox", intent, handler, ct, intent);
    /// <inheritdoc />
    public Task<string> ExecuteAsync(RequestId id, string intent, Func<IReadOnlyDictionary<string, string>, MessageMutation> handler, CancellationToken ct = default)
        => MutateAsync(id, "Idempotency", intent, handler, ct, intent);
    private async Task<string> MutateAsync(RequestId id, string kind, string fingerprint,
        Func<IReadOnlyDictionary<string, string>, MessageMutation> handler, CancellationToken ct, string intent)
    {
        id.Validate(); TransactionData.ValidateText(intent); ArgumentNullException.ThrowIfNull(handler);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureUsable();
            if (_requests.TryGetValue(id.Value, out var prior))
            {
                if (prior.Kind != kind || prior.Intent != fingerprint) { throw new InvalidOperationException("Request identity was reused with different intent."); }
                return prior.Mutation!.Result;
            }
            IReadOnlyDictionary<string, string> snapshot = kind == "Outbox" ? new ReadOnlyDictionary<string, string>(new Dictionary<string, string>())
                : new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(_values, StringComparer.Ordinal));
            var mutation = handler(snapshot); ArgumentNullException.ThrowIfNull(mutation);
            if (mutation.Messages.Any(message => _messageIds.Contains(message.Id.Value))) { throw new InvalidOperationException("Outbound message identity was already committed."); }
            var entry = new Entry(id.Value, kind, fingerprint, mutation);
            await _journal.AppendAsync(entry, "Mutation", ct).ConfigureAwait(false); Apply(entry);
            return mutation.Result;
        }
        finally { _gate.Release(); }
    }
    /// <inheritdoc />
    public async Task<IReadOnlyList<OutboxMessage>> PendingMessagesAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { EnsureUsable(); return _messages.Where(message => !_acknowledged.Contains(message.Id.Value)).ToArray(); }
        finally { _gate.Release(); }
    }
    /// <inheritdoc />
    public async Task AcknowledgeAsync(RequestId messageId, CancellationToken ct = default)
    {
        messageId.Validate(); await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureUsable();
            if (!_messageIds.Contains(messageId.Value)) { throw new KeyNotFoundException("Unknown outbound message."); }
            if (_acknowledged.Contains(messageId.Value)) { return; }
            var entry = new Entry(messageId.Value, "Acknowledged", string.Empty, null);
            await _journal.AppendAsync(entry, "Acknowledge", ct).ConfigureAwait(false); Apply(entry);
        }
        finally { _gate.Release(); }
    }
    /// <summary>Reads committed business state; pending or failed uncommitted batches are invisible.</summary>
    /// <param name="key">Nonblank ordinal business key.</param><param name="ct">Read cancellation token.</param><returns>Committed text, or null for absent/deleted keys.</returns>
    public async Task<string?> ReadAsync(string key, CancellationToken ct = default)
    {
        TransactionData.ValidateText(key); await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { EnsureUsable(); return _values.GetValueOrDefault(key); } finally { _gate.Release(); }
    }
    private void Apply(Entry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Id)) { throw new InvalidDataException("Missing durable identity."); }
        if (entry.Kind == "Acknowledged")
        {
            if (!_messageIds.Contains(entry.Id) || !_acknowledged.Add(entry.Id)) { throw new InvalidDataException("Invalid acknowledgement order."); }
            return;
        }
        if (entry.Kind is not ("Outbox" or "Inbox" or "Idempotency") || entry.Mutation is null || _requests.ContainsKey(entry.Id) || entry.Mutation.Messages.Any(message => _messageIds.Contains(message.Id.Value)))
        { throw new InvalidDataException("Invalid mutation or reused durable identity."); }
        TransactionData.Apply(_values, entry.Mutation.Writes);
        foreach (var message in entry.Mutation.Messages) { _messages.Add(message); _messageIds.Add(message.Id.Value); }
        _requests.Add(entry.Id, entry);
    }
    private void EnsureUsable() { ObjectDisposedException.ThrowIf(_disposed != 0, this); _journal.EnsureUsable(); }
    /// <summary>Idempotently closes the owned journal. Disposal is not a commit or delivery acknowledgement. Dispose waits for a call in progress to finish; calls queued behind it, and later calls, throw <see cref="ObjectDisposedException"/>.</summary>
    /// <remarks>The gate is never disposed: it allocates no kernel handle, and a disposed semaphore neither wakes its waiters nor
    /// accepts the holder's release. A handler must not dispose the store it runs under: Dispose would wait on that handler.</remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) { return; }
        _gate.Wait();
        try { _journal.Dispose(); } finally { _gate.Release(); }
    }
}
