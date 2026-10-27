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
using Boutquin.Storage.Distributed.Simulation;

namespace Boutquin.Storage.Simulation.Benchmarks;

/// <summary>Measures the extra queue and allocation work introduced by delayed duplicate delivery.</summary>
[MemoryDiagnoser]
public class SimulationBenchmark
{
    /// <summary>Number of sends in each fresh seeded run.</summary>
    [Params(1000, 10000)]
    public int Messages { get; set; }

    /// <summary>Immediate reliable-delivery baseline.</summary>
    /// <returns>Number of delivered copies, preventing dead-code elimination.</returns>
    [Benchmark(Baseline = true)]
    public int Reliable() => Run(new FaultInjectionPolicy());

    /// <summary>Delayed delivery with 25 percent loss and 50 percent duplication.</summary>
    /// <returns>Number of surviving delivered copies.</returns>
    [Benchmark]
    public int Faulty() => Run(new FaultInjectionPolicy(0.25, 0.5, 100));

    private int Run(FaultInjectionPolicy policy)
    {
        var runtime = new SimulatedNodeRuntime(42, policy);
        var count = 0;
        runtime.AddNode("source", _ => { });
        runtime.AddNode("target", _ => count++);
        for (var i = 0; i < Messages; i++)
        {
            runtime.Network.Send("source", "target", i);
        }
        runtime.RunUntil(100);
        return count;
    }
}
