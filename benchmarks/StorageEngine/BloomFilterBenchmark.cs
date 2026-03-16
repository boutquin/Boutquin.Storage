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
namespace Boutquin.Storage.BenchMark;

/// <summary>
/// Compares performance of BloomFilter vs CountingBloomFilter for Add and Contains operations.
/// CountingBloomFilter uses int[] counters (32x memory per position) to support removal,
/// so the trade-off in throughput and memory is worth measuring.
/// </summary>
/// <remarks>
/// This class is not sealed because BenchmarkDotNet requires benchmark classes to be inheritable.
/// </remarks>
[MemoryDiagnoser]
public class BloomFilterBenchmark
{
    /// <summary>
    /// Number of items to add/query.
    /// </summary>
    [Params(1000, 10000)]
    public int ItemCount;

    private List<string> _items = null!;
    private List<string> _nonExistentItems = null!;

    // Pre-populated filters for Contains benchmarks.
    private BloomFilter<string> _bloomPopulated = null!;
    private CountingBloomFilter<string> _countingBloomPopulated = null!;

    /// <summary>
    /// Generates test data and pre-populates filters for Contains benchmarks.
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        _items = new List<string>(ItemCount);
        _nonExistentItems = new List<string>(ItemCount);

        for (var i = 0; i < ItemCount; i++)
        {
            _items.Add($"item_{i}");
            _nonExistentItems.Add($"missing_{i}");
        }

        _bloomPopulated = new BloomFilter<string>(ItemCount, 0.01);
        _countingBloomPopulated = new CountingBloomFilter<string>(ItemCount, 0.01);

        foreach (var item in _items)
        {
            _bloomPopulated.Add(item);
            _countingBloomPopulated.Add(item);
        }
    }

    /// <summary>
    /// Benchmarks BloomFilter Add throughput.
    /// </summary>
    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Add")]
    public void BloomFilterAdd()
    {
        var filter = new BloomFilter<string>(ItemCount, 0.01);
        foreach (var item in _items)
        {
            filter.Add(item);
        }
    }

    /// <summary>
    /// Benchmarks CountingBloomFilter Add throughput.
    /// </summary>
    [Benchmark]
    [BenchmarkCategory("Add")]
    public void CountingBloomFilterAdd()
    {
        var filter = new CountingBloomFilter<string>(ItemCount, 0.01);
        foreach (var item in _items)
        {
            filter.Add(item);
        }
    }

    /// <summary>
    /// Benchmarks BloomFilter Contains for items that exist (true positives).
    /// </summary>
    [Benchmark]
    [BenchmarkCategory("Contains")]
    public void BloomFilterContains()
    {
        foreach (var item in _items)
        {
            _bloomPopulated.Contains(item);
        }
    }

    /// <summary>
    /// Benchmarks CountingBloomFilter Contains for items that exist.
    /// </summary>
    [Benchmark]
    [BenchmarkCategory("Contains")]
    public void CountingBloomFilterContains()
    {
        foreach (var item in _items)
        {
            _countingBloomPopulated.Contains(item);
        }
    }

    /// <summary>
    /// Benchmarks BloomFilter Contains for items that do not exist (false positive rate test).
    /// </summary>
    [Benchmark]
    [BenchmarkCategory("ContainsMiss")]
    public void BloomFilterContainsMiss()
    {
        foreach (var item in _nonExistentItems)
        {
            _bloomPopulated.Contains(item);
        }
    }

    /// <summary>
    /// Benchmarks CountingBloomFilter Contains for items that do not exist.
    /// </summary>
    [Benchmark]
    [BenchmarkCategory("ContainsMiss")]
    public void CountingBloomFilterContainsMiss()
    {
        foreach (var item in _nonExistentItems)
        {
            _countingBloomPopulated.Contains(item);
        }
    }
}
