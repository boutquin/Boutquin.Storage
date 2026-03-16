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
using Boutquin.Storage.Infrastructure.LsmTree;

namespace Boutquin.Storage.BenchMark;

/// <summary>
/// Benchmark class for testing the LsmStorageEngine implementation.
/// Inherits standard Write, Read, SearchExisting, and SearchNonExisting benchmarks from
/// <see cref="StorageEngineBenchmark{TKey,TValue}"/> and adds LSM-specific benchmarks
/// for Flush, Compact, and GetRange operations.
/// </summary>
/// <remarks>
/// This class is not sealed because BenchmarkDotNet requires benchmark classes to be inheritable.
/// </remarks>
public class LsmStorageEngineBenchmark : StorageEngineBenchmark<SerializableWrapper<int>, SerializableWrapper<string>>
{
    private readonly LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>> _lsmEngine = null!;
    private readonly string _tempDir = null!;

    /// <summary>
    /// Initializes a new instance of the LsmStorageEngineBenchmark class.
    /// Creates a temporary directory and LSM engine for benchmarking.
    /// </summary>
    public LsmStorageEngineBenchmark()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"LsmBench_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        var entrySerializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();
        _lsmEngine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 100,
            segmentFolder: _tempDir,
            segmentPrefix: "lsm_seg",
            entrySerializer: entrySerializer);

        // LsmStorageEngine now implements IStorageEngine via IBulkStorageEngine,
        // so it can be passed directly to the base class.
        SetStore(_lsmEngine);
    }

    // Inherits from base: Write, Read, SearchExisting, SearchNonExisting

    /// <summary>
    /// Benchmarks flushing the MemTable to an on-disk segment.
    /// </summary>
    [Benchmark]
    [BenchmarkCategory("Flush")]
    public async Task FlushMemTable()
    {
        for (var i = 0; i < 50; i++)
        {
            await _lsmEngine.SetAsync(
                new SerializableWrapper<int>(10000 + i),
                new SerializableWrapper<string>($"FlushValue {i}")).ConfigureAwait(false);
        }

        await _lsmEngine.FlushAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Benchmarks compacting all on-disk segments into a single segment.
    /// </summary>
    [Benchmark]
    [BenchmarkCategory("Compact")]
    public async Task Compact()
    {
        for (var batch = 0; batch < 3; batch++)
        {
            for (var i = 0; i < 100; i++)
            {
                await _lsmEngine.SetAsync(
                    new SerializableWrapper<int>(20000 + batch * 100 + i),
                    new SerializableWrapper<string>($"CompactValue {batch}_{i}")).ConfigureAwait(false);
            }

            await _lsmEngine.FlushAsync().ConfigureAwait(false);
        }

        await _lsmEngine.CompactAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Benchmarks range queries across MemTable and on-disk segments.
    /// </summary>
    [Benchmark]
    [BenchmarkCategory("RangeQuery")]
    public async Task GetRange()
    {
        var startKey = new SerializableWrapper<int>(0);
        var endKey = new SerializableWrapper<int>(ItemCount / 2);
        await _lsmEngine.GetRangeAsync(startKey, endKey).ConfigureAwait(false);
    }

    /// <summary>
    /// Cleans up the temporary directory after benchmarking.
    /// </summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _lsmEngine.Dispose();

        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
