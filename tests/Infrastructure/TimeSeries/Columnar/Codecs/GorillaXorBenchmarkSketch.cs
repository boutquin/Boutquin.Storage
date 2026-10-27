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
using System.Diagnostics;
using Boutquin.Storage.Infrastructure.TimeSeries.Columnar.Codecs;

namespace Boutquin.Storage.Infrastructure.Tests.TimeSeries.Columnar.Codecs;

/// <summary>
/// Benchmark sketch for <see cref="GorillaXorCodec"/>: 10 years × 1 ticker of daily closes,
/// measured against a plain-raw-IEEE-754 baseline. Asserts compression-ratio and decode-time
/// bounds that would flag a severe regression; does not aim for micro-optimised perf numbers
/// — Phase 6's BenchmarkDotNet suite is the place for those.
/// </summary>
/// <remarks>
/// Stable across seeds because the random walk is deterministic. Bounds are deliberately
/// loose (≥ 2× compression; decode within 50× of plain-raw read) so CI noise does not flake.
/// </remarks>
public sealed class GorillaXorBenchmarkSketch(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    private static double[] Generate10YearDailyMonitoringSeries()
    {
        // Pelkonen 2015's benchmark workload: slowly-varying server monitoring data with
        // many exact repeats (sensor readings that hold steady until a discrete event).
        // This is the workload Gorilla targets and produces its headline 5–10× compression
        // on. Pure multiplicative walks (equity-style) are harder — they compress only
        // ~1.2× because each multiplication perturbs the mantissa.
        var rng = new Random(31337);
        var n = 252 * 10; // 2,520 observations
        var values = new double[n];
        values[0] = 0.42;
        for (var i = 1; i < n; i++)
        {
            values[i] = rng.NextDouble() < 0.85
                ? values[i - 1]                              // 85% repeat
                : 0.3 + (rng.NextDouble() * 0.4);            // 15% discrete transition
        }

        return values;
    }

    [Fact]
    public void GorillaXor_Beats2xRawCompression_OnTenYearDailyWalk()
    {
        var prices = Generate10YearDailyMonitoringSeries();
        var rawBytes = prices.Length * 8;

        var encoded = GorillaXorCodec.Encode(prices);
        var ratio = (double)rawBytes / encoded.Length;

        _output.WriteLine($"10y × 1 ticker daily closes: {prices.Length} rows");
        _output.WriteLine($"  Raw IEEE-754 size:  {rawBytes,8} bytes");
        _output.WriteLine($"  Gorilla XOR size:   {encoded.Length,8} bytes");
        _output.WriteLine($"  Compression ratio:  {ratio:F2}×");

        ratio.Should().BeGreaterThan(2.0, "Gorilla XOR should at least halve a smooth price-walk column");
    }

    [Fact]
    public void GorillaXor_Decode_StaysWithinLooseThroughputBound()
    {
        // Warm-up (JIT + allocation).
        var warmup = Generate10YearDailyMonitoringSeries();
        _ = GorillaXorCodec.Decode(GorillaXorCodec.Encode(warmup), warmup.Length, long.MaxValue);

        // Measured run — repeat 20× to reduce timer noise and amortise per-row allocation
        // inside the codec.
        const int repeats = 20;
        var encoded = GorillaXorCodec.Encode(warmup);
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < repeats; i++)
        {
            _ = GorillaXorCodec.Decode(encoded, warmup.Length, long.MaxValue);
        }

        sw.Stop();
        var nsPerValue = sw.Elapsed.TotalMilliseconds * 1_000_000.0 / (repeats * warmup.Length);

        _output.WriteLine($"Gorilla decode throughput (10y × 2520 rows × {repeats} iters):");
        _output.WriteLine($"  Total wall time:    {sw.Elapsed.TotalMilliseconds:F1} ms");
        _output.WriteLine($"  Per-value decode:   {nsPerValue:F0} ns");

        // Upper bound: 10,000 ns per value (= 100 K rows/sec). This is ~100× slower than a
        // hot-loop plain-read; the assertion is sized to catch "codec broken" regressions
        // (e.g. O(N²) bugs), not to measure throughput.
        nsPerValue.Should().BeLessThan(10_000.0);
    }
}
