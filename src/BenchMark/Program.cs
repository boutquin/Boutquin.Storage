﻿// Copyright (c) 2024 Pierre G. Boutquin. All rights reserved.
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
namespace Boutquin.Storage.BenchMark;

/// <summary>
/// Entry point for the benchmarking program.
/// </summary>
public class Program
{
    /// <summary>
    /// Main method that sets up and runs the benchmarks.
    /// </summary>
    /// <param name="args">Command-line arguments.</param>
    public static void Main(string[] args)
    {
        // Dictionary to store benchmark results
        var results = new Dictionary<string, List<Summary>>();

        // List of benchmark types
        var benchmarks = new List<Type>
        {
            typeof(InMemoryKeyValueStoreBenchmark),
            typeof(AppendOnlyFileStorageEngineBenchmark),
            typeof(AppendOnlyFileStorageEngineWithIndexBenchmark)
            // Add other benchmark types here
        };

        // Custom logger for tracking progress
        var customLogger = new CustomLogger(benchmarks.Count);

        // Run benchmarks for each type
        foreach (var benchmark in benchmarks)
        {
            // Run the benchmark and capture the summary
            var summary = BenchmarkRunner.Run(benchmark, CreateCustomConfig(customLogger));
            if (summary != null)
            {
                if (!results.ContainsKey(benchmark.Name))
                {
                    results[benchmark.Name] = new List<Summary>();
                }
                results[benchmark.Name].Add(summary);
            }
        }

        // Display the results
        DisplayResults(results);
    }

    /// <summary>
    /// Creates a custom BenchmarkDotNet configuration.
    /// </summary>
    /// <param name="logger">The custom logger to use for progress tracking.</param>
    /// <returns>A configured IConfig instance.</returns>
    private static IConfig CreateCustomConfig(ILogger logger)
    {
        // Get the solution directory
        var solutionDirectory = Directory.GetParent(Directory.GetCurrentDirectory()).Parent.Parent.FullName;
        var artifactsPath = Path.Combine(solutionDirectory, "BenchmarkDotNet.Artifacts");

        return ManualConfig.Create(DefaultConfig.Instance)
            .WithOptions(ConfigOptions.DisableOptimizationsValidator)
            .AddLogger(logger)
            .AddColumnProvider(DefaultColumnProviders.Instance)
            .AddExporter(MarkdownExporter.Default)
            .AddJob(Job.Default.WithRuntime(CoreRuntime.Core80))
            .WithOrderer(new DefaultOrderer(SummaryOrderPolicy.FastestToSlowest))
            .WithArtifactsPath(artifactsPath);
    }

    /// <summary>
    /// Displays the benchmark results.
    /// </summary>
    /// <param name="results">The dictionary containing the benchmark results.</param>
    private static void DisplayResults(Dictionary<string, List<Summary>> results)
    {
        foreach (var implementation in results)
        {
            Console.WriteLine($"Results for {implementation.Key}:");

            var groupedReports = implementation.Value
                .SelectMany(summary => summary.Reports)
                .GroupBy(report => report.BenchmarkCase.Descriptor.WorkloadMethod.Name);

            foreach (var group in groupedReports)
            {
                Console.WriteLine($"  {group.Key}:");
                foreach (var report in group)
                {
                    var benchmarkCase = report.BenchmarkCase;
                    var parameters = benchmarkCase.Parameters;
                    var metrics = report.ResultStatistics;

                    Console.WriteLine($"    Parameters: {parameters.DisplayInfo}");
                    Console.WriteLine($"      Mean: {metrics.Mean} ms");
                    Console.WriteLine($"      Error: {metrics.StandardError} ms");
                    Console.WriteLine($"      StdDev: {metrics.StandardDeviation} ms");
                }
                Console.WriteLine();
            }
        }
    }
}
