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

public sealed class TransactionRecoveryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "txn-recovery-" + Guid.NewGuid().ToString("N"));
    private string FilePath(string name) => Path.Combine(_directory, name + ".log");
    private static TransactionIntent[] Plan => [new("a", new Dictionary<string, string?> { ["x"] = "a" }), new("b", new Dictionary<string, string?> { ["x"] = "b" })];

    [Theory]
    [InlineData("Start", false, false)]
    [InlineData("Start", true, false)]
    [InlineData("Decision", false, false)]
    [InlineData("Decision", true, true)]
    public async Task CoordinatorCrash_ReplaysDurableDecisionOrAbortsUndecided(string boundary, bool after, bool committed)
    {
        var id = new RequestId("txn");
        using (var a = await DurableTransactionalResource.CreateAsync("a", FilePath("a")).ConfigureAwait(true))
        using (var b = await DurableTransactionalResource.CreateAsync("b", FilePath("b")).ConfigureAwait(true))
        using (var coordinator = await TwoPhaseCommitCoordinator.CreateAsync(FilePath("coordinator"), [a, b]).ConfigureAwait(true))
        {
            coordinator.FaultInjector = (stage, persisted) => { if (stage == boundary && persisted == after) { throw new IOException("crash"); } };
            await Assert.ThrowsAsync<IOException>(() => coordinator.ExecuteAsync(id, Plan)).ConfigureAwait(true);
            (await a.ReadAsync("x").ConfigureAwait(true)).Should().BeNull("no commit is sent until decision append acknowledges durability");
            (await b.ReadAsync("x").ConfigureAwait(true)).Should().BeNull();
        }
        using var recoveredA = await DurableTransactionalResource.CreateAsync("a", FilePath("a")).ConfigureAwait(true);
        using var recoveredB = await DurableTransactionalResource.CreateAsync("b", FilePath("b")).ConfigureAwait(true);
        using var recovered = await TwoPhaseCommitCoordinator.CreateAsync(FilePath("coordinator"), [recoveredA, recoveredB]).ConfigureAwait(true);
        await recovered.RecoverAsync().ConfigureAwait(true);
        await recovered.RecoverAsync().ConfigureAwait(true);
        (await recoveredA.ReadAsync("x").ConfigureAwait(true)).Should().Be(committed ? "a" : null);
        (await recoveredB.ReadAsync("x").ConfigureAwait(true)).Should().Be(committed ? "b" : null);
    }

    [Theory]
    [InlineData("Prepare", false)]
    [InlineData("Prepare", true)]
    [InlineData("Commit", false)]
    [InlineData("Commit", true)]
    public async Task ParticipantCrash_RestoresPreparedLocksAndResolvesUncertainCommit(string boundary, bool after)
    {
        var id = new RequestId("txn");
        using (var a = await DurableTransactionalResource.CreateAsync("a", FilePath("a")).ConfigureAwait(true))
        using (var b = await DurableTransactionalResource.CreateAsync("b", FilePath("b")).ConfigureAwait(true))
        using (var coordinator = await TwoPhaseCommitCoordinator.CreateAsync(FilePath("coordinator"), [a, b]).ConfigureAwait(true))
        {
            b.FaultInjector = (stage, persisted) => { if (stage == boundary && persisted == after) { throw new IOException("crash"); } };
            (await Assert.ThrowsAsync<TransactionDeliveryException>(() => coordinator.ExecuteAsync(id, Plan)).ConfigureAwait(true))
                .Decision.Should().Be(boundary == "Commit" ? CommitResult.Committed : CommitResult.Aborted, "the decision is durable even though one participant missed it");
            (await a.ReadAsync("x").ConfigureAwait(true)).Should().Be(boundary == "Commit" ? "a" : null,
                "separate shard reads can observe partial delivery of a committed decision");
        }
        using var recoveredA = await DurableTransactionalResource.CreateAsync("a", FilePath("a")).ConfigureAwait(true);
        using var recoveredB = await DurableTransactionalResource.CreateAsync("b", FilePath("b")).ConfigureAwait(true);
        using var recovered = await TwoPhaseCommitCoordinator.CreateAsync(FilePath("coordinator"), [recoveredA, recoveredB]).ConfigureAwait(true);
        await recovered.RecoverAsync().ConfigureAwait(true);
        var outcome = await recovered.ExecuteAsync(id, Plan).ConfigureAwait(true);
        outcome.Should().Be(boundary == "Commit" ? CommitResult.Committed : CommitResult.Aborted);
        (await recoveredA.ReadAsync("x").ConfigureAwait(true)).Should().Be(boundary == "Commit" ? "a" : null);
        (await recoveredB.ReadAsync("x").ConfigureAwait(true)).Should().Be(boundary == "Commit" ? "b" : null);
    }

    [Fact]
    public async Task PreparedLocks_SurviveReopenAndPreventConflictingPrepareUntilDecision()
    {
        var id = new RequestId("prepared");
        var writes = new Dictionary<string, string?> { ["x"] = "private" };
        using (var first = await DurableTransactionalResource.CreateAsync("a", FilePath("a")).ConfigureAwait(true))
        {
            (await first.PrepareAsync(id, writes).ConfigureAwait(true)).Should().Be(PrepareResult.Prepared);
            (await first.ReadAsync("x").ConfigureAwait(true)).Should().BeNull();
            await Assert.ThrowsAsync<IOException>(() => DurableTransactionalResource.CreateAsync("a", FilePath("a"))).ConfigureAwait(true);
        }
        using var recovered = await DurableTransactionalResource.CreateAsync("a", FilePath("a")).ConfigureAwait(true);
        (await recovered.PrepareAsync(new RequestId("conflict"), writes).ConfigureAwait(true)).Should().Be(PrepareResult.Rejected);
        (await recovered.AbortAsync(id).ConfigureAwait(true)).Should().Be(AbortResult.Aborted);
        (await recovered.PrepareAsync(new RequestId("later"), writes).ConfigureAwait(true)).Should().Be(PrepareResult.Prepared);
        (await recovered.CommitAsync(new RequestId("later")).ConfigureAwait(true)).Should().Be(CommitResult.Committed);
        (await recovered.AbortAsync(new RequestId("later")).ConfigureAwait(true)).Should().Be(AbortResult.AlreadyCommitted);
        (await recovered.ReadAsync("x").ConfigureAwait(true)).Should().Be("private");
    }
    [Fact]
    public async Task UnicodeWriteIntent_RemainsIdenticalAcrossDurableReplay()
    {
        var priorCulture = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        try
        {
            var id = new RequestId("unicode");
            TransactionIntent[] plan = [new("a", new Dictionary<string, string?> { ["z"] = "last-ascii", ["ä"] = "accent" })];
            using (var resource = await DurableTransactionalResource.CreateAsync("a", FilePath("a")).ConfigureAwait(true))
            using (var coordinator = await TwoPhaseCommitCoordinator.CreateAsync(FilePath("coordinator"), [resource]).ConfigureAwait(true))
            { await coordinator.ExecuteAsync(id, plan).ConfigureAwait(true); }
            using var recovered = await DurableTransactionalResource.CreateAsync("a", FilePath("a")).ConfigureAwait(true);
            using var recoveredCoordinator = await TwoPhaseCommitCoordinator.CreateAsync(FilePath("coordinator"), [recovered]).ConfigureAwait(true);
            (await recoveredCoordinator.ExecuteAsync(id, plan).ConfigureAwait(true)).Should().Be(CommitResult.Committed);
            (await recovered.PrepareAsync(id, plan[0].Writes).ConfigureAwait(true)).Should().Be(PrepareResult.Committed);
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = priorCulture; }
    }

    // Precomposed and decomposed A-ring compare equal under en-US but are distinct ordinal keys;
    // the replayed plan must fingerprint exactly as the captured one, so an identical retry after
    // a coordinator restart returns the recorded outcome instead of reporting reused intent.
    [Fact]
    public async Task CoordinatorIdenticalRetryAfterReopen_WithCultureEqualOrdinalDistinctKeys_ReturnsRecordedOutcome()
    {
        var priorCulture = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        try
        {
            var id = new RequestId("culture-equal");
            TransactionIntent[] plan = [new("a", new Dictionary<string, string?> { ["Å"] = "1", ["Å"] = "2" })];
            using (var resource = await DurableTransactionalResource.CreateAsync("a", FilePath("a")).ConfigureAwait(true))
            using (var coordinator = await TwoPhaseCommitCoordinator.CreateAsync(FilePath("coordinator"), [resource]).ConfigureAwait(true))
            { (await coordinator.ExecuteAsync(id, plan).ConfigureAwait(true)).Should().Be(CommitResult.Committed); }
            using var recovered = await DurableTransactionalResource.CreateAsync("a", FilePath("a")).ConfigureAwait(true);
            using var recoveredCoordinator = await TwoPhaseCommitCoordinator.CreateAsync(FilePath("coordinator"), [recovered]).ConfigureAwait(true);
            var retry = () => recoveredCoordinator.ExecuteAsync(id, plan);
            (await retry.Should().NotThrowAsync().ConfigureAwait(true)).Which.Should().Be(CommitResult.Committed);
            (await recovered.ReadAsync("Å").ConfigureAwait(true)).Should().Be("1");
            (await recovered.ReadAsync("Å").ConfigureAwait(true)).Should().Be("2");
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = priorCulture; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AbortDispatchCrash_PreservesNoVoteOutcomeAndReleasesPreparedKeys(bool after)
    {
        var id = new RequestId("aborted");
        using (var a = await DurableTransactionalResource.CreateAsync("a", FilePath("a")).ConfigureAwait(true))
        using (var b = await DurableTransactionalResource.CreateAsync("b", FilePath("b")).ConfigureAwait(true))
        using (var coordinator = await TwoPhaseCommitCoordinator.CreateAsync(FilePath("coordinator"), [a, b]).ConfigureAwait(true))
        {
            await b.PrepareAsync(new RequestId("blocker"), new Dictionary<string, string?> { ["x"] = "blocked" }).ConfigureAwait(true);
            a.FaultInjector = (stage, persisted) => { if (stage == "Abort" && persisted == after) { throw new IOException("abort delivery interrupted"); } };
            (await Assert.ThrowsAsync<TransactionDeliveryException>(() => coordinator.ExecuteAsync(id, Plan)).ConfigureAwait(true))
                .Decision.Should().Be(CommitResult.Aborted, "the decision is durable even though one participant missed it");
        }
        using var recoveredA = await DurableTransactionalResource.CreateAsync("a", FilePath("a")).ConfigureAwait(true);
        using var recoveredB = await DurableTransactionalResource.CreateAsync("b", FilePath("b")).ConfigureAwait(true);
        using var recovered = await TwoPhaseCommitCoordinator.CreateAsync(FilePath("coordinator"), [recoveredA, recoveredB]).ConfigureAwait(true);
        await recovered.RecoverAsync().ConfigureAwait(true);
        (await recovered.ExecuteAsync(id, Plan).ConfigureAwait(true)).Should().Be(CommitResult.Aborted);
        (await recoveredA.ReadAsync("x").ConfigureAwait(true)).Should().BeNull();
        (await recoveredB.ReadAsync("x").ConfigureAwait(true)).Should().BeNull();
        (await recoveredA.PrepareAsync(new RequestId("new"), Plan[0].Writes).ConfigureAwait(true)).Should().Be(PrepareResult.Prepared);
    }

    [Fact]
    public async Task CancellationAfterDecision_CannotTurnCommitIntoAbort()
    {
        using var a = await DurableTransactionalResource.CreateAsync("a", FilePath("a")).ConfigureAwait(true);
        using var b = await DurableTransactionalResource.CreateAsync("b", FilePath("b")).ConfigureAwait(true);
        using var coordinator = await TwoPhaseCommitCoordinator.CreateAsync(FilePath("coordinator"), [a, b]).ConfigureAwait(true);
        using var cancellation = new CancellationTokenSource();
        coordinator.FaultInjector = (stage, persisted) => { if (stage == "Decision" && persisted) { cancellation.Cancel(); } };
        var id = new RequestId("canceled-after-decision");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.ExecuteAsync(id, Plan, ct: cancellation.Token)).ConfigureAwait(true);
        coordinator.FaultInjector = null;
        await coordinator.RecoverAsync().ConfigureAwait(true);
        (await coordinator.ExecuteAsync(id, Plan).ConfigureAwait(true)).Should().Be(CommitResult.Committed);
        (await a.ReadAsync("x").ConfigureAwait(true)).Should().Be("a");
        (await b.ReadAsync("x").ConfigureAwait(true)).Should().Be("b");
    }

    [Fact]
    public async Task ParticipantIdentityReuse_RejectsDifferentIntentAndUnknownCommit()
    {
        using var resource = await DurableTransactionalResource.CreateAsync("a", FilePath("a")).ConfigureAwait(true);
        var id = new RequestId("same");
        await resource.PrepareAsync(id, new Dictionary<string, string?> { ["x"] = "one" }).ConfigureAwait(true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => resource.PrepareAsync(id, new Dictionary<string, string?> { ["x"] = "two" })).ConfigureAwait(true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => resource.CommitAsync(new RequestId("unknown"))).ConfigureAwait(true);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => resource.CommitAsync(default)).ConfigureAwait(true);
    }

    // DDIA 2PL commit: if any prepare request fails or times out, the coordinator sends abort to
    // all participants. A crashed participant's prepare must therefore durably decide Abort in
    // the same call — the sibling that already voted yes is released at once, and the durable
    // decision stays Abort even when nobody calls RecoverAsync before the identical retry.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrepareFailure_DurablyDecidesAbortAndReleasesPreparedSibling(bool after)
    {
        var id = new RequestId("txn");
        using (var a = await DurableTransactionalResource.CreateAsync("a", FilePath("a")).ConfigureAwait(true))
        using (var b = await DurableTransactionalResource.CreateAsync("b", FilePath("b")).ConfigureAwait(true))
        using (var coordinator = await TwoPhaseCommitCoordinator.CreateAsync(FilePath("coordinator"), [a, b]).ConfigureAwait(true))
        {
            b.FaultInjector = (stage, persisted) => { if (stage == "Prepare" && persisted == after) { throw new IOException("crash"); } };
            (await Assert.ThrowsAsync<TransactionDeliveryException>(() => coordinator.ExecuteAsync(id, Plan)).ConfigureAwait(true))
                .Decision.Should().Be(CommitResult.Aborted, "the decision is durable even though one participant missed it");
            (await a.PrepareAsync(new RequestId("conflict"), Plan[0].Writes).ConfigureAwait(true)).Should().Be(PrepareResult.Prepared,
                "the abort sent after the failed prepare releases the sibling's prepared lock without recovery");
            await a.AbortAsync(new RequestId("conflict")).ConfigureAwait(true);
        }
        using var reopenedA = await DurableTransactionalResource.CreateAsync("a", FilePath("a")).ConfigureAwait(true);
        using var reopenedB = await DurableTransactionalResource.CreateAsync("b", FilePath("b")).ConfigureAwait(true);
        using var reopened = await TwoPhaseCommitCoordinator.CreateAsync(FilePath("coordinator"), [reopenedA, reopenedB]).ConfigureAwait(true);
        (await reopened.ExecuteAsync(id, Plan).ConfigureAwait(true)).Should().Be(CommitResult.Aborted,
            "a failed prepare is a no vote: the durable decision is Abort, so the identical retry cannot commit");
        (await reopenedA.ReadAsync("x").ConfigureAwait(true)).Should().BeNull();
        (await reopenedB.ReadAsync("x").ConfigureAwait(true)).Should().BeNull();
    }

    // A prepare that times out on a participant which is otherwise reachable: the abort reaches
    // every participant, so the call itself returns the final Aborted decision.
    [Fact]
    public async Task PrepareTimeout_AbortsAllParticipantsAndReturnsAborted()
    {
        var id = new RequestId("timeout");
        using var a = await DurableTransactionalResource.CreateAsync("a", FilePath("a")).ConfigureAwait(true);
        using var b = await DurableTransactionalResource.CreateAsync("b", FilePath("b")).ConfigureAwait(true);
        using var coordinator = await TwoPhaseCommitCoordinator.CreateAsync(FilePath("coordinator"), [a, new PrepareTimesOut(b)]).ConfigureAwait(true);
        var execute = () => coordinator.ExecuteAsync(id, Plan);
        (await execute.Should().NotThrowAsync("a timed-out prepare is a no vote, not an uncertain outcome").ConfigureAwait(true))
            .Which.Should().Be(CommitResult.Aborted);
        (await a.PrepareAsync(new RequestId("later"), Plan[0].Writes).ConfigureAwait(true)).Should().Be(PrepareResult.Prepared);
        (await b.PrepareAsync(id, Plan[1].Writes).ConfigureAwait(true)).Should().Be(PrepareResult.Rejected, "the participant received the abort decision");
    }

    private sealed class PrepareTimesOut(ITransactionalResource inner) : ITransactionalResource
    {
        public string ResourceId => inner.ResourceId;
        public Task<PrepareResult> PrepareAsync(RequestId id, IReadOnlyDictionary<string, string?> writes, CancellationToken ct = default)
            => throw new TimeoutException("prepare request timed out");
        public Task<CommitResult> CommitAsync(RequestId id, CancellationToken ct = default) => inner.CommitAsync(id, ct);
        public Task<AbortResult> AbortAsync(RequestId id, CancellationToken ct = default) => inner.AbortAsync(id, ct);
    }

    // CRC-valid journal frames with forged content: replay must fail with the documented
    // InvalidDataException, never a NullReferenceException or an argument exception.
    public sealed record ForgedPart(string? ResourceId, SortedDictionary<string, string?>? Writes);
    public sealed record ForgedCoordinatorEntry(string Id, ForgedPart?[] Plan, string? Decision);

    public static TheoryData<string> ForgedCoordinatorShapes => ["null-part", "null-resource-id", "null-writes", "blank-key"];

    [Theory]
    [MemberData(nameof(ForgedCoordinatorShapes))]
    public async Task CoordinatorReplay_ForgedPlanRecord_ThrowsInvalidData(string shape)
    {
        ForgedPart? part = shape switch
        {
            "null-part" => null,
            "null-resource-id" => new ForgedPart(null, new SortedDictionary<string, string?> { ["x"] = "1" }),
            "null-writes" => new ForgedPart("a", null),
            _ => new ForgedPart("a", new SortedDictionary<string, string?> { [" "] = "1" }),
        };
        await ForgeAsync(FilePath("coordinator"), new ForgedCoordinatorEntry("txn", [part], null)).ConfigureAwait(true);
        using var a = await DurableTransactionalResource.CreateAsync("a", FilePath("a")).ConfigureAwait(true);

        await Assert.ThrowsAsync<InvalidDataException>(() => TwoPhaseCommitCoordinator.CreateAsync(FilePath("coordinator"), [a])).ConfigureAwait(true);
    }

    public sealed record ForgedParticipantEntry(string ResourceId, string Id, string State, SortedDictionary<string, string?>? Writes);

    [Theory]
    [InlineData("committed-null-writes")]
    [InlineData("prepared-blank-key")]
    [InlineData("committed-blank-key")]
    public async Task ParticipantReplay_ForgedRecord_ThrowsInvalidData(string shape)
    {
        var prepared = new ForgedParticipantEntry("a", "txn", "Prepared", new SortedDictionary<string, string?> { ["x"] = "1" });
        ForgedParticipantEntry[] records = shape switch
        {
            "committed-null-writes" => [prepared, prepared with { State = "Committed", Writes = null }],
            "prepared-blank-key" => [prepared with { Writes = new SortedDictionary<string, string?> { [" "] = "1" } }],
            _ => [prepared, prepared with { State = "Committed", Writes = new SortedDictionary<string, string?> { [" "] = "1" } }],
        };
        await ForgeAsync(FilePath("a"), records).ConfigureAwait(true);

        await Assert.ThrowsAsync<InvalidDataException>(() => DurableTransactionalResource.CreateAsync("a", FilePath("a"))).ConfigureAwait(true);
    }

    // Commit publishes exactly the durably prepared write set; a commit record carrying other
    // writes is a forged transition, not a value to apply.
    [Fact]
    public async Task ParticipantReplay_CommitWritesDifferingFromPreparedIntent_ThrowsInvalidData()
    {
        var prepared = new ForgedParticipantEntry("a", "txn", "Prepared", new SortedDictionary<string, string?> { ["x"] = "1" });
        await ForgeAsync(FilePath("a"), prepared, prepared with { State = "Committed", Writes = new SortedDictionary<string, string?> { ["x"] = "2" } }).ConfigureAwait(true);

        await Assert.ThrowsAsync<InvalidDataException>(() => DurableTransactionalResource.CreateAsync("a", FilePath("a"))).ConfigureAwait(true);
    }

    public sealed record ForgedMessage(RequestId Id, string Topic, string Payload);
    public sealed record ForgedMutation(Dictionary<string, string?> Writes, ForgedMessage?[] Messages, string? Result);
    public sealed record ForgedMessageEntry(string Id, string Kind, string Intent, ForgedMutation? Mutation);

    [Theory]
    [InlineData("null-message")]
    [InlineData("null-result")]
    [InlineData("duplicate-message-id")]
    public async Task MessageStoreReplay_ForgedMutation_ThrowsInvalidData(string shape)
    {
        var message = new ForgedMessage(new RequestId("m1"), "topic", "payload");
        var mutation = shape switch
        {
            "null-message" => new ForgedMutation([], [null], "ok"),
            "null-result" => new ForgedMutation([], [message], null),
            _ => new ForgedMutation([], [message, message], "ok"),
        };
        await ForgeAsync(FilePath("messages"), new ForgedMessageEntry("req", "Outbox", "intent", mutation)).ConfigureAwait(true);

        await Assert.ThrowsAsync<InvalidDataException>(() => TransactionalMessageStore.CreateAsync(FilePath("messages"))).ConfigureAwait(true);
    }

    private static async Task ForgeAsync<T>(string path, params T[] events)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var log = await Boutquin.Storage.Infrastructure.Events.AppendOnlyEventLog<T>.CreateAsync(path).ConfigureAwait(true);
        foreach (var record in events) { await log.AppendAsync(record).ConfigureAwait(true); }
    }

    public void Dispose() { if (Directory.Exists(_directory)) { Directory.Delete(_directory, true); } }
}
