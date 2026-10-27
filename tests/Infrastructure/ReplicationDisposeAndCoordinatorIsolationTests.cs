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
using System.Xml.Linq;
using Boutquin.Storage.Distributed.Interfaces.Transactions;

namespace Boutquin.Storage.Infrastructure.Tests;

// Two interleavings no single-call test reaches. (1) Dispose racing an operation that holds the
// replication gate: the holder must finish normally and every caller already queued on the gate must
// observe ObjectDisposedException rather than wait forever. (2) A two-phase-commit call stuck
// delivering to a participant that never answers: the coordinator is serialized, so every other call
// waits until the stuck call completes or its caller cancels, and the documentation must say so.
// Shares a collection with the other gate-blocking suite: both park thread-pool threads inside a
// gate, and running them side by side can starve a holder past the 5 s bound.
[Collection("GateBlockingDisposeTests")]
public sealed class ReplicationDisposeAndCoordinatorIsolationTests : IDisposable
{
    private static readonly TimeSpan s_bound = TimeSpan.FromSeconds(5);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "repl-iso-" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _opened = [];

    // ---- Dispose while an operation holds the gate ----

    public static TheoryData<string> SingleLeaderQueuedCalls() => ["WriteAsync", "ReadAsync", "SyncFollowerAsync", "AddFollower", "RemoveFollower", "GetReplicationLag"];

    [Theory]
    [MemberData(nameof(SingleLeaderQueuedCalls))]
    public async Task SingleLeader_DisposeWhileAWriteHoldsTheGate_QueuedCallThrowsObjectDisposed(string call)
    {
        var hold = new Hold();
        var replication = new SingleLeaderReplication<HoldKey, int>();
        replication.AddFollower("f");
        var inflight = Task.Run(() => replication.WriteAsync(new HoldKey(1, hold), 1));
        hold.Entered.Wait(s_bound).Should().BeTrue("the write is inside the gate");
        Task queued = call switch
        {
            "WriteAsync" => replication.WriteAsync(new HoldKey(2, null), 2),
            "ReadAsync" => replication.ReadAsync(new HoldKey(2, null)),
            "SyncFollowerAsync" => replication.SyncFollowerAsync("f"),
            "AddFollower" => Task.Run(() => replication.AddFollower("g")),
            "RemoveFollower" => Task.Run(() => replication.RemoveFollower("f")),
            _ => Task.Run(() => replication.GetReplicationLag()),
        };
        await WaitUntilQueuedAsync(queued).ConfigureAwait(true);

        replication.Dispose();
        hold.Release.Set();

        var outcome = await Record.ExceptionAsync(() => queued.WaitAsync(s_bound)).ConfigureAwait(true);
        outcome.Should().BeOfType<ObjectDisposedException>($"a {call} queued behind the holder must observe the disposal, not wait forever");
        await inflight.WaitAsync(s_bound).ConfigureAwait(true);
    }

    [Fact]
    public async Task SingleLeader_DisposeWhileAWriteHoldsTheGate_TheInFlightWriteCompletes()
    {
        var hold = new Hold();
        var replication = new SingleLeaderReplication<HoldKey, int>();
        var inflight = Task.Run(() => replication.WriteAsync(new HoldKey(1, hold), 1));
        hold.Entered.Wait(s_bound).Should().BeTrue("the write is inside the gate");

        replication.Dispose();
        hold.Release.Set();

        var outcome = await Record.ExceptionAsync(() => inflight.WaitAsync(s_bound)).ConfigureAwait(true);
        outcome.Should().BeNull("a write that was admitted before Dispose has been applied and must not be reported as failed");
    }

    public static TheoryData<string> QuorumQueuedCalls() => ["ReadAsync", "WriteAsync", "SetReplicaAvailability"];

    [Theory]
    [MemberData(nameof(QuorumQueuedCalls))]
    public async Task Quorum_DisposeWhileAWriteHoldsTheGate_QueuedCallThrowsObjectDisposed(string call)
    {
        var hold = new Hold();
        var quorum = new QuorumReplication<HoldKey, int>(3, 2, 2);
        var inflight = Task.Run(() => quorum.WriteAsync(new HoldKey(1, hold), 1));
        hold.Entered.Wait(s_bound).Should().BeTrue("the write is inside the gate");
        Task queued = call switch
        {
            "ReadAsync" => quorum.ReadAsync(new HoldKey(1, null)),
            "WriteAsync" => quorum.WriteAsync(new HoldKey(2, null), 2),
            _ => Task.Run(() => quorum.SetReplicaAvailability("replica-0", false)),
        };
        await WaitUntilQueuedAsync(queued).ConfigureAwait(true);

        quorum.Dispose();
        hold.Release.Set();

        var outcome = await Record.ExceptionAsync(() => queued.WaitAsync(s_bound)).ConfigureAwait(true);
        outcome.Should().BeOfType<ObjectDisposedException>($"a {call} queued behind the holder must observe the disposal, not wait forever");
        await inflight.WaitAsync(s_bound).ConfigureAwait(true);
    }

    [Fact]
    public async Task Quorum_DisposeWhileAWriteHoldsTheGate_TheInFlightWriteCompletes()
    {
        var hold = new Hold();
        var quorum = new QuorumReplication<HoldKey, int>(3, 2, 2);
        var inflight = Task.Run(() => quorum.WriteAsync(new HoldKey(1, hold), 1));
        hold.Entered.Wait(s_bound).Should().BeTrue("the write is inside the gate");

        quorum.Dispose();
        hold.Release.Set();

        var outcome = await Record.ExceptionAsync(() => inflight.WaitAsync(s_bound)).ConfigureAwait(true);
        outcome.Should().BeNull("a write already fanned out to W replicas must not be reported as failed");
    }

    // A queued call has passed its entry check and parked on the gate; give it a moment to get there.
    private static async Task WaitUntilQueuedAsync(Task queued)
    {
        await Task.Delay(50).ConfigureAwait(true);
        queued.IsCompleted.Should().BeFalse("the call is queued behind the holder");
    }

    // Blocks the first hash of the key inside the gate until the test releases it, so the holder's
    // critical section is held open deterministically rather than by a slow key.
    private sealed class Hold
    {
        private int _armed = 1;
        public ManualResetEventSlim Entered { get; } = new();
        public ManualResetEventSlim Release { get; } = new();
        public void Block()
        {
            if (Interlocked.Exchange(ref _armed, 0) == 1)
            {
                Entered.Set();
                Release.Wait(TimeSpan.FromSeconds(30));
            }
        }
    }

    private readonly record struct HoldKey(int Value, Hold? Gate) : IComparable<HoldKey>
    {
        public int CompareTo(HoldKey other) => Value.CompareTo(other.Value);
        public bool Equals(HoldKey other) => Value == other.Value;
        public override int GetHashCode()
        {
            Gate?.Block();
            return Value;
        }
    }

    // ---- Two-phase commit: one stuck call holds the whole coordinator ----

    // Characterization of the documented contract: the coordinator serializes its calls, so while x is
    // stuck delivering to h (which never answers), an unrelated transaction over z and RecoverAsync both
    // wait; they proceed as soon as x's caller cancels.
    [Fact]
    public async Task Coordinator_CallStuckDeliveringToAnUnresponsiveParticipant_HoldsBackOtherCallsUntilItsCallerCancels()
    {
        var h = new NeverAnswersDecision(await OpenAsync("h")); var z = await OpenAsync("z");
        var coordinator = await TwoPhaseCommitCoordinator.CreateAsync(FilePath("coordinator"), [h, z]).ConfigureAwait(true);
        _opened.Add(coordinator);
        using var stuckCaller = new CancellationTokenSource();
        var x = coordinator.ExecuteAsync(new RequestId("x"), [new("h", Writes("k", "x"))], null, stuckCaller.Token);
        await h.DecisionRequested.Task.WaitAsync(s_bound).ConfigureAwait(true);

        var y = coordinator.ExecuteAsync(new RequestId("y"), [new("z", Writes("k", "y"))]);
        using var recoverCaller = new CancellationTokenSource();
        var recover = coordinator.RecoverAsync(recoverCaller.Token);
        await Task.Delay(200).ConfigureAwait(true);
        y.IsCompleted.Should().BeFalse("y waits on the coordinator while x is stuck delivering");
        recover.IsCompleted.Should().BeFalse("recovery waits on the coordinator while x is stuck delivering");

        await stuckCaller.CancelAsync().ConfigureAwait(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => x).ConfigureAwait(true);
        (await y.WaitAsync(s_bound).ConfigureAwait(true)).Should().Be(CommitResult.Committed);
        (await z.ReadAsync("k").ConfigureAwait(true)).Should().Be("y");
        recover.IsCompleted.Should().BeFalse("recovery resends x's commit to h, which never answers");
        await recoverCaller.CancelAsync().ConfigureAwait(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recover.WaitAsync(s_bound)).ConfigureAwait(true);
    }

    [Fact]
    public void Docs_TwoPhaseCommitConcurrencyClaims_StateThatAStuckCallBlocksEveryOtherCoordinatorCall()
    {
        const string Overclaim = "delays only the decisions addressed to it";
        const string Scope = "blocks every other ExecuteAsync and RecoverAsync call on the coordinator until it completes or its caller cancels";
        var guide = Normalize(File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "TRANSACTIONS.md")));
        var typeRemarks = Normalize(XmlDocOf("T:Boutquin.Storage.Distributed.Transactions.TwoPhaseCommitCoordinator"));
        var recoverRemarks = Normalize(XmlDocOf("M:Boutquin.Storage.Distributed.Interfaces.Transactions.ITwoPhaseCommitCoordinator.RecoverAsync("));

        foreach (var (name, text) in new[] { ("TRANSACTIONS.md", guide), ("TwoPhaseCommitCoordinator remarks", typeRemarks), ("RecoverAsync remarks", recoverRemarks) })
        {
            text.Contains(Overclaim, StringComparison.Ordinal).Should().BeFalse($"{name} must not claim cross-call isolation the serialized coordinator does not provide");
            text.Contains(Scope, StringComparison.Ordinal).Should().BeTrue($"{name} must state that a stuck call holds the whole coordinator");
        }
    }

    // The coordinator forwards the caller's token to the participant and waits for the participant call
    // to return; it never stops waiting on its own. Every site that tells the caller to bound delivery
    // with a token must say that this works only through a participant that observes it.
    [Fact]
    public void Docs_TwoPhaseCommitCancellationClaims_StateThatOnlyATokenObservingParticipantReleasesTheCall()
    {
        const string Qualifier = "a participant that ignores cancellation holds the coordinator until it answers";
        var guide = Normalize(File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "TRANSACTIONS.md")));
        var typeRemarks = Normalize(XmlDocOf("T:Boutquin.Storage.Distributed.Transactions.TwoPhaseCommitCoordinator"));
        var recoverRemarks = Normalize(XmlDocOf("M:Boutquin.Storage.Distributed.Interfaces.Transactions.ITwoPhaseCommitCoordinator.RecoverAsync("));

        foreach (var (name, text) in new[] { ("TRANSACTIONS.md", guide), ("TwoPhaseCommitCoordinator remarks", typeRemarks), ("RecoverAsync remarks", recoverRemarks) })
        {
            text.Contains(Qualifier, StringComparison.Ordinal).Should().BeTrue($"{name} must not present the caller's token as sufficient to release a call stuck on a participant");
        }
    }

    // Characterization of the qualified contract: with a participant that never answers and ignores the
    // token, canceling the caller does not release the call, and an unrelated call stays queued behind it.
    [Fact]
    public async Task Coordinator_CallerCancelsWhileTheParticipantIgnoresTheToken_TheCallStaysHeldUntilTheParticipantAnswers()
    {
        var h = new IgnoresTokenDecision(await OpenAsync("h")); var z = await OpenAsync("z");
        var coordinator = await TwoPhaseCommitCoordinator.CreateAsync(FilePath("coordinator"), [h, z]).ConfigureAwait(true);
        _opened.Add(coordinator);
        using var stuckCaller = new CancellationTokenSource();
        var x = coordinator.ExecuteAsync(new RequestId("x"), [new("h", Writes("k", "x"))], null, stuckCaller.Token);
        await h.DecisionRequested.Task.WaitAsync(s_bound).ConfigureAwait(true);
        var y = coordinator.ExecuteAsync(new RequestId("y"), [new("z", Writes("k", "y"))]);

        await stuckCaller.CancelAsync().ConfigureAwait(true);
        await Task.Delay(300).ConfigureAwait(true);
        x.IsCompleted.Should().BeFalse("cancellation reaches the call only through the participant, which ignores it");
        y.IsCompleted.Should().BeFalse("the unrelated call stays queued behind the held coordinator");

        h.Answer.TrySetResult();
        await Record.ExceptionAsync(() => x.WaitAsync(s_bound)).ConfigureAwait(true);
        (await y.WaitAsync(s_bound).ConfigureAwait(true)).Should().Be(CommitResult.Committed);
    }

    private static string Normalize(string text) => Regex.Replace(text.Replace("`", string.Empty, StringComparison.Ordinal), @"\s+", " ");

    private static string XmlDocOf(string memberPrefix)
    {
        var member = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Boutquin.Storage.Distributed.xml")).Descendants("member")
            .Single(m => ((string?)m.Attribute("name"))!.StartsWith(memberPrefix, StringComparison.Ordinal));
        return string.Concat(member.Elements().Select(e => e.Value));
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Boutquin.Storage.slnx")))
        {
            directory = directory.Parent;
        }
        directory.Should().NotBeNull("the test runs inside the repository checkout");
        return directory!.FullName;
    }

    private static Dictionary<string, string?> Writes(string key, string value) => new() { [key] = value };

    private string FilePath(string name) => Path.Combine(_directory, name + ".log");

    private async Task<DurableTransactionalResource> OpenAsync(string id)
    {
        var resource = await DurableTransactionalResource.CreateAsync(id, FilePath(id)).ConfigureAwait(true);
        _opened.Add(resource);
        return resource;
    }

    // Votes through the wrapped participant but never answers a commit or abort until the caller cancels.
    private sealed class NeverAnswersDecision(ITransactionalResource inner) : ITransactionalResource
    {
        public TaskCompletionSource DecisionRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string ResourceId => inner.ResourceId;
        public Task<PrepareResult> PrepareAsync(RequestId id, IReadOnlyDictionary<string, string?> writes, CancellationToken ct = default) => inner.PrepareAsync(id, writes, ct);
        public async Task<CommitResult> CommitAsync(RequestId id, CancellationToken ct = default)
        {
            DecisionRequested.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(true);
            return CommitResult.Committed;
        }
        public async Task<AbortResult> AbortAsync(RequestId id, CancellationToken ct = default)
        {
            DecisionRequested.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(true);
            return AbortResult.Aborted;
        }
    }

    // Votes through the wrapped participant, then answers a commit or abort only when the test says so,
    // never observing the caller's token.
    private sealed class IgnoresTokenDecision(ITransactionalResource inner) : ITransactionalResource
    {
        public TaskCompletionSource DecisionRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string ResourceId => inner.ResourceId;
        public Task<PrepareResult> PrepareAsync(RequestId id, IReadOnlyDictionary<string, string?> writes, CancellationToken ct = default) => inner.PrepareAsync(id, writes, ct);
        public async Task<CommitResult> CommitAsync(RequestId id, CancellationToken ct = default)
        {
            DecisionRequested.TrySetResult();
            await Answer.Task.ConfigureAwait(true);
            return await inner.CommitAsync(id, CancellationToken.None).ConfigureAwait(true);
        }
        public async Task<AbortResult> AbortAsync(RequestId id, CancellationToken ct = default)
        {
            DecisionRequested.TrySetResult();
            await Answer.Task.ConfigureAwait(true);
            return await inner.AbortAsync(id, CancellationToken.None).ConfigureAwait(true);
        }
    }

    public void Dispose()
    {
        for (var i = _opened.Count - 1; i >= 0; i--) { _opened[i].Dispose(); }
        if (Directory.Exists(_directory)) { Directory.Delete(_directory, true); }
    }
}
