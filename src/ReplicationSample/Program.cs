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
using Boutquin.Storage.Distributed.Interfaces.Replication;
using Boutquin.Storage.Distributed.Replication;
using Boutquin.Storage.Distributed.Replication.Exceptions;
using Boutquin.Storage.Distributed.Simulation;

var seed = args.Length == 0 ? 20260917U : uint.Parse(args[0], CultureInfo.InvariantCulture);
RunMultiLeader(new VersionVectorResolver<int>(), "siblings", [18, 19]);
RunMultiLeader(new LastWriteWinsResolver<int>(), "logical LWW", [19]);
RunMultiLeader(new MergeFunctionResolver<int>(values => values.Sum()), "application sum", [37]);
RunSessions();
RunLeaderless();
Console.WriteLine($"PASS seed={seed}: conflicts, sessions, handoff, read repair, and cold-key anti-entropy.");

void RunMultiLeader(IConflictResolver<int> resolver, string policy, int[] expected)
{
    var replicas = new MultiLeaderReplicaSet<string, int>(["a", "b", "c"], resolver);
    var runtime = new SimulatedNodeRuntime(seed, new FaultInjectionPolicy(.3, .5, 9));
    foreach (var node in new[] { "a", "b", "c" })
    {
        runtime.AddNode(node, message => replicas.Deliver(node, (ReplicationOperation<string, int>)message));
    }
    runtime.Network.SetPartition("a", "b", true);
    runtime.Pause("c");
    for (var i = 0; i < 20; i++)
    {
        var source = i % 2 == 0 ? "a" : "b";
        replicas.Write(source, "shared", i);
        Send(source);
        runtime.RunUntil(i * 10 + 10);
    }
    runtime.Network.SetPartition("a", "b", false);
    runtime.Resume("c");
    runtime.Network.Policy = new FaultInjectionPolicy(0, 1, 11);
    foreach (var node in new[] { "a", "b", "c" })
    {
        Send(node);
    }

    runtime.RunUntil(300);
    foreach (var node in new[] { "a", "b", "c" })
    {
        Require(replicas.Capture(node).Count == 20, "Lost operation after healing/retransmission.");
        Require(replicas.Read(node, "shared").Values.SequenceEqual(expected), "Resolver did not converge.");
    }
    Console.WriteLine($"{policy}: [{string.Join(", ", expected)}], all 20 writes delivered despite seeded faults.");
    void Send(string source)
    {
        foreach (var target in new[] { "a", "b", "c" })
        {
            foreach (var operation in replicas.Capture(source))
            {
                runtime.Network.Send(source, target, operation);
            }
        }
    }
}

void RunSessions()
{
    var replicas = new MultiLeaderReplicaSet<string, int>(["a", "b", "c"], new VersionVectorResolver<int>());
    var written = replicas.Write("a", "account", 1);
    Reject(() => replicas.Read("b", "account", written));
    Reject(() => replicas.Write("b", "audit", 2, written));
    foreach (var operation in replicas.Capture("a"))
    {
        replicas.Deliver("b", operation);
    }

    var observed = replicas.Read("b", "account", written).Session;
    var next = replicas.Write("b", "audit", 2, observed);
    Reject(() => replicas.Read("a", "account", next));
    var dependent = replicas.Capture("b").Single(op => op.Version.Origin == "b");
    replicas.Deliver("c", dependent);
    Require(replicas.Read("c", "audit").Values.Count == 0, "Effect became visible before its observed cause.");
    foreach (var operation in replicas.Capture("a"))
    {
        replicas.Deliver("c", operation);
    }

    Require(replicas.Read("c", "account", next).Values.Single() == 1, "Missing causal predecessor.");
    Require(replicas.Read("c", "audit", next).Values.Single() == 2, "Dependency buffer did not drain.");
    Console.WriteLine("Sessions: read-your-writes, monotonic reads/writes, and write-follows-reads verified across lagging replicas.");
}

void RunLeaderless()
{
    var replicas = new LeaderlessReplicaSet<string, int>(["a", "b", "c"], ["d", "e"], new VersionVectorResolver<int>());
    replicas.SetAvailability("a", false);
    replicas.SetAvailability("b", false);
    var token = replicas.Write("hot", 7, ReplicationConsistency.Quorum, sloppy: true);
    Require(replicas.PendingHintCount == 1, "Missing sloppy-quorum hint.");
    replicas.SetAvailability("a", true);
    replicas.SetAvailability("b", true);
    replicas.SetAvailability("c", false);
    Require(replicas.Read("hot", ReplicationConsistency.Quorum).Values.Count == 0, "Expected disjoint sloppy read set.");
    Reject(() => replicas.Read("hot", ReplicationConsistency.Quorum, session: token));
    Require(replicas.Handoff() == 1, "Hint did not reach recovered home.");
    Require(replicas.Read("hot", ReplicationConsistency.Quorum, session: token).Values.Single() == 7, "Read repair failed.");
    Require(replicas.ReadReplica("b", "hot", token).Values.Single() == 7, "Read did not repair stale responder.");
    replicas.SetAvailability("c", true);
    replicas.Write("cold", 42, ReplicationConsistency.One, session: token);
    Require(replicas.ReadReplica("c", "cold").Values.Count == 0, "Cold-key lag not reproduced.");
    var transferred = replicas.Repair("a", "c");
    Require(transferred > 0 && replicas.ReadReplica("c", "cold").Values.Single() == 42, "Merkle anti-entropy failed.");
    Require(replicas.Repair("a", "c") == 0, "Converged Merkle roots should skip transfer.");
    Console.WriteLine($"Leaderless: disjoint sloppy read exposed, hint retried, read repaired, cold-key repair transferred {transferred} operations.");
}

static void Reject(Action action)
{
    try { action(); }
    catch (SessionConsistencyException) { return; }
    throw new InvalidOperationException("A lagging session operation unexpectedly succeeded.");
}

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
