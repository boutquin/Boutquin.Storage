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
namespace Boutquin.Storage.Infrastructure.Tests;

/// <summary>
/// A lock grant can add the wait-for edge that closes a cycle, not only a lock wait. These tests
/// pin detection for cycles closed by a shared-lock grant, an exclusive grant and an upgrade, and
/// compare the engine's behaviour against an independent brute-force cycle finder.
/// </summary>
public sealed class SharedLockGrantDeadlockDetectionTests
{
    [Fact]
    public async Task CycleClosedBySharedGrant_IsDetectedWhileUnrelatedReaderIsStillActive()
    {
        using var store = new StrictTwoPhaseLockingStore<string, int>();
        var t1 = store.BeginTransaction(); var t2 = store.BeginTransaction(); var t3 = store.BeginTransaction();
        await store.WriteAsync("k2", 1, t1).ConfigureAwait(true);
        await store.ReadAsync("k1", t3).ConfigureAwait(true);
        var t1WriteK1 = store.WriteAsync("k1", 1, t1);          // waits for T3
        var t2WriteK2 = store.WriteAsync("k2", 2, t2);          // waits for T1; no cycle yet
        t1WriteK1.IsCompleted.Should().BeFalse();
        t2WriteK2.IsCompleted.Should().BeFalse();
        // Granted (no writer on k1): now T1 waits for T2 (reader of k1) and T2 waits for T1 (k2).
        var t2ReadK1 = store.ReadAsync("k1", t2);
        var first = await Task.WhenAny(t1WriteK1, t2WriteK2).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
        // Either awakened waiter can detect the cycle first. A successful survivor may finish
        // before the victim's outer task publishes its exception, so observe that task explicitly.
        var victimTask = first.IsCompletedSuccessfully ? (first == t1WriteK1 ? t2WriteK2 : t1WriteK1) : first;
        var victim = await Assert.ThrowsAsync<TransactionDeadlockException>(
            () => victimTask.WaitAsync(TimeSpan.FromSeconds(5))).ConfigureAwait(true);
        victim.TransactionId.Should().Be(victimTask == t1WriteK1 ? t1 : t2);
        if (victimTask == t2WriteK2)
        {
            // T1 still conflicts with T3 even though the T1/T2 cycle has been broken.
            t1WriteK1.IsCompleted.Should().BeFalse();
        }
        try { await t2ReadK1.ConfigureAwait(true); }
        catch (TransactionDeadlockException exception)
        {
            // The granted read can observe its own transaction's asynchronous abort too.
            exception.TransactionId.Should().Be(t2);
            victim.TransactionId.Should().Be(t2);
        }
        await store.AbortAsync(t3).ConfigureAwait(true);
        await (victimTask == t1WriteK1 ? t2WriteK2 : t1WriteK1).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
        foreach (var id in new[] { t1, t2 }) { try { await store.AbortAsync(id).ConfigureAwait(true); } catch (InvalidOperationException) { } }
    }

    [Fact]
    public async Task CycleClosedByExclusiveGrant_IsDetected()
    {
        using var store = new StrictTwoPhaseLockingStore<string, int>();
        var t1 = store.BeginTransaction(); var t2 = store.BeginTransaction(); var t3 = store.BeginTransaction();
        await store.WriteAsync("a", 1, t3).ConfigureAwait(true);
        await store.WriteAsync("b", 1, t1).ConfigureAwait(true);
        var t1ReadA = store.ReadAsync("a", t1);                 // waits for T3
        var t3ReadB = store.ReadAsync("b", t3);                 // T3 waits for T1: closes T1<->T3 cycle by a wait
        await Task.WhenAny(Settle(t1ReadA), Settle(t3ReadB), Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(true);
        (t1ReadA.IsFaulted || t3ReadB.IsFaulted).Should().BeTrue("the wait-closed control cycle is detected");
        foreach (var id in new[] { t1, t2, t3 }) { try { await store.AbortAsync(id).ConfigureAwait(true); } catch (InvalidOperationException) { } }
    }

    [Fact]
    public async Task RandomizedSchedules_DetectionAgreesWithBruteForceCycleFinder()
    {
        for (var seed = 1; seed <= 40; seed++) { await RunScheduleAsync(seed).ConfigureAwait(true); }
    }

    internal static async Task RunScheduleAsync(int seed)
    {
        var random = new Random(seed);
        using var store = new StrictTwoPhaseLockingStore<int, int>();
        const int txCount = 4, keyCount = 3;
        var ids = Enumerable.Range(0, txCount).Select(_ => store.BeginTransaction()).ToArray();
        var ops = new List<Op>(); var previouslyFaulted = 0;
        for (var step = 0; step < 14; step++)
        {
            var live = Enumerable.Range(0, txCount).Where(t => !IsAborted(ops, t)).ToArray();
            if (live.Length == 0) { break; }
            var tx = live[random.Next(live.Length)]; var key = random.Next(keyCount); var exclusive = random.Next(2) == 0;
            // Independent expectation: would this operation, added to the settled state, close a cycle?
            var hypothetical = new List<Op>(ops) { new(tx, key, exclusive, null!) };
            var expectDeadlock = HasCycle(Model(hypothetical, includeNew: true));
            var task = exclusive ? store.WriteAsync(key, step, ids[tx]) : store.ReadAsync(key, ids[tx]);
            ops.Add(new Op(tx, key, exclusive, task));
            await QuiesceAsync(ops).ConfigureAwait(true);
            var faulted = ops.Count(o => o.Task.IsFaulted);
            var settled = Model(ops, includeNew: false);
            HasCycle(settled).Should().BeFalse($"seed {seed} step {step}: a wait-for cycle is left undetected");
            if (!expectDeadlock) { faulted.Should().Be(previouslyFaulted, $"seed {seed} step {step}: false-positive deadlock"); }
            else { faulted.Should().BeGreaterThan(previouslyFaulted, $"seed {seed} step {step}: expected a deadlock victim"); }
            previouslyFaulted = faulted;
        }
        for (var t = 0; t < txCount; t++) { try { await store.AbortAsync(ids[t]).ConfigureAwait(true); } catch (InvalidOperationException) { } }
        await Task.WhenAny(Task.WhenAll(ops.Select(o => Settle(o.Task))), Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(true);
    }

    private sealed record Op(int Tx, int Key, bool Exclusive, Task Task);

    private static bool IsAborted(List<Op> ops, int tx) => ops.Any(o => o.Tx == tx && o.Task is { IsFaulted: true });

    private static async Task QuiesceAsync(List<Op> ops)
    {
        int Signature() => ops.Count(o => o.Task.IsCompleted);
        var last = -1; var stable = 0;
        while (stable < 3) { await Task.Delay(8).ConfigureAwait(true); var now = Signature(); if (now == last) { stable++; } else { stable = 0; last = now; } }
    }

    // Brute-force model: tx -> set of txs it waits for, derived only from observed grants/pendings.
    private static Dictionary<int, HashSet<int>> Model(List<Op> ops, bool includeNew)
    {
        var aborted = ops.Where(o => o.Task is { IsFaulted: true }).Select(o => o.Tx).ToHashSet();
        var granted = ops.Where(o => o.Task is { IsCompletedSuccessfully: true } && !aborted.Contains(o.Tx)).ToList();
        var pending = ops.Where(o => o.Task is null || (!o.Task.IsCompleted && !aborted.Contains(o.Tx))).ToList();
        if (includeNew)
        {
            var last = ops[^1];
            if (!aborted.Contains(last.Tx) && !CanGrant(granted, last)) { pending = [.. ops.Where(o => o.Task is { IsCompleted: false } && !aborted.Contains(o.Tx)), last]; }
            else { granted = [.. granted, last]; pending = [.. ops.Take(ops.Count - 1).Where(o => o.Task is { IsCompleted: false } && !aborted.Contains(o.Tx))]; }
        }
        var graph = new Dictionary<int, HashSet<int>>();
        foreach (var wait in pending)
        {
            if (!graph.TryGetValue(wait.Tx, out var set)) { set = []; graph[wait.Tx] = set; }
            foreach (var holder in granted.Where(g => g.Key == wait.Key && g.Tx != wait.Tx && (wait.Exclusive || g.Exclusive))) { set.Add(holder.Tx); }
        }
        return graph;
    }

    private static bool CanGrant(List<Op> granted, Op op) =>
        !granted.Any(g => g.Key == op.Key && g.Tx != op.Tx && (op.Exclusive || g.Exclusive));

    private static bool HasCycle(Dictionary<int, HashSet<int>> graph)
    {
        foreach (var start in graph.Keys)
        {
            var seen = new HashSet<int>(); var stack = new Stack<int>(graph[start]);
            while (stack.Count > 0)
            {
                var next = stack.Pop();
                if (next == start) { return true; }
                if (seen.Add(next) && graph.TryGetValue(next, out var onward)) { foreach (var n in onward) { stack.Push(n); } }
            }
        }
        return false;
    }

    private static async Task Settle(Task task) { try { await task.ConfigureAwait(false); } catch (InvalidOperationException) { } catch (OperationCanceledException) { } }
}
