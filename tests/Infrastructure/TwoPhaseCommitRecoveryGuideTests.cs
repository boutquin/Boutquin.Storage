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
using System.Text.RegularExpressions;
using Boutquin.Storage.Distributed.Interfaces.Transactions;

namespace Boutquin.Storage.Infrastructure.Tests;

// The operator remedy documented for a TransactionDeliveryException (docs/TRANSACTIONS.md "Blocking and
// recovery"): the coordinator keeps sending to the participant instances registered with it, and only a
// reopen hook rebinds one. These tests execute each documented procedure against the production coordinator
// and pin that the guide states exactly those procedures and not the one that cannot succeed.
public sealed class TwoPhaseCommitRecoveryGuideTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "txn-guide-" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _opened = [];
    private string FilePath(string name) => Path.Combine(_directory, name + ".log");
    private static TransactionIntent[] Plan => [new("a", new Dictionary<string, string?> { ["x"] = "a" }), new("b", new Dictionary<string, string?> { ["x"] = "b" })];

    private async Task<DurableTransactionalResource> OpenAsync(string id)
    {
        var resource = await DurableTransactionalResource.CreateAsync(id, FilePath(id)).ConfigureAwait(true);
        _opened.Add(resource);
        return resource;
    }

    // A commit delivery to b that poisons it after the decision is durable.
    private async Task<(DurableTransactionalResource A, DurableTransactionalResource B, TwoPhaseCommitCoordinator Coordinator, TransactionDeliveryException Failure)> PoisonedCommitAsync(RequestId id)
    {
        var a = await OpenAsync("a"); var b = await OpenAsync("b");
        var coordinator = await TwoPhaseCommitCoordinator.CreateAsync(FilePath("coordinator"), [a, b]).ConfigureAwait(true);
        _opened.Add(coordinator);
        b.FaultInjector = (stage, persisted) => { if (stage == "Commit" && !persisted) { throw new IOException("commit delivery lost"); } };
        var failure = await Assert.ThrowsAsync<TransactionDeliveryException>(() => coordinator.ExecuteAsync(id, Plan)).ConfigureAwait(true);
        failure.UndeliveredResourceIds.Should().Equal(["b"]);
        return (a, b, coordinator, failure);
    }

    // Guide, first remedy: retry the original ID on the same coordinator with a hook returning the reopened participant.
    [Fact]
    public async Task Guide_RetryOriginalIdWithReopenHook_RedeliversTheDecisionAndReleasesTheLocks()
    {
        var id = new RequestId("txn");
        var (_, b, coordinator, _) = await PoisonedCommitAsync(id);
        b.Dispose();
        var reopenedB = await OpenAsync("b");

        var result = await coordinator.ExecuteAsync(id, Plan, (_, _) => Task.FromResult<ITransactionalResource>(reopenedB)).ConfigureAwait(true);

        result.Should().Be(CommitResult.Committed);
        (await reopenedB.ReadAsync("x").ConfigureAwait(true)).Should().Be("b");
        (await reopenedB.PrepareAsync(new RequestId("later"), Plan[1].Writes).ConfigureAwait(true)).Should().Be(PrepareResult.Prepared);
    }

    // Guide, second remedy: dispose the coordinator, reopen the participants, create a coordinator over the same journal, RecoverAsync.
    [Fact]
    public async Task Guide_RecreateCoordinatorOverSameJournalWithReopenedParticipants_RecoverAsyncRedeliversTheDecision()
    {
        var (a, b, coordinator, _) = await PoisonedCommitAsync(new RequestId("txn"));
        coordinator.Dispose();
        a.Dispose(); b.Dispose();
        var reopenedA = await OpenAsync("a"); var reopenedB = await OpenAsync("b");
        using var recovered = await TwoPhaseCommitCoordinator.CreateAsync(FilePath("coordinator"), [reopenedA, reopenedB]).ConfigureAwait(true);

        await recovered.RecoverAsync().ConfigureAwait(true);

        (await reopenedB.ReadAsync("x").ConfigureAwait(true)).Should().Be("b");
        (await reopenedB.PrepareAsync(new RequestId("later"), Plan[1].Writes).ConfigureAwait(true)).Should().Be(PrepareResult.Prepared);
    }

    // The remedy the guide must not state: reopening the participant does not rebind it on the raising coordinator.
    [Fact]
    public async Task ReopenParticipantThenRecoverAsyncOnTheSameCoordinator_StillFails_SoTheGuideMustNotPrescribeIt()
    {
        var id = new RequestId("txn");
        var (_, b, coordinator, _) = await PoisonedCommitAsync(id);
        b.Dispose();
        var reopenedB = await OpenAsync("b");

        var recover = await Assert.ThrowsAsync<TransactionDeliveryException>(() => coordinator.RecoverAsync()).ConfigureAwait(true);
        var retry = await Assert.ThrowsAsync<TransactionDeliveryException>(() => coordinator.ExecuteAsync(id, Plan)).ConfigureAwait(true);

        recover.InnerException.Should().BeOfType<ObjectDisposedException>();
        retry.InnerException.Should().BeOfType<ObjectDisposedException>();
        (await reopenedB.ReadAsync("x").ConfigureAwait(true)).Should().BeNull("the reopened participant never received the commit");
    }

    [Fact]
    public void Guide_BlockingAndRecoverySection_DescribesTheWorkingProceduresAndNotTheOneThatCannotSucceed()
    {
        var section = BlockingAndRecoverySection();

        section.Should().NotContain("reopen those participants, then call `RecoverAsync()`",
            "reopening a participant does not rebind it on the coordinator that raised the failure");
        section.Should().Contain("only a `reopen` hook rebinds one");
        section.Should().Contain("retry the original ID with a `reopen` hook that returns the reopened participant");
        section.Should().Contain("dispose the coordinator, reopen the participants, create a coordinator over the same journal with them and call `RecoverAsync()`");
        section.Should().Contain("attempts every transaction and reports the undelivered ones together after the pass");
    }

    private static string BlockingAndRecoverySection()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Boutquin.Storage.slnx")))
        {
            directory = directory.Parent;
        }
        directory.Should().NotBeNull("the test runs inside the repository checkout");
        var text = Regex.Replace(File.ReadAllText(Path.Combine(directory!.FullName, "docs", "TRANSACTIONS.md")), @"\s+", " ");
        var start = text.IndexOf("### Blocking and recovery", StringComparison.Ordinal);
        var end = text.IndexOf("## One-record outbox", start, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0); end.Should().BeGreaterThan(start);
        return text[start..end];
    }

    public void Dispose()
    {
        foreach (var resource in _opened) { resource.Dispose(); }
        if (Directory.Exists(_directory)) { Directory.Delete(_directory, true); }
    }
}
