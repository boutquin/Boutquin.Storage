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
using System.Globalization;
using Boutquin.Storage.Distributed.Interfaces.Transactions;
using Boutquin.Storage.Distributed.Simulation;
using Boutquin.Storage.Distributed.Transactions;

var seed = args.Length == 0 ? 20260917u : uint.Parse(args[0], CultureInfo.InvariantCulture);
var random = new PropertyTestGenerators(seed);
var afterEffect = random.Next(2) == 0;
var directory = Path.Combine(Path.GetTempPath(), "storage-transactions-demo-" + Guid.NewGuid().ToString("N"));
string FilePath(string name) => Path.Combine(directory, name + ".log");
var transaction = new RequestId("transfer");
TransactionIntent[] plan = [new("a", new Dictionary<string, string?> { ["balance"] = "90" }), new("b", new Dictionary<string, string?> { ["balance"] = "110" })];
try
{
    using (var a = await DurableTransactionalResource.CreateAsync("a", FilePath("a")).ConfigureAwait(false))
    using (var b = await DurableTransactionalResource.CreateAsync("b", FilePath("b")).ConfigureAwait(false))
    using (var coordinator = await TwoPhaseCommitCoordinator.CreateAsync(FilePath("decision"), [a, new InterruptedParticipant(b, afterEffect)]).ConfigureAwait(false))
    {
        var uncertain = false;
        try { await coordinator.ExecuteAsync(transaction, plan).ConfigureAwait(false); }
        catch (TransactionDeliveryException delivery) { uncertain = delivery.Decision == CommitResult.Committed; }
        Require(uncertain, "Without a reopen hook the interrupted delivery must report the durable commit decision.");
        Require(await a.ReadAsync("balance").ConfigureAwait(false) == "90", "The first shard received the irrevocable commit.");
        Require(await b.ReadAsync("balance").ConfigureAwait(false) == (afterEffect ? "110" : null), "Partial delivery must be explicit, not described as simultaneous visibility.");
    }
    using (var a = await DurableTransactionalResource.CreateAsync("a", FilePath("a")).ConfigureAwait(false))
    using (var b = await DurableTransactionalResource.CreateAsync("b", FilePath("b")).ConfigureAwait(false))
    using (var coordinator = await TwoPhaseCommitCoordinator.CreateAsync(FilePath("decision"), [a, b]).ConfigureAwait(false))
    {
        await coordinator.RecoverAsync().ConfigureAwait(false);
        Require(await coordinator.ExecuteAsync(transaction, plan).ConfigureAwait(false) == CommitResult.Committed, "Recovery must honor the durable decision.");
        Require(await a.ReadAsync("balance").ConfigureAwait(false) == "90" && await b.ReadAsync("balance").ConfigureAwait(false) == "110", "Both shards must reach the committed final outcome.");
    }
    using (var a = await DurableTransactionalResource.CreateAsync("a", FilePath("retry-a")).ConfigureAwait(false))
    using (var b = await DurableTransactionalResource.CreateAsync("b", FilePath("retry-b")).ConfigureAwait(false))
    using (var coordinator = await TwoPhaseCommitCoordinator.CreateAsync(FilePath("retry-decision"), [a, new InterruptedParticipant(b, afterEffect)]).ConfigureAwait(false))
    {
        // With a reopen hook the coordinator retries the interrupted delivery in-call. This transport fault leaves
        // the participant healthy, so the hook hands back the same instance; a poisoned one would be reopened here.
        var outcome = await coordinator.ExecuteAsync(transaction, plan, (failed, _) => Task.FromResult(failed)).ConfigureAwait(false);
        Require(outcome == CommitResult.Committed && await b.ReadAsync("balance").ConfigureAwait(false) == "110", "Delivery retry must reach the committed final outcome in-call.");
    }

    var message = new OutboxMessage(new RequestId("order-paid"), "orders", "paid-v1");
    using (var sender = await TransactionalMessageStore.CreateAsync(FilePath("sender")).ConfigureAwait(false))
    using (var receiver = await TransactionalMessageStore.CreateAsync(FilePath("receiver")).ConfigureAwait(false))
    {
        await sender.CommitAsync(new RequestId("pay-order"), "payment-v1",
            new MessageMutation(new Dictionary<string, string?> { ["order"] = "paid" }, [message], "accepted")).ConfigureAwait(false);
        foreach (var delivery in await sender.PendingMessagesAsync().ConfigureAwait(false))
        {
            await receiver.ProcessAsync(delivery.Id, delivery.Payload, _ => new MessageMutation(new Dictionary<string, string?> { ["processed"] = "1" }, [], "processed")).ConfigureAwait(false);
        }
        // Simulated process loss after receiver commit but before sender acknowledgement.
        // The logs are closed and reopened; no in-memory identity set survives this boundary.
    }
    using (var sender = await TransactionalMessageStore.CreateAsync(FilePath("sender")).ConfigureAwait(false))
    using (var receiver = await TransactionalMessageStore.CreateAsync(FilePath("receiver")).ConfigureAwait(false))
    {
        var pending = await sender.PendingMessagesAsync().ConfigureAwait(false);
        Require(pending.Count == 1, "Unacknowledged delivery must be retried.");
        foreach (var delivery in pending)
        {
            var result = await receiver.ProcessAsync(delivery.Id, delivery.Payload, _ => throw new InvalidOperationException("A duplicate must not rerun processing.")).ConfigureAwait(false);
            Require(result == "processed", "Duplicate delivery must replay the cached result.");
            await sender.AcknowledgeAsync(delivery.Id).ConfigureAwait(false);
        }
        Require(await sender.ReadAsync("order").ConfigureAwait(false) == "paid" && await receiver.ReadAsync("processed").ConfigureAwait(false) == "1", "Business state and dedup state must survive replay together.");
        Require((await sender.PendingMessagesAsync().ConfigureAwait(false)).Count == 0, "Acknowledged outbox must be empty.");
    }
    using (var readCommitted = new ReadCommittedStore<string, int>())
    {
        var reader = readCommitted.BeginTransaction(); var writer = readCommitted.BeginTransaction();
        Require(!(await readCommitted.ReadAsync("key", reader).ConfigureAwait(false)).Found, "Initial key must be absent.");
        await readCommitted.WriteAsync("key", 1, writer).ConfigureAwait(false); await readCommitted.CommitAsync(writer).ConfigureAwait(false);
        Require((await readCommitted.ReadAsync("key", reader).ConfigureAwait(false)).Value == 1, "Read committed permits a nonrepeatable read.");
        await readCommitted.CommitAsync(reader).ConfigureAwait(false);
    }
    using (var strict = new StrictTwoPhaseLockingStore<string, int>())
    {
        var reader = strict.BeginTransaction(); var writer = strict.BeginTransaction();
        await strict.ReadAsync("key", reader).ConfigureAwait(false);
        var blocked = strict.WriteAsync("key", 1, writer);
        Require(!blocked.IsCompleted, "Strict two-phase locking must retain the absent-key read lock.");
        await strict.CommitAsync(reader).ConfigureAwait(false); await blocked.ConfigureAwait(false); await strict.CommitAsync(writer).ConfigureAwait(false);
    }
    Console.WriteLine($"seed={seed}; interrupted commit {(afterEffect ? "after" : "before")} participant effect; durable recovery and in-call delivery retry passed");
    Console.WriteLine("Outbox redelivery, durable inbox deduplication, and both isolation comparisons passed.");
    Console.WriteLine("Reference boundaries: single coordinator/participant owners; no simultaneous cross-shard read visibility; external effects require their own atomic or idempotent boundary.");
}
finally { if (Directory.Exists(directory)) { Directory.Delete(directory, true); } }

static void Require(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }

internal sealed class InterruptedParticipant(ITransactionalResource inner, bool afterEffect) : ITransactionalResource
{
    private bool _interrupted;
    public string ResourceId => inner.ResourceId;
    public Task<PrepareResult> PrepareAsync(RequestId id, IReadOnlyDictionary<string, string?> writes, CancellationToken ct = default) => inner.PrepareAsync(id, writes, ct);
    public async Task<CommitResult> CommitAsync(RequestId id, CancellationToken ct = default)
    {
        if (!_interrupted)
        {
            _interrupted = true;
            if (afterEffect) { await inner.CommitAsync(id, ct).ConfigureAwait(false); }
            throw new IOException("Injected coordinator/participant delivery interruption.");
        }
        return await inner.CommitAsync(id, ct).ConfigureAwait(false);
    }
    public Task<AbortResult> AbortAsync(RequestId id, CancellationToken ct = default) => inner.AbortAsync(id, ct);
}
