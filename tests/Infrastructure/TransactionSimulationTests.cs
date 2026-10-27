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
using Boutquin.Storage.Distributed.Simulation;

namespace Boutquin.Storage.Infrastructure.Tests;

public sealed class TransactionSimulationTests
{
    [Theory]
    [InlineData(271828u)]
    [InlineData(314159u)]
    [InlineData(4294967295u)]
    public async Task SeededRequestAndResponseFaults_ResolveToOneAtomicFinalOutcome(uint seed)
    {
        var directory = Path.Combine(Path.GetTempPath(), "txn-simulation-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var a = await DurableTransactionalResource.CreateAsync("a", Path.Combine(directory, "a.log")).ConfigureAwait(true);
            using var b = await DurableTransactionalResource.CreateAsync("b", Path.Combine(directory, "b.log")).ConfigureAwait(true);
            var runtime = new SimulatedNodeRuntime(seed);
            var received = new Dictionary<long, int>();
            void Receive(object message) { var id = (long)message; received[id] = received.GetValueOrDefault(id) + 1; }
            runtime.AddNode("coordinator", Receive); runtime.AddNode("a", Receive); runtime.AddNode("b", Receive);
            var transport = new Transport(runtime, received);
            using var coordinator = await TwoPhaseCommitCoordinator.CreateAsync(Path.Combine(directory, "decision.log"),
                [new Participant(a, transport), new Participant(b, transport)]).ConfigureAwait(true);
            for (var i = 0; i < 12; i++)
            {
                runtime.Network.Policy = new FaultInjectionPolicy(0.25, 0.6, 5);
                if (i % 4 == 0) { runtime.Network.SetPartition("coordinator", "b", true); }
                var id = new RequestId($"request-{i}");
                TransactionIntent[] plan = [new("a", new Dictionary<string, string?> { [$"key-{i}"] = "left" }), new("b", new Dictionary<string, string?> { [$"key-{i}"] = "right" })];
                try { await coordinator.ExecuteAsync(id, plan).ConfigureAwait(true); }
                catch (IOException) { /* A missing response leaves the outcome uncertain until recovery. */ }
                runtime.Network.SetPartition("coordinator", "b", false);
                runtime.Network.Policy = new FaultInjectionPolicy();
                await coordinator.RecoverAsync().ConfigureAwait(true);
                var result = await coordinator.ExecuteAsync(id, plan).ConfigureAwait(true);
                (await a.ReadAsync($"key-{i}").ConfigureAwait(true)).Should().Be(result == CommitResult.Committed ? "left" : null);
                (await b.ReadAsync($"key-{i}").ConfigureAwait(true)).Should().Be(result == CommitResult.Committed ? "right" : null);
            }
        }
        finally { if (Directory.Exists(directory)) { Directory.Delete(directory, true); } }
    }

    // The single-driver test host admits all delivered request copies, awaits actual fsync outside
    // the clock callback, then transports replies. Loss on either leg is observably different.
    private sealed class Transport(SimulatedNodeRuntime runtime, Dictionary<long, int> received)
    {
        private long _sequence;
        internal async Task<T> InvokeAsync<T>(string resource, Func<Task<T>> action)
        {
            var request = ++_sequence;
            runtime.Network.Send("coordinator", resource, request);
            runtime.RunUntil(runtime.Clock.Now + 6);
            var copies = received.GetValueOrDefault(request);
            if (copies == 0) { throw new IOException("Request lost before participant admission."); }
            T result = default!;
            for (var i = 0; i < copies; i++) { result = await action().ConfigureAwait(true); }
            var response = ++_sequence;
            runtime.Network.Send(resource, "coordinator", response);
            runtime.RunUntil(runtime.Clock.Now + 6);
            if (!received.ContainsKey(response)) { throw new IOException("Reply lost after participant effect."); }
            return result;
        }
    }
    private sealed class Participant(ITransactionalResource resource, Transport transport) : ITransactionalResource
    {
        public string ResourceId => resource.ResourceId;
        public Task<PrepareResult> PrepareAsync(RequestId id, IReadOnlyDictionary<string, string?> writes, CancellationToken ct = default)
            => transport.InvokeAsync(ResourceId, () => resource.PrepareAsync(id, writes, ct));
        public Task<CommitResult> CommitAsync(RequestId id, CancellationToken ct = default)
            => transport.InvokeAsync(ResourceId, () => resource.CommitAsync(id, ct));
        public Task<AbortResult> AbortAsync(RequestId id, CancellationToken ct = default)
            => transport.InvokeAsync(ResourceId, () => resource.AbortAsync(id, ct));
    }
}
