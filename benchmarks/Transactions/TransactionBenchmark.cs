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
using BenchmarkDotNet.Attributes;
using Boutquin.Storage.Distributed.Interfaces.Transactions;
using Boutquin.Storage.Distributed.Transactions;

namespace Boutquin.Storage.Transactions.Benchmarks;

/// <summary>Compares in-memory lock work and complete durable transaction boundaries.</summary>
/// <remarks>Each invocation owns a fresh store. Durable cases include file startup, fsync, replay setup and cleanup; they are not network benchmarks.</remarks>
[MemoryDiagnoser]
public class TransactionBenchmark
{
    /// <summary>Runs one hundred uncontended read/write transactions using read committed.</summary>
    [Benchmark]
    public Task ReadCommitted() => RunIsolation(new ReadCommittedStore<string, int>());
    /// <summary>Runs one hundred uncontended transactions using retained shared/exclusive locks.</summary>
    [Benchmark]
    public Task StrictTwoPhaseLocking() => RunIsolation(new StrictTwoPhaseLockingStore<string, int>());
    private static async Task RunIsolation(IIsolationComparisonStore<string, int> store)
    {
        using (store)
        {
            for (var i = 0; i < 100; i++)
            {
                var id = store.BeginTransaction(); await store.ReadAsync("key", id).ConfigureAwait(false); await store.WriteAsync("key", i, id).ConfigureAwait(false); await store.CommitAsync(id).ConfigureAwait(false);
            }
        }
    }
    /// <summary>Commits ten two-participant transactions with their real durable decision/prepare/commit records.</summary>
    [Benchmark]
    public async Task DurableTwoPhaseCommit()
    {
        var directory = Path.Combine(Path.GetTempPath(), "txn-benchmark-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var a = await DurableTransactionalResource.CreateAsync("a", Path.Combine(directory, "a.log")).ConfigureAwait(false);
            using var b = await DurableTransactionalResource.CreateAsync("b", Path.Combine(directory, "b.log")).ConfigureAwait(false);
            using var coordinator = await TwoPhaseCommitCoordinator.CreateAsync(Path.Combine(directory, "decision.log"), [a, b]).ConfigureAwait(false);
            for (var i = 0; i < 10; i++)
            {
                TransactionIntent[] plan = [new("a", new Dictionary<string, string?> { ["key"] = "a" }), new("b", new Dictionary<string, string?> { ["key"] = "b" })];
                await coordinator.ExecuteAsync(new RequestId($"request-{i}"), plan).ConfigureAwait(false);
            }
        }
        finally { if (Directory.Exists(directory)) { Directory.Delete(directory, true); } }
    }
    /// <summary>Commits ten atomic business-state/outbound-message records to a real durable log.</summary>
    [Benchmark]
    public async Task AtomicOutbox()
    {
        var directory = Path.Combine(Path.GetTempPath(), "outbox-benchmark-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = await TransactionalMessageStore.CreateAsync(Path.Combine(directory, "messages.log")).ConfigureAwait(false);
            for (var i = 0; i < 10; i++)
            {
                await store.CommitAsync(new RequestId($"request-{i}"), "set",
                    new MessageMutation(new Dictionary<string, string?> { ["key"] = "value" }, [new OutboxMessage(new RequestId($"message-{i}"), "topic", "value")], "ok")).ConfigureAwait(false);
            }
        }
        finally { if (Directory.Exists(directory)) { Directory.Delete(directory, true); } }
    }
}
