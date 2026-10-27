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
using Boutquin.Storage.Distributed.Consensus;
using Boutquin.Storage.Distributed.Coordination;
using Boutquin.Storage.Distributed.Simulation;

namespace Boutquin.Storage.Coordination.Benchmarks;

/// <summary>Measures reference quorum/barrier costs for single-node and three-node clusters.</summary>
/// <remarks>BenchmarkDotNet subclasses this type. Results measure simulated in-process work, not network latency.</remarks>
[MemoryDiagnoser]
public class CoordinationBenchmark
{
    /// <summary>Fixed Raft membership size.</summary>
    [Params(1, 3)]
    public int Members { get; set; }

    /// <summary>Commits one hundred read barriers over a fresh cluster.</summary>
    /// <returns>Last observed revision.</returns>
    [Benchmark]
    public long ReadBarriers() => Run(false);

    /// <summary>Commits one hundred register replacements over a fresh cluster.</summary>
    /// <returns>Last committed revision.</returns>
    [Benchmark]
    public long Writes() => Run(true);

    private long Run(bool write)
    {
        var runtime = new SimulatedNodeRuntime(42);
        var raft = new RaftSimulation<CoordinationCommand>(runtime);
        var nodes = Enumerable.Range(0, Members).Select(i => new RaftNode<CoordinationCommand>($"n{i}")).ToArray();
        foreach (var node in nodes)
        {
            raft.AddNode(node);
        }
        var services = nodes.Select(node => new RaftBackedCoordinationService(node, runtime, raft.Pump)).ToArray();
        try
        {
            raft.Elect("n0");
            runtime.RunUntil(10);
            long revision = 0;
            for (var i = 0; i < 100; i++)
            {
                if (write)
                {
                    var operation = services[0].SetAsync("/key", "value", $"request:{i}");
                    runtime.RunUntil(runtime.Clock.Now + 2);
                    if (!operation.IsCompletedSuccessfully)
                    {
                        throw new InvalidOperationException("Reliable coordination write did not commit.");
                    }
                    revision = operation.GetAwaiter().GetResult().Revision;
                }
                else
                {
                    var operation = services[0].ReadAsync("/key");
                    runtime.RunUntil(runtime.Clock.Now + 2);
                    if (!operation.IsCompletedSuccessfully)
                    {
                        throw new InvalidOperationException("Reliable read barrier did not commit.");
                    }
                    revision = operation.GetAwaiter().GetResult().Revision;
                }
            }
            return revision;
        }
        finally
        {
            foreach (var service in services)
            {
                service.Dispose();
            }
        }
    }
}
