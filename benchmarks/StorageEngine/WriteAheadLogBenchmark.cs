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
using Boutquin.Storage.Infrastructure.WriteAheadLog;

namespace Boutquin.Storage.BenchMark;

/// <summary>
/// Benchmarks WriteAheadLog Append and Recover performance.
/// Each append is fsync'd to disk, so this measures the durability overhead.
/// </summary>
/// <remarks>
/// This class is not sealed because BenchmarkDotNet requires benchmark classes to be inheritable.
/// </remarks>
[MemoryDiagnoser]
public class WriteAheadLogBenchmark
{
    /// <summary>
    /// Number of entries to append/recover.
    /// </summary>
    [Params(100, 1000)]
    public int ItemCount;

    private string _tempDir = null!;
    private List<SerializableWrapper<int>> _keys = null!;
    private List<SerializableWrapper<string>> _values = null!;

    /// <summary>
    /// Creates temporary directory and test data.
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"WalBench_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _keys = new List<SerializableWrapper<int>>(ItemCount);
        _values = new List<SerializableWrapper<string>>(ItemCount);

        for (var i = 0; i < ItemCount; i++)
        {
            _keys.Add(new SerializableWrapper<int>(i));
            _values.Add(new SerializableWrapper<string>($"Value {i}"));
        }
    }

    /// <summary>
    /// Benchmarks appending entries to the WAL (each append is fsync'd).
    /// </summary>
    [Benchmark]
    [BenchmarkCategory("Append")]
    public async Task Append()
    {
        var walPath = Path.Combine(_tempDir, $"wal_append_{Guid.NewGuid():N}.log");
        using var wal = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(walPath);

        for (var i = 0; i < ItemCount; i++)
        {
            await wal.AppendAsync(_keys[i], _values[i]).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Benchmarks recovering entries from a pre-written WAL file.
    /// </summary>
    [Benchmark]
    [BenchmarkCategory("Recover")]
    public async Task Recover()
    {
        // Write a WAL file first.
        var walPath = Path.Combine(_tempDir, $"wal_recover_{Guid.NewGuid():N}.log");
        using (var walWrite = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(walPath))
        {
            for (var i = 0; i < ItemCount; i++)
            {
                await walWrite.AppendAsync(_keys[i], _values[i]).ConfigureAwait(false);
            }
        }

        // Benchmark recovery from the written file.
        using var walRead = new WriteAheadLog<SerializableWrapper<int>, SerializableWrapper<string>>(walPath);
        await walRead.RecoverAsync().ConfigureAwait(false);
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
