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

/// <summary>A durable two-phase coordinator that records its decision before delivering any commit or abort.</summary>
/// <remarks><para><b>Concurrency:</b> Internally serialized: an ExecuteAsync or RecoverAsync call holds the coordinator until
/// its decision delivery ends. Within one call, calls into one participant are made one at a time, in journal order, and
/// distinct participants are driven concurrently, so a participant that never answers holds back only its own decisions in that
/// call. Across calls it does not: a call stuck delivering to a participant that never answers
/// blocks every other ExecuteAsync and RecoverAsync call on the coordinator until it completes or its caller cancels, so pass
/// a cancellation token that bounds delivery. Cancellation reaches a stuck call only through the participant: the coordinator
/// forwards the token and waits for the participant call to return, so a participant that ignores cancellation holds the
/// coordinator until it answers. One live owner per journal path; registered resources are borrowed and must be
/// governed exclusively by this coordinator.</para>
/// <para><b>Atomicity:</b> All participants eventually reach the same final decision when recovery and delivery succeed.
/// Separate shard reads may observe partial commit delivery; no global snapshot or simultaneous visibility is supplied.</para>
/// <para><b>Blocking:</b> Prepared participants retain locks during coordinator or transport failure. A participant prepare that
/// fails or times out counts as a no vote: the abort decision is logged and sent to every participant in the same call.
/// Once a decision is logged, every participant is sent it. With a reopen hook, a failed delivery is retried with capped
/// exponential backoff (10 ms doubling to 1 s) until it succeeds or the caller cancels, and the hook supplies the instance to
/// retry against, so a participant that poisoned itself is reopened first. Without a hook the call fails at once with
/// <see cref="TransactionDeliveryException"/>, naming the durable decision; recovery or an identical retry redelivers it
/// once the registered participant instance can accept it (see that exception for a participant that poisoned itself).
/// Recovery durably aborts every undecided start, then resends every recorded decision without retrying in-call; a
/// participant that fails or never answers does not stop the others from receiving their decisions.</para>
/// <para><b>Cost:</b> O(participants + writes) and two coordinator fsyncs per new transaction, plus participant fsyncs.
/// The journal and identities are retained without compaction. Reference: DDIA, distributed transactions/two-phase commit.</para></remarks>
public sealed class TwoPhaseCommitCoordinator : ITwoPhaseCommitCoordinator, IDisposable
{
    // Writes is journaled through an ordinal-equality interface shape: JSON materializes it as a
    // Dictionary (ordinal), never a default-comparer SortedDictionary whose culture comparison would
    // merge ordinally distinct keys before Apply restores ordinal canonical order for fingerprinting.
    private sealed record PlanPart(string ResourceId, IReadOnlyDictionary<string, string?> Writes);
    private sealed record Entry(string Id, PlanPart[] Plan, string? Decision);
    private readonly TransactionJournal<Entry> _journal;
    private readonly Dictionary<string, ITransactionalResource> _resources;
    private readonly Dictionary<string, Entry> _transactions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _disposed;
    private static readonly TimeSpan s_initialDeliveryBackoff = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan s_maxDeliveryBackoff = TimeSpan.FromSeconds(1);
    private TwoPhaseCommitCoordinator(TransactionJournal<Entry> journal, Dictionary<string, ITransactionalResource> resources) { _journal = journal; _resources = resources; }
    internal Action<string, bool>? FaultInjector { set => _journal.FaultInjector = value; }
    /// <summary>Opens the decision journal and validates that all retained participants are registered.</summary>
    /// <param name="path">Exclusively owned journal file.</param><param name="resources">Borrowed participants with unique stable identities.</param><param name="ct">Startup cancellation token.</param>
    /// <returns>A recovered coordinator; call RecoverAsync to resolve or redeliver recorded transactions.</returns>
    /// <exception cref="ArgumentException">Resources are null or identities duplicate.</exception>
    /// <exception cref="InvalidDataException">Journal transitions or participant bindings are invalid.</exception>
    public static async Task<TwoPhaseCommitCoordinator> CreateAsync(string path, IEnumerable<ITransactionalResource> resources, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(resources);
        var registry = new Dictionary<string, ITransactionalResource>(StringComparer.Ordinal);
        foreach (var resource in resources) { ArgumentNullException.ThrowIfNull(resource); TransactionData.ValidateText(resource.ResourceId); registry.Add(resource.ResourceId, resource); }
        var journal = await TransactionJournal<Entry>.OpenAsync(path, ct).ConfigureAwait(false);
        var result = new TwoPhaseCommitCoordinator(journal, registry);
        try { await foreach (var record in journal.Replay(ct).ConfigureAwait(false)) { result.Apply(record.Event); } return result; }
        // Replayed records are untrusted: a CRC-valid frame with null or invalid fields is corruption,
        // so any non-I/O failure decoding or applying it surfaces as the documented typed exception.
        catch (Exception ex) when (ex is not (IOException or OperationCanceledException))
        { result.Dispose(); throw new InvalidDataException("Invalid coordinator journal record.", ex); }
        catch { result.Dispose(); throw; }
    }
    /// <inheritdoc />
    public async Task<CommitResult> ExecuteAsync(RequestId id, IReadOnlyList<TransactionIntent> plan, Func<ITransactionalResource, CancellationToken, Task<ITransactionalResource>>? reopen = null, CancellationToken ct = default)
    {
        id.Validate(); ArgumentNullException.ThrowIfNull(plan);
        var parts = plan.Select(part => { ArgumentNullException.ThrowIfNull(part); return new PlanPart(part.ResourceId, TransactionData.Copy(part.Writes)); }).OrderBy(part => part.ResourceId, StringComparer.Ordinal).ToArray();
        if (parts.Length == 0 || parts.Select(part => part.ResourceId).Distinct(StringComparer.Ordinal).Count() != parts.Length || parts.Any(part => !_resources.ContainsKey(part.ResourceId)))
        { throw new ArgumentException("A plan needs unique registered participants.", nameof(plan)); }
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureUsable();
            if (_transactions.TryGetValue(id.Value, out var entry))
            {
                if (TransactionData.Fingerprint(entry.Plan) != TransactionData.Fingerprint(parts)) { throw new InvalidOperationException("Transaction ID was reused with different intent."); }
            }
            else
            {
                entry = new Entry(id.Value, parts, null);
                await _journal.AppendAsync(entry, "Start", ct).ConfigureAwait(false); Apply(entry);
            }
            if (entry.Decision is null)
            {
                var commit = true;
                foreach (var part in entry.Plan)
                {
                    PrepareResult vote;
                    // A prepare that fails or times out is a no vote, exactly like Rejected: the
                    // coordinator aborts the transaction and sends abort to every participant
                    // (DDIA 2PC). Only the caller's own cancellation leaves the start undecided.
                    try { vote = await _resources[part.ResourceId].PrepareAsync(id, part.Writes, ct).ConfigureAwait(false); }
                    catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
                    { commit = false; break; }
                    if (vote == PrepareResult.Rejected) { commit = false; break; }
                }
                // The durable decision is Abort after a failed prepare; DeliverAsync sends it to
                // every participant, so a sibling that already voted yes is released even when the
                // failed one is unreachable.
                entry = await DecideAsync(entry, commit ? "Commit" : "Abort", ct).ConfigureAwait(false);
            }
            var failure = (await DeliverAsync([entry], reopen, ct).ConfigureAwait(false))[0];
            if (failure is not null) { throw failure; }
            return DecisionOf(entry);
        }
        finally { _gate.Release(); }
    }
    /// <inheritdoc />
    public async Task RecoverAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureUsable();
            // Decide every undecided start before any delivery, then attempt every transaction: one
            // unreachable participant must not strand other transactions' prepared locks.
            foreach (var original in _transactions.Values.Where(entry => entry.Decision is null).ToArray())
            { await DecideAsync(original, "Abort", ct).ConfigureAwait(false); }
            // Delivery drives every participant independently, so within this pass a participant that
            // never answers holds back only its own decisions (the pass still holds the gate until it ends).
            var failures = (await DeliverAsync(_transactions.Values.ToArray(), null, ct).ConfigureAwait(false))
                .OfType<TransactionDeliveryException>().ToArray();
            if (failures is [var single]) { throw single; }
            if (failures.Length > 0) { throw new AggregateException("Durable decisions of several transactions did not reach every participant.", failures); }
        }
        finally { _gate.Release(); }
    }
    private async Task<Entry> DecideAsync(Entry entry, string decision, CancellationToken ct)
    {
        var decided = entry with { Decision = decision };
        await _journal.AppendAsync(decided, "Decision", ct).ConfigureAwait(false); Apply(decided);
        return decided;
    }
    // DDIA 2PC: after the commit point the decision is irrevocable, so a failed commit or abort
    // request is retried until it succeeds, and every participant is attempted even when another
    // one fails or never answers. Each participant gets its own queue that sends it, one call at a
    // time and in the given entry order, the decisions it takes part in; distinct participants run
    // concurrently, so a participant that does not answer holds back only its own queue. Without a
    // reopen hook nothing retries in-call: the undelivered decision is reported through
    // TransactionDeliveryException and RecoverAsync or an identical retry redelivers it. Returns
    // one slot per entry, null when every participant of that entry acknowledged it.
    private async Task<TransactionDeliveryException?[]> DeliverAsync(IReadOnlyList<Entry> entries, Func<ITransactionalResource, CancellationToken, Task<ITransactionalResource>>? reopen, CancellationToken ct)
    {
        var failures = entries.Select(entry => new Exception?[entry.Plan.Length]).ToArray();
        var queues = entries
            .SelectMany((entry, index) => entry.Plan.Select((part, slot) => (Entry: index, Slot: slot, part.ResourceId)))
            .GroupBy(item => item.ResourceId, StringComparer.Ordinal)
            .Select(group => (ResourceId: group.Key, Resource: _resources[group.Key], Items: group.ToArray()))
            .ToArray();
        // A contradiction or a misbehaving reopen hook stops the other queues: that is a caller or
        // participant fault to surface, not a delivery to keep waiting on.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var reopenGate = new SemaphoreSlim(1, 1);
        var tasks = queues.Select(queue => DeliverToParticipantAsync(queue.ResourceId, queue.Resource, queue.Items, entries, failures, reopen, reopenGate, stop)).ToArray();
        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch when (tasks.FirstOrDefault(task => task.IsFaulted) is { Exception.InnerException: { } fault })
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(fault); }
        return entries.Select((entry, index) =>
        {
            var undelivered = entry.Plan.Where((_, slot) => failures[index][slot] is not null).Select(part => part.ResourceId).ToArray();
            return undelivered.Length == 0 ? null : new TransactionDeliveryException(entry.Id, DecisionOf(entry), undelivered, failures[index].First(failure => failure is not null)!);
        }).ToArray();
    }
    private async Task DeliverToParticipantAsync(string resourceId, ITransactionalResource resource, (int Entry, int Slot, string ResourceId)[] items, IReadOnlyList<Entry> entries, Exception?[][] failures,
        Func<ITransactionalResource, CancellationToken, Task<ITransactionalResource>>? reopen, SemaphoreSlim reopenGate, CancellationTokenSource stop)
    {
        var ct = stop.Token;
        try
        {
            foreach (var item in items)
            {
                var entry = entries[item.Entry];
                var backoff = s_initialDeliveryBackoff;
                while (true)
                {
                    bool acknowledged;
                    try { acknowledged = await SendDecisionAsync(resource, entry, ct).ConfigureAwait(false); }
                    catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
                    {
                        if (reopen is null) { failures[item.Entry][item.Slot] = ex; break; }
                        await Task.Delay(backoff, ct).ConfigureAwait(false);
                        backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, s_maxDeliveryBackoff.Ticks));
                        resource = await ReopenAsync(resourceId, resource, reopen, reopenGate, ct).ConfigureAwait(false);
                        continue;
                    }
                    if (!acknowledged) { throw new InvalidDataException($"A participant contradicted the irrevocable {entry.Decision!.ToLowerInvariant()} decision."); }
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await stop.CancelAsync().ConfigureAwait(false);
            throw;
        }
    }
    private static CommitResult DecisionOf(Entry entry) => entry.Decision == "Commit" ? CommitResult.Committed : CommitResult.Aborted;
    private static async Task<bool> SendDecisionAsync(ITransactionalResource resource, Entry entry, CancellationToken ct)
        => entry.Decision == "Commit"
            ? await resource.CommitAsync(new RequestId(entry.Id), ct).ConfigureAwait(false) == CommitResult.Committed
            : await resource.AbortAsync(new RequestId(entry.Id), ct).ConfigureAwait(false) != AbortResult.AlreadyCommitted;
    // A participant that poisoned itself cannot accept the retry; the caller-owned hook supplies the
    // instance to retry against (it may return the same one). A failing hook is retried after the next
    // backoff, but a replacement with another identity would deliver the decision to the wrong participant.
    // Hook calls and registration updates run one at a time even while participants are driven concurrently.
    private async Task<ITransactionalResource> ReopenAsync(string resourceId, ITransactionalResource failed, Func<ITransactionalResource, CancellationToken, Task<ITransactionalResource>> reopen, SemaphoreSlim reopenGate, CancellationToken ct)
    {
        await reopenGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ITransactionalResource? replacement;
            try { replacement = await reopen(failed, ct).ConfigureAwait(false); }
            catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested)) { return failed; }
            if (replacement is null || !string.Equals(replacement.ResourceId, resourceId, StringComparison.Ordinal))
            { throw new InvalidOperationException($"The reopen hook must return the participant '{resourceId}'."); }
            _resources[resourceId] = replacement;
            return replacement;
        }
        finally { reopenGate.Release(); }
    }
    private void Apply(Entry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Id) || entry.Plan is null || entry.Plan.Length == 0 || entry.Decision is not (null or "Commit" or "Abort") || entry.Plan.Any(part => !_resources.ContainsKey(part.ResourceId)))
        { throw new InvalidDataException("Invalid coordinator journal record or missing participant."); }
        entry = entry with { Plan = entry.Plan.Select(part => part with { Writes = TransactionData.Copy(part.Writes) }).ToArray() };
        if (_transactions.TryGetValue(entry.Id, out var previous))
        {
            if (previous.Decision is not null || entry.Decision is null || TransactionData.Fingerprint(previous.Plan) != TransactionData.Fingerprint(entry.Plan))
            { throw new InvalidDataException("Coordinator intent or final decision changed."); }
        }
        else if (entry.Decision is not null) { throw new InvalidDataException("Decision lacks durable intent."); }
        _transactions[entry.Id] = entry;
    }
    private void EnsureUsable() { ObjectDisposedException.ThrowIf(_disposed != 0, this); _journal.EnsureUsable(); }
    /// <summary>Idempotently closes the owned decision journal. Borrowed resources remain open; disposal makes no decision. Dispose waits for a call in progress to finish; calls queued behind it, and later calls, throw <see cref="ObjectDisposedException"/>.</summary>
    /// <remarks>Dispose waits as long as the call in progress does, so a call stuck on a participant holds Dispose too, until the
    /// participant answers or the call's caller cancels. The gate is never disposed: it allocates no kernel handle, and a disposed
    /// semaphore neither wakes its waiters nor accepts the holder's release. A participant or reopen hook must not dispose the
    /// coordinator that is calling it.</remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) { return; }
        _gate.Wait();
        try { _journal.Dispose(); } finally { _gate.Release(); }
    }
}

/// <summary>A durably logged commit or abort decision could not be delivered to every participant in this call.</summary>
/// <remarks>The decision is irrevocable and already in the coordinator journal; the named participants still hold their
/// prepared locks. Do not infer Abort from this exception: read <see cref="Decision"/>. The coordinator keeps sending to
/// the participant instances registered with it; only a reopen hook rebinds one. To redeliver after a participant poisoned
/// itself, either retry the original ID on this coordinator with a reopen hook that returns the reopened participant, or
/// dispose this coordinator, reopen the participants, create a coordinator over the same journal with the reopened
/// instances and call <c>RecoverAsync</c>. <c>RecoverAsync</c> or a hookless retry on this coordinator still targets the
/// poisoned instance. <see cref="Exception.InnerException"/> carries the first delivery failure.</remarks>
public sealed class TransactionDeliveryException : IOException
{
    internal TransactionDeliveryException(string transactionId, CommitResult decision, IReadOnlyList<string> undeliveredResourceIds, Exception innerException)
        : base($"Transaction '{transactionId}' durably decided {decision}, but {undeliveredResourceIds.Count} participant(s) did not acknowledge it: {string.Join(", ", undeliveredResourceIds)}.", innerException)
    { TransactionId = transactionId; Decision = decision; UndeliveredResourceIds = undeliveredResourceIds; }
    /// <summary>The transaction whose decision is durable.</summary>
    public string TransactionId { get; }
    /// <summary>The durable, irrevocable decision that every participant must still receive.</summary>
    public CommitResult Decision { get; }
    /// <summary>Participant identities that did not acknowledge the decision, in plan order.</summary>
    public IReadOnlyList<string> UndeliveredResourceIds { get; }
}
