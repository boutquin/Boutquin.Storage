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
using BenchmarkDotNet.Running;
using Boutquin.Storage.Distributed.Replication;

BenchmarkSwitcher.FromAssembly(typeof(ReplicationBenchmarks).Assembly).Run(args);

/// <summary>End-to-end reference operation costs, including allocation and topology setup.</summary>
[MemoryDiagnoser]
public class ReplicationBenchmarks
{
    /// <summary>Creates two independent writers, exchanges writes, and resolves concurrent siblings.</summary>
    [Benchmark]
    public int ConcurrentResolution()
    {
        var replicas = new MultiLeaderReplicaSet<string, int>(["a", "b"], new VersionVectorResolver<int>());
        replicas.Write("a", "x", 1);
        replicas.Write("b", "x", 2);
        foreach (var operation in replicas.Capture("a"))
        {
            replicas.Deliver("b", operation);
        }

        return replicas.Read("b", "x").Values.Count;
    }

    /// <summary>Delivers a reversed causal chain to exercise dependency buffering.</summary>
    [Benchmark]
    public int ReorderedDelivery()
    {
        var replicas = new MultiLeaderReplicaSet<string, int>(["a", "b"], new VersionVectorResolver<int>());
        for (var i = 0; i < 16; i++)
        {
            replicas.Write("a", "x", i);
        }

        foreach (var operation in replicas.Capture("a").Reverse())
        {
            replicas.Deliver("b", operation);
        }

        return replicas.Read("b", "x").Values.Single();
    }

    /// <summary>Reads a majority and repairs its stale responder.</summary>
    [Benchmark]
    public int ReadRepair()
    {
        var replicas = Create();
        replicas.Write("x", 1, ReplicationConsistency.One);
        return replicas.Read("x", ReplicationConsistency.Quorum).Values.Single();
    }

    /// <summary>Compares Merkle ranges and repairs sixteen previously unread keys.</summary>
    [Benchmark]
    public int ColdKeyAntiEntropy()
    {
        var replicas = Create();
        for (var i = 0; i < 16; i++)
        {
            replicas.Write(i.ToString(System.Globalization.CultureInfo.InvariantCulture), i, ReplicationConsistency.One);
        }

        return replicas.Repair("a", "c");
    }

    private static LeaderlessReplicaSet<string, int> Create() => new(["a", "b", "c"], [], new VersionVectorResolver<int>());
}
