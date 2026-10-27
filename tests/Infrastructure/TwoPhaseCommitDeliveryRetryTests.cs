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
using Boutquin.Storage.Distributed.Interfaces.Transactions;

namespace Boutquin.Storage.Infrastructure.Tests;

// DDIA 2PC: after the commit point the coordinator retries commit or abort requests until they
// succeed. With a reopen hook the coordinator does that in-call, reopening a participant that
// poisoned itself; without one it fails fast with a typed exception and the decision stays durable.
public sealed class TwoPhaseCommitDeliveryRetryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "txn-delivery-" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _opened = [];
    private string FilePath(string name) => Path.Combine(_directory, name + ".log");
    private static TransactionIntent[] Plan => [new("a", new Dictionary<string, string?> { ["x"] = "a" }), new("b", new Dictionary<string, string?> { ["x"] = "b" })];

    private async Task<DurableTransactionalResource> OpenAsync(string id)
    {
        var resource = await DurableTransactionalResource.CreateAsync(id, FilePath(id)).ConfigureAwait(true);
        _opened.Add(resource);
        return resource;
    }

    // Reopens a durable participant from its own journal, as an operator-supplied hook would.
    private Func<ITransactionalResource, CancellationToken, Task<ITransactionalResource>> ReopenHook(List<string> calls) => async (failed, ct) =>
    {
        calls.Add(failed.ResourceId);
        ((IDisposable)failed).Dispose();
        return await OpenAsync(failed.ResourceId).ConfigureAwait(true);
    };

    [Fact]
    public async Task CommitDeliveryFailure_WithReopenHook_ReopensPoisonedParticipantAndCommitsInCall()
    {
        var a = await OpenAsync("a"); var b = await OpenAsync("b");
        using var coordinator = await TwoPhaseCommitCoordinator.CreateAsync(FilePath("coordinator"), [a, b]).ConfigureAwait(true);
        b.FaultInjector = (stage, persisted) => { if (stage == "Commit" && !persisted) { throw new IOException("commit delivery lost"); } };
        var calls = new List<string>();

        var execute = () => coordinator.ExecuteAsync(new RequestId("txn"), Plan, ReopenHook(calls));

        (await execute.Should().NotThrowAsync("a failed delivery after the commit point is retried until it succeeds").ConfigureAwait(true))
            .Which.Should().Be(CommitResult.Committed);
        calls.Should().Equal(["b"], "only the participant whose delivery failed is reopened");
        var reopenedB = (DurableTransactionalResource)_opened[^1];
        (await reopenedB.ReadAsync("x").ConfigureAwait(true)).Should().Be("b");
        (await a.ReadAsync("x").ConfigureAwait(true)).Should().Be("a");
        (await reopenedB.PrepareAsync(new RequestId("later"), Plan[1].Writes).ConfigureAwait(true)).Should().Be(PrepareResult.Prepared,
            "the delivered commit released the participant's prepared lock");
    }

    [Fact]
    public async Task CommitDeliveryFailure_WithoutHook_FailsFastWithTypedExceptionAndDecisionStaysDurable()
    {
        var id = new RequestId("txn");
        var a = await OpenAsync("a"); var b = await OpenAsync("b");
        using (var coordinator = await TwoPhaseCommitCoordinator.CreateAsync(FilePath("coordinator"), [a, b]).ConfigureAwait(true))
        {
            b.FaultInjector = (stage, persisted) => { if (stage == "Commit" && !persisted) { throw new IOException("commit delivery lost"); } };
            var failure = await Assert.ThrowsAsync<TransactionDeliveryException>(() => coordinator.ExecuteAsync(id, Plan)).ConfigureAwait(true);
            failure.TransactionId.Should().Be("txn");
            failure.Decision.Should().Be(CommitResult.Committed, "the commit decision was durably logged before delivery");
            failure.UndeliveredResourceIds.Should().Equal(["b"]);
            failure.InnerException.Should().BeOfType<IOException>().Which.Message.Should().Be("commit delivery lost");
        }
        a.Dispose(); b.Dispose();
        var reopenedA = await OpenAsync("a"); var reopenedB = await OpenAsync("b");
        using var recovered = await TwoPhaseCommitCoordinator.CreateAsync(FilePath("coordinator"), [reopenedA, reopenedB]).ConfigureAwait(true);
        await recovered.RecoverAsync().ConfigureAwait(true);
        (await reopenedB.ReadAsync("x").ConfigureAwait(true)).Should().Be("b", "caller-driven recovery still redelivers the durable commit");
    }

    [Fact]
    public async Task TransientDeliveryFailures_WithHookReturningSameParticipant_AreRetriedUntilAcknowledged()
    {
        var a = await OpenAsync("a"); var b = await OpenAsync("b");
        var flaky = new FlakyCommit(b, failures: 3);
        using var coordinator = await TwoPhaseCommitCoordinator.CreateAsync(FilePath("coordinator"), [a, flaky]).ConfigureAwait(true);
        var hookCalls = 0;

        var execute = () => coordinator.ExecuteAsync(new RequestId("txn"), Plan, (failed, _) => { hookCalls++; return Task.FromResult(failed); });

        (await execute.Should().NotThrowAsync("transient delivery failures are retried with backoff").ConfigureAwait(true))
            .Which.Should().Be(CommitResult.Committed);
        flaky.CommitCalls.Should().Be(4, "three failed deliveries are each retried once more");
        hookCalls.Should().Be(3, "the hook is consulted before every retry");
        (await b.ReadAsync("x").ConfigureAwait(true)).Should().Be("b");
    }

    [Fact]
    public async Task PrepareFailure_WithHook_DeliversDurableAbortToEveryParticipantInCall()
    {
        var a = await OpenAsync("a"); var b = await OpenAsync("b");
        using var coordinator = await TwoPhaseCommitCoordinator.CreateAsync(FilePath("coordinator"), [a, b]).ConfigureAwait(true);
        b.FaultInjector = (stage, persisted) => { if (stage == "Prepare" && persisted) { throw new IOException("prepare reply lost"); } };
        var calls = new List<string>();

        var execute = () => coordinator.ExecuteAsync(new RequestId("txn"), Plan, ReopenHook(calls));

        (await execute.Should().NotThrowAsync("a failed prepare is a no vote and the abort is retried until delivered").ConfigureAwait(true))
            .Which.Should().Be(CommitResult.Aborted);
        calls.Should().Equal(["b"]);
        var reopenedB = (DurableTransactionalResource)_opened[^1];
        (await reopenedB.PrepareAsync(new RequestId("later"), Plan[1].Writes).ConfigureAwait(true)).Should().Be(PrepareResult.Prepared,
            "the delivered abort released the poisoned participant's durable prepared lock");
        (await a.PrepareAsync(new RequestId("later"), Plan[0].Writes).ConfigureAwait(true)).Should().Be(PrepareResult.Prepared);
    }

    [Fact]
    public async Task PrepareFailure_WithoutHook_ReportsDurableAbortThroughTypedException()
    {
        var a = await OpenAsync("a"); var b = await OpenAsync("b");
        using var coordinator = await TwoPhaseCommitCoordinator.CreateAsync(FilePath("coordinator"), [a, b]).ConfigureAwait(true);
        b.FaultInjector = (stage, persisted) => { if (stage == "Prepare" && persisted) { throw new IOException("prepare reply lost"); } };

        var failure = await Assert.ThrowsAsync<TransactionDeliveryException>(() => coordinator.ExecuteAsync(new RequestId("txn"), Plan)).ConfigureAwait(true);

        failure.Decision.Should().Be(CommitResult.Aborted);
        failure.UndeliveredResourceIds.Should().Equal(["b"]);
        (await a.PrepareAsync(new RequestId("later"), Plan[0].Writes).ConfigureAwait(true)).Should().Be(PrepareResult.Prepared,
            "the reachable sibling still received the abort in the same call");
    }

    [Fact]
    public async Task CancellationDuringRetry_StopsRetryingAndKeepsDecisionDurable()
    {
        var id = new RequestId("txn");
        var a = await OpenAsync("a"); var b = await OpenAsync("b");
        var unreachable = new FlakyCommit(b, failures: int.MaxValue);
        using (var cancellation = new CancellationTokenSource())
        using (var coordinator = await TwoPhaseCommitCoordinator.CreateAsync(FilePath("coordinator"), [a, unreachable]).ConfigureAwait(true))
        {
            var execute = coordinator.ExecuteAsync(id, Plan, (failed, _) =>
            {
                if (unreachable.CommitCalls >= 3) { cancellation.Cancel(); }
                return Task.FromResult(failed);
            }, cancellation.Token);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execute).ConfigureAwait(true);
            unreachable.CommitCalls.Should().BeGreaterThanOrEqualTo(3);
        }
        a.Dispose(); b.Dispose();
        var reopenedA = await OpenAsync("a"); var reopenedB = await OpenAsync("b");
        using var recovered = await TwoPhaseCommitCoordinator.CreateAsync(FilePath("coordinator"), [reopenedA, reopenedB]).ConfigureAwait(true);
        (await recovered.ExecuteAsync(id, Plan).ConfigureAwait(true)).Should().Be(CommitResult.Committed, "cancellation cannot undo a logged commit");
        (await reopenedB.ReadAsync("x").ConfigureAwait(true)).Should().Be("b");
    }

    [Fact]
    public async Task ReopenHookReturningDifferentIdentity_IsRejected()
    {
        var a = await OpenAsync("a"); var b = await OpenAsync("b");
        var flaky = new FlakyCommit(b, failures: 1);
        using var coordinator = await TwoPhaseCommitCoordinator.CreateAsync(FilePath("coordinator"), [a, flaky]).ConfigureAwait(true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ExecuteAsync(new RequestId("txn"), Plan, (_, _) => Task.FromResult<ITransactionalResource>(a))).ConfigureAwait(true);
    }

    // Recovery aborts every undecided start before any delivery and attempts every transaction, so one
    // unreachable participant cannot strand an unrelated undecided start's prepared locks.
    [Fact]
    public async Task Recovery_UndeliverableEarlierTransaction_StillAbortsLaterUndecidedStartAndReleasesItsLocks()
    {
        var a = await OpenAsync("a"); var b = await OpenAsync("b"); var c = await OpenAsync("c");
        using var cancellation = new CancellationTokenSource();
        using var coordinator = await TwoPhaseCommitCoordinator.CreateAsync(FilePath("coordinator"), [a, b, new CancelAfterPrepare(c, cancellation)]).ConfigureAwait(true);
        b.FaultInjector = (stage, persisted) => { if (stage == "Commit" && !persisted) { throw new IOException("b unreachable"); } };
        await Assert.ThrowsAsync<TransactionDeliveryException>(() => coordinator.ExecuteAsync(new RequestId("t1"), Plan)).ConfigureAwait(true);
        var t2 = new TransactionIntent[] { new("c", new Dictionary<string, string?> { ["y"] = "c" }) };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.ExecuteAsync(new RequestId("t2"), t2, null, cancellation.Token)).ConfigureAwait(true);

        var failure = await Assert.ThrowsAsync<TransactionDeliveryException>(() => coordinator.RecoverAsync()).ConfigureAwait(true);

        failure.TransactionId.Should().Be("t1", "the undeliverable transaction is still reported");
        failure.UndeliveredResourceIds.Should().Equal(["b"]);
        (await c.PrepareAsync(new RequestId("later"), new Dictionary<string, string?> { ["y"] = "z" }).ConfigureAwait(true)).Should().Be(PrepareResult.Prepared,
            "recovery durably aborted the undecided start and delivered that abort despite t1's unreachable participant");
        (await c.PrepareAsync(new RequestId("t2"), t2[0].Writes).ConfigureAwait(true)).Should().Be(PrepareResult.Rejected, "c recorded the abort decision for t2");
    }

    // The documented remedy on the coordinator that raised the failure: only a reopen hook rebinds the
    // poisoned participant, so a hookless RecoverAsync keeps failing and a retry with the hook delivers.
    [Fact]
    public async Task DeliveryFailure_RetryWithHookReturningReopenedParticipant_RedeliversOnSameCoordinator()
    {
        var id = new RequestId("txn");
        var a = await OpenAsync("a"); var b = await OpenAsync("b");
        using var coordinator = await TwoPhaseCommitCoordinator.CreateAsync(FilePath("coordinator"), [a, b]).ConfigureAwait(true);
        b.FaultInjector = (stage, persisted) => { if (stage == "Commit" && !persisted) { throw new IOException("commit delivery lost"); } };
        await Assert.ThrowsAsync<TransactionDeliveryException>(() => coordinator.ExecuteAsync(id, Plan)).ConfigureAwait(true);
        b.Dispose();
        var reopenedB = await OpenAsync("b");

        var recover = await Assert.ThrowsAsync<TransactionDeliveryException>(() => coordinator.RecoverAsync()).ConfigureAwait(true);
        recover.InnerException.Should().BeOfType<ObjectDisposedException>("RecoverAsync cannot rebind the registered participant");
        (await coordinator.ExecuteAsync(id, Plan, (_, _) => Task.FromResult<ITransactionalResource>(reopenedB)).ConfigureAwait(true))
            .Should().Be(CommitResult.Committed);
        (await reopenedB.ReadAsync("x").ConfigureAwait(true)).Should().Be("b");
    }

    // Every undeliverable transaction is reported, not only the first one recovery reached.
    [Fact]
    public async Task Recovery_SeveralUndeliverableTransactions_ReportsEachInJournalOrder()
    {
        var a = await OpenAsync("a"); var b = await OpenAsync("b");
        using var coordinator = await TwoPhaseCommitCoordinator.CreateAsync(FilePath("coordinator"), [a, b]).ConfigureAwait(true);
        b.FaultInjector = (stage, persisted) => { if (stage == "Commit" && !persisted) { throw new IOException("b unreachable"); } };
        await Assert.ThrowsAsync<TransactionDeliveryException>(() => coordinator.ExecuteAsync(new RequestId("t1"), Plan)).ConfigureAwait(true);
        var t3 = new TransactionIntent[] { new("b", new Dictionary<string, string?> { ["w"] = "b" }) };
        await Assert.ThrowsAsync<TransactionDeliveryException>(() => coordinator.ExecuteAsync(new RequestId("t3"), t3)).ConfigureAwait(true);

        var failure = await Assert.ThrowsAsync<AggregateException>(() => coordinator.RecoverAsync()).ConfigureAwait(true);

        failure.InnerExceptions.Should().AllBeOfType<TransactionDeliveryException>();
        failure.InnerExceptions.Cast<TransactionDeliveryException>().Select(inner => (inner.TransactionId, inner.Decision))
            .Should().Equal([("t1", CommitResult.Committed), ("t3", CommitResult.Aborted)]);
    }

    // Prepares through the wrapped participant, then cancels the caller's token: the start stays undecided.
    private sealed class CancelAfterPrepare(ITransactionalResource inner, CancellationTokenSource cancellation) : ITransactionalResource
    {
        public string ResourceId => inner.ResourceId;
        public async Task<PrepareResult> PrepareAsync(RequestId id, IReadOnlyDictionary<string, string?> writes, CancellationToken ct = default)
        {
            var vote = await inner.PrepareAsync(id, writes, ct).ConfigureAwait(true);
            if (id.Value == "t2") { await cancellation.CancelAsync().ConfigureAwait(true); ct.ThrowIfCancellationRequested(); }
            return vote;
        }
        public Task<CommitResult> CommitAsync(RequestId id, CancellationToken ct = default) => inner.CommitAsync(id, ct);
        public Task<AbortResult> AbortAsync(RequestId id, CancellationToken ct = default) => inner.AbortAsync(id, ct);
    }

    private sealed class FlakyCommit(ITransactionalResource inner, int failures) : ITransactionalResource
    {
        public int CommitCalls { get; private set; }
        public string ResourceId => inner.ResourceId;
        public Task<PrepareResult> PrepareAsync(RequestId id, IReadOnlyDictionary<string, string?> writes, CancellationToken ct = default) => inner.PrepareAsync(id, writes, ct);
        public Task<CommitResult> CommitAsync(RequestId id, CancellationToken ct = default)
            => ++CommitCalls <= failures ? throw new IOException("commit request timed out") : inner.CommitAsync(id, ct);
        public Task<AbortResult> AbortAsync(RequestId id, CancellationToken ct = default) => inner.AbortAsync(id, ct);
    }

    public void Dispose()
    {
        foreach (var resource in _opened) { resource.Dispose(); }
        if (Directory.Exists(_directory)) { Directory.Delete(_directory, true); }
    }
}
