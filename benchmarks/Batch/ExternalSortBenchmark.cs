// Copyright (c) 2024-2026 Pierre G. Boutquin. All rights reserved.
//
//   Licensed under the Apache License, Version 2.0 (the "License").
//   You may not use this file except in compliance with the License.
//   You may obtain a copy of the License at
//
//       http://www.apache.org/licenses/LICENSE-2.0
//
//   Unless required by applicable law or agreed to in writing, software
//   distributed under the License is distributed on an "AS IS" BASIS,
//   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//
//   See the License for the specific language governing permissions and
//   limitations under the License.
//

using System.Text.Json;

using BenchmarkDotNet.Attributes;

using Boutquin.Storage.Infrastructure.Batch;

namespace Boutquin.Storage.Batch.Benchmarks;

/// <summary>Compares an in-memory sort with bounded runs and disk merge passes.</summary>
[MemoryDiagnoser]
public class ExternalSortBenchmark
{
    private int[] _input = [];
    private string _directory = string.Empty;
    private ExternalMergeSorter<int> _sorter = null!;
    /// <summary>Gets or sets the finite benchmark input size.</summary>
    [Params(5000)]
    public int Count { get; set; }
    /// <summary>Creates reproducible shuffled rows and an owned scratch directory.</summary>
    [GlobalSetup]
    public void Setup()
    {
        var random = new Random(42);
        _input = Enumerable.Range(0, Count).OrderBy(_ => random.Next()).ToArray();
        _directory = Path.Combine(Path.GetTempPath(), "batch-benchmark-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _sorter = new ExternalMergeSorter<int>(_directory, Comparer<int>.Default,
            value => JsonSerializer.SerializeToUtf8Bytes(value), bytes => JsonSerializer.Deserialize<int>(bytes.Span),
            maxItemsInMemory: 128, mergeFanIn: 4);
    }
    /// <summary>Removes the benchmark scratch directory.</summary>
    [GlobalCleanup]
    public void Cleanup() => Directory.Delete(_directory, recursive: true);
    /// <summary>Sorts all rows in memory and consumes their checksum.</summary>
    [Benchmark(Baseline = true)]
    public long InMemory() => _input.Order().Sum(value => (long)value);
    /// <summary>Spills bounded runs, merges them, and checks monotonic output.</summary>
    [Benchmark]
    public async Task<long> ExternalMerge()
    {
        long total = 0;
        var previous = int.MinValue;
        await foreach (var value in _sorter.SortAsync(Input()).ConfigureAwait(false))
        {
            if (value < previous) { throw new InvalidOperationException("Sort order mismatch."); }
            previous = value;
            total += value;
        }
        return total;
    }
    private async IAsyncEnumerable<int> Input()
    {
        foreach (var value in _input) { yield return value; }
        await Task.CompletedTask.ConfigureAwait(false);
    }
}
