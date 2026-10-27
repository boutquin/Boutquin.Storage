# Transactions, durable messaging, and isolation comparisons

The transaction components live in the non-packable `Boutquin.Storage.Distributed`
assembly. They are reference implementations consumed by project reference.
Unlike the existing in-memory MVCC and snapshot-isolation stores, the two-phase
participants, coordinator, and message store below replay real file-backed logs.
They remain single-owner components without replication, sockets, authentication,
or automatic coordinator failover.

## Run the examples

```sh
dotnet run --project src/TransactionSample -- 20260917
dotnet test tests/Infrastructure --filter 'FullyQualifiedName~TransactionRecoveryTests|FullyQualifiedName~TransactionSimulationTests|FullyQualifiedName~TransactionalMessageTests|FullyQualifiedName~IsolationComparisonTests|FullyQualifiedName~DistributedTransactionTests'
dotnet run -c Release --project benchmarks/Transactions -- --filter '*' --job Dry
```

The sample interrupts commit delivery before or after one participant's effect,
closes and reopens every log, and resolves the recorded decision. It then closes
an outbox and inbox after processing but before delivery acknowledgement, reopens
them, and verifies deduplicated redelivery. Finally it executes the difference
between read committed and strict two-phase locking. An unsigned seed selects the
commit interruption boundary. Any failed assertion exits unsuccessfully.

The four benchmark cases measure uncontended read-committed and strict-locking
transactions, real two-participant fsync boundaries, and atomic outbox records.
The first two run 100 in-memory transactions per invocation; the durable cases
run 10 and include file startup and cleanup. Compare like-for-like cases and
account for those batch sizes. Dry is an executable smoke check, not a stable
performance estimate.

## Durable two-phase commit

`ITransactionalResource` exposes prepare, commit and abort. The concrete
`DurableTransactionalResource` owns a string-key write set with null representing
deletion. `ITwoPhaseCommitCoordinator` and `TwoPhaseCommitCoordinator` bind stable
participant IDs to those resources. `RequestId` is a nonblank ordinal identity;
the default struct is invalid. Durable text must be well-formed Unicode: lone
UTF-16 surrogates are rejected before JSON replacement could change data or alias
distinct intents. Valid Unicode, including supplementary characters, is retained.

```csharp
using var west = await DurableTransactionalResource.CreateAsync("west", "west.log");
using var east = await DurableTransactionalResource.CreateAsync("east", "east.log");
using var coordinator = await TwoPhaseCommitCoordinator.CreateAsync(
    "decisions.log", [west, east]);

var transfer = new RequestId("account-command:42");
TransactionIntent[] plan =
[
    new("west", new Dictionary<string, string?> { ["balance"] = "90" }),
    new("east", new Dictionary<string, string?> { ["balance"] = "110" })
];
CommitResult result = await coordinator.ExecuteAsync(transfer, plan);
```

Imports are `Boutquin.Storage.Distributed.Transactions`; interfaces are under
`Boutquin.Storage.Distributed.Interfaces.Transactions`. The example sets explicit
balances; deriving balances safely requires application validation and isolation
around the read/modify/write intent. Two-phase commit alone does not validate an
unlocked earlier read or supply serializable distributed transactions.

The order is observable and intentional:

1. The coordinator fsyncs a Start record containing the complete immutable plan.
2. Each participant checks conflicts, fsyncs its Prepared record, and retains
   exclusive locks on all intended write keys. Prepared values remain private.
3. If every participant votes yes, the coordinator fsyncs Commit. A no vote causes
   a durably recorded Abort. A commit is never dispatched before decision fsync
   acknowledges success.
4. Participants fsync their own final record, then publish the whole local write
   set or discard it and release locks. The coordinator returns after every
   participant acknowledges the same decision.

Identical retries return the existing outcome. Reusing a transaction ID with a
changed participant set or changed writes fails. Write keys are canonicalized
ordinally on both initial capture and replay, independent of current culture.

### Atomic outcome is distinct from cross-shard visibility

A durable Commit decision cannot later turn into Abort. Once reachable, every
prepared participant must converge on that decision. An Abort decision publishes
none of the planned writes. This is atomic **final outcome**.

Commit delivery is not simultaneous: the coordinator sends the decision to each
participant independently. A direct read of one participant can see its new
values while another participant still has its old values and prepared locks.
No simultaneous cross-shard read visibility, global snapshot, or serializable
distributed read is supplied. A caller may
wait for the coordinator's successful completion before reading both shards;
concurrent unrelated transactions can still affect such separate reads.

### Blocking and recovery

A prepared participant never unilaterally times out and releases its locks. It
cannot distinguish a coordinator that crashed before deciding from one that
committed and lost the reply. Another transaction preparing the same key receives
a durable no vote. This is the availability cost of two-phase commit.

After reopening the same participant and coordinator logs, call `RecoverAsync()`.
It durably aborts every recorded Start without a decision, then redelivers all
known decisions idempotently. It also redelivers decisions that previously
completed; terminal participant responses make that safe. `CreateAsync` replays
state but does not contact participants. An identical `ExecuteAsync` retry can
resume an undecided attempt; recovery deliberately chooses Abort for undecided
starts.

A participant prepare that fails or times out is treated as a no vote: the
coordinator durably records Abort and sends it to every participant in the same
call, so siblings that already voted yes release their locks at once.

Once a commit or abort decision is logged it is irrevocable, and every
participant is sent it, even when another one fails or never answers. Within one
call, calls into one participant are made one at a time, in journal order, and
distinct participants are driven concurrently, so a participant that does not
answer holds back only its own decisions in that call. The coordinator itself is
serialized: a call stuck delivering to a participant that does not answer
blocks every other `ExecuteAsync` and `RecoverAsync` call on the coordinator
until it completes or its caller cancels, so pass a cancellation token that
bounds delivery. Cancellation reaches a stuck call only through the
participant: the coordinator forwards the token and waits for the participant
call to return, so a participant that ignores cancellation holds the
coordinator until it answers. What happens to a failed delivery depends on the optional
`reopen` hook passed to `ExecuteAsync`:

- **With a hook**, the coordinator retries the failed request with capped
  exponential backoff (10 ms doubling to 1 s) until it succeeds or the caller
  cancels, as two-phase commit requires after the commit point (DDIA 2e p. 326).
  Before each retry it calls the hook with the participant that failed; the hook
  returns the participant to retry against: a freshly reopened instance for one
  that poisoned itself, or the same instance for a transient transport failure.
  The returned participant must carry the same `ResourceId` and replaces the
  registration. The hook runs under the coordinator's serialization, one call at
  a time, must not call back into it, and owns the lifetime of any instance it
  opens. A failing hook is retried after the next backoff.
- **Without a hook**, the call fails at once with `TransactionDeliveryException`
  (an `IOException`). Its `Decision` names the durable outcome and
  `UndeliveredResourceIds` the participants still holding prepared locks. The
  decision stays in the journal. The coordinator keeps sending to the
  participant instances registered with it, and only a `reopen` hook rebinds one:
  either retry the original ID with a `reopen` hook that returns the reopened
  participant, or dispose the coordinator, reopen the participants, create a
  coordinator over the same journal with them and call `RecoverAsync()`. Do not
  infer Abort from this exception.

A canceled call can leave a prepared transaction, a recorded decision, or a
completed effect whose acknowledgement was lost. Resolve the original ID.
`RecoverAsync()` takes no hook and does not retry in-call. It attempts every
transaction and reports the undelivered ones together after the pass (one
`TransactionDeliveryException`, or an `AggregateException` of them). A
participant that never answers does not stop the other participants from
receiving their decisions; the pass then completes only when that participant
answers or the caller cancels, holding the coordinator until then, and canceling
leaves its decisions to the next recovery (as above, a participant that ignores
cancellation holds the coordinator until it answers). Any journal append exception poisons that instance conservatively:
dispose and reopen before further operations, because the durable outcome may
differ from its in-memory state.

Each state machine holds an exclusive `.owner` file handle. This prevents two
live owners from independently deciding against the same log; the event log's
per-append lock alone would not protect read/decide/write state. Keep participant
IDs, journal paths, and the sole owning coordinator stable across recovery.
Do not delete decision logs, release prepared locks manually, or let another
coordinator issue contradictory terminal commands. The owner lock file itself
is retained rather than unlinked while another owner could be waiting.

## One-record outbox, inbox, and idempotency

`TransactionalMessageStore` implements `ITransactionalMessageOutbox`,
`IInboxDedupStore`, and `IIdempotencyStore`. Its business state is part of this
store: it cannot atomically include an arbitrary write to a separate LSM store,
database, file, HTTP endpoint or payment system.

A `MessageMutation` contains business writes, outbound `OutboxMessage` values,
and a cached response. A single fsynced event record includes **all of those plus
the request ID, operation kind and intent**. Recovery applies that record in full.
There is no independent checkpoint write between processing and deduplication.

```csharp
using var sender = await TransactionalMessageStore.CreateAsync("sender.log");
await sender.CommitAsync(new RequestId("pay-order:42"), "payment-v1",
    new MessageMutation(
        new Dictionary<string, string?> { ["order:42"] = "paid" },
        [new OutboxMessage(new RequestId("order-paid:42"), "orders", "paid-v1")],
        "accepted"));

using var receiver = await TransactionalMessageStore.CreateAsync("receiver.log");
foreach (var message in await sender.PendingMessagesAsync())
{
    await receiver.ProcessAsync(message.Id, message.Payload, state =>
        new MessageMutation(
            new Dictionary<string, string?> { ["received:42"] = "paid" },
            [], "processed"));
    await sender.AcknowledgeAsync(message.Id);
}
```

`PendingMessagesAsync` returns an owned snapshot in commit order. Delivery is
at least once. If the receiver commits and the sender crashes before fsyncing its
acknowledgement, the message remains pending and is delivered again. The receiver
returns its cached response without rerunning the handler. Thus local processing
is effectively once for a retained identity, while the transport can duplicate.

`ExecuteAsync` supplies the same atomic effect/result boundary for general
idempotent commands. Inbox, outbox and idempotency requests share one ordinal
identity namespace in each store; operation-kind reuse is rejected. Outbound
message identities have their own uniqueness set and cannot be reused even after
acknowledgement. Direct outbox commits compare both the intent label and actual
mutation contents. Handler operations compare the caller's input/intent string;
include a handler/schema version when changing semantics.

Handlers receive an immutable snapshot and must be pure mutation builders.
They run under the store's gate, must not re-enter it, and must not perform
external effects. The handler can run again if its record was never committed;
only effects encoded in the returned mutation are in the atomic boundary.
External effects need an idempotent receiver or a shared transaction of their own.
`ICheckpointStore` alone cannot establish this guarantee, because its offset and
an independently written effect would be separate commits.

## Isolation comparison

| Module | Read behavior | Held locks | What it does not provide |
|---|---|---|---|
| `ReadCommittedStore<TKey,TValue>` | Own staged changes or latest committed value per call | Exclusive write locks until completion | Repeatable reads, prevention of stale read/modify/write updates |
| `StrictTwoPhaseLockingStore<TKey,TValue>` | Own writes or a committed value under a shared lock, including absent-key reads | Shared and exclusive point-key locks until completion | Predicate/range protection, fairness |
| Existing `MvccStore` / `SnapshotIsolationStore` | Version/snapshot visibility and optimistic point-key conflict checks | Their existing optimistic bookkeeping | These new modules do not change their semantics |

Both comparison modules keep staged writes private and publish a transaction's
local write set atomically. They are in-memory teaching models, not durable
`ITransactionalResource` implementations. They expose Begin, Read, Write, Delete,
Commit and Abort through `IIsolationComparisonStore<TKey,TValue>`.

Strict locking upgrades retain the original read lock while waiting. Both
modules detect deadlocks: when a lock wait would close a wait-for cycle, or a
lock grant gives an already-waiting transaction a new blocker that closes one, the
transaction whose check finds the cycle is aborted as the victim. Its locks are released, its
staged writes are discarded, and the pending operation fails with
`TransactionDeadlockException`; the other transactions in the cycle proceed.
Acknowledge the victim with `AbortAsync` and retry the work in a new
transaction. Cancellation removes a wait but retains locks already held by the
transaction. Commit or abort releases them. Waiting transactions recheck
availability after wakeup; scheduling is not FIFO and starvation is possible.
Disposing a comparison store discards its state and wakes waiters with a disposed
error. Generic keys and values must remain immutable while stored.

## Storage and verification boundary

Durable modules use `AppendOnlyEventLog`: framed JSON, CRC protection, gap-free
positions, durable creation and fsync before acknowledgement. Its existing torn-tail
recovery and mid-log corruption rejection remain in force. Modules retain all
identities, decisions, messages and responses; there is no compaction or schema
migration layer. Startup scans retained history. Dispose waits for the call in
progress to finish, then closes owned files; every call queued behind it, and every
later call, throws `ObjectDisposedException`. Disposal does not decide transactions
and does not acknowledge messages, and it must not be called from inside a handler or
a participant call the same instance is running, which it would wait on forever. The
coordinator borrows participants and never disposes them.

Tests inject failures before and after real journal append/fsync boundaries and
reopen the files, including uncertain participant commit and outbox acknowledgement.
Seeded tests transport actual prepare/commit/abort requests and responses through
the reusable simulated network with delay, loss, duplicate delivery and partitions.
They check final outcomes after healing; they do not pretend a partition guarantees
progress. These are deterministic fault-boundary tests, not a claim of exercised
machine power loss, replicated disaster recovery, or production deployment readiness.
