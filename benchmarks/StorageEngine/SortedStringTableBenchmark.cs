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
using SsTable = Boutquin.Storage.Infrastructure.SortedStringTable.SortedStringTable<
    Boutquin.Storage.Domain.Helpers.SerializableWrapper<int>,
    Boutquin.Storage.Domain.Helpers.SerializableWrapper<string>>;

namespace Boutquin.Storage.BenchMark;

/// <summary>
/// Benchmarks SortedStringTable write (build from sorted data) and read (sparse index lookup) performance.
/// </summary>
/// <remarks>
/// This class is not sealed because BenchmarkDotNet requires benchmark classes to be inheritable.
/// </remarks>
[MemoryDiagnoser]
public class SortedStringTableBenchmark
{
    /// <summary>
    /// Number of entries in the SSTable.
    /// </summary>
    [Params(100, 1000)]
    public int ItemCount;

    private string _tempDir = null!;
    private List<KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>> _sortedItems = null!;
    private List<SerializableWrapper<int>> _searchKeys = null!;

    /// <summary>
    /// Creates sorted test data and a temporary directory for the SSTable files.
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"SsTableBench_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        // Generate sorted items (SSTable requires strictly ascending key order).
        _sortedItems = new List<KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>>(ItemCount);
        _searchKeys = new List<SerializableWrapper<int>>(ItemCount);

        for (var i = 0; i < ItemCount; i++)
        {
            _sortedItems.Add(new KeyValuePair<SerializableWrapper<int>, SerializableWrapper<string>>(
                new SerializableWrapper<int>(i),
                new SerializableWrapper<string>($"Value {i}")));
            _searchKeys.Add(new SerializableWrapper<int>(i));
        }

        // Pre-write a table for the read benchmark.
        var readTable = new SsTable(_tempDir, "read_bench.dat", sparseIndexInterval: 4);
        readTable.Write(_sortedItems);
    }

    /// <summary>
    /// Benchmarks building an SSTable from sorted data.
    /// </summary>
    [Benchmark]
    [BenchmarkCategory("Write")]
    public void WriteSsTable()
    {
        var table = new SsTable(_tempDir, $"write_{Guid.NewGuid():N}.dat", sparseIndexInterval: 4);
        table.Write(_sortedItems);
    }

    /// <summary>
    /// Benchmarks point lookups against a pre-built SSTable using the sparse index.
    /// </summary>
    [Benchmark]
    [BenchmarkCategory("Read")]
    public void ReadSsTable()
    {
        // Reconstruct to pick up the sparse index from the file written in Setup.
        var table = new SsTable(_tempDir, "read_bench.dat", sparseIndexInterval: 4);

        for (var i = 0; i < _searchKeys.Count; i++)
        {
            table.TryGetValue(_searchKeys[i], out _);
        }
    }

    /// <summary>
    /// Cleans up the temporary directory.
    /// </summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
