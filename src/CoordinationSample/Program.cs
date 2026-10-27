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
using Boutquin.Storage.Distributed.Consensus;
using Boutquin.Storage.Distributed.Coordination;
using Boutquin.Storage.Distributed.Simulation;
using Boutquin.Storage.Domain.Enums;

var seed = args.Length == 0 ? 20260917u : uint.Parse(args[0], CultureInfo.InvariantCulture);
var runtime = new SimulatedNodeRuntime(seed);
var raft = new RaftSimulation<CoordinationCommand>(runtime);
var nodes = Enumerable.Range(0, 3).Select(i => new RaftNode<CoordinationCommand>($"n{i}")).ToArray();
foreach (var node in nodes)
{
    raft.AddNode(node);
}
var services = nodes.Select(node => new RaftBackedCoordinationService(node, runtime, raft.Pump)).ToArray();
try
{
    raft.Elect("n0");
    runtime.RunUntil(10);
    runtime.Network.Policy = new FaultInjectionPolicy(0.1, 0.25, 4);
    var first = Finish(services[0].AcquireLeaseAsync("/shard-lock", "worker-a", 200, "acquire-a"))!;
    var assignment = Finish(services[0].CompareExchangeAsync("/shards/0/leader", 0, "worker-a", "assign-a", first.Id));
    Require(assignment.Exchanged, "The first shard assignment must succeed.");
    var firstId = Finish(new LinearizableIdGenerator(services[0]).NextAsync("id-a"));

    // The old coordinator stays alive but loses its majority. Election is explicit in this reference model.
    runtime.Network.SetPartition("n0", "n1", true);
    runtime.Network.SetPartition("n0", "n2", true);
    runtime.Network.Policy = new FaultInjectionPolicy();
    raft.Elect("n1");
    runtime.RunUntil(runtime.Clock.Now + 20);
    var replacement = 1;
    if (nodes[replacement].State != RaftNodeState.Leader)
    {
        // Loss can leave n1 behind the committed prefix. Raft correctly denies its vote;
        // an up-to-date survivor must campaign instead of weakening the voting rule.
        replacement = 2;
        raft.Elect("n2");
        runtime.RunUntil(runtime.Clock.Now + 20);
    }
    Require(nodes[replacement].State == RaftNodeState.Leader, "A surviving quorum must elect an eligible candidate.");
    var coordinator = services[replacement];
    runtime.Network.Policy = new FaultInjectionPolicy(0.1, 0.25, 4);
    var nextId = Finish(new LinearizableIdGenerator(coordinator).NextAsync("id-b"));
    Require(nextId > firstId, "IDs must increase across leader changes.");
    runtime.RunUntil(Math.Max(runtime.Clock.Now, first.ExpiresAt + 20));
    Require(Finish(coordinator.ReadAsync("/shards/0/leader")).Value is null, "Expiry must remove shard ownership.");
    using var watch = Finish(coordinator.WatchAsync("/shards/0/leader", assignment.Current!.Revision));
    Require(watch.TryRead(out var ended) && ended!.Kind == CoordinationEventKind.LeaseEnded,
        "The replacement leader must replay the lease-ended watch event.");

    var second = Finish(coordinator.AcquireLeaseAsync("/shard-lock", "worker-b", 200, "acquire-b"))!;
    Require(second.FencingToken > first.FencingToken, "Replacement lease must carry a larger fencing token.");
    Require(Finish(coordinator.CompareExchangeAsync("/shards/0/leader", 0, "worker-b", "assign-b", second.Id)).Exchanged,
        "The replacement worker must acquire the now-vacant shard assignment.");
    var shard = new FencedShard();
    Require(shard.TryWrite(second.FencingToken, "worker-b committed data"), "New owner's write must be accepted.");
    Require(!shard.TryWrite(first.FencingToken, "stale worker-a data"), "Stale owner's write must be fenced out.");

    var isolatedReadRejected = false;
    try
    {
        Finish(services[0].ReadAsync("/shards/0/leader"));
    }
    catch (TimeoutException)
    {
        isolatedReadRejected = true;
    }
    Require(isolatedReadRejected, "An isolated old leader must not return a successful stale read.");
    Console.WriteLine($"seed={seed}; IDs={firstId},{nextId}; fencing={first.FencingToken},{second.FencingToken}");
    Console.WriteLine("Shard reassignment, watch replay, stale-writer fencing, and minority read rejection passed.");
    Console.WriteLine("In-process model: shared virtual clock, fixed membership, explicit elections, no OS durability.");
}
finally
{
    foreach (var service in services)
    {
        service.Dispose();
    }
}

T Finish<T>(Task<T> operation)
{
    var deadline = checked(runtime.Clock.Now + 110);
    while (!operation.IsCompleted && runtime.Clock.Now < deadline)
    {
        runtime.RunUntil(runtime.Clock.Now + 1);
    }
    if (!operation.IsCompleted)
    {
        throw new InvalidOperationException("The virtual operation did not reach a bounded outcome.");
    }
    return operation.GetAwaiter().GetResult();
}

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

// The protected resource, not the lease service, must reject stale tokens. Equal-token writes remain
// valid for the current owner; a token authenticates no caller and supplies no data-level transaction.
internal sealed class FencedShard
{
    private long _highestToken;
    internal string? Value { get; private set; }
    internal bool TryWrite(long token, string value)
    {
        if (token < _highestToken)
        {
            return false;
        }
        _highestToken = token;
        Value = value;
        return true;
    }
}
