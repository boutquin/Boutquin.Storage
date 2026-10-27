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
using System.Buffers.Binary;
using Boutquin.Storage.Domain.ValueObjects.TimeSeries;
using Boutquin.Storage.Domain.ValueObjects.TimeSeries.Columnar;
using Boutquin.Storage.Infrastructure.LsmTree;
using Boutquin.Storage.Infrastructure.TimeSeries;

namespace Boutquin.Storage.BenchMark;

/// <summary>
/// Row-oriented vs column-oriented LSM storage comparison on a synthetic daily-bar workload.
/// Parameterises over #tickers and #years so the row-vs-columnar crossover — the point at
/// which columnar's dataset-scoped projection begins to amortise its per-dataset-block
/// encoding overhead — can be inspected directly. The #ticker axis exercises the
/// <c>ColumnarSegmentFormat</c>'s per-dataset block index; the #years axis exercises the
/// inner <c>IColumnarSegmentFormat</c>'s row-group planning.
/// </summary>
/// <remarks>
/// This class is not sealed because BenchmarkDotNet requires benchmark classes to be inheritable.
/// </remarks>
[MemoryDiagnoser]
public class RowVsColumnarBenchmark
{
    private string _rowFolder = null!;
    private string _columnarFolder = null!;

    private LsmStorageEngine<TimeSeriesKey<SerializableWrapper<string>>, BenchmarkBar> _rowEngine = null!;
    private ColumnarTimeSeriesStack<SerializableWrapper<string>, BenchmarkBar>? _columnarStack;

    private LsmTimeSeriesStore<SerializableWrapper<string>, BenchmarkBar> _rowStore = null!;

    private List<SerializableWrapper<string>> _tickers = null!;
    private DateOnly _startDate;

    /// <summary>
    /// Number of distinct datasets (tickers). Drives the <c>ColumnarSegmentFormat</c>'s
    /// per-dataset block count.
    /// </summary>
    [Params(1, 10, 50)]
    public int TickerCount;

    /// <summary>
    /// Number of calendar years of daily-bar data per ticker. Drives row-group count within
    /// each dataset block (default row-group date range is 365 days).
    /// </summary>
    [Params(1, 5, 10)]
    public int Years;

    /// <summary>
    /// Sets up identical row- and columnar-backed stores with the same synthetic bar data so
    /// the benchmarks compare apples-to-apples. Runs once per parameter combination.
    /// </summary>
    [GlobalSetup]
    public async Task Setup()
    {
        _rowFolder = Path.Combine(Path.GetTempPath(), $"RowVsColBenchRow_{Guid.NewGuid():N}");
        _columnarFolder = Path.Combine(Path.GetTempPath(), $"RowVsColBenchCol_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_rowFolder);
        Directory.CreateDirectory(_columnarFolder);

        _startDate = new DateOnly(2015, 1, 1);
        _tickers = new List<SerializableWrapper<string>>(TickerCount);
        for (var i = 0; i < TickerCount; i++)
        {
            _tickers.Add(new SerializableWrapper<string>($"T{i:D4}"));
        }

        // Row engine: default SstSegmentFormat path.
        _rowEngine = LsmEngineFactory.Build<TimeSeriesKey<SerializableWrapper<string>>, BenchmarkBar>(
            memTableCapacity: 10_000,
            segmentFolder: _rowFolder,
            segmentPrefix: "row");

        var rowEngineTs = new LsmTimeSeriesStorageEngine<SerializableWrapper<string>, BenchmarkBar>(_rowEngine);
        _rowStore = new LsmTimeSeriesStore<SerializableWrapper<string>, BenchmarkBar>(
            rowEngineTs,
            new InMemoryDatasetCoverageIndex<SerializableWrapper<string>>());

        // Columnar engine: assembled by the builder (instance-sync enforced internally).
        _columnarStack = await ColumnarTimeSeriesEngineBuilder.BuildAsync<SerializableWrapper<string>, BenchmarkBar>(
            segmentFolder: _columnarFolder,
            segmentPrefix: "col",
            memTableCapacity: 10_000).ConfigureAwait(false);

        // Pre-populate both stores so Read / GetRange benchmarks have data to read.
        foreach (var ticker in _tickers)
        {
            var records = BuildBars(Years * 252).ToList();
            await _rowStore.AppendAsync(ticker, records).ConfigureAwait(false);
            await _columnarStack.Store.AppendAsync(ticker, records).ConfigureAwait(false);
        }

        await _rowEngine.FlushAsync().ConfigureAwait(false);
        await _columnarStack.InnerEngine.FlushAsync().ConfigureAwait(false);
    }

    private IEnumerable<BenchmarkBar> BuildBars(int count)
    {
        // Deterministic synthetic walk — identical for both engines so any benchmark delta is
        // attributable to format, not to input-data variability.
        var rng = new Random(42);
        var price = 100.0;
        for (var i = 0; i < count; i++)
        {
            price += (rng.NextDouble() - 0.5) * 2.0;
            yield return new BenchmarkBar(
                _startDate.AddDays(i),
                price,
                rng.Next(100_000, 10_000_000));
        }
    }

    /// <summary>
    /// Benchmarks a cold full-dataset scan across all tickers and years. Row format materialises
    /// every column of every record; columnar materialises only the ones required by the
    /// <see cref="LsmTimeSeriesStore{TDatasetKey, TRecord}.GetRangeAsync"/> contract (which, at
    /// the store level, is still every column — projection below the store is Phase 6+ work).
    /// </summary>
    [Benchmark(Baseline = true)]
    [BenchmarkCategory("FullScan_Row")]
    public async Task<int> FullScan_Row()
    {
        var count = 0;
        foreach (var ticker in _tickers)
        {
            await foreach (var _ in _rowStore.GetRangeAsync(ticker, _startDate, _startDate.AddDays(Years * 365), ColumnProjection.All).ConfigureAwait(false))
            {
                count++;
            }
        }

        return count;
    }

    [Benchmark]
    [BenchmarkCategory("FullScan_Columnar")]
    public async Task<int> FullScan_Columnar()
    {
        var count = 0;
        foreach (var ticker in _tickers)
        {
            await foreach (var _ in _columnarStack!.Store.GetRangeAsync(ticker, _startDate, _startDate.AddDays(Years * 365), ColumnProjection.All).ConfigureAwait(false))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Single-ticker point lookup through the full engine stack. Exposes the per-segment and
    /// per-dataset-block seek cost for each format.
    /// </summary>
    [Benchmark]
    [BenchmarkCategory("PointLookup_Row")]
    public async Task<int> PointLookup_Row()
    {
        var hits = 0;
        var target = _startDate.AddDays(Years * 252 / 2);
        foreach (var ticker in _tickers)
        {
            await foreach (var _ in _rowStore.GetRangeAsync(ticker, target, target, ColumnProjection.All).ConfigureAwait(false))
            {
                hits++;
            }
        }

        return hits;
    }

    [Benchmark]
    [BenchmarkCategory("PointLookup_Columnar")]
    public async Task<int> PointLookup_Columnar()
    {
        var hits = 0;
        var target = _startDate.AddDays(Years * 252 / 2);
        foreach (var ticker in _tickers)
        {
            await foreach (var _ in _columnarStack!.Store.GetRangeAsync(ticker, target, target, ColumnProjection.All).ConfigureAwait(false))
            {
                hits++;
            }
        }

        return hits;
    }

    /// <summary>
    /// Cleans up the temporary segment folders and disposes both engines.
    /// </summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _rowEngine.Dispose();
        _columnarStack?.Dispose();

        if (Directory.Exists(_rowFolder))
        {
            Directory.Delete(_rowFolder, recursive: true);
        }

        if (Directory.Exists(_columnarFolder))
        {
            Directory.Delete(_columnarFolder, recursive: true);
        }
    }

    /// <summary>
    /// Binary row-oriented serialiser for <see cref="BenchmarkBar"/>. Uses the fixed-width
    /// layout described on the record; no key prefix because that is supplied by the outer
    /// <c>TimeSeriesKey&lt;TDatasetKey&gt;</c>.
    /// </summary>
    public sealed class BinaryBenchmarkBarSerializer
        : IEntrySerializer<TimeSeriesKey<SerializableWrapper<string>>, BenchmarkBar>
    {
        public void WriteEntry(Stream stream, TimeSeriesKey<SerializableWrapper<string>> key, BenchmarkBar value)
        {
            // Key: length-prefixed dataset bytes + date
            var datasetBytes = System.Text.Encoding.UTF8.GetBytes(key.Dataset.Value ?? string.Empty);
            Span<byte> lenBuf = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(lenBuf, datasetBytes.Length);
            stream.Write(lenBuf);
            stream.Write(datasetBytes);
            Span<byte> dateBuf = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(dateBuf, key.Date.DayNumber);
            stream.Write(dateBuf);

            value.Serialize(stream);
        }

        public Task WriteEntryAsync(Stream stream, TimeSeriesKey<SerializableWrapper<string>> key, BenchmarkBar value, CancellationToken cancellationToken = default)
        {
            WriteEntry(stream, key, value);
            return Task.CompletedTask;
        }

        public (TimeSeriesKey<SerializableWrapper<string>> Key, BenchmarkBar Value)? ReadEntry(Stream stream)
        {
            Span<byte> lenBuf = stackalloc byte[4];
            if (stream.Read(lenBuf) != 4)
            {
                return null;
            }

            var len = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
            var datasetBuf = new byte[len];
            if (stream.Read(datasetBuf) != len)
            {
                return null;
            }

            var dataset = new SerializableWrapper<string>(System.Text.Encoding.UTF8.GetString(datasetBuf));

            Span<byte> dateBuf = stackalloc byte[4];
            if (stream.Read(dateBuf) != 4)
            {
                return null;
            }

            var date = DateOnly.FromDayNumber(BinaryPrimitives.ReadInt32LittleEndian(dateBuf));
            var bar = BenchmarkBar.Deserialize(stream);
            return (new TimeSeriesKey<SerializableWrapper<string>>(dataset, date), bar);
        }

        public bool CanRead(Stream stream) => stream.Position < stream.Length;
    }
}
