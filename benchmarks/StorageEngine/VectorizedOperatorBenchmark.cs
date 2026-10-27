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

using Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar;
using Boutquin.Storage.Infrastructure.Analytics;

namespace Boutquin.Storage.BenchMark;

/// <summary>Compares record-at-a-time filtering with typed column-batch operators.</summary>
/// <remarks>Batch processing avoids record rehydration but does not claim hardware SIMD acceleration.</remarks>
[MemoryDiagnoser]
public class VectorizedOperatorBenchmark
{
    private BenchmarkBar[] _rows = null!;
    private ColumnBatch<BenchmarkBar> _batch = null!;
    private FilterOperator<BenchmarkBar, long> _filter = null!;
    private AggregateOperator<BenchmarkBar, long, long> _aggregate = null!;

    [Params(4_096, 65_536)]
    public int RowCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var start = new DateOnly(2020, 1, 1);
        _rows = Enumerable.Range(0, RowCount)
            .Select(i => new BenchmarkBar(start.AddDays(i), 100d + i, i % 10_000))
            .ToArray();
        _batch = new ColumnBatch<BenchmarkBar>(RowCount,
            new Dictionary<string, Array>(StringComparer.Ordinal)
            {
                ["Date"] = _rows.Select(row => row.Date).ToArray(),
                ["Volume"] = _rows.Select(row => row.Volume).ToArray(),
            }, _rows[0].Date, _rows[^1].Date);
        _filter = new FilterOperator<BenchmarkBar, long>("Volume", static volume => volume >= 5_000);
        _aggregate = new AggregateOperator<BenchmarkBar, long, long>(
            "Volume", 0L, static (sum, volume) => sum + volume);
    }

    [Benchmark(Baseline = true)]
    public long RowObjectFilterAndSum()
    {
        var sum = 0L;
        foreach (var row in _rows)
        {
            if (row.Volume >= 5_000)
            {
                sum += row.Volume;
            }
        }
        return sum;
    }

    [Benchmark]
    public long ColumnBatchFilterAndSum() => _aggregate.Execute(_filter.Execute(_batch));
}
