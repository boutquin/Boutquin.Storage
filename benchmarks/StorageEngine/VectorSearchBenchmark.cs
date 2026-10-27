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
using Boutquin.Storage.Infrastructure.Search.Vector;

namespace Boutquin.Storage.BenchMark;

/// <summary>Measures exact and approximate vector-search latency on deterministic data.</summary>
/// <remarks>This class remains inheritable because BenchmarkDotNet generates a derived workload type.</remarks>
[MemoryDiagnoser]
public class VectorSearchBenchmark
{
    private const int Dimension = 32;
    private const int ResultCount = 10;
    private FlatVectorIndex<int> _flat = null!;
    private IvfVectorIndex<int> _ivf = null!;
    private HnswVectorIndex<int> _hnsw = null!;
    private float[][] _queries = null!;

    /// <summary>Number of indexed vectors.</summary>
    [Params(1000)]
    public int VectorCount { get; set; }

    /// <summary>Builds deterministic clustered indexes and query vectors.</summary>
    [GlobalSetup]
    public void Setup()
    {
        var metric = new L2Distance();
        _flat = new FlatVectorIndex<int>(Dimension, metric);
        _ivf = new IvfVectorIndex<int>(Dimension, metric, partitionCount: 32, probeCount: 4);
        _hnsw = new HnswVectorIndex<int>(Dimension, metric, maxConnections: 12, efConstruction: 80, efSearch: 48);
        var random = new Random(20260917);
        var vectors = Enumerable.Range(0, VectorCount).Select(index => CreateVector(index, random)).ToArray();
        for (var index = 0; index < vectors.Length; index++)
        {
            _flat.Upsert(index, vectors[index]);
            _ivf.Upsert(index, vectors[index]);
            _hnsw.Upsert(index, vectors[index]);
        }

        _queries = vectors.Where((_, index) => index % 125 == 0).Take(16).ToArray();
        _ = _ivf.Search(_queries[0], ResultCount);
    }

    /// <summary>Measures exact exhaustive search.</summary>
    [Benchmark(Baseline = true)]
    public int Flat()
    {
        var checksum = 0;
        foreach (var query in _queries)
        {
            checksum += _flat.Search(query, ResultCount)[0].Id;
        }

        return checksum;
    }

    /// <summary>Measures IVF with four coarse-cell probes.</summary>
    [Benchmark]
    public int IvfFourProbes()
    {
        var checksum = 0;
        foreach (var query in _queries)
        {
            checksum += _ivf.Search(query, ResultCount, 4)[0].Id;
        }

        return checksum;
    }

    /// <summary>Measures HNSW with a 48-candidate search list.</summary>
    [Benchmark]
    public int HnswEf48()
    {
        var checksum = 0;
        foreach (var query in _queries)
        {
            checksum += _hnsw.Search(query, ResultCount, 48)[0].Id;
        }

        return checksum;
    }

    private static float[] CreateVector(int index, Random random)
    {
        var cluster = index % 20;
        return Enumerable.Range(0, Dimension)
            .Select(dimension => cluster * 5F + dimension * 0.02F + (float)random.NextDouble())
            .ToArray();
    }
}
