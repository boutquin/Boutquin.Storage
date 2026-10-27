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
using Boutquin.Storage.Distributed.Sync;

namespace Boutquin.Storage.Sync.Benchmarks;

/// <summary>Measures deterministic conflict materialization over replicated changes.</summary>
[MemoryDiagnoser]
public class SyncResolverBenchmark
{
    private readonly LastWriterWinsConflictResolver<string, string> _resolver = new();
    private SyncChange<KeyValueSyncChange<string, string>>[] _changes = null!;

    /// <summary>Gets or sets the number of change envelopes.</summary>
    [Params(100, 1_000)]
    public int ChangeCount { get; set; }

    /// <summary>Creates deterministic keys and replica origins outside measurement.</summary>
    [GlobalSetup]
    public void Setup() => _changes = Enumerable.Range(1, ChangeCount)
        .Select(i => new SyncChange<KeyValueSyncChange<string, string>>(
            $"device-{i % 4}:{i}", $"device-{i % 4}", i,
            new($"key-{i % 50}", $"value-{i}", i, false)))
        .ToArray();

    /// <summary>Materializes last-writer-wins state.</summary>
    [Benchmark]
    public IReadOnlyDictionary<string, string> Resolve() => _resolver.Resolve(_changes);
}
