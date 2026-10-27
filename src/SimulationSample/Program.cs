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
using Boutquin.Storage.Distributed.Consensus;
using Boutquin.Storage.Distributed.Replication;
using Boutquin.Storage.Distributed.Simulation;

var runtime = new SimulatedNodeRuntime(20260917, new FaultInjectionPolicy(0, 0.25, 5));
var raft = new RaftSimulation<int>(runtime);
var nodes = Enumerable.Range(0, 3).Select(i => new RaftNode<int>($"node{i}")).ToArray();
foreach (var node in nodes)
{
    raft.AddNode(node);
}
runtime.Invariants.Add("one leader per term",
    () => nodes.Where(n => n.State == Boutquin.Storage.Domain.Enums.RaftNodeState.Leader)
        .GroupBy(n => n.CurrentTerm).All(group => group.Count() == 1));
raft.Elect("node0");
runtime.RunUntil(100);
var proposal = await nodes[0].ProposeAsync(42).ConfigureAwait(false);
raft.Pump();
runtime.RunUntil(200);
if (nodes[0].GetCommittedLog().Count != 1)
{
    throw new InvalidOperationException("The seeded Raft command failed to commit.");
}
Console.WriteLine($"Raft seed=20260917, tick={runtime.Clock.Now}, immediate commit={proposal}, committed=42");

using var replication = new SingleLeaderReplication<string, int>();
replication.AddFollower("follower");
await replication.WriteAsync("key", 7).ConfigureAwait(false);
var history = new HistoryRecorder<int, int>();
var write = history.Invoke(7);
history.Complete(write, 7);
var read = history.Invoke(-1);
var stale = await replication.ReadAsync("key", "follower").ConfigureAwait(false);
history.Complete(read, stale.Value);
Console.WriteLine($"Lagging follower history: {LinearizabilityChecker.Check(history.Snapshot(), 0, Register)}");
await replication.SyncFollowerAsync("follower").ConfigureAwait(false);
var caughtUp = await replication.ReadAsync("key", "follower").ConfigureAwait(false);
if (!caughtUp.Found || caughtUp.Value != 7)
{
    throw new InvalidOperationException("Follower catchup failed.");
}
Console.WriteLine($"Caught-up follower value: {caughtUp.Value}");
Console.WriteLine("Reference simulation only: explicit elections, immutable payloads, no OS crash model.");

static (bool Allowed, int State) Register(int state, int input, int output)
    => input < 0 ? (output == state, state) : (output == input, input);

