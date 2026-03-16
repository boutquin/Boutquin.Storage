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
/// Compares compaction performance across FullCompactionStrategy, SizeTieredCompactionStrategy,
/// and LeveledCompactionStrategy using LsmStorageEngine.
/// </summary>
/// <remarks>
/// <para>
/// Each benchmark iteration requires fresh segments to compact, since compaction merges segments
/// and subsequent calls on compacted data are no-ops. We use [IterationSetup]/[IterationCleanup]
/// to recreate engines with populated data before each measurement.
/// </para>
/// <para>
/// This class is not sealed because BenchmarkDotNet requires benchmark classes to be inheritable.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class CompactionStrategyBenchmark
{
    private string _fullDir = null!;
    private string _sizeTieredDir = null!;
    private string _leveledDir = null!;

    /// <summary>
    /// Number of items to populate each engine with before compaction.
    /// Must be large enough relative to memTableCapacity to create multiple segments.
    /// </summary>
    [Params(200, 500, 1000)]
    public int ItemCount;

    private LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>> _fullEngine = null!;
    private LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>> _sizeTieredEngine = null!;
    private LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>> _leveledEngine = null!;

    /// <summary>
    /// Creates three LSM engines with different compaction strategies and populates them
    /// with enough data to create multiple on-disk segments before each iteration.
    /// </summary>
    [IterationSetup]
    public void Setup()
    {
        var serializer = new BinaryEntrySerializer<SerializableWrapper<int>, SerializableWrapper<string>>();

        _fullDir = Path.Combine(Path.GetTempPath(), $"CompactBench_Full_{Guid.NewGuid():N}");
        _sizeTieredDir = Path.Combine(Path.GetTempPath(), $"CompactBench_ST_{Guid.NewGuid():N}");
        _leveledDir = Path.Combine(Path.GetTempPath(), $"CompactBench_Lev_{Guid.NewGuid():N}");

        // Small memTableCapacity forces frequent flushes, creating the segments that compaction merges.
        // Each strategy is explicitly passed to its engine.
        _fullEngine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 50, segmentFolder: _fullDir, segmentPrefix: "full",
            entrySerializer: serializer,
            compactionStrategy: new FullCompactionStrategy(threshold: 2));

        _sizeTieredEngine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 50, segmentFolder: _sizeTieredDir, segmentPrefix: "st",
            entrySerializer: serializer,
            compactionStrategy: new SizeTieredCompactionStrategy(minSegments: 2));

        _leveledEngine = new LsmStorageEngine<SerializableWrapper<int>, SerializableWrapper<string>>(
            memTableCapacity: 50, segmentFolder: _leveledDir, segmentPrefix: "lev",
            entrySerializer: serializer,
            compactionStrategy: new LeveledCompactionStrategy(level0Threshold: 2));

        // Populate all three engines identically to create multiple segments.
        var engines = new[] { _fullEngine, _sizeTieredEngine, _leveledEngine };
        foreach (var engine in engines)
        {
            for (var i = 0; i < ItemCount; i++)
            {
                engine.SetAsync(
                    new SerializableWrapper<int>(i),
                    new SerializableWrapper<string>($"Value {i}")).GetAwaiter().GetResult();
            }

            // Force a final flush to ensure all data is in on-disk segments.
            engine.FlushAsync().GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// Benchmarks FullCompactionStrategy: merges all segments into one.
    /// </summary>
    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Compaction")]
    public async Task FullCompaction()
    {
        await _fullEngine.CompactAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Benchmarks SizeTieredCompactionStrategy compaction.
    /// </summary>
    [Benchmark]
    [BenchmarkCategory("Compaction")]
    public async Task SizeTieredCompaction()
    {
        await _sizeTieredEngine.CompactAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Benchmarks LeveledCompactionStrategy compaction.
    /// </summary>
    [Benchmark]
    [BenchmarkCategory("Compaction")]
    public async Task LeveledCompaction()
    {
        await _leveledEngine.CompactAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Cleans up engines and temporary directories after each iteration.
    /// </summary>
    [IterationCleanup]
    public void Cleanup()
    {
        _fullEngine.Dispose();
        _sizeTieredEngine.Dispose();
        _leveledEngine.Dispose();

        foreach (var dir in new[] { _fullDir, _sizeTieredDir, _leveledDir })
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
