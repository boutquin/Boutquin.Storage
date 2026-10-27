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
using Boutquin.Storage.Distributed.Partitioning;

namespace Boutquin.Storage.Sharding.Benchmarks;

/// <summary>Compares the three reference consistent-placement lookup paths.</summary>
[MemoryDiagnoser]
public class ShardingRoutingBenchmark
{
    private readonly string[] _keys = Enumerable.Range(0, 1_024).Select(i => $"key-{i}").ToArray();
    private readonly ulong[] _hashes = Enumerable.Range(0, 1_024)
        .Select(i => unchecked((ulong)new Random(i).NextInt64()))
        .ToArray();
    private ConsistentHashRing<string> _ring = null!;
    private RendezvousHash<string> _rendezvous = null!;
    private int _cursor;

    /// <summary>Gets or sets the number of target nodes/buckets.</summary>
    [Params(8, 64)]
    public int NodeCount { get; set; }

    /// <summary>Builds the topology outside benchmark measurements.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _ring = new ConsistentHashRing<string>();
        _rendezvous = new RendezvousHash<string>();
        foreach (var node in Enumerable.Range(0, NodeCount).Select(i => $"node-{i:D3}"))
        {
            _ring.AddNode(node);
            _rendezvous.AddNode(node);
        }
    }

    /// <summary>Measures pre-hashed Jump Consistent Hash lookup.</summary>
    [Benchmark(Baseline = true)]
    public int Jump() => JumpConsistentHash.GetBucket(_hashes[NextIndex()], NodeCount);

    /// <summary>Measures virtual-node ring lookup including string hashing.</summary>
    [Benchmark]
    public string ConsistentRing() => _ring.GetNode(_keys[NextIndex()]);

    /// <summary>Measures highest-random-weight lookup including string hashing.</summary>
    [Benchmark]
    public string Rendezvous() => _rendezvous.GetNode(_keys[NextIndex()]);

    private int NextIndex() => Interlocked.Increment(ref _cursor) & (_keys.Length - 1);
}
