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
using System.Runtime.CompilerServices;

using Boutquin.Storage.Domain.Enums;
using Boutquin.Storage.Domain.ValueObjects.Streaming;
using Boutquin.Storage.Infrastructure.Streaming;
using Boutquin.Storage.Infrastructure.Streaming.Watermarks;
using Boutquin.Storage.Infrastructure.Streaming.Windows;

namespace Boutquin.Storage.BenchMark;

/// <summary>Compares a processing-order loop with the event-time window operator.</summary>
[MemoryDiagnoser]
public class StreamingWindowBenchmark
{
    private StreamRecord<int>[] _records = null!;

    /// <summary>Gets or sets the number of records processed by one benchmark invocation.</summary>
    [Params(4096, 65536)]
    public int RecordCount { get; set; }

    /// <summary>Creates an ordered event-time input shared by both benchmark cases.</summary>
    [GlobalSetup]
    public void Setup()
    {
        var epoch = DateTimeOffset.UnixEpoch;
        _records = Enumerable.Range(0, RecordCount)
            .Select(index => new StreamRecord<int>(index, epoch.AddSeconds(index), (index % 17) + 1))
            .ToArray();
    }

    /// <summary>Measures a direct processing-order loop with fixed 60-record batches.</summary>
    /// <returns>A checksum that prevents dead-code elimination.</returns>
    [Benchmark(Baseline = true)]
    public long ProcessingOrderLoop()
    {
        long checksum = 0;
        var batchTotal = 0;
        for (var index = 0; index < _records.Length; index++)
        {
            batchTotal += _records[index].Value;
            if ((index + 1) % 60 == 0)
            {
                checksum += batchTotal;
                batchTotal = 0;
            }
        }

        return checksum + batchTotal;
    }

    /// <summary>Measures event-time tumbling windows, watermarking, and asynchronous enumeration.</summary>
    /// <returns>A task that completes with a checksum of all emitted window totals.</returns>
    [Benchmark]
    public async Task<long> EventTimeWindowOperator()
    {
        var processor = new WindowedAggregator<int, byte, long>(
            new TumblingWindow(TimeSpan.FromMinutes(1)),
            new BoundedOutOfOrdernessWatermarkPolicy(TimeSpan.Zero),
            LateEventPolicy.Drop,
            _ => 0,
            () => 0,
            (sum, value) => sum + value);
        long checksum = 0;
        await foreach (var result in processor.ProcessAsync(Records()).ConfigureAwait(false))
        {
            checksum += result.Value;
        }

        return checksum;
    }

    private async IAsyncEnumerable<StreamRecord<int>> Records(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var record in _records)
        {
            ct.ThrowIfCancellationRequested();
            yield return record;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }
}
